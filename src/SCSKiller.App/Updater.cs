using System.Net.Http.Headers;
using System.Text;
using SCSKiller.Core.App;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace SCSKiller.App;

/// <summary>Velopack in the app (docs/patreon-and-updates.md §4.5). Checks at start and every hour and downloads in the
/// background (nothing touches the install); applies only while no queue item runs anywhere (<see cref="Busy"/>): at
/// exit, at the next start, or on "Restart to update". A build Velopack didn't install (dev, the zip) never checks.</summary>
public static class Updater
{
    static readonly SemaphoreSlim One = new(1, 1);
    static readonly string DataDir = AppStore.DefaultDir;
    static readonly FeedTrust Trust = new(new AppStore(DataDir), FeedTrust.ReleaseKeys);
    static string ResumeFile => Path.Combine(DataDir, "resume-queue.txt");

    /// <summary>A finished download: the manager that downloaded it and the channel it came from.</summary>
    sealed record Download(Velo M, VelopackAsset R, string Channel);
    static volatile Download? ready;   // one reference: the UI reads it whole while a check replaces it
    static Timer? timer;
    static int failures;   // consecutive checks that couldn't reach the feed
    static long retryAt;   // a 429's Retry-After, on Environment.TickCount64: no check before (a clock change doesn't move it)
    static volatile bool asked;   // "Check for updates" was clicked: the running or next check shows its failure at once

    /// <summary>Raised on any thread after <see cref="Ready"/>, <see cref="Checking"/>, <see cref="Downloading"/>,
    /// <see cref="UpToDate"/> or <see cref="Problem"/> changed.</summary>
    public static event Action? Changed;
    public static string? Ready => Usable(ready) is { } d ? d.R.Version.ToString() : null;

    static string Chosen() => UpdateChannels.Effective(App.Core.Settings.UpdateChannel, AppVersion.Current.Channel, App.Account.Status?.Ent);

    /// <summary>A download is installed only while its channel is still the chosen one.</summary>
    static Download? Usable(Download? d) => d != null && d.Channel == Chosen() ? d : null;
    public static bool Checking { get; private set; }
    /// <summary>While <see cref="Checking"/>: the version whose package is being downloaded.</summary>
    public static string? Downloading { get; private set; }
    /// <summary>The last check reached the feed and found nothing newer.</summary>
    public static bool UpToDate { get; private set; }
    public static string? Problem { get; private set; }
    /// <summary>"Restart to update" waits for the compile: the channel stays as it is meanwhile.</summary>
    public static bool Restarting { get; private set; }

    static Velo Manager(string channel, bool downgrade) =>
        new(new SignedFeedSource(), new UpdateOptions
        {
            ExplicitChannel = channel, AllowVersionDowngrade = downgrade || UpdateChannels.AllowsDowngrade(channel),
            MaximumDeltasBeforeFallback = -1,   // full packages only: a package rebuilt from deltas fails the feed's checksum
        });

    /// <summary>Velopack's apply passes the package to Update.exe only while its file exists, else Update.exe takes the
    /// newest package on disk, whatever it is (<see cref="ApplyAsync"/>).</summary>
    sealed class Velo(IUpdateSource source, UpdateOptions options) : UpdateManager(source, options)
    {
        string PathOf(VelopackAsset a) => Path.Combine(Locator.PackagesDir ?? "", a.FileName);

        /// <summary>The asset's file, its size and Velopack's own checksum.</summary>
        public async Task<bool> OnDisk(VelopackAsset asset)
        {
            var file = new FileInfo(PathOf(asset));
            if (!file.Exists || file.Length != asset.Size) return false;
            try { await VerifyPackageChecksumAsync(asset, file.FullName); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Velopack.Exceptions.ChecksumFailedException) { return false; }
        }

