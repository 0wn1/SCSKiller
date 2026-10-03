using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Unreal;

/// <summary>Shaders stored inside the packages (bShareMaterialShaderCode=False). The layouts on synthetic bytes; the real
/// games (skipped when not installed) end to end: Detect, Index, ReadShaders.</summary>
public class InlineShadersTests(ITestOutputHelper output)
{
    /// <summary>An FShaderCode as UE cooks it: a prefix, the container, the optional-data trailer with packed resource counts.</summary>
    static byte[] Code(string hlsl, string target, byte cb, byte srv)
    {
        byte[] p = [0, 1, srv, cb, 0]; // FShaderCodePackedResourceCounts: UsageFlags, NumSamplers, NumSRVs, NumCBs, NumUAVs
        return [7, 7, 7, .. UnrealReaderTests.Fxc(hlsl, target), (byte)'p', .. BitConverter.GetBytes(p.Length), .. p, .. BitConverter.GetBytes(1 + 4 + p.Length + 4)];
    }

    static byte[] Zlib(byte[] b)
    {
        using var o = new MemoryStream();
        using (var z = new ZLibStream(o, CompressionLevel.Optimal)) z.Write(b);
        return o.ToArray();
    }

    static byte[] I32(int v) => BitConverter.GetBytes(v);

    static byte[] Anchor(int k) => [.. new byte[20], .. I32(k), .. new byte[20 * k], .. I32(k)]; // ResourceHash, ShaderHashes, entry count

    [Fact]
    public void CarvesEveryLayout()
    {
        var vs = Code("float4 main() : SV_Position { return 0; }", "vs_5_0", 0, 0);
        var ps = Code("Texture2D t; SamplerState s; float4 main(float4 p : SV_Position) : SV_Target { return t.Sample(s, p.xy); }", "ps_5_0", 1, 2);
        var cs = Code("RWBuffer<float> b; [numthreads(8, 1, 1)] void main(uint i : SV_DispatchThreadID) { b[i] = 1; }", "cs_5_0", 0, 0);
        var noise = new byte[4096];
        new Random(1).NextBytes(noise);
        var zps = Zlib(ps);
        byte[] symbols = [.. new byte[64]];
        BinaryPrimitives.WriteUInt32BigEndian(symbols, 0xb7756362);
        BinaryPrimitives.WriteUInt64BigEndian(symbols.AsSpan(24), 64); // FCompressedBuffer with no blocks: header only
        byte[] data =
        [
            .. noise,
            // 'A' (4.25-5.4): one raw entry, one compressed
            .. Anchor(2), .. I32(vs.Length), .. vs, .. I32(vs.Length), 0, .. I32(zps.Length), .. zps, .. I32(ps.Length), 3,
            .. noise,
            // 'B' (5.5+): header buffer, code buffer, symbols (5.6+)
            .. Anchor(1), .. BitConverter.GetBytes(12L), .. I32(cs.Length), .. I32(cs.Length - 14), 5, 0, 0, 0, .. BitConverter.GetBytes((long)cs.Length), .. cs, .. symbols,
            .. noise,
            // 'Z' (4.2x): a zlib stream with its length
            .. I32(zps.Length), .. zps,
            .. noise,
        ];
        var found = InlineShaders.Carve(data, out var undecoded);
        Assert.Equal(0, undecoded);
        Assert.Equal(["A", "A", "B", "Z"], found.Select(e => e.Format.ToString()));
        Assert.Equal([vs, ps, cs, ps], found.Select(e => e.Code));
        Assert.Equal(found[0].Map, found[1].Map);
        Assert.NotEqual(found[1].Map, found[2].Map);
        Assert.Equal(0, found[3].Map);
        foreach (var e in found) Assert.Equal(e.Code, InlineShaders.Decode(data, e.Offset, e.Format));
        Assert.Equal(new ResourceCounts(1, 2, 0, 1), ShaderContainer.UeCounts(found[1].Code));
        Assert.Empty(InlineShaders.Carve(noise, out _));
    }

    static Game? Installed(string name)
    {
        try { return new SteamSource().Discover().Concat(new XboxSource().Discover()).FirstOrDefault(g => g.Name.Contains(name)); }
        catch (Exception) { return null; }
    }

