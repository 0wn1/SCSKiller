using System.Globalization;

namespace SCSKiller.Core.App;

/// <summary>Reads the recorder's scskiller_frames.bin (ARCHITECTURE.md "scskiller_frames.bin") with its
/// scskiller_creates.csv and says which slow frames were shader compiles. Both files count in the recorder's own clock
/// (ms since it loaded: a launch record's microseconds, the csv's t_ms), so a create and a frame line up without any
/// alignment step.</summary>
public static class FrameLog
{
    public const string FileName = "scskiller_frames.bin";
    public const double HitchMs = 50;
    public const int GraphColumns = 1200;  // wider than the game page's graph: each pixel column takes the longest of its slices
    const double BlockMs = 10;           // a create that blocked this long can make a frame a hitch (docs/measuring.md)
    const int LoadCreates = 100;         // measured: loads and precompiles overlap 130-1,700 creates, play stutters 11-36
    const int StartupQuietCreates = 30;  // startup ends at the first 3 s with fewer creates
    const double PauseMs = 5000;         // a play frame this long without a create: the game paused or minimized
    const double QuitMs = 10_000;        // measured: an Unreal game's quit freeze starts 6-7 s before its last frame
    const int BurstCreates = 100;        // a second with this many creates is a precompile or a load
    const double BurstGapMs = 10_000;    // measured: a title screen's second burst 5 s after the first; a level load 100 s and more
    const double ContinueGapMs = 2000;   // a slow frame with no create this close after startup (loading a save) is startup too
    const double ColdCompileMs = 100;    // measured: a compiled run's load creates stay under 100 ms, cold compiles' median is 159 ms

    sealed record Launch(long UnixMs, List<double> Ends);
    sealed record Create(double End, double Ms, char Kind, string Key);

    /// <summary>The report of the last launch with frames, or of the last whose csv launch names
    /// <paramref name="exeFileName"/>; null without frames. <paramref name="rayQuery"/>: RayQuery PSO keys, whose creates
    /// up to <see cref="SessionLog.RayQueryFloorMs"/> are the driver's floor, never a stutter.</summary>
    public static FrameReport? Read(string framesPath, string csvPath, string? exeFileName = null, IReadOnlySet<string>? rayQuery = null)
    {
        if (!File.Exists(framesPath)) return null;
        var launches = ReadFrames(framesPath);
        var sessions = ReadCreates(csvPath);
        for (int i = launches.Count - 1; i >= 0; i--)
        {
            var l = launches[i];
            if (l.Ends.Count < 2) continue;
            // the recorder stamps both in one D3D12CreateDevice call
            var s = sessions.Where(x => Math.Abs(x.UnixMs - l.UnixMs) < 10_000).OrderBy(x => Math.Abs(x.UnixMs - l.UnixMs)).FirstOrDefault();
            if (exeFileName != null && s != null && !s.Exe.Equals(exeFileName, StringComparison.OrdinalIgnoreCase)) continue;
            return Report(l, s?.Creates ?? [], rayQuery);
        }
        return null;
    }

    static FrameReport Report(Launch l, List<Create> creates, IReadOnlySet<string>? rayQuery)
    {
        creates.Sort((a, b) => a.End.CompareTo(b.End));
        double longest = creates.Count > 0 ? creates.Max(c => c.Ms) : 0;
        double startup = StartupEnd(creates);
        var frames = l.Ends.Zip(l.Ends.Skip(1), (a, b) => (Start: a, Ms: b - a)).ToList();
        double quit = l.Ends[^1] - QuitMs;
        var ends = creates.Select(c => c.End).ToList();
        var slow = new List<(double S, double Ms, int N, bool Shader, double Cold)>();
        foreach (var (s, ms) in frames.Where(f => f.Ms >= HitchMs))
        {
            int n = 0;
            bool shader = false;
            double cold = 0;
            // creates ending inside the frame or up to the longest create after it; the ones that started by its end overlap
            for (int j = LowerBound(ends, s); j < creates.Count && creates[j].End <= s + ms + longest; j++)
            {
                var c = creates[j];
                if (c.End - c.Ms > s + ms) continue;
                n++;
                bool floor = c.Ms <= SessionLog.RayQueryFloorMs && rayQuery?.Contains(c.Key) == true;
                shader |= c.Kind is 'R' or 'A' ? c.Ms >= SessionLog.StateObjectCompileMs : c.Ms >= BlockMs && !floor;
                if (c.Ms >= ColdCompileMs && !floor) cold += c.Ms;
            }
            slow.Add((s, ms, n, shader, cold));
        }
        foreach (var f in slow)
            if (f.N == 0 && f.S <= startup + ContinueGapMs && f.S + f.Ms > startup) startup = f.S + f.Ms;
        var hitches = new List<Hitch>();
        foreach (var (s, ms, n, shader, cold) in slow)
        {
            bool compiles = cold >= ms / 2;   // summed over threads
            var cause = s + ms <= startup ? compiles ? HitchCause.LoadingShaders : HitchCause.Loading
                : s >= quit ? HitchCause.Quitting
                : compiles ? HitchCause.Shader   // a load in play that compiles: the shader cost the player feels
                : n >= LoadCreates ? HitchCause.Loading : shader ? HitchCause.Shader : HitchCause.Other;
            if (cause == HitchCause.Other && ms >= PauseMs) continue;
            hitches.Add(new Hitch(TimeSpan.FromMilliseconds(s), ms, cause));
        }
        var play = frames.Where(f => f.Start >= startup && f.Start < quit && f.Ms < PauseMs).Select(f => f.Ms).OrderDescending().ToList();
        var slowest = play.Take(Math.Max(1, play.Count / 100)).ToList();
        double low = slowest.Count > 0 ? 1000 * slowest.Count / slowest.Sum() : 0;
        var peaks = new float[GraphColumns];
        foreach (var (s, ms) in frames)
        {
            int c = Math.Clamp((int)(s / l.Ends[^1] * GraphColumns), 0, GraphColumns - 1);
            peaks[c] = Math.Max(peaks[c], (float)ms);
        }
        return new FrameReport(TimeSpan.FromMilliseconds(l.Ends[^1]), TimeSpan.FromMilliseconds(Math.Min(startup, l.Ends[^1])),
            frames.Count, low, hitches, peaks);
    }

