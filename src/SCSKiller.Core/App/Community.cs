using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>A manifest entry: a game build's merged hash-only recording (docs/db-contract.md). <see cref="Object"/> is the
/// lowercase hex SHA-256 of the bytes served at /v1/o/&lt;Object&gt;, <see cref="Size"/> their length.</summary>
public sealed record CommunityEntry(string ContentHash, string Object, long Size, int Psos, int Uploaders);

/// <summary>What was downloaded for a game (games\&lt;id&gt;\community.json, next to community.db).</summary>
public sealed record CommunityDownload(string Object, string ContentHash, int Psos, DateTimeOffset DownloadedAt);

/// <summary>The manifest (docs/db-contract.md): fixed 80-byte little-endian records, replayed in file order. Record 0 is the
/// 'M' header (format 1, the epoch); 'E' entry: content hash -> object (the last one wins); 'A' alias: SHA-1 of the store
/// build key -> content hash; 'T' tombstone: withdraws an entry until a later 'E'. Unknown kinds are skipped.</summary>
public sealed class CommunityManifest
{
    public const int RecordSize = 80;
    readonly Dictionary<string, CommunityEntry> entries = [];
    readonly Dictionary<string, string> aliases = [];
    public uint Epoch { get; }

    CommunityManifest(uint epoch) => Epoch = epoch;

    /// <summary>InvalidDataException when it isn't a format-1 manifest of whole records.</summary>
    public static CommunityManifest Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < RecordSize || file.Length % RecordSize != 0 || file[0] != 'M' || file[1] != 1)
            throw new InvalidDataException("not a format-1 community manifest");
        var m = new CommunityManifest(BinaryPrimitives.ReadUInt32LittleEndian(file[4..]));
        for (var at = RecordSize; at < file.Length; at += RecordSize)
        {
            var r = file.Slice(at, RecordSize);
            var hash = Convert.ToHexStringLower(r.Slice(4, 20));
            switch ((char)r[0])
            {
                case 'E':
                    m.entries[hash] = new(hash, Convert.ToHexStringLower(r.Slice(24, 32)), BinaryPrimitives.ReadUInt32LittleEndian(r[56..]),
                        (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(r[60..])), BinaryPrimitives.ReadUInt16LittleEndian(r[2..]));
                    break;
                case 'A': m.aliases[hash] = Convert.ToHexStringLower(r.Slice(24, 20)); break;
                case 'T': m.entries.Remove(hash); break;
            }
        }
        return m;
    }

    /// <summary>The alias key of a store build: lowercase hex SHA-1 of the UTF-8 "&lt;Game.Id&gt;@&lt;Game.Version&gt;"; null
    /// when the store gives no build id (such a game is found by its content hash once indexed).</summary>
    public static string? AliasKey(Game g) => g.Version is { Length: > 0 } v ? Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{g.Id}@{v}"))) : null;

    /// <summary>By content hash when given (a fresh index), else by the game's store build.</summary>
    public CommunityEntry? Find(Game g, string? contentHash) =>
        contentHash != null ? entries.GetValueOrDefault(contentHash)
        : AliasKey(g) is { } k && aliases.TryGetValue(k, out var h) ? entries.GetValueOrDefault(h) : null;
}

/// <summary>The read side of the community shader hash database (docs/plan-db.md §6, docs/db-contract.md): the manifest,
/// kept under %LOCALAPPDATA%\SCSKiller\community and brought up to date at most every <see cref="ManifestMaxAge"/> (a Range
/// request for the records after the copy's end), and recordings downloaded with an access token that carries "db",
/// checked by SHA-256 and by content, then kept for good (plan-db.md §4 "Churn"). Every failure (offline, 401/403, 429,
/// the server down) lands in <see cref="Problem"/>; nothing throws but cancellation, so a scan or a compile always goes on
/// locally.</summary>
public sealed class Community
{
    public static readonly TimeSpan ManifestMaxAge = TimeSpan.FromHours(4);
    public const int MaxRaw = 256 << 20;   // decompressed (db-contract.md)

    readonly string dir;
    readonly RouteFailover routes;
    readonly HttpClient http;
    readonly TimeProvider clock;
    readonly Func<bool, CancellationToken, Task<string?>> dbToken;
    readonly SemaphoreSlim manifestGate = new(1, 1);
    CommunityManifest? manifest;
    DateTimeOffset backoffUntil;

