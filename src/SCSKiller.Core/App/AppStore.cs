using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SCSKiller.Core.App;

/// <summary>What SCSKiller remembers per game (games\&lt;id&gt;\state.json).</summary>
public sealed class GameRecord
{
    public string? IndexContentHash { get; set; }
    public string? IndexGameVersion { get; set; }           // Game.Version when IndexContentHash was taken (sharing needs the pair)
    public string? IndexExeStamp { get; set; }              // exe size + write time when IndexContentHash was taken
    public string? KeysIndexHash { get; set; }              // the index whose shaders the recorder's keys file names; null: none
    public int? ShaderCount { get; set; }
    public Plan? Plan { get; set; }
    public DateTimeOffset? PlanBuiltAt { get; set; }
    public int PlanVersion { get; set; }                     // Planner.Version the plan was built with
    public int WarmedPlanVersion { get; set; }               // PlanVersion of the plan the last complete warm replayed
    public string? PlanItems { get; set; }                   // ScsKiller.PlanFingerprint of plan.bin's records (null: not taken, or not readable)
    public string? WarmedPlanItems { get; set; }             // PlanItems of the plan the last complete warm replayed
    public long? PlanNewItems { get; set; }                  // records of plan.bin the last complete warm didn't replay; null = not known
    public bool PlanPerStage { get; set; }                  // the plan has each stage unit once, not every pairing (per-stage cache, not Maximum)
    public bool WarmedPerStage { get; set; }                // PlanPerStage of the plan the last complete warm replayed
    public string? PlanMiddleware { get; set; }             // MiddlewarePacks.Fingerprint when the plan was built (DLL versions + pack sizes)
    public string? PlanCommunity { get; set; }              // the community recording (object id) the plan was built with; null = none
    public long ResumeAt { get; set; }                       // Done of a stopped warm of the current plan
    public long ResumeItems { get; set; }                    // items the stopped segments of that warm created...
    public double ResumeSeconds { get; set; }                // ...and their time: a complete warm's time and rate include them
    public string? WarmedDriverVersion { get; set; }
    public DateTimeOffset? WarmedAt { get; set; }
    public TimeSpan? LastWarmTime { get; set; }
    public long? LastCacheGrowthBytes { get; set; }
    public long? LastWarmFailed { get; set; }                // the last complete warm: PSOs the driver rejected
    public long? LastWarmSkipped { get; set; }               // ...and PSOs skipped: a shader not in this install (never replayed)
    public long? LastWarmNeedsRecording { get; set; }        // ...of those, flagged by the community recording: only a recording here has them
    public long? LastWarmCrashed { get; set; }               // ...and items skipped because they crash the GPU driver
    public HashSet<string> CrashKeys { get; set; } = [];     // record keys of items whose create removed the D3D12 device: every warm skips them
    public string? CrashKeysDriver { get; set; }             // the driver they crashed: a warm on another driver clears them (one retry each)
    public string? WarmedIndexHash { get; set; }
    public string? WarmedExeStamp { get; set; }             // exe size + write time at the warm: a game patch changes it
    public string? WarmedGameVersion { get; set; }          // Game.Version (store build id) at the warm; preferred over the exe stamp
    public double? BytesPerPso { get; set; }                // measured by the last complete warm onto a cold cache (ScsKiller.ColdWarm)
    public double? PsoPerSecond { get; set; }               // ...and this
    public DateTimeOffset? RecordingImportedAt { get; set; }  // when an import last added records
    public string? RecordingInbox { get; set; }             // the game folder's scskiller.db (size:write ticks) when last imported
    public string? RecordingIndexHash { get; set; }         // the index build whose shaders the recording names by hash only; null: not checked since an import
    public long RecordedSinceWarm { get; set; }             // pipeline records the imports added since the last complete warm that its plan lacks
    public Dictionary<string, string> RecorderFiles { get; set; } = [];   // file name in the exe folder -> SHA-256 we installed
    public string? RecorderExe { get; set; }                 // the game's exe while a recorder is installed: uninstall finds it without a scan
    // null = not migrated: the first reconcile makes an installed recorder of ours On (the user put it there), else Default
    public RecorderOverride? Recorder { get; set; }
    public bool RecordAlongsideMod { get; set; }             // opt-in: install the recorder where a mod's d3d12.dll is, chained to it
    public ChainedDll? RecorderChained { get; set; }         // that mod's d3d12.dll, renamed for the chain: put back when the recorder goes
    public HashSet<string> CacheKeys { get; set; } = [];     // driver-cache application keys seen open by this game's warms or the game (IAppCache)
    public HashSet<string> GameKeys { get; set; } = [];      // ...of them, the ones the game's own process held open
    public HashSet<string>? WarmedKeys { get; set; }         // ...the ones the last complete warm held open; null = not recorded
    public HashSet<string>? WarmedFiles { get; set; }        // AMD: names of those keys' D3D12 cache files after that warm; null = not recorded
    // The exe file name exactly as the game's own process was launched (its case can differ from the file on disk, and AMD's
    // cache key is case-sensitive): from the recorder's #session marker or the running game's module path; null = not seen.
    // Only kept when it equals the install's exe file name apart from case. Warms stage this name (ScsKiller.WarmExeName).
    public string? LaunchedExeName { get; set; }
    public DateTimeOffset? LaunchedExeSeenAt { get; set; }  // when LaunchedExeName was seen: a later sighting replaces it
    public string? WarmedExeName { get; set; }              // the name the last complete warm staged; null = the install's file name
    public string? WarmedAgsApp { get; set; }               // AMD: the AGS app name the last complete warm registered; null = a plain device
    public bool AgsMissed { get; set; }                     // a launch after such a warm showed it missed the game: warms stay plain until the game is seen holding the AGS key
    public bool Careful { get; set; }                       // AMD: compile in passes on few threads (ScsKiller.CarefulThreads)
    public bool WarmedCareful { get; set; }                 // the last complete warm was careful
    public LaunchCheck? FirstLaunch { get; set; }           // the game's first launch after the last complete warm (AMD); null = not yet
    public PlayWindow? LastPlay { get; set; }               // the last run of the game the app watched from start to exit
}

