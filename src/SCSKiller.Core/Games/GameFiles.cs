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

    /// <summary>Looks for anti-cheat folders/files in the install root and the exe's folder (and one level below each).
    /// Battle.net titles are marked conservatively: Blizzard's Warden is server-side, not a file the install carries.</summary>
    public static AntiCheat DetectAntiCheat(Game game)
    {
        if (game.Id.StartsWith("battlenet:", StringComparison.Ordinal)) return AntiCheat.Other;
        var dirs = new[] { game.InstallDir, Path.GetDirectoryName(game.ExePath)! }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1, IgnoreInaccessible = true };
        foreach (var dir in dirs)
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", opts))
            {
                var name = Path.GetFileName(entry);
                foreach (var (marker, kind) in Markers)
                    if (name.Equals(marker, StringComparison.OrdinalIgnoreCase)) return kind;
                if (name.EndsWith("_BE.exe", StringComparison.OrdinalIgnoreCase)) return AntiCheat.BattlEye;
            }
        return AntiCheat.None;
    }
}
