using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NSec.Cryptography;

namespace SCSKiller.Core.App;

// The update system's pure parts (docs/patreon-and-updates.md §4); Velopack itself lives in the App project.

/// <summary>A SemVer 2 version, X.Y.Z[-pre][+build] (a leading "v" is accepted: tags). Compares by SemVer precedence:
/// 1.5.0-alpha.3 &lt; 1.5.0-beta.1 &lt; 1.5.0-internal.1 &lt; 1.5.0 (internal sorts after beta by ASCII, which is why the
/// internal channel allows downgrades, release-process.md §4.1). Build metadata is ignored.</summary>
public sealed partial record AppVersion(int Major, int Minor, int Patch, string Pre) : IComparable<AppVersion>
{
    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Form();

    public static AppVersion? Parse(string? s) => s != null && Form().Match(s.Trim()) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, out var a) && int.TryParse(m.Groups[2].Value, out var b) && int.TryParse(m.Groups[3].Value, out var c)
        ? new(a, b, c, m.Groups[4].Value) : null;

    /// <summary>This build's version (AssemblyInformationalVersion, set from the tag by build/publish.ps1).</summary>
    public static AppVersion Current { get; } = Parse((Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new(0, 0, 0, "internal.0");

    /// <summary>The channel this version belongs to: its pre-release label; stable without one. An unknown label (and a dev
    /// build's 0.0.0-internal.0) is internal: it never goes to anyone else.</summary>
    public string Channel => Pre.Length == 0 ? UpdateChannels.Stable : Pre.Split('.')[0] is var l && UpdateChannels.All.Contains(l) ? l : UpdateChannels.Internal;

    public override string ToString() => Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";

    public int CompareTo(AppVersion? o)
    {
        if (o is null) return 1;
        var c = (Major, Minor, Patch).CompareTo((o.Major, o.Minor, o.Patch));
        if (c != 0) return c;
        if (Pre.Length == 0 || o.Pre.Length == 0) return (Pre.Length == 0).CompareTo(o.Pre.Length == 0);   // a release sorts after its pre-releases
        string[] x = Pre.Split('.'), y = o.Pre.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xn = ulong.TryParse(x[i], out var xv), yn = ulong.TryParse(y[i], out var yv);
            c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return Math.Sign(c);
        }
        return x.Length.CompareTo(y.Length);
    }
}

/// <summary>Which update feed to follow (§4.1, §4.5 item 5).</summary>
public static class UpdateChannels
{
    public const string Stable = "stable", Beta = "beta", Alpha = "alpha", Internal = "internal";
    /// <summary>From the public end to the earliest builds; each channel's entitlement flag has the same name.</summary>
    public static readonly string[] All = [Stable, Beta, Alpha, Internal];

    /// <summary>The channels the account may pick: stable, plus each one its token's <c>ent</c> flags grant. Settings shows
    /// the picker only when there is more than stable.</summary>
    public static IReadOnlyList<string> Offered(IReadOnlyCollection<string>? ent) => All.Where(c => c == Stable || ent?.Contains(c) == true).ToList();

    /// <summary>The feed to check: the chosen channel (null: the running build's own) capped by the entitlement, stepping
    /// back towards stable (a lapsed alpha supporter with beta gets beta). Nothing is downgraded by this: Velopack only
    /// takes a newer version, so the installed beta stays until stable passes it.</summary>
    public static string Effective(string? chosen, string buildChannel, IReadOnlyCollection<string>? ent)
    {
        var at = Array.IndexOf(All, chosen ?? buildChannel);
        while (at > 0 && ent?.Contains(All[at]) != true) at--;
        return All[Math.Max(at, 0)];
    }

    /// <summary>The internal channel installs whatever its feed names, even an older version (§4.1).</summary>
    public static bool AllowsDowngrade(string channel) => channel == Internal;
}

/// <summary>Where updates and their source come from. <see cref="GhRepo"/> is compiled in: renaming the public repo
/// strands every installed build's stable updates.</summary>
public static class UpdateFeeds
{
    public const string GhRepo = "BlueHeisenberg/SCSKiller";
    public static readonly Uri Packages = new("https://dl.scskiller.io/");   // alpha/beta/internal packages: VPS route only (hosting.md §3)
    /// <summary>The app's background check; the Library's refresh and About's "Check for updates" check in between.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>Stable: the feed on GitHub Releases (§4.4). Other channels: the edge, behind the access token, through
    /// <see cref="RouteFailover"/> (a URL on its primary route).</summary>
    public static Uri Feed(string channel, string file) => channel == UpdateChannels.Stable
        ? new($"https://github.com/{GhRepo}/releases/latest/download/{file}")
        : new(RouteFailover.Default.Primary, $"v1/updates/{channel}/{file}");