/// <summary>A run of the game as the app's watcher saw it: not running at <paramref name="From"/>, last seen running at
/// <paramref name="To"/> (both within a poll of the real start and exit).</summary>
public sealed record PlayWindow(DateTimeOffset From, DateTimeOffset To);

/// <summary>A launch's driver creates from the recorder's csv (<see cref="SessionLog"/>): cache hits and compiles, started at
/// <paramref name="At"/>.</summary>
public sealed record LaunchCheck(DateTimeOffset At, long Hits, long Compiles)
{
    public double Compiled => Hits + Compiles > 0 ? (double)Compiles / (Hits + Compiles) : 0;
}

/// <summary>A mod's d3d12.dll renamed to <paramref name="Name"/> in the exe folder, with the SHA-256 of its bytes.</summary>
public sealed record ChainedDll(string Name, string Sha256);

/// <summary>The expensive part of a scan (engine detection, planner check, anti-cheat), reused while <see cref="Key"/>
/// (exe stamp, store version, vendor profile, recording, SCSKiller build) is unchanged.</summary>
public sealed record Evaluation(string Key, EngineInfo? Engine, AntiCheat AntiCheat, PlanCheck Check);

/// <summary>%LOCALAPPDATA%\SCSKiller: settings.json, scan.json, dismissed.json + games\&lt;id&gt;\state.json.</summary>
public sealed class AppStore(string dataDir)
{
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCSKiller");

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string DataDir { get; } = dataDir;

    public static Settings DefaultSettings => new(Environment.ProcessorCount, WarmPriority.BelowNormal,   // below-normal priority keeps the PC responsive on every thread
       
        DriverUpdateMode.Ask, BackgroundThreads: 8, PauseWhileGaming: true);

    public Settings LoadSettings() => Load<Settings>(Path.Combine(DataDir, "settings.json")) ?? DefaultSettings;
    public void SaveSettings(Settings s) => Save(Path.Combine(DataDir, "settings.json"), s);

    public Dictionary<string, Evaluation> LoadScan() => Load<Dictionary<string, Evaluation>>(Path.Combine(DataDir, "scan.json")) ?? [];
    public void SaveScan(Dictionary<string, Evaluation> scan) => Save(Path.Combine(DataDir, "scan.json"), scan);

    /// <summary>Game id -> the stale key the user skipped (driver + game version).</summary>
    public Dictionary<string, string> LoadDismissed() => Load<Dictionary<string, string>>(Path.Combine(DataDir, "dismissed.json")) ?? [];
    public void SaveDismissed(Dictionary<string, string> dismissed) => Save(Path.Combine(DataDir, "dismissed.json"), dismissed);

    /// <summary>Game id -> what the new-shaders notification last told about it (<see cref="NewShaders.Key"/>).</summary>
    public Dictionary<string, string> LoadNotified() => Load<Dictionary<string, string>>(Path.Combine(DataDir, "notified.json")) ?? [];
    public void SaveNotified(Dictionary<string, string> notified) => Save(Path.Combine(DataDir, "notified.json"), notified);