    /// <param name="dbToken">(fresh, ct): an access token that carries "db", else null (signed out, or not a supporter);
    /// fresh: a new one, after a 401 (<see cref="Account.GetDbTokenAsync"/>)</param>
    public Community(string dataDir, Func<bool, CancellationToken, Task<string?>> dbToken, RouteFailover? routes = null, TimeProvider? clock = null)
    {
        dir = Path.Combine(dataDir, "community");
        this.routes = routes ?? RouteFailover.Default;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        this.clock = clock ?? TimeProvider.System;
        this.dbToken = dbToken;
    }

    /// <summary>The last failure, in plain words; null after a success.</summary>
    public string? Problem { get; private set; }

    /// <summary>The manifest: the local copy while it was checked less than <see cref="ManifestMaxAge"/> ago (its file's write
    /// time), else brought up to date first (3 s per route). A failed check keeps the copy (null when there's none).</summary>
    public async Task<CommunityManifest?> ManifestAsync(CancellationToken ct = default)
    {
        await manifestGate.WaitAsync(ct);
        try
        {
            var file = Path.Combine(dir, "manifest.bin");
            if (manifest == null && File.Exists(file))
                try { manifest = CommunityManifest.Parse(File.ReadAllBytes(file)); }
                catch (InvalidDataException) { }   // damaged: fetched whole
            if (manifest != null && clock.GetUtcNow() - File.GetLastWriteTimeUtc(file) < ManifestMaxAge) return manifest;
            var have = manifest != null && File.Exists(file) ? File.ReadAllBytes(file) : null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(routes.Primary, $"v1/manifest/{manifest?.Epoch ?? 0}"));
                request.Options.Set(RouteFailover.AttemptTimeout, TimeSpan.FromSeconds(3));
                if (have != null) request.Headers.Range = new(have.Length, null);
                using var r = await http.SendAsync(request, ct);
                var next = r.StatusCode switch
                {
                    HttpStatusCode.RequestedRangeNotSatisfiable when have != null => have,   // nothing new
                    HttpStatusCode.PartialContent when have != null && r.Content.Headers.ContentRange?.From == have.Length =>
                        [.. have, .. await r.Content.ReadAsByteArrayAsync(ct)],
                    HttpStatusCode.OK => await r.Content.ReadAsByteArrayAsync(ct),   // the whole file: a first fetch or a compaction
                    _ => null,
                };
                if (next == null) Problem = Refused(r);
                else
                {
                    var parsed = CommunityManifest.Parse(next);   // before saving: a body this version can't read never replaces a good copy
                    if (next != have) AppStore.WriteAtomic(file, next);
                    File.SetLastWriteTimeUtc(file, clock.GetUtcNow().UtcDateTime);   // checked now
                    (manifest, Problem) = (parsed, null);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { Problem = Plain(e); }
            return manifest;
        }
        finally { manifestGate.Release(); }
    }

