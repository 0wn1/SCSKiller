using System.Net.Http.Headers;
using System.Text;
using SCSKiller.Core.App;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SCSKiller.App;

/// <summary>Velopack in the app (docs/patreon-and-updates.md §4.5). Checks at start and every 6 h and downloads in the
/// background (nothing touches the install); applies only while no queue item runs anywhere (<see cref="Busy"/>): at
/// exit, or on "Restart to update". A build Velopack didn't install (dev, the zip) never checks.</summary>
public static class Updater
{
    static readonly TimeSpan Every = TimeSpan.FromHours(6);
    static readonly SemaphoreSlim One = new(1, 1);
    static readonly string DataDir = AppStore.DefaultDir;
    static readonly FeedTrust Trust = new(new AppStore(DataDir), FeedTrust.ReleaseKeys);
    static string ResumeFile => Path.Combine(DataDir, "resume-queue.txt");

    static UpdateManager? manager;   // the one that downloaded `ready`
    static VelopackAsset? ready;
    static Timer? timer;
    static int failures;   // consecutive checks that couldn't reach the feed

    /// <summary>Raised on any thread after <see cref="Ready"/>, <see cref="Checking"/> or <see cref="Problem"/> changed.</summary>
    public static event Action? Changed;
    public static string? Ready => ready?.Version.ToString();
    public static bool Checking { get; private set; }
    public static string? Problem { get; private set; }

