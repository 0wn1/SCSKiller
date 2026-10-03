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
                        else if (r.Tag != 'B') added.Add(r.Key);
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
