using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Planning;

/// <summary>Middleware packs: detection next to the exe, promotion from a recording, seeding another install's plan with
/// the same DLL version and materializing from that install's copy (fake DLLs with embedded fake containers).</summary>
public class MiddlewarePackTests(ITestOutputHelper output)
{
    internal static readonly EngineInfo Engine = new("Fake", "1", null, "D3D12", false, null);
    static readonly VendorCaps Caps = new("nvidia-1", true, true, true);

    /// <summary>A valid DXBC container with one chunk (fourcc + data); the seed makes each one's bytes (and hash) unique.</summary>
    internal static byte[] Container(string fourcc, string seed)
    {
        // an RTS0 part is an empty 1.0 root signature (no parameters, no samplers), then the seed
        byte[] data = [.. fourcc == "RTS0" ? new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 24, 0, 0, 0, 0, 0, 0, 0, 24, 0, 0, 0, 0, 0, 0, 0 } : [], .. Encoding.ASCII.GetBytes(seed.PadRight(16, '.'))];
        var size = 32 + 4 + 8 + data.Length;
        var b = new byte[size];
        "DXBC"u8.CopyTo(b);
        SHA1.HashData(data).AsSpan(0, 16).CopyTo(b.AsSpan(4)); // "checksum"
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), 36);
        Encoding.ASCII.GetBytes(fourcc).CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)data.Length);
        data.CopyTo(b, 44);
        return b;
    }

    internal static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));

    /// <summary>A minimal PE32+ whose export directory names it <paramref name="exportName"/>, followed by
    /// <paramref name="payload"/> (embedded containers, as a DLL's .rdata holds them).</summary>
    internal static byte[] Pe(string? exportName, params byte[][] payload)
    {
        var head = new byte[0x400];
        head[0] = (byte)'M'; head[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(0x3C), 0x40);
        "PE\0\0"u8.CopyTo(head.AsSpan(0x40));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x44), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x46), 1);        // one section
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x54), 240);      // optional header size
        var opt = 0x58;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(opt), 0x20B);
        if (exportName != null) BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(opt + 112), 0x1000); // export directory RVA
        var sec = opt + 240;
        ".rdata"u8.CopyTo(head.AsSpan(sec));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(sec + 8), 0x200);   // virtual size
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(sec + 12), 0x1000); // VA
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(sec + 16), 0x200);  // raw size
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(sec + 20), 0x200);  // raw pointer
        if (exportName != null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(0x200 + 12), 0x1000 + 40); // Name RVA
            Encoding.ASCII.GetBytes(exportName).CopyTo(head, 0x200 + 40);
        }
        return [.. head, .. payload.SelectMany(p => p.Concat(new byte[13]))]; // unaligned, padded like real data
    }

    internal static Game GameIn(string dir, string id) => new(id, id, Store.Other, dir, Path.Combine(dir, "game.exe"));

    internal static ShaderIndex Index(params string[] shas) =>
        new("content-1", ["PCD3D_SM6"], shas.ToDictionary(h => h, h => new ShaderInfo(h, Stage.Compute, "cs_6_0", 0, new(0, 0, 0, 0), [], [], [])),
            [new ShaderMap("map", "Global", "PCD3D_SM6", shas)]);

    sealed class NoShaders : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Engine;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => MiddlewarePackTests.Index();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    sealed class Lines : IProgress<string>
    {
        public readonly List<string> All = [];
        public void Report(string value) { lock (All) All.Add(value); }
    }

    [Fact]
    public void DetectsKnownMiddlewareByFileOrExportName()
    {
        var dir = Ff7.TempDir("mw-detect");
        void Put(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(dir, name), bytes);
        Put("game.exe", Pe(null));
        Put("amdxcffx64.dll", Pe("amdxcffx64.dll"));
        Put("amd_fidelityfx_upscaler_dx12.dll", Pe("amd_fidelityfx_upscaler_dx12.dll"));
        Put("amd_fidelityfx_vk.dll", Pe("amd_fidelityfx_vk.dll"));          // Vulkan: not ours
        Put("ffx_fsr2_api_dx12_x64.dll", Pe("ffx_fsr2_api_dx12_x64.dll"));
        Put("libxess.dll", Pe("libxess.dll"));
        Put("nvngx_dlss.dll", Pe("nvngx_dlss.dll"));                        // flagged, not packable
        Put("sl.common.dll", Pe("sl.common.dll"));                          // Streamline plugins embed their shaders
        Put("sl.interposer.dll", Pe("sl.interposer.dll"));                  // Streamline's device wrapper: none
        Put("dxgi.dll", Pe("OptiScaler.dll"));                              // OptiScaler under a proxy name: by export
        Put("winmm.dll", Pe("winmm.dll"));                                  // a real proxy name, something else
        Put("renamed.dll", Pe("OptiScaler.dll"));                           // not a proxy name: the export isn't read
        Put("engine.dll", Pe("engine.dll"));
        Put("d3d12.dll", [1, 2, 3]);                                        // not a PE (e.g. truncated): ignored

        var found = Middleware.Detect(GameIn(dir, "test:detect"));
        output.WriteLine(string.Join("\n", found));
        Assert.Equal(
            [("optiscaler", "OptiScaler.dll", true), ("amd", "amd_fidelityfx_upscaler_dx12.dll", true), ("amd", "amdxcffx64.dll", true),
             ("amd", "ffx_fsr2_api_dx12_x64.dll", true), ("intel", "libxess.dll", true), ("nvidia", "nvngx_dlss.dll", false), ("nvidia", "sl.common.dll", true)],
            found.Select(d => (d.Vendor, d.Name, d.Packable)).OrderBy(d => d.Name, StringComparer.Ordinal).ToList());
        Assert.Equal("dxgi.dll", Path.GetFileName(found.Single(d => d.Vendor == "optiscaler").Path));

        // OptiScaler.dll next to its dxgi.dll copy: one entry, the real file
        Put("OptiScaler.dll", Pe("OptiScaler.dll"));
        Assert.Equal("OptiScaler.dll", Path.GetFileName(Middleware.Detect(dir).Single(d => d.Vendor == "optiscaler").Path));

        // anti-cheat games: nothing is opened
        Directory.CreateDirectory(Path.Combine(dir, "EasyAntiCheat"));
        Assert.Empty(Middleware.Detect(GameIn(dir, "test:detect")));
    }

    [Fact]
    public void ScanFindsEmbeddedContainersByTheProxyHash()
    {
        var (a, b) = (Container("DXIL", "shader a"), Container("DXIL", "shader b"));
        var dir = Ff7.TempDir("mw-scan");
        var dll = Path.Combine(dir, "amdxcffx64.dll");
        File.WriteAllBytes(dll, Pe("amdxcffx64.dll", a, "DXBC junk that isn't a container"u8.ToArray(), b));
        var image = Middleware.Scan(dll);
        Assert.Equal(new[] { Sha(a), Sha(b) }.Order(), image.Containers.Keys.Order());
        Assert.Equal(a, image.Read(Sha(a)));
        Assert.Equal(Sha(File.ReadAllBytes(dll)), image.ContentHash);
        Assert.True(SCSKiller.Core.Carved.Dxbc.IsRootSignatureOnly(Container("RTS0", "rs")));
        Assert.False(SCSKiller.Core.Carved.Dxbc.IsRootSignatureOnly(a));
    }

    /// <summary>The pieces of one scenario: a DLL with shaders A, B (compute) and VS/PS (graphics), a recording of a game
    /// next to it with PSOs of them and of the game's own shader.</summary>
    sealed record Fixture(byte[] Dll, byte[] A, byte[] B, byte[] Vs, byte[] Ps, byte[] Rs, byte[] GameCs, string RecordingDb, string PacksDir, string Root);

    static Fixture Make(string name)
    {
        var root = Ff7.TempDir(name);
        var (a, b, vs, ps) = (Container("DXIL", "fsr a"), Container("DXIL", "fsr b"), Container("DXIL", "fsr vs"), Container("DXIL", "fsr ps"));
        var rs = Container("RTS0", "runtime-built root signature");
        var shaderRs = Container("DXIL", "a 'root signature' that carries code");
        var noBlobRs = Container("RTS0", "root signature the recording lacks");
        var gameCs = Container("DXIL", "the game's own compute shader");
        var dll = Pe("amdxcffx64.dll", a, b, vs, ps);

        var db = Path.Combine(root, "recording.db");
        using (var f = File.Create(db))
        {
            foreach (var blob in new[] { rs, shaderRs, a, b, vs, ps, gameCs }) PsoDb.WriteBlob(f, Sha(blob), blob);
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(rs), Sha(a)));             // promoted
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(rs), Sha(b)));             // promoted
            PsoDb.Write(f, 'S', PsoDb.Stream(Sha(rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha(vs), [(int)Stage.Pixel] = Sha(ps) },
                [new("POSITION", 0, 6, 0)], 3, [PsoDb.R16G16B16A16Float], 0)); // promoted: graphics, whole payload
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(noBlobRs), Sha(gameCs)));  // the game's index shader: not middleware
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(shaderRs), Sha(a)));       // its root signature blob isn't RTS0-only: left out
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(noBlobRs), Sha(b)));       // root signature not recorded: left out
        }
        return new Fixture(dll, a, b, vs, ps, rs, gameCs, db, Path.Combine(root, "packs"), root);
    }

    static string Install(Fixture x, string name, byte[] dll)
    {
        var dir = Path.Combine(x.Root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), Pe(null));
        File.WriteAllBytes(Path.Combine(dir, "amdxcffx64.dll"), dll);
        return dir;
    }

    [Fact]
    public void PromotesOnlyRecordsMadeOfOneDllsShadersWithAnRts0RootSignature()
    {
        var x = Make("mw-promote");
        var first = GameIn(Install(x, "first", x.Dll), "test:first");
        var log = new Lines();
        var planner = new Planner(x.PacksDir);
        planner.Build(first, Engine, Index(Sha(x.GameCs)), new Recording(x.RecordingDb), Caps, Path.Combine(x.Root, "plan-first"), log, default);
        output.WriteLine(string.Join("\n", log.All));

        var packPath = Path.Combine(x.PacksDir, "amd", MiddlewarePack.FileName("amdxcffx64.dll", Sha(x.Dll)));
        var pack = MiddlewarePack.Read(packPath);
        Assert.Equal(("amd", "amdxcffx64.dll", Sha(x.Dll)), (pack.Header.Vendor, pack.Header.Dll, pack.Header.ContentHash));
        Assert.Equal(["test:first"], pack.Header.Sources);
        Assert.Equal(3, pack.Entries.Count);
        Assert.Equal("CCS", string.Concat(pack.Entries.Select(e => e.Tag)));
        Assert.Equal([Sha(x.Rs)], pack.RootSignatures.Keys);
        // hash-only: no shader container of the DLL (or the game) is in the pack
        var bytes = File.ReadAllBytes(packPath);
        foreach (var c in new[] { x.A, x.B, x.Vs, x.Ps, x.GameCs }) Assert.True(bytes.AsSpan().IndexOf(c) < 0);

        // promoting the same recording again adds nothing; the plan of that same game counts them as recorded
        var again = new Planner(x.PacksDir).Packs!.Promote(PsoDb.Read(x.RecordingDb).Where(r => r.Tag != 'B'),
            PsoDb.Read(x.RecordingDb).Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]),
            Index(Sha(x.GameCs)).Shaders, Middleware.Detect(first), "test:first");
        Assert.Equal((5, 0), (again.Single().Records, again.Single().New)); // 5 made of DLL shaders, 3 kept, none new
        Assert.Contains(log.All, l => l.Contains("3 pack PSOs (3 already in the recording, 0 new"));
    }

    [Fact]
    public void AGameTheUserAddedNeverFillsAPackButIsSeededFromOne()
    {
        var x = Make("mw-manual");
        var dir = Install(x, "added", x.Dll);
        var added = new Game("manual:0123456789abcdef", "added", Store.Manual, dir, Path.Combine(dir, "game.exe"));
        var planner = new Planner(x.PacksDir);
        planner.Build(added, Engine, Index(Sha(x.GameCs)), new Recording(x.RecordingDb), Caps, Path.Combine(x.Root, "plan-added"), null, default);
        Assert.False(Directory.Exists(Path.Combine(x.PacksDir, "amd")));   // its recording's PSOs stay its own

        planner.Build(GameIn(Install(x, "store", x.Dll), "test:store"), Engine, Index(Sha(x.GameCs)), new Recording(x.RecordingDb), Caps, Path.Combine(x.Root, "plan-store"), null, default);
        Assert.Equal(3, planner.Build(added, Engine, Index(), null, Caps, Path.Combine(x.Root, "plan-added-2"), null, default).Stats.MiddlewareItems);
    }

    [Fact]
    public void SeedsAnotherInstallWithTheSameDllAndMaterializesFromItsCopy()
    {
        var x = Make("mw-seed");
        var first = GameIn(Install(x, "first", x.Dll), "test:first");
        var planner = new Planner(x.PacksDir);
        var second = GameIn(Install(x, "second", x.Dll), "test:second");
        Assert.Equal("", planner.Packs!.Fingerprint(second)); // no pack yet: nothing to re-plan for
        planner.Build(first, Engine, Index(Sha(x.GameCs)), new Recording(x.RecordingDb), Caps, Path.Combine(x.Root, "plan-first"), null, default);
        Assert.StartsWith($"amdxcffx64.dll:{Sha(x.Dll)}:", planner.Packs.Fingerprint(second)); // another game's recording filled one: re-plan

        // another game, no recording, the same DLL version next to its exe
        var log = new Lines();
        var plan = planner.Build(second, Engine, Index(), null, Caps, Path.Combine(x.Root, "plan-second"), log, default);
        output.WriteLine(string.Join("\n", log.All));
        Assert.Equal(3, plan.Stats.MiddlewareItems);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.Equal(3, body.Count(r => r.Tag == 'M'));
        Assert.Contains(body, r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) == Sha(x.Rs));
        Assert.DoesNotContain(body, r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) != Sha(x.Rs)); // hash-only

        // a third game with another DLL version gets nothing
        var other = GameIn(Install(x, "other", Pe("amdxcffx64.dll", x.A)), "test:other");
        Assert.Equal(0, planner.Build(other, Engine, Index(), null, Caps, Path.Combine(x.Root, "plan-other"), null, default).Stats.MiddlewareItems);

        var work = Path.Combine(x.Root, "work-second");
        planner.Log = log;
        planner.Materialize(plan, second, Engine, new NoShaders(), null, work, default);
        var gen = PsoDb.Read(Path.Combine(work, "scskiller_gen.db")).ToList();
        var blobs = gen.Where(r => r.Tag == 'B').ToDictionary(r => PsoDb.Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        foreach (var c in new[] { x.A, x.B, x.Vs, x.Ps, x.Rs }) Assert.Equal(c, blobs[Sha(c)]);
        Assert.Equal(new[] { 'C', 'C', 'S' }, gen.Where(r => r.Tag != 'B').Select(r => r.Tag));
        // every PSO after the blobs it uses (the proxy resolves them as it loads)
        var at = gen.Select((r, i) => (r, i)).Where(p => p.r.Tag == 'B').ToDictionary(p => PsoDb.Hex(p.r.Payload.AsSpan(0, 20)), p => p.i);
        foreach (var (r, i) in gen.Select((r, i) => (r, i)).Where(p => p.r.Tag != 'B'))
        {
            var p = PsoDb.Parse(r);
            Assert.All(p.Stages.Values.Append(p.Rs), h => Assert.True(at[h] < i));
        }
        Assert.Contains(log.All, l => l.Contains("3 of 3 entries written"));
    }

    /// <summary>Serves fixed bytes by SHA-1, like an engine reader over the game's own index.</summary>
    sealed class Serves(params byte[][] install) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => Engine;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => MiddlewarePackTests.Index();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var b in install) if (sha1s.Contains(Sha(b))) sink(Sha(b), b);
        }
    }

    /// <summary>A downloaded (hash-only) recording: only its RTS0 root signature as a 'B' record; three PSOs of the DLL's
    /// shaders (FSR4-like) and one of the game's own.</summary>
    static string HashOnly(Fixture x)
    {
        var db = Path.Combine(x.Root, "hashonly.db");
        using var f = File.Create(db);
        PsoDb.WriteBlob(f, Sha(x.Rs), x.Rs);
        PsoDb.Write(f, 'C', PsoDb.Compute(Sha(x.Rs), Sha(x.A)));
        PsoDb.Write(f, 'C', PsoDb.Compute(Sha(x.Rs), Sha(x.B)));
        PsoDb.Write(f, 'S', PsoDb.Stream(Sha(x.Rs), new Dictionary<int, string> { [(int)Stage.Vertex] = Sha(x.Vs), [(int)Stage.Pixel] = Sha(x.Ps) },
            [new("POSITION", 0, 6, 0)], 3, [PsoDb.R16G16B16A16Float], 0));
        PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, Sha(x.GameCs))); // no root signature: the fake RTS0 isn't one the planner can parse
        return db;
    }

    /// <summary>Blobs the PSOs of a work dir (scskiller.db + scskiller_gen.db) use that neither file carries: what fails at warm.</summary>
    static List<string> Unresolved(string work) => RehydrateTests.Unresolved(Path.Combine(work, "scskiller.db"), Path.Combine(work, "scskiller_gen.db"));

    [Fact]
    public void AHashOnlyRecordingsMiddlewarePsosRehydrateFromTheDllAndMaterializeComplete()
    {
        var x = Make("mw-rehydrate");
        var game = GameIn(Install(x, "game", x.Dll), "test:game");
        var hashOnly = HashOnly(x);
        var reader = new Serves(x.GameCs); // the game's index knows only its own shader

        // without the DLLs: the middleware shaders stay missing
        var outDb = Path.Combine(x.Root, "rehydrated", "recording.db");
        Assert.Equal(new[] { x.A, x.B, x.Vs, x.Ps }.Select(Sha).Order(StringComparer.Ordinal), Rehydrate.Run(hashOnly, outDb, game, Engine, reader).Missing);

        var r = Rehydrate.Run(hashOnly, outDb, game, Engine, reader, moreBlobs: h => Middleware.Blobs(game, h));
        output.WriteLine(r.ToString());
        Assert.Empty(r.Missing);
        Assert.Equal((6, 1, 5, 4), (r.Referenced, r.AlreadyPresent, r.Found, r.FoundInMiddleware));
        Assert.Empty(RehydrateTests.Unresolved(outDb));
        var blobs = PsoDb.Read(outDb).Where(b => b.Tag == 'B').ToDictionary(b => PsoDb.Hex(b.Payload.AsSpan(0, 20)), b => b.Payload[20..]);
        foreach (var c in new[] { x.A, x.B, x.Vs, x.Ps, x.GameCs, x.Rs }) Assert.Equal(c, blobs[Sha(c)]);

        // plan (promotes the 3 middleware PSOs from the hash-only recording) + materialize: every PSO has its blobs
        var planner = new Planner(x.PacksDir);
        var plan = planner.Build(game, Engine, Index(Sha(x.GameCs)), new Recording(hashOnly), Caps, Path.Combine(x.Root, "plan"), null, default);
        Assert.Equal(3, PlanFile.Read(plan.FilePath).Records.Count(m => m.Tag == 'M'));
        var log = new Lines();
        planner.Log = log;
        var work = Path.Combine(x.Root, "work");
        planner.Materialize(plan, game, Engine, reader, new Recording(outDb), work, default);
        output.WriteLine(string.Join("\n", log.All));
        Assert.Empty(Unresolved(work));
        Assert.Equal(4, PsoDb.Read(Path.Combine(work, "scskiller.db")).Count(p => p.Tag != 'B'));
        Assert.Contains(log.All, l => l.Contains("3 already in the recording") && !l.Contains("from the DLL") && !l.Contains("skipped"));

        // Materialize alone, given the hash-only recording: the pack entries it holds get their shaders from the DLL
        // (only the game's own PSO, whose shader rehydrating pulls, is skipped)
        log.All.Clear();
        planner.Materialize(plan, game, Engine, reader, new Recording(hashOnly), work, default);
        output.WriteLine(string.Join("\n", log.All));
        Assert.Empty(Unresolved(work));
        Assert.Equal(1, Planner.SkippedIn(work));
        Assert.Contains(log.All, l => l.Contains("3 already in the recording (3 with their shaders from the DLL)"));
    }

    [Fact]
    public void ARecordedMiddlewarePsoWhoseShaderTheDllLacksIsReportedMissingAndSkippedNotReplayed()
    {
        var x = Make("mw-rehydrate-lacks");
        var dir = Install(x, "game", x.Dll);
        var game = GameIn(dir, "test:game");
        var hashOnly = HashOnly(x);
        var reader = new Serves(x.GameCs);
        var planner = new Planner(x.PacksDir);
        var plan = planner.Build(game, Engine, Index(Sha(x.GameCs)), new Recording(hashOnly), Caps, Path.Combine(x.Root, "plan"), null, default);

        // the DLL was updated after planning: this copy only holds shader A
        File.WriteAllBytes(Path.Combine(dir, "amdxcffx64.dll"), Pe("amdxcffx64.dll", x.A));
        var outDb = Path.Combine(x.Root, "rehydrated", "recording.db");
        var r = Rehydrate.Run(hashOnly, outDb, game, Engine, reader, moreBlobs: h => Middleware.Blobs(game, h));
        Assert.Equal(new[] { x.B, x.Vs, x.Ps }.Select(Sha).Order(StringComparer.Ordinal), r.Missing);
        Assert.Equal((2, 1), (r.Found, r.FoundInMiddleware));

        var log = new Lines();
        planner.Log = log;
        var work = Path.Combine(x.Root, "work");
        planner.Materialize(plan, game, Engine, reader, new Recording(outDb), work, default);
        output.WriteLine(string.Join("\n", log.All));
        Assert.Contains(log.All, l => l.Contains("1 already in the recording") && l.Contains("2 skipped") && l.Contains("amdxcffx64.dll (2)"));
        Assert.Empty(Unresolved(work)); // the two it can't give are out of the work copy: nothing fails at warm
        var left = PsoDb.Read(Path.Combine(work, "scskiller.db")).Where(p => p.Tag != 'B').Select(PsoDb.Parse).ToList();
        Assert.Equal(new[] { Sha(x.A), Sha(x.GameCs) }.Order(), left.Select(p => p.Stages[(int)Stage.Compute]).Order());
        Assert.DoesNotContain(PsoDb.Read(Path.Combine(work, "scskiller_gen.db")), p => p.Tag != 'B');
    }

    [Fact]
    public void ARecordedPsoWhoseShaderIsNotInThisInstallIsSkippedAndCountedNotReplayed()
    {
        var x = Make("mw-skip-game");
        var game = GameIn(Install(x, "game", x.Dll), "test:game");
        var hashOnly = HashOnly(x);
        var reader = new Serves(); // this build no longer has the game's compute shader
        var outDb = Path.Combine(x.Root, "rehydrated", "recording.db");
        var r = Rehydrate.Run(hashOnly, outDb, game, Engine, reader, moreBlobs: h => Middleware.Blobs(game, h));
        Assert.Equal([Sha(x.GameCs)], r.Missing);

        var planner = new Planner(x.PacksDir);
        var plan = planner.Build(game, Engine, Index(Sha(x.GameCs)), new Recording(hashOnly), Caps, Path.Combine(x.Root, "plan"), null, default);
        var log = new Lines();
        planner.Log = log;
        var work = Path.Combine(x.Root, "work");
        planner.Materialize(plan, game, Engine, reader, new Recording(outDb), work, default);
        output.WriteLine(string.Join("\n", log.All));

        Assert.Empty(Unresolved(work)); // nothing the warm would count as failed for a missing shader
        var main = PsoDb.Read(Path.Combine(work, "scskiller.db")).Where(p => p.Tag != 'B').Select(PsoDb.Parse).ToList();
        Assert.Equal(3, main.Count); // the three middleware PSOs; the game's own is out of the work copy
        Assert.DoesNotContain(main, p => p.Stages.ContainsValue(Sha(x.GameCs)));
        var gen = PsoDb.Read(Path.Combine(work, "scskiller_gen.db")).Where(p => p.Tag != 'B').ToList();
        Assert.DoesNotContain(gen, p => Rehydrate.References([p]).Contains(Sha(x.GameCs)));
        var skipped = Planner.SkippedIn(work);
        Assert.Equal(1 + PlanFile.Read(plan.FilePath).Records.Count(p => p.Tag is 'P' or '1' && Rehydrate.References([p]).Contains(Sha(x.GameCs))), skipped);
        Assert.Contains(log.All, l => l.Contains("skipped: a shader not in this install") && l.StartsWith($"{skipped} PSO"));

        // everything there: nothing skipped, the file says 0
        Assert.Empty(Rehydrate.Run(hashOnly, outDb, game, Engine, new Serves(x.GameCs), moreBlobs: h => Middleware.Blobs(game, h)).Missing);
        planner.Materialize(plan, game, Engine, new Serves(x.GameCs), new Recording(outDb), work, default);
        Assert.Equal(0, Planner.SkippedIn(work));
        Assert.Equal(4, PsoDb.Read(Path.Combine(work, "scskiller.db")).Count(p => p.Tag != 'B'));
    }

    /// <summary>A shader the game (or a mod) compiled at run time is in no file of any install: this PC's recording is its
    /// only copy. It replays from there and the plan log says so; the shared hash-only form names the PSO without the shader,
    /// so another PC reports it missing (and skips it) instead of losing it silently.</summary>
    [Fact]
    public void AShaderBuiltAtRunTimeReplaysFromThisPcsRecordingAndIsReportedMissingElsewhere()
    {
        var x = Make("mw-runtime");
        var game = GameIn(Install(x, "game", x.Dll), "test:game");
        var runtime = Container("DXIL", "compiled by the game at run time");
        var db = Path.Combine(x.Root, "runtime.db");
        File.Copy(x.RecordingDb, db);
        using (var f = new FileStream(db, FileMode.Append))
        {
            PsoDb.WriteBlob(f, Sha(runtime), runtime);
            PsoDb.Write(f, 'C', PsoDb.Compute(Sha(x.Rs), Sha(runtime)));
        }
        var log = new Lines();
        var planner = new Planner(x.PacksDir);
        var plan = planner.Build(game, Engine, Index(Sha(x.GameCs)), new Recording(db), Caps, Path.Combine(x.Root, "plan"), log, default);
        output.WriteLine(string.Join("\n", log.All));
        Assert.Contains(log.All, l => l.Contains("1 PSOs use 1 shaders in no file of the install") && l.Contains("1 of those shaders are in this PC's recording"));

        var work = Path.Combine(x.Root, "work");
        planner.Materialize(plan, game, Engine, new Serves(x.GameCs), new Recording(x.RecordingDb), work, default);
        var fixtureSkips = Planner.SkippedIn(work);   // its PSOs whose root signature wasn't recorded
        planner.Materialize(plan, game, Engine, new Serves(x.GameCs), new Recording(db), work, default);
        Assert.Equal(fixtureSkips, Planner.SkippedIn(work));
        Assert.Empty(Unresolved(work));
        Assert.Contains(PsoDb.Read(Path.Combine(work, "scskiller.db")), r => r.Tag == 'C' && PsoDb.Parse(r).Stages.ContainsValue(Sha(runtime)));

        var shared = SCSKiller.Core.Planning.HashOnly.Canonical(PsoDb.Read(db), local: true, out _);
        Assert.Contains(shared, r => r.Tag == 'C' && PsoDb.Parse(r).Stages.ContainsValue(Sha(runtime)));
        Assert.DoesNotContain(shared, r => r.Tag == 'B' && PsoDb.Hex(r.Payload.AsSpan(0, 20)) == Sha(runtime));
        var hashOnly = Path.Combine(x.Root, "shared.db");
        using (var f = File.Create(hashOnly)) foreach (var r in shared) PsoDb.Write(f, r.Tag, r.Payload);
        var other = Rehydrate.Run(hashOnly, Path.Combine(x.Root, "other", "recording.db"), game, Engine, new Serves(x.GameCs), moreBlobs: h => Middleware.Blobs(game, h));
        Assert.Contains(Sha(runtime), other.Missing);

        // the upload flags it ('L': the index and the DLLs next to the exe don't ship it), and the other PC's warm counts it as
        // needing a recording there; the flag never reaches the proxy
        var shipped = Index(Sha(x.GameCs)).Shaders.Keys
            .Concat(Middleware.Detect(game).Where(d => d.Packable).SelectMany(d => Middleware.Scan(d.Path).Containers.Keys)).ToHashSet();
        var flag = Assert.Single(SCSKiller.Core.Planning.HashOnly.LocalOnly(shared, shipped.Contains));
        var runtimePso = shared.Single(r => r.Tag == 'C' && PsoDb.Parse(r).Stages.ContainsValue(Sha(runtime)));
        Assert.Equal(runtimePso.Key, SCSKiller.Core.Planning.HashOnly.Target(flag));
        using (var f = File.Create(hashOnly))
            foreach (var r in SCSKiller.Core.Planning.HashOnly.Canonical([.. shared, flag], local: false, out _)) PsoDb.Write(f, r.Tag, r.Payload);
        Rehydrate.Run(hashOnly, Path.Combine(x.Root, "other", "recording.db"), game, Engine, new Serves(x.GameCs), moreBlobs: h => Middleware.Blobs(game, h));
        var otherWork = Path.Combine(x.Root, "other", "work");
        planner.Materialize(plan, game, Engine, new Serves(x.GameCs), new Recording(Path.Combine(x.Root, "other", "recording.db")), otherWork, default);
        Assert.Equal(1, Planner.NeedsRecordingIn(otherWork));
        Assert.True(Planner.SkippedIn(otherWork) >= 1);
        Assert.DoesNotContain(PsoDb.Read(Path.Combine(otherWork, "scskiller.db")), r => r.Tag == 'L');
    }

    [Fact]
    public void APackEntryIsSkippedWhenTheInstallsDllCopyLacksItsShader()
    {
        var x = Make("mw-skip");
        var first = GameIn(Install(x, "first", x.Dll), "test:first");
        var planner = new Planner(x.PacksDir);
        planner.Build(first, Engine, Index(Sha(x.GameCs)), new Recording(x.RecordingDb), Caps, Path.Combine(x.Root, "plan-first"), null, default);
        var dir = Install(x, "second", x.Dll);
        var second = GameIn(dir, "test:second");
        var plan = planner.Build(second, Engine, Index(), null, Caps, Path.Combine(x.Root, "plan-second"), null, default);
        Assert.Equal(3, plan.Stats.MiddlewareItems);

        // the DLL was updated after planning: this copy no longer holds shader B (nor the graphics pair)
        File.WriteAllBytes(Path.Combine(dir, "amdxcffx64.dll"), Pe("amdxcffx64.dll", x.A));
        var log = new Lines();
        planner.Log = log;
        var work = Path.Combine(x.Root, "work-second");
        planner.Materialize(plan, second, Engine, new NoShaders(), null, work, default);
        output.WriteLine(string.Join("\n", log.All));

        var gen = PsoDb.Read(Path.Combine(work, "scskiller_gen.db")).ToList();
        var psos = gen.Where(r => r.Tag != 'B').Select(PsoDb.Parse).ToList();
        Assert.Equal([Sha(x.A)], psos.Select(p => p.Stages[(int)Stage.Compute]));
        var blobs = gen.Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
        Assert.Equal(new[] { Sha(x.A), Sha(x.Rs) }.Order(), blobs.Order());
        Assert.Contains(log.All, l => l.Contains("1 of 3 entries written") && l.Contains("2 skipped") && l.Contains("amdxcffx64.dll (2)"));

        // and with the DLL gone altogether, every entry is skipped
        File.Delete(Path.Combine(dir, "amdxcffx64.dll"));
        planner.Materialize(plan, second, Engine, new NoShaders(), null, work, default);
        Assert.DoesNotContain(PsoDb.Read(Path.Combine(work, "scskiller_gen.db")), r => r.Tag != 'B');
    }
}
