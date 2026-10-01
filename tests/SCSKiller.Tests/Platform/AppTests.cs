using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Tests.Platform;

// Everything here runs on a fake game under %TEMP%; no real game folder is touched.
[Collection(TimingCollection.Name)]
public class AppTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "scskiller-app-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Game _game;
    readonly string _exeDir, _proxy;
    static readonly GpuInfo Gpu = new(GpuVendor.Unknown, "Fake GPU", "100.01", 1, 0);
    static readonly EngineInfo Unreal = new("Unreal", "4.26", null, "D3D12", false, null);

    public AppTests()
    {
        var install = Path.Combine(_root, "FakeGame");
        _exeDir = Path.Combine(install, "Fake", "Binaries", "Win64");
        Directory.CreateDirectory(_exeDir);
        File.WriteAllBytes(Path.Combine(_exeDir, "Fake-Win64-Shipping.exe"), new byte[4096]);
        Directory.CreateDirectory(Path.Combine(install, "Engine", "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(install, "Engine", "Binaries", "Win64", "CrashReportClient.exe"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(install, "Fake.exe"), new byte[100]);   // launcher stub
        _game = new Game("test:fake", "Fake Game", Store.Other, install, Core.Games.GameFiles.FindExe(install)!);
        _proxy = Path.Combine(_root, "tools", "d3d12.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(_proxy)!);
        File.WriteAllBytes(_proxy, [.. "MZ fake proxy SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);
    }

    public void Dispose() => Directory.Delete(_root, true);

    ScsKiller Killer(IEngineReader? reader = null, IPlanner? planner = null, IWarmer? warmer = null, string driver = "100.01", Game? game = null,
        Game[]? games = null, IGpuVendorBackend? vendor = null) =>
        new([new FakeSource(games ?? [game ?? _game])], vendor ?? new FakeVendor(Gpu with { DriverVersion = driver }), reader ?? new FakeReader(null),
            planner ?? new FakePlanner(), warmer ?? new FakeWarmer(), Path.Combine(_root, "data"), _proxy)
        { LocalAppData = _root, MyGames = Path.Combine(_root, "My Games"), ProgramData = Path.Combine(_root, "ProgramData") };   // never the real D3DSCache or Saved folders

    async Task<ScsKiller> Warmed(Game? game = null)
    {
        var k = Killer(new FakeReader(Unreal), game: game);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        return k;
    }

    static async Task Until(Func<bool> cond, double seconds = 10)
    {
        for (var t = Stopwatch.StartNew(); !cond(); await Task.Delay(20))
            if (t.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException();
    }

    [Fact]
    public void Exe_heuristic_skips_engine_helpers_and_launcher_stubs() =>
        Assert.Equal(Path.Combine(_exeDir, "Fake-Win64-Shipping.exe"), _game.ExePath);

    [Fact]
    public void Elevated_helper_quotes_finds_the_cli_and_reads_its_result()
    {
        Assert.Equal(@"cache set 100 --result ""C:\Users\A B\t.json""", Elevated.CommandLine(["cache", "set", "100", "--result", @"C:\Users\A B\t.json"]));

        var published = Path.Combine(_root, "dist");   // dist\SCSKiller.exe + dist\cli\scskiller.exe
        Directory.CreateDirectory(Path.Combine(published, "cli"));
        File.WriteAllBytes(Path.Combine(published, "cli", "scskiller.exe"), [0]);
        Assert.Equal(Path.Combine(published, "cli", "scskiller.exe"), Elevated.CliExe(published));

        var file = Path.Combine(_root, "result.json");
        Assert.Equal(new Elevated.Result(false, "the command-line tool failed (exit code 3)"), Elevated.ReadResult(file, 3));   // no file
        Assert.True(Elevated.ReadResult(file, 0).Ok);
        Elevated.WriteResult(file, false, "needs admin");
        Assert.Equal(new Elevated.Result(false, "needs admin"), Elevated.ReadResult(file, 1));
    }

    [Fact]
    public void Elevated_cli_writes_its_outcome_and_refuses_another_account()
    {
        // The dev tree's CLI build, as the app finds it. Both commands fail before touching anything: not elevated here,
        // or (if the tests run elevated) the --for-user SID is not this account's.
        if (Elevated.CliExe() is not { } cli) return;
        foreach (var args in new[] { new[] { "nvidia-auto-shader", "bogus" }, ["nvidia-auto-shader", "medium", Elevated.ForUserArg, "S-1-5-21-1-2-3-500"] })
        {
            var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
            var psi = new ProcessStartInfo(cli, [.. args, Elevated.YesArg, Elevated.ResultArg, file]) { RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            var r = Elevated.ReadResult(file, p.ExitCode);
            Assert.False(r.Ok);
            Assert.True(File.Exists(file), "the CLI wrote no --result file");
            Assert.NotEmpty(r.Message);
        }
    }

    [Fact]
    public async Task Recorder_install_then_uninstall_removes_only_our_files_and_imports_the_recording()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Assert.False(k.Games.Single().RecorderInstalled);

        k.InstallRecorder(_game.Id);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.Contains("mode=record", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.True(k.Games.Single().RecorderInstalled);

        // the game ran with the recorder: it wrote its db, log and timings
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db"))) PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        var recorded = PsoDb.Read(Path.Combine(_exeDir, "scskiller.db")).ToList();
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "10.0,G,0,0,50.0\n11.0,s,0,0,0.2\n12.0,C,1,1,0.5\n");

        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.True(File.Exists(Path.Combine(_exeDir, "scskiller.db")));   // the proxy's output is not ours to delete, only emptied once imported
        Assert.Equal(0, new FileInfo(Path.Combine(_exeDir, "scskiller.db")).Length);
        Assert.Equal(recorded.Select(r => r.Key), PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Select(r => r.Key));
        var s = k.Games.Single();
        Assert.False(s.RecorderInstalled);
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(12), 3, 1, 1, 1, 50.0), s.LastSession);
    }

    [Fact]
    public async Task Uninstall_removes_an_untracked_recorder_of_ours()
    {
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));   // installed by hand, before SCSKiller tracked it
        var k = Killer();
        await k.ScanAsync(default);
        Assert.True(k.Games.Single().RecorderInstalled);
        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(k.Games.Single().RecorderInstalled);
    }

    [Fact]
    public async Task Uninstall_leaves_files_changed_since_install()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        File.AppendAllText(Path.Combine(_exeDir, "scskiller.ini"), "threads=4\r\n");   // the user edited it
        k.UninstallRecorder(_game.Id);
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.True(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    [Fact]
    public async Task Install_refuses_a_foreign_d3d12_dll()
    {
        var foreign = Path.Combine(_exeDir, "d3d12.dll");
        File.WriteAllText(foreign, "some other wrapper");
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.Equal("some other wrapper", File.ReadAllText(foreign));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    [Fact]
    public async Task Install_refuses_anti_cheat_games()
    {
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    Game FakeGame(string id, string name, string under = "")
    {
        var dir = Path.Combine(_root, under, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".exe"), new byte[64]);
        return new Game(id, name, Store.Other, dir, Path.Combine(dir, name + ".exe"));
    }

    static string Dll(Game g) => Path.Combine(g.InstallDir, "d3d12.dll");

    ScsKiller RecordKiller(Game[] games, Dictionary<string, string>? apis = null, Func<IReadOnlySet<string>>? running = null)
    {
        var k = Killer(new ApiReader(apis ?? []), games: games);
        k.ProcessNames = running ?? (() => new HashSet<string>());
        return k;
    }

    [Fact]
    public void Recorder_effective_is_the_override_else_the_global_setting_and_never_when_incompatible()
    {
        Assert.True(ScsKiller.RecorderEffective(RecorderOverride.Default, true, null));
        Assert.False(ScsKiller.RecorderEffective(RecorderOverride.Default, false, null));
        Assert.True(ScsKiller.RecorderEffective(RecorderOverride.On, false, null));
        Assert.False(ScsKiller.RecorderEffective(RecorderOverride.Off, true, null));
        foreach (var o in Enum.GetValues<RecorderOverride>())
            Assert.False(ScsKiller.RecorderEffective(o, true, ScsKiller.SkipAntiCheat));
    }

    [Fact]
    public async Task Recorder_compatibility_skips_anti_cheat_non_DirectX_12_foreign_dlls_and_protected_folders()
    {
        var ok = FakeGame("test:ok", "Ok");
        var eac = FakeGame("test:eac", "Eac");
        Directory.CreateDirectory(Path.Combine(eac.InstallDir, "EasyAntiCheat"));
        var other = FakeGame("battlenet:other", "Other");   // Battle.net: AntiCheat.Other
        var dx11 = FakeGame("test:dx11", "Dx11");
        var either = FakeGame("test:either", "Either");
        var foreign = FakeGame("test:foreign", "Foreign");
        File.WriteAllText(Dll(foreign), "ReShade");
        var xbox = FakeGame("xbox:store", "Xbox", "WindowsApps");
        var k = RecordKiller([ok, eac, other, dx11, either, foreign, xbox], new() { ["test:dx11"] = "D3D11", ["test:either"] = "D3D11 or D3D12" });
        await k.ScanAsync(default);

        string? Skip(Game g) => k.Games.Single(s => s.Game.Id == g.Id).RecorderSkip;
        Assert.Null(Skip(ok));
        Assert.Null(Skip(either));
        Assert.Equal(ScsKiller.SkipAntiCheat, Skip(eac));
        Assert.Equal(ScsKiller.SkipAntiCheat, Skip(other));
        Assert.Equal(ScsKiller.SkipNotDx12, Skip(dx11));
        Assert.Equal(ScsKiller.SkipForeignDll, Skip(foreign));
        Assert.Equal(ScsKiller.SkipNeedsAdmin, Skip(xbox));

        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Dll(ok)));
        Assert.True(ScsKiller.IsOurProxy(Dll(either)));
        foreach (var g in new[] { eac, other, dx11, xbox }) Assert.False(File.Exists(Dll(g)));
        Assert.Equal("ReShade", File.ReadAllText(Dll(foreign)));
        Assert.Equal([either.Id, ok.Id], k.Games.Where(s => s.RecorderInstalled).Select(s => s.Game.Id).Order());
    }

    [Fact]
    public async Task Recorder_skips_a_folder_it_cannot_write_to()
    {
        var locked = FakeGame("test:locked", "Locked");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(sid,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.AccessControlType.Deny);
        var info = new DirectoryInfo(locked.InstallDir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        try
        {
            var k = RecordKiller([locked]);
            await k.ScanAsync(default);
            k.ReconcileRecorders();
            var s = k.Games.Single();
            Assert.False(File.Exists(Dll(locked)));
            Assert.Equal(ScsKiller.SkipNeedsAdmin, s.RecorderSkip);
            Assert.False(s.RecorderEffective);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task Reconcile_installs_and_removes_exactly_the_effective_set()
    {
        var byDefault = FakeGame("test:default", "Default");
        var off = FakeGame("test:off", "Off");
        var on = FakeGame("test:on", "On");
        var eac = FakeGame("test:eac", "Eac");
        Directory.CreateDirectory(Path.Combine(eac.InstallDir, "EasyAntiCheat"));
        var k = RecordKiller([byDefault, off, on, eac]);
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        Assert.True(k.Settings.RecordAllGames);
        Assert.Equal([byDefault.Id, off.Id, on.Id], k.Games.Where(s => s.RecorderInstalled).Select(s => s.Game.Id).Order());
        var recorded = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        using (var f = File.Create(Path.Combine(byDefault.InstallDir, "scskiller.db"))) PsoDb.Write(f, recorded.Tag, recorded.Payload);   // the game ran with it

        k.SetRecorderOverride(off.Id, RecorderOverride.Off);
        k.SetRecorderOverride(on.Id, RecorderOverride.On);
        Assert.False(File.Exists(Dll(off)));
        Assert.False(File.Exists(Path.Combine(off.InstallDir, "scskiller.ini")));

        k.Settings = k.Settings with { RecordAllGames = false };   // removed everywhere but the On override
        await Until(() => !k.Games.Single(s => s.Game.Id == byDefault.Id).RecorderInstalled);
        Assert.True(ScsKiller.IsOurProxy(Dll(on)));
        Assert.False(File.Exists(Path.Combine(byDefault.InstallDir, "scskiller.ini")));
        Assert.True(File.Exists(Path.Combine(byDefault.InstallDir, "scskiller.db")));   // the recorder's data stays, emptied once imported
        Assert.Equal([recorded.Key], PsoDb.Read(Path.Combine(k.Store.GameDir(byDefault.Id), "recording.db")).Select(r => r.Key));
        Assert.Equal([on.Id], k.Games.Where(s => s.RecorderInstalled).Select(s => s.Game.Id));

        k.SetRecorderOverride(off.Id, RecorderOverride.Default);   // "Use default" while the global is off: stays out
        k.SetRecorderOverride(on.Id, RecorderOverride.Default);
        Assert.DoesNotContain(k.Games, s => s.RecorderInstalled);
        Assert.All(k.Games, s => Assert.Equal(RecorderOverride.Default, s.RecorderOverride));
        Assert.False(File.Exists(Dll(eac)));

        k.Settings = k.Settings with { RecordAllGames = true };
        await Until(() => k.Games.Count(s => s.RecorderInstalled) == 3);
        Assert.Contains("recorder installed", File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));
    }

    [Fact]
    public async Task Reconcile_never_touches_a_foreign_d3d12_dll()
    {
        var g = FakeGame("test:mod", "Mod");
        File.WriteAllText(Dll(g), "OptiScaler");
        var k = RecordKiller([g]);
        await k.ScanAsync(default);
        k.SetRecorderOverride(g.Id, RecorderOverride.On);
        Assert.Equal("OptiScaler", File.ReadAllText(Dll(g)));
        Assert.Equal(ScsKiller.SkipForeignDll, k.Games.Single().RecorderSkip);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(g.Id));
        k.SetRecorderOverride(g.Id, RecorderOverride.Off);
        k.Settings = k.Settings with { RecordAllGames = false };
        k.ReconcileRecorders();
        Assert.Equal("OptiScaler", File.ReadAllText(Dll(g)));
        Assert.False(File.Exists(Path.Combine(g.InstallDir, "scskiller.ini")));
    }

    static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task Record_alongside_a_mod_chains_to_it_and_removal_puts_it_back_byte_for_byte()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());   // a wrapper: not ours, no marker
        File.WriteAllBytes(mod, bytes);
        var sha = Sha256Of(mod);
        var chained = Path.Combine(_exeDir, ScsKiller.ChainName);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        Assert.Equal(ScsKiller.SkipForeignDll, k.Games.Single().RecorderSkip);   // opt-in: nothing touched until asked
        Assert.Equal("d3d12.dll", k.RecorderMod(_game.Id));

        k.SetRecordAlongsideMod(_game.Id, true);
        k.InstallRecorder(_game.Id);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(mod));
        Assert.Equal(bytes, File.ReadAllBytes(chained));
        Assert.Contains($"next={ScsKiller.ChainName}", File.ReadAllText(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.Equal(new ChainedDll(ScsKiller.ChainName, sha), k.Store.LoadGame(_game.Id).RecorderChained);
        var s = k.Games.Single();
        Assert.True(s.RecorderInstalled);
        Assert.Null(s.RecorderSkip);
        Assert.Equal("d3d12.dll", k.RecorderMod(_game.Id));   // still named: the chained one

        k.UninstallRecorder(_game.Id);
        Assert.Equal(sha, Sha256Of(mod));
        Assert.False(File.Exists(chained));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);

        // on again, then the choice turned off: the recorder goes and the mod is back
        k.InstallRecorder(_game.Id);
        Assert.True(File.Exists(chained));
        k.SetRecordAlongsideMod(_game.Id, false);
        Assert.Equal(sha, Sha256Of(mod));
        Assert.False(File.Exists(chained));
        Assert.Equal(ScsKiller.SkipForeignDll, k.Games.Single().RecorderSkip);
    }

    string RecordersLog() => File.ReadAllText(Path.Combine(_root, "data", "recorders.log"));

    [Fact]
    public async Task Uninstall_hook_removes_our_recorder_and_its_data_files_after_keeping_the_recording()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        foreach (var f in new[] { "scskiller.log", "scskiller_creates.csv", Recordings.KeysFile }) File.WriteAllText(Path.Combine(_exeDir, f), f);
        using (var db = File.Create(Path.Combine(_exeDir, "scskiller.db"))) PsoDb.Write(db, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
        var recorded = PsoDb.Read(Path.Combine(_exeDir, "scskiller.db")).Select(r => r.Key).ToList();
        File.WriteAllText(Path.Combine(_exeDir, "game.ini"), "the game's own");

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.Equal(["Fake-Win64-Shipping.exe", "game.ini"], Directory.GetFiles(_exeDir).Select(Path.GetFileName).Order());
        Assert.Equal(recorded, PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Select(r => r.Key));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Empty(rec.RecorderFiles);
        Assert.Null(rec.RecorderExe);
        Assert.Contains($"recorder removed from {_exeDir}", RecordersLog());
    }

    [Fact]
    public async Task Uninstall_hook_keeps_a_foreign_dll_even_with_our_hash_on_record()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());   // a wrapper the user put over ours
        File.WriteAllBytes(dll, bytes);
        var rec = k.Store.LoadGame(_game.Id);
        rec.RecorderFiles["d3d12.dll"] = Sha256Of(dll);   // a state that went wrong: the marker still decides
        k.Store.SaveGame(_game.Id, rec);

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.Equal(bytes, File.ReadAllBytes(dll));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    [Fact]
    public async Task Uninstall_hook_leaves_a_running_game_and_merges_a_recording_not_imported_yet()
    {
        var running = FakeGame("test:running", "Running");
        var k = RecordKiller([_game, running]);
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        k.InstallRecorder(running.Id);
        static PsoDb.Rec Pso(char c) => new('C', PsoDb.Compute(PsoDb.Zero, new string(c, 40)));
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        using (var f = File.Create(store)) PsoDb.Write(f, 'C', Pso('a').Payload);   // imported before compact recordings
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db"))) { PsoDb.Write(f, 'C', Pso('b').Payload); PsoDb.Write(f, 'C', Pso('a').Payload); }
        var runningDb = Path.Combine(running.InstallDir, "scskiller.db");
        File.WriteAllText(runningDb, "its recorder is writing it");

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running" });
        Assert.True(ScsKiller.IsOurProxy(Dll(running)));
        Assert.True(File.Exists(Path.Combine(running.InstallDir, "scskiller.ini")));
        Assert.Equal(running.ExePath, k.Store.LoadGame(running.Id).RecorderExe);
        Assert.Equal("its recorder is writing it", File.ReadAllText(runningDb));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.db")));
        Assert.Equal([Pso('a').Key, Pso('b').Key], PsoDb.Read(store).Select(r => r.Key));
        Assert.True(PsoDb.IsCompact(store));
        Assert.Contains($"{running.ExePath} is running", RecordersLog());
    }

    [Fact]
    public async Task Uninstall_hook_puts_a_chained_mod_back()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(_game.Id, true);
        k.InstallRecorder(_game.Id);
        Assert.True(ScsKiller.IsOurProxy(mod));

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);
    }

    [Fact]
    public async Task Reconcile_records_where_a_recorder_is_for_uninstall()
    {
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));   // installed by hand or by a build that didn't record its exe
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.Equal(_game.ExePath, k.Store.LoadGame(_game.Id).RecorderExe);

        ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>The proxy's side of the chain, on WARP (no GPU cache): `selftest chain` creates a device and a compute PSO
    /// through the built proxy with next= naming fakenext.dll (a mod's d3d12.dll that exports only D3D12CreateDevice). The
    /// device comes through the mod, the other exports from the system dll, and the PSO is recorded; a next= that doesn't
    /// load, or isn't a bare file name, leaves the game running on the system dll. With a mod that replaces shaders
    /// (fakenext's wrapper device swapping a graphics PSO's PS), both the game's desc and the one the mod passed on to the
    /// device underneath are recorded, the replaced PS with its bytes (a shader in no file of the install).</summary>
    [Fact]
    public void The_proxy_chains_to_the_renamed_mod_and_records_through_it()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SCSKiller.slnx"))) root = root.Parent;
        var bin = root == null ? null : Path.Combine(root.FullName, "proxy", "build", "Release");
        if (bin == null || !File.Exists(Path.Combine(bin, "fakenext.dll"))) return;   // this checkout's proxy isn't built
        var dir = Path.Combine(_root, "chain");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.Copy(Path.Combine(bin, "fakenext.dll"), Path.Combine(dir, ScsKiller.ChainName));
        (int Exit, string Out) Run(string next, int seed, string mode = "")
        {
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\nnext={next}\r\n");
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {seed} {mode}") { RedirectStandardOutput = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, o);
        }
        var seed = Environment.TickCount % 100000;
        var chained = Run(ScsKiller.ChainName, seed);
        Assert.Equal(0, chained.Exit);
        Assert.Contains("next-calls 1", chained.Out);
        Assert.Contains("created 0x00000000", chained.Out);
        Assert.Single(PsoDb.Read(Path.Combine(dir, "scskiller.db")), r => r.Tag == 'C');

        foreach (var (next, why) in new[] { ("missing.dll", "failed to load"), (@"..\" + ScsKiller.ChainName, "not a file name") })
        {
            var r = Run(next, ++seed);
            Assert.Equal(0, r.Exit);
            Assert.Contains("next-calls -1", r.Out);
            Assert.Contains(why, File.ReadAllText(Path.Combine(dir, "scskiller.log")));
        }
        Assert.Equal(3, PsoDb.Read(Path.Combine(dir, "scskiller.db")).Count(r => r.Tag == 'C'));

        var swapped = Run(ScsKiller.ChainName, ++seed, "swap");
        Assert.Equal(0, swapped.Exit);
        Assert.Contains("created 0x00000000", swapped.Out);
        var db = PsoDb.Read(Path.Combine(dir, "scskiller.db")).ToList();
        var gfx = db.Where(r => r.Tag == 'G').ToList();
        Assert.Equal(2, gfx.Count);
        var ps = gfx.Select(g => PsoDb.Parse(g).Stages[(int)Stage.Pixel]).ToList();
        Assert.NotEqual(ps[0], ps[1]);
        Assert.Equal(gfx[0].Payload[..40], gfx[1].Payload[..40]);   // root signature and VS
        Assert.Equal(gfx[0].Payload[60..], gfx[1].Payload[60..]);   // the rest of the desc as the game gave it
        var blobs = db.Where(r => r.Tag == 'B').Select(r => Convert.ToHexStringLower(r.Payload.AsSpan(0, 20))).ToHashSet();
        Assert.All(ps, h => Assert.Contains(h, blobs));
    }

    /// <summary>The proxy's frame times on WARP (`selftest frames`): each present of a swap chain made by the hooked DXGI factory
    /// is one frame, with an overlay that hooked Present / Present1 before the proxy did and a present nested in another; a
    /// DXGI_PRESENT_TEST isn't one. A create's csv row names its thread and whether that thread presents. frames=0 in
    /// scskiller.ini: no frame log.</summary>
    [Fact]
    public void The_proxy_counts_each_present_once()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "frames");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        string Run(string ini)
        {
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\n{ini}");
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), "frames 20") { RedirectStandardOutput = true })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            return o;
        }
        var on = Run("");
        Assert.Contains("overlay 43", on);
        Assert.Contains("frames 41", on);
        // tid,presents at the end of each create row: the presenting thread's PSO, then another thread's
        var rows = File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv")).Where(l => !l.StartsWith('#')).Select(l => l.Split(',')).ToList();
        Assert.Equal(["1", "0"], rows.Select(r => r[8]));
        Assert.NotEqual(rows[0][7], rows[1][7]);
        File.Delete(Path.Combine(dir, "scskiller_frames.bin"));
        Assert.Contains("frames -1", Run("frames=0\r\n"));
    }

    /// <summary>The proxy's max_db_bytes on WARP: a db at the limit gets no record (never part of one), the csv still gets its
    /// row; under it the next record is appended whole, then nothing more. Needs this checkout's proxy built.</summary>
    [Fact]
    public void The_proxy_stops_recording_at_the_limit_and_keeps_the_timings()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "limit");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        var db = Path.Combine(dir, "scskiller.db");
        var seed = Environment.TickCount % 100000;
        void Run(long cap)
        {
            File.WriteAllText(Path.Combine(dir, "scskiller.ini"), $"[scskiller]\r\nmode=record\r\nmax_db_bytes={cap}\r\n");
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {++seed}") { RedirectStandardOutput = true })!;
            Assert.Contains("created 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        long Framed() => PsoDb.Read(db).Sum(r => 5L + r.Payload.Length);
        int Rows() => File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv")).Count(l => !l.StartsWith('#'));

        Run(0);   // 0 is a limit too: nothing at all
        Assert.Equal(0, new FileInfo(db).Length);
        Assert.Equal(1, Rows());
        using (var f = File.Create(db)) PsoDb.WriteBlob(f, new string('a', 40), new byte[4096]);
        var full = new FileInfo(db).Length;
        Run(full);
        Assert.Equal(full, new FileInfo(db).Length);
        Assert.Equal(2, Rows());
        Assert.Contains("recording limit reached", File.ReadAllText(Path.Combine(dir, "scskiller.log")));

        Run(full + 1);   // under it: the record and its blobs, whole
        Assert.Single(PsoDb.Read(db), r => r.Tag == 'C');
        Run(full + 1);
        Assert.Single(PsoDb.Read(db), r => r.Tag == 'C');
        Assert.Equal(new FileInfo(db).Length, Framed());
        Assert.Equal(4, Rows());
    }

    /// <summary>The proxy's keys file on WARP: a PSO it names isn't recorded again (the csv still marks it known), and a new PSO
    /// is recorded without the root signature the file names. Needs this checkout's proxy built.</summary>
    [Fact]
    public void The_proxy_records_only_what_the_keys_file_does_not_name()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var bin = Path.GetDirectoryName(warmExe)!;
        var dir = Path.Combine(_root, "keys");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n");
        var db = Path.Combine(dir, "scskiller.db");
        void Run(int seed)
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"chain {seed}") { RedirectStandardOutput = true })!;
            Assert.Contains("created 0x00000000", p.StandardOutput.ReadToEnd());
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        string Known() => File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv")).Last(l => !l.StartsWith('#')).Split(',')[2];
        var seed = Environment.TickCount % 100000;

        Run(seed);
        Assert.Equal(["B", "B", "C"], PsoDb.Read(db).Select(r => r.Tag.ToString()).Order());   // root signature, CS, PSO
        var store = Path.Combine(dir, "recording.db");
        Recordings.Merge(store, db, null);
        Recordings.WriteKeys(store, null, Path.Combine(dir, Recordings.KeysFile));
        Assert.True(Recordings.Rotate(db, new FileInfo(db).Length));

        Run(seed);
        Assert.Equal(0, new FileInfo(db).Length);
        Assert.Equal("1", Known());
        Run(seed + 1);   // another CS under the same root signature
        Assert.Equal(["B", "C"], PsoDb.Read(db).Select(r => r.Tag.ToString()));
        Assert.Equal("0", Known());

        // a first session of a game whose files ship that CS: the keys file names it, the record goes in without its bytes
        var cs = PsoDb.Parse(PsoDb.Read(db).Last()).Stages.Values.Single();
        File.Delete(store);
        Recordings.WriteKeys(store, new HashSet<string> { cs }, Path.Combine(dir, Recordings.KeysFile));
        Assert.True(Recordings.Rotate(db, new FileInfo(db).Length));
        Run(seed + 1);
        var recorded = PsoDb.Read(db).ToList();
        Assert.Equal(["B", "C"], recorded.Select(r => r.Tag.ToString()));   // the root signature's bytes only
        Assert.Equal(cs, PsoDb.Parse(recorded[1]).Stages.Values.Single());
        Assert.NotEqual(cs, PsoDb.Hex(recorded[0].Payload.AsSpan(0, 20)));
        Assert.Equal("0", Known());
    }

    /// <summary>A game update the scan sees (the store's build id, or without one the exe) takes the last index's shaders out
    /// of the keys file until a compile indexes the new build. A record a session wrote by hash before the scan, whose shader
    /// the new build doesn't ship: the import keeps it, the compile skips only it and stops naming it, and once the recorder
    /// has recorded it again with its bytes it compiles.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_game_update_takes_the_shipped_shaders_out_of_the_keys_file_until_the_next_index(bool storeVersion)
    {
        byte[] a = "DXBC shader in both builds"u8.ToArray(), b = "DXBC shader in the first build only"u8.ToArray(), m = "DXBC shader a mod built"u8.ToArray();
        static string Sha(byte[] x) => PsoDb.Hex(SHA1.HashData(x));
        static PsoDb.Rec Cs(byte[] x) => new('C', PsoDb.Compute(PsoDb.Zero, Sha(x)));
        var (keys, inbox) = (Path.Combine(_exeDir, Recordings.KeysFile), Path.Combine(_exeDir, "scskiller.db"));
        HashSet<string> Named() => File.ReadAllBytes(keys)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        void Session(params PsoDb.Rec[] written)
        {
            using var f = File.Create(inbox);
            foreach (var r in written) PsoDb.Write(f, r.Tag, r.Payload);
        }
        var planner = new FakePlanner();
        async Task<FakePlanner> Compile(ScsKiller k)
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
            return planner;
        }
        ScsKiller Build(string content, string? version, params byte[][] shaders)
        {
            var k = Killer(new BlobReader(Unreal, shaders.ToDictionary(Sha), indexed: true, content), planner, game: _game with { Version = version });
            k.ProcessNames = () => new HashSet<string>();
            return k;
        }

        var k = Build(new string('a', 40), storeVersion ? "100" : null, a, b);
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        await Compile(k);
        Assert.Equal(new[] { Sha(a), Sha(b) }.ToHashSet(), Named());

        // the update, then a session before SCSKiller scans: the recorder still has the first build's keys
        if (!storeVersion) File.AppendAllText(_game.ExePath, "patched");
        Session(Cs(b), new('B', [.. SHA1.HashData(m), .. m]), Cs(m));

        k = Build(new string('b', 40), storeVersion ? "101" : null, a);
        await k.ScanAsync(default);
        Assert.Equal(new[] { Sha(m), Cs(m).Key, Cs(b).Key }.ToHashSet(), Named());   // no shipped shader: a new pipeline goes in whole
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Assert.Equal(new[] { Cs(b).Key, Cs(m).Key }, PsoDb.Read(store).Where(r => r.Tag != 'B').Select(r => r.Key));

        Assert.Equal(1, (await Compile(k)).UnresolvedAtMaterialize);   // b alone
        Assert.Equal(new[] { Sha(a), Sha(m), Cs(m).Key }.ToHashSet(), Named());

        Session(new('B', [.. SHA1.HashData(b), .. b]), Cs(b));   // the game still creates it: recorded again, with its bytes
        k.RefreshGame(_game.Id);
        Assert.Equal(new[] { Sha(a), Sha(m), Cs(m).Key, Sha(b), Cs(b).Key }.ToHashSet(), Named());
        Assert.Equal(0, (await Compile(k)).UnresolvedAtMaterialize);
    }

    /// <summary>The emptied db filled again to the size, and at the write time, of what was imported is a new recording.</summary>
    [Fact]
    public async Task An_inbox_filled_again_to_the_same_size_and_write_time_is_imported()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        var (db, at) = (Path.Combine(_exeDir, "scskiller.db"), DateTime.UtcNow.AddMinutes(-1));
        PsoDb.Rec Record(byte cs)
        {
            var r = new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, PsoDb.Hex(SHA1.HashData([cs]))));
            using (var f = File.Create(db)) PsoDb.Write(f, r.Tag, r.Payload);
            File.SetLastWriteTimeUtc(db, at);
            k.RefreshGame(_game.Id);
            return r;
        }
        var recorded = new[] { Record(1), Record(2) };
        Assert.Equal(recorded.Select(r => r.Key), PsoDb.Read(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Select(r => r.Key));
    }

    /// <summary>A recorder installed before the game was ever indexed has no keys file; the compile's index writes one naming
    /// its shaders, which isn't a recording; a build with other shaders replaces them, and Clear recording leaves them.</summary>
    [Fact]
    public async Task The_keys_file_names_the_shaders_of_the_last_index_with_or_without_a_recording()
    {
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        HashSet<string> Named() => File.ReadAllBytes(keys)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        async Task<ScsKiller> Compiled(string content, int shaders)
        {
            var k = Killer(new FakeReader(Unreal, content, shaders));
            k.ProcessNames = () => new HashSet<string>();
            await k.ScanAsync(default);
            k.InstallRecorder(_game.Id);
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            return k;
        }
        var first = Killer(new FakeReader(Unreal));
        first.ProcessNames = () => new HashSet<string>();
        await first.ScanAsync(default);
        first.InstallRecorder(_game.Id);
        Assert.False(File.Exists(keys));

        var k = await Compiled(new string('a', 40), 5);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"{i:x40}").ToHashSet(), Named());
        Assert.Equal(0, k.Games.Single().RecordingBytes);
        Assert.False(k.ClearRecording(_game.Id));

        var cs = "DXBC compute shader built at run time"u8.ToArray();
        var (sha, rec) = (PsoDb.Hex(SHA1.HashData(cs)), new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, $"{1:x40}")));
        using (var f = File.Create(Path.Combine(_exeDir, "scskiller.db")))
        {
            PsoDb.Write(f, rec.Tag, rec.Payload);   // a shipped shader, as the recorder writes it: no bytes
            PsoDb.WriteBlob(f, sha, cs);
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, sha));
        }
        k.RefreshGame(_game.Id);
        Assert.Equal(5 + 3, Named().Count);
        Assert.Contains(rec.Key, Named());

        k = await Compiled(new string('b', 40), 1);   // the game was updated: shader 1 is gone
        Assert.Equal(new[] { $"{0:x40}", sha, new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, sha)).Key }.ToHashSet(), Named());

        Assert.True(k.ClearRecording(_game.Id));
        Assert.Equal(new[] { $"{0:x40}" }.ToHashSet(), Named());

        k.UninstallRecorder(_game.Id);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.log"), "loaded\n");
        Assert.True(k.ClearRecording(_game.Id));   // without the recorder nothing reads the keys file
        Assert.False(File.Exists(keys));
    }

    /// <summary>The db's share of the limit: the import empties the db into the copy, so the db may grow to what the copy
    /// leaves; paused = the db reached it.</summary>
    [Fact]
    public void The_db_cap_is_what_the_imported_recording_leaves()
    {
        const long L = 1000;
        Assert.Null(ScsKiller.DbCap(0, 10));
        Assert.Equal(1000, ScsKiller.DbCap(L, 0));   // first recording
        Assert.Equal(700, ScsKiller.DbCap(L, 300));
        Assert.Equal(0, ScsKiller.DbCap(L, 1200));
        Assert.Equal(("256 MB", "1 GB", "Unlimited"), (ScsKiller.LimitText(256), ScsKiller.LimitText(1024), ScsKiller.LimitText(0)));
        Assert.Equal(256, new Settings(1, WarmPriority.Idle, DriverUpdateMode.Off, 1, false).RecordingLimitMB);
    }

    [Theory]
    [InlineData(2048, 0)]   // a choice of an earlier version
    [InlineData(100, 128)]
    [InlineData(1024, 1024)]
    [InlineData(0, 0)]
    public void A_stored_recording_limit_that_is_no_choice_becomes_the_next_one_up_or_unlimited(int stored, int limit)
    {
        var k = Killer();
        k.Store.SaveSettings(k.Settings with { RecordingLimitMB = stored });
        Assert.Equal(limit, Killer().Settings.RecordingLimitMB);
    }

    [Fact]
    public async Task The_recording_limit_goes_to_the_ini_and_pauses_the_recorder_when_reached()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        Assert.Contains($"max_db_bytes={256L << 20}\r\n", File.ReadAllText(ini));
        Assert.False(k.Games.Single().RecordingPaused);

        var db = Path.Combine(_exeDir, "scskiller.db");
        using (var f = File.Create(db))
            for (int i = 0; i < 6; i++)
            {
                var cs = RandomNumberGenerator.GetBytes(100_000);   // shader bytes that don't compress, in no file of the game
                PsoDb.WriteBlob(f, PsoDb.Hex(SHA1.HashData(cs)), cs);
                PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, PsoDb.Hex(SHA1.HashData(cs))));
            }
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "1.0,C,0,0,5.0\n");
        var held = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // the game runs: the recorder has it open
        k.RefreshGame(_game.Id);   // imported, not emptied: 600 KB here and about as much in the copy
        Assert.False(k.Games.Single().RecordingPaused);
        Assert.Equal(6 * (25 + 100_000 + 5 + 48), new FileInfo(db).Length);

        k.Settings = k.Settings with { RecordingLimitMB = 1 };
        k.ReconcileRecorders(_game.Id);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.True(s.RecordingPaused);
        var stored = new FileInfo(Path.Combine(k.Store.GameDir(_game.Id), "recording.db")).Length;
        Assert.InRange(stored, 600_000, 620_000);
        Assert.Equal(new FileInfo(db).Length + stored + 14, s.RecordingBytes);   // the db, the copy and the csv
        Assert.Equal("Recording paused: limit reached (1 MB)", ScsKiller.PausedNote(k.Settings));
        Assert.Contains($"max_db_bytes={(1 << 20) - stored}\r\n", File.ReadAllText(ini));

        held.Dispose();   // the game exits: the db is emptied, recording goes on
        k.RefreshGame(_game.Id);
        Assert.Equal(0, new FileInfo(db).Length);
        Assert.False(k.Games.Single().RecordingPaused);

        k.Settings = k.Settings with { RecordingLimitMB = 0 };
        k.ReconcileRecorders(_game.Id);
        k.RefreshGame(_game.Id);
        Assert.DoesNotContain("max_db_bytes", File.ReadAllText(ini));
        Assert.False(k.Games.Single().RecordingPaused);

        k.UninstallRecorder(_game.Id);   // rewritten twice, still SCSKiller's by its hash: removed
        Assert.False(File.Exists(ini));

        k.InstallRecorder(_game.Id);
        File.AppendAllText(ini, "threads=4\r\n");   // the user's own edit: not rewritten
        k.Settings = k.Settings with { RecordingLimitMB = 256 };
        k.ReconcileRecorders(_game.Id);
        Assert.EndsWith("threads=4\r\n", File.ReadAllText(ini));
    }

    [Fact]
    public async Task Clear_recording_deletes_only_the_recorders_data_files_and_never_while_the_game_runs()
    {
        var k = Killer(new FakeReader(Unreal));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var data = new[] { "scskiller.db", "scskiller_creates.csv", "scskiller.log" }.Select(f => Path.Combine(_exeDir, f)).ToList();
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        using (var f = File.Create(data[0])) PsoDb.Write(f, 'C', new byte[48]);
        File.WriteAllText(data[1], "1.0,C,0,0,5.0\n");
        File.WriteAllText(data[2], "loaded\n");
        k.RefreshGame(_game.Id);   // imported: the db emptied, the keys file written
        var copy = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Assert.True(File.Exists(copy));
        Assert.True(k.Games.Single().RecordingBytes > 0);
        Assert.True(File.Exists(keys));
        var kept = Directory.GetFiles(_game.InstallDir, "*", SearchOption.AllDirectories).Except(data.Append(keys)).ToList();   // the game's files and the recorder
        Assert.Contains(Path.Combine(_exeDir, "d3d12.dll"), kept);
        Assert.Contains(Path.Combine(_exeDir, "scskiller.ini"), kept);
        var imported = k.Store.LoadGame(_game.Id).RecordingImportedAt;

        running.Add("Fake-Win64-Shipping");
        Assert.Throws<InvalidOperationException>(() => k.ClearRecording(_game.Id));
        Assert.All(data.Append(copy), f => Assert.True(File.Exists(f)));

        running.Clear();
        Assert.True(k.ClearRecording(_game.Id));
        Assert.All(data.Append(copy), f => Assert.False(File.Exists(f)));
        Assert.False(File.Exists(keys));   // never indexed: nothing left to name
        Assert.All(kept, f => Assert.True(File.Exists(f)));
        Assert.Equal(0, k.Games.Single().RecordingBytes);
        Assert.True(k.Store.LoadGame(_game.Id).RecordingImportedAt > imported);   // the next compile re-plans without it
        Assert.False(k.ClearRecording(_game.Id));
    }

    /// <summary>A warmed game whose play session recorded new pipelines shows them as more to compile (the stale state with
    /// the "Add to queue" action); compiling it again includes them and it is warmed again.</summary>
    [Fact]
    public async Task New_recorded_pipelines_ask_for_a_compile_that_includes_them()
    {
        var k = await Warmed();
        var db = Path.Combine(_exeDir, "scskiller.db");
        void Record(int n)
        {
            using var f = new FileStream(db, FileMode.Append);
            for (int i = 0; i < n; i++)
            {
                var cs = Guid.NewGuid().ToByteArray();
                var sha = PsoDb.Hex(SHA1.HashData(cs));
                PsoDb.WriteBlob(f, sha, cs);
                PsoDb.Write(f, 'C', PsoDb.Compute(sha, sha));
            }
        }
        Record(3);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, "3 new pipelines recorded; compile again to include them"), (s.Status, s.StatusReason));
        Assert.Contains(s, k.StaleGames());

        await WarmOnce(k, _game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        Record(1);   // appended: only the new records count
        k.RefreshGame(_game.Id);
        Assert.Equal("1 new pipeline recorded; compile again to include them", k.Games.Single().StatusReason);
    }

    /// <summary>Pipelines the compiled plan already has don't count: a session that only re-records them leaves the game
    /// warmed; one more that the plan lacks is the one to compile.</summary>
    [Fact]
    public async Task Re_recording_planned_pipelines_asks_for_nothing()
    {
        static PsoDb.Rec Pso()
        {
            var sha = PsoDb.Hex(SHA1.HashData(Guid.NewGuid().ToByteArray()));
            return new('C', PsoDb.Compute(sha, sha));
        }
        var planned = new List<PsoDb.Rec> { Pso(), Pso() };
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: planned));
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var db = Path.Combine(_exeDir, "scskiller.db");
        void Record(params PsoDb.Rec[] rs)
        {
            using var f = new FileStream(db, FileMode.Append);
            foreach (var r in rs) PsoDb.Write(f, r.Tag, r.Payload);
        }

        Record([.. planned]);
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.Empty(k.StaleGames());
        Assert.Equal(0, k.Store.LoadGame(_game.Id).RecordedSinceWarm);

        Record(planned[0], Pso());
        k.RefreshGame(_game.Id);
        Assert.Equal("1 new pipeline recorded; compile again to include them", k.Games.Single().StatusReason);
    }

    [Fact]
    public async Task Record_alongside_refuses_a_mod_that_reads_its_own_file_name()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var bytes = Planning.MiddlewarePackTests.Pe("OptiScaler.dll");   // OptiScaler installed as d3d12.dll
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        Assert.StartsWith("OptiScaler.dll picks its role", ScsKiller.ChainBlocker(mod));
        k.SetRecordAlongsideMod(_game.Id, true);
        Assert.Equal(ScsKiller.SkipModNotChainable, k.Games.Single().RecorderSkip);
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(_game.Id));
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
    }

    /// <summary>vkd3d-proton as the game's d3d12.dll: named in the game's state, and refused when asked to record alongside
    /// it, with its own reason (a D3D12 warm doesn't reach its Vulkan pipelines); nothing in the folder changes.</summary>
    [Fact]
    public async Task Record_alongside_refuses_vkd3d_proton_with_its_reason()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        byte[] bytes = [.. Planning.MiddlewarePackTests.Pe("d3d12.dll"), .. "vkd3d-proton - build: 2.14"u8];
        File.WriteAllBytes(mod, bytes);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.Equal(("d3d12.dll", false, ScsKiller.SkipForeignDll), (s.RecorderMod, s.RecordAlongsideMod, s.RecorderSkip));
        k.SetRecordAlongsideMod(_game.Id, true);
        s = k.Games.Single();
        Assert.Equal((true, ScsKiller.SkipVulkanMod), (s.RecordAlongsideMod, s.RecorderSkip));
        Assert.Equal(bytes, File.ReadAllBytes(mod));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
    }

    [Fact]
    public async Task A_mod_reinstalled_over_the_chained_recorder_is_never_overwritten()
    {
        var mod = Path.Combine(_exeDir, "d3d12.dll");
        var old = Planning.MiddlewarePackTests.Pe("d3d12.dll", [1]);
        File.WriteAllBytes(mod, old);
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.SetRecordAlongsideMod(_game.Id, true);
        k.InstallRecorder(_game.Id);
        var fresh = Planning.MiddlewarePackTests.Pe("d3d12.dll", [2]);
        File.WriteAllBytes(mod, fresh);   // the mod's installer put its d3d12.dll back over ours

        k.UninstallRecorder(_game.Id);
        Assert.Equal(fresh, File.ReadAllBytes(mod));
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(_exeDir, ScsKiller.ChainName)));   // left, not lost
        Assert.Null(k.Store.LoadGame(_game.Id).RecorderChained);
        Assert.Contains("another d3d12.dll is in its place", File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log")));
    }

    [Fact]
    public async Task The_games_recording_is_an_inbox_merged_by_record_key_then_emptied()
    {
        static byte[] Db(params string[] shaders)
        {
            using var m = new MemoryStream();
            foreach (var s in shaders)
            {
                var b = System.Text.Encoding.ASCII.GetBytes("DXBC shader built at run time: " + s);
                PsoDb.WriteBlob(m, PsoDb.Hex(SHA1.HashData(b)), b);
                PsoDb.Write(m, 'C', PsoDb.Compute(PsoDb.Zero, PsoDb.Hex(SHA1.HashData(b))));
            }
            return m.ToArray();
        }
        static List<string> Keys(IEnumerable<PsoDb.Rec> recs) => recs.Select(r => r.Key).ToList();
        var game = Path.Combine(_exeDir, "scskiller.db");
        var k = Killer(new FakeReader(Unreal));
        var imported = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        File.WriteAllBytes(game, Db("a", "b"));
        await k.ScanAsync(default);
        Assert.Equal(Keys(HashOnly.Records(Db("a", "b"))), Keys(PsoDb.Read(imported)));
        Assert.True(PsoDb.IsCompact(imported));
        Assert.Equal(0, new FileInfo(game).Length);   // emptied once imported

        File.WriteAllBytes(game, Db("a", "b", "c"));   // recorded again (a recorder without the keys file): only c is new
        await k.ScanAsync(default);
        Assert.Equal(Keys(HashOnly.Records(Db("a", "b", "c"))), Keys(PsoDb.Read(imported)));

        File.WriteAllBytes(game, Db("d"));
        await k.ScanAsync(default);
        Assert.Equal(Keys(HashOnly.Records(Db("a", "b", "c", "d"))), Keys(PsoDb.Read(imported)));

        var written = File.GetLastWriteTimeUtc(imported);   // nothing new since: not written again
        await k.ScanAsync(default);
        Assert.Equal(written, File.GetLastWriteTimeUtc(imported));
    }

    /// <summary>A recording stored before compact recordings (a proxy db beside its merge with the community's, the game
    /// folder's db imported as a copy) is converted once, in the background after a scan, never while the game runs: shader
    /// bytes the build's index has are left out, recording.all.db goes, the game folder's db is emptied, and nothing counts
    /// as newly recorded. A migration that fails midway leaves the old file as it was, and the next scan finishes it.</summary>
    [Fact]
    public async Task A_recording_from_before_compact_storage_is_converted_once_and_an_interrupted_conversion_loses_nothing()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = "DXBC shipped shader"u8.ToArray(), r = "DXBC shader built at run time"u8.ToArray();
        static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));
        static PsoDb.Rec Blob(byte[] b) => new('B', [.. SHA1.HashData(b), .. b]);
        PsoDb.Rec ca = new('C', PsoDb.Compute(Sha(rs), Sha(a))), cr = new('C', PsoDb.Compute(Sha(rs), Sha(r)));
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = Killer(new FakeReader(Unreal), game: game);
        k.ProcessNames = () => running;
        await k.ScanAsync(default);
        k.InstallRecorder(game.Id);
        running.Add("Fake-Win64-Shipping");

        var dir = k.Store.GameDir(game.Id);
        var store = Path.Combine(dir, "recording.db");
        byte[] Db(params PsoDb.Rec[] recs)
        {
            using var m = new MemoryStream();
            foreach (var x in recs) PsoDb.Write(m, x.Tag, x.Payload);
            return m.ToArray();
        }
        var legacy = Db(Blob(rs), Blob(a), ca, Blob(r), cr);
        File.WriteAllBytes(store, legacy);
        File.WriteAllBytes(Path.Combine(dir, "recording.all.db"), legacy);
        File.WriteAllText(Path.Combine(dir, "recording.all.db.key"), "stamp");
        var inbox = Path.Combine(_exeDir, "scskiller.db");
        File.WriteAllBytes(inbox, legacy);   // imported as a copy, the way it was
        Sharing.SaveShipped(dir, new ShaderIndex(content, ["PCD3D_SM6"], new Dictionary<string, ShaderInfo> { [Sha(a)] = null! }, []));
        var rec = k.Store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion, rec.RecordingImportedAt) = (content, "100", DateTimeOffset.Now.AddDays(-1));
        k.Store.SaveGame(game.Id, rec);

        k.Settings = k.Settings with { RecordingLimitMB = 1 };
        File.WriteAllBytes(Path.Combine(dir, "recording.all.db"), [.. legacy, .. new byte[2 << 20]]);   // over the limit alone

        await k.ScanAsync(default);   // the game runs: left for later
        await k.RecordingMigration;
        Assert.Equal(legacy, File.ReadAllBytes(store));
        Assert.Equal(legacy, File.ReadAllBytes(inbox));
        Assert.False(k.Games.Single().RecordingPaused);   // the old merge doesn't count against the limit: it's deleted, not kept

        running.Clear();
        Directory.CreateDirectory(store + ".tmp");   // the conversion fails midway (this is where it writes)
        await k.ScanAsync(default);
        await k.RecordingMigration;
        Assert.Equal(legacy, File.ReadAllBytes(store));
        Assert.True(File.Exists(Path.Combine(dir, "recording.all.db")));
        Assert.Equal(legacy, File.ReadAllBytes(inbox));

        Directory.Delete(store + ".tmp");
        await k.ScanAsync(default);
        await k.RecordingMigration;
        Assert.True(PsoDb.IsCompact(store));
        Assert.Equal(new[] { Blob(rs), ca, Blob(r), cr }.Select(x => x.Key), PsoDb.Read(store).Select(x => x.Key));   // a's bytes come from the install
        Assert.False(File.Exists(Path.Combine(dir, "recording.all.db")));
        Assert.False(File.Exists(Path.Combine(dir, "recording.all.db.key")));
        Assert.Equal(0, new FileInfo(inbox).Length);
        var keys = File.ReadAllBytes(Path.Combine(_exeDir, Recordings.KeysFile))[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        Assert.Equal(new[] { Sha(a), Sha(rs), Sha(r), ca.Key, cr.Key }.Order(), keys.Order());
        var after = k.Store.LoadGame(game.Id);
        Assert.Equal((rec.RecordingImportedAt, 0L, content), (after.RecordingImportedAt, after.RecordedSinceWarm, after.RecordingIndexHash));
        Assert.Equal(new[] { store, inbox }.Sum(f => new FileInfo(f).Length), k.Games.Single().RecordingBytes);
    }

    /// <summary>A community recording merged the old way, with no recording of this PC's: the merge goes and no empty
    /// recording is left in its place (the game would then look recorded).</summary>
    [Fact]
    public async Task A_community_only_merge_from_before_compact_storage_leaves_no_recording()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        var dir = k.Store.GameDir(_game.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "recording.all.db"), CommunityTests.HashOnly(new string('1', 40)));
        File.WriteAllText(Path.Combine(dir, "recording.all.db.key"), "stamp");
        await k.ScanAsync(default);
        await k.RecordingMigration;
        Assert.Empty(Directory.GetFiles(dir, "recording*"));
    }

    /// <summary>A compile after the recording was stored without an index (never compiled, or a build since) leaves out the
    /// shader bytes the index has; the plan and the warm still get every recorded shader, from the install.</summary>
    [Fact]
    public async Task A_compile_keeps_only_the_shader_bytes_its_index_lacks_and_plans_and_warms_with_all_of_them()
    {
        var rs = CommunityTests.RootSignature();
        byte[] vs = "DXBC vertex shader in the game's files"u8.ToArray(), rt = "DXBC vertex shader built at run time"u8.ToArray();
        static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));
        static PsoDb.Rec Blob(byte[] b) => new('B', [.. SHA1.HashData(b), .. b]);
        PsoDb.Rec S(byte[] v) => new('S', PsoDb.Stream(Sha(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha(v) }, [], 3, [], 0));
        var planner = new NeedsRecordingPlanner();
        var k = Killer(new BlobReader(Unreal, new() { [Sha(vs)] = vs }, indexed: true), planner);
        var store = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        PsoDb.WriteCompact(store, [Blob(rs), Blob(vs), S(vs), Blob(rt), S(rt)]);
        await k.ScanAsync(default);

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(new[] { Blob(rs), Blob(vs), S(vs), Blob(rt), S(rt) }.Select(r => r.Key).Order(), planner.BuiltWith!.Order());
        Assert.Equal(0, planner.Inner.UnresolvedAtMaterialize);
        Assert.Equal(new[] { Blob(rs), S(vs), Blob(rt), S(rt) }.Select(r => r.Key), PsoDb.Read(store).Select(r => r.Key));
        Assert.Equal("content-1", k.Store.LoadGame(_game.Id).RecordingIndexHash);
    }

    [Fact]
    public async Task Reconcile_waits_while_the_game_runs()
    {
        var g = FakeGame("test:run", "Runner");
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "RUNNER" };   // by name only, any case
        var k = RecordKiller([g], running: () => running);
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        Assert.False(File.Exists(Dll(g)));
        Assert.Equal("installs when the game exits", k.Games.Single().RecorderNote);
        Assert.True(k.Games.Single().RecorderEffective);

        running.Clear();   // it exited: the watcher calls this
        k.ReconcileRecorders(g.Id);
        Assert.True(ScsKiller.IsOurProxy(Dll(g)));
        Assert.Null(k.Games.Single().RecorderNote);

        running.Add("Runner");
        k.SetRecorderOverride(g.Id, RecorderOverride.Off);
        Assert.True(ScsKiller.IsOurProxy(Dll(g)));
        Assert.Equal("removed when the game exits", k.Games.Single().RecorderNote);
        Assert.Throws<InvalidOperationException>(() => k.UninstallRecorder(g.Id));

        running.Clear();
        k.ReconcileRecorders(g.Id);
        Assert.False(File.Exists(Dll(g)));
    }

    [Fact]
    public async Task A_recorder_from_another_SCSKiller_build_is_replaced_once_the_game_is_not_running()
    {
        var g = FakeGame("test:old", "Old");
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var k = RecordKiller([g], running: () => running);
        await k.ScanAsync(default);
        k.InstallRecorder(g.Id);
        var old = File.ReadAllBytes(Dll(g));

        File.WriteAllBytes(_proxy, [.. "MZ other build SCSKiller_StartWarm "u8, .. Guid.NewGuid().ToByteArray()]);
        k = RecordKiller([g], running: () => running);
        await k.ScanAsync(default);
        running.Add("Old");
        Assert.Contains("updates when the game exits", Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(g.Id)).Message);
        Assert.Equal(old, File.ReadAllBytes(Dll(g)));

        running.Clear();
        k.ReconcileRecorders(g.Id);
        Assert.Equal(File.ReadAllBytes(_proxy), File.ReadAllBytes(Dll(g)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_proxy))), k.Store.LoadGame(g.Id).RecorderFiles["d3d12.dll"]);
        Assert.Null(k.Games.Single().RecorderNote);
    }

    [Fact]
    public async Task The_watcher_installs_a_waiting_recorder_when_the_game_exits()
    {
        var g = FakeGame("test:watched", "Watched");
        bool playing = true;
        var k = RecordKiller([g], running: () => playing ? new HashSet<string> { "Watched" } : new HashSet<string>());
        k.RunningGameExes = () => playing ? new HashSet<string> { "Watched.exe" } : new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);   // the scan's reconcile finds the game running: waits
        Assert.False(File.Exists(Dll(g)));
        Assert.Equal("installs when the game exits", k.Games.Single().RecorderNote);

        k.PollGames();
        Assert.True(k.IsPlaying(g.Id));
        playing = false;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.False(k.IsPlaying(g.Id));
        Assert.True(ScsKiller.IsOurProxy(Dll(g)));
        Assert.Null(k.Games.Single().RecorderNote);
        Assert.True(k.Games.Single().RecorderInstalled);
    }

    [Fact]
    public async Task An_existing_recorder_install_becomes_an_On_override()
    {
        var mine = FakeGame("test:mine", "Mine");
        var plain = FakeGame("test:plain", "Plain");
        var k = RecordKiller([mine, plain]);
        File.Copy(_proxy, Dll(mine));   // a record without an override (not migrated)
        var old = k.Store.LoadGame(mine.Id);
        old.RecorderFiles["d3d12.dll"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Dll(mine))));
        k.Store.SaveGame(mine.Id, old);
        Assert.Null(k.Store.LoadGame(mine.Id).Recorder);
        k.Settings = k.Settings with { RecordAllGames = false };
        await k.ScanAsync(default);

        k.ReconcileRecorders();
        Assert.True(ScsKiller.IsOurProxy(Dll(mine)));   // the global is off, the user's own install stays
        Assert.Equal(RecorderOverride.On, k.Store.LoadGame(mine.Id).Recorder);
        Assert.Equal(RecorderOverride.Default, k.Store.LoadGame(plain.Id).Recorder);
        Assert.False(File.Exists(Dll(plain)));

        k.Settings = k.Settings with { RecordAllGames = true };
        k.ReconcileRecorders();
        k.Settings = k.Settings with { RecordAllGames = false };
        k.ReconcileRecorders();   // installed by the default: not migrated to On a second time
        Assert.False(File.Exists(Dll(plain)));
        Assert.Equal(RecorderOverride.Default, k.Store.LoadGame(plain.Id).Recorder);
    }

    [Fact]
    public void Start_with_Windows_writes_the_Run_value_only_when_it_differs_and_removes_it_when_off()
    {
        string? value = null;   // the HKCU Run value, faked
        int writes = 0;
        bool Apply(bool on, string exe) => WindowsStartup.Apply(on, exe, () => value, v => (value, writes) = (v, writes + 1));
        Assert.True(Apply(true, @"C:\Apps\SCSKiller\SCSKiller.exe"));
        Assert.Equal("\"C:\\Apps\\SCSKiller\\SCSKiller.exe\" --tray", value);
        Assert.False(Apply(true, @"C:\Apps\SCSKiller\SCSKiller.exe"));   // every start: nothing to do
        Assert.True(Apply(true, @"D:\Moved\SCSKiller.exe"));             // the app moved: the value follows
        Assert.True(Apply(false, @"D:\Moved\SCSKiller.exe"));
        Assert.Null(value);
        Assert.False(Apply(false, @"D:\Moved\SCSKiller.exe"));
        Assert.Equal(3, writes);
    }

    [Fact]
    public void Settings_and_game_state_round_trip()
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        Assert.Equal(AppStore.DefaultSettings, store.LoadSettings());
        Assert.Equal(new Settings(Environment.ProcessorCount, WarmPriority.BelowNormal, DriverUpdateMode.Ask, 8, true),
            AppStore.DefaultSettings);
        Assert.False(AppStore.DefaultSettings.ShareRecordings);   // opt-in: off until the user ticks it
        Assert.False(AppStore.DefaultSettings.SharePromptDismissed);
        var custom = new Settings(12, WarmPriority.Idle, DriverUpdateMode.WhenIdle, 4, false, true, MaximumPlans: true);
        store.SaveSettings(custom);
        Assert.Equal(custom, store.LoadSettings());
        Assert.Contains("\"WhenIdle\"", File.ReadAllText(Path.Combine(_root, "data", "settings.json")));
        Assert.True(custom.StartWithWindows);   // on by default, also for a settings.json from before the setting:
        File.WriteAllText(Path.Combine(_root, "data", "settings.json"), System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(_root, "data", "settings.json")), @",\s*""StartWithWindows"": true", ""));
        Assert.DoesNotContain("StartWithWindows", File.ReadAllText(Path.Combine(_root, "data", "settings.json")));
        Assert.True(store.LoadSettings().StartWithWindows);
        // the community database is on by default, also for a settings.json that saved the old, never settable CommunityPlans
        File.WriteAllText(Path.Combine(_root, "data", "settings.json"), File.ReadAllText(Path.Combine(_root, "data", "settings.json"))
            .Replace("\"UseCommunityDb\": true", "\"CommunityPlans\": false"));
        Assert.Contains("CommunityPlans", File.ReadAllText(Path.Combine(_root, "data", "settings.json")));
        Assert.True(store.LoadSettings().UseCommunityDb);

        var plan = new Plan("steam:1", "hash", "PCD3D_SM6", "nvidia-1", new PlanStats(10, 20, 3, 4, true), @"C:\x\plan.bin");
        var rec = new GameRecord
        {
            IndexContentHash = "hash", ShaderCount = 5, Plan = plan, PlanBuiltAt = DateTimeOffset.Now, ResumeAt = 7,
            WarmedDriverVersion = "610.88", WarmedAt = DateTimeOffset.Now, LastWarmTime = TimeSpan.FromSeconds(288.5), BytesPerPso = 24576.5,
            RecorderFiles = { ["d3d12.dll"] = "ABC" },
        };
        store.SaveGame("steam:1", rec);
        Assert.True(File.Exists(Path.Combine(_root, "data", "games", "steam_1", "state.json")));
        var back = store.LoadGame("steam:1");
        Assert.Equal(plan, back.Plan);
        Assert.Equal((rec.WarmedAt, rec.LastWarmTime, rec.ResumeAt, rec.BytesPerPso), (back.WarmedAt, back.LastWarmTime, back.ResumeAt, back.BytesPerPso));
        Assert.Equal("ABC", back.RecorderFiles["d3d12.dll"]);
        Assert.Null(store.LoadGame("steam:2").Plan);
    }

    /// <summary>A build that left most stage sets out (no root signature covers them: Hogwarts Legacy without a recording) keeps
    /// the game compilable, and its status says so and what adds the rest, from the last build's stats (state.json).</summary>
    [Fact]
    public async Task A_partial_plan_says_what_it_compiles_and_that_a_recording_adds_the_rest()
    {
        var partial = new PlanStats(0, 41_422, 41_422, 105, false, Uncovered: 182_857);
        var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: partial));
        await k.ScanAsync(default);
        Assert.Equal("synthesized templates", k.Games.Single().StatusReason);   // before a build nothing is known
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);   // the plan it has still compiles
        Assert.Equal(182_857, s.Plan!.Uncovered);
        Assert.Contains($"; compiles {41_422:N0} pipelines; a 5-minute recording lets SCSKiller rebuild the rest", s.StatusReason);

        var rec = k.Store.LoadGame(_game.Id);   // planned, not warmed yet: Ready with the same note
        rec.WarmedAt = null;
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        Assert.Equal((GameStatus.Ready, ScsKiller.PartialNote(partial)), (k.Games.Single().Status, k.Games.Single().StatusReason));

        Assert.False(ScsKiller.IsPartial(new PlanStats(0, 417_154, 417_154, 202, true, Uncovered: 6)));    // a handful left out: a full plan
        Assert.False(ScsKiller.IsPartial(new PlanStats(0, 900, 900, 5, true, Uncovered: 100)));           // 10%: not above
        Assert.True(ScsKiller.IsPartial(new PlanStats(0, 900, 900, 5, true, Uncovered: 101)));
        Assert.Contains($"{182_857:N0} more shader combinations", ScsKiller.PartialNote(partial with { Recorded = 500 }));   // recorded: no recording to ask for
    }

    /// <summary>A game whose ray tracing the plan can't compile without a recording (AMD, or an engine without a collection
    /// rule) doesn't look Ready or Warmed after its build: it needs a recording, with one plain reason, and says when the
    /// community database has no entry for its build; compiling the rest stays possible (a partial compile).</summary>
    [Fact]
    public async Task Ray_tracing_the_plan_cannot_compile_makes_the_game_need_a_recording()
    {
        var rt = new PlanStats(0, 40_000, 40_000, 100, true, RtLibraries: 12_000, RtUncovered: 12_000);
        var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: rt));
        await k.ScanAsync(default);
        Assert.Equal(GameStatus.Ready, k.Games.Single().Status);   // before a build nothing is known
        Directory.CreateDirectory(Path.Combine(_root, "data", "community"));
        File.WriteAllBytes(Path.Combine(_root, "data", "community", "manifest.bin"), CommunityTests.Manifest());   // fetched, no entry for it
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);   // the rest compiled
        var s = k.Games.Single();
        Assert.Equal((GameStatus.NeedsRecording, ScsKiller.RtNote(false)), (s.Status, s.StatusReason));
        Assert.Equal(false, s.InCommunityDb);
        Assert.Contains("not in the community database yet", s.StatusReason);
        Assert.Equal("Ray-traced effects need one short recording; " + ScsKiller.InDbNote, ScsKiller.RtNote(true));   // the database named once

        Assert.False(ScsKiller.NeedsRtRecording(rt with { RtUncovered = 1_200 }));   // 10%: not above
        Assert.True(ScsKiller.NeedsRtRecording(rt with { RtUncovered = 1_201 }));
        Assert.False(ScsKiller.NeedsRtRecording(rt with { RtLibraries = 0, RtUncovered = 0 }));   // no ray tracing shaders
    }

    /// <summary>Coverage: the stage sets found minus the ones left out, as the detail page shows it (floored, 100 only when
    /// nothing is left out); none for a plan without the count (DirectX 11 only, or built before it).</summary>
    [Fact]
    public void Coverage_is_the_found_stage_sets_the_plan_compiles()
    {
        Assert.Null(ScsKiller.Coverage(null));
        Assert.Null(ScsKiller.CoveragePercent(new PlanStats(0, 900, 900, 5, true)));   // StageSets 0: not counted
        Assert.Equal(0.25, ScsKiller.Coverage(new PlanStats(0, 1, 1, 1, true, StageSets: 4, LeftOut: 3)));
        Assert.Equal(18, ScsKiller.CoveragePercent(new PlanStats(0, 41_422, 41_422, 105, false, Uncovered: 182_857, StageSets: 224_279, LeftOut: 182_857)));
        Assert.Equal(99, ScsKiller.CoveragePercent(new PlanStats(0, 1, 1, 1, true, StageSets: 100_000, LeftOut: 1)));   // 99.999%: never "100" with one left out
        Assert.Equal(100, ScsKiller.CoveragePercent(new PlanStats(0, 1, 1, 1, true, StageSets: 7, LeftOut: 0)));
    }

    [Fact]
    public async Task Skipped_psos_are_counted_apart_from_failed_in_the_progress_the_done_note_and_the_game_state()
    {
        var warmer = new FakeWarmer(failed: 3);
        var progress = new List<WarmProgress>();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(skipped: 50), warmer);
        k.QueueChanged += q => { if (q.Progress is { } p) lock (progress) progress.Add(p); };
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));

        var done = k.Queue.Single();
        Assert.Equal(QueueStage.Done, done.Stage);
        Assert.Equal((10000L, 3L, 50L), (done.Progress!.Done, done.Progress.Failed, done.Progress.Skipped));   // skipped: not in Total, not failed
        Assert.Equal("3 failed (the driver rejected them), 50 skipped (a shader not in this install)", done.Note);
        lock (progress) Assert.All(progress, p => Assert.Equal(50, p.Skipped));   // the warm's own lines carry it too
        var s = k.Games.Single();
        Assert.Equal((3L, 50L), (s.LastWarmFailed, s.LastWarmSkipped));
        Assert.Equal((3L, 50L), (k.Store.LoadGame(_game.Id).LastWarmFailed, k.Store.LoadGame(_game.Id).LastWarmSkipped));

        Assert.Equal(" 10000/10000 (3 failed, 50 skipped: not in this install) 1000/s - 3 failed (the driver rejected them), 50 skipped (a shader not in this install)",
            ScsKiller.ProgressText(done with { Progress = done.Progress with { PerSecond = 1000 } }));
        Assert.Equal(" 5/9 (0 failed) 2/s", ScsKiller.ProgressText(new QueueItem("g", QueueStage.Warming, new WarmProgress(5, 9, 0, 2), null)));
        Assert.Equal("", ScsKiller.ProgressText(new QueueItem("g", QueueStage.Waiting, null, null)));
        Assert.Null(ScsKiller.WarmCounts(0, 0));
        Assert.Equal("2 failed (the driver rejected them)", ScsKiller.WarmCounts(2, 0));
        Assert.Equal("7 skipped (a shader not in this install)", ScsKiller.WarmCounts(0, 7));
        Assert.Equal("2 failed (the driver rejected them), 3 skipped (they crash the GPU driver)", ScsKiller.WarmCounts(2, 0, 3));
    }

    [Fact]
    public async Task A_clean_warm_has_no_done_note()
    {
        var k = await Warmed();
        Assert.Null(k.Queue.Single().Note);
        Assert.Equal((0L, 0L), (k.Games.Single().LastWarmFailed, k.Games.Single().LastWarmSkipped));
    }

    [Fact]
    public async Task Queue_indexes_plans_materializes_warms_records_and_goes_stale_on_a_new_driver()
    {
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(new EngineInfo("Unreal", "4.26", null, "D3D12", false, null)), warmer: warmer);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Ready, s.Status);

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.True(warmer.SawMaterializedWork);
        Assert.False(Directory.Exists(Path.Combine(k.Store.GameDir(_game.Id), "work")));
        s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.Equal("100.01", s.WarmedDriverVersion);
        Assert.Equal(3000, s.ShaderCount);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(1000.0, rec.PsoPerSecond);
        Assert.Equal(20000.0 * 1024 / 10000, rec.BytesPerPso);
        Assert.Equal(TimeSpan.FromSeconds(10), s.EstimatedWarmTime);

        var after = Killer(new FakeReader(new EngineInfo("Unreal", "4.26", null, "D3D12", false, null)), driver: "101.00");
        await after.ScanAsync(default);
        var stale = Assert.Single(after.StaleGames());
        Assert.Contains("100.01 -> 101.00", stale.StatusReason);
    }

    [Fact]
    public void Session_log_uses_the_last_launch()
    {
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, "393.0,G,0,0,94.692\n402.5,C,0,0,9.404\n295.5,G,0,0,3.286\n296.2,c,0,0,0.608\n296.6,S,1,1,0.396\n400.0,S,0,1,12.5\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(400), 4, 1, 1, 2, 12.5), SessionLog.Read(csv).Last);
        // newer proxies append the PSO key and the proxy's own time: same stats
        File.AppendAllText(csv, $"401.0,S,0,0,20.5,{new string('a', 40)},0.031\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(401), 5, 1, 1, 3, 20.5), SessionLog.Read(csv).Last);
        Assert.Null(SessionLog.Read(Path.Combine(_root, "missing.csv")).Last);
    }

    /// <summary>A compiled RayQuery PSO's create at NVIDIA's floor is counted apart: not a compile, not the worst; over the
    /// floor (a miss at cold cost) it is a compile again, as is any other PSO's slow create.</summary>
    [Fact]
    public void Session_log_counts_RayQuery_creates_at_the_floor_apart()
    {
        string rq = new('a', 40), other = new('b', 40);
        var db = Path.Combine(_root, "recording.db");
        using (var f = File.Create(db))
        {
            var (traces, plain) = (Tests.Planning.RtCollectionTests.Sfi0(0x100000), Tests.Planning.RtCollectionTests.Sfi0(0x4000));
            var (tracesSha, plainSha) = (Convert.ToHexStringLower(SHA1.HashData(traces)), Convert.ToHexStringLower(SHA1.HashData(plain)));
            PsoDb.WriteBlob(f, tracesSha, traces);
            PsoDb.WriteBlob(f, plainSha, plain);
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, tracesSha));
            PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, plainSha));
            (rq, other) = (new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, tracesSha)).Key, new PsoDb.Rec('C', PsoDb.Compute(PsoDb.Zero, plainSha)).Key);
        }
        var keysFile = Path.Combine(_root, "rayquery.keys");
        Assert.Null(SessionLog.ReadRayQueryKeys(keysFile));
        SessionLog.WriteRayQueryKeys(db, keysFile);
        var keys = SessionLog.ReadRayQueryKeys(keysFile)!;
        Assert.Equal([rq], keys);

        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, $"1.0,C,1,1,0.5,{rq},0.01\n2.0,C,1,1,20.0,{rq},0.01\n3.0,C,1,1,59.0,{rq},0.01\n"   // a hit, two at the floor
            + $"4.0,C,1,1,300.0,{rq},0.01\n5.0,C,1,1,20.0,{other},0.01\n6.0,C,1,1,20.0\n");                        // a miss, another PSO, an older proxy's row
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(6), 6, 0, 1, 3, 300.0, 2), SessionLog.Read(csv, rayQuery: keys).Last);
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(6), 6, 0, 1, 5, 300.0), SessionLog.Read(csv).Last);
        File.WriteAllText(csv, $"1.0,C,1,1,20.0,{rq},0.01\n2.0,C,1,1,8.0,{other},0.01\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(2), 2, 0, 0, 1, 8.0, 1), SessionLog.Read(csv, rayQuery: keys).Last);   // not the worst
    }

    /// <summary>The game's RayQuery keys (written by a compile) count apart on NVIDIA only; AMD keeps every create a hit or a compile.</summary>
    [Theory]
    [InlineData(GpuVendor.Nvidia, 0, 1)]
    [InlineData(GpuVendor.Amd, 1, 0)]
    public async Task RayQuery_creates_count_apart_on_NVIDIA(GpuVendor vendor, long compiles, long apart)
    {
        var key = new string('a', 40);
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = vendor }));
        Directory.CreateDirectory(k.Store.GameDir(_game.Id));
        File.WriteAllLines(Path.Combine(k.Store.GameDir(_game.Id), "rayquery.keys"), [key]);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"1.0,C,1,1,0.5,{new string('b', 40)},0.01\n2.0,C,1,1,20.0,{key},0.01\n");
        await k.ScanAsync(default);
        var s = k.Games.Single().LastSession!;
        Assert.Equal((compiles, apart), (s.Compiles, s.RayQueryRecompiles));
    }

    /// <summary>Each exit of the game shows its launch: the recorder appends a launch per start (no #end when the game
    /// is terminated), and the state raised on exit reads it.</summary>
    [Fact]
    public async Task The_last_session_follows_every_exit()
    {
        var k = Killer(new FakeReader(Unreal));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.RunningGameExes = () => running.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await k.ScanAsync(default);
        var changed = new List<GameState>();
        k.GameChanged += changed.Add;
        var csv = Path.Combine(_exeDir, "scskiller_creates.csv");
        foreach (var (compiles, at) in new[] { (7, 1_700_000_000_000L), (3, 1_700_000_100_000L), (0, 1_700_000_200_000L) })
        {
            running.Add(Path.GetFileName(_game.ExePath));
            k.PollGames();
            File.AppendAllText(csv, $"#session,{at},{Path.GetFileName(_game.ExePath)}\n" + string.Concat(Enumerable.Range(1, 10).Select(i => $"{i}.0,S,1,1,{(i <= compiles ? 25.0 : 0.5)}\n")));
            running.Clear();
            for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
            Assert.Equal(compiles, k.Games.Single().LastSession!.Compiles);
            Assert.Equal(compiles, changed[^1].LastSession!.Compiles);
        }
    }

    /// <summary>Two paths hold the same game's record: each save writes only what its holder changed, onto what the other
    /// saved; sets merge by what each added and removed. A record that wasn't loaded is written whole.</summary>
    [Fact]
    public void A_save_writes_only_what_its_holder_changed()
    {
        var store = new AppStore(Path.Combine(_root, "store"));
        var seed = store.LoadGame(_game.Id);
        (seed.CacheKeys, seed.RecordedSinceWarm) = (["old", "gone"], 3);
        store.SaveGame(_game.Id, seed);
        var compile = store.LoadGame(_game.Id);
        var exit = store.LoadGame(_game.Id);
        exit.LastPlay = new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(10));
        exit.CacheKeys.Add("game");
        store.SaveGame(_game.Id, exit);
        (compile.WarmedAt, compile.RecordedSinceWarm) = (DateTimeOffset.UnixEpoch.AddHours(1), 0);
        compile.CacheKeys.Remove("gone");
        compile.CacheKeys.Add("warm");
        store.SaveGame(_game.Id, compile);
        compile.CacheKeys.Add("later");   // a second save of the same record: only its change since the first
        store.SaveGame(_game.Id, compile);
        var r = store.LoadGame(_game.Id);
        Assert.Equal((exit.LastPlay, compile.WarmedAt, 0L), (r.LastPlay, r.WarmedAt, r.RecordedSinceWarm));
        Assert.Equal(["game", "later", "old", "warm"], r.CacheKeys.Order());

        store.SaveGame(_game.Id, new GameRecord { ShaderCount = 7 });
        Assert.Equal((7, null), (store.LoadGame(_game.Id).ShaderCount, store.LoadGame(_game.Id).LastPlay));
    }

    /// <summary>A compile loads the game's record, the game runs and exits while the compile indexes, and the compile's
    /// saves keep the exit's watched run.</summary>
    [Fact]
    public async Task A_compile_keeps_what_a_game_exit_saved_meanwhile()
    {
        var reader = new SlowIndexReader();
        var k = Killer(reader);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        (k.RunningGameExes, k.Clock) = (() => running.ToHashSet(StringComparer.OrdinalIgnoreCase), () => clock);
        await k.ScanAsync(default);
        void Poll() { clock = clock.AddSeconds(3); k.PollGames(); }
        Poll();
        k.Enqueue(_game.Id);
        k.StartQueue();
        Assert.True(reader.Indexing.Wait(TimeSpan.FromSeconds(10)));   // the compile holds the record it loaded
        running.Add(Path.GetFileName(_game.ExePath));
        for (int i = 0; i < 10; i++) Poll();
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) Poll();
        var played = k.Store.LoadGame(_game.Id).LastPlay;
        Assert.NotNull(played);
        reader.Go.Set();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.NotNull(rec.WarmedAt);
        Assert.Equal(played, rec.LastPlay);
    }

    sealed class SlowIndexReader : IEngineReader
    {
        public readonly ManualResetEventSlim Indexing = new(false), Go = new(false);
        public EngineInfo? Detect(Game game) => Unreal;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct)
        {
            Indexing.Set();
            Go.Wait(TimeSpan.FromSeconds(10));
            return new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        }
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>A launch without #end (the game terminated itself) lasts until the exit the watcher saw, when the run it
    /// saw from start to exit holds the launch's start; else until its last create. An #end always wins.</summary>
    [Fact]
    public void A_session_without_an_end_lasts_until_the_watched_exit()
    {
        const long t0 = 1_700_000_000_000;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(t0);
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, $"#session,{t0},Fake.exe\n1.0,S,1,1,0.5\n20000.0,S,1,1,25.0\n");
        TimeSpan Played(PlayWindow? w) => SessionLog.Read(csv, played: w).Last!.Duration;
        Assert.Equal(TimeSpan.FromSeconds(20), Played(null));
        Assert.Equal(TimeSpan.FromMinutes(10), Played(new(start.AddSeconds(-3), start.AddMinutes(10))));
        Assert.Equal(TimeSpan.FromSeconds(20), Played(new(start.AddSeconds(5), start.AddMinutes(10))));    // another run: started after this launch
        Assert.Equal(TimeSpan.FromSeconds(20), Played(new(start.AddHours(-2), start.AddHours(-1))));      // an older run
        File.AppendAllText(csv, $"#end,{t0 + 300_000}\n");
        Assert.Equal(TimeSpan.FromMinutes(5), Played(new(start.AddSeconds(-3), start.AddMinutes(10))));
    }

    [Fact]
    public async Task The_watcher_records_the_run_and_the_session_shows_its_length()
    {
        var k = Killer(new FakeReader(Unreal));
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        (k.RunningGameExes, k.Clock) = (() => running.ToHashSet(StringComparer.OrdinalIgnoreCase), () => clock);
        await k.ScanAsync(default);
        void Poll(int seconds) { clock = clock.AddSeconds(seconds); k.PollGames(); }
        Poll(3);   // not running
        running.Add(Path.GetFileName(_game.ExePath));
        Poll(3);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"#session,{clock.AddSeconds(2).ToUnixTimeMilliseconds()},Fake.exe\n1.0,S,1,1,0.5\n9000.0,S,1,1,25.0\n");
        for (int i = 0; i < 200; i++) Poll(3);   // 10 minutes of play
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) Poll(3);
        Assert.Equal(TimeSpan.FromSeconds(600 - 2), k.Games.Single().LastSession!.Duration);
        Assert.NotNull(k.Store.LoadGame(_game.Id).LastPlay);
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = null;
        k.Store.SaveGame(_game.Id, rec);

        // the app started while the game ran: that run's start is unknown, so its launch keeps the last create's time
        var late = Killer(new FakeReader(Unreal));
        (late.RunningGameExes, late.Clock) = (() => running.ToHashSet(StringComparer.OrdinalIgnoreCase), () => clock);
        await late.ScanAsync(default);
        running.Add(Path.GetFileName(_game.ExePath));
        late.PollGames();
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) late.PollGames();
        Assert.Equal(TimeSpan.FromSeconds(9), late.Games.Single().LastSession!.Duration);
    }

    /// <summary>A scan overlapping a game's exit: the exit's refresh reads the new launch while the scan is still busy with
    /// another game, and the scan's older state of this one must not replace it.</summary>
    [Fact]
    public async Task A_scan_never_replaces_a_newer_state()
    {
        var a = FakeGame("test:a", "A Game");
        var b = FakeGame("test:b", "B Game");
        var reader = new StallingReader(b.Id);
        var k = Killer(reader, games: [a, b]);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.RunningGameExes = () => running.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await k.ScanAsync(default);
        var csv = Path.Combine(Path.GetDirectoryName(a.ExePath)!, "scskiller_creates.csv");
        File.WriteAllText(csv, "#session,1700000000000,A.exe\n1.0,S,1,1,25.0\n");
        running.Add(Path.GetFileName(a.ExePath));
        k.PollGames();

        reader.Gate.Reset();
        var scan = k.RescanAsync(default);   // evaluates A (the old launch), then waits in B's detection
        Assert.True(reader.Waiting.Wait(TimeSpan.FromSeconds(10)));
        File.AppendAllText(csv, "#session,1700000100000,A.exe\n" + string.Concat(Enumerable.Range(1, 5).Select(i => $"{i}.0,S,1,1,25.0\n")));
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.Equal(5, k.Games.Single(s => s.Game.Id == a.Id).LastSession!.Compiles);
        reader.Gate.Set();
        var states = await scan;
        Assert.Equal(5, k.Games.Single(s => s.Game.Id == a.Id).LastSession!.Compiles);
        Assert.Equal(5, states.Single(s => s.Game.Id == a.Id).LastSession!.Compiles);
    }

    sealed class StallingReader(string blocked) : IEngineReader
    {
        public readonly ManualResetEventSlim Gate = new(true), Waiting = new(false);
        public EngineInfo? Detect(Game game)
        {
            if (game.Id == blocked && !Gate.IsSet) { Waiting.Set(); Gate.Wait(); }
            return Unreal;
        }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public void Scheduled_task_xml_has_logon_and_idle_triggers_and_runs_rewarm()
    {
        var xml = ScheduledTask.Xml(@"C:\Program Files\SCSKiller\scskiller.exe", @"PC\user");
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.NotNull(doc.Root!.Element(ns + "Triggers")!.Element(ns + "LogonTrigger"));
        Assert.NotNull(doc.Root.Element(ns + "Triggers")!.Element(ns + "IdleTrigger"));
        var exec = doc.Root.Element(ns + "Actions")!.Element(ns + "Exec")!;
        Assert.Equal(@"C:\Program Files\SCSKiller\scskiller.exe", exec.Element(ns + "Command")!.Value);
        Assert.Equal("rewarm-stale --if-driver-changed", exec.Element(ns + "Arguments")!.Value);
        Assert.Equal("LeastPrivilege", doc.Descendants(ns + "RunLevel").Single().Value);
    }

    [Fact]
    public void Session_log_reads_the_last_launched_exe_name_from_the_markers()
    {
        var csv = Path.Combine(_root, "creates.csv");
        File.WriteAllText(csv, "1.0,G,0,0,5.0\n");
        Assert.Null(SessionLog.Read(csv).Exe);   // an older proxy: no markers
        File.AppendAllText(csv, "#session,1700000000000,Fake-Win64-Shipping.exe\n2.0,G,0,0,5.0\n#end,1700000001000\n" +
                                "#session,1700000100000,FAKE,Win64.EXE\n#end,1700000101000\n");
        var (last, exe, _) = SessionLog.Read(csv);
        Assert.Equal(new LaunchedExe("FAKE,Win64.EXE", DateTimeOffset.FromUnixTimeMilliseconds(1700000100000)), exe);   // commas kept
        Assert.Equal(TimeSpan.FromSeconds(1), last!.Duration);
        Assert.Equal("Fake-Win64-Shipping.exe", SessionLog.Read(csv, "fake-win64-shipping.exe").Exe!.Name);   // the last of that exe
        Assert.Null(SessionLog.Read(csv, "Other.exe").Exe);
        Assert.Equal((null, null, null), SessionLog.Read(Path.Combine(_root, "missing.csv")));
    }

    /// <summary>A launch in the recorder's csv: its markers, cache hits (0.5 ms), compiles (25 ms), library loads and a ray
    /// tracing request (neither).</summary>
    static string Launch(long at, int hits, int compiles, string exe = "Fake-Win64-Shipping.exe", bool end = true, int library = 0)
    {
        var rows = Enumerable.Repeat("S,1,1,0.5", hits).Concat(Enumerable.Repeat("S,0,0,25.0", compiles)).Concat(Enumerable.Repeat("s,1,1,0.3", library)).Append("R,1,1,40.0");
        return $"#session,{at},{exe}\n" + string.Concat(rows.Select((r, i) => $"{i + 1}.0,{r}\n")) + (end ? $"#end,{at + 60_000}\n" : "");
    }

    [Fact]
    public void The_first_launch_after_a_warm_is_judged_by_its_hits_and_compiles()
    {
        var csv = Path.Combine(_root, "creates.csv");
        const long t0 = 1_700_000_000_000;
        var warmed = DateTimeOffset.FromUnixTimeMilliseconds(t0);
        LaunchCheck? First() => SessionLog.Read(csv, "fake-win64-shipping.exe", warmed, ScsKiller.MinJudgedCreates).First;
        File.WriteAllText(csv, Launch(t0 - 1000, 0, 500)                // before the warm
            + Launch(t0 + 1000, 10, 40)                                  // fewer creates than a judgement needs
            + Launch(t0 + 2000, 50, 50, exe: "Launcher.exe")             // another exe of the folder
            + Launch(t0 + 3000, 80, 20, library: 900)                    // the first launch: a fifth compiled
            + Launch(t0 + 4000, 0, 100));
        Assert.Equal(new LaunchCheck(DateTimeOffset.FromUnixTimeMilliseconds(t0 + 3000), 80, 20), First());
        Assert.False(ScsKiller.IsPartlyWarmed(First()));                 // exactly a fifth: warmed
        Assert.True(ScsKiller.IsPartlyWarmed(new LaunchCheck(warmed, 79, 21)));
        Assert.False(ScsKiller.IsPartlyWarmed(new LaunchCheck(warmed, 80, 20)));
        Assert.True(ScsKiller.IsPartlyWarmed(new LaunchCheck(warmed, 0, 100)));
        Assert.False(ScsKiller.IsPartlyWarmed((LaunchCheck?)null));
        Assert.Equal(0.2, First()!.Compiled, 9);

        File.WriteAllText(csv, Launch(t0 + 3000, 10, 190, end: false));   // still running (or crashed): not judged yet
        Assert.Null(First());
        File.AppendAllText(csv, Launch(t0 + 9000, 200, 0));              // the next launch ends it
        Assert.Equal(190, First()!.Compiles);
        Assert.Null(SessionLog.Read(csv, null, null).First);             // nothing asked
    }

    static byte[] SiblingsDb()   // two recorded PSOs of one shader set (another root signature each): two careful passes
    {
        var stages = new Dictionary<int, string> { [(int)Stage.Vertex] = $"{1:x40}" };
        var db = new MemoryStream();
        PsoDb.Write(db, 'S', PsoDb.Stream($"{8:x40}", stages, [], 3, [], 0));
        PsoDb.Write(db, 'S', PsoDb.Stream($"{9:x40}", stages, [], 3, [], 0));
        return db.ToArray();
    }

    static readonly PlanStats RecordedPlan = new(1000, 9000, 5, 7, true);

    [Fact]
    public async Task A_partly_warmed_game_on_AMD_offers_the_careful_compile_which_persists_and_runs_in_passes()
    {
        var amd = new FakeVendor(Gpu with { Vendor = GpuVendor.Amd });
        var warmer = new FakeWarmer();
        FakePlanner Planner() => new(stats: RecordedPlan, mainDb: SiblingsDb());
        var k = Killer(new FakeReader(Unreal), Planner(), warmer, vendor: amd);
        k.Settings = k.Settings with { Threads = 20 };
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        // the recorded PSOs at the careful rate, the rest at the game's measured fast rate (10000 in 10 s)
        var careful = TimeSpan.FromSeconds(1000 / ScsKiller.DefaultCarefulPsoPerSecond + 9000 / 1000.0);
        Assert.Equal((GameStatus.Warmed, "warmed for driver 100.01"), (s.Status, s.StatusReason));
        Assert.Equal(new CarefulCompile(false, null, careful, 1000), s.Careful);
        Assert.Equal((20, 0, 0), (warmer.Options!.Threads, warmer.Options.CarefulThreads, warmer.Passes));

        var warmedAt = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), Launch(warmedAt + 1, 100, 300) + Launch(warmedAt + 2, 400, 0));
        k.RefreshGame(_game.Id);
        s = k.Games.Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        Assert.True(ScsKiller.IsPartlyWarmed(s));
        Assert.Equal(0.75, s.Careful!.LaunchCompiled);
        Assert.Equal("partly warmed for driver 100.01: 75% of the 400 pipelines its first launch created still compiled; " +
                     $"a careful compile ({ScsKiller.AmdCarefulThreads} threads, in passes) reaches more of them, in about {ScsKiller.Duration(careful)}", s.StatusReason);
        Assert.Equal(TimeSpan.FromSeconds(10), s.EstimatedWarmTime);   // the fast compile's, measured

        k.SetCarefulCompile(_game.Id, true);
        await Task.Delay(20);   // the next warm ends after both launches
        warmer = new FakeWarmer();
        k = Killer(new FakeReader(Unreal), Planner(), warmer, vendor: amd);   // persisted
        k.Settings = k.Settings with { Threads = 20 };
        await k.ScanAsync(default);
        s = k.Games.Single();
        Assert.Equal((true, careful), (s.Careful!.On, s.EstimatedWarmTime));
        Assert.EndsWith(": the next compile is careful", s.StatusReason);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((20, ScsKiller.AmdCarefulThreads, 2), (warmer.Options!.Threads, warmer.Options.CarefulThreads, warmer.Passes));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.True(rec.WarmedCareful && rec.Careful);
        Assert.Null(rec.FirstLaunch);                                   // judged again by the next launch (the csv's are older)
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, false), (s.Status, ScsKiller.IsPartlyWarmed(s)));
        Assert.Equal(careful, s.EstimatedWarmTime);                      // a careful warm's rate doesn't replace the fast one

        File.AppendAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), Launch(DateTimeOffset.Now.AddMinutes(1).ToUnixTimeMilliseconds(), 50, 150));
        k.RefreshGame(_game.Id);
        Assert.EndsWith(", even after a careful compile", k.Games.Single().StatusReason);

        k.SetCarefulCompile(_game.Id, false);
        Assert.False(k.Store.LoadGame(_game.Id).Careful);
    }

    [Fact]
    public async Task Without_a_recording_the_careful_compile_has_nothing_to_do_and_says_so()
    {
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(), warmer, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        var rec = k.Store.LoadGame(_game.Id);
        rec.Careful = true;
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, warmer.Passes);   // no pass file: one ordinary warm
        var warmedAt = k.Store.LoadGame(_game.Id).WarmedAt!.Value.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), Launch(warmedAt + 1, 100, 300));
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal(0, s.Careful!.Recorded);
        Assert.Equal(s.EstimatedWarmTime, s.Careful.Estimate);          // nothing slower
        Assert.EndsWith("there is no recording yet (turn recording on and play)", s.StatusReason);
    }

    [Theory]
    [InlineData(GpuVendor.Amd, true, 20, 8, ScsKiller.AmdCarefulThreads, ScsKiller.AmdCarefulThreads, 2)]
    [InlineData(GpuVendor.Amd, true, 1, 8, 1, ScsKiller.AmdCarefulThreads, 2)]   // fewer threads asked: fewer
    [InlineData(GpuVendor.Amd, false, 20, 3, 0, 0, 0)]
    [InlineData(GpuVendor.Nvidia, true, 20, 3, 0, 0, 0)]    // a careful choice kept from an AMD GPU: ignored
    [InlineData(GpuVendor.Unknown, true, 20, 3, 0, 0, 0)]
    public async Task The_careful_thread_cap_and_passes_apply_on_AMD_only(GpuVendor vendor, bool careful, int threads, int background,
        int foregroundCareful, int backgroundCareful, int passes)
    {
        foreach (var (idle, full, capped) in new[] { (false, threads, foregroundCareful), (true, background, backgroundCareful) })
        {
            var warmer = new FakeWarmer();
            var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: RecordedPlan, mainDb: SiblingsDb()), warmer, vendor: new FakeVendor(Gpu with { Vendor = vendor }));
            k.Settings = k.Settings with { Threads = threads, BackgroundThreads = background };
            k.Background = idle;
            var rec = k.Store.LoadGame(_game.Id);
            rec.Careful = careful;
            k.Store.SaveGame(_game.Id, rec);
            await k.ScanAsync(default);
            Assert.Equal(vendor == GpuVendor.Amd, k.Games.Single().Careful != null);
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal((full, capped, passes), (warmer.Options!.Threads, warmer.Options.CarefulThreads, warmer.Passes));   // the fast items keep every thread
            if (vendor != GpuVendor.Amd) Assert.Throws<InvalidOperationException>(() => k.SetCarefulCompile(_game.Id, true));
        }
    }
    ScsKiller CaseKiller(GpuVendor vendor, FakeWarmer warmer) =>
        Killer(new FakeReader(Unreal), warmer: warmer, vendor: new FakeVendor(Gpu with { Vendor = vendor }));

    [Fact]
    public async Task A_slow_progress_listener_gets_the_latest_line_not_a_backlog()
    {
        // 2000 progress lines at once and a listener taking 100 ms a report: reporting each line would take 200 s
        var lines = string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"{{\"event\":\"progress\",\"done\":{i * 10},\"total\":20000,\"failed\":0,\"rate\":5620.0}}"))
                    + "\n{\"event\":\"done\",\"done\":20000,\"total\":20000,\"failed\":2,\"seconds\":9.5,\"stopped\":false}\n";
        var reports = new List<WarmProgress>();
        var clock = Stopwatch.StartNew();
        var (last, done, error) = await WarmOutput.Pump(new StringReader(lines), new SlowProgress(reports, 100), () => 0, TimeSpan.FromMilliseconds(50));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed}");
        Assert.InRange(reports.Count, 1, 20);
        Assert.Equal((20000L, 2L), (reports[^1].Done, reports[^1].Failed));   // the last line is always reported
        Assert.Equal(20000, done!.Done);
        Assert.Same(done, last);
        Assert.Null(error);
    }

    sealed class SlowProgress(List<WarmProgress> seen, int ms) : IProgress<WarmProgress>
    {
        public void Report(WarmProgress p) { Thread.Sleep(ms); lock (seen) seen.Add(p); }
    }

    [Fact]
    public void The_rate_is_the_recent_one_not_the_average_since_the_start()
    {
        var rate = new RecentRate();
        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromSeconds(4), 20_000);
        Assert.Null(rate.PerSecond);   // too short to say
        long done = 20_000;
        for (int s = 5; s <= 30; s++) rate.Add(TimeSpan.FromSeconds(s), done += 5000);   // the fast, cached start
        for (int s = 31; s <= 120; s++) rate.Add(TimeSpan.FromSeconds(s), done += 1000);  // then 1,000/s
        Assert.Equal(1000, rate.PerSecond!.Value, 1);   // the average since the start: ~2,000/s
        for (int s = 121; s <= 160; s++) rate.Add(TimeSpan.FromSeconds(s), done);          // stuck
        Assert.Equal(0, rate.PerSecond!.Value);
    }

    [Fact]
    public async Task Slow_cache_attribution_never_holds_up_the_warms_progress_and_a_stuck_warm_says_so()
    {
        var game = _game;
        // progress every 20 ms, stuck at 3,000 from line 30 on
        var warmer = new StreamingWarmer(60, TimeSpan.FromMilliseconds(20), i => Math.Min(i, 30) * 100);
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.AppCache = new SlowCache(TimeSpan.FromMilliseconds(400), "11111111");
        k.StallAfter = TimeSpan.FromMilliseconds(300);
        var notes = new List<string?>();
        k.QueueChanged += q => { if (q.Stage == QueueStage.Warming) lock (notes) notes.Add(q.Note); };
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.True(warmer.SlowestReport < TimeSpan.FromMilliseconds(100), $"a progress report took {warmer.SlowestReport}");
        Assert.Contains("11111111", k.Store.LoadGame(game.Id).CacheKeys);   // attribution still ran, on its own
        Assert.Contains(ScsKiller.StalledNote(TimeSpan.Zero), notes);        // "no progress for 1 min" once stuck
        Assert.Null(notes[0]);
        Assert.True(ScsKiller.Stalled(new QueueItem(game.Id, QueueStage.Warming, null, null, ScsKiller.StalledNote(TimeSpan.FromMinutes(3)))));
    }

    /// <summary>Reports <paramref name="lines"/> progress lines <paramref name="every"/> from its own thread, as the warm's
    /// stdout reader does, and times each report.</summary>
    sealed class StreamingWarmer(int lines, TimeSpan every, Func<int, long> doneAt) : IWarmer
    {
        public TimeSpan SlowestReport;
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress) => new Run(Task.Run(async () =>
        {
            for (int i = 1; i <= lines; i++)
            {
                var t = Stopwatch.StartNew();
                progress?.Report(new WarmProgress(doneAt(i), 10000, 0, 1000));
                if (t.Elapsed > SlowestReport) SlowestReport = t.Elapsed;
                await Task.Delay(every);
            }
            return new WarmResult(WarmOutcome.Completed, 10000, 10000, 0, TimeSpan.FromSeconds(10), 0, "", null);
        }));
        sealed class Run(Task<WarmResult> completion) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = completion;
            public void Pause() { }
            public void Resume() { }
            public void Stop() { }
        }
    }

    /// <summary>An attribution that takes as long as one over a huge driver cache.</summary>
    sealed class SlowCache(TimeSpan delay, string key) : IAppCache
    {
        public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys) => [];
        public long SizeOf(IEnumerable<string> keys) => 0;
        public IReadOnlySet<string> KeysOpenBy(string exeFileName) { Thread.Sleep(delay); return new HashSet<string> { key }; }
        public int Delete(IEnumerable<string> keys) => 0;
    }

    static async Task WarmOnce(ScsKiller k, string gameId)
    {
        k.Enqueue(gameId);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
    }

    void Marker(string exe, DateTimeOffset at) =>
        File.AppendAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), $"#session,{at.ToUnixTimeMilliseconds()},{exe}\n1.0,G,0,0,5.0\n#end,{at.ToUnixTimeMilliseconds() + 1000}\n");

    [Fact]
    public async Task Amd_warm_stages_the_exe_name_as_the_recorder_saw_the_game_launched()
    {
        var warmer = new FakeWarmer();
        var k = CaseKiller(GpuVendor.Amd, warmer);
        var t0 = DateTimeOffset.Now.AddHours(-1);
        Marker("FAKE-WIN64-SHIPPING.EXE", t0);                   // the launcher starts the game in upper case
        Marker("Launcher.exe", t0.AddMinutes(1));                // another exe in the folder: not this game's name
        await k.ScanAsync(default);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(("FAKE-WIN64-SHIPPING.EXE", t0.ToUnixTimeMilliseconds()), (rec.LaunchedExeName, rec.LaunchedExeSeenAt!.Value.ToUnixTimeMilliseconds()));
        Assert.Equal("FAKE-WIN64-SHIPPING.EXE", ScsKiller.WarmExeName(_game, rec));

        await WarmOnce(k, _game.Id);
        Assert.Equal(["FAKE-WIN64-SHIPPING.EXE"], warmer.Staged);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Equal("FAKE-WIN64-SHIPPING.EXE", k.Store.LoadGame(_game.Id).WarmedExeName);

        // a later launch in the file's own case: that cache is cold on AMD, so the game is stale and the next warm stages it
        rec = k.Store.LoadGame(_game.Id);
        rec.ResumeAt = 777;   // a stopped warm under the old name: its progress is in the other name's cache
        k.Store.SaveGame(_game.Id, rec);
        Marker("Fake-Win64-Shipping.exe", t0.AddMinutes(5));
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);
        Assert.Contains("runs as Fake-Win64-Shipping.exe", s.StatusReason);
        Assert.Equal(0, k.Store.LoadGame(_game.Id).ResumeAt);

        // an older sighting doesn't win over a newer one
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "");
        Marker("FAKE-WIN64-SHIPPING.EXE", t0.AddMinutes(1));
        await k.ScanAsync(default);
        Assert.Equal("Fake-Win64-Shipping.exe", k.Store.LoadGame(_game.Id).LaunchedExeName);

        await WarmOnce(k, _game.Id);
        Assert.Equal("Fake-Win64-Shipping.exe", warmer.Staged[^1]);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Nvidia_stages_the_launched_name_too_but_a_case_change_is_not_stale()
    {
        var warmer = new FakeWarmer();
        var k = CaseKiller(GpuVendor.Nvidia, warmer);
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        Assert.Equal(["Fake-Win64-Shipping.exe"], warmer.Staged);
        Marker("fake-win64-shipping.exe", DateTimeOffset.Now);   // NVIDIA's DXCache key ignores case: the same files
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Warmed, s.Status);
        await WarmOnce(k, _game.Id);
        Assert.Equal("fake-win64-shipping.exe", warmer.Staged[^1]);
    }

    [Fact]
    public async Task A_running_game_process_tells_the_launched_exe_name()
    {
        var warmer = new FakeWarmer();
        var k = CaseKiller(GpuVendor.Amd, warmer);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fake-win64-shipping.EXE" };   // the module path's case
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        await k.ScanAsync(default);   // first scan: the processes are checked once the game list is known
        Assert.Equal("fake-win64-shipping.EXE", k.Store.LoadGame(_game.Id).LaunchedExeName);

        lock (running) running.Clear();
        await WarmOnce(k, _game.Id);
        Assert.Equal(["fake-win64-shipping.EXE"], warmer.Staged);

        // the install's own case running (what a process we can't read reports) doesn't overwrite what was seen
        lock (running) running.Add("Fake-Win64-Shipping.exe");
        await k.ScanAsync(default);
        Assert.Equal("fake-win64-shipping.EXE", k.Store.LoadGame(_game.Id).LaunchedExeName);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public void Session_log_reads_markers_and_stays_compatible()
    {
        var csv = Path.Combine(_root, "creates.csv");
        // two marked launches: play time = #end - #session; other # lines are ignored
        File.WriteAllText(csv, "#session,1700000000000,Fake-Win64-Shipping.exe\n10.0,G,0,0,50.0\n#end,1700000060000\n" +
                               "#session,1700000100000,Fake-Win64-Shipping.exe\n5.0,G,0,0,4.0\n#flush,1700000200000\n6.0,c,0,0,0.1\n7.0,C,0,0,1.0\n#end,1700000400000\n");
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(300), 3, 1, 1, 1, 4.0), SessionLog.Read(csv).Last);
        // crash: no #end -> the last create's t_ms
        File.AppendAllText(csv, "#session,1700000500000,Fake-Win64-Shipping.exe\n2.0,G,0,0,8.0\n9.5,S,0,0,0.5\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(9.5), 2, 0, 1, 1, 8.0), SessionLog.Read(csv).Last);
        // a launch that created nothing still counts, with its marker time
        File.AppendAllText(csv, "#session,1700001000000,Fake-Win64-Shipping.exe\n#end,1700001002000\n");
        Assert.Equal(new SessionStats(TimeSpan.FromSeconds(2), 0, 0, 0, 0, 0), SessionLog.Read(csv).Last);
        // an older proxy appending unmarked rows: t_ms restarting still starts a new launch
        File.AppendAllText(csv, "1.0,G,0,0,20.0\n3.0,G,0,0,5.0\n");
        Assert.Equal(new SessionStats(TimeSpan.FromMilliseconds(3), 2, 0, 0, 2, 20.0), SessionLog.Read(csv).Last);
    }

    /// <summary>Sets the settings and waits for the games' re-evaluation (GameChanged: one game).</summary>
    static async Task<int> SetSettings(ScsKiller k, Settings s)
    {
        var changed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(GameState _) => changed.TrySetResult(Environment.CurrentManagedThreadId);
        k.GameChanged += On;
        try
        {
            k.Settings = s;
            return await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { k.GameChanged -= On; }
    }

    // The GUI sets Settings and runs ScanAsync on its UI thread: neither may evaluate games (disk IO per game) there.
    [Fact]
    public async Task Scan_and_a_settings_change_evaluate_games_off_the_callers_thread()
    {
        var gate = new ManualResetEventSlim();
        int detectThread = 0;
        var k = Killer(new GatedReader(Unreal, gate, id => detectThread = id));
        int caller = Environment.CurrentManagedThreadId;
        var scan = k.ScanAsync(default);
        Assert.False(scan.IsCompleted);   // returned while detection waits
        gate.Set();
        await scan;
        Assert.NotEqual(caller, detectThread);

        caller = Environment.CurrentManagedThreadId;
        Assert.NotEqual(caller, await SetSettings(k, k.Settings with { MaximumPlans = !k.Settings.MaximumPlans }));
    }

    sealed class GatedReader(EngineInfo engine, ManualResetEventSlim gate, Action<int> onDetect) : IEngineReader
    {
        public EngineInfo? Detect(Game game) { onDetect(Environment.CurrentManagedThreadId); gate.Wait(TimeSpan.FromSeconds(10)); return engine; }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    // A newer planner (Planner.Version) re-warms a game only when its rebuilt plan has records the warm didn't replay.
    // The bump is simulated by marking the record's plan and warm as built by the version before.
    async Task<(ScsKiller K, FakeWarmer Warmer, List<PsoDb.Rec> Records)> WarmedWithPlan()
    {
        List<PsoDb.Rec> records = [new('B', [9]), new('P', [1]), new('P', [2]), new('C', [3])];
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), new FakePlanner(records: records), warmer);
        k.IdleTime = () => TimeSpan.FromHours(1);   // plan checks are "when idle" items
        await k.ScanAsync(default);
        await Compile(k);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        return (k, warmer, records);
    }

    async Task Compile(ScsKiller k)
    {
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
    }

    void OlderPlanner(ScsKiller k, bool fingerprint = true)
    {
        var rec = k.Store.LoadGame(_game.Id);
        (rec.PlanVersion, rec.WarmedPlanVersion) = (Planner.Version - 1, Planner.Version - 1);
        if (!fingerprint) (rec.PlanItems, rec.WarmedPlanItems) = (null, null);   // a record from before the fingerprint
        k.Store.SaveGame(_game.Id, rec);
    }

    [Fact]
    public async Task A_newer_planner_with_the_same_plan_is_not_stale_and_is_not_rewarmed()
    {
        var (k, warmer, records) = await WarmedWithPlan();
        Assert.NotNull(k.Store.LoadGame(_game.Id).WarmedPlanItems);
        OlderPlanner(k);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);   // not rebuilt yet: it may compile more
        Assert.Equal("SCSKiller can now compile more of this game", s.StatusReason);

        records.Reverse();   // the same records in another order: the same plan
        k.CheckPlans = true;   // the app: a "when idle" plan rebuild, no warm
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Done, "already compiled: the new plan adds nothing"), (k.Queue.Single().Stage, k.Queue.Single().Note));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Single(warmer.Started);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((Planner.Version, rec.PlanItems), (rec.WarmedPlanVersion, rec.WarmedPlanItems));

        records.RemoveAt(0);   // fewer records (a subset) is nothing new either
        OlderPlanner(k);
        await Compile(k);   // the user's compile of the stale game: rebuilds the plan, no warm
        Assert.Equal("already compiled: the new plan adds nothing", k.Queue.Single().Note);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Single(warmer.Started);
    }

    [Fact]
    public async Task A_newer_planner_with_new_pipelines_is_stale_with_their_count()
    {
        var (k, warmer, records) = await WarmedWithPlan();
        records.AddRange([new('P', [4]), new('Y', [5]), new('B', [6])]);   // a root signature alone compiles nothing
        OlderPlanner(k);
        k.CheckPlans = true;
        await k.ScanAsync(default);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, "SCSKiller can now compile 2 more pipelines for this game", 2L), (s.Status, s.StatusReason, s.NewPipelines));
        Assert.Equal(s.StatusReason, k.Queue.Single().Note);
        Assert.True(k.Queue.Single().PlanCheck);   // finished, still left out of the lists
        Assert.Null(ScsKiller.PlanCheckLine(k.Queue));
        Assert.Single(warmer.Started);   // the plan check doesn't warm
        Assert.Single(k.StaleGames());   // the Library's "Needs rebuilding" and the driver-update re-warm read the same

        await Compile(k);
        Assert.Equal(2, warmer.Started.Count);
        Assert.False(k.Queue.Single().PlanCheck);
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, (long?)null), (s.Status, s.NewPipelines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_plan_check_is_counted_apart_until_the_game_is_queued_to_compile(bool driverUpdate)
    {
        var (k, warmer, records) = await WarmedWithPlan();
        records.Add(new('P', [4]));
        OlderPlanner(k);
        k.IdleTime = () => TimeSpan.Zero;   // the user is at the PC: the check waits
        k.CheckPlans = true;
        await k.ScanAsync(default);
        Assert.True(k.Queue.Single().PlanCheck);
        Assert.Equal("Checking 1 game for more to compile (while idle)", ScsKiller.PlanCheckLine(k.Queue));

        if (driverUpdate) k.EnqueueWhenIdle(_game.Id);   // OnDriverUpdate's re-warm
        else { k.Enqueue(_game.Id); k.StartQueue(); }
        Assert.False(k.Queue.Single().PlanCheck);
        Assert.Equal(driverUpdate ? "starts when the PC is idle" : null, k.Queue.Single().Note);
        Assert.Null(ScsKiller.PlanCheckLine(k.Queue));
        k.IdleTime = () => TimeSpan.FromHours(1);
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((QueueStage.Done, false), (k.Queue.Single().Stage, k.Queue.Single().PlanCheck));
        Assert.Equal(2, warmer.Started.Count);   // compiled, not only planned
    }

    [Fact]
    public async Task Queue_positions_leave_plan_checks_out()
    {
        var k = Killer(new FakeReader(Unreal), games: Three());
        await k.ScanAsync(default);
        await Compile(k);
        OlderPlanner(k);
        k.IdleTime = () => TimeSpan.Zero;
        k.CheckPlans = true;
        await k.ScanAsync(default);
        k.Enqueue("test:b");
        k.Enqueue("test:c");
        Assert.Equal([true, false, false], k.Queue.Select(q => q.PlanCheck));

        k.MoveInQueue("test:c", 0);
        Assert.Equal(["test:fake", "test:c", "test:b"], k.Queue.Select(q => q.GameId));
        k.MoveInQueue("test:c", 1);   // the queue page's "move down": below b, wherever the check is
        Assert.Equal(["test:fake", "test:b", "test:c"], k.Queue.Select(q => q.GameId));
        foreach (var q in k.Queue) k.Remove(q.GameId);
    }

    [Fact]
    public async Task The_driver_notification_is_only_about_games_a_driver_made_stale()
    {
        var (k, _, _) = await WarmedWithPlan();
        OlderPlanner(k);   // stale only because SCSKiller plans more: its idle plan check handles it, no notification
        await k.ScanAsync(default);
        Assert.Equal(DriverUpdateMode.Ask, k.Settings.OnDriverUpdate);
        Assert.Single(k.StaleGames());
        Assert.Empty(k.DriverStaleGames());
        Assert.False(k.ShouldNotifyStale());

        var after = Killer(new FakeReader(Unreal), driver: "101.00");   // the same game, now also a new driver
        await after.ScanAsync(default);
        Assert.Contains("100.01 -> 101.00", Assert.Single(after.DriverStaleGames()).StatusReason);
        Assert.True(after.ShouldNotifyStale());
    }

    [Fact]
    public async Task An_older_record_takes_its_fingerprint_from_the_plan_its_warm_replayed()
    {
        var (k, warmer, _) = await WarmedWithPlan();
        OlderPlanner(k, fingerprint: false);
        await Compile(k);
        Assert.Equal("already compiled: the new plan adds nothing", k.Queue.Single().Note);
        Assert.Single(warmer.Started);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.NotNull(rec.WarmedPlanItems);
        Assert.Equal((Planner.Version, rec.PlanItems), (rec.WarmedPlanVersion, rec.WarmedPlanItems));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        // plan.bin built by another planner than the warmed one: not the warmed plan, so it may compile more (re-warmed)
        OlderPlanner(k, fingerprint: false);
        rec = k.Store.LoadGame(_game.Id);
        rec.PlanVersion = Planner.Version - 2;
        k.Store.SaveGame(_game.Id, rec);
        await Compile(k);
        Assert.Equal(2, warmer.Started.Count);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Maximum_mode_rewarms_only_a_per_stage_warm()
    {
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { DriverVersion = "100.01" }, perStage: true));
        async Task Compile()
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        }
        await k.ScanAsync(default);
        await Compile();
        Assert.True(k.Store.LoadGame(_game.Id).WarmedPerStage);

        await SetSettings(k, k.Settings with { MaximumPlans = true });   // every pairing is new work
        Assert.Equal(GameStatus.Stale, k.Games.Single().Status);
        Assert.Contains("Maximum mode", k.Games.Single().StatusReason);
        await Compile();
        var rec = k.Store.LoadGame(_game.Id);
        Assert.False(rec.PlanPerStage || rec.WarmedPerStage);

        await SetSettings(k, k.Settings with { MaximumPlans = false });  // a pairing warm already holds every stage unit
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        await Compile();   // the plan is rebuilt per-stage
        Assert.True(k.Store.LoadGame(_game.Id).PlanPerStage);
    }

    [Fact]
    public async Task Middleware_next_to_the_exe_is_listed_by_its_player_name()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, "libxess.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "nvngx_dlss.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "unrelated.dll"), []);
        var s = (await Killer(new FakeReader(Unreal)).ScanAsync(default)).Single();
        Assert.Equal(["DLSS", "XeSS"], s.Middleware!.Select(t => t.Label).Order());
        Assert.All(s.Middleware!, t => Assert.Equal(0, t.Pipelines));   // no packs: detected only

        // OptiScaler folds what it bundles (XeSS, FSR4, FidelityFX); DirectStorage (no pack) is hidden; DLSS stays
        File.WriteAllBytes(Path.Combine(_exeDir, "OptiScaler.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "amdxcffx64.dll"), []);
        File.WriteAllBytes(Path.Combine(_exeDir, "dstoragecore.dll"), []);
        var packs = Path.Combine(_root, "packs");
        var planner = new Planner(packs);
        s = (await Killer(new FakeReader(Unreal), planner).ScanAsync(default)).Single();
        Assert.Equal(["DLSS", "OptiScaler"], s.Middleware!.Select(t => t.Label).Order());
        var opti = s.Middleware!.Single(t => t.Label == "OptiScaler");
        Assert.Equal(["OptiScaler.dll", "amdxcffx64.dll", "libxess.dll"], opti.Dlls.Order(StringComparer.Ordinal));
        Assert.Equal(0, opti.Pipelines);

        // a pack for this FSR4 DLL version: it heads the OptiScaler tag, with its pipelines
        var fsr4 = Middleware.Detect(_exeDir).Single(d => d.Name == "amdxcffx64.dll");
        var pack = new MiddlewarePack(fsr4.Vendor, fsr4.Name, Middleware.Scan(fsr4.Path).ContentHash, 0);
        pack.Add(new PsoDb.Rec('C', [1, 2, 3]), "test");
        pack.Add(new PsoDb.Rec('C', [4, 5, 6]), "test");
        pack.Write(planner.Packs!.PathOf(fsr4.Vendor, fsr4.Name, pack.Header.ContentHash));
        s = (await Killer(new FakeReader(Unreal), planner).ScanAsync(default)).Single();
        Assert.Equal(("OptiScaler (FSR4)", 2), (s.Middleware![0].Label, s.Middleware[0].Pipelines));
        Assert.Equal(0, s.Middleware!.Single(t => t.Label == "DLSS").Pipelines);
    }

    [Fact]
    public void Concurrent_saves_and_loads_of_one_file_never_fail()
    {
        var store = new AppStore(Path.Combine(_root, "data"));
        var scan = new Dictionary<string, Evaluation> { ["g"] = new("k", null, AntiCheat.None, new PlanCheck(Readiness.Ready, "r")) };
        Parallel.For(0, 400, i => { if (i % 2 == 0) store.SaveScan(scan); else store.LoadScan(); });   // the app + the CLI at once
        Assert.Equal("k", store.LoadScan()["g"].Key);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "data"), "*.tmp"));
    }

    [Fact]
    public async Task Store_version_decides_staleness_with_the_exe_stamp_as_fallback()
    {
        var exe = _game.ExePath;
        await Warmed(_game with { Version = "100" });
        Assert.Equal("100", Killer().Store.LoadGame(_game.Id).WarmedGameVersion);

        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(5));   // e.g. Steam re-verified the files: same build
        var k = Killer(new FakeReader(Unreal), game: _game with { Version = "100" });
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);

        k = Killer(new FakeReader(Unreal), game: _game with { Version = "101" });
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);
        Assert.Contains("build 100 -> 101", s.StatusReason);

        // no store version (other stores, or state from before versions): the exe stamp decides
        k = Killer(new FakeReader(Unreal));
        Assert.Equal(GameStatus.Stale, (await k.ScanAsync(default)).Single().Status);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.WarmedGameVersion, rec.WarmedExeStamp) = (null, $"{new FileInfo(exe).Length}:{new FileInfo(exe).LastWriteTimeUtc.Ticks}");
        k.Store.SaveGame(_game.Id, rec);
        k = Killer(new FakeReader(Unreal), game: _game with { Version = "101" });
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);
    }

    [Fact]
    public async Task A_compile_after_an_update_the_scan_missed_records_the_installed_build()
    {
        await Warmed(_game with { Version = "100" });
        var source = new FakeSource([_game with { Version = "100" }]);
        var k = new ScsKiller([source], new FakeVendor(Gpu with { DriverVersion = "100.01" }), new FakeReader(Unreal), new FakePlanner(),
            new FakeWarmer(), Path.Combine(_root, "data"), _proxy) { LocalAppData = _root };
        await k.ScanAsync(default);
        source.Games = [_game with { Version = "101" }];   // the store updated the game while the app ran
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal(("101", "101"), (rec.IndexGameVersion, rec.WarmedGameVersion));
        Assert.Equal(("101", GameStatus.Warmed), (k.Games.Single().Game.Version, k.Games.Single().Status));
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);
    }

    [Fact]
    public async Task Scan_reuses_detection_until_the_game_changes_and_rescan_forces_it()
    {
        var reader = new FakeReader(Unreal);
        await Killer(reader).ScanAsync(default);
        Assert.Equal(1, reader.Detects);
        var s = (await Killer(reader).ScanAsync(default)).Single();   // the next app start
        Assert.Equal(1, reader.Detects);
        Assert.Equal((Unreal, GameStatus.Ready, AntiCheat.None), (s.Engine, s.Status, s.AntiCheat));

        var patched = _game with { Version = "7" };
        await Killer(reader, game: patched).ScanAsync(default);
        Assert.Equal(2, reader.Detects);
        await Killer(reader, game: patched).ScanAsync(default);
        Assert.Equal(2, reader.Detects);
        File.SetLastWriteTimeUtc(_game.ExePath, DateTime.UtcNow.AddMinutes(1));   // exe replaced
        await Killer(reader, game: patched).ScanAsync(default);
        Assert.Equal(3, reader.Detects);
        await Killer(reader, game: patched).RescanAsync(default);
        Assert.Equal(4, reader.Detects);
    }

    [Fact]
    public async Task Dismissed_stale_games_stay_listed_but_are_not_notified_until_the_driver_changes()
    {
        await Warmed();
        var k = Killer(new FakeReader(Unreal), driver: "101.00");
        await k.ScanAsync(default);
        Assert.True(k.ShouldNotifyStale());
        k.DismissStale();
        Assert.False(k.ShouldNotifyStale());

        k = Killer(new FakeReader(Unreal), driver: "101.00");   // persisted across restarts
        await k.ScanAsync(default);
        Assert.Single(k.StaleGames());
        Assert.False(k.ShouldNotifyStale());

        k = Killer(new FakeReader(Unreal), driver: "102.00");
        await k.ScanAsync(default);
        Assert.True(k.ShouldNotifyStale());
        k.Settings = k.Settings with { OnDriverUpdate = DriverUpdateMode.WhenIdle };   // only Ask notifies
        Assert.False(k.ShouldNotifyStale());
    }

    [Fact]
    public async Task When_idle_item_waits_for_idle_pauses_on_input_and_runs_as_background()
    {
        var warmer = new ControlledWarmer();
        var idle = TimeSpan.Zero;
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.IdleTime = () => idle;
        k.Settings = k.Settings with { Threads = 20, BackgroundThreads = 3 };
        await k.ScanAsync(default);

        k.EnqueueWhenIdle(_game.Id);
        Assert.Equal((QueueStage.Waiting, "starts when the PC is idle"), (k.Queue.Single().Stage, k.Queue.Single().Note));
        await Task.Delay(1200);
        Assert.Null(warmer.Run);                                    // the user is at the PC

        idle = TimeSpan.FromMinutes(3);
        await Until(() => warmer.Run != null);
        Assert.Equal(new WarmOptions(3, WarmPriority.Idle, 0, ScsKiller.AutoCompileMemoryGB() * 1024), warmer.Options);   // Auto memory budget

        await Until(() => k.Queue.Single().Stage == QueueStage.Warming);
        idle = TimeSpan.FromSeconds(1);                             // input
        var t = Stopwatch.StartNew();
        await Until(() => warmer.Run!.Paused);
        Assert.True(t.Elapsed < TimeSpan.FromSeconds(1.5), $"paused after {t.Elapsed}");   // polled every 0.5 s
        await Until(() => k.Queue.Single().Stage == QueueStage.Paused);
        Assert.Equal("paused until the PC is idle", k.Queue.Single().Note);

        idle = TimeSpan.FromMinutes(3);
        await Until(() => !warmer.Run!.Paused);
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Enqueue_makes_a_waiting_when_idle_item_a_normal_one_that_runs_with_the_queue_in_the_foreground()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        k.IdleTime = () => TimeSpan.Zero;
        k.Settings = k.Settings with { Threads = 20, BackgroundThreads = 3, MaxCompileMemoryGB = 6 };
        await k.ScanAsync(default);
        k.EnqueueWhenIdle(_game.Id);
        await Task.Delay(700);
        Assert.Null(warmer.Run);
        k.Enqueue(_game.Id);
        Assert.Null(k.Queue.Single().Note);
        await Task.Delay(700);
        Assert.Null(warmer.Run);                                    // not started: the queue isn't running
        k.StartQueue();
        await Until(() => warmer.Run != null);
        Assert.Equal(new WarmOptions(20, WarmPriority.BelowNormal, 0, 6144), warmer.Options);   // the set memory budget
        await Task.Delay(700);
        Assert.False(warmer.Run!.Paused);                           // no longer waits for idle
        warmer.Run.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Published_layout_resolves_the_task_exe_and_the_app()
    {
        var dist = Path.Combine(_root, "dist");
        var cli = Path.Combine(dist, "cli");
        Directory.CreateDirectory(cli);
        File.WriteAllText(Path.Combine(dist, "SCSKiller.exe"), "");
        File.WriteAllText(Path.Combine(cli, "scskiller.exe"), "");
        Assert.Equal(Path.Combine(cli, "scskiller.exe"), ScheduledTask.TaskExe(dist));   // called from the app
        Assert.Equal(Path.Combine(cli, "scskiller.exe"), ScheduledTask.TaskExe(cli));    // called from the CLI
        File.WriteAllText(Path.Combine(cli, "scskillerw.exe"), "");
        Assert.Equal(Path.Combine(cli, "scskillerw.exe"), ScheduledTask.TaskExe(dist));  // windowless copy preferred
        Assert.Equal(Path.Combine(dist, "SCSKiller.exe"), ScheduledTask.AppExe(cli));
        Assert.Null(ScheduledTask.AppExe(dist));
        Assert.Equal("--driver-updated", ScheduledTask.DriverUpdatedArg);
    }

    Game[] Three() => [_game, _game with { Id = "test:b", Name = "B" }, _game with { Id = "test:c", Name = "C" }];

    [Fact]
    public async Task Enqueue_waits_for_StartQueue_which_runs_the_items_in_order_and_then_stops()
    {
        var warmer = new FakeWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: Three());
        await k.ScanAsync(default);
        foreach (var id in new[] { "test:fake", "test:b", "test:c" }) k.Enqueue(id);
        int events = 0;
        k.QueueChanged += _ => Interlocked.Increment(ref events);
        await Task.Delay(700);
        Assert.Empty(warmer.Started);
        Assert.False(k.QueueRunning);

        k.MoveInQueue("test:c", 0);
        k.MoveInQueue("test:fake", 99);   // clamped: last
        Assert.Equal(["test:c", "test:b", "test:fake"], k.Queue.Select(q => q.GameId));
        Assert.Equal(2, events);

        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["test:c", "test:b", "test:fake"], warmer.Started);
        Assert.False(k.QueueRunning);
        Assert.All(k.Queue, q => Assert.Equal(QueueStage.Done, q.Stage));   // finished items stay listed...
        k.Enqueue("test:b");                                                 // (a finished game queued again waits before them)
        Assert.Equal(("test:b", QueueStage.Waiting), (k.Queue[0].GameId, k.Queue[0].Stage));
        k.StartQueue();                                                      // ...until the next start clears them
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("test:b", QueueStage.Done), (k.Queue.Single().GameId, k.Queue.Single().Stage));
    }

    [Fact]
    public async Task Stop_halts_the_queue_and_the_next_start_continues_the_stopped_item_first()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: Three());
        await k.ScanAsync(default);
        k.Enqueue("test:fake");
        k.Enqueue("test:b");
        k.StartQueue();
        Assert.True(k.QueueRunning);
        await Until(() => warmer.Run != null);
        k.Enqueue("test:c");
        k.MoveInQueue("test:c", 0);
        Assert.Equal(["test:fake", "test:c", "test:b"], k.Queue.Select(q => q.GameId));   // the running item stays first

        k.StopQueue();
        await Until(() => k.Queue.Any(q => q.Stage == QueueStage.Stopped));
        Assert.False(k.QueueRunning);
        await Task.Delay(700);
        Assert.Equal(["test:fake"], warmer.Started);                                         // nothing else started
        Assert.Equal(["test:c", "test:b", "test:fake"], k.Queue.Select(q => q.GameId));   // waiting, then finished

        k.StartQueue();
        await Until(() => warmer.Started.Count == 2);
        Assert.Equal(["test:fake", "test:c", "test:b"], k.Queue.Select(q => q.GameId));   // the stopped one continues first
        Assert.Equal(10, warmer.Options!.StartAt);
        warmer.Run!.Finish();
        foreach (var n in new[] { 3, 4 })
        {
            await Until(() => warmer.Started.Count == n);
            warmer.Run!.Finish();
        }
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["test:fake", "test:fake", "test:c", "test:b"], warmer.Started);
        Assert.False(k.QueueRunning);
    }

    string CacheFile(string name, int size)
    {
        var path = Path.Combine(_root, "DXCache", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    [Fact]
    public async Task A_warm_attributes_the_cache_files_a_process_named_like_the_game_holds_open()
    {
        CacheFile("0002a91d99999999.nvph", 4096);   // an app from before
        var warmer = new FakeWarmer(() =>
        {
            CacheFile("0002a91d11111111.nvph", 65536);
            var held = new FileStream(CacheFile("fc52a91d11111111.nvph", 4096), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            CacheFile("fc52a91d22222222.nvph", 4096);   // another app starting meanwhile: new, but not open in the game's process
            return held;
        });
        // the game is named like this test process, which holds the file the way the driver does in the staged warm
        var game = _game with { ExePath = Path.Combine(_exeDir, Process.GetCurrentProcess().ProcessName + ".exe") };
        var k = Killer(new FakeReader(Unreal), warmer: warmer, game: game);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (k.AppCache, k.RunningGameExes) = (new NvidiaAppCache(Path.Combine(_root, "DXCache")), () => running);   // this process plays the staged warm, not the game
        await k.ScanAsync(default);
        Assert.Null(k.Games.Single().CacheOnDisk);

        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["11111111"], k.Store.LoadGame(game.Id).CacheKeys);
        Assert.Equal(65536 + 4096, k.Games.Single().CacheOnDisk);

        // a process named like the game runs (this one): the game or a warm of it, refused either way
        Assert.Equal("Fake Game is running", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(game.Id)).Message);
        Assert.True(File.Exists(Path.Combine(_root, "DXCache", "0002a91d11111111.nvph")));
    }

    /// <summary>An Xbox game on NVIDIA: a warm outside its package identity filled another key than the one the game opens.</summary>
    [Fact]
    public async Task A_game_played_on_other_keys_than_its_warm_filled_is_not_warmed_until_a_warm_fills_its_own()
    {
        var warmKey = "ad873243";
        var warmer = new FakeWarmer(() => new FileStream(CacheFile($"0002a91d{warmKey}.nvph", 65536), FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
        // the game is named like this test process, which holds the files the way the driver does
        var game = _game with { ExePath = Path.Combine(_exeDir, Process.GetCurrentProcess().ProcessName + ".exe") };
        var k = Killer(new FakeReader(Unreal), warmer: warmer, game: game);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (k.AppCache, k.RunningGameExes) = (new NvidiaAppCache(Path.Combine(_root, "DXCache")), () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); });
        await k.ScanAsync(default);
        async Task Warm()
        {
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Warm();
        Assert.Equal((GameStatus.Warmed, "ad873243"), (k.Games.Single().Status, string.Join(",", k.Store.LoadGame(game.Id).WarmedKeys!)));

        using (new FileStream(CacheFile("0002a91d7f303a45.nvph", 4096), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            lock (running) running.Add(Path.GetFileName(game.ExePath));
            k.PollGames();
            await Until(() => k.Store.LoadGame(game.Id).GameKeys.Count > 0);
            lock (running) running.Clear();
        }
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        var rec = k.Store.LoadGame(game.Id);
        Assert.Equal(["7f303a45"], rec.GameKeys);
        Assert.Equal(["7f303a45", "ad873243"], rec.CacheKeys.Order());   // Clear cache deletes both
        Assert.Equal((GameStatus.Stale, ScsKiller.MissesGameReason), (k.Games.Single().Status, k.Games.Single().StatusReason));

        warmKey = "7f303a45";   // a warm with the game's package identity
        await Warm();
        Assert.Equal((GameStatus.Warmed, "7f303a45"), (k.Games.Single().Status, string.Join(",", k.Store.LoadGame(game.Id).WarmedKeys!)));
    }

    [Fact]
    public void A_warm_misses_the_game_only_when_the_games_own_keys_are_known_and_it_filled_none_of_them()
    {
        static GameRecord R(string[] cache, string[] game, string[]? warmed) => new() { CacheKeys = [.. cache], GameKeys = [.. game], WarmedKeys = warmed?.ToHashSet() };
        Assert.False(ScsKiller.WarmMissesGame(R(["a"], [], ["a"])));             // the game not seen yet
        Assert.True(ScsKiller.WarmMissesGame(R(["a", "g"], ["g"], ["a"])));
        Assert.False(ScsKiller.WarmMissesGame(R(["a", "g"], ["g"], ["g"])));
        Assert.False(ScsKiller.WarmMissesGame(R(["g"], ["g"], [])));             // the warm's keys weren't seen: unknown, not a miss
        Assert.True(ScsKiller.WarmMissesGame(R(["a", "g"], ["g"], null)));      // a record from before WarmedKeys: its warm keys are the rest
        Assert.False(ScsKiller.WarmMissesGame(R(["g"], ["g"], null)));

    }

    /// <summary>The four AMD games whose keys were handle-verified (ARCHITECTURE.md): a warm registers an AGS app name only
    /// where that is proven to reach the game's own key, else it is plain (the exe name's key, profiles included).</summary>
    [Theory]
    //          exe                            app         game's own keys  missed  AGS used
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "",              false,  true)]    // a measured app name
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "",              true,   false)]   // a launch showed a miss
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "dxc:dc72f790",  true,   true)]    // seen holding it
    [InlineData("Townfall-Win64-Shipping.exe", "Townfall", "dxc:12bba8ae",  false,  false)]   // seen holding the plain key
    [InlineData("Wonderlands.exe",             "OakGame",  "",              false,  false)]   // OakGame's profile key f2f80824 isn't the game's
    [InlineData("Wonderlands.exe",             "OakGame",  "dxc:85c2b2e5",  false,  false)]
    [InlineData("HogwartsLegacy.exe",          "Phoenix",  "",              false,  false)]   // plain: the stage path reaches d32786a7
    [InlineData("HogwartsLegacy.exe",          "Phoenix",  "dxc:d32786a7",  false,  true)]    // Phoenix's profile is the game's key
    [InlineData("ff7rebirth_.exe",             "End",      "",              false,  false)]
    [InlineData("ff7rebirth_.exe",             "End",      "dxc:6b2fcd83",  false,  false)]   // the exe name's profile: plain
    [InlineData("Unknown-Win64-Shipping.exe",  "Unknown",  "",              false,  false)]   // not proven
    public void Amd_a_warm_registers_the_AGS_app_name_only_where_proven(string exe, string app, string gameKey, bool missed, bool used)
    {
        var r = new GameRecord { GameKeys = gameKey.Length > 0 ? [gameKey] : [], AgsMissed = missed };
        var reg = new AgsRegistration(app, "UnrealEngine5.6");
        Assert.Equal(used ? reg : null, ScsKiller.AgsFor(reg, exe, r));
        Assert.Null(ScsKiller.AgsFor(null, exe, r));
    }

    [Fact]
    public void A_launch_that_still_compiled_most_pipelines_after_an_AGS_warm_is_a_miss()
    {
        var at = DateTimeOffset.Now;
        GameRecord R(string? app, long hits, long compiles, params string[] game) =>
            new() { WarmedAgsApp = app, FirstLaunch = new LaunchCheck(at, hits, compiles), GameKeys = [.. game] };
        Assert.True(ScsKiller.AgsLaunchMissed(R("OakGame", 1223, 16975)));
        Assert.False(ScsKiller.AgsLaunchMissed(R("Townfall", 9000, 125)));
        Assert.False(ScsKiller.AgsLaunchMissed(R(null, 1223, 16975)));                    // a plain warm: nothing to fall back from
        Assert.False(ScsKiller.AgsLaunchMissed(R("OakGame", 1223, 16975, "dxc:85c2b2e5")));   // the game's keys decide (WarmMissesGame)
        Assert.False(ScsKiller.AgsLaunchMissed(new GameRecord { WarmedAgsApp = "OakGame" }));
    }

    [Fact]
    public async Task Amd_the_AGS_key_is_a_hint_only_once_the_game_is_seen_holding_it()
    {
        File.WriteAllBytes(Path.Combine(_exeDir, AmdAgs.DllName), []);   // links AGS, Unreal 4.26: registers its project name "Fake"
        var exe = Path.GetFileName(_game.ExePath);
        var agsKey = AmdAppCache.AppNameKey("Fake");
        var ags = AmdFile(agsKey[4..]);
        var nameHash = AmdFile(AmdAppCache.DxcKey(exe)[4..]);
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        await k.ScanAsync(default);
        Assert.Null(k.WarmAgs(_game.Id));   // not proven: a plain warm
        Assert.DoesNotContain("AGS", k.DriverCacheGap(_game.Id));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.True(File.Exists(ags));
        Assert.False(File.Exists(nameHash));

        var rec = k.Store.LoadGame(_game.Id);
        rec.GameKeys.Add(agsKey);   // the game's own process held it
        k.Store.SaveGame(_game.Id, rec);
        Assert.Equal("Fake", k.WarmAgs(_game.Id)?.App);
        Assert.Contains($"{agsKey} that Fake Game gets from its AGS app name Fake isn't learned yet", k.DriverCacheGap(_game.Id));
        rec.CacheKeys.Add(agsKey);
        k.Store.SaveGame(_game.Id, rec);
        Assert.Null(k.DriverCacheGap(_game.Id));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(ags));

        var nvidia = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }));
        await nvidia.ScanAsync(default);
        Assert.Null(nvidia.WarmAgs(_game.Id));
    }

    [Fact]
    public async Task Clearing_deletes_exactly_the_attributed_files_and_nothing_while_one_is_in_use()
    {
        string[] ours = [CacheFile("0002a91d33333333.nvph", 65536), CacheFile("fc52a91d33333333.nvph", 4096)];
        var other = CacheFile("0002a91d44444444.nvph", 65536);
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        Assert.False(k.ClearGameCache(_game.Id));   // nothing attributed
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);

        using (new FileStream(ours[1], FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))   // open, like a running game's
        {
            var e = Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id));
            Assert.Contains($"files in use by {Process.GetCurrentProcess().ProcessName}.exe (pid {Environment.ProcessId})", e.Message);
        }
        Assert.All(ours, f => Assert.True(File.Exists(f)));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        Assert.True(k.ClearGameCache(_game.Id));
        Assert.All(ours, f => Assert.False(File.Exists(f)));
        Assert.True(File.Exists(other));
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, null, 0L), (s.Status, s.WarmedAt, s.CacheOnDisk));
        Assert.True(k.ClearGameCache(_game.Id));   // attributed, nothing left on disk: still cleared
    }

    // A case-sensitive fake process list: matching another case is the watcher's own doing.
    (ScsKiller K, HashSet<string> Running, Func<int> Changes) Watched(ScsKiller k)
    {
        var running = new HashSet<string>(StringComparer.Ordinal);
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.Ordinal); };
        int changes = 0;
        k.GameChanged += s => { if (s.Game.Id == _game.Id) Interlocked.Increment(ref changes); };
        return (k, running, () => Volatile.Read(ref changes));
    }

    [Fact]
    public async Task Watcher_shows_a_started_game_as_playing_and_rereads_it_once_when_it_exits()
    {
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("33333333");
        k.Store.SaveGame(_game.Id, rec);
        CacheFile("0002a91d33333333.nvph", 65536);
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.Equal((GameStatus.Ready, 0L), (k.Games.Single().Status, k.Games.Single().CacheOnDisk));
        var (_, running, changes) = Watched(k);

        running.Add("Fake-Win64-Shipping.exe");
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));
        Assert.True(k.Games.Single().Playing);
        Assert.Equal(1, changes());                                  // "Playing now"
        // the game rebuilds its own driver cache, and the recorder logs the session
        CacheFile("0002a91d33333333.nvph", 768 * 1024);
        File.WriteAllText(Path.Combine(_exeDir, "scskiller_creates.csv"), "10.0,G,0,0,50.0\n12.0,C,1,1,0.5\n");
        k.PollGames();
        Assert.Equal(1, changes());                                  // still running: nothing re-read
        Assert.Equal(0L, k.Games.Single().CacheOnDisk);

        running.Clear();
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));                          // one poll without it isn't an exit yet
        Assert.Equal(1, changes());
        k.PollGames();
        Assert.False(k.IsPlaying(_game.Id));
        Assert.Equal(2, changes());                                  // one refresh
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Ready, 768L * 1024, false), (s.Status, s.CacheOnDisk, s.Playing));
        Assert.Equal(1, s.LastSession?.Compiles);
        k.PollGames();
        Assert.Equal(2, changes());
    }

    [Fact]
    public async Task Watcher_matches_the_exe_name_in_any_case()
    {
        var k = Killer();
        await k.ScanAsync(default);
        var (_, running, changes) = Watched(k);
        running.Add("FAKE-WIN64-SHIPPING.EXE");
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));
        Assert.Equal(1, changes());
    }

    [Fact]
    public async Task Watcher_ignores_the_launcher_stub_and_follows_the_game_exe()
    {
        var k = Killer();
        await k.ScanAsync(default);
        var (_, running, changes) = Watched(k);
        running.Add("Fake.exe");                                     // the stub at the install's root starts first
        k.PollGames();
        Assert.False(k.IsPlaying(_game.Id));
        running.Add("Fake-Win64-Shipping.exe");
        k.PollGames();
        running.Remove("Fake.exe");                                  // the stub exits while the game plays
        k.PollGames();
        k.PollGames();
        Assert.True(k.IsPlaying(_game.Id));
        Assert.Equal(1, changes());
        running.Clear();
        k.PollGames();
        k.PollGames();
        Assert.False(k.IsPlaying(_game.Id));
        Assert.Equal(2, changes());
    }

    [Fact]
    public async Task Watcher_debounces_a_game_that_restarts_itself()
    {
        var k = Killer();
        await k.ScanAsync(default);
        var (_, running, changes) = Watched(k);
        running.Add("Fake-Win64-Shipping.exe");
        k.PollGames();
        for (int i = 0; i < 3; i++)                                  // gone for one poll at a time: the same session
        {
            running.Clear();
            k.PollGames();
            running.Add("Fake-Win64-Shipping.exe");
            k.PollGames();
        }
        Assert.True(k.IsPlaying(_game.Id));
        Assert.Equal(1, changes());
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls + 3; i++) k.PollGames();
        Assert.Equal(2, changes());
    }

    [Fact]
    public async Task Watcher_stops_when_cancelled()
    {
        var k = Killer();
        await k.ScanAsync(default);
        int polls = 0;
        k.RunningGameExes = () => { Interlocked.Increment(ref polls); return new HashSet<string>(); };
        k.WatchInterval = TimeSpan.FromMilliseconds(10);
        using var cts = new CancellationTokenSource();
        var watching = k.WatchGames(cts.Token);
        await Until(() => Volatile.Read(ref polls) >= 3);
        cts.Cancel();
        await watching.WaitAsync(TimeSpan.FromSeconds(5));
        var n = Volatile.Read(ref polls);
        await Task.Delay(100);
        Assert.Equal(n, Volatile.Read(ref polls));
    }

    [Fact]
    public async Task The_real_process_list_finds_a_game_named_like_a_running_process()
    {
        // this test process plays the game
        var game = _game with { ExePath = Path.Combine(_exeDir, Process.GetCurrentProcess().ProcessName.ToUpperInvariant() + ".exe") };
        var k = Killer(game: game);
        await k.ScanAsync(default);
        k.PollGames();
        Assert.True(k.IsPlaying(game.Id));
    }

    [Fact]
    public async Task The_real_process_list_leaves_out_a_staged_warm_even_for_an_anti_cheat_game()
    {
        // dummies: a copy of cmd named scskiller_warm.exe starts a copy of ping named like the game, as the warm stages it.
        // The process list is the PC's: a name of this test alone, or another run's game is taken for this one's
        var name = "Fake-" + Guid.NewGuid().ToString("N")[..8];
        var played = _game with { ExePath = Path.Combine(_exeDir, name + ".exe") };
        var warm = Path.Combine(_root, "native", "scskiller_warm.exe");
        var staged = Path.Combine(_root, "stage", name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(warm)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), warm);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), staged);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));   // its process is only ever named, never opened
        var k = Killer(game: played);
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        static Process Start(string exe, string args) => Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!;
        using var parent = Start(warm, $"/c \"{staged}\" -n 30 127.0.0.1");
        Process? game = null;
        try
        {
            await Until(() => Process.GetProcessesByName(name).Length > 0);
            k.PollGames();
            Assert.False(k.IsPlaying(_game.Id));
            game = Start(staged, "-n 30 127.0.0.1");                 // the same exe, not under a warm: the game
            k.PollGames();
            Assert.True(k.IsPlaying(_game.Id));
        }
        finally
        {
            parent.Kill(entireProcessTree: true);
            game?.Kill();
            game?.Dispose();
        }
    }

    [Fact]
    public async Task Window_activation_rereads_only_cache_sizes_and_the_detail_refresh_one_game()
    {
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("55555555");
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        var (_, _, changes) = Watched(k);
        k.RefreshCacheSizes();
        Assert.Equal(0, changes());                                  // unchanged: nothing raised
        CacheFile("0002a91d55555555.nvph", 4096);
        k.RefreshCacheSizes();
        Assert.Equal((1, 4096L), (changes(), k.Games.Single().CacheOnDisk));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);

        k.RefreshGame(_game.Id);
        Assert.Equal(2, changes());
        Assert.Throws<ArgumentException>(() => k.RefreshGame("test:unknown"));
    }

    [Fact]
    public async Task Clearing_deletes_the_games_D3DSCache_folder_and_its_own_caches_only_when_asked()
    {
        var d3ds = Path.Combine(_root, "D3DSCache");   // Killer: LocalAppData = _root, MyGames = _root\My Games
        var ours = FakeD3DSCache.Folder(d3ds, "1111", _game.ExePath);
        var other = FakeD3DSCache.Folder(d3ds, "2222", @"C:\Games\Other\Fake-Win64-Shipping.exe");
        var broken = FakeD3DSCache.Folder(d3ds, "3333", _game.ExePath, broken: true);   // unparsable: nothing deleted in it
        string F(string dir, string name, int size)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), new byte[size]);
            return Path.Combine(dir, name);
        }
        var written = F(Path.Combine(_root, "My Games", "Fake Game", "Saved"), "Fake_PCD3D_SM6.upipelinecache", 1000);
        string[] precache =   // %LOCALAPPDATA%\<game>\Saved (Tiny Tina's Wonderlands' own cache) and C:\ProgramData\<game>
        [
            F(Path.Combine(_root, "Fake Game", "Saved"), "D3DGraphics_V4098_D5510_GBX_1.ushaderprecache", 700),
            F(Path.Combine(_root, "ProgramData", "Fake Game"), "D3DCompute_V4098_D5510_GBX_1.ushaderprecache", 300),
        ];
        string[] shipped =   // the install (_root\FakeGame, itself one level under LocalAppData here) is never touched
        [
            F(Path.Combine(_game.InstallDir, "Fake", "Content", "PipelineCaches", "Windows"), "Fake_PCD3D_SM6.stable.upipelinecache", 500),
            F(Path.Combine(_game.InstallDir, "Saved"), "Fake_PCD3D_SM6.upipelinecache", 500),
            F(Path.Combine(_game.InstallDir, "Saved"), "D3DGraphics_V4098_D5510_GBX_1.ushaderprecache", 500),
        ];
        var otherFiles = Directory.GetFiles(other).Concat(Directory.GetFiles(broken))
            .Append(F(Path.Combine(_root, "Other Game", "Saved"), "D3DGraphics_V4098_D5510_GBX_1.ushaderprecache", 100)).ToList();
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);

        var parts = k.GameCaches(_game.Id);
        Assert.Equal([ScsKiller.WindowsPart], parts.Select(p => p.Name));   // the game's own caches only when asked
        Assert.Equal(Directory.GetFiles(ours).Sum(f => new FileInfo(f).Length), parts[0].Bytes);
        var all = k.GameCaches(_game.Id, gamePrecache: true);
        Assert.Equal([ScsKiller.WindowsPart, ScsKiller.PipelinePart, ScsKiller.PrecachePart], all.Select(p => p.Name));
        Assert.Equal([written], all[1].Files);
        Assert.Equal(precache.Order(), all[2].Files.Order());
        Assert.Equal(1000, all[2].Bytes);

        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(Directory.Exists(ours));
        Assert.All(precache.Append(written), f => Assert.True(File.Exists(f), f));   // kept by default
        Assert.Empty(k.GameCaches(_game.Id));
        Assert.False(k.ClearGameCache(_game.Id));   // nothing left, no driver keys

        Assert.True(k.ClearGameCache(_game.Id, gamePrecache: true));
        Assert.All(precache.Append(written), f => Assert.False(File.Exists(f), f));
        Assert.All(otherFiles.Concat(shipped), f => Assert.True(File.Exists(f), f));
        Assert.Empty(k.GameCaches(_game.Id, gamePrecache: true));
    }

    [Fact]
    public async Task A_folder_named_like_another_game_is_neither_games_precache()
    {
        var precache = Path.Combine(_root, "Fake Game", "Saved", "D3DGraphics_GBX_1.ushaderprecache");
        Directory.CreateDirectory(Path.GetDirectoryName(precache)!);
        File.WriteAllBytes(precache, new byte[10]);
        var twin = _game with { Id = "test:twin", InstallDir = Path.Combine(_root, "Twin"), ExePath = Path.Combine(_root, "Twin", "Twin.exe") };
        Directory.CreateDirectory(twin.InstallDir);
        File.WriteAllBytes(twin.ExePath, new byte[100]);
        var k = Killer(new FakeReader(Unreal), games: [_game, twin]);
        await k.ScanAsync(default);
        Assert.Empty(k.GameCaches(_game.Id, gamePrecache: true));   // both are named "Fake Game"
        Assert.True(File.Exists(precache));
    }

    [Fact]
    public async Task Clearing_is_refused_while_the_game_runs_and_near_anti_cheat_leaves_all_but_the_driver_cache()
    {
        // the game is named like this test process: running, from the process list alone
        var running = _game with { ExePath = Path.Combine(_exeDir, Process.GetCurrentProcess().ProcessName + ".exe") };
        var folder = FakeD3DSCache.Folder(Path.Combine(_root, "D3DSCache"), "1111", running.ExePath);
        var k = Killer(new FakeReader(Unreal), game: running);
        await k.ScanAsync(default);
        Assert.Single(k.GameCaches(running.Id));
        Assert.Equal("Fake Game is running", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(running.Id)).Message);
        Assert.Equal(3, Directory.GetFiles(folder).Length);

        FakeD3DSCache.Folder(Path.Combine(_root, "D3DSCache"), "2222", _game.ExePath);
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "EasyAntiCheat"));
        var nvph = CacheFile("0002a91d55555555.nvph", 4096);
        k = Killer(new FakeReader(Unreal));
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add("55555555");
        k.Store.SaveGame(_game.Id, rec);
        await k.ScanAsync(default);
        Assert.Equal(AntiCheat.EasyAntiCheat, k.Games.Single().AntiCheat);
        Assert.Equal([ScsKiller.DriverPart], k.GameCaches(_game.Id).Select(p => p.Name));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(nvph));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "D3DSCache", "2222")).Length);
    }

    [Fact]
    public async Task A_warm_waits_while_its_game_runs_and_stops_when_it_starts_then_continues_from_there()
    {
        var warmer = new ControlledWarmer();
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fake-win64-shipping.exe" };
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        void Game(bool on) { lock (running) if (on) running.Add("Fake-Win64-Shipping.exe"); else running.Clear(); }
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Until(() => k.Queue.Single().Stage == QueueStage.Paused);   // the game was already running: no warm yet
        Assert.Equal("stopped while Fake Game is running: continues when it exits", k.Queue.Single().Note);
        await Task.Delay(700);
        Assert.Empty(warmer.Started);

        Game(false);
        await Until(() => warmer.Started.Count == 1);
        var first = warmer.Run!;
        k.PauseQueue();                                                   // paused or not, the game's start stops it
        await Until(() => first.Paused);
        Game(true);
        await Until(() => first.Stopped);
        await Until(() => k.Queue.Single().Note?.StartsWith("stopped while Fake Game") == true);
        await Until(() => k.Store.LoadGame(_game.Id).ResumeAt == 10);     // saved after the note: once the run has returned
        await Task.Delay(700);
        Assert.Single(warmer.Started);                                    // waits for the game to exit

        Game(false);
        await Task.Delay(1200);
        Assert.Single(warmer.Started);                                    // the queue is still paused
        k.ResumeQueue();
        await Until(() => warmer.Started.Count == 2);
        Assert.Equal(10, warmer.Options!.StartAt);                        // from where it stopped
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Another_game_only_pauses_a_background_warm_and_its_own_game_stops_it()
    {
        var warmer = new ControlledWarmer();
        var other = _game with { Id = "test:other", Name = "Other Game", ExePath = Path.Combine(_exeDir, "Other.exe") };
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: [_game, other]);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        k.RunningGameExes = () => { lock (running) return running.ToHashSet(StringComparer.OrdinalIgnoreCase); };
        k.Background = true;
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await Until(() => warmer.Run != null);
        var run = warmer.Run!;

        lock (running) running.Add("Other.exe");
        await Until(() => run.Paused);
        await Until(() => k.Queue.Single().Note == "paused while Other Game is running");
        k.Settings = k.Settings with { PauseWhileGaming = false };        // the setting only governs other games
        await Until(() => !run.Paused);
        lock (running) running.Add("Fake-Win64-Shipping.exe");
        await Until(() => run.Stopped);
        lock (running) running.Clear();
        await Until(() => warmer.Started.Count == 2);
        Assert.Equal(10, warmer.Options!.StartAt);
        warmer.Run!.Finish();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
    }

    const string Ff7Profile = "dxc:6b2fcd83";
    string AmdFile(string app, string kind = "71efbc0e", int size = 65536)
    {
        var path = Path.Combine(_root, "AMD", "DxcCache", $"{app}.dfac411e.{kind}.2b1a674a.0.parc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
    AmdAppCache AmdCache() => new(Path.Combine(_root, "AMD", "DxcCache"), Path.Combine(_root, "AMD", "DxCache"));
    static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);   // as the driver does

    /// <summary>An AMD killer whose game is named like this test process, which plays the staged warm holding
    /// <paramref name="held"/> (a cache file) open.</summary>
    (ScsKiller K, Game Game, FakeWarmer Warmer) AmdKiller(string held, Game[]? others = null)
    {
        var game = _game with { ExePath = Path.Combine(_exeDir, Process.GetCurrentProcess().ProcessName + ".exe") };
        var warmer = new FakeWarmer(() => Hold(held));
        var k = Killer(new FakeReader(Unreal), warmer: warmer, games: [game, .. others ?? []], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        k.RunningGameExes = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (k, game, warmer);
    }

    [Fact]
    public async Task Amd_a_profiled_key_is_learned_from_the_open_files_not_the_name_hash_and_case_does_not_matter()
    {
        var (k, game, _) = AmdKiller(AmdFile("6b2fcd83"));   // the driver's app profile: a fixed key, not FNV-1a(name)
        var exe = Path.GetFileName(game.ExePath);
        AmdFile(AmdAppCache.DxcKey(exe)[4..], size: 4096);   // name-hash files (another tool, an older driver): not the game's
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Equal([Ff7Profile], k.Store.LoadGame(game.Id).CacheKeys);
        Assert.Equal(65536, k.Games.Single().CacheOnDisk);

        Marker(exe.ToUpperInvariant(), DateTimeOffset.Now);   // launched in another case: the profile's key is the same
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(exe.ToUpperInvariant(), k.Store.LoadGame(game.Id).LaunchedExeName);
        Assert.Equal(GameStatus.Warmed, s.Status);
    }

    [Fact]
    public async Task Amd_a_name_hashed_key_keeps_the_exe_case_rule()
    {
        var exe = Process.GetCurrentProcess().ProcessName + ".exe";
        var (k, game, _) = AmdKiller(AmdFile(AmdAppCache.DxcKey(exe)[4..]));
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Equal([AmdAppCache.DxcKey(exe)], k.Store.LoadGame(game.Id).CacheKeys);

        Marker(exe.ToUpperInvariant(), DateTimeOffset.Now);
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal(GameStatus.Stale, s.Status);
        Assert.Contains($"runs as {exe.ToUpperInvariant()}", s.StatusReason);
    }

    [Fact]
    public async Task Amd_keys_are_learned_while_the_game_itself_runs()
    {
        var (k, game, _) = AmdKiller(AmdFile("6b2fcd83"));
        var exe = Path.GetFileName(game.ExePath);
        await k.ScanAsync(default);
        Assert.Null(k.Games.Single().CacheOnDisk);
        k.RunningGameExes = () => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exe };   // this process plays the game
        using (Hold(AmdFile("6b2fcd83")))
            await k.ScanAsync(default);
        Assert.Equal([Ff7Profile], k.Store.LoadGame(game.Id).CacheKeys);
        Assert.Equal(65536, k.Games.Single().CacheOnDisk);
    }

    [Fact]
    public async Task Amd_a_warmed_game_whose_cache_file_the_driver_trimmed_is_stale()
    {
        var pipelines = AmdFile("6b2fcd83");
        var stages = AmdFile("6b2fcd83", kind: "a4a986a3", size: 16384);
        AmdFile("12345678", size: 4096);   // another app's file
        var (k, game, _) = AmdKiller(pipelines);
        await k.ScanAsync(default);
        await WarmOnce(k, game.Id);
        Assert.Equal([Path.GetFileName(pipelines), Path.GetFileName(stages)], k.Store.LoadGame(game.Id).WarmedFiles!.Order());
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);

        File.Delete(Path.Combine(_root, "AMD", "DxcCache", "12345678.dfac411e.71efbc0e.2b1a674a.0.parc"));   // not this game's
        File.WriteAllBytes(pipelines.Replace(".0.parc", ".1.parc"), new byte[4096]);   // new files don't matter
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);

        File.Delete(stages);   // the driver trims one file of the key
        var s = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedPartReason), (s.Status, s.StatusReason));

        File.Delete(pipelines);
        s = (await k.ScanAsync(default)).Single();
        Assert.Equal((GameStatus.Stale, ScsKiller.TrimmedAllReason), (s.Status, s.StatusReason));

        AmdFile("6b2fcd83");   // the warm fills the key again
        await WarmOnce(k, game.Id);
        Assert.Equal(GameStatus.Warmed, (await k.ScanAsync(default)).Single().Status);
    }

    [Fact]
    public void Amd_queue_warning_names_the_games_and_the_overshoot_only_past_the_free_room()
    {
        const long G = 1L << 30;
        Assert.Null(AmdAppCache.QueueWarning(10 * G, [("A", 4 * G), ("B", 2 * G)]));   // exactly the 6 GB free
        Assert.Null(AmdAppCache.QueueWarning(10 * G, []));
        Assert.Equal("This queue adds about 7 GB to the shader cache (A 4 GB, B 3 GB), 1 GB more than the 6 GB free under the AMD driver's fixed 16 GB limit. "
            + "The driver then trims the least recently used caches, older games' included, and those games stutter until compiled again.",
            AmdAppCache.QueueWarning(10 * G, [("A", 4 * G), ("B", 3 * G), ("Warmed", 0)]));
        Assert.Contains("more than the 0 MB free", AmdAppCache.QueueWarning(17 * G, [("A", 512L << 20)]));   // already past the cap
        Assert.Equal(16 * G, AmdAppCache.DxcCacheCap);
    }

    [Fact]
    public void Cache_growth_is_the_estimate_less_what_the_games_keys_hold()
    {
        var s = new GameState(_game, null, AntiCheat.None, GameStatus.Ready, "", null, null, 3000, null, null, null, null, false, null, CacheOnDisk: 1000);
        Assert.Equal(2000, ScsKiller.CacheGrowth(s));
        Assert.Equal(0, ScsKiller.CacheGrowth(s with { CacheOnDisk = 5000 }));
        Assert.Equal(0, ScsKiller.CacheGrowth(s with { EstimatedCacheBytes = null }));
    }

    [Fact]
    public void Amd_cache_listing_never_reads_cache_file_contents()
    {
        var files = new[] { AmdFile("6b2fcd83"), AmdFile("6b2fcd83", kind: "a4a986a3"), AmdFile("12345678") };
        var old = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var probe = AmdFile("0badf00d", size: 4096);
        File.SetLastAccessTimeUtc(probe, old);
        File.ReadAllBytes(probe);
        if (File.GetLastAccessTimeUtc(probe) == old) return;   // this volume doesn't update access times: nothing to observe
        File.Delete(probe);
        foreach (var f in files) File.SetLastAccessTimeUtc(f, old);

        var cache = AmdCache();
        string[] keys = [Ff7Profile, "dxc:12345678"];
        Assert.Equal(3 * 65536, cache.SizeOf(keys));
        Assert.Equal(3, cache.FilesOf(keys).Count);
        Assert.Equal(2, cache.D3D12FileNames([Ff7Profile]).Count);
        Assert.Empty(cache.Missing(files.Select(f => Path.GetFileName(f))));
        Assert.Equal(3 * 65536, cache.DxcBytes());
        Assert.Empty(cache.KeysOpenBy(Process.GetCurrentProcess().ProcessName + ".exe"));
        foreach (var f in files) Assert.Equal(old, File.GetLastAccessTimeUtc(f));   // the driver evicts by access time: listing must not refresh it
    }

    [Fact]
    public async Task Amd_clearing_refuses_a_key_shared_with_another_game_and_clears_once_it_is_not()
    {
        // another discovered game named "ff7rebirth*": the driver profile gives it the same key, warmed or not
        var demoDir = Path.Combine(_root, "Demo");
        Directory.CreateDirectory(demoDir);
        var demo = new Game("test:demo", "FF7 Demo", Store.Other, demoDir, Path.Combine(demoDir, "ff7rebirth_demo.exe"));
        var other = new Game("test:other", "Other Game", Store.Other, demoDir, Path.Combine(demoDir, "Other.exe"));
        var profile = AmdFile("6b2fcd83");
        var k = Killer(new FakeReader(Unreal), games: [_game, demo], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        await k.ScanAsync(default);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add(Ff7Profile);   // what its warm held (see the attribution test above)
        k.Store.SaveGame(_game.Id, rec);
        var e = Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id));
        Assert.Contains($"shared with FF7 Demo ({Ff7Profile})", e.Message);
        Assert.True(File.Exists(profile));

        // a game with a name the profile table doesn't know, but whose own warms held the same files: shared too
        k = Killer(new FakeReader(Unreal), games: [_game, other], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        var otherRec = k.Store.LoadGame(other.Id);
        otherRec.CacheKeys.Add(Ff7Profile);
        k.Store.SaveGame(other.Id, otherRec);
        await k.ScanAsync(default);
        Assert.Contains("shared with Other Game", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id)).Message);

        otherRec.CacheKeys.Clear();
        k.Store.SaveGame(other.Id, otherRec);
        using (Hold(profile))   // any running process using the key holds its files: refused, nothing deleted
            Assert.Contains("files in use by", Assert.Throws<InvalidOperationException>(() => k.ClearGameCache(_game.Id)).Message);
        Assert.True(File.Exists(profile));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(profile));
    }

    [Fact]
    public async Task Clearing_a_game_with_no_learned_key_takes_its_name_hash_on_AMD_unless_a_profile_is_learned()
    {
        var exe = Path.GetFileName(_game.ExePath);
        var nameHash = AmdFile(AmdAppCache.DxcKey(exe)[4..]);
        var other = AmdFile("12345678");
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        k.AppCache = AmdCache();
        await k.ScanAsync(default);
        Assert.Equal([nameHash], k.GameCaches(_game.Id).Single(p => p.Name == ScsKiller.DriverPart).Files);
        Assert.Contains("isn't learned yet", k.DriverCacheGap(_game.Id));
        Assert.True(k.ClearGameCache(_game.Id));
        Assert.False(File.Exists(nameHash));
        Assert.True(File.Exists(other));

        AmdFile(AmdAppCache.DxcKey(exe)[4..]);   // name-hash files beside a learned profile key: not the game's
        var rec = k.Store.LoadGame(_game.Id);
        rec.CacheKeys.Add(Ff7Profile);
        k.Store.SaveGame(_game.Id, rec);
        Assert.Null(k.DriverCacheGap(_game.Id));
        Assert.DoesNotContain(nameHash, k.GameCaches(_game.Id).SelectMany(p => p.Files));

        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));   // NVIDIA's key isn't derivable from the name
        rec.CacheKeys.Clear();
        k.Store.SaveGame(_game.Id, rec);
        Assert.Contains("not cleared", k.DriverCacheGap(_game.Id));
    }

    [Fact]
    public void A_piped_answer_drops_the_byte_order_mark_Windows_PowerShell_sends()
    {
        Assert.Equal("y", Elevated.ReadAnswer(new MemoryStream([0xEF, 0xBB, 0xBF, (byte)'y', (byte)' ', (byte)'\r', (byte)'\n'])));
        Assert.Null(Elevated.ReadAnswer(new MemoryStream()));
    }

    [Fact]
    public async Task An_install_folder_two_sources_list_is_one_game_the_earlier_sources()
    {
        var ea = _game with { Id = "ea:1", Store = Store.EA, InstallDir = _game.InstallDir.ToUpperInvariant() + @"\" };
        var k = new ScsKiller([new FakeSource([_game]), new FakeSource([ea])], new FakeVendor(Gpu), new FakeReader(null), new FakePlanner(),
            new FakeWarmer(), Path.Combine(_root, "data"), _proxy);
        Assert.Equal(_game.Id, Assert.Single(await k.ScanAsync(default)).Game.Id);
    }

    [Fact]
    public async Task The_estimate_uses_this_pcs_first_measured_warm_rate_else_the_vendors_default()
    {
        var second = _game with { Id = "test:second", Name = "Second" };
        var k = Killer(new FakeReader(Unreal), games: [_game, second], vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }));
        var rec = k.Store.LoadGame(second.Id);
        rec.Plan = new Plan(second.Id, "content-1", "PCD3D_SM6", "fake-1", new PlanStats(0, 4000, 0, 0, true, D3D11Shaders: 800), @"C:\x\plan.bin");
        k.Store.SaveGame(second.Id, rec);
        await k.ScanAsync(default);
        TimeSpan? Estimate() => k.Games.Single(s => s.Game.Id == second.Id).EstimatedWarmTime;
        Assert.Equal(TimeSpan.FromSeconds(4800 / ScsKiller.DefaultAmdPsoPerSecond), Estimate());

        await WarmOnce(k, _game.Id);   // FakeWarmer: 10000 items in 10 s
        await k.ScanAsync(default);
        Assert.Equal(TimeSpan.FromSeconds(4.8), Estimate());
        Assert.Equal(ScsKiller.DefaultPsoPerSecond, ScsKiller.DefaultWarmRate(GpuVendor.Nvidia));
    }

    [Fact]
    public async Task The_measured_rate_sums_a_resumed_warms_segments_and_skips_a_rewarm_onto_a_filled_cache()
    {
        WarmResult R(WarmOutcome o, long done, double s) => new(o, done, 10000, 0, TimeSpan.FromSeconds(s), 1 << 20, "", null);
        var warmer = new ScriptedWarmer(R(WarmOutcome.Stopped, 4000, 10), R(WarmOutcome.Completed, 10000, 20), R(WarmOutcome.Completed, 10000, 5));
        var k = Killer(new FakeReader(Unreal), warmer: warmer);
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Stopped, k.Queue.Single().Stage);
        await WarmOnce(k, _game.Id);
        var rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((TimeSpan.FromSeconds(30), 10000 / 30.0), (rec.LastWarmTime, rec.PsoPerSecond));
        Assert.Equal(10000 / 30.0, ScsKiller.MeasuredRate(k.Games));

        await WarmOnce(k, _game.Id);   // the same driver: its cache already holds the game
        rec = k.Store.LoadGame(_game.Id);
        Assert.Equal((TimeSpan.FromSeconds(5), 10000 / 30.0), (rec.LastWarmTime, rec.PsoPerSecond));
        Assert.Equal([0L, 4000L, 0L], warmer.StartAts);
    }

    sealed class ScriptedWarmer(params WarmResult[] results) : IWarmer
    {
        public readonly List<long> StartAts = [];
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            StartAts.Add(options.StartAt);
            return new Run(Task.FromResult(results[StartAts.Count - 1]));
        }
        sealed class Run(Task<WarmResult> completion) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = completion;
            public void Pause() { }
            public void Resume() { }
            public void Stop() { }
        }
    }

    /// <summary>D3D11 items of one shader for the real-GPU attribution tests: the app samples the open cache files on the
    /// warm's progress lines, so the warm must outlive a few of them. 3000 (one cached shader, well under a second) left it
    /// unsampled in about half the AMD runs (keys empty, 4-5 progress reports) and flaked once on NVIDIA in a
    /// full parallel run; 20000 keep it ~5 s (AMD 4/4 after).</summary>
    const int SampledWarmItems = 20000;

    /// <summary>This checkout's own proxy\build\Release\scskiller_warm.exe (the repo root is the folder with SCSKiller.slnx
    /// above the test binaries), else null: NativeTools.Find walks further up and, from a git worktree under the main
    /// checkout, would pick the main checkout's (older) build.</summary>
    static string? OwnWarmExe()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SCSKiller.slnx")))
                return Path.Combine(d.FullName, "proxy", "build", "Release", "scskiller_warm.exe") is var p && File.Exists(p) ? p : null;
        return null;
    }

    // Real AMD GPU: a tiny warm under a fresh, non-profiled exe name holds the DxcCache
    // files of FNV-1a(name); clearing removes them. Never run under an "ff7rebirth*" name: that is the real game's cache.
    [Trait("Needs", "Gpu")]
    [Fact]
    public async Task Real_amd_a_fresh_name_warms_under_its_name_hash_and_clearing_removes_its_files()
    {
        if (GpuBackends.Detect() is not AmdBackend amd || OwnWarmExe() is not { } warmExe) return;   // not this machine, or this checkout's proxy isn't built
        var gpuLock = TestEnv.GpuLockPath;
        if (File.Exists(gpuLock) && DateTime.Now - File.GetLastWriteTime(gpuLock) < TimeSpan.FromMinutes(30)) return;   // someone's GPU run
        Directory.CreateDirectory(Path.GetDirectoryName(gpuLock)!);
        File.WriteAllText(gpuLock, "SCSKiller.Tests: AMD name-hash attribution");
        var game = _game with { Id = "test:gpu", ExePath = Path.Combine(_exeDir, $"scsk-test-{Guid.NewGuid():N}"[..20] + ".exe") };
        var exe = Path.GetFileName(game.ExePath);
        Assert.Null(AmdAppCache.ProfileKey(exe));
        HashSet<string?> D3D11Keys() => Directory.Exists(AmdBackend.D3D11CacheDir)
            ? Directory.GetFiles(AmdBackend.D3D11CacheDir).Select(f => AmdAppCache.Key(Path.GetFileName(f), d3d12: false)).ToHashSet() : [];
        var d3d11Before = D3D11Keys();
        try
        {
            // Creating the staged D3D12 device already makes the driver create and open the name's 2 DxcCache files (measured:
            // also with no D3D12 item at all), so open files don't show that any PSO compiled: the warm's failed count is checked
            // (its compute PSO once failed to decode unnoticed). The D3D11 items keep the warm alive long enough (progress
            // lines) for the attribution to sample it with the files open (SampledWarmItems).
            var warmer = new KeepLog(new Warmer(amd, warmExe));
            var k = Killer(new FakeReader(Unreal), new FakePlanner(GenDb(SampledWarmItems, d3d12: true)), warmer, game: game, vendor: amd);
            k.ThreadsOverride = 1;
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            k.Log = new Progress<string>(log.Enqueue);
            await k.ScanAsync(default);
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(2));
            var keys = k.Store.LoadGame(game.Id).CacheKeys;
            var info = $"{exe}: {k.Queue.Single().Stage} {k.Queue.Single().Error} {k.Queue.Single().Progress}; keys {string.Join(", ", keys)}; " +
                       $"name hash {AmdAppCache.DxcKey(exe)} with {amd.AppCache.FilesOf([AmdAppCache.DxcKey(exe)]).Count} files; " +
                       $"{warmer.ProgressReports} progress reports (attribution samples); app log: {string.Join(" | ", log)}";
            Assert.True(k.Queue.Single().Stage == QueueStage.Done, info);
            Assert.True(warmer.Result?.Failed == 0, $"{info}; failed {warmer.Result?.Failed}; log:\n{warmer.Log}");   // the compute PSO compiles
            Assert.True(keys.Contains(AmdAppCache.DxcKey(exe)), info);
            Assert.Equal(true, AmdAppCache.IsNameHashed(keys, exe));
            Assert.True(k.ClearGameCache(game.Id));
            Assert.Empty(amd.AppCache.FilesOf(keys));
        }
        finally
        {
            // a fresh name: its name-hash files and the D3D11 keys that appeared meanwhile (D3D11 attribution is unreliable)
            var cache = new AmdAppCache(AmdBackend.CacheDir, AmdBackend.D3D11CacheDir);
            foreach (var key in D3D11Keys().Except(d3d11Before).OfType<string>().Append(AmdAppCache.DxcKey(exe)))
                try { cache.Delete([key]); }
                catch (InvalidOperationException) { }   // in use: another app's, not ours
            File.Delete(gpuLock);
        }
    }

    // Real GPU (NVIDIA): a warm of SampledWarmItems D3D11 items (one shader) under a fresh fake exe name, then its cache files
    // are removed.
    [Trait("Needs", "Gpu")]
    [Fact]
    public async Task Real_machine_a_small_warm_attributes_a_new_cache_key_and_clearing_removes_its_files()
    {
        if (GpuBackends.Detect() is not NvidiaBackend nv || OwnWarmExe() is not { } warmExe) return;   // not this machine, or this checkout's proxy isn't built
        var gpuLock = TestEnv.GpuLockPath;
        if (File.Exists(gpuLock) && DateTime.Now - File.GetLastWriteTime(gpuLock) < TimeSpan.FromMinutes(30)) return;   // someone's GPU run
        Directory.CreateDirectory(Path.GetDirectoryName(gpuLock)!);
        File.WriteAllText(gpuLock, "SCSKiller.Tests: cache attribution");
        try
        {
            var game = _game with { Id = "test:gpu", ExePath = Path.Combine(_exeDir, $"scsk-test-{Guid.NewGuid():N}"[..20] + ".exe") };
            var before = Directory.GetFiles(NvidiaBackend.CacheDir).Select(Path.GetFileName).ToHashSet();
            var k = Killer(new FakeReader(Unreal), new FakePlanner(GenDb(SampledWarmItems)), new Warmer(nv, warmExe), game: game, vendor: nv);
            k.ThreadsOverride = 1;
            await k.ScanAsync(default);
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
            var key = Assert.Single(k.Store.LoadGame(game.Id).CacheKeys);
            var files = nv.AppCache.FilesOf([key]);
            Assert.NotEmpty(files);
            Assert.All(files, f => Assert.DoesNotContain(f.Name, before));   // a fresh name: all its files are new
            Assert.Equal(files.Sum(f => f.Length), k.Games.Single().CacheOnDisk);

            Assert.True(k.ClearGameCache(game.Id));
            Assert.Empty(nv.AppCache.FilesOf([key]));
        }
        finally { File.Delete(gpuLock); }
    }

    /// <summary>The real warm on WARP (no GPU cache): the create of item K removes the device (SCSKILLER_WARM_REMOVE, as the AMD
    /// driver did on two compute PSOs). The compile goes on in new processes, every other item compiles, K is kept in the
    /// game's state as crashing the driver, and the next compile skips it without a removal. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task A_pipeline_that_removes_the_device_is_skipped_kept_and_never_created_again()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        var warp = new FakeVendor(Gpu with { AdapterLuid = Convert.ToInt64(luid, 16) });
        const int K = 17;
        var (db, keys) = ComputeDb(40);
        var game = _game with { ExePath = Path.Combine(_exeDir, $"scsk-rm-{Guid.NewGuid():N}"[..16] + ".exe") };
        var warmer = new KeepLog(new Warmer(warp, warmExe) { Environment = new Dictionary<string, string> { ["SCSKILLER_WARM_REMOVE"] = K.ToString() } });
        var k = Killer(new FakeReader(Unreal), new FakePlanner(db), warmer, game: game, vendor: warp);
        k.ThreadsOverride = 4;
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        k.QueueChanged += q => { if (q.Note != null) notes.Enqueue(q.Note); };
        await k.ScanAsync(default);
        async Task Compile()
        {
            notes.Clear();
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(2));
            var q = k.Queue.Single();
            Assert.True(q.Stage == QueueStage.Done, $"{q.Stage} {q.Error}\n{warmer.Log}");
            Assert.Equal((40L, 40L, 0L), (warmer.Result!.Done, warmer.Result.Total, warmer.Result.Failed));   // the other 39 compiled
            Assert.Equal(keys[K], Assert.Single(warmer.Result.Crashed!));
            Assert.Equal("1 skipped (it crashes the GPU driver)", q.Note);
        }

        await Compile();
        Assert.Contains("recovering from a GPU driver crash", notes);
        Assert.Equal([keys[K]], k.Store.LoadGame(game.Id).CrashKeys);
        Assert.Equal(1, k.Games.Single().LastWarmCrashed);

        await Compile();   // the hook is still set: a create of K would remove the device again
        Assert.DoesNotContain("recovering from a GPU driver crash", notes);
        Assert.Contains("1 items skipped: they removed the device in an earlier run", warmer.Log);
        Assert.DoesNotContain("the device was removed", warmer.Log);
    }

    /// <summary>A careful compile on WARP (the recording's 40 compute PSOs, each shader under two root signatures: two
    /// passes, then the plan's 10 at full speed): the create of item K in the second pass removes the device. That pass
    /// recovers like any warm (new process, K blamed and kept), the fast pass still runs, and the next careful compile skips K
    /// inside its pass without a removal. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task A_careful_pass_recovers_from_a_removed_device_and_skips_the_blamed_item_in_later_compiles()
    {
        if (OwnWarmExe() is not { } warmExe) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(warmExe)!, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!
            .StandardOutput.ReadToEnd().Trim();
        var warp = new FakeVendor(Gpu with { Vendor = GpuVendor.Amd, AdapterLuid = Convert.ToInt64(luid, 16) });
        const int K = 25;   // the second root signature's 6th PSO: pass 2
        var (recorded, keys) = SiblingComputeDb(20);
        var (plan, _) = ComputeDb(10);
        var game = _game with { ExePath = Path.Combine(_exeDir, $"scsk-cp-{Guid.NewGuid():N}"[..16] + ".exe") };
        var warmer = new KeepLog(new Warmer(warp, warmExe) { Environment = new Dictionary<string, string> { ["SCSKILLER_WARM_REMOVE"] = K.ToString() } });
        var k = Killer(new FakeReader(Unreal), new FakePlanner(plan, stats: new PlanStats(40, 10, 0, 3, true), mainDb: recorded), warmer, game: game, vendor: warp);
        k.ThreadsOverride = 4;
        var rec = k.Store.LoadGame(game.Id);
        rec.Careful = true;
        k.Store.SaveGame(game.Id, rec);
        var notes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        k.QueueChanged += q => { if (q.Note != null) notes.Enqueue(q.Note); };
        await k.ScanAsync(default);
        async Task Compile()
        {
            notes.Clear();
            k.Enqueue(game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(TimeSpan.FromMinutes(3));
            var q = k.Queue.Single();
            Assert.True(q.Stage == QueueStage.Done, $"{q.Stage} {q.Error}\n{warmer.Log}");
            Assert.Equal((50L, 50L, 0L), (warmer.Result!.Done, warmer.Result.Total, warmer.Result.Failed));   // the other 49 compiled
            Assert.Equal(keys[K], Assert.Single(warmer.Result.Crashed!));
            Assert.Contains("(pass 255, 40 items of other passes)", warmer.Log);   // the last process: the plan's pass
        }

        await Compile();
        Assert.Contains("recovering from a GPU driver crash", notes);
        Assert.Contains("careful compile: the plan's other pipelines at full speed", notes);   // the passes after it ran
        Assert.Equal([keys[K]], k.Store.LoadGame(game.Id).CrashKeys);

        await Compile();   // the hook is still set: a create of K would remove the device again
        Assert.DoesNotContain("recovering from a GPU driver crash", notes);
        Assert.True(k.Store.LoadGame(game.Id).WarmedCareful);
    }

    /// <summary>A recorded db of <paramref name="n"/> compute shaders, each under two root signatures (all the first ones, then
    /// all the second ones): siblings, so a careful compile splits them into two passes. Keys in item order.</summary>
    static (byte[] Db, string[] Keys) SiblingComputeDb(int n)
    {
        var db = new MemoryStream();
        var rss = new[] { 0u, 1u }.Select(flags => Core.Planning.RootSig.Serialize(new(flags, [[0, 0, 1, 1, 0, 0, 0]]), [])).ToList();
        var rsShas = rss.Select(rs => PsoDb.Hex(SHA1.HashData(rs))).ToList();
        for (int r = 0; r < 2; r++) PsoDb.WriteBlob(db, rsShas[r], rss[r]);
        var css = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var cs = Carved.Hlsl.Compile($"RWBuffer<uint> b : register(u0); [numthreads(1,1,1)] void main() {{ b[0] = {i + 1000}; }}", "main", "cs_5_0");
            css.Add(PsoDb.Hex(SHA1.HashData(cs)));
            PsoDb.WriteBlob(db, css[i], cs);
        }
        var keys = new List<string>();
        for (int r = 0; r < 2; r++)
            foreach (var cs in css)
            {
                var c = PsoDb.Compute(rsShas[r], cs);
                PsoDb.Write(db, 'C', c);
                keys.Add(new PsoDb.Rec('C', c).Key);
            }
        return (db.ToArray(), [.. keys]);
    }

    /// <summary>Items that crash the driver are skipped by every warm on the driver they crashed; the recompile on a new driver
    /// clears them (one retry each), and one that crashes again is kept for that driver.</summary>
    [Fact]
    public async Task Items_that_crash_the_driver_get_one_retry_on_a_new_driver()
    {
        var warmer = new FakeWarmer { Crashed = ["aa"] };
        async Task<ScsKiller> Compile(string driver)
        {
            var k = Killer(new FakeReader(Unreal), warmer: warmer, driver: driver);
            await k.ScanAsync(default);
            await WarmOnce(k, _game.Id);
            return k;
        }
        string Kept(ScsKiller k) => k.Store.LoadGame(_game.Id) is var r ? string.Join(",", r.CrashKeys.Order()) + "@" + r.CrashKeysDriver : "";

        var k = await Compile("100.01");
        Assert.Null(warmer.Options!.SkipKeys);
        Assert.Equal("aa@100.01", Kept(k));
        await Compile("100.01");
        Assert.Equal(["aa"], warmer.Options!.SkipKeys!);   // the same driver: never created again

        warmer.Crashed = null;   // the new driver compiles it
        k = await Compile("101.00");
        Assert.Null(warmer.Options!.SkipKeys);
        Assert.StartsWith("@", Kept(k));

        warmer.Crashed = ["bb"];
        k = await Compile("102.00");
        Assert.Equal("bb@102.00", Kept(k));
        await Compile("102.00");
        Assert.Equal(["bb"], warmer.Options!.SkipKeys!);
    }

    /// <summary>A gen db of <paramref name="n"/> compute PSOs of distinct shaders ('C' records, one item each) and their keys.</summary>
    static (byte[] Db, string[] Keys) ComputeDb(int n)
    {
        var db = new MemoryStream();
        var rs = Core.Planning.RootSig.Serialize(new(0, [[0, 0, 1, 1, 0, 0, 0]]), []);   // table, all stages, UAV x1 at u0
        var rsSha = PsoDb.Hex(SHA1.HashData(rs));
        PsoDb.WriteBlob(db, rsSha, rs);
        var keys = new string[n];
        for (int i = 0; i < n; i++)
        {
            var cs = Carved.Hlsl.Compile($"RWBuffer<uint> b : register(u0); [numthreads(1,1,1)] void main() {{ b[0] = {i + 1}; }}", "main", "cs_5_0");
            var csSha = PsoDb.Hex(SHA1.HashData(cs));
            PsoDb.WriteBlob(db, csSha, cs);
            var c = PsoDb.Compute(rsSha, csSha);
            PsoDb.Write(db, 'C', c);
            keys[i] = new PsoDb.Rec('C', c).Key;
        }
        return (db.ToArray(), keys);
    }

    /// <summary>A gen db with <paramref name="items"/> D3D11 items of one SM5 compute shader; <paramref name="d3d12"/> adds a
    /// D3D12 compute PSO of it.</summary>
    static byte[] GenDb(int items, bool d3d12 = false)
    {
        // RWBuffer<uint> b : register(u0); [numthreads(1,1,1)] void main() { b[0] = 318566; }  (fxc cs_5_0)
        var cs = Convert.FromBase64String(
            "RFhCQwDxuZNT6cVlRYGgmeR3nx4BAAAA7AEAAAUAAAA0AAAAxAAAANQAAADkAAAAUAEAAFJERUaIAAAAAAAAAAAAAAABAAAAPAAAAAAFU0MAAQAAXgAAAFJEMTE8AAAAGAAAACAAAAAoAAAAJAAAAAwAAAAAAAAAXAAAAAQAAAAEAAAAAQAAAP////8AAAAAAQAAAAEAAABiAE1pY3Jvc29mdCAoUikgSExTTCBTaGFkZXIgQ29tcGlsZXIgMTAuMQCrq0lTR04IAAAAAAAAAAgAAABPU0dOCAAAAAAAAAAIAAAAU0hFWGQAAABQAAUAGQAAAGoIAAGcCAAEAOARAAAAAABERAAAmwAABAEAAAABAAAAAQAAAKQAAA3y4BEAAAAAAAJAAAAAAAAAAAAAAAAAAAAAAAAAAkAAAGbcBABm3AQAZtwEAGbcBAA+AAABU1RBVJQAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAA");
        var sha = System.Security.Cryptography.SHA1.HashData(cs);
        var db = new MemoryStream();
        var w = new BinaryWriter(db);
        w.Write((byte)'B'); w.Write(20 + cs.Length); w.Write(sha); w.Write(cs);
        for (int i = 0; i < items; i++) { w.Write((byte)'1'); w.Write(24); w.Write((int)Stage.Compute); w.Write(sha); }
        if (d3d12)   // one D3D12 compute PSO of the same shader: a root signature with a u0 table, then the 'C' template
        {
            var rs = Core.Planning.RootSig.Serialize(new(0, [[0, 0, 1, 1, 0, 0, 0]]), []);   // table, all stages, UAV x1 at u0
            var rsSha = System.Security.Cryptography.SHA1.HashData(rs);
            w.Write((byte)'B'); w.Write(20 + rs.Length); w.Write(rsSha); w.Write(rs);
            var c = PsoDb.Compute(PsoDb.Hex(rsSha), PsoDb.Hex(sha));   // the whole desc (48 bytes): a 40-byte one failed to decode
            w.Write((byte)'C'); w.Write(c.Length); w.Write(c);
        }
        return db.ToArray();
    }


    /// <summary>A warm that runs until Finish(), recording its options and pause state.</summary>
    sealed class ControlledWarmer : IWarmer
    {
        public volatile ControlledRun? Run;
        public WarmOptions? Options;
        public readonly List<string> Started = [];
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            lock (Started) Started.Add(game.Id);
            Options = options;
            progress?.Report(new WarmProgress(10, 100, 0, 50));
            return Run = new ControlledRun();
        }
    }

    /// <summary>A real warmer whose last result and stage\scskiller.log are kept (the work folder is deleted after the warm).</summary>
    sealed class KeepLog(IWarmer inner) : IWarmer
    {
        public WarmResult? Result;
        public string? Log;
        public int ProgressReports;
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress) =>
            new Run(this, inner.Start(game, workDir, options, new Counted(this, progress)));

        sealed class Counted(KeepLog owner, IProgress<WarmProgress>? progress) : IProgress<WarmProgress>
        {
            public void Report(WarmProgress p) { Interlocked.Increment(ref owner.ProgressReports); progress?.Report(p); }
        }

        sealed class Run(KeepLog owner, IWarmRun run) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = run.Completion.ContinueWith(t =>
            {
                owner.Result = t.Result;
                try { owner.Log = File.ReadAllText(t.Result.LogPath); } catch (IOException) { }
                return t.Result;
            }, TaskScheduler.Default);
            public void Pause() => run.Pause();
            public void Resume() => run.Resume();
            public void Stop() => run.Stop();
        }
    }

    sealed class ControlledRun : IWarmRun
    {
        readonly TaskCompletionSource<WarmResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Paused, Stopped;
        public Task<WarmResult> Completion => _done.Task;
        public void Pause() => Paused = true;
        public void Resume() => Paused = false;
        public void Stop()
        {
            Stopped = true;
            _done.TrySetResult(new WarmResult(WarmOutcome.Stopped, 10, 100, 0, TimeSpan.FromSeconds(1), 0, "", null));
        }
        public void Finish() => _done.TrySetResult(new WarmResult(WarmOutcome.Completed, 100, 100, 0, TimeSpan.FromSeconds(1), 0, "", null));
    }

    [Fact]
    public async Task The_server_stutter_list_replaces_the_cached_one_after_a_scan_daily_or_on_a_forced_rescan()
    {
        static string List(string name) =>
            $$"""{"games":[{"name":"{{name}}","ids":[],"severity":"severe","reason":"r","source":"{{Core.Games.StutterList.OwnMeasurement}}","date":"2026-01-01"}]}""";
        var cache = Path.Combine(_root, "data", "known-stutter.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, List("Fake Game"));
        File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddDays(-2));
        var served = List("Other Game");
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath.EndsWith("known-stutter.json")
            ? CommunityTests.Ours(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(served)) : CommunityTests.Ours(HttpStatusCode.NotFound));
        try
        {
            var k = Killer();
            await k.ScanAsync(default);   // no routes (the CLI): the cached copy, over the embedded one
            Assert.NotNull(k.Games.Single().KnownStutter);

            k.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
            var changed = new List<GameState>();
            k.GameChanged += s => { lock (changed) changed.Add(s); };
            await k.ScanAsync(default);
            await k.StutterUpdate;
            Assert.Null(k.Games.Single().KnownStutter);   // the server's copy, without another scan
            Assert.Null(changed[^1].KnownStutter);
            Assert.Equal(served, File.ReadAllText(cache));
            Assert.Equal(2, fake.Log.Count);   // both lists

            served = List("Fake Game");
            await k.ScanAsync(default);
            await k.StutterUpdate;
            Assert.Equal(2, fake.Log.Count);   // checked less than a day ago
            await k.RescanAsync(default);
            await k.StutterUpdate;
            Assert.Equal(4, fake.Log.Count);
            Assert.NotNull(k.Games.Single().KnownStutter);
        }
        finally { Core.Games.StutterList.Current = Core.Games.StutterList.Embedded; }
    }

    /// <summary>A scan keeps the planner's check; an engine the server's list confirms afterwards loses its note without a rescan.</summary>
    [Fact]
    public async Task The_server_confirmed_engines_list_drops_the_not_tested_note_after_a_scan()
    {
        const string served = """{"engines":[{"version":"5.4","fork":"GAME_OnlyThisTest","game":"Some Game","evidence":"its recording"}]}""";
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath.EndsWith("confirmed-engines.json")
            ? CommunityTests.Ours(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(served)) : CommunityTests.Ours(HttpStatusCode.NotFound));
        try
        {
            var k = Killer(new FakeReader(Unreal with { Version = "5.4", Fork = "GAME_OnlyThisTest" }), new Planner());
            await k.ScanAsync(default);
            Assert.Equal((GameStatus.Ready, Planner.Untested), (k.Games.Single().Status, k.Games.Single().StatusReason));

            k.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
            await k.ScanAsync(default);
            await k.StutterUpdate;
            Assert.Equal((GameStatus.Ready, Planner.NoRecording), (k.Games.Single().Status, k.Games.Single().StatusReason));
            Assert.Equal(served, File.ReadAllText(Path.Combine(_root, "data", "confirmed-engines.json")));

            Core.Planning.ConfirmedEngines.Current = Core.Planning.ConfirmedEngines.Embedded;
            var next = Killer(new FakeReader(Unreal with { Version = "5.4", Fork = "GAME_OnlyThisTest" }), new Planner());   // the next start: the cached copy
            await next.ScanAsync(default);
            Assert.Equal(Planner.NoRecording, next.Games.Single().StatusReason);
        }
        finally { Core.Planning.ConfirmedEngines.Current = Core.Planning.ConfirmedEngines.Embedded; }
    }

    [Fact]
    public async Task A_scan_sends_the_daily_check_once_and_registers_no_sharing_device()
    {
        var fake = new CommunityTests.Fake(_ => CommunityTests.Ours(HttpStatusCode.NoContent));
        var routes = new RouteFailover(fake, [new("https://api.test.com/")]);
        var k = Killer();
        await k.ScanAsync(default);
        await k.ActiveCheckSent;
        Assert.Empty(fake.Log);   // the command line sets none

        k.Settings = k.Settings with { ShareRecordings = true };
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.Settings.ShareRecordings, routes);
        k.ActiveCheck = new ActiveCheck(Path.Combine(_root, "data"), () => k.Settings.ActiveCheck, k.Vendor.Vendor, AppVersion.Parse("1.2.3"), routes);
        for (var i = 0; i < 2; i++)
        {
            await k.ScanAsync(default);
            await k.ActiveCheckSent;
            await k.SharingPass;
        }
        Assert.Equal(["POST https://api.test.com/v1/active"], fake.Log);   // nothing to upload: no device

        k.Settings = k.Settings with { ActiveCheck = false };
        File.Delete(Path.Combine(_root, "data", "active-check.txt"));
        await k.RescanAsync(default);
        await k.ActiveCheckSent;
        Assert.Single(fake.Log);

        // the app's wiring: nothing until the welcome dialog is closed, then at once
        k.ActiveCheck = new ActiveCheck(Path.Combine(_root, "data"), () => k.Settings is { ActiveCheck: true, WelcomeSeen: true }, k.Vendor.Vendor,
            AppVersion.Parse("1.2.3"), routes);
        k.Settings = k.Settings with { ActiveCheck = true, WelcomeSeen = false };
        await k.ScanAsync(default);
        await k.ActiveCheckSent;
        Assert.Single(fake.Log);
        k.Settings = k.Settings with { WelcomeSeen = true };
        await k.ActiveCheckSent;
        Assert.Equal(2, fake.Log.Count);
    }

    [Fact]
    public async Task A_recording_is_shared_once_its_build_is_indexed_with_the_store_build_key()
    {
        const string hash = "00112233445566778899aabbccddeeff00112233";
        var game = _game with { Version = "42" };
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());   // the recorder's output
        var uploads = new List<string>();
        var fake = new CommunityTests.Fake(r =>
        {
            if (r.RequestUri!.AbsolutePath == "/v1/devices")
                return CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray());
            lock (uploads) uploads.Add(System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(r.Headers.GetValues("X-SCSK-Upload").Single())));
            return CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray());
        });
        var k = Killer(new FakeReader(Unreal, hash), game: game);
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.Settings.ShareRecordings,
            new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));

        await k.ScanAsync(default);   // imports the recording; sharing is off
        await k.SharingPass;
        k.Settings = k.Settings with { ShareRecordings = true };   // on: a pass, but no content hash of this build yet
        await k.SharingPass;
        Assert.Empty(fake.Log);

        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        await k.SharingPass;   // after the compile's index
        Assert.Contains("\"store_build_key\":\"test:fake@42\"", Assert.Single(uploads));
        Assert.Contains($"\"content_hash\":\"{hash}\"", uploads[0]);
        Assert.NotNull(k.Games.Single().RecordingSharedAt);

        await k.ScanAsync(default);
        await k.SharingPass;
        Assert.Single(uploads);   // unchanged: not again
    }

    [Fact]
    public async Task A_recording_indexed_by_another_process_is_shared_when_the_game_exits()
    {
        const string hash = "00112233445566778899aabbccddeeff00112233";
        var game = _game with { Version = "42" };
        File.WriteAllBytes(Path.Combine(_exeDir, "scskiller.db"), SharingTests.LocalRecording());
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath == "/v1/devices"
            ? CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_anon","device_id":"d"}"""u8.ToArray())
            : CommunityTests.Ours(HttpStatusCode.Accepted, """{"upload_id":"u","records":1,"new_records":1}"""u8.ToArray()));
        int uploads() { lock (fake.Log) return fake.Log.Count(l => l.Contains("/v1/upload")); }
        var k = Killer(new FakeReader(Unreal, hash), game: game);
        k.Settings = k.Settings with { ShareRecordings = true };
        k.Sharing = new Sharing(Path.Combine(_root, "data"), () => k.Settings.ShareRecordings,
            new RouteFailover(fake, [new("https://api.test.com/"), new("https://api.test.io/")]));
        await k.ScanAsync(default);   // imports the recording: no index of this build yet
        await k.SharingPass;
        Assert.Equal(0, uploads());

        var rec = k.Store.LoadGame(game.Id);   // the command line's compile indexes it; the recording stays the same
        (rec.IndexContentHash, rec.IndexGameVersion) = (hash, "42");
        k.Store.SaveGame(game.Id, rec);
        var (_, running, _) = Watched(k);
        running.Add("Fake-Win64-Shipping.exe");   // played: nothing new recorded
        k.PollGames();
        running.Clear();
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        await k.SharingPass;
        Assert.Equal(1, uploads());
    }

    sealed class FakeSource(Game[] games) : IGameSource
    {
        public Game[] Games { get; set; } = games;
        public Store Store => Store.Other;
        public IReadOnlyList<Game> Discover() => Games;
    }

    sealed class FakeVendor(GpuInfo gpu, bool perStage = false) : IGpuVendorBackend
    {
        public GpuVendor Vendor => gpu.Vendor;
        public GpuInfo Gpu => gpu;
        public VendorCaps Caps => new("fake-1", true, true, false, perStage);
        public CacheUsage GetCacheUsage() => new("", 0, true);
        public CacheLimit? GetCacheLimit() => null;
        public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_hash_only_recording_is_rehydrated_into_the_work_folder_before_materializing()
    {
        var vs = "vertex shader bytes"u8.ToArray();
        var vsSha = Convert.ToHexStringLower(SHA1.HashData(vs));
        var planner = new FakePlanner();
        var k = Killer(new BlobReader(new EngineInfo("Unreal", "4.26", null, "D3D12", false, null), new() { [vsSha] = vs }), planner);
        await k.ScanAsync(default);
        var recording = Path.Combine(k.Store.GameDir(_game.Id), "recording.db");
        Directory.CreateDirectory(Path.GetDirectoryName(recording)!);
        using (var f = File.Create(recording)) // what a shared plan carries: the PSOs, no shader bytes
            PsoDb.Write(f, 'S', PsoDb.Stream(PsoDb.Zero, new Dictionary<int, string> { [(int)Stage.Vertex] = vsSha }, [], 3, [], 0));

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        Assert.Equal(Path.Combine(k.Store.GameDir(_game.Id), "work", "recording.db"), planner.MaterializedWith?.DbPath);
        Assert.Equal(0, planner.UnresolvedAtMaterialize);
        Assert.Equal([vsSha], SCSKiller.Tests.Planning.RehydrateTests.Unresolved(recording)); // the imported recording stays as it was
    }

    [Fact]
    public async Task A_community_recording_is_downloaded_after_the_scan_and_feeds_the_planner_like_a_local_one()
    {
        var vs = "vertex shader bytes"u8.ToArray();
        var vsSha = Convert.ToHexStringLower(SHA1.HashData(vs));
        var raw = CommunityTests.HashOnly(vsSha);   // synthetic: one root signature, one pipeline
        var obj = CommunityTests.Brotli(raw);
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var manifest = CommunityTests.Manifest(CommunityTests.Alias($"{game.Id}@100", content), CommunityTests.Entry(content, obj, 1));
        var fake = new CommunityTests.Fake(r => CommunityTests.Ours(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.StartsWith("/v1/o/") ? obj : manifest));
        var planner = new NeedsRecordingPlanner();
        var k = KillerWith(planner);   // on by default

        Assert.Equal(GameStatus.NeedsRecording, (await k.ScanAsync(default)).Single().Status);   // the scan itself stays local
        await k.CommunitySync;
        var s = k.Games.Single();
        Assert.Equal(GameStatus.Ready, s.Status);
        Assert.Equal(new CommunityInfo(1, s.Community!.DownloadedAt, WithLocalRecording: false), s.Community);

        k.Enqueue(game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueStage.Done, k.Queue.Single().Stage);
        var vsBlob = new PsoDb.Rec('B', [.. SHA1.HashData(vs), .. vs]).Key;   // rehydrated from the install before planning
        Assert.Equal(PsoDb.Read(new MemoryStream(raw)).Select(r => r.Key).Append(vsBlob).Order(), planner.BuiltWith!.Order());
        Assert.Equal(0, planner.Inner.UnresolvedAtMaterialize);
        Assert.False(File.Exists(Path.Combine(k.Store.GameDir(game.Id), "recording.all.db")));   // merged in the work folder only
        Assert.Equal(1, fake.Log.Count(l => l.Contains("/v1/o/")));   // downloaded once

        k.Store.SaveSettings(k.Settings with { UseCommunityDb = false });   // off: what was downloaded isn't used either
        Assert.Equal(GameStatus.NeedsRecording, (await KillerWith(planner).ScanAsync(default)).Single().Status);

        ScsKiller KillerWith(IPlanner p)
        {
            var killer = Killer(new BlobReader(Unreal, new() { [vsSha] = vs }), p, game: game);
            killer.Community = new Community(killer.Store.DataDir, (_, _) => Task.FromResult<string?>("token"), new RouteFailover(fake, [new("https://api.test.com/")]));
            return killer;
        }
    }

    [Fact]
    public async Task A_community_recording_for_a_compiled_game_asks_for_a_compile_of_what_it_adds()
    {
        var raw = CommunityTests.HashOnly(new string('a', 40));
        var obj = CommunityTests.Brotli(raw);
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var manifest = CommunityTests.Manifest(CommunityTests.Alias($"{game.Id}@100", content), CommunityTests.Entry(content, obj, 1));
        var fake = new CommunityTests.Fake(r => CommunityTests.Ours(HttpStatusCode.OK, r.RequestUri!.AbsolutePath.StartsWith("/v1/o/") ? obj : manifest));
        var k = await Warmed(game);
        k.Community = new Community(k.Store.DataDir, (_, _) => Task.FromResult<string?>("token"), new RouteFailover(fake, [new("https://api.test.com/")]));

        k.StartCommunitySync();
        await k.CommunitySync;
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, "1 new pipeline recorded; compile again to include them", 1L), (s.Status, s.StatusReason, s.RecordedSinceWarm));
        Assert.NotNull(NewShaders.Key(s, Gpu.DriverVersion));

        using (var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append))   // then played: recorded here too
            foreach (var r in PsoDb.Read(new MemoryStream(raw))) PsoDb.Write(f, r.Tag, r.Payload);
        k.RefreshGame(game.Id);
        Assert.Equal(1, k.Games.Single().RecordedSinceWarm);
    }

    [Fact]
    public async Task Signed_out_the_public_manifest_says_a_game_is_in_the_community_database_and_nothing_else_is_asked()
    {
        const string content = "0123456789abcdef0123456789abcdef01234567";
        var game = _game with { Version = "100" };
        var manifest = CommunityTests.Manifest(CommunityTests.Alias($"{game.Id}@100", content), CommunityTests.Entry(content, "object"u8.ToArray(), 18_406));
        var fake = new CommunityTests.Fake(_ => CommunityTests.Ours(HttpStatusCode.OK, manifest));
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), game: game);
        k.Community = new Community(k.Store.DataDir, (_, _) => Task.FromResult<string?>(null), new RouteFailover(fake, [new("https://api.test.com/")]));   // no "db"

        Assert.Null((await k.ScanAsync(default)).Single().InCommunityDb);   // no copy of the manifest yet
        await k.CommunitySync;
        var s = k.Games.Single();   // re-evaluated by the manifest check alone: nothing was downloaded
        Assert.Equal((GameStatus.NeedsRecording, true, 18_406, (CommunityInfo?)null), (s.Status, s.InCommunityDb, s.CommunityDbPsos, s.Community));
        Assert.Equal("needs one short recording; " + ScsKiller.InDbNote, s.StatusReason);
        Assert.Equal(["GET https://api.test.com/v1/manifest/0"], fake.Log);   // no object, no token, nothing about the game
        Assert.False(File.Exists(Path.Combine(k.Store.GameDir(game.Id), "community.db")));
    }

    [Fact]
    public async Task A_game_is_matched_to_the_manifest_by_its_index_only_while_that_index_is_of_the_installed_build()
    {
        const string indexed = "0123456789abcdef0123456789abcdef01234567", published = "fedcba9876543210fedcba9876543210fedcba98";
        var game = _game with { Version = "101" };
        var k = Killer(new FakeReader(Unreal), new NeedsRecordingPlanner(), game: game);
        await k.ScanAsync(default);
        Directory.CreateDirectory(Path.Combine(k.Store.DataDir, "community"));
        File.WriteAllBytes(Path.Combine(k.Store.DataDir, "community", "manifest.bin"), CommunityTests.Manifest(
            CommunityTests.Alias($"{game.Id}@101", published), CommunityTests.Entry(published, "a"u8.ToArray(), 7)));
        var rec = k.Store.LoadGame(game.Id);
        (rec.IndexContentHash, rec.IndexGameVersion) = (indexed, "100");   // indexed before the game's update
        k.Store.SaveGame(game.Id, rec);

        k.RefreshGame(game.Id);
        Assert.Equal((true, 7), (k.Games.Single().InCommunityDb, k.Games.Single().CommunityDbPsos));   // by the store build, not the old index

        File.WriteAllBytes(Path.Combine(k.Store.DataDir, "community", "manifest.bin"), CommunityTests.Manifest(
            CommunityTests.Alias($"{game.Id}@101", published), CommunityTests.Entry(published, "a"u8.ToArray(), 7), CommunityTests.Entry(indexed, "b"u8.ToArray(), 5)));
        k.RefreshGame(game.Id);
        Assert.Equal(7, k.Games.Single().CommunityDbPsos);   // the old build's entry isn't this one's
        rec.IndexGameVersion = "101";
        k.Store.SaveGame(game.Id, rec);
        k.RefreshGame(game.Id);
        Assert.Equal(5, k.Games.Single().CommunityDbPsos);   // an index of the installed build: its own content hash
    }

    /// <summary>A game that needs a recording (every D3D12 game on AMD); captures what Build planned from.</summary>
    sealed class NeedsRecordingPlanner : IPlanner
    {
        public readonly FakePlanner Inner = new();
        public List<string>? BuiltWith;
        public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps) =>
            recording == null ? new(Readiness.NeedsRecording, "needs one short recording") : new(Readiness.Ready, "planned from a recording");
        public Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false)
        {
            BuiltWith = recording == null ? null : PsoDb.Read(recording.DbPath).Select(r => r.Key).ToList();
            return Inner.Build(game, engine, index, recording, caps, outDir, log, ct, maximum);
        }
        public void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct) =>
            Inner.Materialize(plan, game, engine, reader, recording, workDir, ct);
    }

    /// <param name="indexed">the index lists <paramref name="blobs"/> (else it's empty and they come only from ReadShaders)</param>
    sealed class BlobReader(EngineInfo engine, Dictionary<string, byte[]> blobs, bool indexed = false, string content = "content-1") : IEngineReader
    {
        public EngineInfo? Detect(Game game) => engine;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) =>
            new(content, ["PCD3D_SM6"], indexed ? blobs.Keys.ToDictionary(h => h, _ => (ShaderInfo)null!) : new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var (h, b) in blobs) if (sha1s.Contains(h)) sink(h, b);
        }
    }

    sealed class FakeReader(EngineInfo? engine, string content = "content-1", int shaders = 3000) : IEngineReader
    {
        public int Detects;
        public EngineInfo? Detect(Game game) { Interlocked.Increment(ref Detects); return engine; }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) =>
            new(content, ["PCD3D_SM6"], Enumerable.Range(0, shaders).ToDictionary(i => $"{i:x40}", i => (ShaderInfo)null!), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>An Unreal game per id, with the graphics API given for it (default D3D12).</summary>
    sealed class ApiReader(Dictionary<string, string> apis) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Unreal with { GraphicsApi = apis.GetValueOrDefault(game.Id, "D3D12") };
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary><paramref name="records"/>: a real plan.bin with these records (read at each build); else a stand-in file.</summary>
    sealed class FakePlanner(byte[]? genDb = null, long skipped = 0, PlanStats? stats = null, List<PsoDb.Rec>? records = null, byte[]? mainDb = null) : IPlanner
    {
        public PlanCheck Check(Game game, EngineInfo engine, Recording? recording, VendorCaps caps) => new(Readiness.Ready, "synthesized templates");
        public Plan Build(Game game, EngineInfo engine, ShaderIndex index, Recording? recording, VendorCaps caps, string outDir, IProgress<string>? log, CancellationToken ct, bool maximum = false)
        {
            var file = Path.Combine(outDir, "plan.bin");
            Directory.CreateDirectory(outDir);
            var plan = new Plan(game.Id, index.ContentHash, "PCD3D_SM6", caps.Profile, stats ?? new PlanStats(0, 10000, 5, 7, true), file);
            if (records != null) PlanFile.Write(plan, records);
            else File.WriteAllText(file, "plan");
            return plan;
        }
        public Recording? MaterializedWith;
        public int UnresolvedAtMaterialize = -1;
        public void Materialize(Plan plan, Game game, EngineInfo engine, IEngineReader reader, Recording? recording, string workDir, CancellationToken ct)
        {
            (MaterializedWith, UnresolvedAtMaterialize) = (recording, recording == null ? -1 : SCSKiller.Tests.Planning.RehydrateTests.Unresolved(recording.DbPath).Count);
            Directory.CreateDirectory(workDir);
            File.WriteAllBytes(Path.Combine(workDir, "scskiller_gen.db"), genDb ?? "gen"u8.ToArray());
            if (mainDb != null) File.WriteAllBytes(Path.Combine(workDir, "scskiller.db"), mainDb);
            if (skipped > 0) File.WriteAllText(Path.Combine(workDir, Planner.SkippedFile), skipped.ToString()); // like Planner.Materialize
        }
    }

    /// <summary>Completes at once. <paramref name="whileRunning"/> plays the warm's side effects (e.g. the driver opening
    /// cache files); what it returns is disposed when the run ends.</summary>
    sealed class FakeWarmer(Func<IDisposable?>? whileRunning = null, long failed = 0) : IWarmer
    {
        public bool SawMaterializedWork;
        public readonly List<string> Started = [], Staged = [];   // game ids; the exe file names the warms staged
        public WarmOptions? Options;
        public IReadOnlyCollection<string>? Crashed;   // the result's keys of items that crash the driver
        public int Passes;   // the work folder's careful passes (0 = none)
        public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress)
        {
            lock (Started) { Started.Add(game.Id); Staged.Add(Path.GetFileName(game.ExePath)); Options = options; }
            SawMaterializedWork = File.Exists(Path.Combine(workDir, "scskiller_gen.db"));
            Passes = WarmPasses.Read(workDir)?.Count ?? 0;
            var held = whileRunning?.Invoke();   // what the staged warm holds open while it runs: attribution samples it then
            progress?.Report(new WarmProgress(5000, 10000, 0, 1000));
            var result = new WarmResult(WarmOutcome.Completed, 10000, 10000, failed, TimeSpan.FromSeconds(10), 20000 * 1024, "", null, Crashed: Crashed);
            return new Run(held == null ? Task.FromResult(result) : Task.Run(async () => { await Task.Delay(1000); held.Dispose(); return result; }));
        }
        sealed class Run(Task<WarmResult> completion) : IWarmRun
        {
            public Task<WarmResult> Completion { get; } = completion;
            public void Pause() { }
            public void Resume() { }
            public void Stop() { }
        }
    }
}
