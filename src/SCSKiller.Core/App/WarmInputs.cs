using System.Security.Cryptography;
using SCSKiller.Core.Planning;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.App;

/// <summary>The records a warm takes as input, each with one bit: whether every blob it names directly is in the sources a
/// warm reads (the recordings, the plan, the packs, the build's shaders, the DLLs next to the exe). Nothing here predicts
/// what compiles: the count of new pipelines compares these with the ones taken when the last complete warm started.</summary>
public static class WarmInputs
{
    /// <summary>The recordings' part, read once as their union (the first's records first), keeping no blob bytes: each
    /// record's key with the blobs it names that neither the recordings nor <paramref name="elsewhere"/> (the install or a
    /// DLL) hold, and the recordings' blobs. With <paramref name="nvidia"/> a state object counts once per
    /// <see cref="StateObjectIdentity">identity</see> and NVAPI state, keyed by the SHA-1 of the two (whichever record of it
    /// comes first) with the blobs of its first record: the same addition on another base, or under a launch's export
    /// names, compiles nothing new there, and the same one under another NVAPI state does. <see cref="Members"/> holds the
    /// keys of its records. AMD caches a whole state object (AmdBackend), so elsewhere each record counts by its key. The
    /// warm replays every record either way.</summary>
    public sealed record Recorded(List<(string Key, List<string> Missing)> Records, HashSet<string> Blobs)
    {
        /// <summary>An identity's key -> the keys of its records, in the recordings' order.</summary>
        public Dictionary<string, List<string>> Members { get; init; } = [];

        public static Recorded Read(IReadOnlyList<string> recordings, Func<string, bool> elsewhere, bool nvidia = false)
        {
            var blobs = new HashSet<string>();
            // NVIDIA keys on the NVAPI state a record is created with (selftest nvext). The replay's: the last 'N' per record
            // in the recordings' union, which drops an 'N' an earlier recording has (Community.Union, proxy.cpp g_nvext)
            var nv = new Dictionary<string, NvState>();
            var union = new HashSet<string>();
            if (nvidia)
                foreach (var r in recordings.Where(File.Exists).SelectMany(PsoDb.Read))
                    if (r.Tag == 'N' && r.Payload.Length == NvState.Size && union.Add(r.Key)) { var n = NvState.Parse(r); nv[n.Target] = n; }
            string Identity(Rec r) => Hex(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"so{StateObjectIdentity(r)}|" + (nv.TryGetValue(r.Key, out var n) ? $"{n.Slot},{n.Space},{n.Options}" : "none"))));
            var seen = new HashSet<string>();
            var members = new Dictionary<string, List<string>>();
            var records = new List<(string Key, List<string> Missing)>();
            foreach (var db in recordings.Where(File.Exists))
                foreach (var r in PsoDb.Read(db))
                    if (r.Tag == 'B') { if (r.Payload.Length >= 20) blobs.Add(Hex(r.Payload.AsSpan(0, 20))); }
                    else if (r.Tag is not ('N' or 'L' or 'W') && seen.Add(r.Key))
                    {
                        var key = nvidia && IsStateObject(r.Tag) ? Identity(r) : r.Key;
                        if (key != r.Key && !members.TryAdd(key, [r.Key])) { members[key].Add(r.Key); continue; }
                        records.Add((key, Missing(r, h => blobs.Contains(h) || elsewhere(h))));
                    }
            return new([.. records.Select(r => r.Missing.Count == 0 ? r : (r.Key, r.Missing.Where(h => !blobs.Contains(h)).ToList()))], blobs) { Members = members };   // a blob may come later
        }
    }

    static List<string> Missing(Rec r, Func<string, bool> has)
    {
        try { return [.. Rehydrate.References([r]).Where(h => !has(h))]; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { return []; }
    }

    /// <summary>Each input's key, with a trailing '!' when a blob it names isn't at hand, then for a state object identity
    /// the keys of its records ('|' before each): the recordings' records, then the plan's (a pack entry unwrapped), then
    /// <paramref name="packEntries"/>.</summary>
    public static HashSet<string> Of(Recorded recorded, string? planFile, IEnumerable<Rec> packEntries, Func<string, bool> elsewhere)
    {
        var blobs = new HashSet<string>(recorded.Blobs);
        var body = Planner.PlanBody(planFile);
        foreach (var r in body) if (r.Tag == 'B' && r.Payload.Length >= 20) blobs.Add(Hex(r.Payload.AsSpan(0, 20)));
        bool Has(string h) => blobs.Contains(h) || elsewhere(h);
        var inputs = new HashSet<string>();
        var seen = new HashSet<string>();
        foreach (var (key, missing) in recorded.Records)
        {
            seen.Add(key);
            inputs.Add((missing.All(Has) ? key : key + "!") + (recorded.Members.TryGetValue(key, out var m) ? "|" + string.Join('|', m) : ""));
        }
        foreach (var (key, r) in Planner.PlanInputs(body).Select(x => x.Rec.Tag == 'M' ? (MiddlewarePacks.Unwrap(x.Rec).Entry.Key, MiddlewarePacks.Unwrap(x.Rec).Entry) : x)
                     .Concat(packEntries.Select(r => (r.Key, r))))
            if (seen.Add(key)) inputs.Add(Missing(r, Has).Count == 0 ? key : key + "!");
        return inputs;
    }

    public static HashSet<string> Of(IReadOnlyList<string> recordings, string? planFile, IEnumerable<Rec> packEntries, Func<string, bool> elsewhere) =>
        Of(Recorded.Read(recordings, elsewhere), planFile, packEntries, elsewhere);

    public static string Key(string input) => input[..40];

    /// <summary>The keys of the records an input stands for: a state object identity's records, else its own key.</summary>
    public static IEnumerable<string> Records(string input) => input.Contains('|') ? input.Split('|').Skip(1) : [Key(input)];

    /// <summary>A baseline (key file of <see cref="Token"/>s) took this input: it holds its key, or it lacks a blob now and
    /// the baseline has it so too. Only gaining a blob makes a taken input new again; losing one doesn't. A baseline written
    /// before state object identities holds their records' keys instead: one of them takes the identity, whatever NVAPI
    /// state it was warmed under, until the next warm writes the identity's key. A key file written since holds no state
    /// object's record key on NVIDIA: plans ('Y' / 'H') and packs carry none.</summary>
    public static bool Taken(IReadOnlySet<string> baseline, string input)
    {
        var missing = input.Length > 40 && input[40] == '!';
        bool Holds(string key) => baseline.Contains(key) || missing && baseline.Contains(Token(key + "!"));
        return Holds(Key(input)) || input.Contains('|') && input.Split('|').Skip(1).Any(Holds);
    }

    /// <summary>An input as a key file holds it: its key, or for one with a blob missing the SHA-1 of its key and '!', so
    /// that the same record with its blobs at hand is another input.</summary>
    public static string Token(string input) =>
        input.Length == 40 || input[40] != '!' ? input[..40] : Convert.ToHexStringLower(SHA1.HashData([.. Convert.FromHexString(input[..40]), (byte)'!']));
}
