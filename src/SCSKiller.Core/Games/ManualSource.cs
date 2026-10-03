using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

/// <summary>A game the user added by its exe: <see cref="Exe"/> is the resolved D3D12 process (<see cref="ManualSource.Resolve"/>).</summary>
public sealed record ManualEntry(string Exe, string InstallDir, string Name);

/// <summary>Games the user added (manual-games.json in the data folder). Id "manual:" + a hash of the exe's full path, so
/// the same exe added again is the same game. No build id: a changed exe (size, write time) is what marks a patch, as for
/// the stores that give none. An entry whose exe is missing (a drive unplugged) is kept and listed again once it's back.</summary>
public sealed class ManualSource(AppStore store) : IGameSource
{
    // the app, the CLI and the scheduled task may change the list at once: one read-modify-write at a time per data folder
    readonly string _mutex = @"Local\SCSKiller.manual-games." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(store.DataDir).ToUpperInvariant())))[..16];

    public Store Store => Store.Manual;

    public IReadOnlyList<Game> Discover() => Entries().Where(e => File.Exists(e.Exe)).Select(ToGame).ToList();

    /// <summary>The well-formed entries: an entry with no exe or install folder, or a path that isn't a full one, is left out.</summary>
    public List<ManualEntry> Entries() => store.LoadManualGames().Where(Valid).ToList();

    static bool Valid(ManualEntry e) =>
        e is { Exe.Length: > 0, InstallDir.Length: > 0 } && e.Exe.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(e.Exe)
        && e.InstallDir.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(e.InstallDir);

    public static Game ToGame(ManualEntry e) =>
        new(IdOf(e.Exe), e.Name is { Length: > 0 } n ? n : Path.GetFileName(Path.TrimEndingDirectorySeparator(e.InstallDir)), Store.Manual, e.InstallDir, e.Exe);

    public static string IdOf(string exe) =>
        "manual:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant())))[..16];

    /// <summary>The game and whether it was already added.</summary>
    public (Game Game, bool Existed) Add(ManualEntry entry) => Locked(() =>
    {
        var list = Entries();
        var id = IdOf(entry.Exe);
        if (list.FirstOrDefault(e => IdOf(e.Exe) == id) is { } had) return (ToGame(had), true);
        list.Add(entry);
        store.SaveManualGames(list);
        return (ToGame(entry), false);
    });

    public bool Remove(string gameId) => Locked(() =>
    {
        var list = Entries();
        if (list.RemoveAll(e => IdOf(e.Exe) == gameId) == 0) return false;
        store.SaveManualGames(list);
        return true;
    });

    T Locked<T>(Func<T> change)
    {
        using var m = new Mutex(false, _mutex);
        try { m.WaitOne(); }
        catch (AbandonedMutexException) { }   // its holder died: the file was replaced whole or not at all (WriteAtomic)
        try { return change(); }
        finally { m.ReleaseMutex(); }
    }

    /// <summary>The game an exe the user picked belongs to: its install root (Unreal's, above Engine\ and
    /// &lt;Project&gt;\Binaries; REDengine's, above bin\x64), the process that creates the D3D12 device found as for the
    /// stores (<see cref="GameFiles.FindExe"/>: a launcher stub becomes its Shipping exe), and a name. ArgumentException
    /// with the message to show for a pick that is no 64-bit program, sits at a drive's root, or launches one of several
    /// exes SCSKiller can't tell apart.</summary>
    public static ManualEntry Resolve(string picked)
    {
        var pick = Path.GetFullPath(picked);
        var file = Path.GetFileName(pick);
        if (!pick.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{file} isn't a program: pick the game's .exe file.");
        if (!File.Exists(pick)) throw new ArgumentException($"{pick} doesn't exist.");
        CheckX64(pick, file);
        var root = InstallRoot(pick);
        if (Path.GetPathRoot(root) == root) throw new ArgumentException($"{file} is at the root of a drive: SCSKiller needs the game in a folder of its own.");
        // as discovery: no import table is read in an install with anti-cheat
        var clean = GameFiles.DetectAntiCheat(new Game("", "", Store.Manual, root, pick)) == AntiCheat.None;
        var graphics = clean ? GameFiles.ImportsGraphics(pick) : null;
        var exe = graphics == true ? pick : GameFiles.FindExe(root, Path.GetRelativePath(root, pick)) ?? pick;
        if (Same(exe, pick) && graphics == false)   // a launcher FindExe kept: the one exe of the install that loads D3D, if there is one
            switch (GameFiles.GraphicsExes(root).Where(f => !Same(f, pick)).ToList())
            {
                case [var one]: exe = one; break;
                case { Count: > 1 } several:
                    throw new ArgumentException($"{file} looks like a launcher, and SCSKiller can't tell which program it starts: "
                        + string.Join(", ", several.Take(4).Select(Path.GetFileName)) + (several.Count > 4 ? "…" : "")
                        + ". Pick the one that opens the game's window.");
            }
        if (!Same(exe, pick)) CheckX64(exe, Path.GetFileName(exe));   // what is added is the game's exe, not the pick
        return new ManualEntry(exe, root, NameOf(exe, pick, root));
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static void CheckX64(string path, string file)
    {
        PEHeaders h;
        try
        {
            using var pe = PeFile.Open(path);
            h = pe.PEHeaders;
        }
        catch (BadImageFormatException) { throw new ArgumentException($"{file} isn't a Windows program."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ArgumentException($"Couldn't read {file}: {e.Message}"); }
        if (h.IsDll || h.PEHeader == null) throw new ArgumentException($"{file} isn't a Windows program.");
        if (h.CoffHeader.Machine != Machine.Amd64 || h.PEHeader.Magic != PEMagic.PE32Plus)
            throw new ArgumentException(h.CoffHeader.Machine == Machine.I386
                ? $"{file} is a 32-bit program: SCSKiller compiles 64-bit DirectX 12 games."
                : $"{file} isn't an x64 program: SCSKiller compiles 64-bit DirectX 12 games.");
    }

    /// <summary>Unreal: the folder holding Engine\ and the project (the project's own folder when Engine\ isn't beside it);
    /// bin*\ or bin\x64*\: the folder above bin. Else the exe's folder. A folder up to two above with anti-cheat among its
    /// own entries is the install, so the anti-cheat check sees the whole game whatever its layout.</summary>
    static string InstallRoot(string exe)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var root = dir;
        if (Path.GetDirectoryName(dir) is { } parent)
        {
            if (Path.GetFileName(parent).Equals("Binaries", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } project)
                root = Path.GetDirectoryName(project) is { } above && Directory.Exists(Path.Combine(above, "Engine")) ? above : project;
            else if (Path.GetFileName(parent).Equals("bin", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } game)
                root = game;
            else if (Path.GetFileName(dir).StartsWith("bin", StringComparison.OrdinalIgnoreCase)) root = parent;
        }
        var up = Path.GetDirectoryName(root);
        for (var i = 0; i < 2 && up != null && Path.GetPathRoot(up) != up; i++, up = Path.GetDirectoryName(up))
            if (GameFiles.MarkerIn(up) != AntiCheat.None) root = up;
        return root;
    }

    // Unreal's launcher stub, engine defaults and launchers name no game
    static readonly string[] Generic = ["UnrealGame", "UE4Game", "UE5Game", "Unity"];

    static string NameOf(string exe, string pick, string root)
    {
        foreach (var f in new[] { exe, pick }.Distinct())
        {
            var v = FileVersionInfo.GetVersionInfo(f);
            foreach (var n in new[] { v.ProductName, v.FileDescription })
                if (n?.Trim() is { Length: > 0 } name && !Generic.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !name.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase) && !name.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                    return name;
        }
        return Path.GetFileName(root);
    }
}
