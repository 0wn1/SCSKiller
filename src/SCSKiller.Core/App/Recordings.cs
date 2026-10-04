using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.App;

/// <summary>A game's recording: SCSKiller's copy (games\&lt;id&gt;\recording.db, compact: <see cref="PsoDb.WriteCompact"/>)
/// is the durable one, and the recorder's scskiller.db in the game folder is an inbox, emptied once imported. The recorder
/// skips what the copy already has, and the bytes of the shaders the game ships, through <see cref="KeysFile"/>, next to
/// its scskiller.ini.</summary>
public static class Recordings
{
    public const string KeysFile = "scskiller.keys";

    /// <summary>Held around a read-modify-write of <paramref name="store"/> (import, compaction, migration, clearing) by every
    /// process: two writers would each replace the file with their own merge and drop the other's records. Every writer
    /// of <paramref name="store"/> holds it, so a temp file found under it belongs to a writer that is gone: removed once
    /// not written for an hour, or written before the PC started (no process is looked at). <paramref name="wait"/>,
    /// <paramref name="ct"/>: as <see cref="AppStore.PathGate"/>.</summary>
    public static IDisposable Lock(string store, TimeSpan? wait = null, CancellationToken ct = default)
    {
        var gate = new AppStore.PathGate(store, wait, ct);
        var dir = Path.GetDirectoryName(Path.GetFullPath(store))!;
        var booted = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        try
        {
            if (Directory.Exists(dir))
                foreach (var tmp in new DirectoryInfo(dir).EnumerateFiles(Path.GetFileName(store) + ".*tmp"))
                    try
                    {
                        if (tmp.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1) || tmp.LastWriteTimeUtc < booted) tmp.Delete();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // still open: left
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // left for the next writer
        return gate;
    }

    /// <summary>Tests: runs once a keys file is written, before it replaces the old one.</summary>
    internal static Action? BeforeKeysPublished;

    // proxy.cpp load_keys: magic, then 20-byte record keys and blob hashes
    static ReadOnlySpan<byte> KeysMagic => "SCSKKEY1"u8;

    /// <summary>Writes <paramref name="store"/> (none: empty) plus the records of <paramref name="inbox"/> it lacks, in that
    /// order, as a compact recording at <paramref name="store"/>. A shader blob that a record names and <paramref name="shipped"/>
    /// has is left out: the install gives it back (<see cref="Rehydrate"/>); root signatures and every other blob stay. Returns
    /// the keys of the pipeline and state object records it added.</summary>
    public static HashSet<string> Merge(string store, string? inbox, Func<string, bool>? shipped) => Merge(store, inbox, shipped, out _);

    /// <param name="nvAdded">an 'N' record the store lacked was added: the plan may take another NVAPI state for the PSOs it
    /// synthesizes (<see cref="PlanBuilder.RasterNv"/>)</param>
    public static HashSet<string> Merge(string store, string? inbox, Func<string, bool>? shipped, out bool nvAdded)
    {
        var hasStore = File.Exists(store);
        var hasInbox = inbox != null && File.Exists(inbox);
        var nv = false;
        IEnumerable<Rec> Sources() => (hasStore ? Read(store) : []).Concat(hasInbox ? Read(inbox!) : []);
        var named = shipped == null ? [] : Rehydrate.References(Sources().Where(r => r.Tag != 'B'));
        var seen = new HashSet<string>();
        var added = new HashSet<string>();
        IEnumerable<Rec> Union()
        {
            if (hasStore) foreach (var r in Read(store)) if (seen.Add(Id(r))) yield return r;
            if (hasInbox)
                foreach (var r in Read(inbox!))
                    if (seen.Add(Id(r)))
                    {
                        if (r.Tag == 'N') nv = true;
                        else if (r.Tag is not ('B' or 'W')) added.Add(r.Key);
                        yield return r;
                    }
        }
        bool Dropped(Rec r) => r.Tag == 'B' && shipped != null && Hex(r.Payload.AsSpan(0, 20)) is var h && named.Contains(h) && shipped(h)
                               && !Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20));
        WriteCompact(store, Union().Where(r => !Dropped(r)));
        KeyFiles.Forget(store);
        nvAdded = nv;
        return added;
    }

    // a blob by its hash: cheaper than hashing its bytes again
    static string Id(Rec r) => r.Tag == 'B' ? "B" + Hex(r.Payload.AsSpan(0, 20)) : r.Key;

    /// <summary>Writes <paramref name="path"/>: every shader of <paramref name="shipped"/> (the recorder then names it by
    /// hash, without its bytes: the install gives them back), every blob of <paramref name="store"/> and the key of every
    /// record it holds that replays from the two. A record naming a blob neither has is left out, so the recorder records it
    /// again, with its bytes, if the game still creates it. Returns how many were left out. With nothing to name, no file.
    /// Not <paramref name="nameShipped"/> (the install may be another build than <paramref name="shipped"/>'s): its shaders
    /// only say which records stay named, and the recorder records every new shader with its bytes. Nothing created,
    /// deleted or published unless <paramref name="publish"/> holds before it starts and right before the publish (the
    /// proxy is still there and the game isn't running: a rollback deletes the file without the recording lock).</summary>
    public static int WriteKeys(string store, IReadOnlySet<string>? shipped, string path, bool nameShipped = true, Func<bool>? publish = null)
    {
        var blobs = new HashSet<string>(nameShipped && shipped != null ? shipped : []);
        var records = new List<Rec>();
        foreach (var r in File.Exists(store) ? Read(store) : [])
            if (r.Tag == 'B') blobs.Add(Hex(r.Payload.AsSpan(0, 20)));
            else records.Add(r);
        var keep = records.Where(r => r.Tag == 'N' || Rehydrate.References([r]).All(h => blobs.Contains(h) || shipped?.Contains(h) == true)).ToList();
        if (publish?.Invoke() == false) return records.Count - keep.Count;   // nothing created or deleted
        if (blobs.Count + keep.Count == 0)
        {
            File.Delete(path);
            return records.Count;
        }
        var tmp = path + ".tmp";
        try
        {
            using (var f = new BufferedStream(File.Create(tmp), 1 << 16))
            {
                f.Write(KeysMagic);
                foreach (var h in blobs) f.Write(Convert.FromHexString(h));
                foreach (var r in keep) f.Write(Convert.FromHexString(r.Key));
            }
            BeforeKeysPublished?.Invoke();
            if (publish?.Invoke() != false) File.Move(tmp, path, true);
        }
        finally { File.Delete(tmp); }
        return records.Count - keep.Count;
    }

    /// <summary>The records of <paramref name="store"/> a layer wrapping the device (a mod) made, by its 'W' records: the
    /// game's creates it changed before the driver got them, and its own creates. (0, 0): recorded without one.</summary>
    public static (int Changed, int Own) Layered(string store)
    {
        var pairs = Pairs(store);
        var own = pairs.Count(p => p.EndsWith(Zero, StringComparison.Ordinal));
        return (pairs.Count - own, own);
    }

    /// <summary>The keys of the records of <paramref name="store"/> a layer made ('W': changed or its own); a store that
    /// can't be read throws: what is shared is checked against them.</summary>
    public static HashSet<string> LayerMade(string store) => [.. Pairs(store).Select(p => p[..40])];

    /// <summary>A recording whose 'W' records can't all be read yet: a torn tail (normal while the recorder writes its
    /// inbox) or a malformed 'W'. Nothing is shared until it reads whole, usually once the game has closed.</summary>
    public sealed class IncompleteLayerList(string message) : IOException(message);

    static IReadOnlySet<string> Pairs(string store) =>
        KeyFiles.Keys(store, p =>
        {
            // the recorder holds its inbox open for writing while the game runs: read alongside it
            using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[8];
            var n = f.ReadAtLeast(head, 8, false);
            f.Position = 0;
            // a proxy db starts with a record's tag, never 0: a leading 0 is a compact recording's magic, whole or damaged
            if (n > 0 && head[0] == 0 && !head.AsSpan(0, n).SequenceEqual("\0SCSKREC"u8)) throw new IncompleteLayerList($"{p}: a damaged compact header");
            IEnumerable<Rec> records;
            try { records = n > 0 && head[0] == 0 ? [.. Read(p)] : [.. Whole(new BufferedStream(f, 1 << 20), f.Length, p)]; }
            catch (InvalidDataException e) { throw new IncompleteLayerList($"{p}: {e.Message}"); }
            if (records.Where(r => r.Tag == 'W').Select(r => r.Payload.Length).FirstOrDefault(n => n != 40, 40) is not 40 and var bad)
                throw new IncompleteLayerList($"{p}: a 'W' record of {bad} bytes");
            return [.. records.Where(r => r.Tag == 'W').Select(r => Hex(r.Payload))];
        }, "layer", failOpen: false);

    /// <summary>A proxy db's records, all of them: a torn tail throws, where <see cref="Read(Stream)"/> stops quietly.</summary>
    static IEnumerable<Rec> Whole(Stream f, long length, string path)
    {
        var head = new byte[5];
        long at = 0;
        for (int n; (n = f.ReadAtLeast(head, 5, false)) > 0;)
        {
            var len = BitConverter.ToUInt32(head, 1);
            if (n < 5 || len > length - at - 5) throw new IncompleteLayerList($"{path}: a torn tail at byte {at}");
            var body = new byte[len];
            f.ReadExactly(body);
            at += 5 + len;
            yield return new Rec((char)head[0], body);
        }
    }

    static readonly object DiskGate = new();
    static (string Fingerprint, HashSet<string> Keys)? onDisk;
    static readonly Dictionary<string, (long Length, long Written, string[] Inboxes)> InboxesOf = [];

    /// <summary>What a layer made by every recording on this PC, read from disk (not from the scan's games, which a
    /// first scan publishes only once evaluated): every games\*\recording.db, and the recorder's inbox (scskiller.db)
    /// next to each recorded exe in state.json. One enumeration and a stat per file on each call; the set is rebuilt
    /// only when one of them changed. Anything there that can't be listed, stat'ed or read throws (a recording not whole
    /// yet: <see cref="IncompleteLayerList"/>): what is shared is checked against it.</summary>
    public static HashSet<string> LayerMadeOnDisk(string dataDir)
    {
        var games = new DirectoryInfo(Path.Combine(dataDir, "games"));
        List<DirectoryInfo> dirs;
        try { dirs = [.. games.EnumerateDirectories()]; }
        catch (DirectoryNotFoundException) { return []; }   // only a missing folder is none: Exists is false on an error too
        var sources = new List<string>();
        var fingerprint = new System.Text.StringBuilder();
        void Add(string path, long length, long written) { sources.Add(path); fingerprint.Append(path).Append('|').Append(length).Append('|').Append(written).Append('\n'); }
        foreach (var dir in dirs)
            foreach (var f in dir.EnumerateFiles())
                if (f.Name.Equals("recording.db", StringComparison.OrdinalIgnoreCase)) Add(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks);
                else if (f.Name.Equals("state.json", StringComparison.OrdinalIgnoreCase))
                {
                    fingerprint.Append(f.FullName).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
                    foreach (var inbox in Inboxes(f))
                        if (Stat(inbox) is { } st) Add(inbox, st.Length, st.Written);
                }
        var fp = fingerprint.ToString();
        // unchanged sizes and write times: the files hold what was read, so their keys stand even if a read would now fail
        lock (DiskGate)
            if (onDisk is { } c && c.Fingerprint == fp) return [.. c.Keys];
        HashSet<string> keys = [];
        foreach (var s in sources) keys.UnionWith(LayerMade(s));
        lock (DiskGate) onDisk = (fp, keys);
        return [.. keys];
    }

    /// <summary>The recorder inboxes a game's state.json names (next to its recorded exe, and the two of a move), parsed
    /// again only when the file changed.</summary>
    static string[] Inboxes(FileInfo state)
    {
        lock (DiskGate)
            if (InboxesOf.TryGetValue(state.FullName, out var c) && (c.Length, c.Written) == (state.Length, state.LastWriteTimeUtc.Ticks)) return c.Inboxes;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(state.FullName));
        string[] inboxes = [.. new[] { "RecorderExe", "RecorderMoveFrom", "RecorderMoveTo" }
            .Select(n => !doc.RootElement.TryGetProperty(n, out var exe) ? null : exe.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => exe.GetString(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => throw new InvalidDataException($"{state.FullName}: {n} is a {exe.ValueKind}, not a path"),
            })
            .OfType<string>().Where(p => p.Length > 0).Select(p => Path.Combine(Path.GetDirectoryName(p)!, "scskiller.db"))];
        lock (DiskGate) InboxesOf[state.FullName] = (state.Length, state.LastWriteTimeUtc.Ticks, inboxes);
        return inboxes;
    }

    /// <summary>A file's size and write time from its handle; null only when it (or its folder) isn't there.</summary>
    static (long Length, long Written)? Stat(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (RandomAccess.GetLength(h), File.GetLastWriteTimeUtc(h).Ticks);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    /// <summary>Empties the recorder's scskiller.db once imported, only while nothing has it open (the recorder holds it for
    /// the whole session) and only at the <paramref name="imported"/> length it was read at: what came after isn't imported.</summary>
    public static bool Rotate(string inbox, long imported)
    {
        try
        {
            using var f = new FileStream(inbox, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (f.Length != imported) return false;
            f.SetLength(0);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
