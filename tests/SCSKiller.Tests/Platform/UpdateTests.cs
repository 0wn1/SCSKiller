using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// The update system's pure parts (Core/App/Updates.cs). The keys here are generated per run: a test key never exists
// outside this file, and production pins only FeedTrust.ReleaseKeys.
public class UpdateTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-update-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly (string Seed, string Public) A = FeedTrust.NewKey(), B = FeedTrust.NewKey();
    static readonly byte[] Feed = """{"Assets":[{"PackageId":"SCSKiller.App","Version":"1.5.0-beta.1","Type":"Full","FileName":"SCSKiller.App-1.5.0-beta.1-full.nupkg","SHA256":"ab"}]}"""u8.ToArray();
    static readonly DateTimeOffset T = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    FeedTrust Trust() => new(new AppStore(_dir), new Dictionary<string, string> { ["rel-a"] = A.Public, ["rel-b"] = B.Public });
    static byte[] Sig(string seed, string kid, string channel, byte[] feed, DateTimeOffset at) =>
        Encoding.UTF8.GetBytes(FeedTrust.Sign(Convert.FromBase64String(seed), kid, channel, feed, at));

    [Fact]
    public void GoodSignature_WithEitherPinnedKey_IsAccepted()
    {
        var t = Trust();
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T));
        t.Accept("beta", Feed, Sig(B.Seed, "rel-b", "beta", Feed, T.AddHours(1)));   // the backup key
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddHours(1)));   // the same feed fetched again
    }

    [Fact]
    public void BadSignatures_AreRefused()
    {
        var t = Trust();
        var other = FeedTrust.NewKey();
        Refused(() => t.Accept("beta", Feed, Sig(other.Seed, "rel-a", "beta", Feed, T)), "bad signature");        // not the pinned key
        Refused(() => t.Accept("beta", Feed, Sig(other.Seed, "rel-z", "beta", Feed, T)), "unknown key");          // an unpinned kid
        Refused(() => t.Accept("stable", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T)), "another channel");        // a beta feed served as stable
        Refused(() => t.Accept("beta", Feed, "not json"u8.ToArray()), "malformed");
        Refused(() => new FeedTrust(new AppStore(_dir), FeedTrust.ReleaseKeys).Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T)), "bad signature");   // a test key under a production kid
    }

    [Fact]
    public void TamperedFeedOrSignatureFile_IsRefused()
    {
        var t = Trust();
        var sig = Sig(A.Seed, "rel-a", "beta", Feed, T);
        var feed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Feed).Replace("\"ab\"", "\"cd\""));   // another package hash
        Refused(() => t.Accept("beta", feed, sig), "doesn't match");
        foreach (var (field, value) in new[] { ("signed_at", "2027-01-01T00:00:00Z"), ("feed_sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(feed))) })
        {
            var j = JsonNode.Parse(sig)!;
            j[field] = value;   // signed fields changed after signing
            Refused(() => t.Accept("beta", field == "feed_sha256" ? feed : Feed, Encoding.UTF8.GetBytes(j.ToJsonString())), "bad signature");
        }
    }

    [Fact]
    public void OlderFeed_IsARefusedReplay_PerChannel()
    {
        var t = Trust();
        t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T));
        Refused(() => t.Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddMinutes(-1))), "older");
        t.Accept("stable", Feed, Sig(A.Seed, "rel-a", "stable", Feed, T.AddDays(-30)));   // switching back to stable isn't a replay
        Refused(() => Trust().Accept("beta", Feed, Sig(A.Seed, "rel-a", "beta", Feed, T.AddMinutes(-1))), "older");   // remembered on disk
    }

    static void Refused(Action a, string why) => Assert.Contains(why, Assert.Throws<FeedRejectedException>(a).Message);

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "", "stable")]
    [InlineData("v1.5.0-beta.2", 1, 5, 0, "beta.2", "beta")]
    [InlineData("1.5.0-alpha.1", 1, 5, 0, "alpha.1", "alpha")]
    [InlineData("1.5.0-internal.3+abc123", 1, 5, 0, "internal.3", "internal")]
    [InlineData("0.0.0-internal.0+4f2a9c1d", 0, 0, 0, "internal.0", "internal")]   // a dev build
    [InlineData("2.0.0-rc.1", 2, 0, 0, "rc.1", "internal")]                          // an unknown label goes nowhere public
    public void Versions_Parse(string s, int major, int minor, int patch, string pre, string channel)
    {
        var v = AppVersion.Parse(s)!;
        Assert.Equal((major, minor, patch, pre, channel), (v.Major, v.Minor, v.Patch, v.Pre, v.Channel));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("")]
    [InlineData(null)]
    public void NotVersions(string? s) => Assert.Null(AppVersion.Parse(s));

    [Fact]
    public void Versions_OrderBySemVerPrecedence()
    {
        string[] ordered = ["1.4.9", "1.5.0-alpha.2", "1.5.0-alpha.10", "1.5.0-beta", "1.5.0-beta.1", "1.5.0-beta.2", "1.5.0-internal.1", "1.5.0", "1.5.1-alpha.1", "1.10.0"];
        var parsed = ordered.Select(s => AppVersion.Parse(s)!).ToList();
        Assert.Equal(ordered, parsed.OrderBy(v => v).Select(v => v.ToString()));
        for (var i = 1; i < parsed.Count; i++) Assert.True(parsed[i - 1].CompareTo(parsed[i]) < 0, $"{parsed[i - 1]} < {parsed[i]}");
        Assert.Equal(0, AppVersion.Parse("1.5.0+a")!.CompareTo(AppVersion.Parse("1.5.0+b")));   // build metadata doesn't order
        // internal sorts after beta: only the internal channel, which installs what its feed names, may go back
        Assert.True(AppVersion.Parse("1.5.0-internal.1")!.CompareTo(AppVersion.Parse("1.5.0-beta.3")) > 0);
        Assert.True(UpdateChannels.AllowsDowngrade("internal"));
        Assert.All(new[] { "stable", "beta", "alpha" }, c => Assert.False(UpdateChannels.AllowsDowngrade(c)));
    }

    [Fact]
    public void Channels_AreGatedByTheTokensFlags()
    {
        Assert.Equal(["stable"], UpdateChannels.Offered(null));                          // signed out: no picker
        Assert.Equal(["stable"], UpdateChannels.Offered(["db"]));
        Assert.Equal(["stable", "beta"], UpdateChannels.Offered(["db", "beta"]));
        Assert.Equal(["stable", "beta", "alpha"], UpdateChannels.Offered(["db", "beta", "alpha", "prio"]));
        Assert.Equal(["stable", "beta", "alpha", "internal"], UpdateChannels.Offered(["beta", "alpha", "internal"]));

        Assert.Equal("beta", UpdateChannels.Effective("beta", "stable", ["beta"]));
        Assert.Equal("alpha", UpdateChannels.Effective(null, "alpha", ["beta", "alpha"]));    // null: the build's own channel
        Assert.Equal("beta", UpdateChannels.Effective("alpha", "alpha", ["beta"]));           // alpha lapsed: beta, not stable
        Assert.Equal("stable", UpdateChannels.Effective("beta", "beta", ["db"]));             // beta lapsed
        Assert.Equal("stable", UpdateChannels.Effective("beta", "beta", null));               // signed out
        Assert.Equal("stable", UpdateChannels.Effective("stable", "beta", ["beta"]));         // chose stable: waits for stable to pass the beta
        Assert.Equal("stable", UpdateChannels.Effective(null, "stable", ["beta", "alpha"]));
        Assert.Equal("stable", UpdateChannels.Effective("nonsense", "beta", ["beta"]));
    }

    [Fact]
    public async Task BusyMutex_GuardsTheApply()
    {
        var name = @"Local\SCSKiller.Busy.test-" + Guid.NewGuid().ToString("N");   // not the real one: queue tests may hold it
        Assert.False(Busy.IsHeld(name));
        var h1 = Busy.Hold(name);
        var h2 = await Task.Run(() => Busy.Hold(name));   // another thread (or process) holding it too
        Assert.True(Busy.IsHeld(name));
        h1.Dispose();
        Assert.True(Busy.IsHeld(name));
        await Task.Run(h2.Dispose);   // released from a thread that didn't create it: no ownership needed
        Assert.False(Busy.IsHeld(name));
    }

    [Fact]
    public void ApplyingMarker_StopsTheCliForTwoMinutes()
    {
        Assert.False(Busy.Applying(_dir, T));
        Busy.MarkApplying(_dir, T);
        Assert.True(Busy.Applying(_dir, T.AddSeconds(90)));
        Assert.False(Busy.Applying(_dir, T.AddMinutes(3)));    // a crashed apply
        Assert.False(Busy.Applying(_dir, T.AddMinutes(-3)));   // the clock went back: not forever
        Busy.ClearApplying(_dir);
        Assert.False(Busy.Applying(_dir, T));
        Busy.ClearApplying(_dir);   // already gone
    }

    [Fact]
    public void Codecs_OnlyPinnedBytesAreKept()
    {
        string dir = Path.Combine(_dir, "codecs"), seed = Path.Combine(_dir, "seed");
        Directory.CreateDirectory(seed);
        File.WriteAllText(Path.Combine(seed, "zlib-ng2.dll"), "not it");   // a wrong copy next to the exe isn't used
        var downloads = 0;
        var e = Assert.Throws<InvalidDataException>(() => Codecs.Ensure("zlib-ng2.dll", p => { downloads++; File.WriteAllText(p, "tampered"); return true; }, dir, seed));
        Assert.Contains("SHA-256", e.Message);
        Assert.Equal(1, downloads);
        Assert.False(File.Exists(Path.Combine(dir, "zlib-ng2.dll")));   // never left behind to load
        Assert.Throws<InvalidDataException>(() => Codecs.Ensure("other.dll", _ => true, dir, seed));   // no pin, no load
    }

    [Fact]
    public void SourceCode_AndPackageUrls()
    {
        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/tree/v1.4.2", UpdateFeeds.Source(AppVersion.Parse("1.4.2")!)!.AbsoluteUri);
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/beta/source-1.5.0-beta.2.zip"), UpdateFeeds.Source(AppVersion.Parse("1.5.0-beta.2+abc")!));
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/alpha/source-1.5.0-alpha.1.zip"), UpdateFeeds.Source(AppVersion.Parse("1.5.0-alpha.1")!));
        Assert.Null(UpdateFeeds.Source(AppVersion.Parse("1.5.0-internal.1")!));
        Assert.Null(UpdateFeeds.Source(AppVersion.Parse("0.0.0-internal.0+abc")!));

        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/releases/latest/download/releases.stable.json", UpdateFeeds.Feed("stable", "releases.stable.json").AbsoluteUri);
        Assert.Equal(new Uri(RouteFailover.Default.Primary, "v1/updates/beta/releases.beta.json.sig"), UpdateFeeds.Feed("beta", "releases.beta.json.sig"));
        // a beta feed lists stable releases too: each package is fetched from its own version's channel
        Assert.Equal($"https://github.com/{UpdateFeeds.GhRepo}/releases/download/v1.4.2/SCSKiller.App-1.4.2-full.nupkg",
            UpdateFeeds.Package(AppVersion.Parse("1.4.2")!, "SCSKiller.App-1.4.2-full.nupkg").AbsoluteUri);
        if (Environment.GetEnvironmentVariable("SCSKILLER_API") is not { Length: > 0 })
            Assert.Equal("https://dl.scskiller.io/v1/updates/beta/SCSKiller.App-1.5.0-beta.2-delta.nupkg",
                UpdateFeeds.Package(AppVersion.Parse("1.5.0-beta.2")!, "SCSKiller.App-1.5.0-beta.2-delta.nupkg").AbsoluteUri);
    }
}