    /// <summary>Vendor -> warm rate (PSO/s) of the first complete warm measured on this PC, for games not warmed yet.</summary>
    public Dictionary<GpuVendor, double> LoadWarmRates() => Load<Dictionary<GpuVendor, double>>(Path.Combine(DataDir, "warmrates.json")) ?? [];
    public void SaveWarmRates(Dictionary<GpuVendor, double> rates) => Save(Path.Combine(DataDir, "warmrates.json"), rates);

    /// <summary>Update channel -> signed_at of the newest signed feed accepted (FeedTrust: no replay of an older feed).</summary>
    public Dictionary<string, DateTimeOffset> LoadFeedTimes() => Load<Dictionary<string, DateTimeOffset>>(Path.Combine(DataDir, "feeds.json")) ?? [];
    public void SaveFeedTimes(Dictionary<string, DateTimeOffset> times) => Save(Path.Combine(DataDir, "feeds.json"), times);

    public string GameDir(string gameId) => Path.Combine(DataDir, "games", gameId.Replace(':', '_'));

    // What each record handed out held when it was loaded or last saved: a save writes only what its holder changed since.
    readonly ConditionalWeakTable<GameRecord, JsonObject> _held = new();

    public GameRecord LoadGame(string gameId)
    {
        var r = Load<GameRecord>(Path.Combine(GameDir(gameId), "state.json")) ?? new GameRecord();
        _held.AddOrUpdate(r, JsonSerializer.SerializeToNode(r, Json)!.AsObject());
        return r;
    }

    /// <summary>Writes the fields <paramref name="r"/> changed since <see cref="LoadGame"/> (or its last save) onto the
    /// stored record, re-read under a lock shared by every process of this user: a path that holds a record for long (a
    /// compile) never puts back what another path saved meanwhile (a watched exit, a learned key). The record's sets
    /// merge by what was added and removed. A record that wasn't loaded here is written whole.</summary>
    public void SaveGame(string gameId, GameRecord r)
    {
        var path = Path.Combine(GameDir(gameId), "state.json");
        var now = JsonSerializer.SerializeToNode(r, Json)!.AsObject();
        using (var gate = new Mutex(false, "SCSKiller-state-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))))
        {
            try { gate.WaitOne(); }
            catch (AbandonedMutexException) { }   // its holder exited without releasing it: owned now all the same
            try
            {
                var stored = _held.TryGetValue(r, out var was) && Load<JsonObject>(path) is { } latest ? Merge(latest, was, now) : now;
                WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(stored, Json));
            }
            finally { gate.ReleaseMutex(); }
        }
        _held.AddOrUpdate(r, (JsonObject)now.DeepClone());
    }

    static JsonObject Merge(JsonObject latest, JsonObject was, JsonObject now)
    {
        foreach (var (name, value) in now)
        {
            var before = was[name];
            if (JsonNode.DeepEquals(before, value)) continue;
            if (value is JsonArray set && latest[name] is JsonArray other)   // GameRecord's arrays are all HashSet<string>
            {
                var (had, has) = (Strings(before), Strings(set));
                latest[name] = new JsonArray([.. Strings(other).Except(had.Except(has)).Union(has.Except(had)).Select(s => (JsonNode)JsonValue.Create(s))]);
            }
            else latest[name] = value?.DeepClone();
        }
        return latest;

        static HashSet<string> Strings(JsonNode? a) => a is JsonArray items ? [.. items.Select(i => i!.GetValue<string>())] : [];
    }

    static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            // shared with the app, the CLI and the sign-in task: a reader must not block another process's replace
            using var f = Retry(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return JsonSerializer.Deserialize<T>(f, Json);
        }
        catch (JsonException) { return null; }   // corrupt file: start over rather than refuse to run
    }

    static void Save<T>(string path, T value) => WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));

    /// <summary>Replaces the file through a temp file next to it: a reader in another process (the app, the CLI, the
    /// sign-in task) sees the old file or the new one, never half of one.</summary>
    internal static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";   // per writer: two processes may save at once
        File.WriteAllBytes(tmp, bytes);
        try { Retry(() => { File.Move(tmp, path, overwrite: true); return 0; }); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    /// <summary>Another process may be replacing the file right now: short retries for 2.5 s, then the error.</summary>
    static TR Retry<TR>(Func<TR> f)
    {
        // measured: 400 parallel saves and loads of one file wait up to 330 ms
        for (var i = 0; ; i++)
            try { return f(); }
            catch (Exception e) when (i < 100 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(25); }
    }
}
