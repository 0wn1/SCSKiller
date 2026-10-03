using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace SCSKiller.Core.Games;

/// <summary>Installed Xbox app / Game Pass PC games: &lt;drive&gt;:\XboxGames\&lt;title&gt;\Content, each with its
/// own MicrosoftGame.config (the GDK package manifest) naming the game's real executable(s) and version. No
/// package-manager query is needed to enumerate installs: the manifest sits right there in Content, readable like
/// any other file since the 2022 Xbox app. It's only used, via the native PackageFamilyNameFromId (below), to turn
/// the manifest's Identity name+publisher into the same PackageFamilyName Get-AppxPackage would show, for the id.</summary>
public sealed class XboxSource : IGameSource
{
    public Store Store => Store.Xbox;

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var titleDir in Directory.EnumerateDirectories(root))
            {
                var content = Path.Combine(titleDir, "Content");
                var configPath = Path.Combine(content, "MicrosoftGame.config");
                if (!File.Exists(configPath)) continue;
                try
                {
                    if (ParseGame(content, configPath) is { } game) games.Add(game);
                }
                catch (Exception) { }   // a config we can't read/parse shouldn't take the rest of the scan down
            }
        }
        return games;
    }

    static Game? ParseGame(string content, string configPath)
    {
        var doc = XDocument.Load(configPath);
        var ns = doc.Root!.GetDefaultNamespace();
        var identity = doc.Root.Element(ns + "Identity");
        var name = identity?.Attribute("Name")?.Value;
        var publisher = identity?.Attribute("Publisher")?.Value;
        var version = identity?.Attribute("Version")?.Value;
        if (name == null) return null;
        // An add-on (DLC, the MCC per-game content packs, Game Pass trackers) names the products it belongs to.
        if (doc.Root.Element(ns + "AllowedProducts") != null) return null;
        var display = doc.Root.Element(ns + "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value ?? name;

        var exeRel = doc.Root.Element(ns + "ExecutableList")?.Elements(ns + "Executable")
            .Where(IsGameExecutable)
            .Select(e => e.Attribute("Name")?.Value)
            .FirstOrDefault(n => n != null)
            ?? FindExeFallback(content);
        if (exeRel == null) return null;
        var exe = Path.GetFullPath(Path.Combine(content, exeRel));

        var familyName = publisher != null ? PackageFamilyName(name, publisher) : null;
        // e.g. Minecraft Launcher. Name-based because nothing else says it: neither MicrosoftGame.config nor
        // appxmanifest.xml has an app type or category (checked on 37 installed titles: the launcher's elements and
        // attributes are the same set the games use), and the GamingServices registry only mirrors the config.
        if (display.EndsWith(" Launcher", StringComparison.OrdinalIgnoreCase)) return null;
        return new Game($"xbox:{familyName ?? name}", display, Store.Xbox, content, exe, version);
    }

    // Names that mark an ExecutableList entry (or a fallback-scan candidate) as a launcher/utility rather than the
    // game itself. "codShip"-style IW bootstrap.exe is the one real exception this misses: it's the only entry in
    // its config, so it's picked anyway (nothing else to prefer over it).
    static readonly string[] HelperHints =
        ["launch", "bootstrap", "helper", "crash", "webview", "uploader", "gpudetection", "hashdatabase", "setup", "redist", "unins", "reporter", "cache_builder"];

    static bool IsGameExecutable(XElement e)
    {
        var n = e.Attribute("Name")?.Value;
        if (n == null || HelperHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase))) return false;
        var family = e.Attribute("TargetDeviceFamily")?.Value;
        if (family != null && !family.Contains("PC", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(e.Attribute("IsDevOnly")?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>No config entry survived the helper filter (e.g. Darktide's config lists only its launcher): pick
    /// the largest non-helper-named exe under Content.</summary>
    static string? FindExeFallback(string content)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(content, "*.exe", opts)
            .Where(f => !HelperHints.Any(h => Path.GetFileName(f).Contains(h, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new FileInfo(f)).MaxBy(f => f.Length)?.FullName;
    }

    /// <summary>"&lt;package family&gt;!&lt;app id&gt;" of an Xbox game (its Content\appxmanifest.xml), null for any other game.</summary>
    public static string? AppUserModelId(Game g)
    {
        if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal)) return null;
        try
        {
            var doc = XDocument.Load(Path.Combine(g.InstallDir, "appxmanifest.xml"));
            var app = doc.Root?.Element(doc.Root.GetDefaultNamespace() + "Applications")?.Elements().FirstOrDefault()?.Attribute("Id")?.Value;
            return app == null ? null : $"{g.Id["xbox:".Length..]}!{app}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
    }

    // PackageFamilyNameFromId (appmodel.h)
    [StructLayout(LayoutKind.Sequential)]
    struct PACKAGE_VERSION { public ulong Version; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PACKAGE_ID
    {
        public uint Reserved, ProcessorArchitecture;
        public PACKAGE_VERSION Version;
        public string? Name, Publisher, ResourceId, PublisherId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int PackageFamilyNameFromId(ref PACKAGE_ID packageId, ref uint packageFamilyNameLength, char[]? packageFamilyName);

    public static string? PackageFamilyName(string name, string publisher)
    {
        var id = new PACKAGE_ID { Name = name, Publisher = publisher };
        uint len = 0;
        PackageFamilyNameFromId(ref id, ref len, null);
        if (len == 0) return null;
        var buf = new char[len];
        return PackageFamilyNameFromId(ref id, ref len, buf) == 0 ? new string(buf, 0, (int)len - 1) : null;
    }
}
