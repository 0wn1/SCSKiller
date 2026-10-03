using System.Runtime.InteropServices;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

// These read the real machine (Steam libraries, NVIDIA driver). Read-only: nothing here writes a driver setting.
public class DiscoveryAndVendorTests(ITestOutputHelper output)
{
    [Trait("Needs", "Game")]
    [Fact]
    public void Steam_finds_ff7_rebirth_with_the_real_game_exe()
    {
        var games = new SteamSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-16} {g.Name,-45} {GameFiles.DetectAntiCheat(g),-14} {g.ExePath}");
        var ff7 = Assert.Single(games, g => g.Id == "steam:2909400");
        Assert.Equal("FINAL FANTASY VII REBIRTH", ff7.Name);
        Assert.EndsWith(@"\End\Binaries\Win64\ff7rebirth_.exe", ff7.ExePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(ff7));
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Steam_leaves_out_tools_and_applications_by_their_app_type()
    {
        if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string root) return;
        var types = SteamSource.AppTypes(Path.Combine(root, "appcache", "appinfo.vdf"), new HashSet<uint> { 250820, 993090, 2909400, 3564860 });
        Assert.NotNull(types);
        Assert.Equal(("Tool", "Application", "Game", "Demo"), (types[250820], types[993090], types[2909400], types[3564860]));
        var ids = new SteamSource().Discover().Select(g => g.Id).ToList();
        Assert.DoesNotContain("steam:250820", ids);   // SteamVR
        Assert.DoesNotContain("steam:993090", ids);   // Lossless Scaling
        Assert.Contains("steam:2909400", ids);
        var libraries = SteamSource.Values(File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf")), "path");
        if (libraries.Any(l => File.Exists(Path.Combine(l, "steamapps", "appmanifest_3564860.acf"))))   // installed on this machine
            Assert.Contains("steam:3564860", ids);    // a demo is played like a game
    }

