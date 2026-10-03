using System.Diagnostics;
using System.Text.Json;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Games;

/// <summary>Install-folder heuristics shared by the game sources.</summary>
public static class GameFiles
{
    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true };

    /// <summary>The process that creates the D3D12 device: the largest exe under a Binaries\Win64 folder (Unreal; not
    /// Engine\Binaries, which only holds helpers like CrashReportClient), else <paramref name="launcherExe"/>, else the largest
    /// exe near the install root. A launcher among the last two is replaced by the game it starts (<see cref="LaunchedExe"/>).</summary>
    public static string? FindExe(string installDir, string? launcherExe = null)
    {
        if (!Directory.Exists(installDir)) return null;
        var unreal = Directory.EnumerateDirectories(installDir, "Win64", Deep)
            .Where(d => string.Equals(Path.GetFileName(Path.GetDirectoryName(d)), "Binaries", StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.exe", Flat))
            .Where(f => !IsEngineFolder(installDir, f))
            .Select(f => new FileInfo(f))
            .MaxBy(f => f.Length);
        if (unreal != null) return unreal.FullName;
        if (launcherExe != null)
        {
            var p = Path.GetFullPath(Path.Combine(installDir, launcherExe));
            if (File.Exists(p)) return RedLauncherTarget(installDir, p) ?? p;
        }
        // ponytail: non-Unreal games get a guess; Steam's real launch target lives in the binary appinfo.vdf, parse it if this misfires
        var exes = Directory.EnumerateFiles(installDir, "*.exe", Deep)
            .Where(f => !NotTheGame.Any(s => Path.GetRelativePath(installDir, f).Contains(s, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new FileInfo(f))
            .ToList();
        if (exes.FirstOrDefault(f => Directory.Exists(Path.ChangeExtension(f.FullName, null) + "_Data")) is { } unity) return unity.FullName;   // Unity: Game.exe + Game_Data
        var guess = exes.OrderBy(f => f.DirectoryName!.Length > installDir.TrimEnd('\\').Length ? 1 : 0)   // root folder first
            .ThenByDescending(f => f.Length).FirstOrDefault();
        return guess == null ? null : LaunchedExe(installDir, guess, exes);
    }

    /// <summary>The game a launcher (<paramref name="guess"/>) starts: the target of CD PROJEKT RED's launcher-configuration.json
    /// next to it, else, when the guess imports no graphics API, the one larger exe of <paramref name="exes"/> that does (no
    /// binary read in an install with anti-cheat). Anything else (one unreadable, several, none) keeps the guess.</summary>
    static string LaunchedExe(string installDir, FileInfo guess, IReadOnlyList<FileInfo> exes)
    {
        if (RedLauncherTarget(installDir, guess.FullName) is { } red) return red;
        var larger = exes.Where(f => f.Length > guess.Length).ToList();
        if (larger.Count == 0 || ImportsGraphics(guess.FullName) != false
            || DetectAntiCheat(new Game("", "", Store.Other, installDir, guess.FullName)) != AntiCheat.None) return guess.FullName;
        var imports = larger.Select(f => (f.FullName, Imports: ImportsGraphics(f.FullName))).ToList();
        return imports.All(x => x.Imports != null) && imports.Where(x => x.Imports == true).ToList() is [var game] ? game.FullName : guess.FullName;
    }

    /// <summary>launcher-configuration.json's executables[]: the entry whose description is its "fallback", else the first;
    /// null unless that exe exists inside the install.</summary>
    static string? RedLauncherTarget(string installDir, string exe)
    {
        var config = Path.Combine(Path.GetDirectoryName(exe)!, "launcher-configuration.json");
        if (!File.Exists(config)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("executables", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var fallback = r.TryGetProperty("fallback", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var entries = list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
            if (entries.Count == 0) return null;
            var pick = entries.FirstOrDefault(e => e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() == fallback,
                entries[0]);
            if (!pick.TryGetProperty("executable", out var x) || x.ValueKind != JsonValueKind.Object) return null;
            string? S(string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (S("fileName") is not { Length: > 0 } name) return null;
            var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(config)!, S("directoryPath") ?? "", name));
            var root = DirKey(installDir);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return null;
            // a junction or symlink below the install root leads out of it, as the anti-cheat scan takes it (the root itself may be one)
            for (var p = target; p.Length > root.Length; p = Path.GetDirectoryName(p)!)
                if ((p == target ? new FileInfo(p) : (FileSystemInfo)new DirectoryInfo(p)) is { LinkTarget: not null }) return null;
            return target;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // Streamline's interposer stands in for dxgi and d3d12 in games that ship it: witcher3.exe imports neither
    static readonly string[] GraphicsDlls = ["d3d12.dll", "d3d11.dll", "dxgi.dll", "sl.interposer.dll"];

    /// <summary>Null when the file isn't a readable PE.</summary>
    internal static bool? ImportsGraphics(string exe)
    {
        try { return CarvedReader.PeImports(exe, out _).Any(d => GraphicsDlls.Contains(d, StringComparer.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException) { return null; }
    }

    /// <summary>The exes near the install root (as <see cref="FindExe"/> looks) that import a graphics API.</summary>
    internal static IEnumerable<string> GraphicsExes(string installDir) => Directory.EnumerateFiles(installDir, "*.exe", Deep)
        .Where(f => !NotTheGame.Any(s => Path.GetRelativePath(installDir, f).Contains(s, StringComparison.OrdinalIgnoreCase)) && !IsEngineFolder(installDir, f))
        .Where(f => ImportsGraphics(f) == true);

    /// <summary><paramref name="path"/> is inside the folder <paramref name="dir"/> (case-insensitive, full paths).</summary>
    public static bool Inside(string dir, string path) =>
        dir.Length > 0 && Path.GetFullPath(path).StartsWith(DirKey(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>An install folder compared across sources: full path, no trailing separator (a drive root keeps its own).</summary>
    public static string DirKey(string dir) => dir.Length == 0 ? dir : Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    static readonly string[] NotTheGame = ["redist", "directx", "crash", "unins", "setup", "vconsole"];   // vconsole2.exe: Source 2's developer console

    static bool IsEngineFolder(string installDir, string path) =>
        Path.GetRelativePath(installDir, path).StartsWith("Engine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static readonly (string Name, AntiCheat Kind)[] Markers =
    [
        ("EasyAntiCheat", AntiCheat.EasyAntiCheat), ("EasyAntiCheat_EOS", AntiCheat.EasyAntiCheat), ("start_protected_game.exe", AntiCheat.EasyAntiCheat),
        ("EasyAntiCheat_EOS_Setup.exe", AntiCheat.EasyAntiCheat), ("EasyAntiCheat_Setup.exe", AntiCheat.EasyAntiCheat),
        ("BattlEye", AntiCheat.BattlEye), ("BEService.exe", AntiCheat.BattlEye), ("BEService_x64.exe", AntiCheat.BattlEye), ("BELauncher.exe", AntiCheat.BattlEye),
        ("EAAntiCheat.Installer.exe", AntiCheat.Other), ("GameGuard", AntiCheat.Other), ("XIGNCODE", AntiCheat.Other), ("nProtect", AntiCheat.Other),
        ("randgrid.sys", AntiCheat.Other),   // Ricochet (Call of Duty)
    ];

    /// <summary>The marker names as the proxy's built-in list has them (its check beside the exe): "*x" matches a name ending in x.</summary>
    public static IEnumerable<string> MarkerNames => Markers.Select(m => m.Name).Append("*_BE.exe");

    /// <summary>Looks for anti-cheat folders/files by name anywhere under the install, under the exe's folder when it is
    /// outside it, and in the names of the folders from the exe's up to the install root. A tree that can't be read whole
    /// (a folder it may not list, the root included; more than <see cref="MaxEntries"/> entries; longer than <see cref="Budget"/>) is <see cref="AntiCheat.Other"/>:
    /// not known to be clean. A junction or symlink is a name, not followed: a folder it points into inside the tree is
    /// read where it is, and one outside is another folder's; one on the way from the install root to the exe is Other. <paramref name="quick"/>: the install root's and the exe
    /// folder's own entries only, for a recheck right after a full one. Battle.net titles are marked conservatively:
    /// Blizzard's Warden is server-side, not a file the install carries. The only anti-cheat detector: engine readers and
    /// middleware detection call it to skip their own work, exe discovery to read no other binary; the app's evaluation acts on its verdict.</summary>
    public static AntiCheat DetectAntiCheat(Game game, bool quick = false, TimeSpan? budget = null)
    {
        if (game.Id.StartsWith("battlenet:", StringComparison.Ordinal)) return AntiCheat.Other;
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!;
        bool Inside(string d) => d.Equals(install, StringComparison.OrdinalIgnoreCase) || d.StartsWith(install + '\\', StringComparison.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        var clock = Stopwatch.StartNew();

        AntiCheat Walk(string root)
        {
            var dirs = new Stack<string>([root]);
            while (dirs.TryPop(out var dir))
            {
                if (!visited.Add(dir)) continue;
                if (clock.Elapsed > (budget ?? Budget)) return AntiCheat.Other;   // also between folders: empty ones queued, a slow open
                IEnumerator<FileSystemInfo> entries;   // streamed: the cap bounds memory too
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", AllNames).GetEnumerator(); }
                catch (DirectoryNotFoundException) when (dir == root) { continue; }   // nothing installed there (access denied throws otherwise)
                using var _ = entries;
                while (entries.MoveNext())
                {
                    var e = entries.Current;
                    if (++seen > MaxEntries || clock.Elapsed > (budget ?? Budget)) return AntiCheat.Other;
                    if (Marker(e.Name) is var kind and not AntiCheat.None) return kind;
                    if (!quick && e is DirectoryInfo d && ((d.Attributes & FileAttributes.ReparsePoint) == 0 || d.LinkTarget == null)) dirs.Push(d.FullName);
                }
            }
            return AntiCheat.None;
        }

        try
        {
            // the walk doesn't follow links: one between the install root and the exe would hide the exe's own folders.
            // The root itself may be one (a game folder moved to another drive): it is walked, so nothing is hidden.
            for (var d = exeDir; d != null && Inside(d) && d.Length > install.Length; d = Path.GetDirectoryName(d))
                if (new DirectoryInfo(d) is { Exists: true } info && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget != null)
                    return AntiCheat.Other;
            foreach (var root in quick || !Inside(exeDir) ? new[] { install, exeDir } : new[] { install })
                if (Walk(root) is var kind and not AntiCheat.None) return kind;
            for (var d = exeDir; d != null && Inside(d); d = Path.GetDirectoryName(d))
                if (Marker(Path.GetFileName(d)) is var kind and not AntiCheat.None) return kind;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return AntiCheat.Other; }
        return AntiCheat.None;
    }

    // AttributesToSkip defaults to Hidden | System: a hidden EasyAntiCheat folder or a system randgrid.sys still counts
    static readonly EnumerationOptions AllNames = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

    /// <summary>Entries one detection reads at most (names only: an install of 170,000 entries reads in about 0.1 s warm).</summary>
    public const int MaxEntries = 2_000_000;

    /// <summary>An anti-cheat marker among the folder's own entries (names only, not below); None when it can't be listed
    /// (the install's own check, <see cref="DetectAntiCheat"/>, treats that as anti-cheat).</summary>
    internal static AntiCheat MarkerIn(string dir)
    {
        try
        {
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", AllNames))
                if (Marker(e.Name) is var kind and not AntiCheat.None) return kind;
            return AntiCheat.None;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return AntiCheat.None; }
    }

    /// <summary>The time one detection takes at most (the slowest install measured: about 0.3 s cold).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    static AntiCheat Marker(string name)
    {
        foreach (var (marker, kind) in Markers)
            if (name.Equals(marker, StringComparison.OrdinalIgnoreCase)) return kind;
        return name.EndsWith("_BE.exe", StringComparison.OrdinalIgnoreCase) ? AntiCheat.BattlEye : AntiCheat.None;
    }
}
