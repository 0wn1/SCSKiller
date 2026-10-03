using System.Text.RegularExpressions;
using Microsoft.Win32;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Unreal;

/// <summary>Which graphics API an Unreal game runs on (EngineInfo.GraphicsApi). SM5 shaders serve both DX11 and DX12, so
/// the shader libraries alone don't tell; this follows the engine's own choice (WindowsDynamicRHI.cpp):
///   1. command line -dx11/-d3d11, -dx12/-d3d12, -vulkan (here: the user's Steam launch options, then the game's Steam
///      launch menu: one API offered decides it; several (DirectX 12 / DirectX 11 entries) leave it to the game's last log,
///      else to Steam's default entry when that runs DX12;
///   2. the user's GameUserSettings.ini: [D3DRHIPreference] PreferredRHI (5.1+) or bUseD3D12InGame, or a fork's own
///      PreferredGraphicsAPI (Gearbox). UE4 honours it only when the project doesn't set DefaultGraphicsRHI explicitly;
///   3. [/Script/WindowsTargetPlatform.WindowsTargetSettings] DefaultGraphicsRHI over the Engine ini hierarchy (paks, then
///      the user's saved Engine.ini); _Default or unset = the engine default: UE4 DX11, UE5 DX12.
/// Values: "D3D12" / "D3D11" / "Vulkan", suffixed " (launch option)", " (user setting)" or " (last run)" when that decided
/// it rather than the project default; "D3D11 or D3D12" when it can't be decided: the project defaults to DX11 but ships
/// DX12-only (SM6) shaders (DX12 is then a launch-menu or in-game choice we can't see), the Steam launch menu offers
/// several and the game's last log doesn't say, or the config is unreadable (encrypted) and the log doesn't say. SM6-only
/// shader libraries mean D3D12 whatever the config says.</summary>
public static class UnrealRhi
{
    public const string Ambiguous = "D3D11 or D3D12";

    /// <summary>Config files inside the paks that take part, lowest priority first ({P} = the project folder).</summary>
    static readonly string[] EngineIni = ["Engine/Config/Windows/BaseWindowsEngine.ini", "{P}/Config/DefaultEngine.ini", "Engine/Config/Windows/WindowsEngine.ini", "{P}/Config/Windows/WindowsEngine.ini"];
    static readonly string[] UserSettingsIni = ["{P}/Config/DefaultGameUserSettings.ini", "{P}/Config/Windows/WindowsGameUserSettings.ini"];

    /// <summary>Whether a pak file path is one of the config files <see cref="Resolve"/> reads.</summary>
    public static bool IsConfig(string path, string project) => EngineIni.Concat(UserSettingsIni).Any(p => Same(p, path, project));