    static UpdateManager Manager(string channel, bool downgrade) =>
        new(new SignedFeedSource(), new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = downgrade || UpdateChannels.AllowsDowngrade(channel) });

    public static bool Installed { get; } = Manager(UpdateChannels.Stable, false).IsInstalled;

    /// <summary>At app start (real data only): a package downloaded earlier is ready at once; then check now and every 6 h.</summary>
    public static void Start()
    {
        if (!Installed) return;
        var m = Manager(AppVersion.Current.Channel, false);
        if (m.UpdatePendingRestart is { } pending) (manager, ready) = (m, pending);
        timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(30), Every);   // not in the start's busy first seconds
    }

    /// <summary>Checks the effective channel's signed feed and downloads a newer version. <paramref name="backToStable"/>:
    /// "Go back to stable now", the stable feed with a downgrade allowed once.</summary>
    public static async Task CheckAsync(bool backToStable = false)
    {
        if (!Installed || !await One.WaitAsync(backToStable ? Timeout.InfiniteTimeSpan : TimeSpan.Zero)) return;   // the user's click waits for a running check
        (Checking, Problem) = (true, null);
        Changed?.Invoke();
        try
        {
            await App.Account.GetAccessTokenAsync();   // reads the entitlements
            var channel = backToStable ? UpdateChannels.Stable
                : UpdateChannels.Effective(App.Core.Settings.UpdateChannel, AppVersion.Current.Channel, App.Account.Status?.Ent);
            var m = Manager(channel, backToStable);
            var found = await m.CheckForUpdatesAsync();
            failures = 0;
            if (found is { } info)
            {
                await m.DownloadUpdatesAsync(info);
                (manager, ready) = (m, info.TargetFullRelease);
            }
        }
        catch (FeedRejectedException e) { Problem = "The update feed failed its signature check: " + e.Message; }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { Problem = null; failures = 0; }   // no feed published on this channel yet
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            // offline or a hiccup: the next check retries; say so only once it has kept failing for a day
            Problem = ++failures >= 4 ? "Couldn't reach the update server for a while. SCSKiller keeps trying." : null;
        }
        catch (Exception) { Problem = "Couldn't check for updates right now."; }
        finally
        {
            Checking = false;
            One.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>App exit (the tray's Quit): hands a downloaded update to Update.exe, which swaps it in once this process
    /// has exited. Not while a queue item runs (here or in the CLI): then at a later exit.</summary>
    public static void ApplyOnExit()
    {
        if (ready == null || manager == null || Busy.IsHeld()) return;
        Busy.MarkApplying(DataDir, DateTimeOffset.UtcNow);
        manager.WaitExitThenApplyUpdates(ready, silent: true, restart: false);
    }

    /// <summary>"Restart to update": stops the queue gracefully (in-flight compiles finish, the driver writes its cache),
    /// waits for it, remembers what was queued, and restarts into the new version, which resumes the queue where it
    /// stopped (each game's ResumeAt). False (and <see cref="Problem"/>) when the driver-update task's compile still runs.</summary>
    public static async Task<bool> RestartAsync()
    {
        if (ready == null || manager == null) return false;
        // plan checks aren't resumed: the next version's scan queues its own
        var queued = App.Core.Queue.Where(q => q.Stage is not (Core.QueueStage.Done or Core.QueueStage.Failed) && !q.PlanCheck).Select(q => q.GameId).ToList();
        App.Core.StopQueue();
        while (App.Core.Queue.Any(Format.Running)) await Task.Delay(250);
        for (var i = 0; i < 20 && Busy.IsHeld(); i++) await Task.Delay(100);   // our worker lets go just after the item ends
        if (Busy.IsHeld())
        {
            Problem = "The background rebuild after a driver update is compiling. The update installs when SCSKiller quits after it has finished.";
            Changed?.Invoke();
            return false;
        }
        if (queued.Count > 0) File.WriteAllLines(ResumeFile, queued);
        Busy.MarkApplying(DataDir, DateTimeOffset.UtcNow);
        App.DisposeTray();
        manager.ApplyUpdatesAndRestart(ready);   // exits this process
        return true;
    }

    public static bool HasResume => File.Exists(ResumeFile);

    /// <summary>After "Restart to update": the queue as it was, each game from where its warm stopped.</summary>
    public static void ResumeQueue()
    {
        if (!HasResume) return;
        var ids = File.ReadAllLines(ResumeFile);
        File.Delete(ResumeFile);
        foreach (var id in ids.Where(id => App.Core.Games.Any(g => g.Game.Id == id))) App.Core.Enqueue(id);
        App.Core.StartQueue();
    }

    /// <summary>Velopack's lifecycle hooks (Program.Main, before anything else): run by Update.exe, fast, then exit.</summary>
    public static void RunHooks() => VelopackApp.Build()
        .SetAutoApplyOnStartup(false)   // its apply force-stops every process under the install root: only ApplyOnExit/RestartAsync apply
        .OnAfterInstallFastCallback(_ =>
        {
            // a zip install's driver-update task points at the zip's folder: move it here (current\ keeps its name across updates)
            if (ScheduledTask.Registered && ScheduledTask.TaskExe() is { } exe) ScheduledTask.Register(exe);
        })
        .OnAfterUpdateFastCallback(_ => Busy.ClearApplying(DataDir))   // the swap is done: the CLI may run again
        .OnBeforeUninstallFastCallback(_ =>
        {
            // installer.md §5. Kept: the data dir (recordings, settings).
            ScheduledTask.Unregister();
            if (Environment.ProcessPath is { } app) WindowsStartup.Apply(false, app);
            ScsKiller.RemoveAllRecorders(new AppStore(DataDir));
        })
        .Run();

    /// <summary>The signed feed (§4.3): releases.&lt;channel&gt;.json is used only after <see cref="FeedTrust"/> accepts its
    /// .sig; each package is fetched from its own version's channel (<see cref="UpdateFeeds.Package"/>), and Velopack then
    /// checks its SHA-256 against the signed feed.</summary>
    sealed class SignedFeedSource : IUpdateSource
    {
        static readonly HttpClient Http = new(RouteFailover.Default, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            var name = $"releases.{channel}.json";
            var feed = await GetAsync(UpdateFeeds.Feed(channel, name), channel);
            var sig = await GetAsync(UpdateFeeds.Feed(channel, name + ".sig"), channel);
            Trust.Accept(channel, feed, sig);
            var parsed = VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(feed));
            // Velopack falls back to SHA-1 for an asset without SHA-256: a signed feed must pin every package by SHA-256
            if (parsed.Assets.Any(a => string.IsNullOrEmpty(a.SHA256))) throw new FeedRejectedException("a package without a SHA-256");
            return parsed;
        }

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset entry, string localFile, Action<int> progress, CancellationToken ct = default)
        {
            var version = AppVersion.Parse(entry.Version.ToString()) ?? throw new FeedRejectedException($"version '{entry.Version}'");
            using var request = await RequestAsync(UpdateFeeds.Package(version, entry.FileName), version.Channel);
            using var r = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            r.EnsureSuccessStatusCode();
            await using var file = File.Create(localFile);
            await r.Content.CopyToAsync(file, ct);
        }

        static async Task<byte[]> GetAsync(Uri url, string channel)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            using var request = await RequestAsync(url, channel);
            using var r = await Http.SendAsync(request, cts.Token);
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsByteArrayAsync(cts.Token);
        }

        // The edge's channels need the access token; GitHub (stable) gets none.
        static async Task<HttpRequestMessage> RequestAsync(Uri url, string channel)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (channel != UpdateChannels.Stable)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                    await App.Account.GetAccessTokenAsync() ?? throw new InvalidOperationException($"sign in for the {channel} channel"));
            return request;
        }
    }
}