    /// <summary>The end of the startup: from the first create, the first 3 s with fewer than <see cref="StartupQuietCreates"/>
    /// creates; a burst (a second of <see cref="BurstCreates"/>) within <see cref="BurstGapMs"/> after that is startup too.</summary>
    static double StartupEnd(List<Create> creates)
    {
        var perSecond = new Dictionary<long, int>();
        foreach (var c in creates) perSecond[(long)(c.End / 1000)] = perSecond.GetValueOrDefault((long)(c.End / 1000)) + 1;
        if (perSecond.Count == 0) return 0;
        long last = perSecond.Keys.Max();
        long Quiet(long from)
        {
            for (long s = from; s <= last; s++)
                if (perSecond.GetValueOrDefault(s) + perSecond.GetValueOrDefault(s + 1) + perSecond.GetValueOrDefault(s + 2) < StartupQuietCreates) return s;
            return last;
        }
        long end = Quiet(perSecond.Keys.Min());
        for (long b; (b = perSecond.Keys.Where(k => k > end && k <= end + (long)(BurstGapMs / 1000) && perSecond[k] >= BurstCreates).DefaultIfEmpty(-1).Min()) >= 0;)
            end = Quiet(b);
        return end * 1000.0;
    }

    static int LowerBound(List<double> v, double x)
    {
        int i = v.BinarySearch(x);
        if (i < 0) return ~i;
        while (i > 0 && v[i - 1] >= x) i--;
        return i;
    }

    /// <summary>u32 records: 0xFFFFFFFF + u64 unix_ms, u64 us since the recorder loaded, u64 QPC, u64 QPC frequency opens a
    /// launch; top 4 bits 0-14 = a frame of that swap chain, the low 28 bits the microseconds since the previous record;
    /// top 4 bits 15 = no frame for the low 28 bits' milliseconds. Frames of the launch's busiest swap chain only.</summary>
    static List<Launch> ReadFrames(string path)
    {
        var launches = new List<Launch>();
        byte[] data;
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[f.Length];
            f.ReadExactly(data);
        }
        long unix = 0, t = 0;
        double start = 0;
        Dictionary<uint, List<double>>? chains = null;
        void Close() { if (chains != null) launches.Add(new Launch(unix, chains.Values.MaxBy(c => c.Count) ?? [])); }
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            uint r = BitConverter.ToUInt32(data, i);
            if (r == 0xFFFFFFFF)
            {
                if (i + 36 > data.Length) break;
                Close();
                unix = BitConverter.ToInt64(data, i + 4);
                start = BitConverter.ToInt64(data, i + 12) / 1000.0;
                (t, chains) = (0, []);
                i += 32;
            }
            else if (chains == null) continue;
            else if (r >> 28 == 15) t += (r & 0x0FFFFFFF) * 1000L;
            else
            {
                t += r & 0x0FFFFFFF;
                if (!chains.TryGetValue(r >> 28, out var c)) chains[r >> 28] = c = [];
                c.Add(start + t / 1000.0);
            }
        }
        Close();
        return launches;
    }

    sealed record CsvSession(long UnixMs, string Exe, List<Create> Creates);

    static List<CsvSession> ReadCreates(string path)
    {
        var sessions = new List<CsvSession>();
        if (!File.Exists(path)) return sessions;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            if (f[0] == "#session")
            {
                long.TryParse(f.Length > 1 ? f[1] : "", CultureInfo.InvariantCulture, out var ms);
                sessions.Add(new CsvSession(ms, f.Length > 2 ? string.Join(',', f[2..]).Trim() : "", []));
            }
            else if (!line.StartsWith('#') && sessions.Count > 0 && f.Length >= 5 && f[1].Length == 1
                     && double.TryParse(f[0], CultureInfo.InvariantCulture, out var end) && double.TryParse(f[4], CultureInfo.InvariantCulture, out var cms))
                sessions[^1].Creates.Add(new Create(end, cms, char.ToUpperInvariant(f[1][0]), f.Length > 5 ? f[5] : ""));
        }
        return sessions;
    }
}