        /// <summary>A download that failed <see cref="OnDisk"/>: gone, so the next check downloads it again.</summary>
        public void Delete(VelopackAsset asset)
        {
            try { File.Delete(PathOf(asset)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Every package but <paramref name="keep"/> and the installed version's: true once none other is left.</summary>
        public bool OnlyThis(VelopackAsset keep)
        {
            bool Other(VelopackAsset p) => p.FileName != keep.FileName && p.Version != CurrentVersion;
            foreach (var p in Locator.GetLocalPackages().Where(Other)) Delete(p);
            return !Locator.GetLocalPackages().Any(Other);
        }
    }

    /// <summary>The one way an update is applied (Quit, "Restart to update"), in order: the check's lock (a check may be
    /// replacing the download; <paramref name="wait"/> at most); the ready download read again; its file whole on disk,
    /// else deleted so the next check downloads it again; every other package deleted, so Update.exe has nothing else to
    /// fall back on; then, last, no compile running anywhere, the channel still the chosen one and <paramref name="still"/>;
    /// then <paramref name="apply"/>.</summary>
    static async Task<bool> ApplyAsync(TimeSpan wait, Action<Velo, VelopackAsset> apply, Func<bool>? still = null)
    {
        if (!await One.WaitAsync(wait)) return false;
        try
        {
            if (Usable(ready) is not var (m, r, _))
                return Fail("The update changed meanwhile. Restart to update again once it is ready.");
            if (!await m.OnDisk(r))
            {
                m.Delete(r);
                ready = null;
                return Fail("The downloaded update is no longer whole on disk. It downloads again at the next check.");
            }
            if (!m.OnlyThis(r)) return Fail("An older downloaded update couldn't be removed. The update installs at a later quit.");
            // the marker first, then Busy: a compile worker holds Busy first, then reads the marker (ScsKiller.Work), so
            // one of the two always sees the other
            Busy.MarkApplying(DataDir, DateTimeOffset.UtcNow);
            if (Busy.IsHeld()) return Undo("A compile started meanwhile. The update installs when SCSKiller quits after it has finished.");
            if (Usable(ready) is null) return Undo("The update channel changed meanwhile. Restart to update again once it is ready.");
            if (still?.Invoke() == false) return Undo(null);
            if (OfflineBlocks()) return Undo(OfflineRunning);
            apply(m, r);   // its preparation too (the resume file): a failure anywhere is undone below
            return true;
        }
        catch (Exception e) { return Undo("Couldn't hand the update to the installer: " + e.Message); }
        finally
        {
            One.Release();
            Changed?.Invoke();
        }

        static bool Fail(string? why)
        {
            Problem = why;
            return false;
        }

        // this process goes on: no marker stops the compiles, no resume file replays a queue that is still here
        static bool Undo(string? why)
        {
            try { Busy.ClearApplying(DataDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // it expires (Busy.ApplyingFor)
            try { File.Delete(ResumeFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return Fail(why);
        }
    }

    const string OfflineRunning = "An offline session's game or cleanup is running. The update installs once it has ended.";

    public static bool Installed { get; } = Manager(UpdateChannels.Stable, false).IsInstalled;

    /// <summary>At app start (real data only): check soon and every hour. A package downloaded before isn't ready by itself:
    /// Velopack keeps the newest one whatever its channel, so only the chosen channel's feed makes it ready, when it offers
    /// that version (its download then finds the package on disk and fetches nothing).</summary>
    public static void Start()
    {
        if (!Installed) return;
        timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(30), UpdateFeeds.CheckEvery);   // not in the start's busy first seconds
    }

    /// <summary>App start (a launch, not a toast: its activation wouldn't survive the restart): an update downloaded in an
    /// earlier run (the PC shut down with the app still open) installs now, once the chosen channel's feed confirms it
    /// (nothing is downloaded), and the app restarts into it with <paramref name="args"/>. Not after a handover whose new
    /// version never started: a failed apply must not restart the app in a loop. Once the user has queued a game or
    /// quits, the update waits for a quit as before.</summary>
    public static async Task ApplyAtStartAsync(string[] args)
    {
        if (!Installed || Busy.Marked(DataDir)) return;
        await CheckAsync(download: false);
        if (Usable(ready) is not null && Untouched() && BeginUpdate())
            if (!await ApplyAsync(TimeSpan.Zero, (m, r) => m.ApplyUpdatesAndRestart(r, args), Untouched)) EndUpdate();   // exits this process
        if (Usable(ready) is null) await CheckAsync();   // the timer's first check skips while this one runs

        // on the UI thread, like Quit and the queue's changes: nothing slips in between this and the handover
        static bool Untouched() => !App.Quitting && !App.Core.Queue.Any(q => q.Stage is not (Core.QueueStage.Done or Core.QueueStage.Failed) && !q.PlanCheck);
    }

    /// <summary>About's "Check for updates": checks and downloads now. A click while a check runs joins it, and that
    /// check's failure shows at once.</summary>
    public static Task CheckNowAsync()
    {
        asked = true;
        if (Installed && Environment.TickCount64 < Volatile.Read(ref retryAt))
        {
            (Problem, asked) = ("The update server asked SCSKiller to wait. It checks again by itself later.", false);
            Changed?.Invoke();
            return Task.CompletedTask;
        }
        return CheckAsync();
    }

    /// <summary>Checks the effective channel's signed feed and downloads a newer version. <paramref name="backToStable"/>:
    /// "Go back to stable now", the stable feed with a downgrade allowed once. Not <paramref name="download"/>: a newer
    /// version is made ready only when its package is already on disk.</summary>
    public static async Task CheckAsync(bool backToStable = false, bool download = true)
    {
        if (!Installed || Environment.TickCount64 < Volatile.Read(ref retryAt)) return;
        if (!await One.WaitAsync(backToStable ? Timeout.InfiniteTimeSpan : TimeSpan.Zero)) return;   // the user's click waits for a running check
        if (Environment.TickCount64 < Volatile.Read(ref retryAt))   // the check it waited for got a 429
        {
            One.Release();
            return;
        }
        (Checking, Problem, UpToDate) = (true, null, false);
        Changed?.Invoke();
        try
        {
            // reads the entitlements; stable's feed is on GitHub and needs no token, so a backend that's down mustn't stop it
            try { await App.Account.GetAccessTokenAsync(); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or AccountException) { }
            var channel = backToStable ? UpdateChannels.Stable : Chosen();
            if (ready?.Channel != channel) ready = null;   // another channel's download, even if this check fails
            var m = Manager(channel, backToStable);
            var found = await m.CheckForUpdatesAsync();
            failures = 0;
            // not the installed version again: the internal channel lists stable's packages, and Velopack offers the same
            // version while the channels differ
            if (found is not { } info || info.TargetFullRelease.Version == m.CurrentVersion) UpToDate = true;
            else if (await m.OnDisk(info.TargetFullRelease) is var have && (download || have))
            {
                if (!have)
                {
                    Downloading = info.TargetFullRelease.Version.ToString();
                    Changed?.Invoke();
                }
                await m.DownloadUpdatesAsync(info);   // a package already whole on disk is not fetched again
                if (channel == Chosen()) ready = new(m, info.TargetFullRelease, channel);   // the choice may have changed meanwhile
            }
        }
        catch (FeedRejectedException e) { Problem = "The update feed failed its signature check: " + e.Message; }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { Problem = null; failures = 0; }   // no feed published on this channel yet
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            // offline or a hiccup: the next check retries; say so only once it has kept failing for a day
            Problem = ++failures >= 24 ? "Couldn't reach the update server for a while. SCSKiller keeps trying."
                : asked ? "Couldn't reach the update server. SCSKiller tries again within the hour." : null;
        }
        catch (Exception) { Problem = "Couldn't check for updates right now."; }
        finally
        {
            (Checking, Downloading, asked) = (false, null, false);
            One.Release();
            Changed?.Invoke();
        }
    }

    // An apply stops every process under the install root, an offline session's cleanup helper too: from its start until it
    // is handed over, no offline session starts, and one that started before holds it back (ScsKiller.OfflineBlocksUpdate)
    static bool BeginUpdate() => App.Core is not Core.App.ScsKiller k || k.BeginUpdate();
    static void EndUpdate() => (App.Core as Core.App.ScsKiller)?.EndUpdate();
    static bool OfflineBlocks() => App.Core is Core.App.ScsKiller { OfflineBlocksUpdate: true };

    /// <summary>App exit (the tray's Quit): hands a downloaded update to Update.exe, which swaps it in once this process
    /// has exited. Not while a queue item runs (here or in the CLI), nor while a check still downloads after 30 s, nor
    /// while an offline session is pending: then at a later start or exit.</summary>
    public static async Task ApplyOnExitAsync()
    {
        if (Usable(ready) is null || Busy.IsHeld() || !BeginUpdate()) return;
        if (!await ApplyAsync(TimeSpan.FromSeconds(30), (m, r) => m.WaitExitThenApplyUpdates(r, silent: true, restart: false))) EndUpdate();
    }

    /// <summary>"Restart to update": stops the queue gracefully (in-flight compiles finish, the driver writes its cache),
    /// waits for it, remembers what was queued, and restarts into the new version, which resumes the queue where it
    /// stopped (each game's ResumeAt). False (and <see cref="Problem"/>) when the driver-update task's compile still runs.</summary>
    public static async Task<bool> RestartAsync()
    {
        if (Usable(ready) is null) return false;
        if (!BeginUpdate())
        {
            Problem = OfflineRunning;
            Changed?.Invoke();
            return false;
        }
        (Restarting, Problem) = (true, null);
        Changed?.Invoke();
        var applied = false;
        try
        {
            // plan checks aren't resumed: the next version's scan queues its own
            var queued = App.Core.Queue.Where(q => q.Stage is not (Core.QueueStage.Done or Core.QueueStage.Failed) && !q.PlanCheck).Select(q => q.GameId).ToList();
            App.Core.StopQueue();
            while (App.Core.Compiling) await Task.Delay(250);
            for (var i = 0; i < 20 && Busy.IsHeld(); i++) await Task.Delay(100);   // our worker lets go just after the item ends
            if (Busy.IsHeld())
            {
                Problem = "The background rebuild after a driver update is compiling. The update installs when SCSKiller quits after it has finished.";
                return false;
            }
            return applied = await ApplyAsync(Timeout.InfiniteTimeSpan, (m, r) =>
            {
                if (queued.Count > 0) File.WriteAllLines(ResumeFile, queued);
                m.ApplyUpdatesAndRestart(r);   // exits this process
            });
        }
        finally
        {
            if (!applied) EndUpdate();
            Restarting = false;
            Changed?.Invoke();
        }
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
        .SetAutoApplyOnStartup(false)   // its apply force-stops every process under the install root: only ApplyOnExitAsync/RestartAsync apply
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

        /// <summary>The channel's feed, plus the full packages of the channels after it (§4.1): their own feeds, read here
        /// too, so a release published after this channel's last feed (a stable fix, a beta promoted) still reaches it. A
        /// later feed that can't be fetched is left out; one that fails its signature fails the check.</summary>
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            VelopackAssetFeed parsed;
            try { parsed = await Signed(channel); }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound && channel != UpdateChannels.Stable) { parsed = new() { Assets = [] }; }   // none yet
            foreach (var later in UpdateChannels.All.TakeWhile(c => c != channel))
            {
                VelopackAssetFeed more;
                try { more = await Signed(later); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or AccountException) { continue; }
                var have = parsed.Assets.Select(a => a.FileName).ToHashSet();
                parsed.Assets = [.. parsed.Assets, .. more.Assets.Where(a => a.Type == VelopackAssetType.Full && !have.Contains(a.FileName))];
            }
            return parsed;
        }

        static async Task<VelopackAssetFeed> Signed(string channel)
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
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headers.CancelAfter(TimeSpan.FromMinutes(1));
            using var r = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            headers.CancelAfter(Timeout.InfiniteTimeSpan);   // the body has its own stall limit
            Limited(r);
            r.EnsureSuccessStatusCode();
            await using var file = File.Create(localFile);
            await using var body = await r.Content.ReadAsStreamAsync(ct);
            await UpdateFeeds.Download(body, file, entry.Size, TimeSpan.FromMinutes(2), ct);
        }

        static async Task<byte[]> GetAsync(Uri url, string channel)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            using var request = await RequestAsync(url, channel);
            using var r = await Http.SendAsync(request, cts.Token);
            Limited(r);
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsByteArrayAsync(cts.Token);
        }

        static void Limited(HttpResponseMessage r)
        {
            if (r.StatusCode != System.Net.HttpStatusCode.TooManyRequests) return;
            var wait = r.Headers.RetryAfter is { Delta: { } d } ? d : r.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromHours(1);
            // the edge's longest window is a day: a larger or negative value is not believed
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait;
            Volatile.Write(ref retryAt, Environment.TickCount64 + (long)wait.TotalMilliseconds);
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