    static bool Same(string pattern, string path, string project) => string.Equals(pattern.Replace("{P}", project), path, StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Ordered(string[] order, IReadOnlyDictionary<string, string> configs, string project) =>
        order.SelectMany(p => configs.Where(c => Same(p, c.Key, project)).Select(c => c.Value));

    /// <summary>(GraphicsApi, the evidence that decided it). <paramref name="configs"/>: pak path -> text of the files
    /// <see cref="IsConfig"/> selects; <paramref name="userDir"/>: the user's Saved folder or null; <paramref name="launch"/>:
    /// the command line the store adds; <paramref name="menu"/>, <paramref name="menuDefault"/>: <see cref="LaunchMenu"/>.</summary>
    public static (string Api, string Why) Resolve(int engineMajor, IReadOnlyCollection<string> platforms, IReadOnlyDictionary<string, string> configs,
        string project, string? userDir, string launch, IReadOnlyList<string>? menu = null, string? menuDefault = null)
    {
        bool sm5 = platforms.Contains("PCD3D_SM5"), sm6 = platforms.Contains("PCD3D_SM6");
        if (sm6 && !sm5) return ("D3D12", "only SM6 shaders, which run on DX12 only");
        if (Regex.Match(launch, @"(?:^|\s)-(dx11|d3d11|dx12|d3d12|vulkan)\b", RegexOptions.IgnoreCase) is { Success: true } cmd)
            return ($"{Api(cmd.Groups[1].Value)} (launch option)", $"launch option -{cmd.Groups[1].Value}");
        if (menu?.Any(e => MenuApi(e) != null) == true)
        {
            var bare = Resolve(engineMajor, platforms, configs, project, userDir, launch);   // what an entry without a flag runs
            var plain = bare.Api.Split(" (")[0];
            var offered = menu.Select(e => MenuApi(e) ?? plain).Distinct().ToList();
            if (offered.Count == 1)
                return menu.All(e => MenuApi(e) != null) ? ($"{offered[0]} (launch option)", $"the game's Steam launch menu passes {offered[0]}") : bare;
            if (LastRun(userDir) is { } run) return ($"{run.Api} (last run)", $"the game's Steam launch menu offers {string.Join(" / ", offered)}; the game's last log: {run.Line}");
            // a player who picks DX11 runs it, so only a DX12 default decides
            if (menuDefault != null && (MenuApi(menuDefault) ?? plain) == "D3D12")
                return MenuApi(menuDefault) != null ? ("D3D12 (launch option)", "the default entry of the game's Steam launch menu passes D3D12, no log") : bare;
            return (Ambiguous, $"the game's Steam launch menu offers {string.Join(" / ", offered)}, no log");
        }

        var userConfig = UserConfig(userDir);
        string? User(string file) => userConfig != null && File.Exists(Path.Combine(userConfig, file)) ? File.ReadAllText(Path.Combine(userConfig, file)) : null;
        var engine = Ordered(EngineIni, configs, project).Append(User("Engine.ini") ?? "").ToList();
        var settings = Ordered(UserSettingsIni, configs, project).Append(User("GameUserSettings.ini") ?? "").ToList();

        var def = Last(engine, "/Script/WindowsTargetPlatform.WindowsTargetSettings", "DefaultGraphicsRHI");
        var explicitDefault = def is not (null or "DefaultGraphicsRHI_Default");
        var byProject = def switch
        {
            "DefaultGraphicsRHI_DX11" => "D3D11", "DefaultGraphicsRHI_DX12" => "D3D12", "DefaultGraphicsRHI_Vulkan" => "Vulkan",
            _ => engineMajor >= 5 ? "D3D12" : "D3D11",
        };
        var why = def != null ? $"DefaultGraphicsRHI={def}" + (def == "DefaultGraphicsRHI_Default" ? $" (UE{engineMajor} default)" : "") : $"no DefaultGraphicsRHI (UE{engineMajor} default)";

        // the user's choice, if the engine honours it
        var pref = Last(settings, "D3DRHIPreference", "PreferredRHI") is { } p ? (Api(p), $"PreferredRHI={p}")
            : Last(settings, "*", "PreferredGraphicsAPI") is { } g ? (Api(g), $"PreferredGraphicsAPI={g}")
            : Last(settings, "D3DRHIPreference", "bUseD3D12InGame") is { } b && b.Equals("true", StringComparison.OrdinalIgnoreCase) ? ("D3D12", "bUseD3D12InGame=True")
            : ((string?)null, "");
        if (pref.Item1 is { } api && api != "?" && api != byProject && (engineMajor >= 5 || !explicitDefault))
            return ($"{api} (user setting)", $"{pref.Item2} in the user's GameUserSettings.ini over {why}");

        if (!configs.Keys.Any(k => Same(EngineIni[1], k, project))) // the project's own config is unreadable (encrypted paks)
        {
            if (LastRun(userDir) is { } log) return ($"{log.Api} (last run)", $"project config unreadable; the game's last log: {log.Line}");
            return sm5 && sm6 ? (Ambiguous, "project config unreadable, both SM5 and SM6 shaders ship, no log")
                : (Ambiguous, $"project config unreadable, no log (UE{engineMajor} default would be {byProject})");
        }
        if (byProject == "D3D11" && sm6) return (Ambiguous, $"{why}, but DX12-only (SM6) shaders ship: DX12 is a launch or in-game option");
        return (byProject, why + (pref.Item1 != null ? $"; user {pref.Item2} agrees or is ignored" : ""));
    }

    /// <summary>The API a launch-menu entry picks: a flag in its arguments, else "DirectX 12" or "DX12" in its description.</summary>
    static string? MenuApi(string entry) =>
        Regex.Match(entry, @"(?:^|\s)-(dx11|d3d11|dx12|d3d12|vulkan)\b|\b(?:DirectX|DX)\s*(1[12])\b", RegexOptions.IgnoreCase) is { Success: true } m
            ? Api(m.Groups[1].Success ? m.Groups[1].Value : "dx" + m.Groups[2].Value) : null;

    static string Api(string v) => v.Trim('"').ToLowerInvariant() switch
    {
        "dx11" or "d3d11" => "D3D11", "dx12" or "d3d12" => "D3D12", "vulkan" => "Vulkan", _ => "?",
    };

    /// <summary>Last value of section/key over ini texts in priority order; section "*" = any section.</summary>
    static string? Last(IEnumerable<string> inis, string section, string key)
    {
        string? v = null;
        foreach (var text in inis)
        {
            string? cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { cur = line.Trim('[', ']'); continue; }
                var eq = line.IndexOf('=');
                if (cur != null && eq > 0 && (section == "*" || cur.Equals(section, StringComparison.OrdinalIgnoreCase))
                    && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) v = line[(eq + 1)..].Trim().Trim('"');
            }
        }
        return v;
    }

