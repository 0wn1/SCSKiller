using System.Buffers.Binary;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

public sealed class ManualGamesTests : IDisposable
{
    readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-manual-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>A minimal x64 exe; <paramref name="imports"/>: the one DLL its import table names (a graphics API makes it the game).</summary>
    internal static byte[] Exe(string? imports = null, int padding = 0)
    {
        var pe = Planning.MiddlewarePackTests.Pe(null);
        if (imports != null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x58 + 120), 0x1100);          // import directory RVA
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x200 + 0x100 + 12), 0x1180);  // its one descriptor's Name RVA
            System.Text.Encoding.ASCII.GetBytes(imports).CopyTo(pe, 0x200 + 0x180);
        }
        return [.. pe, .. new byte[padding]];
    }

    /// <summary>&lt;root&gt;\Game.exe (Unreal's launcher stub), Game\Binaries\Win64\Game-Win64-Shipping.exe, Engine\.</summary>
    internal static (string Root, string Stub, string Shipping) UnrealLayout(string root)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "Binaries", "Win64")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(root, "Engine", "Binaries", "Win64", "CrashReportClient.exe"), Exe(padding: 20_000));
        var shipping = Path.Combine(bin, "Game-Win64-Shipping.exe");
        File.WriteAllBytes(shipping, Exe(padding: 8192));
        var stub = Path.Combine(root, "Game.exe");
        File.WriteAllBytes(stub, Exe());
        return (root, stub, shipping);
    }

    [Fact]
    public void An_unreal_launcher_stub_resolves_to_its_shipping_exe_and_install_root()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        Assert.Equal(new ManualEntry(shipping, root, "Some Game"), ManualSource.Resolve(stub));
        Assert.Equal(new ManualEntry(shipping, root, "Some Game"), ManualSource.Resolve(shipping));   // the root from <Project>\Binaries\Win64
        Assert.Equal(ManualSource.IdOf(shipping), ManualSource.IdOf(shipping.ToUpperInvariant()));
    }

    [Fact]
    public void A_launcher_resolves_to_the_one_exe_that_loads_d3d_and_several_are_ambiguous()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Plain")).FullName;
        var launcher = Path.Combine(dir, "Launcher.exe");
        File.WriteAllBytes(launcher, Exe(padding: 50_000));   // the largest, importing nothing graphic
        File.WriteAllBytes(Path.Combine(dir, "game_dx12.exe"), Exe("d3d12.dll"));
        Assert.Equal(Path.Combine(dir, "game_dx12.exe"), ManualSource.Resolve(launcher).Exe);
        Assert.Equal(Path.Combine(dir, "game_dx12.exe"), ManualSource.Resolve(Path.Combine(dir, "game_dx12.exe")).Exe);   // the game itself: kept

        File.WriteAllBytes(Path.Combine(dir, "game_dx11.exe"), Exe("d3d11.dll"));
        var e = Assert.Throws<ArgumentException>(() => ManualSource.Resolve(launcher));
        Assert.Contains("can't tell which", e.Message);
        Assert.Contains("game_dx11.exe", e.Message);
    }

    [Fact]
    public void Anti_cheat_above_the_exe_makes_that_folder_the_install()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "Shooter")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "EasyAntiCheat"));
        var exe = Path.Combine(Directory.CreateDirectory(Path.Combine(game, "bin64", "retail")).FullName, "shooter.exe");
        File.WriteAllBytes(exe, Exe("d3d12.dll"));
        var e = ManualSource.Resolve(exe);
        Assert.Equal((exe, game), (e.Exe, e.InstallDir));
        Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(ManualSource.ToGame(e)));

        // anti-cheat beside the exe: found where it is, the install doesn't widen to the library folder
        var souls = Directory.CreateDirectory(Path.Combine(_root, "Souls", "Game")).FullName;
        Directory.CreateDirectory(Path.Combine(souls, "EasyAntiCheat"));
        File.WriteAllBytes(Path.Combine(souls, "souls.exe"), Exe("d3d12.dll"));
        Assert.Equal(souls, ManualSource.Resolve(Path.Combine(souls, "souls.exe")).InstallDir);
    }

    [Fact]
    public void Picks_that_are_no_64_bit_program_are_refused()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Bad")).FullName;
        string Put(string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
        Assert.Contains("isn't a program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("game.lnk", Exe()))).Message);
        Assert.Contains("isn't a Windows program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("text.exe", "hello"u8.ToArray()))).Message);
        Assert.Contains("doesn't exist", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Path.Combine(dir, "gone.exe"))).Message);
        var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe");
        Assert.Contains("32-bit", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("old.exe", File.ReadAllBytes(wow)))).Message);
    }

    [Fact]
    public void The_exe_a_pick_resolves_to_is_checked_too()
    {
        var (_, stub, shipping) = UnrealLayout(Path.Combine(_root, "Broken"));
        File.WriteAllBytes(shipping, new byte[50_000]);   // still the largest exe under Binaries\Win64
        Assert.Contains("Game-Win64-Shipping.exe isn't a Windows program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(stub)).Message);
    }

    [Fact]
    public void Malformed_entries_are_left_out_and_the_rest_kept()
    {
        var (root, _, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var good = System.Text.Json.JsonSerializer.Serialize(new ManualEntry(shipping, root, "Some Game"));
        File.WriteAllText(Path.Combine(data, "manual-games.json"),
            """[null, 7, "x", {}, {"Exe": "relative.exe", "InstallDir": "dir"}, {"Exe": "C:/a\u0000b.exe", "InstallDir": "C:/a"}, """ + good + "]");
        var source = new ManualSource(new AppStore(data));
        Assert.Equal(shipping, Assert.Single(source.Discover()).ExePath);

        var other = UnrealLayout(Path.Combine(_root, "Other Game"));
        Assert.False(source.Add(new ManualEntry(other.Shipping, other.Root, "Other Game")).Existed);
        Assert.Equal(2, new ManualSource(new AppStore(data)).Discover().Count);
    }

    [Fact]
    public void Adds_from_two_sources_at_once_lose_nothing()
    {
        var data = Path.Combine(_root, "data");
        ManualSource a = new(new AppStore(data)), b = new(new AppStore(data));
        Parallel.For(0, 40, i => (i % 2 == 0 ? a : b).Add(new ManualEntry(Path.Combine(_root, $"g{i}", "g.exe"), Path.Combine(_root, $"g{i}"), $"g{i}")));
        Assert.Equal(40, a.Entries().Count);
        Parallel.For(0, 40, i => Assert.True((i % 2 == 0 ? b : a).Remove(ManualSource.IdOf(Path.Combine(_root, $"g{i}", "g.exe")))));
        Assert.Empty(b.Entries());
    }

    [Fact]
    public void Added_games_persist_in_the_data_folder_and_are_removed_by_id()
    {
        var (root, _, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        var data = Path.Combine(_root, "data");
        var entry = new ManualEntry(shipping, root, "Some Game");
        var (added, existed) = new ManualSource(new AppStore(data)).Add(entry);
        Assert.False(existed);
        Assert.True(File.Exists(Path.Combine(data, "manual-games.json")));

        var source = new ManualSource(new AppStore(data));   // as the next start reads it
        var g = Assert.Single(source.Discover());
        Assert.Equal(new Game(added.Id, "Some Game", Store.Manual, root, shipping), g);
        Assert.StartsWith("manual:", g.Id);
        Assert.True(source.Add(entry).Existed);

        File.Move(shipping, shipping + ".away");   // a drive unplugged: not listed, still kept
        Assert.Empty(source.Discover());
        File.Move(shipping + ".away", shipping);
        Assert.Single(source.Discover());

        Assert.True(source.Remove(g.Id));
        Assert.Empty(new ManualSource(new AppStore(data)).Discover());
        Assert.False(source.Remove(g.Id));
        Assert.True(File.Exists(shipping));
    }
}
