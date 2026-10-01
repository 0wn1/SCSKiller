using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using SCSKiller.Core.Carved;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>Hash-only recordings, the form the community database stores, serves and accepts (docs/plan-db.md §0, §3;
/// docs/db-contract.md "Objects"): every record the recorder writes, 'G' / 'C' / 'S' PSOs, 'R' / 'A' ray tracing state
/// objects and the 'N' NVAPI state of either, plus the root signatures they name as 'B' records, never shader bytes (a state object's DXIL libraries are
/// hashes, rehydrated from the install like shaders), and the uploader's 'L' flags (<see cref="LocalOnly"/>). Shared by the
/// client's export and the server's import and upload validation (scsk-origin links this file).</summary>
public static class HashOnly
{
    public const int MaxRecords = 200_000, MaxRootSignature = 64 << 10;
    public const long MaxRaw = 256L << 20;

    /// <summary>The tags a hash-only recording may hold: all the recorder (proxy.cpp) writes, and 'L'. 'P' / 'Y' / '1' / '2' /
    /// 'M' are plan or work-folder records, rebuilt by each client from its own index.</summary>
    public const string Tags = "BGCSRANL";

    public sealed record Counts(int Psos, int GraphicsPsos, int DistinctVs);

    /// <summary>What <see cref="Canonical"/> dropped from a full local recording. <paramref name="StateObjects"/>: ray tracing
    /// state objects that can't be replayed from hashes (a root signature they name isn't in the recording, or a record they
    /// build on is missing or dropped).</summary>
    public sealed record Dropped(int ShaderBlobs, int StateObjects, int UnusedRootSignatures, int Duplicates);

    /// <summary>The canonical form: root-signature 'B' records the PSOs and state objects name, sorted by SHA-1, then the PSO
    /// records sorted by <see cref="Rec.Key"/>, then the state objects, each after the records it builds on (collections, the
    /// base of an addition; otherwise by key), then the 'N', then the 'L' records of kept ones, each by key, no duplicates;
    /// every record checked with the client's parser. A state object
    /// must have every root signature it names as a 'B' (a client can't rebuild those: they aren't in the game's files) and
    /// every record it builds on. <paramref name="local"/>: a full recording of this machine, whose shader blobs (DXIL
    /// libraries included) and unreplayable state objects are dropped; otherwise (someone else's upload) they are errors.
    /// InvalidDataException names the first problem.</summary>
    public static List<Rec> Canonical(IEnumerable<Rec> records, bool local, out Dropped dropped)
    {
        var blobs = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        var psos = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        var states = new SortedDictionary<string, (Rec Rec, StateObject So)>(StringComparer.Ordinal);
        var nv = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        var flags = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        int shaders = 0, dups = 0, n = 0;
        foreach (var r in records)
        {
            switch (r.Tag)
            {
                case 'B':
                    if (r.Payload.Length <= 20) throw new InvalidDataException("empty 'B' record");
                    var body = r.Payload.AsSpan(20);
                    var sha = Hex(r.Payload.AsSpan(0, 20));
                    if (Hex(SHA1.HashData(body)) != sha) throw new InvalidDataException($"blob {sha} doesn't match its SHA-1");
                    if (!Dxbc.IsRootSignatureOnly(body))
                    {
                        if (!local) throw new InvalidDataException($"blob {sha} is not a root signature (shader bytes are never shared)");
                        shaders++;
                    }
                    else if (body.Length > MaxRootSignature) throw new InvalidDataException($"root signature {sha} is over {MaxRootSignature} bytes");
                    else if (!blobs.TryAdd(sha, r)) dups++;
                    break;
                case 'R' or 'A':
                    if (!states.TryAdd(r.Key, (r, ParseStateObject(r)))) dups++; // ParseStateObject: InvalidDataException when malformed
                    break;
                case 'N':
                    NvState.Parse(r);
                    if (!nv.TryAdd(r.Key, r)) dups++;
                    break;
                case 'L':
                    if (r.Payload.Length != 20) throw new InvalidDataException($"'L' record of {r.Payload.Length} bytes");
                    if (!flags.TryAdd(r.Key, r)) dups++;
                    break;
                default:
                    Check(r);
                    if (!psos.TryAdd(r.Key, r)) dups++;
                    break;
            }
            // dropped shader blobs don't count (29k of a real 86k-record session)
            if (++n - shaders > MaxRecords) throw new InvalidDataException($"more than {MaxRecords} records");
        }
        // state objects: replayable from hashes, in dependency order (a record's key hashes the keys it builds on, so there
        // are no cycles; the walk guards anyway). Depth-first post-order with an explicit stack: uploads can chain any depth.
        var ordered = new List<Rec>();
        var done = new Dictionary<string, bool>(); // key -> kept; false while on the stack
        var stack = new List<(string Key, int Next)>();
        bool Fail(string key, string why) => local ? false : throw new InvalidDataException($"state object {key}: {why}");
        bool? Enter(string key)   // null: pushed, its dependencies next
        {
            if (done.TryGetValue(key, out var kept)) return kept;
            if (!states.TryGetValue(key, out var s)) return false;
            done[key] = false;
            if (s.So.Libraries.Count == 0 && s.So.Depends.Count == 0) return Fail(key, "no DXIL library, collection or base: nothing to create");
            if (s.So.RootSignatures.FirstOrDefault(h => !blobs.ContainsKey(h)) is { } rs) return Fail(key, $"root signature {rs} isn't in the recording");
            stack.Add((key, 0));
            return null;
        }
        foreach (var root in states.Keys)
            for (var result = Enter(root); stack.Count > 0;)
            {
                var (key, next) = stack[^1];
                var s = states[key];
                if (result == false)
                {
                    stack.RemoveAt(stack.Count - 1);
                    result = Fail(key, $"the record it builds on ({s.So.Depends[next - 1]}) isn't in the recording or can't be replayed");
                }
                else if (next == s.So.Depends.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    ordered.Add(s.Rec);
                    result = done[key] = true;
                }
                else
                {
                    stack[^1] = (key, next + 1);
                    result = Enter(s.So.Depends[next]);
                }
            }
        if (psos.Count == 0 && ordered.Count == 0) throw new InvalidDataException("no PSO or state object records");

        var used = psos.Values.Select(r => Parse(r).Rs).Concat(ordered.SelectMany(r => ParseStateObject(r).RootSignatures)).ToHashSet();
        var keep = blobs.Where(b => used.Contains(b.Key)).Select(b => b.Value).ToList();
        dropped = new Dropped(shaders, states.Count - ordered.Count, blobs.Count - keep.Count, dups);
        var kept = psos.Keys.Concat(ordered.Select(r => r.Key)).ToHashSet();
        return [.. keep, .. psos.Values, .. ordered, .. nv.Values.Where(r => kept.Contains(Target(r))), .. flags.Values.Where(r => kept.Contains(Target(r)))];
    }

    /// <summary>The key of the record an 'N' or 'L' record is about.</summary>
    public static string Target(Rec r) => r.Tag == 'N' ? NvState.Parse(r).Target : Hex(r.Payload);

    /// <summary>An 'L' record (payload: the record's key) for each PSO and state object of <paramref name="records"/> that
    /// names a shader (a stage, a DXIL library) the game doesn't ship (<paramref name="shipped"/>): built at run time, or a
    /// mod's replacement. Another PC has its bytes only in a recording of its own.</summary>
    public static IEnumerable<Rec> LocalOnly(IEnumerable<Rec> records, Func<string, bool> shipped) =>
        records.Where(r => r.Tag switch
        {
            'G' or 'C' or 'S' => Parse(r).Stages.Values.Any(h => !shipped(h)),
            'R' or 'A' => ParseStateObject(r).Libraries.Any(h => !shipped(h)),
            _ => false,
        }).Select(r => new Rec('L', Convert.FromHexString(r.Key)));

    public static Counts Count(IEnumerable<Rec> records)
    {
        int all = 0, gfx = 0;
        var vs = new HashSet<string>();
        foreach (var r in records.Where(r => r.Tag is 'G' or 'C' or 'S'))
        {
            var p = Parse(r);
            all++;
            if (!p.Stages.ContainsKey((int)Stage.Compute)) gfx++;
            if (p.Stages.TryGetValue((int)Stage.Vertex, out var v)) vs.Add(v);
        }
        return new Counts(all, gfx, vs.Count);
    }

    /// <summary>Splits a canonical recording into canonical recordings (uploads, db-contract.md "Anonymous uploads") of at most
    /// <paramref name="maxRecords"/> PSO and state object records and about <paramref name="maxRaw"/> bytes each, every one
    /// valid on its own: a state object travels with every record it builds on (a shared base then goes in several), a record
    /// with its 'N' and 'L', and each with the root signatures its records name.</summary>
    public static List<List<Rec>> Chunks(IReadOnlyList<Rec> canonical, int maxRecords, long maxRaw)
    {
        var blobs = canonical.Where(r => r.Tag == 'B').ToList();
        var states = canonical.Where(r => r.Tag is 'R' or 'A').ToDictionary(r => r.Key);
        var about = canonical.Where(r => r.Tag is 'N' or 'L').ToLookup(Target);
        void Close(Rec r, Dictionary<string, Rec> into)
        {
            if (!into.TryAdd(r.Key, r)) return;
            foreach (var a in about[r.Key]) into.TryAdd(a.Key, a);
            if (r.Tag is 'R' or 'A') foreach (var d in ParseStateObject(r).Depends) Close(states[d], into);
        }
        var chunks = new List<List<Rec>>();
        var chunk = new Dictionary<string, Rec>();
        long raw = 0;
        void Flush()
        {
            if (chunk.Count > 0) chunks.Add(Canonical([.. blobs, .. chunk.Values], local: false, out _));
            chunk = [];
            raw = 0;
        }
        foreach (var r in canonical.Where(r => r.Tag is not ('B' or 'N' or 'L')))
        {
            var unit = new Dictionary<string, Rec>();
            Close(r, unit);
            var add = unit.Values.Where(u => !chunk.ContainsKey(u.Key)).ToList();
            if (chunk.Count > 0 && (chunk.Count + add.Count > maxRecords || raw + add.Sum(u => 5L + u.Payload.Length) > maxRaw))
            {
                Flush();
                add = [.. unit.Values];
            }
            foreach (var u in add) chunk[u.Key] = u;
            raw += add.Sum(u => 5L + u.Payload.Length);
        }
        Flush();
        return chunks;
    }

    /// <summary>The db bytes of <paramref name="records"/>, Brotli (quality 11): an object of the community database.</summary>
    public static byte[] Compress(IEnumerable<Rec> records)
    {
        var list = records as IList<Rec> ?? [.. records];
        using var raw = new MemoryStream((int)Math.Min(Array.MaxLength, list.Sum(r => 5L + r.Payload.Length)));   // sized once: growing it held up to 3x the bytes
        foreach (var r in list) Write(raw, r.Tag, r.Payload);
        var out_ = new byte[BrotliEncoder.GetMaxCompressedLength((int)raw.Length)];
        if (!BrotliEncoder.TryCompress(raw.GetBuffer().AsSpan(0, (int)raw.Length), out_, out var n, 11, 22))
            throw new InvalidOperationException("Brotli failed");
        return out_[..n];
    }

    /// <summary>The records of a Brotli-compressed db, at most <paramref name="max"/> bytes decompressed; a torn tail is an error.</summary>
    public static List<Rec> Decompress(ReadOnlySpan<byte> compressed, long max = MaxRaw)
    {
        using var raw = new MemoryStream();
        // BrotliDecoder, not BrotliStream: the stream reads garbage or a truncated input as a short, valid end
        using var d = new BrotliDecoder();
        var buf = new byte[1 << 16];
        for (var status = OperationStatus.DestinationTooSmall; status != OperationStatus.Done;)
        {
            status = d.Decompress(compressed, buf, out var used, out var wrote);
            if (status is OperationStatus.InvalidData or OperationStatus.NeedMoreData) throw new InvalidDataException("not a complete Brotli stream");
            if (raw.Length + wrote > max) throw new InvalidDataException($"over {max} bytes decompressed");
            compressed = compressed[used..];
            raw.Write(buf, 0, wrote);
        }
        if (!compressed.IsEmpty) throw new InvalidDataException("bytes after the Brotli stream");
        return Records(raw.GetBuffer().AsSpan(0, (int)raw.Length));
    }

    /// <summary>The records of an uncompressed db, read to its last byte; a torn tail is an error (unlike <see cref="PsoDb.Read(Stream)"/>,
    /// which stops there like the proxy). The framing is checked before anything is allocated from a length field.</summary>
    public static List<Rec> Records(ReadOnlySpan<byte> raw)
    {
        var recs = new List<Rec>();
        for (var at = 0L; at < raw.Length;)
        {
            if (at + 5 > raw.Length) throw new InvalidDataException("truncated record");
            var len = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw[(int)(at + 1)..]);
            if (at + 5 + len > raw.Length) throw new InvalidDataException("truncated record");
            recs.Add(new Rec((char)raw[(int)at], raw.Slice((int)at + 5, (int)len).ToArray()));
            at += 5 + len;
        }
        return recs;
    }
}
