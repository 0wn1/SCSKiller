using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>The upload's X-SCSK-Upload header (docs/db-contract.md "Anonymous uploads"; the server's copy is UploadMeta in
/// server/Shared), sent as base64url of snake_case JSON.</summary>
public sealed record UploadMeta(string StoreBuildKey, string ContentHash, string? Engine = null, string? Vendor = null, string? AppVersion = null,
    UploadDll[]? Middleware = null);

public sealed record UploadDll(string Name, string Sha1);

/// <summary>What sharing did with a game's recording (games\&lt;id&gt;\shared.json). <see cref="Stamp"/>: the recording and
/// content hash last looked at to the end (uploaded, or found to have nothing to share); <see cref="Sent"/>: the uploads of
/// it the server has (SHA-256 of an upload's records, "|", its content hash), kept across a pass cut short; the rest is the
/// last upload, if any (<see cref="Psos"/> / <see cref="NewPsos"/>: summed over its uploads).</summary>
public sealed record SharedRecording(string Stamp, DateTimeOffset? At = null, string? UploadId = null, int Psos = 0, int NewPsos = 0, string[]? Sent = null);

/// <summary>The write side of the community database (docs/plan-db.md §3 "Upload flow", docs/db-contract.md): a game's own
/// recording (recording.db, never the merge with the community's), stripped to its hash-only form, posted with an anonymous
/// upload device's token. That device is registered once (POST /v1/devices) and kept DPAPI-protected in upload.dat, apart
/// from the Patreon sign-in (auth.dat), which this class never sees. Works signed out. Quiet: failures
/// land in <see cref="Problem"/> and back off; nothing throws but cancellation.</summary>
public sealed class Sharing
{
    public const int MaxBody = 4 << 20;   // = the edge's cap (db-contract.md)
    // One upload: real sessions run 0.8 to 1 KB a record hash-only, so ~20 MB and well under the origin's default 32 MB
    public const int ChunkRecords = 20_000;
    public const long ChunkRaw = 24 << 20;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    readonly string file;
    readonly Func<bool> enabled;
    readonly RouteFailover routes;
    readonly HttpClient http;
    readonly TimeProvider clock;
    DateTimeOffset backoffUntil;

