using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>What a ReShade add-on does to the game's pipelines.</summary>
public enum AddonKind
{
    NotPipeline,       // leaves them alone (renodx-dlss5, dlssfix, an effect add-on)
    ReplacesShaders,   // replaces some shaders: only their pipelines change
    LayoutInjecting,   // adds a constant to every root signature the game creates: every pipeline changes
}

/// <param name="Mod">"RenoDX" or "Luma"; the file's name for one that couldn't be read; null = another add-on</param>
/// <param name="Disabled">listed in ReShade.ini's DisabledAddons: never loaded</param>
public sealed record ReShadeAddon(string Path, string? Mod, AddonKind Kind, bool Disabled = false);

/// <summary>ReShade next to the game: its DLL (whatever its name), ReShade.ini and ReShade.log when present, and the
/// add-ons in the folder it loads them from (its own, or ReShade.ini's AddonPath).</summary>
/// <param name="LoadsAddons">the build with full add-on support; the standard one loads no add-on files</param>
/// <param name="BesideExe">in the exe's own folder, where the game loads it; else in the install root above it</param>
public sealed record ReShadeInstall(string Dll, bool LoadsAddons, string? Ini, string? Log, IReadOnlyList<ReShadeAddon> Addons, bool BesideExe = true)
{
    /// <summary>The loaded add-on that changes the most of the game's pipelines; null = none changes any.</summary>
    public ReShadeAddon? ShaderMod => LoadsAddons ? Addons.Where(a => a is { Disabled: false, Kind: not AddonKind.NotPipeline }).MaxBy(a => a.Kind) : null;

