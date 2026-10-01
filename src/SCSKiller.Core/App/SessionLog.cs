using System.Globalization;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>The exe file name a game's process was launched with (the case the driver keys its cache on), seen at
/// <see cref="At"/>.</summary>
public sealed record LaunchedExe(string Name, DateTimeOffset At);

/// <summary>Reads the proxy's scskiller_creates.csv (t_ms,kind,known,tuple_known,ms, then in newer proxies key,proxy_ms,
/// which this reader ignores; appended per game launch, t_ms restarts at each launch). Lowercase kind = loaded from the
/// game's own pipeline library; a create over 3 ms is a real driver compile (but a RayQuery PSO's floor, see
/// <see cref="RayQueryFloorMs"/>), under it a cache hit. Kinds 'R' / 'A' (ray
/// tracing state objects) are counted apart: compiled from <see cref="StateObjectCompileMs"/>, else ready. Newer proxies bracket each launch with <c>#session,&lt;unix_ms&gt;,&lt;exe&gt;</c> and <c>#end,&lt;unix_ms&gt;</c>
/// (missing after a crash, and whenever the game terminates its own process, as Unreal does); other <c>#</c> lines are ignored. The #session exe is
/// GetModuleFileNameW(NULL)'s file name inside the game process: the name exactly as launched (measured: a process started
/// as CASEPROBE.EXE from the file caseProbe.exe has CASEPROBE.EXE there, while its kernel image name and
/// QueryFullProcessImageName say caseProbe.exe), the case AMD's cache key uses.</summary>
public static class SessionLog
{
    const double CompileMs = 3.0;

    sealed class Session
    {
        public long? Start, End;
        public bool OtherExe;
        public long Rows, Library, Hits, Compiles, RayQuery, SoReady, SoCompiled;
        public double LastT = double.NegativeInfinity, Worst;

        // Play time = end - start. Without an #end (a crash, an older proxy, or a game that terminates itself, as Unreal
        // does, so DLL_PROCESS_DETACH never runs): the watched exit of the run this launch started in, else the last create's t_ms.
        public SessionStats Stats(PlayWindow? played)
        {
            double ms = Rows > 0 ? LastT : 0;
            if (Start is { } s && End is { } e && e >= s) ms = e - s;
            else if (Start is { } st && played is { } p && p.From.ToUnixTimeMilliseconds() <= st && st <= p.To.ToUnixTimeMilliseconds())
                ms = Math.Max(ms, p.To.ToUnixTimeMilliseconds() - st);
            return new(TimeSpan.FromMilliseconds(ms), Rows, Library, Hits, Compiles, Worst, RayQuery, SoReady, SoCompiled);
        }
    }

    /// <summary>NVIDIA recompiles part of a cached RayQuery PSO at every create: about 7-15% of its cold create, 9-35 ms
    /// for an Unreal 5.6 game's, whose cold creates take 39-466 ms (selftest bindless). Up to this it is that floor; above
    /// it the cache missed. In play both stretch: SILENT HILL: Townfall's floor 12-88 ms (1-2% over 60), its cold creates
    /// 75 ms and up, none of 1,193 under 60.</summary>
    public const double RayQueryFloorMs = 60.0;

    /// <summary>A ray tracing state object create this long or longer compiled; shorter, it came from the driver cache. A
    /// cached one still costs about 1.5 ms per shader or collection (ARCHITECTURE.md). Hogwarts Legacy, the same objects in a
    /// cold and a compiled run: cached creates p90 23 ms, p99 57 ms (3 of 875 over 60); cold ones 60 ms and more for 672
    /// of 875. Small objects compile faster, so this undercounts compiles rather than calling a cached object compiled.</summary>
    public const double StateObjectCompileMs = 60.0;

