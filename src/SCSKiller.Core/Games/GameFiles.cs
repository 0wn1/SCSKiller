namespace SCSKiller.Core.Games;

/// <summary>Install-folder heuristics shared by the game sources.</summary>
public static class GameFiles
{
    static readonly EnumerationOptions Deep = new() { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true };

    /// <summary>The process that creates the D3D12 device: the largest exe under a Binaries\Win64 folder (Unreal; not
    /// Engine\Binaries, which only holds helpers like CrashReportClient), else <paramref name="launcherExe"/>, else the largest exe near the install root.</summary>
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
            if (File.Exists(p)) return p;
        }
        // ponytail: non-Unreal games get a guess; Steam's real launch target lives in the binary appinfo.vdf, parse it if this misfires
        var exes = Directory.EnumerateFiles(installDir, "*.exe", Deep)
            .Where(f => !NotTheGame.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new FileInfo(f))
            .ToList();
        return (exes.FirstOrDefault(f => Directory.Exists(Path.ChangeExtension(f.FullName, null) + "_Data"))   // Unity: Game.exe + Game_Data
                ?? exes.OrderBy(f => f.DirectoryName!.Length > installDir.TrimEnd('\\').Length ? 1 : 0)       // root folder first
                    .ThenByDescending(f => f.Length).FirstOrDefault())?.FullName;
    }

    /// <summary>An install folder compared across sources: full path, no trailing separator (a drive root keeps its own).</summary>
    public static string DirKey(string dir) => dir.Length == 0 ? dir : Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    static readonly string[] NotTheGame = ["redist", "directx", "crash", "unins", "setup"];

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

    /// <summary>Looks for anti-cheat folders/files by name anywhere under the install, under the exe's folder when it is
    /// outside it, and in the names of the folders from the exe's up to the install root. A tree that can't be read whole
    /// (a folder it may not list, the root included; more than <see cref="MaxEntries"/> entries) is <see cref="AntiCheat.Other"/>:
    /// not known to be clean. A junction or symlink is a name, not followed: a folder it points into inside the tree is
    /// read where it is, and one outside is another folder's; one on the way from the install root to the exe is Other. <paramref name="quick"/>: the install root's and the exe
    /// folder's own entries only, for a recheck right after a full one. Battle.net titles are marked conservatively:
    /// Blizzard's Warden is server-side, not a file the install carries. The only anti-cheat detector: engine readers and
    /// middleware detection call it to skip their own work; the app's evaluation acts on its verdict.</summary>
    public static AntiCheat DetectAntiCheat(Game game, bool quick = false)
    {
        if (game.Id.StartsWith("battlenet:", StringComparison.Ordinal)) return AntiCheat.Other;
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!;
        bool Inside(string d) => d.Equals(install, StringComparison.OrdinalIgnoreCase) || d.StartsWith(install + '\\', StringComparison.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;

        AntiCheat Walk(string root)
        {
            var dirs = new Stack<string>([root]);
            while (dirs.TryPop(out var dir))
            {
                if (!visited.Add(dir)) continue;
                List<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", AllNames).ToList(); }
                catch (DirectoryNotFoundException) when (dir == root) { continue; }   // nothing installed there (access denied throws otherwise)
                foreach (var e in entries)
                {
                    if (++seen > MaxEntries) return AntiCheat.Other;
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

    static AntiCheat Marker(string name)
    {
        foreach (var (marker, kind) in Markers)
            if (name.Equals(marker, StringComparison.OrdinalIgnoreCase)) return kind;
        return name.EndsWith("_BE.exe", StringComparison.OrdinalIgnoreCase) ? AntiCheat.BattlEye : AntiCheat.None;
    }
}