    [Fact]
    public void Steam_keeps_everything_when_appinfo_is_missing_or_unreadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "scskiller-steam-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
            foreach (var (id, dir) in new[] { (7u, "SomeTool"), (8u, "SomeGame") })
            {
                Directory.CreateDirectory(Path.Combine(apps, "common", dir));
                File.WriteAllBytes(Path.Combine(apps, "common", dir, dir + ".exe"), new byte[1024]);
                File.WriteAllText(Path.Combine(apps, $"appmanifest_{id}.acf"),
                    $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{dir}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n");
            }
            var appinfo = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "appcache")).FullName, "appinfo.vdf");
            string[] Found() => new SteamSource(root).Discover().Select(g => g.Id).Order().ToArray();

            Assert.Equal(["steam:7", "steam:8"], Found());   // no appinfo.vdf
            File.WriteAllBytes(appinfo, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            Assert.Equal(["steam:7", "steam:8"], Found());   // not understood
            File.WriteAllBytes(appinfo, AppInfoV28((7, "Tool"), (8, "Game")));
            Assert.Equal(["steam:8"], Found());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>appinfo.vdf v28: header, then per app id + size + 60 bytes of state/hashes + KeyValues, then app id 0.</summary>
    static byte[] AppInfoV28(params (uint Id, string Type)[] apps)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x07564428u);
        w.Write(1u);
        foreach (var (id, type) in apps)
        {
            var kv = new MemoryStream();
            var k = new BinaryWriter(kv);
            void S(string s) { k.Write(System.Text.Encoding.UTF8.GetBytes(s)); k.Write((byte)0); }
            k.Write(new byte[60]);
            k.Write((byte)0); S("appinfo");
            k.Write((byte)2); S("appid"); k.Write((int)id);
            k.Write((byte)0); S("common");
            k.Write((byte)1); S("name"); S("x");
            k.Write((byte)1); S("type"); S(type);
            k.Write((byte)8); k.Write((byte)8); k.Write((byte)8);
            w.Write(id);
            w.Write((uint)kv.Length);
            w.Write(kv.ToArray());
        }
        w.Write(0u);
        return ms.ToArray();
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Epic_discovery_reads_manifests()
    {
        foreach (var g in new EpicSource().Discover()) output.WriteLine($"{g.Id} {g.Name} {g.ExePath}");
    }

    [Fact]
    public void Vdf_values_are_unescaped()
    {
        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"name\"\t\"a \\\"quoted\\\" b\"\n\t}\n}";
        Assert.Equal(@"C:\Program Files (x86)\Steam", SteamSource.Values(vdf, "path").Single());
        Assert.Equal("a \"quoted\" b", SteamSource.Values(vdf, "name").Single());
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Nvidia_backend_reads_driver_usage_and_cache_limit()
    {
        var v = GpuBackends.Detect();
        output.WriteLine($"{v.Gpu.Name} vendor={v.Vendor} driver={v.Gpu.DriverVersion} luid={v.Gpu.AdapterLuid:X16} vram={v.Gpu.DedicatedVideoMemory >> 20} MiB caps={v.Caps}");
        if (v.Vendor != GpuVendor.Nvidia) return;   // not an NVIDIA machine
        var nv = Assert.IsType<NvidiaBackend>(v);
        Assert.Matches(@"^\d{3}\.\d{2}$", nv.Gpu.DriverVersion);
        Assert.True(nv.Gpu.DedicatedVideoMemory > 1UL << 30);
        Assert.Equal(new VendorCaps("nvidia-1", true, true, true, PerStageCache: true, RtCacheGranularity: RtCacheGranularity.Collection, PackageKeyed: true), nv.Caps);
        var dxgi = GpuBackends.PrimaryAdapter()!;
        Assert.Equal(NvidiaBackend.ReadDriverVersion(), nv.Gpu.DriverVersion);   // earlier builds stored NvAPI's: the same string
        Assert.Equal(nv.Gpu.DriverVersion, NvidiaBackend.FromUserModeVersion(dxgi.DriverVersion));
        var before = nv.Gpu;
        Assert.True(nv.Refresh(dxgi));
        Assert.Equal(before, nv.Gpu);
        // this process's NvAPI keeps answering the running driver: two updates in a row follow DXGI's version
        Assert.True(nv.Refresh(dxgi with { DriverVersion = "32.0.16.1800", AdapterLuid = 1 }));
        Assert.Equal(("618.00", 1L), (nv.Gpu.DriverVersion, nv.Gpu.AdapterLuid));
        Assert.True(nv.Refresh(dxgi with { DriverVersion = "32.0.16.1905" }));
        Assert.Equal("619.05", nv.Gpu.DriverVersion);
        Assert.False(nv.Refresh(dxgi with { DriverVersion = "" }));   // no DXGI version: the last one stays, not NvAPI's older one
        Assert.Equal("619.05", nv.Gpu.DriverVersion);
        Assert.True(nv.Refresh(dxgi));
        Assert.Equal(before, nv.Gpu);
        Assert.Equal(before.DriverVersion, nv.FallbackVersion(dxgi.DriverVersion));

        var usage = nv.GetCacheUsage();
        output.WriteLine($"usage: {usage.Path} {usage.BytesOnDisk / 1048576.0:0} MiB (upper bound {usage.UpperBound})");
        Assert.True(usage.BytesOnDisk > 0);

        var (id, raw, def) = nv.ReadCacheSetting();
        var limit = nv.GetCacheLimit();
        output.WriteLine($"setting 0x{id:X8} raw={(raw is { } r ? $"0x{r:X8}" : "not set")} driver default=0x{def:X8} -> limit {limit}");
        Assert.NotNull(limit);
        Assert.Equal(raw == null, limit.IsDriverDefault);
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Amd_backend_reads_adrenalin_version_and_cache_usage()
    {
        var v = GpuBackends.Detect();
        if (v.Vendor != GpuVendor.Amd) return;   // not an AMD machine
        output.WriteLine($"{v.Gpu.Name} vendor={v.Vendor} driver={v.Gpu.DriverVersion} luid={v.Gpu.AdapterLuid:X16} caps={v.Caps}");
        var amd = Assert.IsType<AmdBackend>(v);
        Assert.Equal(new VendorCaps("amd-1", CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
            PerStageCache: true), amd.Caps);
        // Adrenalin + driver store: "26.8.1 (32.0.31041.1004)", so a hotfix with the same Adrenalin number is a new driver
        Assert.Matches(@"^\d{2}\.\d{1,2}\.\d{1,2} \(\d+\.\d+\.\d+\.\d+\)$", amd.Gpu.DriverVersion);

        var usage = amd.GetCacheUsage();
        long d3d12 = AmdBackend.Bytes(AmdBackend.CacheDir), d3d11 = AmdBackend.Bytes(AmdBackend.D3D11CacheDir);
        output.WriteLine($"usage: {usage.Path} {usage.BytesOnDisk / 1048576.0:0} MiB = D3D12 {d3d12 / 1048576.0:0} + D3D11 {d3d11 / 1048576.0:0} MiB (upper bound {usage.UpperBound})");
        Assert.Equal(AmdBackend.CacheDir, usage.Path);
        Assert.True(usage.UpperBound);
        Assert.True(usage.BytesOnDisk > 0);
        Assert.InRange(usage.BytesOnDisk, Math.Max(d3d12, d3d11), d3d12 + d3d11 + (64L << 20));   // both folders (they may grow meanwhile)
        Assert.Equal(new CacheLimit(16L << 30, false), amd.GetCacheLimit());
        Assert.Throws<NotSupportedException>(() => amd.SetCacheLimit(new CacheLimit(null, true)));
    }

    [Fact]
    public void Amd_the_version_stays_the_registrys_whatever_DXGIs_format_and_waits_for_the_registry_after_an_update()
    {
        (string?, string?) registry = ("26.8.1", "32.0.31041.1004");
        var dxgi = new GpuInfo(GpuVendor.Amd, "AMD Radeon RX 9070 XT", "31.0.24033.1003", 7, 16UL << 30);   // not the store's format
        var amd = new AmdBackend(dxgi, _ => registry);
        Assert.Equal(AmdBackend.FormatVersion("26.8.1", "32.0.31041.1004"), amd.Gpu.DriverVersion);   // what earlier builds stored
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.8.1 (32.0.31041.1004)", amd.Gpu.DriverVersion);

        dxgi = dxgi with { DriverVersion = "31.0.24033.2001", AdapterLuid = 8 };   // installed; the registry not yet
        Assert.False(amd.Refresh(dxgi));
        Assert.False(amd.Refresh(dxgi));
        Assert.Equal(("26.8.1 (32.0.31041.1004)", 8L), (amd.Gpu.DriverVersion, amd.Gpu.AdapterLuid));
        registry = ("26.9.1", "32.0.31051.1001");
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
        Assert.True(amd.Refresh(dxgi));

        registry = ("26.9.1", null);   // a read during an install: the last complete one stays until the registry is back
        Assert.False(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
        registry = (null, null);
        Assert.False(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
        registry = ("26.9.1", "32.0.31051.1001");
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
    }

    [Fact]
    public void Amd_without_a_store_version_in_the_registry_the_version_takes_DXGIs_as_before()
    {
        (string?, string?) registry = ("26.9.1", null);
        var dxgi = new GpuInfo(GpuVendor.Amd, "AMD Radeon RX 9070 XT", "31.0.24033.2001", 7, 16UL << 30);
        var amd = new AmdBackend(dxgi, _ => registry);
        Assert.Equal("26.9.1 (31.0.24033.2001)", amd.Gpu.DriverVersion);
        Assert.True(amd.Refresh(dxgi with { DriverVersion = "31.0.24033.3001" }));
        Assert.Equal("26.9.1 (31.0.24033.3001)", amd.Gpu.DriverVersion);

        registry = (null, null);   // nothing in the registry yet, from the start: DXGI's version, read again at every check
        amd = new AmdBackend(dxgi, _ => registry);
        Assert.Equal("31.0.24033.2001", amd.Gpu.DriverVersion);
        Assert.False(amd.Refresh(dxgi));
        registry = ("26.9.1", "32.0.31051.1001");
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
    }

    [Fact]
    public void Amd_driver_version_is_adrenalin_plus_driver_store()
    {
        Assert.Equal("26.8.1 (32.0.31041.1004)", AmdBackend.FormatVersion("26.8.1", "32.0.31041.1004"));
        Assert.Equal("26.8.1", AmdBackend.FormatVersion("26.8.1", null));
        Assert.Equal("32.0.31041.1004", AmdBackend.FormatVersion(null, "32.0.31041.1004"));
        Assert.Equal("", AmdBackend.FormatVersion("", ""));
        // a hotfix keeps the Adrenalin number but not the store version: a different driver for staleness
        Assert.NotEqual(AmdBackend.FormatVersion("26.8.1", "32.0.31041.1004"), AmdBackend.FormatVersion("26.8.1", "32.0.31041.2001"));
    }

    [Fact]
    public void Nvidia_driver_version_from_the_DXGI_user_mode_version()
    {
        Assert.Equal("617.14", NvidiaBackend.FromUserModeVersion("32.0.16.1714"));
        Assert.Equal("552.22", NvidiaBackend.FromUserModeVersion("31.0.15.5222"));
        Assert.Equal("610.08", NvidiaBackend.FromUserModeVersion("32.0.16.1008"));
        Assert.Equal("560.09", NvidiaBackend.FromUserModeVersion("32.0.15.6009"));
        Assert.Null(NvidiaBackend.FromUserModeVersion(""));
        Assert.Null(NvidiaBackend.FromUserModeVersion("26.8.1"));
    }

    [Fact]
    public void Amd_an_AGS_app_name_keys_the_cache_by_its_FNV_1a()
    {
        Assert.Equal("dxc:dc72f790", AmdAppCache.AppNameKey("Townfall"));   // SILENT HILL: Townfall's own process, handle-verified
        Assert.NotEqual(AmdAppCache.AppNameKey("Townfall"), AmdAppCache.AppNameKey("townfall"));
        Assert.Equal("dxc:12bba8ae", AmdAppCache.DxcKey("Townfall-Win64-Shipping.exe"));   // its exe name's, where a plain device lands
        Assert.Equal(AmdAppCache.DxcAppHash("selftest.exe"), AmdAppCache.Fnv1a("selftest.exe"));
        // handle-verified: an app-name profile wins, then an exe-name profile, then the app name's hash
        Assert.Equal("dxc:dc72f790", AmdAppCache.AgsKey("Townfall-Win64-Shipping.exe", "Townfall"));
        Assert.Equal("dxc:d32786a7", AmdAppCache.AgsKey("HogwartsLegacy.exe", "Phoenix"));
        Assert.Equal("dxc:f2f80824", AmdAppCache.AgsKey("Wonderlands.exe", "OakGame"));
        Assert.Equal("dxc:f2f80824", AmdAppCache.AgsKey("agsprobe.exe", "OakGame"));
        Assert.Equal("dxc:85c2b2e5", AmdAppCache.AgsKey("Wonderlands.exe", "scskAgsA"));
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.AgsKey("ff7rebirth_.exe", "scskAgsA"));
        Assert.Equal("dxc:9832c88a", AmdAppCache.AgsKey("agsprobe.exe", "End"));
        Assert.Equal("dxc:15d4f9d9", AmdAppCache.AgsKey("agsprobe.exe", "scskAgsA"));
        Assert.True(AmdAppCache.ProvenAgsApp("Townfall"));
        Assert.False(AmdAppCache.ProvenAgsApp("townfall"));
        Assert.False(AmdAppCache.ProvenAgsApp("OakGame"));
    }

    [Fact]
    public void Amd_cache_file_names_map_to_app_keys()
    {
        // FNV-1a-32 of the UTF-16LE exe name, case-sensitive: keys observed in DxcCache on AMD
        Assert.Equal(0x207c35a9u, AmdAppCache.DxcAppHash("selftest.exe"));
        Assert.Equal(0x3c98ec7du, AmdAppCache.DxcAppHash("chrome.exe"));
        Assert.Equal(0x7d588db3u, AmdAppCache.DxcAppHash("steamwebhelper.exe"));
        Assert.Equal(0xab5f8ae4u, AmdAppCache.DxcAppHash(@"C:\x\amdtestA.exe"));   // the path doesn't matter
        Assert.Equal(0xcff591a4u, AmdAppCache.DxcAppHash("AMDTESTA.EXE"));          // the case does
        Assert.Equal("dxc:207c35a9", AmdAppCache.DxcKey("selftest.exe"));

        // Driver app profiles override the name hash (measured): the hash is only a hint
        Assert.Equal(0xf63a0641u, AmdAppCache.DxcAppHash("ff7rebirth_.exe"));        // what the name would give...
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.HintKey("ff7rebirth_.exe"));        // ...and the profile's fixed key
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.HintKey(@"D:\x\FF7REBIRTH_.EXE"));  // any case, any "ff7rebirth*" name
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.ProfileKey("ff7rebirth_demo.exe"));
        Assert.Equal("dxc:81de6978", AmdAppCache.HintKey("Cyberpunk2077.exe"));
        Assert.Equal("dxc:b73b6179", AmdAppCache.HintKey("eldenring.exe"));
        Assert.Null(AmdAppCache.ProfileKey("ff7remake_.exe"));                       // name-hashed
        Assert.Null(AmdAppCache.ProfileKey("bg3.exe"));
        Assert.Equal(AmdAppCache.DxcKey("bg3.exe"), AmdAppCache.HintKey("bg3.exe"));
        Assert.Equal(true, AmdAppCache.IsNameHashed(["dxc:207c35a9", "dx:0d67622373f61b94"], "selftest.exe"));
        Assert.Equal(false, AmdAppCache.IsNameHashed(["dxc:6b2fcd83"], "ff7rebirth_.exe"));
        Assert.Null(AmdAppCache.IsNameHashed(["dx:0d67622373f61b94", "11111111"], "selftest.exe"));   // no D3D12 key learned

        Assert.Equal("dxc:207c35a9", AmdAppCache.Key("207c35a9.dfac411e.71efbc0e.2b1a674a.0.parc", d3d12: true));
        Assert.Equal("dxc:031116bd", AmdAppCache.Key("31116bd.caaa38cb.a4a986a3.cef507b2.1.parc", d3d12: true));   // no leading zeros on disk
        Assert.Equal("dx:0d67622373f61b94", AmdAppCache.Key("d67622373f61b94.f2d9161043adafe8.7b1f799a.dad1858.0.parc", d3d12: false));
        Assert.Null(AmdAppCache.Key("1b60938d3b6cb660.f2d9161043adafe8.7b1f799a.dad1858.0.parc", d3d12: true));   // 64-bit: not a DxcCache key
        Assert.Null(AmdAppCache.Key("207c35a9.dfac411e.71efbc0e.2b1a674a.parc", d3d12: true));
        Assert.Null(AmdAppCache.Key("0002a91d69d04596.nvph", d3d12: true));

        var root = Path.Combine(Path.GetTempPath(), "scsk-amd-" + Guid.NewGuid().ToString("N"));
        var (dxc, dx) = (Path.Combine(root, "DxcCache"), Path.Combine(root, "DxCache"));
        Directory.CreateDirectory(dxc);
        Directory.CreateDirectory(dx);
        try
        {
            foreach (var n in new[] { "ab5f8ae4.dfac411e.71efbc0e.2b1a674a.0.parc", "ab5f8ae4.dfac411e.a4a986a3.2b1a674a.0.parc",
                                      "ab5f8ae4.dfac411e.a4a986a3.2b1a674a.1.parc", "6528721f.dfac411e.71efbc0e.2b1a674a.0.parc" })
                File.WriteAllBytes(Path.Combine(dxc, n), new byte[4096]);
            File.WriteAllBytes(Path.Combine(dx, "1b60938d3b6cb660.f2d9161043adafe8.7b1f799a.dad1858.0.parc"), new byte[16384]);
            var cache = new AmdAppCache(dxc, dx);
            Assert.Equal(3, cache.FilesOf([AmdAppCache.HintKey("amdtestA.exe")]).Count);   // both kinds, both slots
            Assert.Empty(cache.FilesOf([AmdAppCache.HintKey("AMDTESTA.EXE")]));
            string[] keys = ["dxc:ab5f8ae4", "dx:1b60938d3b6cb660"];
            Assert.Equal(3 * 4096 + 16384, cache.SizeOf(keys));
            Assert.Empty(cache.KeysOpenBy("scsk-no-such-process.exe"));

            using (File.Open(Path.Combine(dxc, "ab5f8ae4.dfac411e.a4a986a3.2b1a674a.1.parc"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var e = Assert.Throws<InvalidOperationException>(() => cache.Delete(keys));   // one file in use: nothing is deleted
                output.WriteLine(e.Message);
                Assert.Equal(4, cache.FilesOf(keys).Count);
            }
            Assert.Equal(4, cache.Delete(keys));
            Assert.Equal(["6528721f.dfac411e.71efbc0e.2b1a674a.0.parc"], Directory.GetFiles(dxc).Select(Path.GetFileName));
            Assert.Empty(Directory.GetFiles(dx));
        }
        finally { Directory.Delete(root, true); }
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Xbox_finds_the_gdk_titles_with_the_real_game_exe_not_launchers()
    {
        var games = new XboxSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-55} {g.Name,-40} {g.Version,-16} {GameFiles.DetectAntiCheat(g),-14} {g.ExePath}");
        if (games.Count == 0) return;   // this machine's Xbox app has no games (or C:/D:\XboxGames don't exist)

        // Atomfall's config declares Launcher/Atomfall.exe as Id="Game", an API-picking launcher stub (the "launch" helper
        // hint rejects it); the real D3D12 exe is Atomfall_dx12.exe (docs/engine-survey.md). Each title is checked only
        // where it is installed.
        if (games.SingleOrDefault(g => g.Name == "Atomfall") is { } atomfall)
        {
            Assert.EndsWith(@"\bin\Atomfall_dx12.exe", atomfall.ExePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("xbox:Rebellion.Windscale_2vbwqmt31j4mr", atomfall.Id);
        }

        // Darktide's config lists only launcher\launcher.exe -> the fallback scan must skip it for the real binary
        if (games.SingleOrDefault(g => g.Name.Contains("Darktide")) is { } darktide)
            Assert.EndsWith(@"\binaries\Darktide.exe", darktide.ExePath, StringComparison.OrdinalIgnoreCase);

        // Halo MCC lists mcclauncher.exe (a launcher by name) and the direct Shipping exe -> pick the latter
        if (games.SingleOrDefault(g => g.Name.Contains("Master Chief Collection")) is { } mcc)
            Assert.EndsWith("MCCWinStore-Win64-Shipping.exe", mcc.ExePath, StringComparison.OrdinalIgnoreCase);

        if (games.SingleOrDefault(g => g.Name == "Starfield") is { } starfield)
        {
            Assert.EndsWith(@"\Starfield.exe", starfield.ExePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1.14.74.0", starfield.Version);
        }
    }

    /// <summary>A PE that imports <paramref name="dll"/> (one import descriptor at RVA 0x1100, its name at 0x1180), padded by
    /// <paramref name="size"/> bytes.</summary>
    internal static byte[] Exe(string? dll, int size = 0)
    {
        var pe = Planning.MiddlewarePackTests.Pe(null, new byte[size]);
        if (dll == null) return pe;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x58 + 120), 0x1100);   // import directory
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x58 + 124), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x300 + 12), 0x1180);
        System.Text.Encoding.ASCII.GetBytes(dll).CopyTo(pe, 0x380);
        return pe;
    }

    /// <summary>launcher-configuration.json as CD PROJEKT RED's launcher ships it with The Witcher 3 (Steam).</summary>
    internal static string RedConfig(string fallback = "DirectX 12", params (string Description, string Dir)[] entries) =>
        $$"""
        { "revision": 3, "executables": [{{string.Join(",", (entries.Length > 0 ? entries : [("DirectX 12", @"bin\\x64_dx12")]).Select(e =>
            $$"""{ "description": "{{e.Description}}", "executable": { "directoryPath": "{{e.Dir}}", "fileName": "witcher3.exe" } }"""))}}],
          "gameId": "witcher3", "platform": "steam", "fallback": "{{fallback}}", "editions": [{ "name": "remasteredEdition" }] }
        """;

    [Trait("Needs", "Game")]
    [Fact]
    public void Steam_finds_the_witcher_3_with_the_real_exe_not_the_prelauncher()
    {
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:292030") is not { } w3) return;   // not installed here
        Assert.EndsWith(@"\bin\x64_dx12\witcher3.exe", w3.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exe_discovery_takes_the_game_cdpr_s_launcher_configuration_names()
    {
        var tmp = Directory.CreateTempSubdirectory("scskiller-launcher-test-").FullName;
        var root = Path.Combine(tmp, "The Witcher 3");
        try
        {
            void Put(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), bytes); }
            Put(@"..\Other\witcher3.exe", Exe("d3d12.dll", 100));
            Put("REDprelauncher.exe", Exe("Qt5Core.dll", 100));
            Put(@"bin\x64_dx12\witcher3.exe", Exe("sl.interposer.dll", 5000));
            Put(@"bin\x64_dx12\D3D12_0\d3dconfig.exe", Exe(null, 200));
            Put(@"bin\x64_dx12\crashreporter\CrashReporter.exe", Exe("mscoree.dll", 300));
            Put(@"bin\x64\witcher3.exe", Exe("d3d11.dll", 4000));
            var config = Path.Combine(root, "launcher-configuration.json");
            string Dx(string dir) => Path.Combine(root, dir, "witcher3.exe");

            File.WriteAllText(config, RedConfig());
            Assert.Equal(Dx(@"bin\x64_dx12"), GameFiles.FindExe(root));
            Assert.Equal(Dx(@"bin\x64_dx12"), GameFiles.FindExe(root, "REDprelauncher.exe"));   // a store naming the launcher
            File.WriteAllText(config, RedConfig("DirectX 12", ("DirectX 11", @"bin\\x64"), ("DirectX 12", @"bin\\x64_dx12")));
            Assert.Equal(Dx(@"bin\x64_dx12"), GameFiles.FindExe(root));
            File.WriteAllText(config, RedConfig("Vulkan", ("DirectX 11", @"bin\\x64"), ("DirectX 12", @"bin\\x64_dx12")));
            Assert.Equal(Dx(@"bin\x64"), GameFiles.FindExe(root));   // no entry is the fallback: the first

            // a target that's missing or outside the install, or a config that doesn't parse: the launcher stays (two exes import a graphics API)
            // a junction inside the install that leads to another folder: as outside
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                       $"/c mklink /J \"{Path.Combine(root, "linked")}\" \"{Path.Combine(tmp, "Other")}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            foreach (var bad in new[] { RedConfig("DirectX 12", ("DirectX 12", @"bin\\gone")), RedConfig("DirectX 12", ("DirectX 12", @"..\\Other")),
                         RedConfig("DirectX 12", ("DirectX 12", "linked")), "{ not json" })
            {
                File.WriteAllText(config, bad);
                Assert.Equal(Path.Combine(root, "REDprelauncher.exe"), GameFiles.FindExe(root));
            }
        }
        finally
        {
            if (Directory.Exists(Path.Combine(root, "linked"))) Directory.Delete(Path.Combine(root, "linked"));   // the junction, not its target
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Exe_discovery_takes_the_one_exe_that_imports_a_graphics_api_over_a_launcher_that_doesn_t()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-launcher-test-").FullName;
        try
        {
            void Put(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), bytes); }
            var launcher = Path.Combine(root, "Launcher.exe");
            var game = Path.Combine(root, "Game", "Bin", "Game.exe");
            Put("Launcher.exe", Exe("user32.dll", 1000));
            Put(@"Game\Bin\Game.exe", Exe("d3d12.dll", 5000));
            Put(@"Game\Bin\Small.exe", Exe("dxgi.dll", 10));             // smaller than the launcher: not the game
            Put(@"Game\Bin\CrashHandler.exe", Exe("d3d11.dll", 9000));   // a crash handler is never the game
            Assert.Equal(game, GameFiles.FindExe(root));

            Put(@"Tools\Editor.exe", Exe("d3d11.dll", 6000));
            Assert.Equal(launcher, GameFiles.FindExe(root));   // two candidates: the guess stays
            File.Delete(Path.Combine(root, "Tools", "Editor.exe"));
            Put(@"Tools\Locked.exe", new byte[3000]);
            Assert.Equal(launcher, GameFiles.FindExe(root));   // a larger exe whose imports can't be read may be the game
            File.Delete(Path.Combine(root, "Tools", "Locked.exe"));

            Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Exe(null, 10));
            Assert.Equal(launcher, GameFiles.FindExe(root));   // anti-cheat in the install: no other binary is read
            Directory.Delete(Path.Combine(root, "EasyAntiCheat"), true);

            Put("Launcher.exe", Exe("dxgi.dll", 1000));
            Assert.Equal(launcher, GameFiles.FindExe(root));   // the guess imports one itself
            Put("Launcher.exe", new byte[1000]);
            Assert.Equal(launcher, GameFiles.FindExe(root));   // not a readable PE: unknown, kept
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Xbox_package_family_name_matches_the_real_appx_identity()
    {
        // Known-good values read from Get-AppxPackage on this machine: PackageFamilyNameFromId must reproduce them
        // from just the manifest's Identity name + publisher, with no package-manager query.
        Assert.Equal("Rebellion.Windscale_2vbwqmt31j4mr", XboxSource.PackageFamilyName("Rebellion.Windscale", "CN=9136491E-6A28-4C39-989B-12D6D89FB1B3"));
        Assert.Equal("Microsoft.SeaofThieves_8wekyb3d8bbwe", XboxSource.PackageFamilyName("Microsoft.SeaofThieves",
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
        Assert.Equal("FocusHomeInteractiveSA.APlagueTaleFelons_4hny5m903y3g0",
            XboxSource.PackageFamilyName("FocusHomeInteractiveSA.APlagueTaleFelons", "CN=244B08DA-6A27-4DCD-99EF-F6DCBE2A0C28"));
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Gog_finds_cyberpunk_with_the_real_exe_not_the_prelauncher()
    {
        var games = new GogSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-16} {g.Name,-40} {g.Version,-16} {g.ExePath}");
        if (games.Count == 0) return;   // GOG isn't installed on this machine

        var cp = Assert.Single(games, g => g.Id == "gog:1423049311");
        Assert.Equal("Cyberpunk 2077", cp.Name);
        Assert.EndsWith(@"\bin\x64\Cyberpunk2077.exe", cp.ExePath, StringComparison.OrdinalIgnoreCase);
        // the Phantom Liberty DLC registers its own id in the same folder but has no "game" playTask -> not a Game
        Assert.DoesNotContain(games, g => g.Id == "gog:1256837418");
    }

    [Fact]
    public void Ea_finds_finished_installs_with_the_exe_the_manifest_names()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-ea-test-").FullName;
        try
        {
            var reg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Install(string name, bool staged = false, bool touchedUp = true)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                Directory.CreateDirectory(Path.Combine(dir, "__Installer"));
                Directory.CreateDirectory(Path.Combine(dir, "bin"));
                File.WriteAllBytes(Path.Combine(dir, "bin", "Game.exe"), new byte[100]);
                File.WriteAllBytes(Path.Combine(dir, "Tool.exe"), new byte[5000]);   // bigger, at the root: FindExe's own guess
                var key = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Fake\{name}\Install Dir";
                File.WriteAllText(Path.Combine(dir, "__Installer", "installerdata.xml" + (staged ? "_DiP_Staged" : "")), $"""
                    <?xml version='1.0' encoding='utf-8'?>
                    <DiPManifest version="4.0">
                      <buildMetaData><gameVersion version="1.0.0.11" /></buildMetaData>
                      <contentIDs><contentID>{name.Length}00</contentID></contentIDs>
                      <gameTitles><gameTitle locale="fr_FR">Le {name}</gameTitle><gameTitle locale="en_US">{name}™</gameTitle></gameTitles>
                      <runtime><launcher uid="1-1"><filePath>[{key}]bin\Game.exe</filePath><trial>0</trial></launcher></runtime>
                    </DiPManifest>
                    """);
                if (touchedUp) reg[key] = dir + @"\";
                return dir;
            }
            var done = Install("Done");
            Install("Downloading", staged: true, touchedUp: false);   // mid-download: every file is *_DiP_Staged
            Install("Destaged", touchedUp: false);                    // files in place, the installer's touchup not run yet
            reg[@"HKEY_LOCAL_MACHINE\SOFTWARE\Fake\Moved\Install Dir"] = @"X:\Elsewhere";
            Install("Moved", touchedUp: false);                       // a stale copy: the registry names another folder

            var games = new EaSource(Directory.EnumerateDirectories(root), k => reg.GetValueOrDefault(k)).Discover();
            Assert.Equal(new Game("ea:400", "Done", Store.EA, done, Path.Combine(done, "bin", "Game.exe"), "1.0.0.11"), Assert.Single(games));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A walk that runs out of time is not known to be clean; a missing install is.</summary>
    [Fact]
    public void An_anti_cheat_check_out_of_time_is_other()
    {
        var dir = Directory.CreateTempSubdirectory("scskiller-anticheat-budget-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Game.exe"), new byte[100]);
            var game = new Game("test:budget", "Budget", Store.Other, dir, Path.Combine(dir, "Game.exe"));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game, budget: TimeSpan.Zero));
            var gone = game with { InstallDir = Path.Combine(dir, "gone"), ExePath = Path.Combine(dir, "gone", "Game.exe") };
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(gone));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Hidden_and_system_anti_cheat_markers_are_found()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            Game Install(string name, Action<string> mark)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                File.WriteAllBytes(Path.Combine(dir, "Game.exe"), new byte[100]);
                mark(dir);
                return new Game($"test:{name}", name, Store.Other, dir, Path.Combine(dir, "Game.exe"));
            }
            var eac = Install("eac", d => File.SetAttributes(Directory.CreateDirectory(Path.Combine(d, "EasyAntiCheat")).FullName, FileAttributes.Directory | FileAttributes.Hidden));
            var ricochet = Install("ricochet", d =>
            {
                File.WriteAllBytes(Path.Combine(d, "randgrid.sys"), [0]);
                File.SetAttributes(Path.Combine(d, "randgrid.sys"), FileAttributes.System | FileAttributes.Hidden);
            });
            var below = Install("below", d =>   // inside a hidden folder one level down
            {
                var hidden = Directory.CreateDirectory(Path.Combine(d, "bin")).FullName;
                File.WriteAllBytes(Path.Combine(hidden, "BEService_x64.exe"), [0]);
                File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
            });
            var deep = Install("deep", d =>   // <install>\support\security, away from the exe's folder
                File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(d, "support", "security")).FullName, "BEService_x64.exe"), [0]));
            var clean = Install("clean", _ => { });
            var exeElsewhere = clean with { Id = "test:elsewhere", ExePath = Path.Combine(root, "eac", "Game.exe") };   // the exe's folder is outside the install

            Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(eac));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(ricochet));
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(below));
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(deep));
            Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(exeElsewhere));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(clean));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(f, File.GetAttributes(f) & FileAttributes.Directory);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_install_folder_that_cannot_be_listed_is_not_known_to_be_clean()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-acl-test-").FullName;
        var locked = Directory.CreateDirectory(Path.Combine(root, "support")).FullName;
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Game.exe"), new byte[100]);
            var game = new Game("test:locked", "Locked", Store.Other, root, Path.Combine(root, "Game.exe"));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));

            var acl = new DirectoryInfo(locked).GetAccessControl();
            acl.AddAccessRule(deny);
            new DirectoryInfo(locked).SetAccessControl(acl);
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));   // whatever it holds: never installed into
        }
        finally
        {
            var acl = new DirectoryInfo(locked).GetAccessControl();
            acl.RemoveAccessRule(deny);
            new DirectoryInfo(locked).SetAccessControl(acl);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_install_root_that_cannot_be_listed_is_not_known_to_be_clean()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-root-test-").FullName;
        var install = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
        var exeDir = Directory.CreateDirectory(Path.Combine(root, "Launcher")).FullName;   // the exe outside the install
        File.WriteAllBytes(Path.Combine(exeDir, "Game.exe"), new byte[100]);
        var game = new Game("test:root", "Root", Store.Other, install, Path.Combine(exeDir, "Game.exe"));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        System.Security.AccessControl.FileSystemAccessRule Deny(System.Security.AccessControl.FileSystemRights r) =>
            new(me, r, System.Security.AccessControl.AccessControlType.Deny);
        // the install can't be listed nor its attributes read, and its parent can't be listed: Directory.Exists says false
        var (installDeny, parentDeny) = (Deny(System.Security.AccessControl.FileSystemRights.ListDirectory | System.Security.AccessControl.FileSystemRights.ReadAttributes),
            Deny(System.Security.AccessControl.FileSystemRights.ListDirectory));
        void Acl(string dir, System.Security.AccessControl.FileSystemAccessRule rule, bool add)
        {
            var acl = new DirectoryInfo(dir).GetAccessControl();
            if (add) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            new DirectoryInfo(dir).SetAccessControl(acl);
        }
        try
        {
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
            Acl(install, installDeny, true);
            Acl(root, parentDeny, true);
            Assert.False(Directory.Exists(install));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game, quick: true));
        }
        finally
        {
            Acl(root, parentDeny, false);
            Acl(install, installDeny, false);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Junctions_are_names_not_folders_to_walk()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-link-test-").FullName;
        var links = new List<string>();
        try
        {
            void Junction(string link, string target)
            {
                links.Add(link);
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            var protectedGame = Directory.CreateDirectory(Path.Combine(root, "Protected", "EasyAntiCheat")).Parent!.FullName;
            var install = Directory.CreateDirectory(Path.Combine(root, "Clean", "bin")).Parent!.FullName;
            File.WriteAllBytes(Path.Combine(install, "bin", "Game.exe"), new byte[100]);
            var game = new Game("test:clean", "Clean", Store.Other, install, Path.Combine(install, "bin", "Game.exe"));
            Junction(Path.Combine(install, "shared"), protectedGame);   // into another game's install: not this game's files
            Junction(Path.Combine(install, "bin", "loop"), install);    // a cycle
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));

            Junction(Path.Combine(install, "BattlEye"), protectedGame);   // its name still counts
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(game));

            // the exe's own folder is a link: <install>\bin -> elsewhere\GameBin, with Game.exe and support\EasyAntiCheat
            var binTarget = Directory.CreateDirectory(Path.Combine(root, "elsewhere", "GameBin", "support", "EasyAntiCheat")).Parent!.Parent!.FullName;
            File.WriteAllBytes(Path.Combine(binTarget, "Game.exe"), new byte[100]);
            var linked = Directory.CreateDirectory(Path.Combine(root, "Linked")).FullName;
            Junction(Path.Combine(linked, "bin"), binTarget);
            var viaLink = new Game("test:linked", "Linked", Store.Other, linked, Path.Combine(linked, "bin", "Game.exe"));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(viaLink));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(viaLink, quick: true));
        }
        finally
        {
            foreach (var link in links.Where(Directory.Exists)) Directory.Delete(link);   // the link only, never its target
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Launcher_ids_never_name_a_folder_outside_the_data_folder()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-ids-test-").FullName;
        try
        {
            var store = new Core.App.AppStore(Path.Combine(root, "data"));
            var games = Path.GetFullPath(Path.Combine(root, "data", "games"));
            var install = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
            File.WriteAllBytes(Path.Combine(install, "Game.exe"), new byte[100]);
            const string up = @"..\..\..\..\..\..\..\..\XboxGames\Demo\Content";

            var manifests = Directory.CreateDirectory(Path.Combine(root, "Manifests")).FullName;   // Epic: the manifest's AppName
            File.WriteAllText(Path.Combine(manifests, "a.item"), System.Text.Json.JsonSerializer.Serialize(new { AppName = @"x\" + up, InstallLocation = install }));
            File.WriteAllText(Path.Combine(manifests, "b.item"), System.Text.Json.JsonSerializer.Serialize(new { AppName = @"C:\Windows", InstallLocation = install }));
            Directory.CreateDirectory(Path.Combine(install, "__Installer"));   // EA: the installer's contentID
            File.WriteAllText(Path.Combine(install, "__Installer", "installerdata.xml"),
                $"<DiPManifest><contentIDs><contentID>x/{up.Replace('\\', '/')}</contentID></contentIDs><runtime><launcher><filePath>Game.exe</filePath></launcher></runtime></DiPManifest>");
            var ids = new EpicSource(manifests).Discover().Concat(new EaSource([install], _ => null).Discover()).Select(g => g.Id).ToList();
            Assert.Equal(3, ids.Count);

            // Xbox: Identity.Name when the manifest has no Publisher; the rest are what a GameDir caller may pass
            string[] unsafeIds = [.. ids, $@"xbox:x\{up}", "xbox:..", "xbox:x. ", "..", ".", @"\\server\share", "steam:1/../../x", "epic:Demo/A", "%x", ""];
            string[] plainIds = ["epic:Demo_A", "epic:Demo:A", "epic:a:b", "epic:a%3Ab", "a_b"];
            foreach (var id in unsafeIds.Concat(plainIds))
            {
                var dir = Path.GetFullPath(store.GameDir(id));
                Assert.Equal(games, Path.GetDirectoryName(dir), StringComparer.OrdinalIgnoreCase);
                Assert.Equal(store.GameDir(id), store.GameDir(Core.App.AppStore.GameId(Path.GetFileName(dir))));   // the uninstall hook's way back
                Assert.Equal(unsafeIds.Contains(id), Path.GetFileName(dir).StartsWith('%'));
            }
            Assert.Equal(unsafeIds.Length, unsafeIds.Select(id => store.GameDir(id).ToUpperInvariant()).Distinct().Count());
            // the folders main used, whenever they are plain names; two ids main gave one folder still share it
            Assert.Equal(Path.Combine(games, "steam_1245620"), store.GameDir("steam:1245620"));
            Assert.Equal(Path.Combine(games, "xbox_Pub.Game_8wekyb3d8bbwe"), store.GameDir("xbox:Pub.Game_8wekyb3d8bbwe"));
            Assert.Equal(Path.Combine(games, "ea_Origin.OFR.50.0004321"), store.GameDir("ea:Origin.OFR.50.0004321"));
            Assert.Equal(Path.Combine(games, "epic_Demo_A"), store.GameDir("epic:Demo:A"));
            Assert.Equal(store.GameDir("epic:Demo_A"), store.GameDir("epic:Demo:A"));
            Assert.NotEqual(store.GameDir("epic:Demo/A"), store.GameDir("epic:Demo_A"));
            Assert.Equal(Path.Combine(games, "epic_a_b"), store.GameDir("epic:a:b"));
            Assert.Equal(Path.Combine(games, "epic_a%3Ab"), store.GameDir("epic:a%3Ab"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Ea_finds_jedi_survivor_once_its_download_is_done()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dirs = EaSource.InstallDirs().ToList();
        var games = new EaSource().Discover();
        output.WriteLine($"{dirs.Count} candidate folders, {sw.ElapsedMilliseconds} ms");
        foreach (var g in games) output.WriteLine($"{g.Id,-12} {g.Name,-30} {g.Version,-12} {GameFiles.DetectAntiCheat(g),-8} {g.ExePath}");
        const string jedi = @"D:\EA\Jedi Survivor";
        if (!Directory.Exists(jedi)) return;   // not on this machine
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var b = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var k = b.OpenSubKey(@"SOFTWARE\Respawn\Jedi Survivor");
            using var u = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B9CBE70C-C93E-467A-B112-D126650B08A5}");
            output.WriteLine($"{view}: Install Dir = {k?.GetValue("Install Dir")}, uninstall InstallLocation = {u?.GetValue("InstallLocation")}");
        }
        var touchedUp = EaSource.ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Jedi Survivor\Install Dir") != null;
        if (!File.Exists(Path.Combine(jedi, "__Installer", "installerdata.xml")) || !touchedUp)
        {
            output.WriteLine("Jedi Survivor: not installed yet (download in progress, or the installer's touchup not run)");
            Assert.DoesNotContain(games, g => g.InstallDir.Equals(jedi, StringComparison.OrdinalIgnoreCase));
            return;
        }
        var game = Assert.Single(games, g => g.Id == "ea:198300");
        Assert.EndsWith(@"\SwGame\Binaries\Win64\JediSurvivor.exe", game.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Ubisoft_discovery_skips_gracefully_when_connect_is_not_installed()
    {
        var games = new UbisoftSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id} {g.Name} {g.ExePath}");
        // Ubisoft Connect itself isn't installed on this machine (D:\Ubisoft is a bare folder, no registry Installs
        // key), so this only proves Discover() doesn't throw; a machine with Connect installed exercises the rest.
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void BattleNet_finds_overwatch_conservatively_marked_as_anti_cheat()
    {
        var games = new BattleNetSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-20} {g.Name,-20} {GameFiles.DetectAntiCheat(g)} {g.ExePath}");
        if (games.Count == 0) return;   // Battle.net isn't installed on this machine

        var ow = Assert.Single(games, g => g.Name == "Overwatch");
        Assert.Equal("battlenet:prometheus", ow.Id);
        Assert.True(File.Exists(ow.ExePath), ow.ExePath);
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(ow));   // Warden: conservative, not file-based
    }

    [Fact]
    public void Nvidia_cache_size_encoding()
    {
        Assert.Equal(new CacheLimit(100L << 30, false), NvidiaBackend.Decode(0x19000, false));   // NVCP "100 GB"
        Assert.Equal(new CacheLimit(null, false), NvidiaBackend.Decode(0xFFFFFFFF, false));
        Assert.Equal(0x19000u, NvidiaBackend.Encode(new CacheLimit(100L << 30, false)));
        Assert.Equal(0xFFFFFFFFu, NvidiaBackend.Encode(new CacheLimit(null, false)));
        Assert.Equal(1u, NvidiaBackend.Encode(new CacheLimit(1, false)));   // rounds up to whole MiB
    }

    [Fact]
    public void Nvidia_auto_shader_compilation_decoding()
    {
        Assert.Equal(AutoShaderCompilation.Off, NvidiaBackend.DecodeAutoShaderCompilation(null));   // not set = the App's default
        Assert.Equal(AutoShaderCompilation.Off, NvidiaBackend.DecodeAutoShaderCompilation(0));
        Assert.Equal(AutoShaderCompilation.Low, NvidiaBackend.DecodeAutoShaderCompilation(1));
        Assert.Equal(AutoShaderCompilation.Medium, NvidiaBackend.DecodeAutoShaderCompilation(2));
        Assert.Equal(AutoShaderCompilation.High, NvidiaBackend.DecodeAutoShaderCompilation(3));
        Assert.Null(NvidiaBackend.DecodeAutoShaderCompilation(4));   // unknown: shown as not readable, never guessed
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Nvidia_auto_shader_compilation_reads_without_changing_anything()
    {
        // Read-only: GetSetting on the base profile + schtasks /Query. Never call SetAutoShaderCompilation from tests
        // (a user decision: it writes a global driver setting and runs NvOSC.exe, which registers a scheduled task).
        if (GpuBackends.Detect() is not NvidiaBackend nv) return;
        var state = nv.GetAutoShaderCompilation();
        output.WriteLine($"Auto Shader Compilation: {state?.ToString() ?? "not readable"}, NvOSC.exe: {NvidiaBackend.NvOscPath() ?? "not found"}");
        Assert.NotNull(state);
        Assert.NotNull(NvidiaBackend.NvOscPath());   // r610+: in the active driver's DriverStore folder
    }

    // Fake NvAPI functions for the argument-layout tests: record what they get, answer what the test says.
    static int fakeRc;
    static uint fakeValue, gotId, gotVersion, gotType, gotValue, gotA, gotB;
    static nint gotSession, gotProfile;
    static bool gotFlags, publicCalled;

    [UnmanagedCallersOnly]
    static unsafe int FakeGetEx(nint session, nint profile, uint id, byte* s, uint* flags)
    {
        (gotSession, gotProfile, gotId, gotVersion, gotFlags) = (session, profile, id, *(uint*)s, flags != null && *flags == 0);
        *(uint*)(s + 8220) = fakeValue;
        return fakeRc;
    }

    [UnmanagedCallersOnly]
    static unsafe int FakeGet(nint session, nint profile, uint id, byte* s)
    {
        publicCalled = true;
        return -160;   // what the public GetSetting answers for the hidden id, set or not
    }

    [UnmanagedCallersOnly]
    static unsafe int FakeSetEx(nint session, nint profile, byte* s, uint a, uint b)
    {
        (gotSession, gotProfile, gotVersion, gotId, gotType, gotValue, gotA, gotB) =
            (session, profile, *(uint*)s, *(uint*)(s + 4100), *(uint*)(s + 4104), *(uint*)(s + 8220), a, b);
        return 0;
    }

    [Fact]
    public unsafe void Nvidia_auto_shader_ex_calls_pass_the_nvcplplugin_argument_layout()
    {
        nint getEx = (nint)(delegate* unmanaged<nint, nint, uint, byte*, uint*, int>)&FakeGetEx;
        nint get = (nint)(delegate* unmanaged<nint, nint, uint, byte*, int>)&FakeGet;
        nint setEx = (nint)(delegate* unmanaged<nint, nint, byte*, uint, uint, int>)&FakeSetEx;

        (fakeRc, fakeValue, publicCalled) = (0, 2, false);
        Assert.Equal(AutoShaderCompilation.Medium, NvidiaBackend.ReadAutoShaderLevel(11, 22, getEx, get));
        Assert.Equal((11, 22, 0x00EAD189u, 0x13020u, true), (gotSession, gotProfile, gotId, gotVersion, gotFlags));   // NVDRS_SETTING_V1, flags in = 0
        Assert.False(publicCalled);
        (fakeRc, fakeValue) = (-160, 0);
        Assert.Equal(AutoShaderCompilation.Off, NvidiaBackend.ReadAutoShaderLevel(11, 22, getEx, get));   // Ex sees hidden ids: not set = Off
        Assert.Null(NvidiaBackend.ReadAutoShaderLevel(11, 22, 0, get));   // older driver, public only: unknown, not Off
        fakeRc = -5;
        Assert.Null(NvidiaBackend.ReadAutoShaderLevel(11, 22, getEx, get));   // both fail: unknown

        Assert.Equal(0, NvidiaBackend.WriteAutoShaderLevel(33, 44, setEx, AutoShaderCompilation.High));
        Assert.Equal((33, 44, 0x13020u, 0x00EAD189u, 0u, 3u, 0u, 0u), (gotSession, gotProfile, gotVersion, gotId, gotType, gotValue, gotA, gotB));
        Assert.Equal(0x13020u, NvidiaBackend.SettingVersion1);
    }

    [Fact]
    public void Nvidia_auto_shader_compilation_refused_while_the_cache_is_disabled()
    {
        var disabled = new CacheLimit(0, false);
        Assert.NotNull(NvidiaBackend.RefuseReason(AutoShaderCompilation.Medium, disabled));
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.Off, disabled));   // turning it off is always allowed
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.High, new CacheLimit(100L << 30, false)));
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.Low, new CacheLimit(null, false)));   // unlimited
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.Low, null));   // unreadable: let the driver decide
    }

    [Fact]
    public void Nvidia_auto_shader_task_counts_only_when_registered_and_enabled()
    {
        const string head = """<?xml version="1.0" encoding="UTF-16"?><Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""";
        Assert.False(NvidiaBackend.TaskEnabled(null));   // schtasks: not registered
        Assert.True(NvidiaBackend.TaskEnabled(head + "<Settings><Priority>4</Priority></Settings></Task>"));   // Enabled absent = true
        Assert.True(NvidiaBackend.TaskEnabled(head + "<Settings><Enabled>true</Enabled></Settings></Task>"));
        Assert.False(NvidiaBackend.TaskEnabled(head + "<Settings><Enabled>false</Enabled></Settings></Task>"));   // the NVIDIA App's "off"
    }

    [Fact]
    public void Epic_keeps_a_base_game_whose_manifest_names_itself_as_the_main_game()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-epic-test-").FullName;
        try
        {
            var install = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
            File.WriteAllBytes(Path.Combine(install, "Game.exe"), new byte[100]);
            var manifests = Directory.CreateDirectory(Path.Combine(root, "Manifests")).FullName;
            void Item(string file, object m) => File.WriteAllText(Path.Combine(manifests, file), System.Text.Json.JsonSerializer.Serialize(m));
            Item("a.item", new { AppName = "Game", MainGameAppName = "Game", InstallLocation = install });   // Legendary's export of a base game
            Item("b.item", new { AppName = "Addon", MainGameAppName = "Game", InstallLocation = install });  // an add-on in the game's install
            Item("c.item", new { AppName = "Other", MainGameAppName = "", InstallLocation = install });      // the launcher's own base game
            Assert.Equal(["epic:Game", "epic:Other"], new EpicSource(manifests).Discover().Select(g => g.Id).Order());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FindExe_judges_helper_names_inside_the_install_only_and_skips_Source_2s_console()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-exe-test-").FullName;
        try
        {
            foreach (var parent in new[] { "Setup", "Crash Tests", "DirectX Games", "Redist" })
            {
                var install = Directory.CreateDirectory(Path.Combine(root, parent, "Demo")).FullName;
                File.WriteAllBytes(Path.Combine(install, "Game.exe"), new byte[100]);
                Directory.CreateDirectory(Path.Combine(install, "_CommonRedist"));
                File.WriteAllBytes(Path.Combine(install, "_CommonRedist", "vc_redist.x64.exe"), new byte[500]);
                Assert.Equal(Path.Combine(install, "Game.exe"), GameFiles.FindExe(install));
            }
            var cs2 = Directory.CreateDirectory(Path.Combine(root, "Counter-Strike Global Offensive", "game", "bin", "win64")).FullName;
            File.WriteAllBytes(Path.Combine(cs2, "cs2.exe"), new byte[300]);
            File.WriteAllBytes(Path.Combine(cs2, "vconsole2.exe"), new byte[500]);
            Assert.Equal(Path.Combine(cs2, "cs2.exe"), GameFiles.FindExe(Path.Combine(root, "Counter-Strike Global Offensive")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>appinfo.vdf v28 with one app whose KeyValues nest far deeper than any real entry: that entry is left out,
    /// discovery isn't taken down by a stack overflow.</summary>
    [Fact]
    public void Steam_app_types_survive_absurdly_nested_key_values()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("scskiller-appinfo-test-").FullName, "appinfo.vdf");
        try
        {
            var kv = new MemoryStream();
            for (var i = 0; i < 200_000; i++) kv.Write([0, 0]);   // type 0 (an object), empty key
            var entry = new byte[60 + kv.Length];
            kv.ToArray().CopyTo(entry, 60);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(0x07564428u); w.Write(1u);
                w.Write(7u); w.Write((uint)entry.Length); w.Write(entry);
                w.Write(0u);
            }
            Assert.Empty(SteamSource.AppTypes(path, new HashSet<uint> { 7 })!);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    /// <summary>All or nothing: a read-only cache file is deleted with the others, not left after them.</summary>
    [Fact]
    public void Deleting_cache_files_takes_read_only_ones_too()
    {
        var dir = Directory.CreateTempSubdirectory("scskiller-delete-test-").FullName;
        try
        {
            var files = new[] { "a.parc", "b.parc" }.Select(n => Path.Combine(dir, n)).ToList();
            foreach (var f in files) File.WriteAllBytes(f, new byte[10]);
            File.SetAttributes(files[1], FileAttributes.ReadOnly);
            Assert.Equal(2, AppCacheFiles.DeleteAll(files.Select(f => new FileInfo(f)).ToList()));
            Assert.All(files, f => Assert.False(File.Exists(f)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