    /// <summary>The last launch's stats and the exe name of the last <c>#session</c> marker (null without markers); with
    /// <paramref name="exeFileName"/>, of the last marker naming that exe apart from case (another exe of the folder may
    /// have loaded the proxy too). And the first launch that started after <paramref name="firstAfter"/>
    /// and ended (its <c>#end</c>, or a later launch) with at least <paramref name="minCreates"/> hits and compiles
    /// (<see cref="LaunchCheck"/>); of <paramref name="exeFileName"/> when given. Launches without a <c>#session</c> marker
    /// have no start time: never that one. <paramref name="rayQuery"/>: keys of PSOs SCSKiller compiled whose shaders trace
    /// rays inline (<see cref="WriteRayQueryKeys"/>); a create of one over 3 ms and up to <see cref="RayQueryFloorMs"/>
    /// counts in <see cref="SessionStats.RayQueryRecompiles"/> only. Null: every create is a hit or a compile.
    /// <paramref name="played"/>: the game's last watched run, the play time of a launch without <c>#end</c> that started in it.</summary>
    public static (SessionStats? Last, LaunchedExe? Exe, LaunchCheck? First) Read(string csvPath, string? exeFileName = null,
        DateTimeOffset? firstAfter = null, long minCreates = 1, IReadOnlySet<string>? rayQuery = null, PlayWindow? played = null)
    {
        if (!File.Exists(csvPath)) return (null, null, null);
        Session? cur = null;
        LaunchedExe? exe = null;
        LaunchCheck? first = null;
        void Ended(Session? s)
        {
            if (first == null && firstAfter is { } a && s is { Start: { } st, OtherExe: false } && st > a.ToUnixTimeMilliseconds() && s.Hits + s.Compiles >= minCreates)
                first = new LaunchCheck(DateTimeOffset.FromUnixTimeMilliseconds(st), s.Hits, s.Compiles);
        }
        using var reader = new StreamReader(new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            if (line.StartsWith('#'))
            {
                long ms = f.Length > 1 && long.TryParse(f[1], CultureInfo.InvariantCulture, out var x) ? x : 0;
                if (f[0] == "#session")
                {
                    Ended(cur);
                    cur = new Session { Start = ms };
                    // the name may hold commas; the proxy writes it through the C locale, so a non-ASCII name comes out mangled:
                    // callers only accept it when it matches the install's exe name apart from case
                    var name = f.Length > 2 ? string.Join(',', f[2..]).Trim() : "";
                    cur.OtherExe = exeFileName != null && !name.Equals(exeFileName, StringComparison.OrdinalIgnoreCase);
                    if (ms > 0 && name.Length > 0 && !cur.OtherExe) exe = new LaunchedExe(name, DateTimeOffset.FromUnixTimeMilliseconds(ms));
                }
                else if (f[0] == "#end" && cur != null) { cur.End = ms; Ended(cur); }
                continue;
            }
            if (f.Length < 5 || f[1].Length != 1 || !double.TryParse(f[0], CultureInfo.InvariantCulture, out var t)
                || !double.TryParse(f[4], CultureInfo.InvariantCulture, out var ms2)) continue;
            if (cur == null || cur.End != null || t < cur.LastT)   // no markers (older proxy): t_ms restarting = a new launch
            {
                if (cur?.End == null) Ended(cur);
                cur = new Session();
            }
            cur.Rows++;
            cur.LastT = t;
            if (!char.IsUpper(f[1][0])) { cur.Library++; continue; }
            if (f[1][0] is 'R' or 'A')
            {
                if (ms2 >= StateObjectCompileMs) cur.SoCompiled++; else cur.SoReady++;
                continue;
            }
            if (ms2 > CompileMs && ms2 <= RayQueryFloorMs && f.Length > 5 && rayQuery?.Contains(f[5]) == true) { cur.RayQuery++; continue; }
            if (ms2 > CompileMs) cur.Compiles++; else cur.Hits++;
            cur.Worst = Math.Max(cur.Worst, ms2);
        }
        return (cur?.Stats(played), exe, first);
    }

    /// <summary>Writes the keys of the recording's PSOs with a shader that traces rays inline (SFI0's RayQuery flag), one
    /// per line; the recording must carry the shader bytes (rehydrated).</summary>
    public static void WriteRayQueryKeys(string recordingDb, string keysFile)
    {
        var rayQuery = new HashSet<string>();
        var psos = new List<(string Key, ICollection<string> Stages)>();
        foreach (var r in PsoDb.Read(recordingDb))
            if (r.Tag == 'B') { if (r.Payload.Length > 20 && Dxbc.InlineRayTracing(r.Payload.AsSpan(20))) rayQuery.Add(PsoDb.Hex(r.Payload.AsSpan(0, 20))); }
            else if (r.Tag is 'G' or 'C' or 'S')
                try { psos.Add((r.Key, PsoDb.Parse(r).Stages.Values)); }
                catch (Exception e) when (e is InvalidDataException or ArgumentException or KeyNotFoundException) { }   // a record from a newer proxy
        File.WriteAllLines(keysFile, psos.Where(p => p.Stages.Any(rayQuery.Contains)).Select(p => p.Key));
    }

    /// <summary>What <see cref="WriteRayQueryKeys"/> wrote; null without the file.</summary>
    public static IReadOnlySet<string>? ReadRayQueryKeys(string keysFile) => File.Exists(keysFile) ? File.ReadAllLines(keysFile).ToHashSet() : null;
}