    /// <summary>A package named by a signed feed: the version's own channel says where it lives (a beta feed also lists
    /// stable releases, §4.1). Its SHA-256 comes from the signed feed, so the host doesn't have to be trusted.</summary>
    public static Uri Package(AppVersion v, string fileName) => v.Channel == UpdateChannels.Stable
        ? new($"https://github.com/{GhRepo}/releases/download/v{v}/{Uri.EscapeDataString(fileName)}")
        : new(PackageHost, $"v1/updates/{v.Channel}/{Uri.EscapeDataString(fileName)}");

    /// <summary>A package body into <paramref name="to"/>: at most the signed feed's <paramref name="size"/> (its SHA-256 is
    /// only checked once the download ends), and refused when no byte arrives for <paramref name="stall"/>.</summary>
    public static async Task Download(Stream from, Stream to, long size, TimeSpan stall, CancellationToken ct)
    {
        var buf = new byte[81920];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (long total = 0; ;)
        {
            idle.CancelAfter(stall);
            int n;
            try { n = await from.ReadAsync(buf, idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"no package bytes for {stall.TotalSeconds:0} s"); }
            if (n == 0) return;
            if ((total += n) > size) throw new FeedRejectedException($"a package over the feed's {size} bytes");
            await to.WriteAsync(buf.AsMemory(0, n), ct);
        }
    }

    // SCSKILLER_API (a local backend) serves the packages too
    static Uri PackageHost => Environment.GetEnvironmentVariable("SCSKILLER_API") is { Length: > 0 } ? RouteFailover.Default.Primary : Packages;

    /// <summary>The About page's "Download source code" (§4.5 item 7): the public repo's tag for stable; for alpha and beta
    /// the source zip next to the package on the edge (with the access token); none for internal and dev builds.</summary>
    public static Uri? Source(AppVersion v) => v.Channel switch
    {
        UpdateChannels.Stable => new($"https://github.com/{GhRepo}/tree/v{v}"),
        UpdateChannels.Alpha or UpdateChannels.Beta => new(RouteFailover.Default.Primary, $"v1/updates/{v.Channel}/source-{v}.zip"),
        _ => null,
    };
}

public sealed class FeedRejectedException(string message) : Exception(message);

/// <summary>The signed feed (§4.3): releases.&lt;channel&gt;.json.sig next to Velopack's feed,
/// {"v":1,"channel","signed_at","feed_sha256","kid","sig"}, sig = Ed25519 over
/// "scskiller-feed-v1\n" + channel + "\n" + signed_at + "\n" + feed_sha256. A feed is used only once its signature
/// verifies with a pinned key, its hash matches, and it isn't older than the newest one accepted for that channel.</summary>
public sealed class FeedTrust(AppStore store, IReadOnlyDictionary<string, string> keys)
{
    /// <summary>The release public keys (kid -> base64 raw Ed25519 key), generated offline by the maintainer
    /// (tools/release-sign keygen): rel-a signs, rel-b is the backup.</summary>
    public static readonly IReadOnlyDictionary<string, string> ReleaseKeys = new Dictionary<string, string>
    {
        ["rel-a"] = "sitj5K2fcZku26c/EvUo793SeSuVIb0YcN+6/mi0CwM=",
        ["rel-b"] = "OBpxPkqkWZJIx6p89z8R4cEp52aMRcmeZQVHZQEZ0ns=",
    };

    static readonly SignatureAlgorithm Ed = SignatureAlgorithm.Ed25519;

    public static byte[] Message(string channel, string signedAt, string feedSha256) =>
        Encoding.UTF8.GetBytes($"scskiller-feed-v1\n{channel}\n{signedAt}\n{feedSha256}");

    /// <summary>The .sig file for <paramref name="feed"/> (tools/release-sign; tests). <paramref name="seed"/>: the 32-byte private key.</summary>
    public static string Sign(ReadOnlySpan<byte> seed, string kid, string channel, byte[] feed, DateTimeOffset signedAt)
    {
        using var key = Key.Import(Ed, seed, KeyBlobFormat.RawPrivateKey);
        var at = signedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var sha = Convert.ToHexStringLower(SHA256.HashData(feed));
        return JsonSerializer.Serialize(new { v = 1, channel, signed_at = at, feed_sha256 = sha, kid, sig = Convert.ToBase64String(Ed.Sign(key, Message(channel, at, sha))) });
    }

