using SCSKiller.Core;
using SCSKiller.Core.App;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

public class FrameLogTests(ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-frames-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A launch record (unix ms, us since the recorder loaded) and frames that return at <paramref name="endsMs"/>,
    /// as the proxy writes them: microsecond deltas, a skip record for a gap past 28 bits.</summary>
    static byte[] Launch(long unixMs, long loadedUs, IEnumerable<double> endsMs, uint chain = 0)
    {
        var b = new List<byte>();
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        U32(0xFFFFFFFF);
        foreach (var v in new[] { unixMs, loadedUs, 123456789L, 10_000_000L }) b.AddRange(BitConverter.GetBytes(v));
        long last = loadedUs;
        foreach (var e in endsMs)
        {
            long us = (long)Math.Round(e * 1000), d = us - last;
            last = us;
            while (d >> 28 != 0) { long ms = d / 1000; U32(0xF0000000u | (uint)ms); d -= ms * 1000; }
            U32(chain << 28 | (uint)d);
        }
        return [.. b];
    }

    static IEnumerable<double> Every10Ms(double from, double to) { for (var t = from; t < to; t += 10) yield return t; }

    /// <summary>Slow frames get their cause from the creates that overlap them on the recorder's clock: a compile of 10 ms or
    /// more (not a RayQuery PSO at the driver's floor, not a ray tracing state object under SessionLog.StateObjectCompileMs) is a shader stutter,
    /// none is another hitch, the startup (with a burst following it closely and a slow frame with no create right after it) or
    /// a load in play of 100 fast creates or more is loading; cold compiles filling a frame make it "loading, compiling
    /// shaders" in startup and a shader stutter in play, one in
    /// the last 10 s is quitting, and a frame of 5 s or more
    /// with no create is a pause, left out. The last launch of the game's exe is reported; a later one of another exe isn't.</summary>
    [Fact]
    public void Slow_frames_are_shader_compiles_other_hitches_or_loading()
    {
        var csv = new List<string> { "#session,1000000,Game.exe" };
        for (int i = 0; i < 200; i++) csv.Add($"{10 * i:0.0},S,1,1,50.000,{i:x40},0.010,7,0");   // startup: 0-2 s
        for (int i = 0; i < 5; i++) csv.Add($"{600 + 20 * i:0.0},S,1,1,200.000,{i + 4000:x40},0.010,7,0");   // cold compiles in its first slow frame
        csv.Add("15040.0,S,1,1,60.000,aa,0.010,8,0");    // compile inside the 15000 ms frame
        csv.Add("18040.0,C,1,1,40.000,bb,0.010,8,0");    // RayQuery floor inside the 18000 ms frame
        csv.Add("27040.0,R,1,1,50.000,cc,0.010,8,0");    // a cached state object inside the 27000 ms frame
        csv.Add("23040.0,A,1,1,70.000,dd,0.010,8,0");    // a state object compiled inside the 23000 ms frame
        for (int i = 0; i < 120; i++) csv.Add($"{24100 + i:0.0},S,1,1,1.000,{i + 1000:x40},0.010,9,0");   // a load
        for (int i = 0; i < 110; i++) csv.Add($"{25100 + i:0.0},S,1,1,150.000,{i + 3000:x40},0.010,9,0");   // a load in play that compiles
        for (int i = 0; i < 150; i++) csv.Add($"{7100 + i * 0.5:0.0},S,1,1,1.000,{i + 2000:x40},0.010,9,0");   // a second startup burst
        csv.Add("#session,1000000000,Other.exe");
        File.WriteAllLines(Path.Combine(_dir, "scskiller_creates.csv"), csv);

        var ends = new List<double>();
        void Frame(double ms) => ends.Add(ends[^1] + ms);
        ends.Add(500);
        Frame(500);                                             // at 500: startup
        foreach (var (at, ms) in new[] { (8500.0, 700.0), (15000, 80), (18000, 70), (21000, 120), (23000, 90), (24000, 300), (25000, 400), (27000, 60), (29000, 400_000), (429_500, 80) })
        {
            foreach (var t in Every10Ms(ends[^1] + 10, at + 0.5)) ends.Add(t);
            ends[^1] = at;
            Frame(ms);
        }
        foreach (var t in Every10Ms(ends[^1] + 10, ends[^1] + 1000)) ends.Add(t);
        var bin = Path.Combine(_dir, FrameLog.FileName);
        File.WriteAllBytes(bin, [.. Launch(999_000, 100_000, [200, 300, 400]), .. Launch(1_000_050, 0, ends),
            .. Launch(1_000_000_010, 0, [1, 2, 3])]);

        var r = FrameLog.Read(bin, Path.Combine(_dir, "scskiller_creates.csv"), "game.exe", new HashSet<string> { "bb" })!;
        Assert.Equal(TimeSpan.FromMilliseconds(9200), r.Startup);   // the burst 5 s after the first quiet second, then a frame with no create
        Assert.Equal(ends.Count - 1, r.Frames);
        Assert.Equal(TimeSpan.FromMilliseconds(ends[^1]), r.Duration);
        Assert.Equal([(500.0, 500.0, HitchCause.LoadingShaders), (8500, 700, HitchCause.Loading), (15000, 80, HitchCause.Shader), (18000, 70, HitchCause.Other),
            (21000, 120, HitchCause.Other), (23000, 90, HitchCause.Shader), (24000, 300, HitchCause.Loading), (25000, 400, HitchCause.Shader), (27000, 60, HitchCause.Other),
            (429_500, 80, HitchCause.Quitting)],
            r.Hitches.Select(h => (Math.Round(h.At.TotalMilliseconds, 3), Math.Round(h.Ms, 3), h.Cause)));
        var play = ends.Zip(ends.Skip(1), (a, b) => (a, ms: b - a)).Where(f => f.a >= 9200 && f.a < ends[^1] - 10_000 && f.ms < 5000).Select(f => f.ms).OrderDescending().ToList();
        var slow = play.Take(play.Count / 100).ToList();
        Assert.Equal(1000 * slow.Count / slow.Sum(), r.Low1PctFps, 6);
        Assert.Equal(FrameLog.GraphColumns, r.Peaks.Count);
        Assert.Equal(300, r.Peaks[(int)(24000 / ends[^1] * FrameLog.GraphColumns)], 3);   // each slice keeps its longest frame

        Assert.Null(FrameLog.Read(Path.Combine(_dir, "none.bin"), Path.Combine(_dir, "scskiller_creates.csv")));
    }

    /// <summary>SILENT HILL: Townfall's recorder files, read only: the last launch has frames and at least one hitch.</summary>
    [Trait("Needs", "Game")]
    [Fact]
    public void Townfall_last_launch()
    {
        var game = new SCSKiller.Core.Games.SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:1636440");
        var dir = game == null ? "" : Path.GetDirectoryName(game.ExePath)!;
        if (!File.Exists(Path.Combine(dir, FrameLog.FileName))) return;
        var r = FrameLog.Read(Path.Combine(dir, FrameLog.FileName), Path.Combine(dir, "scskiller_creates.csv"), Path.GetFileName(game!.ExePath))!;
        output.WriteLine($"{r.Duration}, startup {r.Startup}, {r.Frames} frames, 1% low {r.Low1PctFps:0.0}");
        foreach (var h in r.Hitches) output.WriteLine($"  {h.At:mm\\:ss\\.f} {h.Ms,8:0.0} ms {h.Cause}");
        Assert.True(r.Frames > 0);
    }
}
