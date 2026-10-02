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
    /// DLL) hold, and the recordings' blobs.</summary>
    public sealed record Recorded(List<(string Key, List<string> Missing)> Records, HashSet<string> Blobs)
    {
        public static Recorded Read(IReadOnlyList<string> recordings, Func<string, bool> elsewhere)
        {
            var blobs = new HashSet<string>();
            var seen = new HashSet<string>();
            var records = new List<(string Key, List<string> Missing)>();
            foreach (var db in recordings.Where(File.Exists))
                foreach (var r in PsoDb.Read(db))
                    if (r.Tag == 'B') { if (r.Payload.Length >= 20) blobs.Add(Hex(r.Payload.AsSpan(0, 20))); }
                    else if (r.Tag is not ('N' or 'L') && seen.Add(r.Key)) records.Add((r.Key, Missing(r, h => blobs.Contains(h) || elsewhere(h))));
            return new([.. records.Select(r => r.Missing.Count == 0 ? r : (r.Key, r.Missing.Where(h => !blobs.Contains(h)).ToList()))], blobs);   // a blob may come later
        }
    }

    static List<string> Missing(Rec r, Func<string, bool> has)
    {
        try { return [.. Rehydrate.References([r]).Where(h => !has(h))]; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException) { return []; }
    }

    /// <summary>Each input's key, with a trailing '!' when a blob it names isn't at hand: the recordings' records, then
    /// the plan's (a pack entry unwrapped), then <paramref name="packEntries"/>.</summary>
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
            inputs.Add(missing.All(Has) ? key : key + "!");
        }
        foreach (var r in body.Where(r => r.Tag != 'B').Select(r => r.Tag == 'M' ? MiddlewarePacks.Unwrap(r).Entry : r).Concat(packEntries))
            if (seen.Add(r.Key)) inputs.Add(Missing(r, Has).Count == 0 ? r.Key : r.Key + "!");
        return inputs;
    }

    public static HashSet<string> Of(IReadOnlyList<string> recordings, string? planFile, IEnumerable<Rec> packEntries, Func<string, bool> elsewhere) =>
        Of(Recorded.Read(recordings, elsewhere), planFile, packEntries, elsewhere);

    public static string Key(string input) => input[..40];

    /// <summary>A baseline (key file of <see cref="Token"/>s) took this input: it holds its key, or it lacks a blob now and
    /// the baseline has it so too. Only gaining a blob makes a taken input new again; losing one doesn't.</summary>
    public static bool Taken(IReadOnlySet<string> baseline, string input) =>
        baseline.Contains(Key(input)) || input.Length > 40 && baseline.Contains(Token(input));

    /// <summary>An input as a key file holds it: its key, or for one with a blob missing the SHA-1 of its key and '!', so
    /// that the same record with its blobs at hand is another input.</summary>
    public static string Token(string input) =>
        input.Length == 40 ? input : Convert.ToHexStringLower(SHA1.HashData([.. Convert.FromHexString(input[..40]), (byte)'!']));
}
