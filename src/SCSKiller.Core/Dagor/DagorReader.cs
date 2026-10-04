using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;
using SCSKiller.Core.Unreal;
using ZstdSharp;

namespace SCSKiller.Core.Dagor;

/// <summary>Gaijin's Dagor Engine (War Thunder): the DirectX 11 shader dump compiledShaders\game.ps50.shdump.bin
/// (game.compatibility.ps50 in the game's compatibility mode), dump version 11.3 only (DagorEngine's shader_layout.h).
/// A 64-byte header ("VSPS", "dump", version), then the zstd-compressed main layout; in it every shader is a zstd frame
/// of the dump's own dictionary holding plain DXBC: a pixel or compute shader, or a vertex shader followed by the hull,
/// domain and geometry shaders it is drawn with. Maps: one per dump entry, so those stages pair as the game draws them.
/// The DirectX 12 dump (gameDX12.ps50) isn't read: the engine builds its root signatures at run time from per-shader
/// headers. GraphicsApi: config.blk's video/driver ("auto" or none: "D3D11 or D3D12"). EngineInfo: Family "Dagor",
/// Version "11.3".</summary>
public sealed class DagorReader : IEngineReader
{
    public const string Family = "Dagor", Version = "11.3", Platform = "PCD3D_SM5";

    const int HeaderSize = 64, MaxShader = 256 << 20;
    // ScriptedShadersBinDump fields of version 11.3: vprCount, fshCount, uncompressed_shader_sizes, shaders, dictionary
    const int VprCount = 16, FshCount = 20, Sizes = 172, Codes = 188, Dictionary = 196;

