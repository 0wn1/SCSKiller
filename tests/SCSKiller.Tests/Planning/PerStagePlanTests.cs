using SCSKiller.Core;
using SCSKiller.Core.Planning;
using Xunit.Abstractions;
using static SCSKiller.Core.Planning.PsoDb;
using static SCSKiller.Tests.Planning.ExactLayoutsTests;

namespace SCSKiller.Tests.Planning;

/// <summary>The planner's per-stage branch (<see cref="VendorCaps.PerStageCache"/>, AMD): stage units covered once, each PSO
/// a synthesized stream carrying the state its units are keyed on.</summary>
public class PerStagePlanTests(ITestOutputHelper output)
{
    static readonly EngineInfo Ue426 = new("Unreal", "4.26", "GAME_FinalFantasy7Rebirth", "D3D12", false, null);

    static readonly SigElement PosOut = new("SV_Position", 0, 0, 0xF, 1, 3), UvOut = new("TEXCOORD", 0, 1, 0x3, 0, 3);
    static ShaderInfo Vs(string name, params SigElement[] inputs) => Shader(name, Stage.Vertex, inputs, [PosOut, UvOut]);
    static ShaderInfo Ps(string name, params SigElement[] outputs) => Shader(name, Stage.Pixel, [PosOut, UvOut], outputs);

