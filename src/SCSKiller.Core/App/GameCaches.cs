using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

/// <summary><c>%LOCALAPPDATA%\D3DSCache</c>: one folder per exe path of SQLite dbs (<c>*.dxcache</c>, WAL) with an
/// <c>app_id(exe_path)</c> table. Opened immutable (no lock, no -shm/-wal write); rows only in the -wal are found by scanning it.</summary>
public static class D3DSCache
{
    /// <summary>Folders whose every exe path is the game's; skipped on doubt (an unreadable db, no path, another path).</summary>
    public static IReadOnlyList<string> FoldersOf(string root, Game g)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        foreach (var dir in Directory.EnumerateDirectories(root))
            try
            {
                if (ExePaths(dir) is { Count: > 0 } paths && paths.All(p => IsGameExe(p, g))) found.Add(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // skip on doubt
        return found;
    }

    /// <summary>Null when a db can't be read.</summary>
    public static HashSet<string>? ExePaths(string dir)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir))
            if (f.EndsWith(".dxcache", StringComparison.OrdinalIgnoreCase))
            {
                if (!ReadAppIds(f, paths)) return null;
            }
            else if (f.EndsWith(".dxcache-wal", StringComparison.OrdinalIgnoreCase))
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[s.Length];
                s.ReadExactly(bytes);
                foreach (Match m in WalExe.Matches(Encoding.Latin1.GetString(bytes))) paths.Add(m.Value);
            }
        return paths;
    }

    static readonly Regex WalExe = new(@"[A-Za-z]:\\[ -~]{3,}?\.[Ee][Xx][Ee]");

    /// <summary>An Xbox game's exe runs from <c>...\WindowsApps\&lt;Name&gt;_&lt;Version&gt;_&lt;Arch&gt;_&lt;ResourceId&gt;_&lt;PublisherId&gt;\&lt;path in Content&gt;</c>:
    /// Name_PublisherId is its package family, any version matches.</summary>
    public static bool IsGameExe(string path, Game g)
    {
        if (path.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
        if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal)) return false;
        var parts = path.Split('\\');
        var i = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= parts.Length) return false;
        var pkg = parts[i + 1].Split('_');   // package names have no '_'
        return pkg.Length == 5 && $"{pkg[0]}_{pkg[4]}".Equals(g.Id["xbox:".Length..], StringComparison.OrdinalIgnoreCase)
            && string.Join('\\', parts[(i + 2)..]).Equals(Path.GetRelativePath(g.InstallDir, g.ExePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>False when not a readable SQLite db; one without the table yet (rows still in the WAL) adds nothing.</summary>
    static bool ReadAppIds(string file, HashSet<string> paths)
    {
        var uri = "file:" + file.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23").Replace("?", "%3f") + "?immutable=1";
        var rc = sqlite3_open_v2(uri, out var db, 0x01 /* READONLY */ | 0x40 /* URI */, 0);
        try
        {
            if (rc != 0) return false;
            long tables = 0;
            if (!Rows(db, "select count(*) from sqlite_master where type='table' and name='app_id'", st => tables = sqlite3_column_int64(st, 0))) return false;
            return tables == 0 || Rows(db, "select exe_path from app_id", st =>
            {
                if (Marshal.PtrToStringUTF8(sqlite3_column_text(st, 0)) is { Length: > 0 } p) paths.Add(p);
            });
        }
        finally { sqlite3_close_v2(db); }
    }

    static bool Rows(nint db, string sql, Action<nint> row)
    {
        if (sqlite3_prepare_v2(db, sql, -1, out var st, 0) != 0) return false;
        try
        {
            int rc;
            while ((rc = sqlite3_step(st)) == 100 /* ROW */) row(st);
            return rc == 101;   // DONE
        }
        finally { sqlite3_finalize(st); }
    }

    [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_close_v2(nint db);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint stmt, nint tail);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_step(nint stmt);
    [DllImport("winsqlite3.dll")] static extern nint sqlite3_column_text(nint stmt, int col);
    [DllImport("winsqlite3.dll")] static extern long sqlite3_column_int64(nint stmt, int col);
    [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(nint stmt);
}

/// <summary>Unreal's user pipeline cache, <c>&lt;Saved&gt;\&lt;Project&gt;_&lt;ShaderPlatform&gt;.upipelinecache</c>. Saved's place varies
/// by game (e.g. <c>%LOCALAPPDATA%\Iris\Saved_Steam_&lt;id&gt;</c>), so it's found by file name in any Saved / Saved_* folder one
/// level under the roots; never inside the install (the shipped caches).</summary>
public static class UnrealUserCache
{
    /// <summary>The folder holding <c>Binaries\&lt;platform&gt;\</c> the exe is in; null when not laid out like that.</summary>
    public static string? Project(string exePath)
    {
        var bin = Path.GetDirectoryName(Path.GetDirectoryName(exePath));
        return bin != null && Path.GetFileName(bin).Equals("Binaries", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(bin)) is { Length: > 0 } project ? project : null;
    }

    public static IReadOnlyList<string> FilesOf(Game g, IEnumerable<string> roots)
    {
        if (Project(g.ExePath) is not { } project) return [];
        var name = new Regex($@"^{Regex.Escape(project)}_(PCD3D|SF_VULKAN|VULKAN)_[A-Z0-9_]+(\.stable)?\.upipelinecache$", RegexOptions.IgnoreCase);
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + '\\';
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var files = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var app in Directory.EnumerateDirectories(root, "*", options))
                try
                {
                    foreach (var saved in Directory.EnumerateDirectories(app, "Saved*", options))
                        if (Path.GetFileName(saved) is var s && (s.Equals("Saved", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Saved_", StringComparison.OrdinalIgnoreCase)))
                            files.AddRange(Directory.EnumerateFiles(saved, "*.upipelinecache", options)
                                .Where(f => name.IsMatch(Path.GetFileName(f)) && !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>The folder names a game's own caches go under in %LOCALAPPDATA%, My Games or ProgramData: its name, its
    /// install folder's, its Unreal project's (Tiny Tina's Wonderlands: <c>%LOCALAPPDATA%\Tiny Tina's Wonderlands\Saved</c>).</summary>
    public static IEnumerable<string> Folders(Game g) =>
        new[] { g.Name, Path.GetFileName(Path.TrimEndingDirectorySeparator(g.InstallDir)), Project(g.ExePath) }
            .OfType<string>().Select(n => n.Trim()).Where(n => n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>*.ushaderprecache</c> files (a shader and pipeline cache some Unreal games write themselves, and rebuild
    /// at their next start) in <c>&lt;root&gt;\&lt;folder&gt;</c> and two levels below (its Saved folder); never inside the install.</summary>
    public static IReadOnlyList<string> PrecacheFilesOf(Game g, IEnumerable<string> roots, IEnumerable<string> folders)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(g.InstallDir)) + '\\';
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = true, MaxRecursionDepth = 2 };
        var names = folders.ToList();
        return roots.Where(Directory.Exists).SelectMany(r => names.Select(n => Path.Combine(r, n))).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.ushaderprecache", options))
            .Where(f => !Path.GetFullPath(f).StartsWith(install, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
