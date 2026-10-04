using System.Buffers.Binary;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Dagor;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using static SCSKiller.Tests.Dagor.DagorReaderTests;

namespace SCSKiller.Tests.Dagor;

/// <summary>War Thunder's DirectX 12 dump (gameDX12.ps50.shdump.bin) built synthetically: dxil::ShaderContainer metadata with
/// a dxil::ShaderHeader per stage, and shader-class passes; and the root signatures DagorRootSig rebuilds from the headers.</summary>
public class DagorDx12Tests
{
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA1.HashData(b));

    /// <summary>A dxil::ShaderHeader: the resource usage table, inOutSemanticMask, shaderType, the low feature flags.</summary>
    static byte[] Header(byte type, uint t = 0, uint s = 0, ushort b = 0, ushort u = 0, uint bindless = 0, byte rootConstants = 0,
        byte special = 0, uint inOut = 0, uint features = 0)
    {
        var h = new byte[DagorRootSig.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), t);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), s);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), bindless);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), b);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), u);
        (h[24], h[25]) = (rootConstants, special);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(32), inOut);
        h[81] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), features);
        return h;
    }

    /// <summary>A dxil::ShaderContainer: type, flags, a 20-byte hash, its data list, the layout hash; the data one dxil::Shader
    /// (header, bytecode offset and size, an empty source, the layout hash: 124 bytes) or, combined, a
    /// dxil::VertexShaderPipeline (VS, HS, DS, GS pointers and the layout hash) followed by its shaders.</summary>
    static byte[] Meta(ushort type, ushort flags, params (byte[] Header, int Offset, int Size, int Slot)[] stages)
    {
        static byte[] Shader((byte[] Header, int Offset, int Size, int Slot) s) =>
            [.. s.Header, .. BitConverter.GetBytes(s.Offset), .. BitConverter.GetBytes(s.Size), .. new byte[20]];
        byte[] data;
        if (type == 0) data = Shader(stages[0]);
        else
        {
            data = new byte[24 + 124 * stages.Length];
            for (var i = 0; i < stages.Length; i++)
            {
                var at = 24 + 124 * i;
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4 * stages[i].Slot), at - 4 * stages[i].Slot);
                Shader(stages[i]).CopyTo(data, at);
            }
        }
        var m = new byte[40 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(m, type);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(2), flags);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(24), 40 - 24);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(28), data.Length);
        data.CopyTo(m, 40);
        return m;
    }

    [Fact]
    public void EveryPassIsAPipelineAndEveryShaderCarriesItsHeader()
    {
        var vsHeader = Header(0, inOut: 1);
        var gsHeader = Header(2);
        var psHeader = Header(1, t: 1, s: 1);
        var csHeader = Header(5, u: 1);
        byte[][] entries = [[.. Vs, .. Gs], Ps, Cs, Ps];
        byte[][] meta =
        [
            Meta(1, 0, (vsHeader, 0, Vs.Length, 0), (gsHeader, Vs.Length, Gs.Length, 3)),
            Meta(0, 0, (psHeader, 0, Ps.Length, 0)),
            Meta(0, 0, (csHeader, 0, Cs.Length, 0)),
            Meta(0, 1, (psHeader, 0, Ps.Length, 0)),   // stream output: not read
        ];
        var dump = Dump(entries, 1, metadata: meta, passes: [(0, 0), (0xFFFF, 1), (0, 0xFFFF), (0, 0), (0, 2)]);
        var game = Install("dagor-dx12", dump, "video{\n  driver:t=\"dx12\"\n}", "gameDX12.ps50.shdump.bin");
        var reader = new DagorReader();
        var engine = reader.Detect(game)!;
        Assert.Equal(new EngineInfo(DagorReader.Family, "11.3", DagorReader.CbvRangesFork, "D3D12", false, null), engine);
        var index = reader.Index(game, engine, null, CancellationToken.None);

        Assert.Equal([DagorReader.Platform12], index.Platforms);
        Assert.All(index.Maps, m => Assert.True(m.IsPipeline));
        Assert.Equal([[Sha(Vs), Sha(Gs), Sha(Ps)], [Sha(Cs)]], index.Maps.Select(m => m.Shaders.ToList()));   // the depth pass and the stream-output entry left out
        Assert.Equal(vsHeader, index.Shaders[Sha(Vs)].EngineHeader);
        Assert.Equal(gsHeader, index.Shaders[Sha(Gs)].EngineHeader);
        Assert.Equal(psHeader, index.Shaders[Sha(Ps)].EngineHeader);

        var got = new Dictionary<string, byte[]>();
        reader.ReadShaders(game, engine, new HashSet<string> { Sha(Gs), Sha(Cs) }, (h, b) => got.Add(h, b), CancellationToken.None);
        Assert.Equal(Gs, got[Sha(Gs)]);
        Assert.Equal(Cs, got[Sha(Cs)]);

        Assert.Equal(RootSig.Rule.DagorCbvRanges, RootSig.RuleFor(engine));
        Assert.Equal(Readiness.Ready, new Planner().Check(game, engine, null, Ff7.Nvidia).Readiness);
        var plan = new Planner().Build(game, engine, index, null, Ff7.Nvidia with { PerStageCache = true }, Ff7.TempDir("dagor-dx12-plan"), null, CancellationToken.None);
        Assert.Equal(2, plan.Stats.StageSets);
        Assert.Equal(0, plan.Stats.LeftOut);
        Assert.Equal(2, plan.Stats.RootSignatures);
    }

    [Fact]
    public void APassNamingAShaderTheDumpLacksThrows()
    {
        var dump = Dump([Ps], 0, metadata: [Meta(0, 0, (Header(1), 0, Ps.Length, 0))], passes: [(0xFFFF, 1)]);
        var game = Install("dagor-dx12-bad", dump, null, "gameDX12.ps50.shdump.bin");
        Assert.Throws<InvalidDataException>(() => new DagorReader().Index(game, new DagorReader().Detect(game)!, null, CancellationToken.None));
        var outside = Dump([Ps], 0, metadata: [Meta(0, 0, (Header(1), 8, Ps.Length, 0))]);   // bytecode past the entry
        Assert.Throws<InvalidDataException>(() => DagorReader.Entries(DagorReader.Body(outside), CancellationToken.None).Select(DagorReader.Stages).ToList());
    }

    static ShaderInfo Info(Stage stage, byte[] header) => new(Sha(header) + stage, stage, "6_4", 0, new(0, 0, 0, 0), [], [], [], EngineHeader: header);

    [Fact]
    public void GraphicsRootSignatureFollowsTheDriversOrderAndSharedOffsets()
    {
        var stages = new Dictionary<Stage, ShaderInfo>
        {
            [Stage.Vertex] = Info(Stage.Vertex, Header(0, t: 0b0110, s: 0b1, b: 0b101, inOut: 1)),
            [Stage.Pixel] = Info(Stage.Pixel, Header(1, t: 0b1011, s: 0b11, b: 0b1, u: 0b1, rootConstants: 4, special: 1)),
            [Stage.Geometry] = Info(Stage.Geometry, Header(2, t: 0b1000)),
        };
        var d = DagorRootSig.Build(stages);
        Assert.True(d.Version10);
        Assert.Equal(0x1u | 0x4 | 0x8, d.Flags);   // input layout; no hull or domain shader
        Assert.Equal(new uint[][]
        {
            [1, 0, 7, 1, 1],                              // draw id: b7 space 1
            [1, 5, 8, 5, 4],                              // PS root constants: b8, space 1 + 4 dwords
            [2, 5, 0, 0, 0], [2, 1, 0, 0, 0], [2, 1, 2, 0, 0],
            [0, 5, 3, 1, 0, 0, 0, 3, 1, 1, 0, 1],         // PS samplers, one range each
            [0, 1, 3, 1, 0, 0, 0],
            [0, 5, 0, 2, 0, 0, 0, 0, 1, 3, 0, 2],         // PS t0-t1, t3 at offset 2
            [0, 1, 0, 2, 1, 0, 0],                        // VS t1-t2: offset 0 among VS | GS (t1-t3)
            [0, 4, 0, 1, 3, 0, 2],                        // GS t3: offset 2
            [0, 5, 1, 1, 0, 0, 0],
        }, d.Rows);
        var blob = RootSig.Serialize(d, []);
        Assert.Equal(1u, BitConverter.ToUInt32(RootSig.Rts0(blob)));   // D3D_ROOT_SIGNATURE_VERSION_1_0
        var ranges = RootSig.Parse(blob);
        Assert.Equal(d.Flags, ranges.Flags);
        Assert.Contains(ranges.Slots, x => (x.Vis, x.Type, x.Base, x.Count, x.Space) == (4u, 0u, 3u, 1u, 0u));
    }

    [Fact]
    public void ConstantBuffersGoInTablesWhenTheCacheSaysSo()
    {
        var stages = new Dictionary<Stage, ShaderInfo>
        {
            [Stage.Vertex] = Info(Stage.Vertex, Header(0, b: 0b101, inOut: 1)),
            [Stage.Pixel] = Info(Stage.Pixel, Header(1, b: 0b1, rootConstants: 2)),
            [Stage.Geometry] = Info(Stage.Geometry, Header(2, b: 0b100)),
        };
        var d = DagorRootSig.Build(stages, cbvRanges: true);
        Assert.Equal(new uint[][]
        {
            [1, 5, 8, 3, 2],                            // PS root constants
            [0, 5, 2, 1, 0, 0, 0],                      // PS b0
            [0, 1, 2, 1, 0, 0, 0, 2, 1, 2, 0, 1],       // VS b0, b2: offsets among VS | GS (b0, b2)
            [0, 4, 2, 1, 2, 0, 1],                      // GS b2: offset 1
        }, d.Rows);
        RootSig.Serialize(d, []);

        // the engine's setting comes from the header of the cache the game writes (CX12, version 34, flags at 28)
        var game = Install("dagor-cbv", Dump([Ps], 0, metadata: [Meta(0, 0, (Header(1), 0, Ps.Length, 0))]), null, "gameDX12.ps50.shdump.bin");
        Assert.True(DagorReader.CbvRanges(game));   // no cache yet: War Thunder's
        var header = new byte[152];
        "CX12"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 34);
        Directory.CreateDirectory(Path.Combine(game.InstallDir, "cache"));
        File.WriteAllBytes(Path.Combine(game.InstallDir, "cache", "dx12.cache"), header);
        var engine = new DagorReader().Detect(game)!;
        Assert.Equal((null, RootSig.Rule.Dagor), (engine.Fork, RootSig.RuleFor(engine)));
        var (stamp, hash) = (new DagorReader().IndexStamp(game), new DagorReader().Index(game, engine, null, CancellationToken.None).ContentHash);
        header[28] = 1;
        File.WriteAllBytes(Path.Combine(game.InstallDir, "cache", "dx12.cache"), header);
        engine = new DagorReader().Detect(game)!;
        Assert.Equal((DagorReader.CbvRangesFork, RootSig.Rule.DagorCbvRanges), (engine.Fork, RootSig.RuleFor(engine)));
        // the warm goes stale and the next compile's plan is rebuilt: both follow the mode
        Assert.NotEqual(stamp, new DagorReader().IndexStamp(game));
        Assert.NotEqual(hash, new DagorReader().Index(game, engine, null, CancellationToken.None).ContentHash);
    }

    [Fact]
    public void BindlessTablesAreSharedAndComputeGetsTheVendorExtension()
    {
        var d = DagorRootSig.Build(new Dictionary<Stage, ShaderInfo>
        {
            [Stage.Vertex] = Info(Stage.Vertex, Header(0, bindless: 0b1101)),   // sampler space 1; SRV spaces 1 and 2
            [Stage.Pixel] = Info(Stage.Pixel, Header(1, bindless: 0b0100, features: 0x02000000)),
        });
        Assert.Equal(0x400u | 0x4 | 0x8 | 0x10, d.Flags);   // resource heap indexing; no HS, DS or GS
        Assert.Equal(new uint[][]
        {
            [0, 1, 3, uint.MaxValue, 0, 1, 0],                                 // VS only: its visibility
            [0, 0, 0, uint.MaxValue, 0, 1, 0, 0, uint.MaxValue, 0, 2, 0],     // PS then VS: all stages, space 1 once
        }, d.Rows);
        RootSig.Serialize(d, []);

        var cs = DagorRootSig.Build(new Dictionary<Stage, ShaderInfo> { [Stage.Compute] = Info(Stage.Compute, Header(5, b: 1, u: 0b11, special: 2)) });
        Assert.Equal(0u, cs.Flags);
        Assert.Equal(new uint[][] { [2, 0, 0, 0, 0], [0, 0, 1, 2, 0, 0, 0], [0, 0, 1, 1, 0, 99, 0] }, cs.Rows);
        RootSig.Serialize(cs, []);
    }
}
