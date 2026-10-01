using System.Buffers.Binary;

namespace SCSKiller.Core.Carved;

/// <summary>DXBC/DXIL container checks for carving raw game files (port of tools/engine_survey.py parse_container): header,
/// chunk table inside the container, FourCC chunk names. Layout: "DXBC", 16-byte checksum, u32 1, u32 size, u32 chunk count,
/// u32 chunk offsets; each chunk = FourCC, u32 length, data.</summary>
public static class Dxbc
{
    public const int MaxSize = 64 << 20;

    /// <summary>Container size from a 32-byte header, or 0 when the header can't start a container.</summary>
    public static int HeaderSize(ReadOnlySpan<byte> h)
    {
        if (h.Length < 32 || !h.StartsWith("DXBC"u8)) return 0;
        uint ver = U(h, 20), size = U(h, 24), n = U(h, 28);
        return ver == 1 && size > 32 && size <= MaxSize && n is >= 1 and <= 64 && 32 + 4 * n <= size ? (int)size : 0;
    }

    /// <summary>The whole container (exactly its size) is well formed.</summary>
    public static bool Valid(ReadOnlySpan<byte> c)
    {
        if (HeaderSize(c) != c.Length) return false;
        for (var k = 0; k < (int)U(c, 28); k++)
        {
            long co = U(c, 32 + 4 * k);
            if (co + 8 > c.Length) return false;
            foreach (var b in c.Slice((int)co, 4)) if (b is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9')) return false;
            if (co + 8 + U(c, (int)co + 4) > c.Length) return false;
        }
        return true;
    }

    /// <summary>Every valid container in the bytes, in order; the bytes inside one aren't searched.</summary>
    public static IEnumerable<(int Offset, byte[] Container)> Containers(byte[] b)
    {
        for (var i = 0; b.AsSpan(i).IndexOf("DXBC"u8) is var k and >= 0;)
        {
            var at = i + k;
            var size = HeaderSize(b.AsSpan(at));
            if (size > 0 && at + size <= b.Length && Valid(b.AsSpan(at, size)))
            {
                yield return (at, b[at..(at + size)]);
                i = at + size;
            }
            else i = at + 1;
        }
    }

    /// <summary>Data of the first chunk named <paramref name="fourcc"/> (valid containers only), empty if none.</summary>
    public static ReadOnlySpan<byte> Part(ReadOnlySpan<byte> c, ReadOnlySpan<byte> fourcc)
    {
        for (var k = 0; k < (int)U(c, 28); k++)
        {
            var co = (int)U(c, 32 + 4 * k);
            if (c.Slice(co, 4).SequenceEqual(fourcc)) return c.Slice(co + 8, (int)U(c, co + 4));
        }
        return default;
    }

    /// <summary>Program type from SHEX/SHDR or the DXIL program header (0 ps, 1 vs, 2 gs, 3 hs, 4 ds, 5 cs, 6 lib, 13 ms,
    /// 14 as); -1 = no program (e.g. a root-signature-only container).</summary>
    public static int Kind(ReadOnlySpan<byte> c)
    {
        foreach (var name in Programs)
            if (Part(c, name) is { Length: >= 4 } p) return (int)(U(p, 0) >> 16);
        return -1;
    }

    static readonly byte[][] Programs = ["DXIL"u8.ToArray(), "SHEX"u8.ToArray(), "SHDR"u8.ToArray()];

    /// <summary>A DXIL shader's required wave lane range ([WaveSize], SM 6.6+) from PSV0's runtime info (after the 16-byte
    /// stage union: MinimumExpectedWaveLaneCount, MaximumExpectedWaveLaneCount); null = no requirement.</summary>
    public static (uint Min, uint Max)? WaveLanes(ReadOnlySpan<byte> c)
    {
        var psv = Part(c, "PSV0"u8);
        if (psv.Length < 28 || U(psv, 0) < 24) return null;
        var (min, max) = (U(psv, 20), U(psv, 24));
        return min == 0 && max == uint.MaxValue ? null : (min, max);
    }

    /// <summary>A DXIL shader that traces rays inline (RayQuery): SFI0's D3D_SHADER_REQUIRES_RAYTRACING_TIER_1_1.</summary>
    public static bool InlineRayTracing(ReadOnlySpan<byte> c) =>
        Valid(c) && Part(c, "SFI0"u8) is { Length: >= 8 } f && (BinaryPrimitives.ReadUInt64LittleEndian(f) & 0x100000) != 0;

    /// <summary>A serialized root signature (D3D12SerializeRootSignature output): a container of RTS0 chunks only, no
    /// program. Generated binding metadata, never shader code: the only blobs packs and hash-only recordings keep.</summary>
    public static bool IsRootSignatureOnly(ReadOnlySpan<byte> blob)
    {
        if (!Valid(blob)) return false;
        var n = (int)U(blob, 28);
        for (var i = 0; i < n; i++)
            if (!blob.Slice((int)U(blob, 32 + 4 * i), 4).SequenceEqual("RTS0"u8)) return false;
        return n > 0;
    }

    /// <summary>Stages a graphics pipeline is built from (not compute, not DXR libraries).</summary>
    public static bool IsGraphics(int kind) => kind is >= 0 and <= 4 or 13 or 14;

    static uint U(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
}
