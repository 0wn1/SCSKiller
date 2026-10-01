using System.Diagnostics;
using System.Globalization;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>Ray tracing collections synthesized per DXIL library (<see cref="RtCollections"/>): only for a driver
/// that caches collections on their own (NVIDIA), with a rule learned from a recording's collections (checked byte for byte)
/// or, for Unreal 4.26/4.27 without a recording, UE 4.26's; a library whose resources the root signatures don't give is left
/// out. The Jedi: Survivor tests use the install and its recording on this machine (read-only) and return early without.</summary>
public class RtCollectionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly EngineInfo Ue427 = new("Unreal", "4.27", null, "D3D12", false, null);
    static readonly VendorCaps Nvidia = Ff7.Nvidia with { PerStageCache = true };
    static readonly ShaderInfo Vs = Shader("rt-vs", Stage.Vertex, [In("POSITION", 0, 0, 7)], [In("SV_Position", 0, 0, 0xF, 3, 1)]);
    static ShaderInfo Lib(string name, params Binding[] b) => new(Hash(name), Stage.Library, "lib_6_3", 100, new ResourceCounts(2, 3, 0, 1), b, [], []);
    static readonly ShaderInfo Chs = Lib("rt-chs", new Binding("srv", 0, 0, 3), new Binding("sampler", 0, 0, 1), new Binding("cbv", 0, 0, 1), new Binding("cbv", 0, 1, 1), new Binding("srv", 2, 0, 1), new Binding("cbv", 2, 0, 1));
    static readonly ShaderInfo Bindless = Lib("rt-bindless", new Binding("srv", 3, 0, -1)); // a bindless range no rule has (spaces 4-9 are Avalanche's fork)

    static ShaderIndex Index() => new("rt", ["PCD3D_SM5"], new[] { Vs, Chs, Bindless }.ToDictionary(s => s.Sha1),
        [new ShaderMap("m", "Game", "PCD3D_SM5", [Vs.Sha1, Chs.Sha1, Bindless.Sha1])]);

    static List<RtCollections.ItemFields> Items(Plan p) => PlanFile.Read(p.FilePath).Records.Where(r => r.Tag == 'Y').Select(r => RtCollections.ParseItem(r.Payload)).ToList();

    [Fact]
    public void CollectionsOnlyWhereTheDriverCachesThem()
    {
        var dir = Ff7.TempDir("rt-plan");
        var nv = new Planner().Build(Ff7.Game, Ue427, Index(), null, Nvidia, Path.Combine(dir, "nv"), new Log(output.WriteLine), CancellationToken.None);
        var y = Assert.Single(Items(nv)); // the bindless library is left out
        Assert.Equal(Chs.Sha1, y.Library);
        Assert.Equal((32u, 8u, 1u, 4u), (y.Payload, y.Attributes, y.Depth, y.Flags));
        var blobs = PlanFile.Read(nv.FilePath).Records.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        Assert.Equal(RtCollections.Serialize(RtCollections.Ue426Global, RootSig.Ue426Samplers).Hash, y.Global);
        var local = RootSig.Parse(blobs[y.LocalOther]);
        Assert.Equal(0x80u, local.Flags); // LOCAL_ROOT_SIGNATURE
        Assert.Null(RootSig.Uncovered(new RootSig.Ranges(0, [.. RootSig.Parse(blobs[y.Global]).Slots, .. local.Slots]), Stage.Library, Chs));
        Assert.Empty(RootSig.Parse(blobs[y.LocalRayGen]).Slots);
        Assert.Equal(nv.Stats.Generated, PlanFile.Read(nv.FilePath).Records.Count(r => r.Tag is 'S' or 'P' or 'Y'));

        Assert.Equal((2L, 1L), (nv.Stats.RtLibraries, nv.Stats.RtUncovered)); // the rt-bindless library is left to a recording

        // AMD caches whole objects only; UE 5 but 5.1 has no rule without a recording: every library is left to a recording
        var amd = new Planner().Build(Ff7.Game, Ue427, Index(), null, Ff7.Amd, Path.Combine(dir, "amd"), null, CancellationToken.None);
        var ue5 = new Planner().Build(Ff7.Game, Ue427 with { Version = "5.2" }, Index(), null, Nvidia, Path.Combine(dir, "ue5"), null, CancellationToken.None);
        Assert.Empty(Items(amd));
        Assert.Empty(Items(ue5));
        Assert.Equal((2L, 2L), (ue5.Stats.RtLibraries, ue5.Stats.RtUncovered));
        var db = Path.Combine(dir, "rec.db");
        using (var f = File.Create(db))
        {
            var rs = RootSig.Serialize(RootSig.Build(RootSig.Rule.Ue426, new Dictionary<Stage, ShaderInfo> { [Stage.Vertex] = Vs }, false), RootSig.Ue426Samplers);
            var h = Hex(System.Security.Cryptography.SHA1.HashData(rs));
            WriteBlob(f, h, rs);
            var s = Stream(h, new SortedDictionary<int, string> { [(int)Stage.Vertex] = Vs.Sha1 }, Planner.VsLayout(Vs), 3, [], D32Float);
            Write(f, 'S', s);
        }
        // a recording without state objects (ray tracing off as played): the no-recording rule, as without one
        Assert.Single(Items(new Planner().Build(Ff7.Game, Ue427, Index(), new Recording(db), Nvidia, Path.Combine(dir, "rec"), null, CancellationToken.None)));
    }

    /// <summary>A DXIL container with just an SFI0 part of these feature flags.</summary>
    internal static byte[] Sfi0(ulong flags)
    {
        byte[] part = [.. "SFI0"u8, .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(flags)];
        return [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36 + part.Length), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36), .. part];
    }

    /// <summary>A game whose ray tracing is inline (RayQuery in compute and pixel shaders) ships DXIL libraries it never builds a
    /// state object from: a recording that traces rays inline and has no state object leaves none to a recording. Without a
    /// recording, or with one without ray tracing (off as played), UE 5.6 (no collection rule) leaves them all.</summary>
    [Fact]
    public void InlineRayTracingLeavesNoLibraryToARecording()
    {
        var dir = Ff7.TempDir("rt-inline");
        var ue56 = Ue427 with { Version = "5.6" };
        (long, long) Rt(string name, ShaderIndex index, params byte[][] shaders)
        {
            Recording? rec = null;
            if (shaders.Length > 0)
            {
                var db = Path.Combine(dir, name + ".db");
                using (var f = File.Create(db))
                    foreach (var c in shaders)
                    {
                        var h = Hex(System.Security.Cryptography.SHA1.HashData(c));
                        WriteBlob(f, h, c);
                        Write(f, 'C', Compute(Hash("rs"), h));
                    }
                rec = new Recording(db);
            }
            var s = new Planner().Build(Ff7.Game, ue56, index, rec, Nvidia, Path.Combine(dir, name), new Log(output.WriteLine), CancellationToken.None).Stats;
            return (s.RtLibraries, s.RtUncovered);
        }
        const ulong RayQuery = 0x100000, WaveOps = 0x4000;
        Assert.True(Core.Carved.Dxbc.InlineRayTracing(Sfi0(RayQuery | WaveOps)));
        Assert.False(Core.Carved.Dxbc.InlineRayTracing(Sfi0(WaveOps)));
        Assert.Equal((2L, 2L), Rt("none", Index()));
        Assert.Equal((2L, 2L), Rt("off", Index(), Sfi0(WaveOps)));
        Assert.Equal((2L, 0L), Rt("inline", Index(), Sfi0(WaveOps), Sfi0(RayQuery)));
        var noLibraries = new ShaderIndex("rt", ["PCD3D_SM5"], new Dictionary<string, ShaderInfo> { [Vs.Sha1] = Vs }, [new ShaderMap("m", "Game", "PCD3D_SM5", [Vs.Sha1])]);
        Assert.Equal((0L, 0L), Rt("inline-only", noLibraries, Sfi0(RayQuery)));
    }

    /// <summary>UE 4.26's local root signature: none for a ray generation shader; the hit group system parameters, then the
    /// shader's tables and root CBVs.</summary>
    [Fact]
    public void LocalRootSignatures()
    {
        Assert.Empty(RtCollections.LocalRs(new(3, 5, 1, 2), rayGen: true).Rows);
        var rows = RtCollections.LocalRs(new(2, 5, 1, 3), rayGen: false).Rows;
        Assert.Equal(["3,0,0,2,0", "3,0,1,2,0", "1,0,0,2,4", "0,0,0,5,0,0,5", "0,0,3,3,0,0,1", "0,0,1,1,0,0,3", "2,0,0,0,8", "2,0,1,0,8"], rows.Select(r => string.Join(',', r)));
        Assert.Equal(3, RtCollections.LocalRs(new(0, 0, 0, 0), rayGen: false).Rows.Count); // a miss shader: the system parameters only
        Assert.Equal("1,0,0,2,6", string.Join(',', RtCollections.LocalRs(new(0, 0, 0, 0), rayGen: false, systemConstants: 6).Rows[2])); // UE 5.1
    }

    /// <summary>UE 5.1 without a recording: <see cref="RtCollections.Ue51Global"/>, 6 system constants, each library's own payload.</summary>
    [Fact]
    public void Ue51CollectionsWithoutARecording()
    {
        var dir = Ff7.TempDir("rt-ue51");
        var ue51 = Ue427 with { Version = "5.1" };
        var plan = new Planner().Build(Ff7.Game, ue51, Index(), null, Nvidia, dir, new Log(output.WriteLine), CancellationToken.None);
        var y = Assert.Single(Items(plan)); // the bindless library is left out
        Assert.Equal((0u, 8u, 1u, 4u), (y.Payload, y.Attributes, y.Depth, y.Flags));
        var (global, blob) = RtCollections.Serialize(RtCollections.Ue51Global, RootSig.Ue426Samplers);
        Assert.Equal(global, y.Global);
        Assert.Equal(["0,0,0,64,0,1,5", "0,0,3,16,0,1,1", "0,0,1,16,0,1,3", "2,0,0,1,8"], RtCollections.Ue51Global.Rows.Take(4).Select(r => string.Join(',', r)));
        Assert.Equal("4,0,0,999,2", string.Join(',', RtCollections.Ue51Global.Rows[^1]));
        if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(blob));
        Assert.Equal(RtCollections.Serialize(RtCollections.LocalRs(Chs.Counts, false, Chs.Bindings, 6), []).Hash, y.LocalOther);
        Assert.Equal((2L, 1L), (plan.Stats.RtLibraries, plan.Stats.RtUncovered));
    }

    /// <summary>A DXIL library container with just an RDAT part (a string buffer and a function table): what
    /// <see cref="ShaderContainer.Rdat"/> reads, enough for <see cref="RtCollections.Collection"/>.</summary>
    internal static byte[] Library(params (int Kind, string Name, int Payload)[] fns)
    {
        var strings = new List<byte>();
        uint Str(string s) { var at = (uint)strings.Count; strings.AddRange([.. System.Text.Encoding.ASCII.GetBytes(s), 0]); return at; }
        var table = new List<byte>(BitConverter.GetBytes(fns.Length)) { };
        table.AddRange(BitConverter.GetBytes(28));
        foreach (var f in fns)
        {
            var n = Str(f.Name);
            foreach (var v in new uint[] { n, n, 0, 0, (uint)f.Kind, (uint)f.Payload, f.Kind == 7 || f.Kind == 11 ? 0u : 8 }) table.AddRange(BitConverter.GetBytes(v));
        }
        byte[] Part(uint type, List<byte> data) => [.. BitConverter.GetBytes(type), .. BitConverter.GetBytes(data.Count), .. data];
        byte[] a = Part(1, strings), b = Part(4, table);
        byte[] rdat = [.. BitConverter.GetBytes(0x10), .. BitConverter.GetBytes(2), .. BitConverter.GetBytes(16), .. BitConverter.GetBytes(16 + a.Length), .. a, .. b];
        byte[] part = [.. "RDAT"u8, .. BitConverter.GetBytes(rdat.Length), .. rdat];
        return [.. "DXBC"u8, .. new byte[16], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36 + part.Length), .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(36), .. part];
    }

    static readonly byte[] HitLib = Library((10, "MaterialCHS", 64), (9, "MaterialAHS", 64)), ShadowLib = Library((10, "ShadowCHS", 12)), RayGenLib = Library((7, "RayGen", 0));

    /// <summary>A collection whose one library exports a closest hit AND an any hit shader (Hogwarts Legacy: 290 of its 981)
    /// reads back as UE-shaped with every export.</summary>
    [Fact]
    public void ReadTakesALibraryWithSeveralExports()
    {
        var sha = Hash("hit-lib");
        var c = RtCollections.Collection(HitLib, sha, Hash("g"), Hash("lr"), Hash("lo"), 64, 8, 1, 4, "e00cd9132f99c247")!;
        Assert.Equal(new RtCollections.Recorded(sha, Hash("g"), Hash("lo"), 64, 8, 1, 4, "e00cd9132f99c247"), RtCollections.Read(new Rec('R', c)));
        Assert.Equal([sha], ParseStateObject(new Rec('R', c)).Libraries);
    }

    /// <summary>Avalanche's 4.27 fork (Hogwarts Legacy; its recording: 981/981 collections rebuilt from its files): its libraries'
    /// bindless SRVs (spaces 4-9) select its global root signature (128 SRVs in space 1, a table per bindless space from 5 up,
    /// samplers, UAVs, b0-b15, u0 space 1001 at offset 0; no NVAPI slot), a local one with the space-4 table after the SRV
    /// table, and each library's own payload; a ray generation library is compiled once per payload the other libraries have.</summary>
    [Fact]
    public void AvalancheForkCollectionsWithoutARecording()
    {
        var hit = Lib("fork-hit", new Binding("srv", 0, 0, 3), new Binding("srv", 4, 0, -1), new Binding("srv", 1, 100, 1), new Binding("uav", 1001, 0, 1));
        var shadow = Lib("fork-shadow", new Binding("srv", 7, 0, -1));
        var rayGen = Lib("fork-raygen", new Binding("srv", 6, 0, -1));
        var index = new ShaderIndex("fork", ["PCD3D_SM5"], new[] { hit, shadow, rayGen }.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM5", [hit.Sha1, shadow.Sha1, rayGen.Sha1])]);
        var dir = Ff7.TempDir("rt-fork");
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue427, index, null, Nvidia, Path.Combine(dir, "plan"), new Log(log.Add), CancellationToken.None);
        var items = Items(plan);
        Assert.Equal(3, items.Count); // none uncovered
        Assert.All(items, y => Assert.Equal((0u, 8u, 1u, 4u), (y.Payload, y.Attributes, y.Depth, y.Flags)));
        var global = RtCollections.GlobalFor([hit, shadow, rayGen]);
        Assert.Equal(RtCollections.Serialize(global, RootSig.Ue426Samplers).Hash, items[0].Global);
        Assert.Equal(["0,0,0,128,0,1,5", "0,0,0,4294967295,0,5,5", "0,0,0,4294967295,0,6,5", "0,0,0,4294967295,0,7,5", "0,0,3,16,0,1,1", "0,0,1,16,0,1,3"],
            global.Rows.Take(6).Select(r => string.Join(',', r)));
        Assert.Equal("0,0,1,1,0,1001,0,0", string.Join(',', global.Rows[^1])); // explicit offset 0
        var blob = RtCollections.Serialize(global, RootSig.Ue426Samplers).Blob;
        if (D3D12Runtime.Available) Assert.Equal(0, D3D12Runtime.CreateRootSignature(blob));
        var local = RtCollections.LocalRs(hit.Counts, false, hit.Bindings).Rows.Select(r => string.Join(',', r)).ToList();
        Assert.Equal(["3,0,0,2,0", "3,0,1,2,0", "1,0,0,2,4", "0,0,0,3,0,0,5", "0,0,0,4294967295,0,4,5", "0,0,3,1,0,0,1"], local.Take(6));
        Assert.Contains(log, l => l.Contains("Avalanche's UE 4.27 fork") && l.Contains("payload each library's own") && l.Contains("NVAPI state unknown without a recording"));
        Assert.All(items, y => Assert.Null(y.Nv));

        // materialized: each library's own payload; the ray generation one once per other payload (12 and 64)
        var libs = new Dictionary<string, byte[]> { [hit.Sha1] = HitLib, [shadow.Sha1] = ShadowLib, [rayGen.Sha1] = RayGenLib };
        new Planner().Materialize(plan, Ff7.Game, Ue427, new Shaders(libs), null, Path.Combine(dir, "work"), CancellationToken.None);
        var made = Read(Path.Combine(dir, "work", "scskiller_gen.db")).Where(r => r.Tag == 'R').Select(RtCollections.Read).ToList();
        Assert.Equal([(hit.Sha1, 64u), (rayGen.Sha1, 12u), (rayGen.Sha1, 64u), (shadow.Sha1, 12u)], made.Select(c => (c!.Library, c.Payload)).Order());
        Assert.Equal(0, Planner.SkippedIn(Path.Combine(dir, "work")));
    }

    /// <summary>The NVAPI state a recording's collections were created with ('N') is learned when (nearly) all carry one, goes
    /// into each 'Y', and Materialize writes an 'N' (thread scope) for every collection it makes from that 'Y'.</summary>
    [Fact]
    public void RecordedNvapiStateGoesToSynthesizedCollections()
    {
        var cols = Enumerable.Range(0, 200).Select(i => new Rec('R', [(byte)i, 1, 2, 3])).ToList();
        var nv = cols.Select(c => new NvState(c.Key, 0, 1001, 2, 0).ToRec()).ToList();
        Assert.Equal(new RtCollections.Nv(0, 1001, 0), RtCollections.LearnedNv(cols, nv));
        Assert.Null(RtCollections.LearnedNv(cols, nv.Skip(10)));  // 95%: not the game's rule
        Assert.Null(RtCollections.LearnedNv(cols, []));
        Assert.Equal(NvState.Parse(nv[0]), NvState.Parse(NvState.Parse(nv[0]).ToRec()));

        var hit = Lib("nv-hit", new Binding("srv", 4, 0, -1));
        var rule = new RtCollections.Rule(Zero.Replace('0', '1'), 4, 1, 0, 8, true);
        var y = RtCollections.ParseItem(RtCollections.Item(hit.Sha1, rule.GlobalRs, Zero, Zero, rule, new(0, 1001, 0)));
        Assert.Equal((hit.Sha1, 4u, new RtCollections.Nv(0, 1001, 0)), (y.Library, y.Flags, y.Nv!.Value));
        Assert.Null(RtCollections.ParseItem(RtCollections.Item(hit.Sha1, rule.GlobalRs, Zero, Zero, rule)).Nv);

        var dir = Ff7.TempDir("rt-nvapi");
        var plan = new Plan("t", "t", "PCD3D_SM5", "nvidia-1", new PlanStats(0, 1, 0, 0, true), Path.Combine(dir, "plan.bin"));
        PlanFile.Write(plan, [new('Y', RtCollections.Item(hit.Sha1, rule.GlobalRs, Zero, Zero, rule, new(0, 1001, 0)))]);
        new Planner().Materialize(plan, Ff7.Game, Ue427, new Shaders(new() { [hit.Sha1] = HitLib }), null, Path.Combine(dir, "work"), CancellationToken.None);
        var gen = Read(Path.Combine(dir, "work", "scskiller_gen.db")).ToList();
        var r = Assert.Single(gen, r => r.Tag == 'R');
        Assert.Equal(new NvState(r.Key, 0, 1001, 2, 0), NvState.Parse(Assert.Single(gen, r => r.Tag == 'N')));
    }

    /// <summary>Hash-only recordings keep an 'N' only with the record it applies to.</summary>
    [Fact]
    public void HashOnlyKeepsNvapiStateWithItsRecord()
    {
        var rs = RootSig.Serialize(RtCollections.EmptyLocal, []);
        var rsSha = Hex(System.Security.Cryptography.SHA1.HashData(rs));
        var cs = new Rec('C', Compute(rsSha, Vs.Sha1));
        var kept = new NvState(cs.Key, 0, 1001, 3, 0).ToRec();
        var orphan = new NvState(Zero.Replace('0', '2'), 0, 1001, 2, 0).ToRec();
        var canonical = HashOnly.Canonical([new('B', [.. Convert.FromHexString(rsSha), .. rs]), orphan, kept, cs], local: false, out _);
        Assert.Equal(['B', 'C', 'N'], canonical.Select(r => r.Tag));
        Assert.Equal(kept.Key, canonical[^1].Key);
        Assert.Throws<InvalidDataException>(() => HashOnly.Canonical([cs, new Rec('N', new byte[35])], local: true, out _));
    }

    sealed class Shaders(Dictionary<string, byte[]> bytes) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
        {
            foreach (var (h, b) in bytes) if (sha1s.Contains(h)) sink(h, b);
        }
    }

    /// <summary>Hogwarts Legacy (Avalanche's 4.27 fork) against SCSKiller's recording of it (54,680 PSOs, 981 collections,
    /// 8 pipelines, 154 additions; read, never written): every recorded root signature
    /// is rebuilt byte for byte by the stock 4.26 rule + the fork's bindless tables and 128-SRV tables, and the plan made
    /// WITHOUT the recording reproduces every recorded collection byte for byte (given UE's export names; ours differ, and
    /// NVIDIA doesn't key on them). Install read only; returns early without the game or recording.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void HogwartsRecordingConfirmsTheForkRules()
    {
        var game = new SCSKiller.Core.Games.SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:990080");
        if (game == null) return;
        Ff7.Codecs();
        var reader = new UnrealReader(Ff7.TempDir("rt-hogwarts-data"));
        var engine = reader.Detect(game)!;
        if (Ff7.Recording(game, engine, reader, "rt-hogwarts-rec") is not { } db) return;
        var recs = Read(db).ToList();
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var bc = index.Shaders;
        var blobs = recs.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        var maxSrvs = RootSig.MaxSrvsFor(RootSig.Rule.Ue426, bc.Values);
        int n = 0, ok = 0;
        foreach (var pso in recs.Where(r => r.Tag is 'G' or 'C' or 'S').Select(Parse))
        {
            if (!pso.Stages.Values.All(bc.ContainsKey) || !blobs.TryGetValue(pso.Rs, out var blob)) continue;
            n++;
            if (RootSig.Serialize(RootSig.Build(RootSig.Rule.Ue426, pso.Stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]), false, maxSrvs), RootSig.Ue426Samplers).AsSpan().SequenceEqual(blob)) ok++;
        }
        output.WriteLine($"root signatures rebuilt byte-exact: {ok}/{n} recorded PSOs (SRV tables of {maxSrvs})");
        Assert.True(n > 10_000, $"only {n} recorded PSOs of index shaders");
        Assert.Equal(n, ok);

        var plan = new Planner().Build(game, engine, index, null, Nvidia, Ff7.TempDir("rt-hogwarts-plan"), new Log(output.WriteLine), CancellationToken.None);
        Assert.Equal(0, plan.Stats.Uncovered);
        var ys = Items(plan).ToDictionary(y => y.Library);
        var keys = recs.Where(r => IsStateObject(r.Tag)).Select(r => r.Key).ToHashSet();
        var cols = recs.Where(r => IsStateObject(r.Tag)).Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        if (cols.Count == 0) { output.WriteLine("no ray tracing collections in the recording (ray tracing off)"); return; }
        var own = cols.Select(c => c.Library).Distinct().ToDictionary(h => h, h => RtCollections.OwnPayload(blobs[h]));
        var payloads = own.Values.Where(p => p > 0).Distinct().Order().ToList();
        var same = cols.Count(c => ys.TryGetValue(c.Library, out var y) && RtCollections.Payloads(y.Payload, own[c.Library], payloads).Any(p =>
            keys.Contains(new Rec('R', RtCollections.Collection(blobs[c.Library], c.Library, y.Global, y.LocalRayGen, y.LocalOther, p, y.Attributes, y.Depth, y.Flags, c.NameHash)!).Key)));
        output.WriteLine($"{ys.Count} collections planned without the recording; recorded collections rebuilt from them: {same}/{cols.Count}");
        Assert.True(cols.Count > 900, $"only {cols.Count} recorded collections");
        Assert.Equal(cols.Count, same);
    }

    /// <summary>Oblivion Remastered (stock UE 5.1) against SCSKiller's recording of it, read only: every root signature and
    /// collection rebuilt byte for byte, and the no-recording plan covers the units of most of its PSOs.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void OblivionRemasteredRecordingConfirmsTheUe51Rules()
    {
        var game = new SCSKiller.Core.Games.XboxSource().Discover().FirstOrDefault(g => g.Id.StartsWith("xbox:BethesdaSoftworks.ProjectAltar"));
        if (game == null) return;
        Ff7.Codecs();
        var reader = new UnrealReader(Ff7.TempDir("rt-oblivion-data"));
        var engine = reader.Detect(game)!;
        if (Ff7.Recording(game, engine, reader, "rt-oblivion-rec") is not { } db) return;
        var recs = Read(db).ToList();
        Assert.Equal((RootSig.Rule.Ue51, true), (RootSig.RuleFor(engine), RootSig.Verified(engine)));
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var bc = index.Shaders;
        var blobs = recs.Where(r => r.Tag == 'B').GroupBy(r => Hex(r.Payload.AsSpan(0, 20))).ToDictionary(g => g.Key, g => g.First().Payload[20..]);
        var psos = recs.Where(r => r.Tag is 'G' or 'C' or 'S').Select(Parse).Where(p => p.Stages.Values.All(bc.ContainsKey) && blobs.ContainsKey(p.Rs)).ToList();
        var ok = psos.Count(p => RootSig.Serialize(RootSig.Build(RootSig.Rule.Ue51, p.Stages.ToDictionary(s => (Stage)s.Key, s => bc[s.Value]), true), RootSig.Ue426Samplers).AsSpan().SequenceEqual(blobs[p.Rs]));
        output.WriteLine($"root signatures rebuilt byte-exact: {ok}/{psos.Count} recorded PSOs");
        Assert.True(psos.Count > 20_000, $"only {psos.Count} recorded PSOs of index shaders");
        Assert.Equal(psos.Count, ok);

        var plan = new Planner().Build(game, engine, index, null, Nvidia, Ff7.TempDir("rt-oblivion-plan"), new Log(output.WriteLine), CancellationToken.None);
        Assert.Equal((0, 0L), (plan.Stats.Uncovered, plan.Stats.RtUncovered));
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var units = body.Where(r => r.Tag == 'S').Select(Parse).SelectMany(p => p.Stages.Select(s => (s.Key, s.Value, p.Rs))).ToHashSet();
        var covered = psos.Count(p => p.Stages.All(s => units.Contains((s.Key, s.Value, p.Rs))));
        output.WriteLine($"recorded PSOs whose stages the no-recording plan compiles with their root signature: {covered}/{psos.Count}");
        Assert.True(covered >= 0.995 * psos.Count, $"{covered}/{psos.Count}"); // 99.96% measured; 95% without MS per-primitive outputs and default-material shaders across maps
        Assert.All(psos.Where(p => p.Stages.ContainsKey((int)Stage.Mesh)), p => Assert.True(p.Stages.All(s => units.Contains((s.Key, s.Value, p.Rs)))));

        var ys = body.Where(r => r.Tag == 'Y').Select(r => RtCollections.ParseItem(r.Payload)).ToDictionary(y => y.Library);
        var keys = recs.Where(r => IsStateObject(r.Tag)).Select(r => r.Key).ToHashSet();
        var cols = recs.Where(r => IsStateObject(r.Tag)).Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        var own = cols.Select(c => c.Library).Distinct().ToDictionary(h => h, h => RtCollections.OwnPayload(blobs[h]));
        var payloads = own.Values.Where(p => p > 0).Distinct().Order().ToList();
        var same = cols.Count(c => ys.TryGetValue(c.Library, out var y) && RtCollections.Payloads(y.Payload, own[c.Library], payloads).Any(p =>
            keys.Contains(new Rec('R', RtCollections.Collection(blobs[c.Library], c.Library, y.Global, y.LocalRayGen, y.LocalOther, p, y.Attributes, y.Depth, y.Flags, c.NameHash)!).Key)));
        output.WriteLine($"{ys.Count} collections planned without the recording; recorded collections rebuilt from them: {same}/{cols.Count}");
        Assert.True(cols.Count > 500, $"only {cols.Count} recorded collections");
        Assert.Equal(cols.Count, same);
    }

    const string JediInstall = @"D:\EA\Jedi Survivor";
    static readonly Game Jedi = new("ea:198300", "STAR WARS Jedi: Survivor", Store.EA, JediInstall, Path.Combine(JediInstall, @"SwGame\Binaries\Win64\JediSurvivor.exe"));
    static readonly Lazy<(UnrealReader Reader, EngineInfo Engine)> JediReader = new(() =>
    {
        Ff7.Codecs();
        var reader = new UnrealReader(Ff7.TempDir("rt-jedi-data"));
        return (reader, reader.Detect(Jedi)!);
    });
    static readonly Lazy<IReadOnlyDictionary<string, ShaderInfo>> JediShaders = new(() => JediReader.Value.Reader.Index(Jedi, JediReader.Value.Engine, null, CancellationToken.None).Shaders);
    static readonly Lazy<string?> JediDb = new(() => File.Exists(Jedi.ExePath) ? Ff7.Recording(Jedi, JediReader.Value.Engine, JediReader.Value.Reader, "rt-jedi-rec") : null);
    static bool HasJedi => JediDb.Value != null;

    sealed record JediData(List<Rec> StateObjects, Dictionary<string, byte[]> Blobs, List<RtCollections.Recorded> Collections, string Global);

    static JediData ReadJedi()
    {
        var recs = Read(JediDb.Value!).ToList();
        var blobs = recs.Where(r => r.Tag == 'B').ToDictionary(r => Hex(r.Payload.AsSpan(0, 20)), r => r.Payload[20..]);
        var so = recs.Where(r => IsStateObject(r.Tag)).ToList();
        var cols = so.Select(RtCollections.Read).OfType<RtCollections.Recorded>().ToList();
        return new(so, blobs, cols, cols.GroupBy(c => c.Global).MaxBy(g => g.Count())!.Key);
    }

    static (string Hash, byte[] Blob) Local(ShaderInfo lib, bool rayGen) => RtCollections.Serialize(RtCollections.LocalRs(lib.Counts, rayGen), []);

    /// <summary>Every UE-shaped collection of Jedi's recording (2,917 of its 2,987 state objects) is rebuilt byte for byte from
    /// its library (exports and kinds from RDAT), the library's resource counts (local root signature) and the recording's
    /// global root signature and configs, names included when given UE's shader hash; UE 4.26's global root signature is
    /// the recorded one; the index gives every library its RDAT resources, which the two root signatures cover.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void JediCollectionsRebuildByteForByte()
    {
        if (!HasJedi) return;
        var x = ReadJedi();
        var bc = JediShaders.Value;
        Assert.Equal(x.Global, RtCollections.Serialize(RtCollections.Ue426Global, RootSig.Ue426Samplers).Hash);
        var keys = x.StateObjects.Select(r => r.Key).ToHashSet();
        var global = RootSig.Parse(x.Blobs[x.Global]);
        int n = 0, same = 0, sameButNames = 0, covered = 0, withResources = 0;
        foreach (var c in x.Collections.Where(c => bc.ContainsKey(c.Library)))
        {
            n++;
            var (g, o) = (Local(bc[c.Library], true), Local(bc[c.Library], false));
            var lib = x.Blobs[c.Library];
            if (keys.Contains(new Rec('R', RtCollections.Collection(lib, c.Library, x.Global, g.Hash, o.Hash, c.Payload, c.Attributes, c.Depth, c.Flags, c.NameHash)!).Key)) same++;
            var ours = RtCollections.Read(new Rec('R', RtCollections.Collection(lib, c.Library, x.Global, g.Hash, o.Hash, c.Payload, c.Attributes, c.Depth, c.Flags)!))!;
            if (ours with { NameHash = c.NameHash } == c) sameButNames++;
            if (bc[c.Library].Bindings.Count > 0) withResources++;
            if (RootSig.Uncovered(new RootSig.Ranges(0, [.. global.Slots, .. RootSig.Parse(o.Blob).Slots]), Stage.Library, bc[c.Library]) == null) covered++;
        }
        output.WriteLine($"{x.Collections.Count} UE-shaped collections of {x.StateObjects.Count} state objects; {n} of index libraries: {same} rebuilt byte for byte, {sameButNames} with our names; {withResources} declare RDAT resources, {covered} all covered by the two root signatures");
        Assert.True(n > 2900 && withResources > 2800, $"{n} {withResources}");
        Assert.Equal((n, n, n), (same, sameButNames, covered));
    }

    /// <summary>Materialize turns a plan's 'Y' into the collection's 'R' record, its exports read from the library the reader
    /// serves; a library without a ray tracing export is skipped.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void MaterializeWritesTheCollection()
    {
        if (!HasJedi) return;
        var x = ReadJedi();
        var c = x.Collections.First(c => c.NameHash.Length == 16 && x.Blobs.ContainsKey(c.Library));
        var lib = x.Blobs[c.Library];
        var (g, o) = (RtCollections.Serialize(RtCollections.LocalRs(new(3, 5, 0, 2), true), []), RtCollections.Serialize(RtCollections.LocalRs(new(3, 5, 0, 2), false), []));
        var dir = Ff7.TempDir("rt-materialize");
        var rule = new RtCollections.Rule(x.Global, 4, 1, 32, 8, true);
        var plan = new Plan("t", "t", "PCD3D_SM5", "nvidia-1", new PlanStats(0, 1, 0, 3, true), Path.Combine(dir, "plan.bin"));
        PlanFile.Write(plan, [new('B', [.. Convert.FromHexString(x.Global), .. x.Blobs[x.Global]]), new('B', [.. Convert.FromHexString(g.Hash), .. g.Blob]),
            new('B', [.. Convert.FromHexString(o.Hash), .. o.Blob]), new('Y', RtCollections.Item(c.Library, x.Global, g.Hash, o.Hash, rule)),
            new('Y', RtCollections.Item(Vs.Sha1, x.Global, g.Hash, o.Hash, rule))]); // not a library: skipped
        new Planner().Materialize(plan, Ff7.Game, Ue427, new OneShader(c.Library, lib), null, Path.Combine(dir, "work"), CancellationToken.None);
        var gen = Read(Path.Combine(dir, "work", "scskiller_gen.db")).ToList();
        var r = Assert.Single(gen, r => r.Tag == 'R');
        Assert.Equal(RtCollections.Collection(lib, c.Library, x.Global, g.Hash, o.Hash, 32, 8, 1, 4), r.Payload);
        Assert.Contains(gen, b => b.Tag == 'B' && Hex(b.Payload.AsSpan(0, 20)) == c.Library);
        Assert.Equal(1, Planner.SkippedIn(Path.Combine(dir, "work")));
    }

    sealed class Log(Action<string> a) : IProgress<string> { public void Report(string v) => a(v); }

    sealed class OneShader(string sha1, byte[] bytes) : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { if (sha1s.Contains(sha1)) sink(sha1, bytes); }
    }

    /// <summary>Elden Ring (EasyAntiCheat: no recording). Its 4,281 lib_6_3 libraries carry no RDAT subobjects (no root
    /// signatures, configs, hit groups or associations), one entry point each (closest hit and any hit in separate libraries),
    /// so its plan GUESSES (<see cref="RtCollections.GuessedFamilies"/>): a global root signature from the libraries'
    /// bindings, an empty local one, a collection and hit group per library, unverified in game. Offline: every library gets a
    /// 'Y' (none uncovered); NVIDIA only. SCSKILLER_RT_ER=warp: the plan's collections, materialized from the install, all
    /// create on WARP; =gpu (NVIDIA; not while TestEnv.GpuBusyElsewhere; holds the GPU lock; throwaway name, never
    /// eldenring.exe; its cache files deleted): a second process re-creating them hits the cache. Whether the game's own
    /// objects would hit is unknowable without its root signatures and pipeline shape (not in its files).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void EldenRingGuessedCollections()
    {
        var game = SCSKiller.Tests.FromSoft.FromSoftGameTests.Games["ER"].Game;
        if (!File.Exists(game.ExePath)) return;
        Ff7.Codecs();
        var reader = new SCSKiller.Core.FromSoft.FromSoftReader(Ff7.TempDir("rt-er-data"));
        var engine = reader.Detect(game)!;
        var index = reader.Index(game, engine, null, CancellationToken.None);
        var libs = index.Shaders.Values.Count(s => s.Stage == Stage.Library);
        var dir = Ff7.TempDir("rt-er");
        var log = new List<string>();
        var plan = new Planner().Build(game, engine, index, null, Nvidia, Path.Combine(dir, "plan"), new Log(log.Add), CancellationToken.None);
        foreach (var l in log.Where(l => l.StartsWith("ray tracing"))) output.WriteLine(l);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.Equal(libs, body.Count(r => r.Tag == 'Y'));
        Assert.Contains(log, l => l.Contains("guessed: RS + local RS from the library bindings, one collection per library; unverified in game"));
        Assert.Empty(Items(new Planner().Build(game, engine, index, null, Ff7.Amd, Path.Combine(dir, "amd"), null, CancellationToken.None)));

        var mode = Environment.GetEnvironmentVariable("SCSKILLER_RT_ER");
        var warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
        if (mode is not ("warp" or "gpu") || !File.Exists(warm)) return;
        if (mode == "gpu" && TestEnv.GpuBusyElsewhere) { output.WriteLine("GPU busy (another tool's lock): not measured"); return; }
        var rt = plan with { FilePath = Path.Combine(dir, "rt.bin") };
        PlanFile.Write(rt, body.Where(r => r.Tag is 'B' or 'Y'));
        var work = Path.Combine(dir, "work");
        new Planner().Materialize(rt, game, engine, reader, null, work, CancellationToken.None);
        Assert.Equal(0, Planner.SkippedIn(work));
        (long Done, long Failed, double[] Ms, string Log) Run(string exe, params string[] extra)
        {
            var psi = new ProcessStartInfo(warm, [work, exe, .. extra]) { RedirectStandardOutput = true, UseShellExecute = false };
            psi.Environment["SCSKILLER_WARM_TIMES"] = "1";
            string o;
            using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var done = System.Text.Json.JsonDocument.Parse(o.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]).RootElement;
            var ms = File.ReadAllLines(Path.Combine(work, "stage", "scskiller_warm_times.csv")).Select(l => double.Parse(l.Split(',')[1], CultureInfo.InvariantCulture)).Order().ToArray();
            return (done.GetProperty("done").GetInt64(), done.GetProperty("failed").GetInt64(), ms, File.ReadAllText(Path.Combine(work, "stage", "scskiller.log")));
        }
        string Stats(double[] v) => $"median {v[v.Length / 2]:F2} ms, p90 {v[(int)(v.Length * 0.9)]:F2}, max {v[^1]:F1}, total {v.Sum() / 1000:F1} s";

        if (mode == "warp")
        {
            var luid = Process.Start(new ProcessStartInfo(Path.Combine(Ff7.ProxyBin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd().Trim();
            var r = Run("scsktrter_warp.exe", "--adapter-luid", luid);
            output.WriteLine($"WARP: {r.Done} created, {r.Failed} failed ({Stats(r.Ms)})");
            Assert.True(r.Done == Read(Path.Combine(work, "scskiller_gen.db")).Count(x => x.Tag == 'R') && r.Failed == 0, r.Log[^Math.Min(r.Log.Length, 3000)..]);
            return;
        }
        var nvCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache");
        var before = Directory.GetFiles(nvCache).ToHashSet();
        var lockFile = TestEnv.GpuLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        using (new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose))
            try
            {
                var name = $"scsktrter{DateTime.Now:HHmmss}.exe";
                var cold = Run(name, "--threads", "1");
                var hot = Run(name, "--threads", "1");
                output.WriteLine($"{name}: first process {cold.Done} collections, {cold.Failed} failed: {Stats(cold.Ms)}");
                output.WriteLine($"{name}: second process (re-create): {Stats(hot.Ms)}");
                Assert.Equal(0, cold.Failed + hot.Failed);
                Assert.True(hot.Ms[hot.Ms.Length / 2] * 3 < cold.Ms[cold.Ms.Length / 2], "the re-create did not hit");
            }
            finally
            {
                var made = Directory.GetFiles(nvCache).Where(p => !before.Contains(p)).ToList();
                if (made.Count <= 4) foreach (var p in made) File.Delete(p);
                output.WriteLine($"{made.Count} new driver-cache files{(made.Count <= 4 ? ", deleted" : ": not all ours, left in place: " + string.Join(", ", made))}");
            }
    }

    /// <summary>The claim (NVIDIA): collections synthesized from the index's libraries, warmed under exe name X, make
    /// the game's own recorded state objects (Jedi: 2,917 collections, 5 pipelines linking them, 63 additions) cache hits
    /// under X. Timed per object on one thread (SCSKILLER_WARM_TIMES): the recording replayed under X after warming only
    /// synthesized collections, vs under a fresh name Z (cold), vs Z again (exact repeats). GPU part: SCSKILLER_GPU_TESTS=1;
    /// not while TestEnv.GpuBusyElsewhere; holds TestEnv.GpuLockPath; throwaway names, the cache files they create deleted.</summary>
    [Trait("Needs", "Gpu")]
    [Fact]
    public void SynthesizedCollectionsMakeJedisRecordedStateObjectsHits()
    {
        var warm = Path.Combine(Ff7.ProxyBin, "scskiller_warm.exe");
        if (Environment.GetEnvironmentVariable("SCSKILLER_GPU_TESTS") != "1" || !HasJedi || !File.Exists(warm)) return;
        if (TestEnv.GpuBusyElsewhere) { output.WriteLine("GPU busy (another tool's lock): not measured"); return; }
        var x = ReadJedi();
        var bc = JediShaders.Value;
        var dir = Ff7.TempDir("rt-gpu");
        var (flags, depth) = (x.Collections.GroupBy(c => c.Flags).MaxBy(g => g.Count())!.Key, x.Collections.GroupBy(c => c.Depth).MaxBy(g => g.Count())!.Key);
        var (payload, attributes) = x.Collections.GroupBy(c => (c.Payload, c.Attributes)).MaxBy(g => g.Count())!.Key;

        // A: only synthesized collections (our export names, the rule's config), for every library the recording's collections use
        var synth = Directory.CreateDirectory(Path.Combine(dir, "synth")).FullName;
        File.WriteAllBytes(Path.Combine(synth, "scskiller.db"), []);
        using (var f = File.Create(Path.Combine(synth, "scskiller_gen.db")))
        {
            var written = new HashSet<string>();
            void Blob(string h, byte[] b) { if (written.Add(h)) WriteBlob(f, h, b); }
            Blob(x.Global, x.Blobs[x.Global]);
            foreach (var lib in x.Collections.Select(c => c.Library).Distinct().Where(bc.ContainsKey))
            {
                var (g, o) = (Local(bc[lib], true), Local(bc[lib], false));
                Blob(g.Hash, g.Blob);
                Blob(o.Hash, o.Blob);
                Blob(lib, x.Blobs[lib]);
                Write(f, 'R', RtCollections.Collection(x.Blobs[lib], lib, x.Global, g.Hash, o.Hash, payload, attributes, depth, flags)!);
            }
        }
        // B: the recording's state objects, as recorded
        var game = Directory.CreateDirectory(Path.Combine(dir, "game")).FullName;
        File.WriteAllBytes(Path.Combine(game, "scskiller_gen.db"), []);
        using (var f = File.Create(Path.Combine(game, "scskiller.db")))
        {
            foreach (var h in Rehydrate.References(x.StateObjects)) WriteBlob(f, h, x.Blobs[h]);
            foreach (var r in x.StateObjects) Write(f, r.Tag, r.Payload);
        }
        var kinds = x.StateObjects.Select(r => r.Tag == 'A' ? "addition" : ParseStateObject(r).Type == 0 ? "collection" : "pipeline").ToArray();

        var nvCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache");
        var before = Directory.GetFiles(nvCache).ToHashSet();
        var stamp = DateTime.Now.ToString("HHmmss");
        var (nameX, nameZ) = ($"scsktrtx{stamp}.exe", $"scsktrtz{stamp}.exe");
        var lockFile = TestEnv.GpuLockPath;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        using (new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.DeleteOnClose))
            try
            {
                var (sOk, sFailed, sSec) = Run(synth, nameX, false);
                output.WriteLine($"synthesized collections warmed under {nameX}: {sOk} created, {sFailed} failed, {sSec:F0} s");
                Assert.Equal(0, sFailed);
                var rows = new List<(string Run, double[] Ms)>();
                foreach (var (run, name) in new[] { ("after the synthesized warm (X)", nameX), ("cold (fresh name Z)", nameZ), ("exact repeat (Z again)", nameZ) })
                    rows.Add((run, Times(game, name)));
                foreach (var kind in kinds.Distinct())
                    foreach (var (run, ms) in rows)
                    {
                        var v = ms.Where((_, i) => kinds[i] == kind).Order().ToArray();
                        output.WriteLine($"{kind,-10} x{v.Length,-5} {run,-32} median {v[v.Length / 2],8:F2} ms, p90 {v[(int)(v.Length * 0.9)],8:F2}, max {v[^1],8:F1}, >= 20 ms {v.Count(t => t >= 20),5}, total {v.Sum() / 1000,6:F1} s");
                    }
                double Median(double[] ms) { var v = ms.Where((_, i) => kinds[i] == "collection").Order().ToArray(); return v[v.Length / 2]; }
                Assert.True(Median(rows[0].Ms) * 4 < Median(rows[1].Ms), "recorded collections are not cheaper after the synthesized warm");
            }
            finally
            {
                var made = Directory.GetFiles(nvCache).Where(p => !before.Contains(p)).ToList();
                if (made.Count <= 8) foreach (var p in made) File.Delete(p);
                output.WriteLine($"{made.Count} new driver-cache files{(made.Count <= 8 ? ", deleted" : ": not all ours, left in place: " + string.Join(", ", made))}");
            }

        (long Ok, long Failed, double Seconds) Run(string work, string exe, bool timed)
        {
            var psi = new ProcessStartInfo(warm, timed ? [work, exe, "--threads", "1"] : [work, exe]) { RedirectStandardOutput = true, UseShellExecute = false };
            if (timed) psi.Environment["SCSKILLER_WARM_TIMES"] = "1";
            string o;
            using (var p = Process.Start(psi)!) { o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            var done = System.Text.Json.JsonDocument.Parse(o.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]).RootElement;
            return (done.GetProperty("done").GetInt64() - done.GetProperty("failed").GetInt64(), done.GetProperty("failed").GetInt64(), done.GetProperty("seconds").GetDouble());
        }

        double[] Times(string work, string exe)
        {
            var (_, failed, _) = Run(work, exe, true);
            Assert.Equal(0, failed);
            var ms = new double[kinds.Length];
            foreach (var l in File.ReadAllLines(Path.Combine(work, "stage", "scskiller_warm_times.csv")).Select(l => l.Split(',')))
                ms[int.Parse(l[0])] = double.Parse(l[1], CultureInfo.InvariantCulture);
            return ms;
        }
    }
}