    /// <summary>A new key pair: (base64 seed, base64 public key).</summary>
    public static (string Seed, string Public) NewKey()
    {
        using var key = Key.Create(Ed, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    /// <summary>Checks <paramref name="feed"/> against its <paramref name="sig"/> file for <paramref name="channel"/>, and
    /// remembers its signed_at (per channel: switching back to stable isn't a replay). Throws <see cref="FeedRejectedException"/>.</summary>
    public void Accept(string channel, byte[] feed, byte[] sig)
    {
        string signedAt, sha, kid;
        byte[] signature;
        try
        {
            var j = JsonDocument.Parse(sig).RootElement;
            if (j.GetProperty("v").GetInt32() != 1) throw new FeedRejectedException("unknown signature version");
            if (j.GetProperty("channel").GetString() != channel) throw new FeedRejectedException("the signature is for another channel");
            (signedAt, sha, kid) = (j.GetProperty("signed_at").GetString()!, j.GetProperty("feed_sha256").GetString()!, j.GetProperty("kid").GetString()!);
            signature = Convert.FromBase64String(j.GetProperty("sig").GetString()!);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException)
        {
            throw new FeedRejectedException("malformed signature file");
        }
        if (!keys.TryGetValue(kid, out var pub)) throw new FeedRejectedException($"unknown key '{kid}'");
        if (!PublicKey.TryImport(Ed, Convert.FromBase64String(pub), KeyBlobFormat.RawPublicKey, out var key) || !Ed.Verify(key!, Message(channel, signedAt, sha), signature))
            throw new FeedRejectedException("bad signature");
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(feed)), sha, StringComparison.OrdinalIgnoreCase))
            throw new FeedRejectedException("the feed doesn't match its signature");
        if (!DateTimeOffset.TryParse(signedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            throw new FeedRejectedException("malformed signed_at");
        lock (store)
        {
            var newest = store.LoadFeedTimes();
            if (newest.TryGetValue(channel, out var seen) && at < seen) throw new FeedRejectedException($"an older feed than one already seen ({seen:u})");
            newest[channel] = at;
            store.SaveFeedTimes(newest);
        }
    }
}

/// <summary>Never apply an update under a running warm (§4.5 item 3). Every process running a queue item holds
/// <see cref="Name"/>; the app applies only while nobody does. The CLI never applies, and exits at once while the app
/// hands over to Update.exe (<see cref="MarkApplying"/>): a marker file, since a mutex dies with the app before the swap.</summary>
public static class Busy
{
    public const string Name = @"Local\SCSKiller.Busy";
    public static readonly TimeSpan ApplyingFor = TimeSpan.FromMinutes(2);   // an older marker is a crashed apply

    /// <summary>Held until disposed. The named object exists while any process has it open, so ownership (and its thread
    /// affinity, awkward across awaits) isn't needed.</summary>
    public static IDisposable Hold(string name = Name) => new Mutex(false, name);

    /// <summary>A compile worker's hold, taken before it reads the marker (the updater writes the marker before it reads
    /// <see cref="IsHeld"/>), so an update and a compile never both go ahead; null, holding nothing, while an update is
    /// being handed over.</summary>
    public static IDisposable? TryHold(string dataDir, DateTimeOffset now, string name = Name)
    {
        var hold = Hold(name);
        if (!Applying(dataDir, now)) return hold;
        hold.Dispose();
        return null;
    }

    public static bool IsHeld(string name = Name)
    {
        if (!Mutex.TryOpenExisting(name, out var m)) return false;
        m.Dispose();
        return true;
    }

    static string Marker(string dataDir) => Path.Combine(dataDir, "applying");

    public static void MarkApplying(string dataDir, DateTimeOffset now)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Marker(dataDir), now.ToString("O", CultureInfo.InvariantCulture));
    }

    public static void ClearApplying(string dataDir) => File.Delete(Marker(dataDir));

    /// <summary>A marker of any age: a handover whose new version never started.</summary>
    public static bool Marked(string dataDir) => File.Exists(Marker(dataDir));

    public static bool Applying(string dataDir, DateTimeOffset now)
    {
        try
        {
            // a reader polls it: the updater's ClearApplying must be able to delete it meanwhile
            using var f = new FileStream(Marker(dataDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return DateTimeOffset.TryParse(new StreamReader(f).ReadToEnd(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                && now - at < ApplyingFor && at - now < ApplyingFor;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