    // the names the game itself loads ReShade under from its exe's folder (system DLLs it imports, or the recorder's chain)
    static readonly string[] Loaded = ["dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "dinput8.dll", ScsKiller.ChainName];

    /// <summary>A copy of the layer next to the warm's exe reproduces what the game loads: ReShade beside the exe under a
    /// name the game loads by itself (not an .asi, ReShade64.dll or a renamed file another loader may or may not pick
    /// up), and no Luma add-on, whose shader files a copy leaves out.</summary>
    public bool Copyable => BesideExe && Loaded.Contains(Path.GetFileName(Dll), StringComparer.OrdinalIgnoreCase)
        && !Addons.Any(a => a is { Disabled: false, Mod: "Luma" });

    /// <summary>A shader mod whose layer a copy reproduces (<see cref="Copyable"/>): compiles run through it.</summary>
    public bool Layered => ShaderMod != null && Copyable;

    /// <summary>An add-on that changes every pipeline in a layer a copy can't reproduce: a compile without it matches
    /// nothing if the game loads it, and one through it nothing if the game doesn't.</summary>
    public bool Blocks => !Copyable && ShaderMod is { Kind: AddonKind.LayoutInjecting };

    /// <summary>ReShade under the recorder's own name (d3d12.dll, or the name a chained mod gets): the recorder records
    /// under it only when the user chains it ("Record alongside").</summary>
    public bool AsD3D12 => Path.GetFileName(Dll) is var n && (n.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase) || n.Equals(ScsKiller.ChainName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a warm through this layer depends on: ReShade's DLL and the add-ons that change pipelines, each by
    /// name, size and write time (not ReShade.ini: ReShade rewrites it as it runs); null = nothing to copy (not <see cref="Layered"/>).</summary>
    public string? Fingerprint => !Layered ? null : string.Join('|', new[] { Dll }.Concat(Addons.Where(a => a is { Disabled: false, Kind: not AddonKind.NotPipeline }).Select(a => a.Path))
        .Select(p => new FileInfo(p)).Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
}

public static class ReShade
{
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true };

    // Raw bytes for a DLL without a readable version resource: its FileDescription, and the export add-ons register through
    static readonly byte[][] Identity = [Encoding.Unicode.GetBytes("ReShade post-processing injector"), "ReShadeRegisterAddon"u8.ToArray()];
    // only in the standard build, which loads no add-on files
    static readonly byte[][] Limited = ["only limited add-on functionality"u8.ToArray()];
    // the names ReShade loads under next to the exe, read whole when the version resource doesn't name it
    static readonly string[] Names = ["dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "dinput8.dll", "ReShade64.dll", ScsKiller.ChainName];

    // Logged unconditionally where release builds register their pipeline hooks: RenoDX's shader-replacement util (every
    // HDR add-on and the devkit, in both RenoDX repos; not dlssfix, fpslimiter or DLSS-only add-ons) and Luma's core.
    static readonly (string Mod, byte[] Marker)[] Mods =
        [("RenoDX", "utils::shader attached."u8.ToArray()), ("Luma", "Luma: trying to load a config from a newer version of the mod"u8.ToArray())];
    static readonly byte[][] ModMarkers = [.. Mods.Select(m => m.Marker)];

    // ReShade.log: which file each add-on name was registered from, then RenoDX (prefixed with that name) adding its
    // constants to a game root signature or cloning the layout instead. Both paths are in every build and a runtime flag
    // picks one, so the binary can't tell them apart.
    static readonly Regex Loading = new(@"Loading add-on from '(.+)' \.\.\.$"), Registered = new(@"Registered add-on ""(.+?)""");
    static readonly Regex Pipeline = new(@"\| \[(.+?)\] mods::shader::(OnCreatePipelineLayout\(will insert|OnInitPipelineLayout\(Cloning)");

    /// <summary>A file larger than this is never read (the largest add-on seen is 65 MB, its strings at the end).</summary>
    const long MaxBytes = 128L << 20;
    const int NotRead = -2;
    static readonly ConcurrentDictionary<string, (long Length, DateTime Written, object? Value)> Probed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>RenoDX's build folders by what their add-on does (shader-mods.json).</summary>
    public static IReadOnlyDictionary<string, AddonKind> RenoDxTable { get => field ??= ParseTable(ContentFile.Embedded("SCSKiller.Core.Games.shader-mods.json")); }

    static Dictionary<string, AddonKind> ParseTable(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var table = new Dictionary<string, AddonKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, kind) in new[] { ("layout_injecting", AddonKind.LayoutInjecting), ("replaces_shaders", AddonKind.ReplacesShaders) })
            foreach (var e in doc.RootElement.GetProperty(key).EnumerateArray()) table[e.GetString()!] = kind;
        return table;
    }

    /// <summary>The RenoDX build folder an add-on file name ("renodx-ff7rebirth.addon64") names; null = not RenoDX's.</summary>
    public static string? RenoDxFolder(string fileName) =>
        fileName.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(fileName)["renodx-".Length..] : null;

    /// <summary>ReShade in the exe's folder, else the install root, known by its version resource (any DLL there) or, for
    /// one of its usual names, its own bytes; null = none. Its add-ons are the ones in ReShade.ini's AddonPath, else its
    /// folder; DisabledAddons are marked. A RenoDX add-on is LayoutInjecting or ReplacesShaders by ReShade.log's lines for
    /// its file, when the log was written after the file; else by its build folder (<see cref="RenoDxTable"/>, from the file
    /// name or the version resource's OriginalFilename); else ReplacesShaders, as is an add-on that couldn't be read: not
    /// known to change every pipeline. Each file is read once while its size and write time stay the same.</summary>
    public static ReShadeInstall? Detect(Game game)
    {
        foreach (var dir in new[] { GameFiles.DirKey(Path.GetDirectoryName(game.ExePath)!), GameFiles.DirKey(game.InstallDir) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var files = List(dir);
            if (files == null) continue;
            var dll = files.FirstOrDefault(f => (Ext(f, ".dll") || Ext(f, ".asi")) && IsReShade(f));
            if (dll == null) continue;
            string? Named(string name) => files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.FullName;
            var (ini, log) = (Named("ReShade.ini"), Named("ReShade.log"));
            var (addonPath, disabled) = ini != null ? Config(new FileInfo(ini)) : (null, []);
            var addonDir = addonPath != null ? GameFiles.DirKey(Path.Combine(dir, addonPath)) : dir;
            var verdicts = log != null ? LogVerdicts(new FileInfo(log)) : null;
            var addons = (addonDir == dir ? files : List(addonDir) ?? []).Where(f => Ext(f, ".addon") || Ext(f, ".addon64"))
                .Select(f =>
                {
                    var a = Classify(f, verdicts, log);
                    return a with { Disabled = disabled.Any(d => Disables(d, a, f)) };
                }).ToList();
            return new(dll.FullName, Find(dll, "limited", Limited) != 0, ini, log, addons, dir.Equals(GameFiles.DirKey(Path.GetDirectoryName(game.ExePath)!), StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    static List<FileInfo>? List(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", Flat).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>A copy of the layer for a warm (scskiller_warm --layer) in <paramref name="dest"/>, emptied first: ReShade's
    /// DLL as dxgi.dll (the warm's exe loads it under that name, whatever the game's is), the add-ons that change pipelines,
    /// and ReShade.ini without the keys that point ReShade at another folder ([ADDON] AddonPath, [INSTALL] BasePath).</summary>
    public static string Stage(ReShadeInstall r, string dest)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        File.Copy(r.Dll, Path.Combine(dest, "dxgi.dll"));
        foreach (var a in r.Addons.Where(a => a is { Disabled: false, Kind: not AddonKind.NotPipeline })) File.Copy(a.Path, Path.Combine(dest, Path.GetFileName(a.Path)));
        if (r.Ini != null) File.WriteAllLines(Path.Combine(dest, "ReShade.ini"), WithoutPaths(File.ReadAllLines(r.Ini)));
        return dest;
    }

    static IEnumerable<string> WithoutPaths(IEnumerable<string> ini)
    {
        var section = "";
        foreach (var line in ini)
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) section = t[1..^1].Trim();
            var key = t.Split('=', 2)[0].Trim();
            if (!(section.Equals("ADDON", StringComparison.OrdinalIgnoreCase) && key.Equals("AddonPath", StringComparison.OrdinalIgnoreCase)
                  || section.Equals("INSTALL", StringComparison.OrdinalIgnoreCase) && key.Equals("BasePath", StringComparison.OrdinalIgnoreCase)))
                yield return line;
        }
    }

    static bool Ext(FileInfo f, string ext) => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase);

    static bool IsReShade(FileInfo f) =>
        Cached(f, "product", () => FileVersionInfo.GetVersionInfo(f.FullName).ProductName == "ReShade") is true
        || Names.Contains(f.Name, StringComparer.OrdinalIgnoreCase) && Find(f, "identity", Identity) >= 0;

    static ReShadeAddon Classify(FileInfo f, IReadOnlyDictionary<string, AddonKind>? verdicts, string? log)
    {
        var mod = Find(f, "mods", ModMarkers);
        if (mod == -1) return new(f.FullName, null, AddonKind.NotPipeline);
        if (mod >= 0 && Mods[mod].Mod != "RenoDX") return new(f.FullName, Mods[mod].Mod, AddonKind.ReplacesShaders);
        // a log from before the file was replaced says nothing about it
        var logged = verdicts != null && verdicts.TryGetValue(f.FullName, out var k) && File.GetLastWriteTimeUtc(log!) > f.LastWriteTimeUtc ? k : (AddonKind?)null;
        var kind = logged ?? Listed(f.Name) ?? Listed(OriginalName(f.FullName));
        return new(f.FullName, mod >= 0 || kind != null ? "RenoDX" : Path.GetFileNameWithoutExtension(f.Name), kind ?? AddonKind.ReplacesShaders);
    }

    /// <summary>A DisabledAddons entry as ReShade reads it: "name" for the add-on registered under that name, "@file" or
    /// "name@file" for its file (the name isn't checked before loading). The name is RenoDX's for its builds, else the
    /// version resource's ProductName or the file's stem, as ReShade takes it unless the add-on exports another.</summary>
    static bool Disables(string entry, ReShadeAddon a, FileInfo f) =>
        entry.IndexOf('@') is var at and >= 0 ? entry[(at + 1)..] == f.Name
        : entry == (a.Mod == "RenoDX" ? "RenoDX" : Cached(f, "productname", () => FileVersionInfo.GetVersionInfo(f.FullName).ProductName ?? "") as string is { Length: > 0 } product
            ? product : Path.GetFileNameWithoutExtension(f.Name));

    static AddonKind? Listed(string fileName) => RenoDxFolder(fileName) is { } folder && RenoDxTable.TryGetValue(folder, out var k) ? k : null;

    static string OriginalName(string path) => Cached(new FileInfo(path), "original", () => FileVersionInfo.GetVersionInfo(path).OriginalFilename ?? "") as string ?? "";

    /// <summary>ReShade.ini's [ADDON] AddonPath (relative to ReShade's folder) and DisabledAddons' entries.</summary>
    static (string? AddonPath, List<string> Disabled) Config(FileInfo ini)
    {
        var text = Cached(ini, "ini", () => File.ReadAllText(ini.FullName)) as string ?? "";
        string? addonPath = null;
        var disabled = new List<string>();
        var section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            if (!section.Equals("ADDON", StringComparison.OrdinalIgnoreCase) || line.IndexOf('=') is not (> 0 and var eq)) continue;
            var (key, value) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
            if (key.Equals("AddonPath", StringComparison.OrdinalIgnoreCase) && value.Trim('"') is { Length: > 0 } p) addonPath = p;
            else if (key.Equals("DisabledAddons", StringComparison.OrdinalIgnoreCase))
                disabled.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return (addonPath, disabled);
    }

    /// <summary>The last verdict ReShade.log gives each add-on file: LayoutInjecting or ReplacesShaders.</summary>
    static IReadOnlyDictionary<string, AddonKind> LogVerdicts(FileInfo log) =>
        Cached(log, "log", () =>
        {
            var (files, verdicts) = (new Dictionary<string, string?>(), new Dictionary<string, AddonKind>(StringComparer.OrdinalIgnoreCase));
            string? loading = null;
            using var r = new StreamReader(new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));   // a running game writes it
            for (string? line; (line = r.ReadLine()) != null;)
                if (Loading.Match(line) is { Success: true } l) loading = l.Groups[1].Value;
                else if (Registered.Match(line) is { Success: true } g && loading != null) (files[g.Groups[1].Value], loading) = (FullPath(loading), null);
                else if (Pipeline.Match(line) is { Success: true } p && files.TryGetValue(p.Groups[1].Value, out var file) && file != null)
                    verdicts[file] = p.Groups[2].Value.StartsWith("OnCreate", StringComparison.Ordinal) ? AddonKind.LayoutInjecting : AddonKind.ReplacesShaders;
            return verdicts;
        }) as IReadOnlyDictionary<string, AddonKind> ?? new Dictionary<string, AddonKind>();

    // as FileInfo.FullName has it: ReShade logs its search folder joined with AddonPath ("C:\Game\.\addons\x.addon64")
    static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The first of <paramref name="markers"/> the file holds; -1 = none; <see cref="NotRead"/>.</summary>
    static int Find(FileInfo f, string what, byte[][] markers) => Cached(f, what, () =>
    {
        using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return Array.FindIndex(markers, m => bytes.AsSpan().IndexOf(m) >= 0);
    }) as int? ?? NotRead;

    /// <summary><paramref name="read"/>'s result (<paramref name="what"/>) for the file while its size and write time stay the same; null = it
    /// couldn't be read or is over <see cref="MaxBytes"/>.</summary>
    static object? Cached(FileInfo f, string what, Func<object> read)
    {
        f.Refresh();
        if (!f.Exists || f.Length > MaxBytes) return null;
        var key = f.FullName + "|" + what;
        if (Probed.TryGetValue(key, out var c) && (c.Length, c.Written) == (f.Length, f.LastWriteTimeUtc)) return c.Value;
        try
        {
            var value = read();
            Probed[key] = (f.Length, f.LastWriteTimeUtc, value);
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