    public EngineInfo? Detect(Game game)
    {
        if (!Directory.Exists(Path.Combine(game.InstallDir, "compiledShaders"))) return null;
        var path = DumpPath(game);
        Span<byte> h = stackalloc byte[12];
        try
        {
            if (!File.Exists(path)) return null;
            using var f = Open(path);
            if (f.ReadAtLeast(h, 12, throwOnEndOfStream: false) < 12 || !h.StartsWith("VSPSdump"u8)) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var version = Encoding.ASCII.GetString(h[8..]);
        // the driver caches per exe name: a store that found the launcher would warm the wrong one
        var exe = GaijinSource.Exe(game.InstallDir);
        return new EngineInfo(Family, version, null, Api(game), false,
            version != Version ? $"shader dump version {version}: SCSKiller reads version {Version}"
            : exe != null && !string.Equals(exe, Path.GetFullPath(game.ExePath), StringComparison.OrdinalIgnoreCase)
                ? $"listed with {Path.GetFileName(game.ExePath)}, but the game runs {Path.GetRelativePath(game.InstallDir, exe)}" : null);
    }

    public string DetectStamp(Game game, EngineInfo? engine)
    {
        var f = new FileInfo(Path.Combine(game.InstallDir, "config.blk"));
        return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-";
    }

    /// <summary>The dump config.blk's compatibility mode selects: switching it changes which shaders the game loads.</summary>
    public string IndexStamp(Game game)
    {
        if (!Directory.Exists(Path.Combine(game.InstallDir, "compiledShaders"))) return "";
        var f = new FileInfo(DumpPath(game));
        return f.Exists ? $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}" : "";
    }

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var path = DumpPath(game);
        var rel = Path.GetRelativePath(game.InstallDir, path);
        var shaders = new Dictionary<string, ShaderInfo>();
        var maps = new Dictionary<string, ShaderMap>();
        int entries = 0, bad = 0;
        foreach (var entry in Entries(Read(path), ct))
        {
            entries++;
            var shas = new List<string>();
            foreach (var c in entry)
            {
                var sha = Convert.ToHexStringLower(SHA1.HashData(c));
                try
                {
                    if (!shaders.ContainsKey(sha) && ShaderContainer.Parse(c, sha, new(0, 0, 0, 0)) is { } info)
                        shaders[sha] = info with { Counts = CarvedReader.Counts(info.Bindings) };
                }
                catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException) { bad++; } // valid container, odd program: not usable
                if (shaders.ContainsKey(sha)) shas.Add(sha);
            }
            if (shas.Count == 0) continue;
            var h = CarvedReader.Sha1Hex(string.Join(',', shas));
            maps.TryAdd(h, new ShaderMap(h, rel, Platform, shas));
        }
        var file = new FileInfo(path);
        log?.Report($"{rel}: {entries} entries, {shaders.Count} shaders ("
            + string.Join(", ", shaders.Values.GroupBy(s => s.Stage).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"))
            + $"){(bad > 0 ? $", {bad} unparseable" : "")} -> {maps.Count} maps ({sw.Elapsed.TotalSeconds:F1}s)");
        var content = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{rel}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")));
        return new ShaderIndex(content, [Platform], shaders, maps.Values.ToList());
    }

    /// <summary>Decompresses the dump again (about a second) and serves each requested shader once.</summary>
    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct)
    {
        var left = new HashSet<string>(sha1s);
        foreach (var entry in Entries(Read(DumpPath(game)), ct))
            foreach (var c in entry)
                if (Convert.ToHexStringLower(SHA1.HashData(c)) is var sha && left.Remove(sha))
                {
                    sink(sha, c);
                    if (left.Count == 0) return;
                }
    }

    /// <summary>Every entry of a version 11.3 dump, vertex entries first, as the containers it holds.</summary>
    internal static IEnumerable<List<byte[]>> Entries(byte[] dump, CancellationToken ct)
    {
        if (dump.Length < HeaderSize + 12 || !dump.AsSpan().StartsWith("VSPSdump11.3"u8)) throw new InvalidDataException("not a version 11.3 shader dump");
        var (at, n) = List(dump, HeaderSize + 4, 1);
        var main = new byte[U(dump, HeaderSize)];
        using (var z = new Decompressor())
            if (Unwrap(z, dump.AsSpan(at, n), main) != main.Length) throw new InvalidDataException("the dump's body doesn't decode to its size");
        var count = (long)U(main, VprCount) + U(main, FshCount);
        var (sizes, ns) = List(main, Sizes, 4);
        var (codes, nc) = List(main, Codes, 8);
        var (dict, nd) = List(main, Dictionary, 1);
        if (ns != count || nc != count) throw new InvalidDataException($"{count} shaders, but {ns} sizes and {nc} codes");
        using var zd = new Decompressor();
        zd.LoadDictionary(main.AsSpan(dict, nd));
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (code, len) = List(main, codes + 8 * i, 1);
            var size = U(main, sizes + 4 * i);
            if (size > MaxShader) throw new InvalidDataException($"shader {i}: {size} bytes");
            var bytes = new byte[(size + 3) & ~3u];   // the engine rounds each shader up to 4 bytes
            var got = Unwrap(zd, main.AsSpan(code, len), bytes);
            yield return Dxbc.Containers(bytes[..got]).Select(x => x.Container).ToList();
        }
    }

    static int Unwrap(Decompressor z, ReadOnlySpan<byte> src, Span<byte> dest)
    {
        try { return z.Unwrap(src, dest); }
        catch (ZstdException e) { throw new InvalidDataException("the dump doesn't decode: " + e.Message, e); }
    }

    /// <summary>A bindump list field: an i32 offset from the field itself and a u32 count; its elements' start, checked to fit.</summary>
    static (int At, int Count) List(byte[] b, int field, int elementSize)
    {
        if (field < 0 || field + 8 > b.Length) throw new InvalidDataException($"list field at {field} is outside the dump");
        long at = field + (long)BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(field)), n = U(b, field + 4);
        if (n > 0 && (at < 0 || at + n * elementSize > b.Length)) throw new InvalidDataException($"list at {field} is outside the dump");
        return ((int)at, (int)n);
    }

    static uint U(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    static string DumpPath(Game game) =>
        Path.Combine(game.InstallDir, "compiledShaders", Video(game, "compatibilityMode") is "yes" or "true" or "1" ? "game.compatibility.ps50.shdump.bin" : "game.ps50.shdump.bin");

    static string Api(Game game) => Video(game, "driver") switch
    {
        "dx11" => "D3D11",
        "dx12" => "D3D12",
        "vulkan" => "Vulkan",
        _ => UnrealRhi.Ambiguous,   // "auto": DirectX 12 when the game's settings prefer it for this GPU
    };

    /// <summary>A parameter of config.blk's top-level video block, lower case; null when it isn't there or the file can't be read.</summary>
    internal static string? Video(Game game, string name)
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(game.InstallDir, "config.blk")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var block = VideoBlock(text);
        if (block == null) return null;
        var m = Regex.Match(block, $@"(?:^|[\s;]){Regex.Escape(name)}:[a-z0-9]+\s*=\s*""?([^""\s;}}]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>The top-level text of the first "video{...}" block, its nested blocks left out.</summary>
    static string? VideoBlock(string text)
    {
        var m = Regex.Match(text, @"(?:^|[\s;])video\s*\{");
        if (!m.Success) return null;
        var sb = new StringBuilder();
        var depth = 1;
        for (var i = m.Index + m.Length; i < text.Length && depth > 0; i++)
        {
            var c = text[i];
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (depth == 1) sb.Append(c);
            if (c is '{' or '}') sb.Append(' ');
        }
        return sb.ToString();
    }

    static byte[] Read(string path)
    {
        using var f = Open(path);
        var b = new byte[f.Length];
        f.ReadExactly(b);
        return b;
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