    /// <summary>The RHI the game's newest log (Saved\Logs, Saved\Crashes) says it started with.</summary>
    static (string Api, string Line)? LastRun(string? userDir)
    {
        if (userDir == null) return null;
        foreach (var log in Logs(userDir))
        {
            using var r = new StreamReader(new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (var (n, line) = (0, r.ReadLine()); line != null && n < 20000; n++, line = r.ReadLine())
                if (Regex.Match(line, @"LogRHI: Using (?:Default|Preferred|Forced) RHI: (D3D11|D3D12|Vulkan)|(LogD3D1[12])RHI:") is { Success: true } m)
                    return (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value == "LogD3D12" ? "D3D12" : "D3D11", $"{log.Name} {log.LastWriteTime:yyyy-MM-dd}: {line.Trim()}");
        }
        return null;
    }

    static IEnumerable<FileInfo> Logs(string userDir)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        return new[] { "Logs", "Crashes" }.Select(d => Path.Combine(userDir, d)).Where(Directory.Exists)
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*.log", opts)).OrderByDescending(f => f.LastWriteTimeUtc);
    }

    /// <summary>What <see cref="Resolve"/> reads besides the game files, as a stamp that changes when any of it does: the
    /// user's Saved folder, its newest ini and log, the Steam launch options and launch menu.</summary>
    public static string Stamp(Game game, string project)
    {
        var (entries, def) = LaunchMenu(game);
        return string.Join('|', UserFiles(UserDir(game, project)), LaunchOptions(game), string.Join(';', entries), def);
    }

    /// <summary>The user files <see cref="Resolve"/> reads, each by presence, length and write time: the selected config
    /// folder's Engine.ini and GameUserSettings.ini, and every log <see cref="LastRun"/> may read.</summary>
    internal static string UserFiles(string? userDir)
    {
        if (userDir == null) return "";
        static string Of(FileInfo f) => f.Exists ? $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : $"{f.FullName}:-";
        var config = UserConfig(userDir);
        var inis = config == null ? [] : new[] { "Engine.ini", "GameUserSettings.ini" }.Select(n => Of(new FileInfo(Path.Combine(config, n))));
        return string.Join('>', inis.Prepend(userDir).Concat(Logs(userDir).Select(Of)));
    }

    /// <summary>The newest Saved\Config\Windows* folder: the one the game reads.</summary>
    static string? UserConfig(string? userDir) =>
        userDir == null || !Directory.Exists(Path.Combine(userDir, "Config")) ? null
            : Directory.EnumerateDirectories(Path.Combine(userDir, "Config"), "Windows*").MaxBy(Directory.GetLastWriteTimeUtc);