    /// <summary>Installed games: engine, API and platform as the game really runs (Windrose's AES key comes from the
    /// app's store, copied into a temp data dir); every shader reads back byte-exact and parses to its indexed stage.</summary>
    [Trait("Needs", "Game")]
    [Theory]
    [InlineData("Windrose Demo", "5.6", "D3D12", "PCD3D_SM6")]            // UE 5.6 FShaderCodeResource + symbols, Oodle
    [InlineData("Automation - The Car Company", "4.27", "D3D12", "PCD3D_SM5")] // FShaderMapResourceCode entries, LZ4
    [InlineData("Life is Strange Remastered", "4.23", "D3D11", "PCD3D_SM5")]   // FShaderResource zlib streams
    public void IndexesShadersInsidePackages(string name, string version, string api, string platform)
    {
        if (Installed(name) is not { } game) return;
        Ff7.Codecs();
        var data = Ff7.TempDir("inline-" + version);
        var key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller", "games", game.Id.Replace(':', '_'), "aes.key");
        var gameDir = Path.Combine(data, "games", game.Id.Replace(':', '_'));
        Directory.CreateDirectory(gameDir);
        if (File.Exists(key)) File.Copy(key, Path.Combine(gameDir, "aes.key"));
        var reader = new UnrealReader(data);
        var e = reader.Detect(game)!;
        Assert.Equal((version, api, null), (e.Version, e.GraphicsApi, e.Unsupported));
        if (api == "D3D11") Assert.Equal(new PlanCheck(Readiness.Ready, "compiles every DirectX 11 shader"), new Planner().Check(game, e, null, Ff7.Nvidia));

        var sw = Stopwatch.StartNew();
        var index = reader.Index(game, e, new Progress<string>(output.WriteLine), CancellationToken.None);
        output.WriteLine($"{index.Shaders.Count} shaders, {index.Maps.Count} maps in {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal([platform], index.Platforms);
        Assert.Contains(index.Maps, m => m.Library == "Global");
        Assert.All(index.Maps.SelectMany(m => m.Shaders), h => Assert.True(index.Shaders.ContainsKey(h), h));
        Assert.True(index.Shaders.Values.Count(s => s.Stage == Stage.Vertex) > 1000 && index.Shaders.Values.Count(s => s.Stage == Stage.Pixel) > 10000);
        Assert.True(index.Shaders.Values.Count(s => s.Counts == new ResourceCounts(0, 0, 0, 0)) < index.Shaders.Count / 50); // the trailer is there
        Assert.Equal(index.ContentHash, reader.Index(game, e, null, CancellationToken.None).ContentHash);

        var got = 0;
        reader.ReadShaders(game, e, index.Shaders.Keys.ToHashSet(), (h, b) =>
        {
            got++;
            Assert.Equal(h, Convert.ToHexStringLower(SHA1.HashData(b)));
            Assert.Equal(index.Shaders[h].Stage, ShaderContainer.Parse(b, h, index.Shaders[h].Counts)!.Stage);
        }, CancellationToken.None);
        Assert.Equal(index.Shaders.Count, got);
    }

    /// <summary>A library game (FF7 Rebirth, bShareMaterialShaderCode=True): its material packages carry shader map
    /// hashes, never code, so the carver finds nothing there (no false positives).</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void LibraryGameMaterialsCarryNoCode()
    {
        if (!Ff7.HasInstall) return;
        Ff7.Codecs();
        using var provider = new CUE4Parse.FileProvider.DefaultFileProvider(Path.Combine(Ff7.Install, @"End\Content\Paks"), SearchOption.TopDirectoryOnly,
            new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_FinalFantasy7Rebirth), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();
        provider.Mount();
        var materials = provider.Files.Values.Where(f => f.Extension == "uasset" && Path.GetFileName(f.Path).StartsWith("M_")).OrderBy(f => f.Path).Take(300).ToList();
        Assert.Equal(300, materials.Count);
        Assert.All(materials, f => Assert.Empty(InlineShaders.Carve(f.Read(), out _)));
    }

    /// <summary>Xbox app games: Windows won't open their exes (encrypted at rest), so the engine comes from the containers
    /// and an encrypted one's key isn't scanned for.</summary>
    [Trait("Needs", "Game")]
    [Theory]
    [InlineData("Atomic Heart", "4.27")]  // .pak v11
    [InlineData("High on Life", "4.27")]  // .utoc v3, a UE4 global container
    [InlineData("Hellblade 2", "5.1")]    // .utoc v5
    [InlineData("Solar Ash", "4.25")]     // .pak v9, encrypted
    public void XboxGamesDetectWithoutTheirExe(string name, string version)
    {
        if (new XboxSource().Discover().FirstOrDefault(g => g.Name.Contains(name)) is not { } game) return;
        Assert.Throws<UnauthorizedAccessException>(() => File.OpenRead(game.ExePath).Dispose());
        var e = new UnrealReader(Ff7.TempDir("xbox-" + version)).Detect(game, out var notes)!;
        output.WriteLine($"{game.Name}: {e}; {notes}");
        Assert.Equal(version, e.Version);
        if (e.Encrypted) Assert.Contains("exe can't be read", notes);
    }

    /// <summary>A 4.2x 'Z' entry is bounded like the others (64 MiB, 64x its stored size): a stream that inflates past that
    /// isn't decoded, whatever it holds.</summary>
    [Fact]
    public void AZlibEntryThatInflatesPastTheBoundIsNotDecoded()
    {
        var vs = Code("float4 main() : SV_Position { return 0; }", "vs_5_0", 0, 0);
        var ok = Zlib(vs);
        Assert.NotNull(InlineShaders.Decode([.. I32(ok.Length), .. ok], 0, 'Z'));
        var bomb = Zlib([.. vs, .. new byte[4 << 20]]);   // ~4 KB stored, 4 MB out
        Assert.Null(InlineShaders.Decode([.. I32(bomb.Length), .. bomb], 0, 'Z'));
    }
}