    /// <param name="enabled">Settings.ShareRecordings, read at each call: off means no work and no request at all</param>
    public Sharing(string dataDir, Func<bool> enabled, RouteFailover? routes = null, TimeProvider? clock = null)
    {
        file = Path.Combine(dataDir, "upload.dat");
        this.enabled = enabled;
        this.routes = routes ?? RouteFailover.Default;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The last failure, in plain words; null after a success.</summary>
    public string? Problem { get; private set; }

    /// <summary>The upload device's id (for the admin's trust-device), null before the first registration.</summary>
    public string? DeviceId => Load()?.Id;

    /// <summary>Forgets the upload device; the next upload registers a new one.</summary>
    public void Reset() => File.Delete(file);

    public static SharedRecording? Shared(string gameDir) => Community.Read<SharedRecording>(Path.Combine(gameDir, "shared.json"));

    /// <summary>Uploads <paramref name="gameDir"/>\recording.db for the build <paramref name="contentHash"/> when sharing is on
    /// and it changed since the last look: in uploads of at most <see cref="ChunkRecords"/> records (<see cref="HashOnly.Chunks"/>),
    /// each skipped when the server already has it or the downloaded community entry for that build holds all its records.
    /// <paramref name="meta"/> is built only for an actual upload (it may hash middleware DLLs). Returns the new
    /// <see cref="SharedRecording"/> after an upload, else null.</summary>
    public async Task<SharedRecording?> ShareAsync(string gameDir, string contentHash, Func<UploadMeta> meta,
        Func<IEnumerable<string>>? middlewareShaders = null, CancellationToken ct = default)
    {
        var db = new FileInfo(Path.Combine(gameDir, "recording.db"));
        if (!enabled() || !db.Exists || clock.GetUtcNow() < backoffUntil) return null;
        Problem = null;   // per game; a back-off's stays
        // "|2": re-examines stamps that marked an over-cap recording as done; the shader list: flags once it is there
        var list = new FileInfo(Path.Combine(gameDir, ShippedFile));
        var stamp = $"{db.Length}:{db.LastWriteTimeUtc.Ticks}|{contentHash}|2" + (list.Exists ? $"|{list.Length}:{list.LastWriteTimeUtc.Ticks}" : "");
        var last = Shared(gameDir) ?? new("");
        if (last.Stamp == stamp) return null;
        var shared = Path.Combine(gameDir, "shared.json");
        List<PsoDb.Rec> records;
        try { records = HashOnly.Canonical(PsoDb.Read(db.FullName), local: true, out _); }   // shader and DXIL library blobs dropped (state objects kept)
        catch (InvalidDataException e)   // e.g. no PSOs yet: not again until the recording changes
        {
            Community.Write(shared, last with { Stamp = stamp });
            Problem = $"A recording has nothing to share ({e.Message}).";
            return null;
        }
        if (Shipped(gameDir, contentHash) is { } shipped)
        {
            shipped.UnionWith(middlewareShaders?.Invoke() ?? []);
            records = HashOnly.Canonical([.. records, .. HashOnly.LocalOnly(records, shipped.Contains)], local: false, out _);
        }
        var have = Community.Downloaded(gameDir)?.ContentHash == contentHash
            ? PsoDb.Read(Path.Combine(gameDir, "community.db")).Where(r => r.Tag != 'B').Select(r => r.Key).ToHashSet() : [];
        var sent = new List<string>();
        var (uploadId, psos, fresh) = ((string?)null, 0, 0);
        foreach (var chunk in HashOnly.Chunks(records, ChunkRecords, ChunkRaw))
        {
            var id = Id(chunk, contentHash);   // the same records for another build are new there
            if (last.Sent?.Contains(id) == true || chunk.All(r => r.Tag == 'B' || have.Contains(r.Key)))
            {
                sent.Add(id);
                continue;
            }
            var body = HashOnly.Compress(chunk);
            if (body.Length > MaxBody) return Stop($"A recording is too large to share ({body.Length >> 20} MB compressed, the limit is {MaxBody >> 20} MB).");
            try
            {
                var r = await PostAsync(body, meta(), ct);
                if (r?.StatusCode == HttpStatusCode.Unauthorized)   // the device expired (90 days unused) or was revoked: once with a new one
                {
                    r.Dispose();
                    Reset();
                    r = await PostAsync(body, meta(), ct);
                }
                if (r == null) return Stop(Problem);
                using (r)
                {
                    if (r.StatusCode == HttpStatusCode.Accepted)
                    {
                        var a = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
                        uploadId = a.GetProperty("upload_id").GetString();
                        psos += a.GetProperty("records").GetInt32();
                        fresh += a.GetProperty("new_records").GetInt32();
                        sent.Add(id);
                        continue;
                    }
                    var why = $"Sharing a recording was refused (error {(int)r.StatusCode}).";
                    if (r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.Forbidden)
                    {
                        Problem = why;
                        if (r.StatusCode == HttpStatusCode.Forbidden) break;   // this device is blocked: done until the recording changes
                        sent.Add(id);   // refused for good; the other uploads still go
                        continue;
                    }
                    BackOff(r);
                    return Stop(why);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);   // offline, or an answer this version can't read
                return Stop(e is HttpRequestException or OperationCanceledException ? "Can't reach the community database to share a recording." : e.Message);
            }
        }
        var got = uploadId is null ? last with { Stamp = stamp, Sent = [.. sent] } : new SharedRecording(stamp, clock.GetUtcNow(), uploadId, psos, fresh, [.. sent]);
        Community.Write(shared, got);   // looked at: not again until the recording changes
        return uploadId is null ? null : got;

        // Cut short: not stamped, so the next pass tries again, skipping what the server already has
        SharedRecording? Stop(string? why)
        {
            if (sent.Count > 0) Community.Write(shared, last with { Sent = [.. (last.Sent ?? []).Union(sent)] });
            if (why is not null) Problem = why;
            return null;
        }
    }

    /// <summary>A game folder's list of the shaders its build ships (the index's): the content hash (20 bytes), then each
    /// shader's SHA-1. What an upload flags against (<see cref="HashOnly.LocalOnly"/>).</summary>
    public const string ShippedFile = "index.shaders";

    public static void SaveShipped(string gameDir, ShaderIndex index)
    {
        if (index.ContentHash.Length != 40) return;   // not a build Share uploads for
        Directory.CreateDirectory(gameDir);
        var tmp = Path.Combine(gameDir, ShippedFile + ".tmp");
        using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
            foreach (var h in index.Shaders.Keys.Prepend(index.ContentHash)) f.Write(Convert.FromHexString(h));
        File.Move(tmp, Path.Combine(gameDir, ShippedFile), true);
    }

    /// <summary>The shaders of <see cref="ShippedFile"/>, null when it's missing or for another build.</summary>
    internal static HashSet<string>? Shipped(string gameDir, string contentHash)
    {
        var path = Path.Combine(gameDir, ShippedFile);
        var b = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (b.Length < 20 || b.Length % 20 != 0 || Convert.ToHexStringLower(b.AsSpan(0, 20)) != contentHash) return null;
        return [.. b.Chunk(20).Skip(1).Select(Convert.ToHexStringLower)];
    }

    static string Id(List<PsoDb.Rec> chunk, string contentHash)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var r in chunk)
        {
            h.AppendData([(byte)r.Tag, .. BitConverter.GetBytes(r.Payload.Length)]);
            h.AppendData(r.Payload);
        }
        return $"{Convert.ToHexStringLower(h.GetHashAndReset())}|{contentHash}";
    }

    /// <summary>POST /v1/upload with the upload device's token (registered first if there is none); null when no device could be had.</summary>
    async Task<HttpResponseMessage?> PostAsync(byte[] body, UploadMeta meta, CancellationToken ct)
    {
        if (await DeviceTokenAsync(ct) is not { } token) return null;
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/upload")) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("X-SCSK-Upload", Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(meta, Json)));
        return await http.SendAsync(request, ct);
    }

    async Task<string?> DeviceTokenAsync(CancellationToken ct)
    {
        if (Load()?.Token is { } token) return token;
        using var r = await http.PostAsync(new Uri(routes.Primary, "v1/devices"), null, ct);
        if (!r.IsSuccessStatusCode)
        {
            BackOff(r);   // 3 registrations a day per IP
            Problem = $"Couldn't register for sharing (error {(int)r.StatusCode}).";
            return null;
        }
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
        var device = new Device(j.GetProperty("device_token").GetString()!, j.GetProperty("device_id").GetString()!);
        Dpapi.Save(file, device);
        return device.Token;
    }

    // 429/503: as long as Retry-After says (1 h without one); 501 (uploads not enabled on this server): a day; others: 5 minutes
    void BackOff(HttpResponseMessage r) => backoffUntil = clock.GetUtcNow() + (r.StatusCode switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => r.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } at } => at - clock.GetUtcNow(),
            _ => TimeSpan.FromHours(1),
        },
        HttpStatusCode.NotImplemented => TimeSpan.FromDays(1),
        _ => TimeSpan.FromMinutes(5),
    });

    // upload.dat: {"Token":"sd1_...","Id":"..."} under DPAPI, like auth.dat but its own file: never the Patreon device
    sealed record Device(string Token, string Id);

    Device? Load() => Dpapi.Load<Device>(file);   // null (another user's or damaged): register again
}