    /// <summary>Downloads <paramref name="e"/> into <paramref name="gameDir"/>\community.db (+ community.json) when an access
    /// token with "db" is at hand. Null when not entitled, while backing off after a 429/503, or on any failure
    /// (<see cref="Problem"/>); what was downloaded before stays.</summary>
    public async Task<CommunityDownload?> DownloadAsync(CommunityEntry e, string gameDir, CancellationToken ct = default)
    {
        if (clock.GetUtcNow() < backoffUntil) return null;
        try
        {
            if (e.Size > MaxRaw || await dbToken(false, ct) is not { } token) return null;
            var r = await GetObject(e, token, ct);
            if (r.StatusCode == HttpStatusCode.Unauthorized && await dbToken(true, ct) is { } fresh)   // expired or revoked: once with a new one
            {
                r.Dispose();
                r = await GetObject(e, fresh, ct);
            }
            using (r)
            {
                if (!r.IsSuccessStatusCode)
                {
                    if (r.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)   // quota; the fallback route's budget
                        backoffUntil = clock.GetUtcNow() + (r.Headers.RetryAfter?.Delta ?? TimeSpan.FromHours(1));
                    Problem = Refused(r);
                    return null;
                }
                var packed = await Bounded(await r.Content.ReadAsStreamAsync(ct), (int)e.Size, ct);
                if (packed == null || Convert.ToHexStringLower(SHA256.HashData(packed)) != e.Object)
                {
                    Problem = "A community recording didn't match its checksum and was ignored. It's downloaded again at the next scan.";
                    return null;
                }
                var raw = await Bounded(new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress), MaxRaw, ct);
                if (raw == null || Check(raw) is not { } psos)
                {
                    Problem = "A community recording this version can't read was ignored. An update of SCSKiller may fix it.";
                    return null;
                }
                AppStore.WriteAtomic(Path.Combine(gameDir, "community.db"), raw);
                var got = new CommunityDownload(e.Object, e.ContentHash, psos, clock.GetUtcNow());
                Write(Path.Combine(gameDir, "community.json"), got);
                Problem = null;
                return got;
            }
        }
        catch (Exception x) when (!ct.IsCancellationRequested)
        {
            if (x is HttpRequestException or OperationCanceledException) backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);   // offline: not once per game
            Problem = Plain(x);
            return null;
        }
    }

    Task<HttpResponseMessage> GetObject(CommunityEntry e, string token, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(routes.Primary, "v1/o/" + e.Object));
        request.Headers.Authorization = new("Bearer", token);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>What <see cref="DownloadAsync"/> left in a game's folder; null when nothing (or it's unreadable).</summary>
    public static CommunityDownload? Downloaded(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "community.db")) ? Read<CommunityDownload>(Path.Combine(gameDir, "community.json")) : null;

    /// <summary>The PSO count of a hash-only recording that is safe to plan from, else null: only <see cref="HashOnly.Tags"/>
    /// (an 'N' or 'L' about a record it has), read to the last byte; every PSO and state object parses, and a state object has its root
    /// signatures and the records it builds on; every 'B' is a root signature (a DXBC container of RTS0 parts only) under
    /// its own SHA-1. So an entry can't bring shader code: shaders and DXIL libraries only ever come from the local install (Rehydrate).
    /// The same check the server applies to uploads (<see cref="HashOnly.Canonical"/>).</summary>
    public static int? Check(byte[] raw)
    {
        try { return HashOnly.Count(HashOnly.Canonical(HashOnly.Records(raw), local: false, out _)).Psos; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary><paramref name="output"/> = <paramref name="first"/>'s records (none when it doesn't exist), then
    /// <paramref name="second"/>'s that it lacks (by Rec.Key), every 'B' first, as a proxy db.</summary>
    public static void Union(string first, string second, string output)
    {
        // streamed: a local recording with its shader bytes can be large
        var have = File.Exists(first) ? PsoDb.Read(first).Select(r => r.Key).ToHashSet() : [];
        IEnumerable<PsoDb.Rec> Pass(bool blobs) => (File.Exists(first) ? PsoDb.Read(first) : []).Where(r => r.Tag == 'B' == blobs)
            .Concat(PsoDb.Read(second).Where(r => r.Tag == 'B' == blobs && have.Add(r.Key)));
        var tmp = output + ".tmp";
        using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
            foreach (var r in Pass(true).Concat(Pass(false))) PsoDb.Write(f, r.Tag, r.Payload);
        File.Move(tmp, output, true);
    }

    static async Task<byte[]?> Bounded(Stream s, int max, CancellationToken ct)
    {
        await using (s)
        {
            var buf = new MemoryStream();
            var chunk = new byte[81920];
            for (int n; (n = await s.ReadAsync(chunk, ct)) > 0;)
            {
                if (buf.Length + n > max) return null;
                buf.Write(chunk, 0, n);
            }
            return buf.ToArray();
        }
    }

    static string Refused(HttpResponseMessage r) => r.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "The community database didn't accept this PC's sign-in. Refresh status or sign in again in Settings.",
        HttpStatusCode.Forbidden => "Your Patreon membership doesn't include the community database right now.",
        HttpStatusCode.TooManyRequests => "The community database's daily limit is reached. Downloads resume later by themselves.",
        HttpStatusCode.ServiceUnavailable => "The community database is busy right now. Downloads resume later by themselves.",
        >= HttpStatusCode.InternalServerError => $"The community database had a problem (error {(int)r.StatusCode}). It retries at the next scan.",
        _ => $"The community database refused the request (error {(int)r.StatusCode}).",
    };

    static string Plain(Exception e) => e switch
    {
        HttpRequestException => "Can't reach the community database right now. Compiling works as usual from your own files.",
        OperationCanceledException => "The community database didn't answer in time. It retries at the next scan.",
        AccountException => e.Message,
        InvalidDataException => "The community database sent a list this version can't read. An update of SCSKiller may fix it.",
        _ => "The community database gave an answer this version doesn't understand. An update of SCSKiller may fix it.",
    };

    internal static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    internal static void Write<T>(string path, T value) => AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value));
}
