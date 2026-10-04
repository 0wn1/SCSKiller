using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// The recorder records only when armed, so every path that leaves it installed in a clean game must leave it armed: here
// from a PC that has no ledger folder yet, as a fresh install has.
public partial class AppTests
{
    /// <summary>The ledger in a folder of this test that doesn't exist yet; the real one after.</summary>
    sealed class FreshLedger : IDisposable
    {
        readonly string _was = ScsKiller.LedgerDir;
        public FreshLedger(string root) => ScsKiller.LedgerDir = Path.Combine(root, "LocalAppData", "SCSKiller", "armed");
        public void Dispose() => ScsKiller.LedgerDir = _was;
    }

    /// <summary>Armed as the proxy checks it: armed=1 beside the exe, and its nonce in the exe's ledger entry.</summary>
    static bool ArmedWithLedger(Game g)
    {
        var armed = Path.Combine(Path.GetDirectoryName(g.ExePath)!, ScsKiller.ArmedFile);
        var ledger = ScsKiller.LedgerFile(g.ExePath);
        if (!File.Exists(armed) || !File.Exists(ledger)) return false;
        static string Nonce(string f) => File.ReadAllLines(f).FirstOrDefault(l => l.StartsWith("nonce=", StringComparison.Ordinal)) ?? "";
        return File.ReadAllText(armed).Contains("armed=1") && Nonce(armed).Length > "nonce=".Length && Nonce(armed) == Nonce(ledger);
    }

    /// <summary>A recording SCSKiller holds for the game: the install writes the keys file (through its temp file).</summary>
    static void Recorded(ScsKiller k, Game g)
    {
        var db = Path.Combine(k.Store.GameDir(g.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        var shader = "shader"u8.ToArray();
        using var f = File.Create(db);
        PsoDb.Write(f, 'B', [.. SHA1.HashData(shader), .. shader]);
    }

    ScsKiller Managed()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        return k;
    }

    /// <summary>Armed when the call returns, and still once the install watcher's events of our own writes arrived.</summary>
    async Task AssertStaysArmed(ScsKiller k, Game g)
    {
        Assert.True(ArmedWithLedger(g));
        await Task.Delay(500);
        await Until(() => !k.DisarmQueued(g));
        Assert.True(ArmedWithLedger(g));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_all_games_arms_a_fresh_install_before_the_scan_returns(bool recorded)
    {
        using var _ = new FreshLedger(_root);
        var k = Managed();
        if (recorded) Recorded(k, _game);
        await k.ScanAsync(default);   // "record all games" installs it
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Equal(recorded, File.Exists(Path.Combine(_exeDir, Recordings.KeysFile)));
        await AssertStaysArmed(k, _game);
    }

    [Fact]
    public async Task A_recorder_updated_at_startup_is_armed()
    {
        using var _ = new FreshLedger(_root);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.WriteAllBytes(dll, [.. "MZ older proxy SCSKiller_StartWarm "u8]);   // an earlier build's, no armed file
        var k = Managed();
        Recorded(k, _game);
        await k.ScanAsync(default);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(dll));
        await AssertStaysArmed(k, _game);
    }

    [Fact]
    public async Task Turning_the_recorder_on_arms_it()
    {
        using var _ = new FreshLedger(_root);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        k.Settings = k.Settings with { RecordAllGames = false };
        k.ManageRecorders = true;
        Recorded(k, _game);
        await k.ScanAsync(default);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        k.SetRecorderOverride(_game.Id, RecorderOverride.On);
        await AssertStaysArmed(k, _game);
    }

    [Fact]
    public async Task Every_watcher_pass_arms_an_installed_recorder_that_isnt()
    {
        using var _ = new FreshLedger(_root);
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);   // the new watcher's first check
        File.Delete(Path.Combine(_exeDir, ScsKiller.ArmedFile));   // a deletion is no install change: no event
        await k.CheckRecorderGames(false);
        Assert.True(ArmedWithLedger(_game));
    }

    [Fact]
    public async Task An_arming_that_fails_is_logged_once_and_retried_at_every_pass()
    {
        using var _ = new FreshLedger(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(ScsKiller.LedgerDir)!);
        File.WriteAllText(ScsKiller.LedgerDir, "");   // a file where the ledger folder goes: it can't be made
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);
        Assert.False(ArmedWithLedger(_game));
        var log = Path.Combine(k.Store.DataDir, "recorders.log");
        Assert.Single(File.ReadAllLines(log), l => l.Contains("couldn't arm the recorder"));

        File.Delete(ScsKiller.LedgerDir);
        await k.CheckRecorderGames(false);
        Assert.True(ArmedWithLedger(_game));
    }

    [Fact]
    public async Task A_recorder_that_cant_be_armed_isnt_checked_at_every_pass()
    {
        using var _ = new FreshLedger(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(ScsKiller.LedgerDir)!);
        File.WriteAllText(ScsKiller.LedgerDir, "");   // arming fails until it goes
        var k = Managed();
        var walks = 0;
        k.FullAntiCheatCheck = g => { Interlocked.Increment(ref walks); return Core.Games.GameFiles.DetectAntiCheat(g); };
        await k.ScanAsync(default);
        await k.CheckRecorderGames(false);   // the new watcher's first check
        walks = 0;
        for (var i = 0; i < 5; i++) await k.CheckRecorderGames(false);
        Assert.Equal(1, walks);   // once, then not before the retry interval

        k.UnarmedRetryInterval = TimeSpan.Zero;
        for (var i = 0; i < 5; i++) await k.CheckRecorderGames(false);
        Assert.Equal(3, walks);   // a few retries, then only at the full passes
        await k.CheckRecorderGames(true);
        Assert.Equal(4, walks);
    }

    [Fact]
    public async Task A_game_with_anti_cheat_is_never_armed()
    {
        using var _ = new FreshLedger(_root);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        var k = Managed();
        await k.ScanAsync(default);
        await k.CheckRecorderGames(true);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ArmedFile)));
        Assert.False(File.Exists(ScsKiller.LedgerFile(_game.ExePath)));
    }
}