    /// <summary>The user's Saved folder (…\Saved\Config\Windows*\ holds Engine.ini and GameUserSettings.ini): under
    /// %LOCALAPPDATA%, Documents\My Games or Saved Games, named after the project, the exe, the install folder or the game,
    /// possibly under a company folder, "Saved" or "Saved_&lt;store&gt;_&lt;user&gt;". Newest wins. Read-only.</summary>
    public static string? UserDir(Game game, string project)
    {
        var names = new[] { project, ExeStem(game), Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')), game.Name }
            .Where(n => n.Length > 0 && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"), Path.Combine(home, "Saved Games") }
            .Where(Directory.Exists).ToList();
        var flat = new EnumerationOptions { IgnoreInaccessible = true };
        return roots.SelectMany(r => names.Select(n => Path.Combine(r, n)).Concat(Directory.EnumerateDirectories(r, "*", flat).SelectMany(c => names.Select(n => Path.Combine(c, n)))))
            .Where(Directory.Exists).SelectMany(d => Directory.EnumerateDirectories(d, "Saved*", flat))
            .Where(s => Directory.Exists(Path.Combine(s, "Config")) && Directory.EnumerateDirectories(Path.Combine(s, "Config"), "Windows*", flat).Any())
            .OrderByDescending(s => Directory.GetLastWriteTimeUtc(Path.Combine(s, "Config"))).FirstOrDefault();
    }

    static string ExeStem(Game game) => Regex.Replace(Path.GetFileNameWithoutExtension(game.ExePath), @"-Win(64|GDK)-Shipping$", "", RegexOptions.IgnoreCase);

    /// <summary>The Steam launch options the user set for the game ("" if none, or not a Steam game): the newest
    /// userdata\*\config\localconfig.vdf, the app's block.</summary>
    public static string LaunchOptions(Game game)
    {
        if (!game.Id.StartsWith("steam:")) return "";
        var userdata = Path.Combine(SteamRoot, "userdata");
        var vdf = Directory.Exists(userdata)
            ? Directory.EnumerateDirectories(userdata).Select(u => new FileInfo(Path.Combine(u, "config", "localconfig.vdf"))).Where(f => f.Exists).MaxBy(f => f.LastWriteTimeUtc)
            : null;
        if (vdf == null) return "";
        var text = File.ReadAllText(vdf.FullName);
        foreach (Match m in Regex.Matches(text, $@"""{Regex.Escape(game.Id[6..])}""\s*\{{")) // the app id also keys other blocks
        {
            var end = m.Index + m.Length;
            for (var depth = 1; end < text.Length && depth > 0; end++) depth += text[end] == '{' ? 1 : text[end] == '}' ? -1 : 0;
            if (SteamSource.Values(text[m.Index..end], "LaunchOptions").FirstOrDefault() is { } options) return options;
        }
        return "";
    }

    /// <summary>The launch menu the game defines in Steam (appinfo.vdf config/launch): "arguments description" of each
    /// Windows entry that starts the game (its exe, or the root launcher named after it), a beta branch's own entries left
    /// out, and the one of type "default". Empty if none, or not a Steam game. Kept per appinfo.vdf stamp.</summary>
    public static (IReadOnlyList<string> Entries, string? Default) LaunchMenu(Game game)
    {
        if (!game.Id.StartsWith("steam:") || !uint.TryParse(game.Id[6..], out var id)) return ([], null);
        var f = new FileInfo(Path.Combine(SteamRoot, "appcache", "appinfo.vdf"));
        var stamp = f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";
        var key = $"{game.Id}|{game.InstallDir}|{game.ExePath}";
        lock (menus)
        {
            if (menuStamp != stamp) { menus.Clear(); menuStamp = stamp; }
            if (menus.TryGetValue(key, out var hit)) return hit;
        }
        var apps = SteamSource.Apps(f.FullName, new HashSet<uint> { id });
        var menu = LaunchMenu(apps?.GetValueOrDefault(id), game);
        if (apps != null) lock (menus) if (menuStamp == stamp) menus[key] = menu;   // a failed read is tried again
        return menu;
    }

    static readonly Dictionary<string, (IReadOnlyList<string>, string?)> menus = [];
    static string? menuStamp;

    internal static (IReadOnlyList<string> Entries, string? Default) LaunchMenu(Dictionary<string, object>? app, Game game)
    {
        if (app?.GetValueOrDefault("config") is not Dictionary<string, object> config || config.GetValueOrDefault("launch") is not Dictionary<string, object> launch) return ([], null);
        var install = Path.GetFullPath(game.InstallDir).TrimEnd('\\', '/');
        bool StartsTheGame(string exe)
        {
            var p = Path.GetFullPath(Path.Combine(install, exe));
            return p.Equals(Path.GetFullPath(game.ExePath), StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(p)!.Equals(install, StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(p).Equals(ExeStem(game), StringComparison.OrdinalIgnoreCase);
        }
        var entries = launch.Values.OfType<Dictionary<string, object>>()
            .Where(e => e.GetValueOrDefault("executable") is string exe && StartsTheGame(exe))
            .Where(e => e.GetValueOrDefault("config") is not Dictionary<string, object> c
                || !c.ContainsKey("BetaKey") && (c.GetValueOrDefault("oslist") is not string os || os.Contains("windows", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        static string Text(Dictionary<string, object> e) => $"{e.GetValueOrDefault("arguments")} {e.GetValueOrDefault("description")}";
        var def = entries.Where(e => "default".Equals(e.GetValueOrDefault("type") as string, StringComparison.OrdinalIgnoreCase)).Select(Text).FirstOrDefault();
        return (entries.Select(Text).ToList(), def);
    }

    static string SteamRoot => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? @"C:\Program Files (x86)\Steam";
}
