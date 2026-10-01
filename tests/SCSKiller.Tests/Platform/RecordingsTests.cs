using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

// Compact recordings (PsoDb.WriteCompact), the store's merge, the recorder's keys file and the inbox's rotation (Recordings).
public class RecordingsTests(ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("scskiller-recordings-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    static PsoDb.Rec Blob(byte[] b) => new('B', [.. SHA1.HashData(b), .. b]);
    static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));
    static PsoDb.Rec Cs(byte[] rs, byte[] cs) => new('C', PsoDb.Compute(Sha(rs), Sha(cs)));
    static byte[] Shader(string name) => System.Text.Encoding.ASCII.GetBytes("DXBC shader " + name);
    static List<string> Keys(IEnumerable<PsoDb.Rec> recs) => recs.Select(r => r.Key).ToList();

    string Raw(string name, params IEnumerable<PsoDb.Rec> recs)
    {
        var path = Path.Combine(_dir, name);
        using var f = File.Create(path);
        foreach (var r in recs) PsoDb.Write(f, r.Tag, r.Payload);
        return path;
    }

    /// <summary>What the recorder writes of a session's creates (proxy.cpp store): the records the keys file doesn't name and,
    /// before each, the blobs it doesn't name.</summary>
    static IEnumerable<PsoDb.Rec> Recorded(IEnumerable<PsoDb.Rec> session, string keysFile)
    {
        var keys = File.Exists(keysFile) ? File.ReadAllBytes(keysFile)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet() : [];
        return session.Where(r => keys.Add(r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key));
    }

    [Fact]
    public void A_compact_recording_reads_back_as_the_proxy_db_it_holds_and_a_proxy_db_still_reads()
    {
        var rs = CommunityTests.RootSignature();
        var recs = new[] { Blob(rs), Blob(Shader("a")), Cs(rs, Shader("a")), new PsoDb.NvState(Cs(rs, Shader("a")).Key, 7, 0, 1, 0).ToRec() };
        var raw = Raw("raw.db", recs);
        var compact = Path.Combine(_dir, "compact.db");
        PsoDb.WriteCompact(compact, PsoDb.Read(raw));

        Assert.True(PsoDb.IsCompact(compact));
        Assert.False(PsoDb.IsCompact(raw));
        Assert.Equal(Keys(recs), Keys(PsoDb.Read(compact)));
        Assert.Equal(Keys(recs), Keys(PsoDb.Read(raw)));
        PsoDb.CopyRaw(compact, Path.Combine(_dir, "back.db"));
        Assert.Equal(File.ReadAllBytes(raw), File.ReadAllBytes(Path.Combine(_dir, "back.db")));
        Assert.False(File.Exists(compact + ".tmp"));

        var bytes = File.ReadAllBytes(compact);
        File.WriteAllBytes(compact, bytes[..^4]);   // cut short: never read as a shorter recording
        Assert.Throws<InvalidDataException>(() => PsoDb.Read(compact).ToList());
        bytes[8] = 99;   // a version this build doesn't know
        File.WriteAllBytes(compact, bytes);
        Assert.Throws<InvalidDataException>(() => PsoDb.Read(compact).ToList());
    }

    [Fact]
    public void An_interrupted_write_leaves_the_old_recording_and_the_next_one_succeeds()
    {
        var rs = CommunityTests.RootSignature();
        var path = Raw("recording.db", Blob(rs), Blob(Shader("a")), Cs(rs, Shader("a")));
        var before = File.ReadAllBytes(path);
        IEnumerable<PsoDb.Rec> Failing()
        {
            foreach (var r in PsoDb.Read(path)) yield return r;
            throw new IOException("the disk is full");
        }
        Assert.Throws<IOException>(() => PsoDb.WriteCompact(path, Failing()));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path + ".tmp", "left by a process that was killed mid-write");
        PsoDb.WriteCompact(path, PsoDb.Read(path).ToList());
        Assert.Equal(Keys(HashOnly.Records(before)), Keys(PsoDb.Read(path)));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_recording_being_read_is_not_replaced_and_the_next_write_is()
    {
        var rs = CommunityTests.RootSignature();
        var path = Path.Combine(_dir, "recording.db");
        PsoDb.WriteCompact(path, [Blob(rs), Cs(rs, Shader("a"))]);
        using (var reading = PsoDb.Read(path).GetEnumerator())
        {
            Assert.True(reading.MoveNext());
            Assert.ThrowsAny<UnauthorizedAccessException>(() => PsoDb.WriteCompact(path, [Blob(rs), Cs(rs, Shader("b"))]));
            Assert.True(reading.MoveNext());
            Assert.Equal(Cs(rs, Shader("a")).Key, reading.Current.Key);
        }
        Assert.False(File.Exists(path + ".tmp"));
        PsoDb.WriteCompact(path, [Blob(rs), Cs(rs, Shader("b"))]);
        Assert.Equal(Cs(rs, Shader("b")).Key, PsoDb.Read(path).Last().Key);
    }

    /// <summary>The store keeps its records and adds the inbox's new ones after them, once each; a shader blob a record names
    /// and the index has is left out, every other blob stays: root signatures, shaders in no file of the game, and blobs no
    /// record names (the planner reads those too).</summary>
    [Fact]
    public void The_merge_adds_what_is_new_once_and_leaves_out_the_shader_bytes_the_install_has()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = Shader("a"), b = Shader("b"), runtime = Shader("built at run time"), orphan = Shader("named by nothing");
        var nv = new PsoDb.NvState(Cs(rs, b).Key, 7, 0, 1, 0).ToRec();
        var store = Path.Combine(_dir, "recording.db");
        Assert.Equal([Cs(rs, a).Key], Recordings.Merge(store, Raw("s1.db", Blob(rs), Blob(a), Cs(rs, a)), null));
        var inbox = Raw("s2.db", Blob(rs), Blob(a), Cs(rs, a), Blob(b), Cs(rs, b), nv, Blob(runtime), Cs(rs, runtime), Blob(orphan));
        var shipped = new HashSet<string> { Sha(a), Sha(b), Sha(orphan), Sha(rs) };

        var added = Recordings.Merge(store, inbox, shipped.Contains);
        Assert.Equal(new[] { Cs(rs, b).Key, Cs(rs, runtime).Key }.Order(), added.Order());
        Assert.Equal(Keys([Blob(rs), Cs(rs, a), Cs(rs, b), nv, Blob(runtime), Cs(rs, runtime), Blob(orphan)]), Keys(PsoDb.Read(store)));
        Assert.Empty(Recordings.Merge(store, inbox, shipped.Contains));   // imported again: nothing new
        Assert.Equal(Keys([Blob(rs), Cs(rs, a), Cs(rs, b), nv, Blob(runtime), Cs(rs, runtime), Blob(orphan)]), Keys(PsoDb.Read(store)));
    }

    /// <summary>The keys file names every blob the store holds and every record that replays from the store and the index; a
    /// record whose shader bytes were left out for an index that no longer has them isn't named, so the recorder records it
    /// again with its bytes.</summary>
    [Fact]
    public void The_keys_file_names_what_the_recorder_needn_t_record_again()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = Shader("a"), gone = Shader("in the last build only"), runtime = Shader("built at run time");
        var store = Path.Combine(_dir, "recording.db");
        var nv = new PsoDb.NvState(Cs(rs, gone).Key, 7, 0, 1, 0).ToRec();
        Recordings.Merge(store, Raw("s.db", Blob(rs), Blob(a), Cs(rs, a), Blob(gone), Cs(rs, gone), nv, Blob(runtime), Cs(rs, runtime)),
            new HashSet<string> { Sha(a), Sha(gone) }.Contains);
        var keys = Path.Combine(_dir, Recordings.KeysFile);

        Assert.Equal(1, Recordings.WriteKeys(store, new HashSet<string> { Sha(a) }, keys));
        var file = File.ReadAllBytes(keys);
        Assert.Equal("SCSKKEY1"u8.ToArray(), file[..8]);
        var named = file[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        Assert.Equal(new[] { Sha(a), Sha(rs), Sha(runtime), Cs(rs, a).Key, nv.Key, Cs(rs, runtime).Key }.Order(), named.Order());
    }

    /// <summary>With the index's shaders in the keys file, a first session names a shipped shader by hash only and keeps the
    /// bytes of one in no file of the game; the import and the install give every record its shaders back. Without an index
    /// there's no keys file and every shader is recorded whole; a shader a later index no longer has is recorded again.</summary>
    [Fact]
    public void A_shipped_shader_is_recorded_by_hash_only_and_read_back_from_the_install()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = Shader("a"), mod = Shader("a mod's");
        PsoDb.Rec[] session = [Blob(rs), Blob(a), Cs(rs, a), Blob(mod), Cs(rs, mod)];
        var shipped = new HashSet<string> { Sha(a) };
        var (store, keys) = (Path.Combine(_dir, "recording.db"), Path.Combine(_dir, Recordings.KeysFile));

        File.WriteAllText(keys, "left by a recording that is gone");
        Assert.Equal(0, Recordings.WriteKeys(store, null, keys));   // no index, no recording
        Assert.False(File.Exists(keys));
        Assert.Equal(Keys(session), Keys(Recorded(session, keys)));

        Recordings.WriteKeys(store, shipped, keys);
        Assert.Equal(8 + 20, new FileInfo(keys).Length);
        var inbox = Raw("scskiller.db", Recorded(session, keys));
        Assert.Equal(Keys([Blob(rs), Cs(rs, a), Blob(mod), Cs(rs, mod)]), Keys(PsoDb.Read(inbox)));
        Assert.Equal(2, Recordings.Merge(store, inbox, shipped.Contains).Count);
        Assert.Equal(0, Recordings.WriteKeys(store, shipped, keys));
        Assert.Empty(Recorded(session, keys));

        File.WriteAllBytes(Path.Combine(_dir, Sha(a) + ".bin"), a);   // the install
        var full = Path.Combine(_dir, "full.db");
        var back = Rehydrate.Run(store, full, new Game("test:keys", "Keys", Store.Other, _dir, _dir), new("Unreal", "4.26", null, "D3D12", false, null), new BytecodeDir(_dir));
        Assert.True(back.Complete);
        Assert.Equal(Keys(session).Order(), Keys(PsoDb.Read(full)).Order());

        Assert.Equal(1, Recordings.WriteKeys(store, new HashSet<string>(), keys));   // the next build doesn't ship a
        Assert.Equal(Keys([Blob(a), Cs(rs, a)]), Keys(Recorded(session, keys)));
    }

    /// <summary>A first session of a big game, simulated from SCSKiller's recording of it on this machine (every pipeline
    /// new, no recording yet): with the index's shaders in the keys file the inbox is a small part of what it is without, and
    /// importing it gives the compile the same shaders. Install and app data read only; returns early without the game or
    /// its recording.</summary>
    [Trait("Needs", "Game")]
    [Theory]
    [InlineData("xbox:BethesdaSoftworks.ProjectAltar_3275kfvn8vcwc")]   // Oblivion Remastered
    [InlineData("steam:990080")]                                        // Hogwarts Legacy
    [InlineData("ea:198300")]                                           // STAR WARS Jedi: Survivor
    public void A_first_session_of_a_big_game_stays_small(string id)
    {
        var game = new IGameSource[] { new Core.Games.SteamSource(), new Core.Games.EaSource(), new Core.Games.XboxSource() }
            .SelectMany(s => s.Discover()).FirstOrDefault(g => g.Id == id);
        if (game == null) return;
        Ff7.Codecs();
        var name = "keys-" + id.Split(':')[0];
        var data = Ff7.TempDir(name + "-data");
        string? made = null;
        try
        {
            var reader = new UnrealReader(data);
            var engine = reader.Detect(game)!;
            if (Ff7.Recording(game, engine, reader, name) is not { } session) return;
            var dir = made = Path.GetDirectoryName(session)!;
            var shipped = reader.Index(game, engine, null, CancellationToken.None).Shaders.Keys.ToHashSet();
            var (store, keys, inbox) = (Path.Combine(dir, "store.db"), Path.Combine(dir, Recordings.KeysFile), Path.Combine(dir, "scskiller.db"));

            Recordings.WriteKeys(store, shipped, keys);
            using (var f = new BufferedStream(File.Create(inbox), 1 << 20))
                foreach (var r in Recorded(PsoDb.Read(session), keys)) PsoDb.Write(f, r.Tag, r.Payload);
            long whole = new FileInfo(session).Length, byHash = new FileInfo(inbox).Length;
            output.WriteLine($"{game.Name}: a first session's scskiller.db {whole:N0} bytes with every shader's bytes, {byHash:N0} with the {shipped.Count:N0} shipped shaders by hash ({new FileInfo(keys).Length:N0} byte keys file)");
            foreach (var g in PsoDb.Read(inbox).GroupBy(r => r.Tag != 'B' ? "records" : Core.Carved.Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20)) ? "root signatures" : "shaders in no file of the game"))
                output.WriteLine($"  {g.Key}: {g.Count():N0}, {g.Sum(r => 5L + r.Payload.Length):N0} bytes");
            Assert.True(byHash * 5 < whole);

            var had = PsoDb.Read(session).Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
            var missing = Rehydrate.References(PsoDb.Read(session)).Where(h => !had.Contains(h)).Order(StringComparer.Ordinal).ToList();
            Recordings.Merge(store, inbox, shipped.Contains);
            var back = Rehydrate.Run(store, Path.Combine(dir, "back.db"), game, engine, reader, moreBlobs: h => Core.Planning.Middleware.Blobs(game, h));
            Assert.Equal(missing, back.Missing);
            Assert.Equal(PsoDb.Read(session).Count(r => r.Tag != 'B'), PsoDb.Read(Path.Combine(dir, "back.db")).Count(r => r.Tag != 'B'));
        }
        finally
        {
            foreach (var d in new[] { data, made }.OfType<string>().Where(Directory.Exists)) Directory.Delete(d, true);
        }
    }

    [Fact]
    public void The_inbox_is_emptied_only_at_its_imported_length_and_while_nothing_has_it_open()
    {
        var inbox = Raw("scskiller.db", Blob(Shader("a")));
        var length = new FileInfo(inbox).Length;
        using (new FileStream(inbox, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))   // the recorder's handle
            Assert.False(Recordings.Rotate(inbox, length));
        Assert.False(Recordings.Rotate(inbox, length - 1));   // it grew after the import read it
        Assert.Equal(length, new FileInfo(inbox).Length);
        Assert.True(Recordings.Rotate(inbox, length));
        Assert.Equal(0, new FileInfo(inbox).Length);
    }
}
