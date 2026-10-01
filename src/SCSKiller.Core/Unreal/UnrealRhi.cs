using System.Text.RegularExpressions;
using Microsoft.Win32;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Unreal;

/// <summary>Which graphics API an Unreal game runs on (EngineInfo.GraphicsApi). SM5 shaders serve both DX11 and DX12, so
/// the shader libraries alone don't tell; this follows the engine's own choice (WindowsDynamicRHI.cpp):
///   1. command line -dx11/-d3d11, -dx12/-d3d12, -vulkan (here: the user's Steam launch options);
///   2. the user's GameUserSettings.ini: [D3DRHIPreference] PreferredRHI (5.1+) or bUseD3D12InGame, or a fork's own
///      PreferredGraphicsAPI (Gearbox). UE4 honours it only when the project doesn't set DefaultGraphicsRHI explicitly;
///   3. [/Script/WindowsTargetPlatform.WindowsTargetSettings] DefaultGraphicsRHI over the Engine ini hierarchy (paks, then
///      the user's saved Engine.ini); _Default or unset = the engine default: UE4 DX11, UE5 DX12.
/// Values: "D3D12" / "D3D11" / "Vulkan", suffixed " (launch option)", " (user setting)" or " (last run)" when that decided
/// it rather than the project default; "D3D11 or D3D12" when it can't be decided: the project defaults to DX11 but ships
/// DX12-only (SM6) shaders (DX12 is then a launch-menu or in-game choice we can't see), or the config is unreadable
/// (encrypted) and the game's last log doesn't say. SM6-only shader libraries mean D3D12 whatever the config says.</summary>
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
    /// the command line the store adds.</summary>
    public static (string Api, string Why) Resolve(int engineMajor, IReadOnlyCollection<string> platforms, IReadOnlyDictionary<string, string> configs,
        string project, string? userDir, string launch)
    {
        bool sm5 = platforms.Contains("PCD3D_SM5"), sm6 = platforms.Contains("PCD3D_SM6");
        if (sm6 && !sm5) return ("D3D12", "only SM6 shaders, which run on DX12 only");
        if (Regex.Match(launch, @"(?:^|\s)-(dx11|d3d11|dx12|d3d12|vulkan)\b", RegexOptions.IgnoreCase) is { Success: true } cmd)
            return ($"{Api(cmd.Groups[1].Value)} (launch option)", $"launch option -{cmd.Groups[1].Value}");

        var userConfig = userDir == null ? null : Directory.EnumerateDirectories(Path.Combine(userDir, "Config"), "Windows*").MaxBy(Directory.GetLastWriteTimeUtc);
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
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        var logs = new[] { "Logs", "Crashes" }.Select(d => Path.Combine(userDir, d)).Where(Directory.Exists)
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*.log", opts)).OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var log in logs)
        {
            using var r = new StreamReader(new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (var (n, line) = (0, r.ReadLine()); line != null && n < 20000; n++, line = r.ReadLine())
                if (Regex.Match(line, @"LogRHI: Using (?:Default|Preferred|Forced) RHI: (D3D11|D3D12|Vulkan)|(LogD3D1[12])RHI:") is { Success: true } m)
                    return (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value == "LogD3D12" ? "D3D12" : "D3D11", $"{log.Name} {log.LastWriteTime:yyyy-MM-dd}: {line.Trim()}");
        }
        return null;
    }

    /// <summary>The user's Saved folder (…\Saved\Config\Windows*\ holds Engine.ini and GameUserSettings.ini): under
    /// %LOCALAPPDATA%, Documents\My Games or Saved Games, named after the project, the exe, the install folder or the game,
    /// possibly under a company folder, "Saved" or "Saved_&lt;store&gt;_&lt;user&gt;". Newest wins. Read-only.</summary>
    public static string? UserDir(Game game, string project)
    {
        var exe = Regex.Replace(Path.GetFileNameWithoutExtension(game.ExePath), @"-Win(64|GDK)-Shipping$", "", RegexOptions.IgnoreCase);
        var names = new[] { project, exe, Path.GetFileName(game.InstallDir.TrimEnd('\\', '/')), game.Name }
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

    /// <summary>The Steam launch options the user set for the game ("" if none, or not a Steam game): the newest
    /// userdata\*\config\localconfig.vdf, the app's block.</summary>
    public static string LaunchOptions(Game game)
    {
        if (!game.Id.StartsWith("steam:")) return "";
        var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? @"C:\Program Files (x86)\Steam";
        var userdata = Path.Combine(root, "userdata");
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
}