    // VS1 recorded (L0); VS2 has VS1's input signature (L1, inferred); VS3 reads an element never recorded (guessed);
    // PS1 recorded to fp16 and PS3 to R32_FLOAT with write mask 0; PS2 (PS1's outputs) is inferred fp16
    static readonly ShaderInfo Vs1 = Vs("vs1", In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3));
    static readonly ShaderInfo Vs2 = Vs("vs2", In("POSITION", 0, 0, 7), In("TEXCOORD", 0, 1, 3));
    static readonly ShaderInfo Vs3 = Vs("vs3", In("BLENDINDICES", 0, 0, 0xF, 1));
    static readonly ShaderInfo Ps1 = Ps("ps1", Target(0)), Ps2 = Ps("ps2", Target(0)), Ps3 = Ps("ps3", Target(0), Target(1));
    static readonly ShaderInfo Cs1 = Shader("cs1", Stage.Compute, []);
    static readonly List<LayoutElem> Layout1 = [new("POSITION", 0, 6, 0), new("TEXCOORD", 0, 34, 12, 1), new("COLOR", 0, 28, 16)]; // COLOR isn't read

    static ShaderIndex Index() => new("synthetic", ["PCD3D_SM6"], new[] { Vs1, Vs2, Vs3, Ps1, Ps2, Ps3, Cs1 }.ToDictionary(s => s.Sha1),
        [new ShaderMap("m", "Game", "PCD3D_SM6", new[] { Vs1, Vs2, Vs3, Ps1, Ps2, Ps3, Cs1 }.Select(s => s.Sha1).ToList())]);

    /// <summary>The root signature the planner builds for a stage set (FF7's rule), as the recording would carry it.</summary>
    static (string Sha, byte[] Blob) Rs(params ShaderInfo[] stages)
    {
        var b = RootSig.Serialize(RootSig.Build(RootSig.Rule.Ff7, stages.ToDictionary(s => s.Stage), true), RootSig.StaticSamplers(RootSig.Rule.Ff7));
        return (Hex(System.Security.Cryptography.SHA1.HashData(b)), b);
    }

    static string Recording(string dir)
    {
        var path = Path.Combine(dir, "recording.db");
        var (rsA, blobA) = Rs(Vs1, Ps1);
        var (rsB, blobB) = Rs(Vs1, Ps3);
        using var f = File.Create(path);
        WriteBlob(f, rsA, blobA);
        if (rsB != rsA) WriteBlob(f, rsB, blobB);
        Write(f, 'S', Gfx(rsA, Vs1, Ps1, Layout1, [R16G16B16A16Float]).Payload);
        Write(f, 'S', Gfx(rsB, Vs1, Ps3, Layout1, [41, 41], [0xF, 0]).Payload); // R32_FLOAT x2, RT 1 masked off
        return path;
    }

    static List<(Rec Rec, PsoState State)> Psos(Plan plan) =>
        PlanFile.Read(plan.FilePath).Records.Where(r => r.Tag == 'S').Select(r => (r, ParseState(r) ?? new PsoState(Parse(r).Rs, Parse(r).Stages, [], 0, [], [], [], 0, 1))).ToList();

    [Fact]
    public void CoversEachStageUnitOnceWithItsState()
    {
        var dir = Ff7.TempDir("perstage");
        var log = new List<string>();
        var plan = new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(Recording(dir)), Ff7.Amd, Path.Combine(dir, "plan"),
            new SyncLog(s => { log.Add(s); output.WriteLine(s); }), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        Assert.DoesNotContain(body, r => r.Tag is 'P' or 'G' or 'C'); // every PSO is its own synthesized stream
        var psos = Psos(plan);
        foreach (var (r, s) in psos) output.WriteLine($"{string.Join('+', s.Stages.Keys)} {string.Join(',', s.Stages.Values.Select(h => h[..4]))} topo {s.Topology} rt [{string.Join(',', s.RtFormats)}] masks [{string.Join(',', s.RtWriteMasks)}] dsv {s.Dsv} layout {L(s.Layout)}");
        string? Sha(PsoState s, Stage st) => s.Stages.GetValueOrDefault((int)st);

        // the units, each once: VS 1-3 alone (depth passes, a D32 depth buffer, no RT), VS 1-3 before a PS, PS 1-3, CS 1;
        // VS1+PS1 and VS1+PS3 are recorded (they replay as they are): not planned
        Assert.Equal(new[] { Vs1, Vs2, Vs3 }.Select(v => v.Sha1).Order(), psos.Where(p => p.State.Stages.Count == 1 && Sha(p.State, Stage.Vertex) != null).Select(p => Sha(p.State, Stage.Vertex)!).Order());
        Assert.All(psos.Where(p => p.State.Stages.Count == 1 && Sha(p.State, Stage.Vertex) != null), p => Assert.Equal((0, D32Float), (p.State.RtFormats.Length, p.State.Dsv)));
        Assert.Single(psos, p => Sha(p.State, Stage.Compute) == Cs1.Sha1);
        var pairs = psos.Where(p => Sha(p.State, Stage.Pixel) != null).ToList();
        Assert.DoesNotContain(pairs, p => Sha(p.State, Stage.Vertex) == Vs1.Sha1 && Sha(p.State, Stage.Pixel) is var ps && (ps == Ps1.Sha1 || ps == Ps3.Sha1));
        Assert.Equal(new[] { Vs2.Sha1, Vs3.Sha1 }.Order(), pairs.Select(p => Sha(p.State, Stage.Vertex)!).Distinct().Order()); // VS1's "ps" unit is recorded
        Assert.Equal((2L, 3L, 2L), (plan.Stats.ExactUnits, plan.Stats.InferredUnits, plan.Stats.GuessedUnits)); // VS1 alone + CS / VS2 x2 + PS2 / VS3 x2
        Assert.Equal(new[] { Ps2.Sha1 }, pairs.Select(p => Sha(p.State, Stage.Pixel)!).Except([Ps1.Sha1, Ps3.Sha1]).Distinct());
        Assert.Equal(3 + 1 + 2, psos.Count); // VS alone x3, CS, then VS2+PS2 (two new units in one PSO) and VS3 with a covered PS: 6, not 9 pairs

        // state: VS2 gets VS1's recorded read layout (COLOR, unread, left out), VS3 a guess; RT formats / masks per shape
        var read1 = ExactLayouts.ReadLayout(Layout1, Vs1);
        Assert.All(psos.Where(p => Sha(p.State, Stage.Vertex) is { } v && v != Vs3.Sha1), p => Assert.Equal(L(read1), L(p.State.Layout)));
        Assert.All(psos.Where(p => Sha(p.State, Stage.Vertex) == Vs3.Sha1), p => Assert.Equal(L(ExactLayouts.ReadLayout(Planner.VsLayout(Vs3), Vs3)), L(p.State.Layout)));
        Assert.All(pairs, p => Assert.Equal(3u, p.State.Topology));
        foreach (var (_, s) in pairs)
            if (Sha(s, Stage.Pixel) == Ps3.Sha1) Assert.Equal(([41u, 41u], new byte[] { 0xF, 0 }), (s.RtFormats, s.RtWriteMasks));
            else Assert.Equal([R16G16B16A16Float], s.RtFormats);

        // guesses last
        var firstGuess = psos.FindIndex(p => Sha(p.State, Stage.Vertex) == Vs3.Sha1);
        Assert.All(psos.Skip(firstGuess), p => Assert.Equal(Vs3.Sha1, Sha(p.State, Stage.Vertex)));

        // stats: VS3's two units are the only guesses; 2 of 3 VSs resolved from the recording (L0 VS1, L1 VS2): a warning
        Assert.Equal((2L, psos.Count), (plan.Stats.Recorded, (int)plan.Stats.Generated));
        Assert.Equal(2, plan.Stats.GuessedUnits);
        Assert.Equal(2.0 / 3, plan.Stats.LayoutCoverage, 6);
        Assert.Equal(3 + 2 + 1 + 1, plan.Stats.ExactUnits + plan.Stats.InferredUnits + plan.Stats.GuessedUnits); // VS alone x3, VS2/VS3 before a PS, PS2, CS
        Assert.Contains(log, l => l.StartsWith("warning: only 67%"));
        Assert.Contains(log, l => l.StartsWith("per-stage (amd): "));
    }

    [Fact]
    public void WholePipelinesWithoutAPerStageCache()
    {
        var dir = Ff7.TempDir("perstage-off");
        var plan = new Planner().Build(Ff7.Game, Ue426, Index(), new Recording(Recording(dir)), Ff7.Amd with { PerStageCache = false }, Path.Combine(dir, "plan"), null, CancellationToken.None);
        Assert.Contains(PlanFile.Read(plan.FilePath).Records, r => r.Tag == 'P'); // recorded templates with shaders swapped, every pair
        Assert.Equal((0L, 0L, 0L, 0.0), (plan.Stats.ExactUnits, plan.Stats.InferredUnits, plan.Stats.GuessedUnits, plan.Stats.LayoutCoverage));
    }

    /// <summary>A shader using AMD's AGS intrinsics (a UAV in space 0x7FFF0ADE) is left out on NVIDIA (no UE 4 root signature
    /// has the slot: E_INVALIDARG, and the game never uses that permutation there), counted as "vendor_extension". AMD gets past
    /// that filter, but a UE 4 root signature has no AGS slot there either, so the pre-emit guard leaves it out ("rs_uncovered").</summary>
    [Fact]
    public void AgsShadersOnlyOnAmd()
    {
        var ags = Shader("csAgs", Stage.Compute, []) with { Bindings = [new Binding("uav", 0x7FFF0ADE, 0, 1)] };
        var all = new[] { Vs1, Ps1, Cs1, ags };
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6"], all.ToDictionary(s => s.Sha1), [new ShaderMap("m", "Game", "PCD3D_SM6", all.Select(s => s.Sha1).ToList())]);
        var dir = Ff7.TempDir("perstage-ags");
        foreach (var (caps, stat) in new[] { (Ff7.Nvidia, "vendor_extension 1"), (Ff7.Amd, "rs_uncovered 1") })
        {
            var log = new List<string>();
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Recording(dir)), caps, Path.Combine(dir, caps.Profile), new SyncLog(log.Add), CancellationToken.None);
            Assert.DoesNotContain(Psos(plan), p => p.State.Stages.ContainsValue(ags.Sha1));
            Assert.Contains(Psos(plan), p => p.State.Stages.ContainsValue(Cs1.Sha1));
            Assert.Contains(log, l => l.Contains(stat));
        }
    }

    /// <summary>[WaveSize] compute on the readers' "&lt;platform&gt; wave&lt;N&gt;" platforms: NVIDIA (32 lanes only) plans none of
    /// it, AMD (32 and 64 lanes) the platforms whose range takes 64.</summary>
    [Fact]
    public void WaveSizePlatformsOnlyOnAmd()
    {
        ShaderInfo w64 = Shader("cs-wave64", Stage.Compute, []), w64To128 = Shader("cs-wave64-128", Stage.Compute, []), w16 = Shader("cs-wave16", Stage.Compute, []);
        var all = new[] { Vs1, Ps1, Cs1, w64, w64To128, w16 };
        var index = new ShaderIndex("synthetic", ["PCD3D_SM6", "PCD3D_SM6 wave16", "PCD3D_SM6 wave64", "PCD3D_SM6 wave64-128"], all.ToDictionary(s => s.Sha1),
            [new ShaderMap("m", "Game", "PCD3D_SM6", [Vs1.Sha1, Ps1.Sha1, Cs1.Sha1]), new ShaderMap("m", "Game", "PCD3D_SM6 wave64", [w64.Sha1]),
             new ShaderMap("m", "Game", "PCD3D_SM6 wave64-128", [w64To128.Sha1]), new ShaderMap("m", "Game", "PCD3D_SM6 wave16", [w16.Sha1])]);
        var dir = Ff7.TempDir("perstage-wave");
        foreach (var (caps, want) in new[] { (Ff7.Nvidia, new[] { Cs1 }), (Ff7.Amd, new[] { Cs1, w64, w64To128 }) })
        {
            var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Recording(dir)), caps, Path.Combine(dir, caps.Profile), null, CancellationToken.None);
            var body = PlanFile.Read(plan.FilePath).Records.ToList();
            bool Planned(ShaderInfo s) => body.Any(r => r.Payload.AsSpan().IndexOf(Convert.FromHexString(s.Sha1)) >= 0);
            Assert.Equal(want.Select(s => s.Sha1).Order(), new[] { Cs1, w64, w64To128, w16 }.Where(Planned).Select(s => s.Sha1).Order());
        }
    }

    /// <summary>FF7 Rebirth planned for AMD from the AMD session: how the NVIDIA machine's recording (another player, another
    /// part of the game) is covered by the plan's units plus the session's own (it replays as it is), per stage.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7AmdPlanCoversTheNvidiaRecording()
    {
        if (!Ff7.HasIndex || !File.Exists(Ff7.AmdSessionDb)) return;
        var index = Ff7.GsIndex();
        var dir = Ff7.TempDir("perstage-ff7");
        var plan = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Ff7.AmdSessionDb), Ff7.Amd, dir, new SyncLog(output.WriteLine), CancellationToken.None);
        var body = PlanFile.Read(plan.FilePath).Records.ToList();
        var streams = body.Where(r => r.Tag == 'S').ToList();
        Assert.Equal(plan.Stats.Generated, streams.Count);
        Assert.DoesNotContain(body, r => r.Tag == 'P');

        // unit keys: root signatures by content (the plan's and the session's blobs), shader signatures from the index
        var x = ExactLayouts.FromDb(Ff7.RecordingDb, UnitPolicy.Amd, index.Shaders);
        var session = ExactLayouts.FromDb(Ff7.AmdSessionDb, UnitPolicy.Amd, index.Shaders);
        var rootSigs = session.Graphics.Select(s => s.Rs).Concat(session.Compute.Select(c => c.Rs)).ToHashSet();
        foreach (var r in body.Where(r => r.Tag == 'B')) x.AddRootSignature(Hex(r.Payload.AsSpan(0, 20)), r.Payload[20..]);
        foreach (var r in PsoDb.Read(Ff7.AmdSessionDb).Where(r => r.Tag == 'B' && rootSigs.Contains(Hex(r.Payload.AsSpan(0, 20)))))
            x.AddRootSignature(Hex(r.Payload.AsSpan(0, 20)), r.Payload[20..]);
        var covered = new HashSet<Unit>();
        foreach (var r in streams)
            if (ParseState(r) is { } s) covered.UnionWith(UnitCover.UnitsOf(x, s));
            else { var c = Parse(r); covered.Add(new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], x.RsKey(c.Rs))); }
        foreach (var s in session.Graphics) covered.UnionWith(UnitCover.UnitsOf(x, s));
        foreach (var c in session.Compute) covered.Add(new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], x.RsKey(c.Rs)));

        var want = UnitCover.RecordedUnits(x).Where(u => index.Shaders.ContainsKey(u.Shader)).ToHashSet(); // library shaders only
        var rows = want.GroupBy(u => u.Stage).OrderBy(g => g.Key).Select(g => (Stage: g.Key, Units: g.Count(), Hit: g.Count(covered.Contains))).ToList();
        output.WriteLine($"{plan.Stats}; NVIDIA recording's units covered: {string.Join(", ", rows.Select(r => $"{r.Stage} {r.Hit}/{r.Units}"))}, "
            + $"total {rows.Sum(r => r.Hit)}/{rows.Sum(r => r.Units)}");
        foreach (var u in want.Where(u => !covered.Contains(u) && u.Stage is Stage.Vertex or Stage.Pixel).Take(20)) output.WriteLine($"  missed {u.Stage} {u.Shader[..8]} {u.Key}");
        Assert.All(rows.Where(r => r.Stage is not (Stage.Vertex or Stage.Pixel)), r => Assert.Equal(r.Units, r.Hit)); // keyed on the root signature alone
        Assert.True(plan.Stats.LayoutCoverage >= 0.8);
        Assert.True(rows.Sum(r => r.Hit) >= 0.97 * rows.Sum(r => r.Units)); // 989/1001 (VS 219/227, PS 309/313)
    }

    /// <summary>Maximum mode on FF7: every stage set as well, and still exactly the per-stage plan's units, under both
    /// policies. Under NVIDIA's (once it reports a per-stage cache) it is today's whole-pipeline plan: the same (root
    /// signature, stages) tuples, as many PSOs.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Ff7MaximumAddsEveryStageSetButNoUnit()
    {
        if (!Ff7.HasIndex || !File.Exists(Ff7.AmdSessionDb)) return;
        var index = Ff7.GsIndex();
        var dir = Ff7.TempDir("perstage-max");
        var today = new Planner().Build(Ff7.Game, Ue426, index, new Recording(Ff7.RecordingDb), Ff7.Nvidia, Path.Combine(dir, "today"), null, CancellationToken.None);
        foreach (var (name, caps, rec) in new[] { ("nvidia", Ff7.Nvidia with { PerStageCache = true }, Ff7.RecordingDb), ("amd", Ff7.Amd, Ff7.AmdSessionDb) })
        {
            var policy = UnitPolicy.For(caps)!;
            Plan Build(bool maximum) => new Planner().Build(Ff7.Game, Ue426, index, new Recording(rec), caps, Path.Combine(dir, $"{name}-{maximum}"), new SyncLog(output.WriteLine), CancellationToken.None, maximum);
            var (units, max) = (Build(false), Build(true));
            HashSet<Unit> UnitsOf(Plan plan)
            {
                var x = ExactLayouts.FromDb(rec, policy, index.Shaders);
                var body = PlanFile.Read(plan.FilePath).Records.ToList();
                foreach (var r in body.Where(r => r.Tag == 'B')) x.AddRootSignature(Hex(r.Payload.AsSpan(0, 20)), r.Payload[20..]);
                var set = UnitCover.RecordedUnits(x).ToHashSet();
                foreach (var r in body.Where(r => r.Tag == 'S'))
                    if (ParseState(r) is { } s) set.UnionWith(UnitCover.UnitsOf(x, s));
                    else { var c = Parse(r); set.Add(new Unit(Stage.Compute, c.Stages[(int)Stage.Compute], x.RsKey(c.Rs))); }
                return set;
            }
            var (u1, u2) = (UnitsOf(units), UnitsOf(max));
            output.WriteLine($"{name} policy: per-stage {units.Stats.Generated} PSOs, maximum {max.Stats.Generated} PSOs; units {u1.Count} / {u2.Count}");
            Assert.True(u1.SetEquals(u2)); // per-stage still compiles every unit
            Assert.True(max.Stats.Generated > units.Stats.Generated);
            Assert.Equal((units.Stats.ExactUnits, units.Stats.InferredUnits, units.Stats.GuessedUnits), (max.Stats.ExactUnits, max.Stats.InferredUnits, max.Stats.GuessedUnits));
            if (name != "nvidia") continue;
            var tuples = PlanFile.Read(max.FilePath).Records.Where(r => r.Tag == 'S').Select(r => Parse(r).Tuple).ToHashSet();
            var todays = Ff7.PlanTuples(today.FilePath);
            output.WriteLine($"today's pair plan: {today.Stats.Generated} PSOs, {todays.Count} tuples; maximum {tuples.Count} tuples, only today {todays.Except(tuples).Count()}, only maximum {tuples.Except(todays).Count()}");
            Assert.True(todays.SetEquals(tuples));
            Assert.Equal(today.Stats.Generated, max.Stats.Generated);
        }
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; the test reads the log right after Build.</summary>
    sealed class SyncLog(Action<string> a) : IProgress<string> { public void Report(string value) => a(value); }
}
