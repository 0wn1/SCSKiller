using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SCSKiller.Core.Carved;
using SCSKiller.Core.FromSoft;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Core.Unity;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Core.App;

/// <summary>The facade the GUI and CLI use: discovery, per-game state, the sequential compile queue, the recorder.</summary>
public sealed class ScsKiller : IScsKiller
{
    public const double DefaultBytesPerPso = 24 * 1024, DefaultPsoPerSecond = 450;

    /// <summary>What compiling the game adds to the driver cache: its estimate less what its keys already hold.</summary>
    public static long CacheGrowth(GameState s) => Math.Max(0, (s.EstimatedCacheBytes ?? 0) - (s.CacheOnDisk ?? 0));
    static readonly byte[] ProxyMarker = "SCSKiller_StartWarm"u8.ToArray();   // an export only our proxy d3d12.dll has
    const string RecorderIni = "[scskiller]\r\n; written by SCSKiller: record the pipelines this game creates. Removed by 'uninstall recorder'.\r\nmode=record\r\n";
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500), AttributionInterval = TimeSpan.FromSeconds(3);
    // A new SCSKiller version may detect engines or plan differently: cached scan results from another one are redone.
    // "1.0.0+<commit>": the same for the app and the CLI built from one commit (their Core.dll bytes differ, so no MVID).
    static readonly string CoreBuild = typeof(ScsKiller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    readonly IReadOnlyList<IGameSource> _sources;
    readonly IEngineReader _reader;
    readonly IPlanner _planner;
    readonly IWarmer _warmer;
    readonly string? _proxyDll;
    readonly object _lock = new(), _scanLock = new();
    readonly List<QueueItem> _queue = [];
    readonly HashSet<string> _whenIdle = [];         // queued with EnqueueWhenIdle
    readonly HashSet<string> _planOnly = [];         // queued by CheckPlans: rebuild the plan, no warm
    readonly ManualResetEventSlim _go = new(true);   // reset = queue paused
    List<GameState> _games = [];
    Dictionary<string, Evaluation>? _scan;
    Settings? _settings;
    Task? _worker;
    bool _running;                                   // StartQueue was called and normal (not "when idle") items are left
    string? _current;
    QueueStage _stage;
    IWarmRun? _run;
    CancellationTokenSource? _itemCts;
    volatile string? _pauseWhy;                      // why Watch suspended the running warm (game running, user at the PC)
    // How running games' processes were launched, by exe file name (case-insensitive), when it differs from the install's
    // file name in case: noted by Running(), taken into the game's record by MergeLaunched.
    readonly ConcurrentDictionary<string, LaunchedExe> _launched = new(StringComparer.OrdinalIgnoreCase);

    public ScsKiller(IEnumerable<IGameSource> sources, IGpuVendorBackend vendor, IEngineReader reader, IPlanner planner, IWarmer warmer,
        string dataDir, string? proxyDll)
    {
        _sources = sources.ToList();
        Vendor = vendor;
        _reader = reader;
        _planner = planner;
        _warmer = warmer;
        Store = new AppStore(dataDir);
        if (StutterList.Cached(StutterFile) is { } cached) StutterList.Current = cached;
        if (ConfirmedEngines.Cached(ConfirmedFile) is { } confirmed) ConfirmedEngines.Current = confirmed;
        _proxyDll = proxyDll;
        AppCache = vendor.AppCache;
        RunningGameExes = DiscoveredGamesRunning;
    }

    public static ScsKiller CreateDefault()
    {
        var vendor = GpuBackends.Detect();
        return new ScsKiller([new SteamSource(), new EpicSource(), new XboxSource(), new GogSource(), new UbisoftSource(), new BattleNetSource(), new EaSource()],
            vendor, DefaultReaders(), new Planner(Path.Combine(AppStore.DefaultDir, "packs")), new Warmer(vendor),
            AppStore.DefaultDir, NativeTools.Find("d3d12.dll"));
    }

    /// <summary>Unreal first, then the engines whose archives the carver can't see into (FromSoftware, Unity, RE Engine), then
    /// the generic raw DXBC/DXIL carver.</summary>
    public static IEngineReader DefaultReaders() =>
        new EngineReaders(("Unreal", new UnrealReader(AppStore.DefaultDir)), (FromSoftReader.Family, new FromSoftReader(AppStore.DefaultDir)),
            (UnityReader.Family, new UnityReader()), (ReEngine.ReEngineReader.Family, new ReEngine.ReEngineReader(AppStore.DefaultDir)),
            (CarvedReader.Family, new CarvedReader()));

    public IGpuVendorBackend Vendor { get; }
    public AppStore Store { get; }

    /// <summary>Background runs (scheduled re-warm, <c>compile --idle</c>): idle priority, Settings.BackgroundThreads, and
    /// pause while another discovered game is running if Settings.PauseWhileGaming (a warm always stops while its own
    /// game runs and continues afterwards, whatever the setting). Items queued with EnqueueWhenIdle are
    /// background runs regardless.</summary>
    public bool Background { get; set; }
    public int? ThreadsOverride { get; set; }
    public IProgress<string>? Log { get; set; }
    /// <summary>After a scan, queue a "when idle" plan rebuild (no warm) for each warmed game whose plan an older planner
    /// built (<see cref="Planner.Version"/>): it tells whether the new planner compiles anything the warm didn't. The app
    /// sets it; the CLI doesn't (its queue waits for what it was asked to do).</summary>
    public bool CheckPlans { get; set; }
    /// <summary>Only the app sets it: the CLI and the scheduled task never install recorders.</summary>
    public bool ManageRecorders { get; set; }
    /// <summary>No extension, case-insensitive; no process is opened. Replaceable for tests.</summary>
    public Func<IReadOnlySet<string>> ProcessNames { get; set; } = RunningProcessNames;
    /// <summary>The driver cache's per-application files, for attributing and clearing a game's share; null = the vendor's
    /// cache isn't per application. Replaceable for tests.</summary>
    public IAppCache? AppCache { get; set; }
    /// <summary>Roots of D3DSCache and of the folders games write their own shader caches in. Replaceable for tests.</summary>
    public string LocalAppData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string MyGames { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
    public string ProgramData { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    /// <summary>The community database's read side (the app sets it: it holds the sign-in); null = local only. What was
    /// downloaded is used either way while Settings.UseCommunityDb is on.</summary>
    public Community? Community { get; set; }
    /// <summary>The last scan's community pass (manifest check and downloads), which runs after the scan returns.</summary>
    public Task CommunitySync { get; private set; } = Task.CompletedTask;
    /// <summary>Anonymous uploads of this PC's recordings while Settings.ShareRecordings is on (the app sets it); null = none.</summary>
    public Sharing? Sharing { get; set; }
    /// <summary>The queued sharing passes (<see cref="StartSharing"/>), one at a time.</summary>
    public Task SharingPass { get; private set; } = Task.CompletedTask;
    /// <summary>The anonymous daily check after a scan (the app sets it); null = none.</summary>
    public ActiveCheck? ActiveCheck { get; set; }
    /// <summary>The last scan's <see cref="ActiveCheck"/>, which runs after the scan returns.</summary>
    public Task ActiveCheckSent { get; private set; } = Task.CompletedTask;
    /// <summary>Where the known-stutter and confirmed-engines lists are fetched from after a scan (the app sets it); null = the cached or embedded lists only.</summary>
    public RouteFailover? ContentRoutes { get; set; }
    /// <summary>The last scan's check of both lists, which runs after the scan returns.</summary>
    public Task StutterUpdate { get; private set; } = Task.CompletedTask;

    /// <summary>How long the user has been away from keyboard and mouse (GetLastInputInfo). Replaceable for tests.</summary>
    public Func<TimeSpan> IdleTime { get; set; } = UserIdleTime;
    /// <summary>"When idle" items start, and resume, once the user has been away this long; input pauses them within a second.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>A warm whose done count hasn't moved for this long (not paused) shows "no progress for N min" instead of
    /// an estimate. Replaceable for tests.</summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(60);

    public Settings Settings
    {
        get => _settings ??= Store.LoadSettings() is var s && s.RecordingLimitMB > 0
            ? s with { RecordingLimitMB = RecordingLimits.FirstOrDefault(l => l >= s.RecordingLimitMB) } : s;   // a size that is no choice: the next one up, or unlimited
        set
        {
            var maximumChanged = value.MaximumPlans != Settings.MaximumPlans;
            var communityOn = value.UseCommunityDb && !Settings.UseCommunityDb;
            var shareOn = value.ShareRecordings && !Settings.ShareRecordings;
            var welcomed = value.WelcomeSeen && !Settings.WelcomeSeen;   // the first scan ran while the welcome was open
            var limitChanged = value.RecordingLimitMB != Settings.RecordingLimitMB;
            var recordersChanged = value.RecordAllGames != Settings.RecordAllGames || limitChanged;
            maximumChanged |= value.UseCommunityDb != Settings.UseCommunityDb;   // the recording planned from, too
            Store.SaveSettings(value);
            _settings = value;
            // Stale reasons (and RecordingPaused) depend on it. Re-evaluated off the caller's thread (the GUI's: Evaluate does
            // disk IO per game), one pass at a time so a quick toggle back ends on the current setting; the new states come as GameChanged.
            if (maximumChanged || limitChanged)
                Task.Run(() =>
                {
                    lock (_refreshLock)
                        try { foreach (var g in Games) Refresh(g.Game); }
                        catch (Exception e) { Log?.Report($"re-evaluating games for the new settings failed: {e.Message}"); }
                });
            if (communityOn) StartCommunitySync();
            if (shareOn) StartSharing();
            if (welcomed && ActiveCheck is { } active) ActiveCheckSent = Task.Run(() => active.SendAsync());
            if (recordersChanged && ManageRecorders)
                Task.Run(() =>
                {
                    try { ReconcileRecorders(); }
                    catch (Exception e) { Log?.Report($"reconciling recorders failed: {e.Message}"); }
                });
        }
    }
    readonly object _refreshLock = new();

    public IReadOnlyList<GameState> Games { get { lock (_lock) return _games.ToList(); } }
    public event Action<GameState>? GameChanged;
    public IReadOnlyList<QueueItem> Queue { get { lock (_lock) return _queue.ToList(); } }
    public event Action<QueueItem>? QueueChanged;

    /// <summary>Completes when the queue has nothing left to run.</summary>
    public Task WhenQueueIdle() { lock (_lock) return _worker ?? Task.CompletedTask; }

    /// <summary>Discovery (cheap) + per-game state. Engine detection, the planner check and anti-cheat detection are
    /// reused from scan.json unless the game is new or its exe, store version or recording changed.</summary>
    public Task<IReadOnlyList<GameState>> ScanAsync(CancellationToken ct) => Scan(false, ct);

    /// <summary>ScanAsync that redoes engine detection, the planner check and anti-cheat detection for every game.</summary>
    public Task<IReadOnlyList<GameState>> RescanAsync(CancellationToken ct) => Scan(true, ct);

    Task<IReadOnlyList<GameState>> Scan(bool force, CancellationToken ct) => Task.Run<IReadOnlyList<GameState>>(() =>
    {
        // An install folder listed by an earlier source isn't listed again by a later one (an EA game bought on Steam has
        // the EA installer's files too): source order decides whose id, and so whose saved state, the game keeps.
        var found = new List<Game>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _sources)
            try
            {
                var games = source.Discover().Where(g => !claimed.Contains(GameFiles.DirKey(g.InstallDir))).ToList();
                found.AddRange(games);
                claimed.UnionWith(games.Select(g => GameFiles.DirKey(g.InstallDir)));
            }
            catch (Exception e) { Log?.Report($"{source.Store}: discovery failed: {e.Message}"); }
        var states = new List<GameState>();
        var tickets = new List<long>();
        bool detected = false;
        foreach (var g in found.DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            tickets.Add(Ticket());
            states.Add(Evaluate(g, force, out var fresh));
            detected |= fresh;
        }
        lock (_lock) _games = Newest(states, tickets);
        var running = Running();   // notes how running games were launched (the real check only opens processes named like a game)
        var learned = LearnKeysOfRunning(running);
        for (int i = 0; i < states.Count; i++)
            if (_launched.ContainsKey(Path.GetFileName(states[i].Game.ExePath)) || learned.Contains(states[i].Game.Id))
                (tickets[i], states[i]) = (Ticket(), Evaluate(states[i].Game, false, out _));
        lock (_lock) _games = Newest(states, tickets);
        foreach (var s in states) GameChanged?.Invoke(s);
        if (CheckPlans)
            foreach (var s in states)
                if (s.Engine != null && NeedsPlanCheck(s.Game, Store.LoadGame(s.Game.Id))) CheckPlan(s.Game.Id);
        if (detected) ReleaseMemory();   // engine detection mounts the game's archives
        if (ManageRecorders)
        {
            ReconcileRecorders();
            states = Games.ToList();
        }
        StartCommunitySync(states);
        StartSharing(states.Select(s => s.Game));
        StartMigration(states);
        StartStutterUpdate(force);
        if (ActiveCheck is { } active) ActiveCheckSent = Task.Run(() => active.SendAsync());
        return states;
    }, ct);

    string StutterFile => Path.Combine(Store.DataDir, "known-stutter.json");
    string ConfirmedFile => Path.Combine(Store.DataDir, "confirmed-engines.json");
    DateTime _stutterTried;

    /// <summary>At most about once a day unless <paramref name="force"/>d: a server list, when valid, replaces the one in
    /// use (and the cached copy), and the games it changes come as GameChanged.</summary>
    void StartStutterUpdate(bool force)
    {
        if (ContentRoutes is not { } routes) return;
        lock (_scanLock)
        {
            var saved = File.GetLastWriteTimeUtc(StutterFile);   // 1601 when missing
            var last = saved > _stutterTried ? saved : _stutterTried;
            // 20 h: a daily start at about the same time still checks
            if (!StutterUpdate.IsCompleted || !force && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;
            _stutterTried = DateTime.UtcNow;
            StutterUpdate = Task.Run(async () =>
            {
                if (await StutterList.FetchAsync(StutterFile, routes) is { } list) UseStutterList(list);
                if (await ConfirmedEngines.FetchAsync(ConfirmedFile, routes) is { } confirmed) UseConfirmedEngines(confirmed);
            });
        }
    }

    void UseConfirmedEngines(ConfirmedEngines list)
    {
        ConfirmedEngines.Current = list;
        lock (_refreshLock)
            foreach (var s in Games)
                if (s.StatusReason.Contains(Planner.Untested) && RootSig.Verified(s.Engine!)) Refresh(s.Game);
    }

    void UseStutterList(StutterList list)
    {
        var changed = new List<GameState>();
        lock (_lock)
        {
            StutterList.Current = list;
            for (int i = 0; i < _games.Count; i++)
                if (list.Find(_games[i].Game) is var k && k != _games[i].KnownStutter)
                    changed.Add(_games[i] = _games[i] with { KnownStutter = k });
        }
        foreach (var s in changed) GameChanged?.Invoke(s);
    }

    GameState Evaluate(Game g, bool force, out bool fresh)
    {
        var rec = Store.LoadGame(g.Id);
        var exeDir = Path.GetDirectoryName(g.ExePath)!;
        var cap = CarefulThreads(Vendor.Vendor);
        var judge = cap != null && rec.FirstLaunch == null ? rec.WarmedAt : null;
        // the RayQuery floor is measured on NVIDIA only
        var rayQuery = Vendor.Vendor == GpuVendor.Nvidia ? SessionLog.ReadRayQueryKeys(RayQueryKeysPath(g.Id)) : null;
        var (session, marker, first) = SessionLog.Read(Path.Combine(exeDir, "scskiller_creates.csv"), Path.GetFileName(g.ExePath), judge, MinJudgedCreates, rayQuery, rec.LastPlay);
        if (first != null) rec.FirstLaunch = first;
        if (first != null && AgsLaunchMissed(rec)) { rec.AgsMissed = true; Log?.Report($"{g.Name}: {AgsMissedReason(rec)}"); }
        var keysOf = rec.KeysIndexHash;
        if (keysOf != null && !IndexIsInstalled(g, rec)) WriteKeys(g, rec);   // the game was updated: no longer this build's shaders
        if (ImportRecording(g, rec) | MergeLaunched(g, rec, marker) | first != null | rec.KeysIndexHash != keysOf) Store.SaveGame(g.Id, rec);
        long? psos = rec.Plan is { } p ? p.Stats.Recorded + p.Stats.Generated + p.Stats.D3D11Shaders + p.Stats.MiddlewareItems : null;
        long? recorded = rec.Plan?.Stats.Recorded;
        TimeSpan? fast = psos is { } n ? TimeSpan.FromSeconds(n / (rec.PsoPerSecond ?? WarmRate)) : null;
        TimeSpan? careful = cap != null && psos is { } np && recorded is { } nr ? TimeSpan.FromSeconds(nr / DefaultCarefulPsoPerSecond + (np - nr) / (rec.PsoPerSecond ?? WarmRate)) : null;
        var (_, engine, antiCheat, check) = Evaluated(g, rec, force, out fresh);
        if (engine != null && check.Reason.Contains(Planner.Untested) && RootSig.Verified(engine))
            check = check with { Reason = check.Reason.Replace(Planner.Untested, Planner.NoRecording) };
        var manifest = LocalManifest();
        var entry = manifest != null ? DbEntry(manifest, g, rec) : null;
        bool? inDb = manifest != null ? entry != null : null;
        var rt = NeedsRtRecording(rec.Plan?.Stats);
        var dll = Path.Combine(exeDir, "d3d12.dll");
        bool ours = IsOurProxy(dll);
        var (status, reason) = check.Readiness switch
        {
            Readiness.Unsupported => (GameStatus.Unsupported, check.Reason),
            Readiness.NeedsRecording when antiCheat != AntiCheat.None =>  // the recorder is never installed next to anti-cheat
                (GameStatus.Unsupported, $"needs a recording, which {(antiCheat == AntiCheat.Other ? "its anti-cheat" : antiCheat)} blocks"),
            Readiness.NeedsRecording => (GameStatus.NeedsRecording, check.Reason + DbNote(inDb)),
            // the plan compiles the rest, but not the game's ray tracing: that needs a recording (compiling stays possible, partial)
            _ when rt && antiCheat == AntiCheat.None && (rec.WarmedAt == null || StaleReason(g, rec) == null) => (GameStatus.NeedsRecording, RtNote(inDb)),
            _ when rec.WarmedAt == null => (GameStatus.Ready, RtBlocked(rt, antiCheat, PartialNote(rec.Plan?.Stats) ?? check.Reason)),
            _ when StaleReason(g, rec) is { } why => (GameStatus.Stale, why),
            _ => (GameStatus.Warmed, RtBlocked(rt, antiCheat, (cap is { } t && PartlyWarmedNote(rec, t, careful) is { } partly
                    ? $"partly warmed for driver {rec.WarmedDriverVersion}: {partly}" : $"warmed for driver {rec.WarmedDriverVersion}")
                + (PartialNote(rec.Plan?.Stats) is { } partial ? "; " + partial : ""))),
        };
        return WithRecorder(new GameState(g, engine, antiCheat, status, reason, rec.ShaderCount, rec.Plan?.Stats,
            psos * (long)(rec.BytesPerPso ?? DefaultBytesPerPso),
            rec.Careful && careful != null ? careful : fast,
            rec.WarmedDriverVersion, rec.WarmedAt, rec.LastWarmTime, ours,
            session,
            rec.CacheKeys.Count > 0 && AppCache != null ? AppCache.SizeOf(rec.CacheKeys) : null,
            rec.LaunchedExeName,
            // ponytail: re-detected on every evaluation (one directory listing); cache with the scan if listings get slow
            antiCheat == AntiCheat.None ? Middleware.Tags(Middleware.Detect(exeDir), d => (_planner as Planner)?.Packs?.Pipelines(d) ?? 0) : null,
            rec.LastWarmFailed, rec.LastWarmSkipped, StutterList.Current.Find(g),
            CommunityInUse(g.Id) is { } c ? new CommunityInfo(c.Psos, c.DownloadedAt, File.Exists(RecordingPath(g.Id))) : null,
            Sharing.Shared(Store.GameDir(g.Id))?.At, inDb,
            rec.WarmedAt != null && rec.WarmedPlanVersion != Planner.Version && rec.PlanVersion == Planner.Version ? rec.PlanNewItems : null,
            IsPlaying(g.Id)) with { LastWarmNeedsRecording = rec.LastWarmNeedsRecording, LastWarmCrashed = rec.LastWarmCrashed,
                Careful = cap != null ? new CarefulCompile(rec.Careful, rec.FirstLaunch?.Compiled, careful, recorded) : null,
                RecordedSinceWarm = rec.WarmedAt != null ? rec.RecordedSinceWarm : 0, CommunityDbPsos = entry?.Psos ?? 0, PsoPerSecond = rec.PsoPerSecond,
                LastFrames = Frames(exeDir, Path.GetFileName(g.ExePath), rayQuery) },
            rec, ours, exeDir);
    }

    GameState WithRecorder(GameState s, GameRecord rec, bool ours, string exeDir)
    {
        var skip = RecorderSkip(s, ModSkip(exeDir, rec, ours)) ?? _recorderSkips.GetValueOrDefault(s.Game.Id);
        var o = rec.Recorder ?? (ours ? RecorderOverride.On : RecorderOverride.Default);
        var db = Length(Path.Combine(exeDir, "scskiller.db"));
        return s with { RecorderOverride = o, RecorderSkip = skip, RecorderEffective = RecorderEffective(o, Settings.RecordAllGames, skip),
            RecorderNote = _recorderNotes.GetValueOrDefault(s.Game.Id), RecorderMod = ModName(exeDir, rec, ours),
            RecordAlongsideMod = rec.RecordAlongsideMod,
            RecordingBytes = RecordingFiles(s.Game).Where(f => Path.GetFileName(f) != FrameLog.FileName).Sum(Length),
            RecordingPaused = ours && DbCap(s.Game) is { } cap && db >= cap };
    }

    static long Length(string path) => new FileInfo(path) is { Exists: true } f ? f.Length : 0;

    /// <summary>What <see cref="ClearRecording"/> deletes: the recorder's output in the game folder and SCSKiller's copy of it.</summary>
    IEnumerable<string> RecordingFiles(Game g) => [.. RecorderDataFiles.Select(f => Path.Combine(Path.GetDirectoryName(g.ExePath)!, f)), RecordingPath(g.Id),
        .. new[] { "recording.all.db", "recording.all.db.key" }.Select(f => Path.Combine(Store.GameDir(g.Id), f))];   // until migrated

    static readonly string[] RecorderDataFiles = ["scskiller.db", "scskiller_creates.csv", "scskiller.log", FrameLog.FileName];

    readonly ConcurrentDictionary<string, (long Bin, DateTime Written, long Csv, FrameReport? Report)> _frames = new();

    /// <summary>The frame log's report, read again only when it or the creates csv changed: a scan evaluates every game.</summary>
    FrameReport? Frames(string exeDir, string exe, IReadOnlySet<string>? rayQuery)
    {
        var (bin, csv) = (new FileInfo(Path.Combine(exeDir, FrameLog.FileName)), Path.Combine(exeDir, "scskiller_creates.csv"));
        if (!bin.Exists) return null;
        var stamp = (bin.Length, bin.LastWriteTimeUtc, Length(csv));
        if (_frames.TryGetValue(bin.FullName, out var c) && (c.Bin, c.Written, c.Csv) == stamp) return c.Report;
        FrameReport? r;
        try { r = FrameLog.Read(bin.FullName, csv, exe, rayQuery); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        _frames[bin.FullName] = (stamp.Length, stamp.LastWriteTimeUtc, stamp.Item3, r);
        return r;
    }

    /// <summary>The size the game folder's scskiller.db may reach (the proxy's max_db_bytes): what the limit leaves beside the
    /// imported recording (<paramref name="stored"/>). An import moves the db's records into it, where they take less room,
    /// and empties the db. Null = unlimited.</summary>
    public static long? DbCap(long limitBytes, long stored) => limitBytes > 0 ? Math.Max(0, limitBytes - stored) : null;

    long? DbCap(Game g) => DbCap(Settings.RecordingLimitMB * (1L << 20), Length(RecordingPath(g.Id)));

    /// <summary>The choices for <see cref="Settings.RecordingLimitMB"/>.</summary>
    public static readonly int[] RecordingLimits = [32, 128, 256, 512, 1024, 0];

    /// <summary>"256 MB", "1 GB"; "Unlimited" for 0.</summary>
    public static string LimitText(int mb) => mb <= 0 ? "Unlimited" : mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb} MB";

    public static string PausedNote(Settings s) => $"Recording paused: limit reached ({LimitText(s.RecordingLimitMB)})";

    /// <summary>The last build can't compile the game's ray tracing: more than 10% of its DXIL libraries have no synthesized
    /// collection and no recorded state object (<see cref="PlanStats.RtUncovered"/>): AMD (its driver caches only the exact
    /// objects a game builds), or an engine whose collection layout SCSKiller can't rebuild from its files (Unreal 5). Known
    /// only after a build, like <see cref="IsPartial"/>.</summary>
    public static bool NeedsRtRecording(PlanStats? p) => p is { RtUncovered: > 0 } && p.RtUncovered > 0.1 * p.RtLibraries;

    /// <summary>The status reason of a game whose ray tracing needs a recording (<see cref="NeedsRtRecording"/>).</summary>
    public static string RtNote(bool? inCommunityDb) => RtNeedsRecording + (inCommunityDb == null ? ", or a community recording for this version" : DbNote(inCommunityDb));

    public const string RtNeedsRecording = "Ray-traced effects need one short recording";

    /// <summary>The end of a NeedsRecording reason when the manifest has an entry for the build, whatever the sign-in (the
    /// manifest is public; downloads need "db"). Short: the Library row shows three lines.</summary>
    public const string InDbNote = "in the community database";

    static string DbNote(bool? inDb) => inDb switch { true => "; " + InDbNote, false => "; not in the community database yet", null => "" };

    /// <summary>The manifest's entry for the installed build: by the index's content hash while the index is of this build
    /// (or, without a store build id, from before the exe stamp was kept), else by the store build's alias.</summary>
    static CommunityEntry? DbEntry(CommunityManifest m, Game g, GameRecord rec) =>
        (IndexIsInstalled(g, rec) || g.Version == null && rec.IndexExeStamp == null ? m.Find(g, rec.IndexContentHash) : null) ?? m.Find(g, null);

    /// <summary>Why a game's ray tracing needs a recording on this PC (<see cref="NeedsRtRecording"/>), for the detail page.</summary>
    public static string RtWhy(VendorCaps caps, EngineInfo? e) => caps.RtCacheGranularity == RtCacheGranularity.WholeObject
        ? "This GPU's driver reuses ray tracing pipelines only exactly as the game builds them, and which ones it builds isn't in its files."
        : $"SCSKiller can't rebuild {(e is { } x ? $"{x.Family} {x.Version}" : "this engine")}'s ray tracing layout from the game files yet.";

    static string RtBlocked(bool rt, AntiCheat antiCheat, string reason) =>
        rt && antiCheat != AntiCheat.None ? $"{reason}; ray-traced effects aren't compiled: they need a recording, which {antiCheat} blocks" : reason;

    /// <summary>The last community manifest fetched (community\manifest.bin), re-read when the file changes; null when there is none.</summary>
    CommunityManifest? LocalManifest()
    {
        var f = new FileInfo(Path.Combine(Store.DataDir, "community", "manifest.bin"));
        var stamp = f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";
        lock (_scanLock)
        {
            if (stamp != _manifestStamp)
            {
                _manifestStamp = stamp;
                try { _manifest = f.Exists ? CommunityManifest.Parse(File.ReadAllBytes(f.FullName)) : null; }
                catch (Exception x) when (x is IOException or InvalidDataException or UnauthorizedAccessException) { _manifest = null; }
            }
            return _manifest;
        }
    }
    CommunityManifest? _manifest;
    string? _manifestStamp;

    /// <summary>The last build left more than 10% of the game's stage sets out (<see cref="PlanStats.Uncovered"/>: no root
    /// signature SCSKiller can build covers them; Hogwarts Legacy without a recording: 182857 left out, 41422 planned). The
    /// share is taken against the planned PSOs (at most one per stage set), so it errs toward "partial". Known only after a
    /// build: <see cref="GameRecord.Plan"/> keeps the last build's stats, which is what <see cref="Evaluate"/> reads.</summary>
    public static bool IsPartial(PlanStats? p) => p is { Uncovered: > 0 } && p.Uncovered > 0.1 * (p.Uncovered + p.Recorded + p.Generated);

    /// <summary>AMD's careful compile (<see cref="CarefulCompile"/>): the thread cap of its recorded passes, null on other
    /// vendors. Measured on AMD (Tiny Tina's Wonderlands' recording in sibling passes, the first launch after the warm,
    /// two runs each): 4 threads median 3.4 ms, 44% of the creates under 2 ms; 2 threads 7.3 ms, 36%.</summary>
    public static int? CarefulThreads(GpuVendor vendor) => vendor == GpuVendor.Amd ? AmdCarefulThreads : null;
    public const int AmdCarefulThreads = 4;
    /// <summary>A careful compile's rate for the recorded PSOs (AMD, 4 threads, sibling passes: the recording above, 18,115 PSOs
    /// in 271 s); the plan's other items at the game's fast rate.</summary>
    public const double DefaultCarefulPsoPerSecond = 67;
    /// <summary>A warmed game whose first launch still compiled more than this share of its creates (<see cref="SessionLog"/>'s
    /// compile mark, over 3 ms) is partly warmed; judged on a launch of at least <see cref="MinJudgedCreates"/> creates.</summary>
    public const double PartlyWarmedShare = 0.2;
    public const long MinJudgedCreates = 100;

    public static bool IsPartlyWarmed(LaunchCheck? launch) => launch != null && launch.Compiles > PartlyWarmedShare * (launch.Hits + launch.Compiles);
    public static bool IsPartlyWarmed(GameState s) => s.Status == GameStatus.Warmed && s.Careful?.LaunchCompiled > PartlyWarmedShare;

    /// <summary>A partly warmed game's (<see cref="IsPartlyWarmed"/>) note: what its first launch found and what a careful
    /// compile would do; null otherwise.</summary>
    static string? PartlyWarmedNote(GameRecord r, int threads, TimeSpan? careful) => r.FirstLaunch is not { } l || !IsPartlyWarmed(l) ? null
        : $"{l.Compiled * 100:0}% of the {l.Hits + l.Compiles:N0} pipelines its first launch created still compiled"
          + (r.WarmedCareful ? ", even after a careful compile"
              : r.Plan?.Stats.Recorded == 0 ? "; a careful compile only changes how the recorded pipelines compile, and there is no recording yet (turn recording on and play)"
              : $"; a careful compile ({threads} threads, in passes) reaches more of them" + (careful is { } t ? $", in about {Duration(t)}" : "")
                + (r.Careful ? ": the next compile is careful" : ""));

    /// <summary>"45 s", "8 min", "2 h 5 min".</summary>
    public static string Duration(TimeSpan t) => t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)} s"
        : t.TotalHours < 1 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h" + (t.Minutes > 0 ? $" {t.Minutes} min" : "");

    /// <summary>The share of the stage sets found in the game files that the plan compiles (<see cref="PlanStats.StageSets"/>
    /// minus <see cref="PlanStats.LeftOut"/>); null when not counted (no plan, a DirectX 11-only plan, a plan from before
    /// the count). Only what the planner found: combinations a game assembles at run time aren't in it.</summary>
    public static double? Coverage(PlanStats? p) => p is { StageSets: > 0 } ? (double)(p.StageSets - p.LeftOut) / p.StageSets : null;

    /// <summary><see cref="Coverage"/> as a whole percent a player reads: floored, and never 100 while anything is left out.</summary>
    public static int? CoveragePercent(PlanStats? p) => Coverage(p) is { } c ? Math.Min((int)Math.Floor(c * 100), p!.LeftOut > 0 ? 99 : 100) : null;

    /// <summary>The status note of a partial plan (<see cref="IsPartial"/>): what it compiles and what would add the rest.</summary>
    public static string? PartialNote(PlanStats? p) => !IsPartial(p) ? null
        : p!.Recorded == 0 ? $"compiles {p.Generated:N0} pipelines; a 5-minute recording lets SCSKiller rebuild the rest"
        : $"compiles {p.Recorded + p.Generated:N0} pipelines; {p.Uncovered:N0} more shader combinations use shader slots SCSKiller can't rebuild yet";

    /// <summary>A queue line's progress (CLI): " done/total (N failed[, M skipped: not in this install]) rate/s", then for
    /// Done/Stopped the note (<see cref="WarmCounts"/>); "" without progress.</summary>
    public static string ProgressText(QueueItem q)
    {
        if (q.Progress is not { } x) return "";
        var counts = $"{x.Failed} failed" + (x.Skipped > 0 ? $", {x.Skipped} skipped: not in this install" : "");
        return $" {x.Done}/{x.Total} ({counts}) {x.PerSecond:0}/s" + (q.Stage is QueueStage.Done or QueueStage.Stopped or QueueStage.Warming && q.Note is { } n ? " - " + n : "");
    }

    /// <summary>A warming item's note when its done count stopped moving (<see cref="StallAfter"/>): "no progress for 3 min".</summary>
    public static string StalledNote(TimeSpan since) => $"no progress for {Math.Max(1, (int)since.TotalMinutes)} min";

    /// <summary>A warming item that stopped moving: its note says for how long, and it has no time estimate.</summary>
    public static bool Stalled(QueueItem q) => q.Stage == QueueStage.Warming && q.Note?.StartsWith("no progress") == true;

    /// <summary>What a warm's counts say beyond the compiled ones; null when all are 0. Failed = the driver rejected it;
    /// skipped = never replayed; crashed = never replayed because its create removed the device (crashed the GPU driver).</summary>
    public static string? WarmCounts(long failed, long skipped, long crashed = 0) =>
        string.Join(", ", new[]
        {
            failed > 0 ? $"{failed} failed (the driver rejected them)" : null,
            skipped > 0 ? $"{skipped} skipped (a shader not in this install)" : null,
            crashed > 0 ? $"{crashed} skipped ({(crashed == 1 ? "it crashes" : "they crash")} the GPU driver)" : null,
        }.OfType<string>()) is { Length: > 0 } s ? s : null;

    /// <summary>Warm rate (PSO/s) before this PC measured one, per vendor: NVIDIA 450 (30 threads: 450-1350);
    /// AMD 160 (~8 threads, from the AMD smoke and per-stage A/B runs: a cold PSO there is ~10 ms).</summary>
    public static double DefaultWarmRate(GpuVendor vendor) => vendor == GpuVendor.Amd ? DefaultAmdPsoPerSecond : DefaultPsoPerSecond;

    /// <summary>The compile memory budget "Auto" picks (GB) from the PC's total physical memory: one step per common size,
    /// with the thresholds between sizes (a 16 GB PC reports a little less).</summary>
    public static int AutoCompileMemoryGB(long totalPhysicalBytes) => (totalPhysicalBytes / (double)(1L << 30)) switch
    {
        <= 20 => 2,
        <= 28 => 4,
        <= 40 => 6,
        <= 56 => 8,
        _ => 16,
    };

    public static int CompileMemoryGB(Settings s) => s.MaxCompileMemoryGB > 0 ? s.MaxCompileMemoryGB : AutoCompileMemoryGB();

    public static int AutoCompileMemoryGB() => AutoCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    public const double DefaultAmdPsoPerSecond = 160;

    /// <summary>The estimate's rate for a game without its own measured warm: the first complete warm measured on this PC for
    /// this vendor (<see cref="SeedWarmRate"/>), else <see cref="DefaultWarmRate"/>.</summary>
    double WarmRate => (_warmRate ??= Store.LoadWarmRates().GetValueOrDefault(Vendor.Vendor)) is > 0 and var r ? r : DefaultWarmRate(Vendor.Vendor);
    double? _warmRate;

    /// <summary>A warm that measures the compile rate: the game's driver cache held nothing for this driver when it started
    /// (never warmed, cleared, or a new driver, which drops the old cache). A re-warm on the same driver mostly hits, at
    /// several times the rate.</summary>
    public static bool ColdWarm(GameRecord rec, string driver) => rec.WarmedAt == null || rec.WarmedDriverVersion != driver;

    /// <summary>The games' measured cold rates as one: their plans' items over the time each takes at its rate. Null: none measured.</summary>
    public static double? MeasuredRate(IEnumerable<GameState> games)
    {
        var m = games.Where(g => g is { PsoPerSecond: > 0, Plan: not null })
            .Select(g => (Items: (double)(g.Plan!.Recorded + g.Plan.Generated + g.Plan.D3D11Shaders + g.Plan.MiddlewareItems), Rate: g.PsoPerSecond!.Value))
            .Where(g => g.Items > 0).ToList();
        return m.Count > 0 ? m.Sum(g => g.Items) / m.Sum(g => g.Items / g.Rate) : null;
    }

    /// <summary>Keeps the first complete warm's rate as this vendor's rate (later warms keep their own per game).</summary>
    void SeedWarmRate(double psoPerSecond)
    {
        var rates = Store.LoadWarmRates();
        if (rates.ContainsKey(Vendor.Vendor)) return;
        rates[Vendor.Vendor] = psoPerSecond;
        Store.SaveWarmRates(rates);
        _warmRate = psoPerSecond;
    }

    /// <summary>The expensive part of a game's state, cached in scan.json per game.</summary>
    Evaluation Evaluated(Game g, GameRecord rec, bool force, out bool fresh)
    {
        var key = string.Join('|', ExeStamp(g), g.Version, Vendor.Caps.Profile, rec.RecordingImportedAt?.UtcTicks, CoreBuild, CommunityInUse(g.Id)?.Object);
        lock (_scanLock)
        {
            _scan ??= Store.LoadScan();
            fresh = force || !_scan.TryGetValue(g.Id, out var cached) || cached.Key != key;
            if (!fresh) return _scan[g.Id];
        }
        EngineInfo? engine = null;
        PlanCheck check;
        try
        {
            engine = _reader.Detect(g);
            check = engine == null ? new(Readiness.Unsupported, "engine not supported yet")
                : CheckRecordings(g, engine);
        }
        catch (Exception e) { check = new(Readiness.Unsupported, e.Message); }
        var ev = new Evaluation(key, engine, GameFiles.DetectAntiCheat(g), check);
        lock (_scanLock)
        {
            _scan[g.Id] = ev;
            Store.SaveScan(_scan);
        }
        return ev;
    }

    string? StaleReason(Game g, GameRecord r) => WarmChanged(g, r)
        ?? (r.WarmedPlanVersion != Planner.Version ? PlannerChanged(r)
            : Settings.MaximumPlans && r.WarmedPerStage ? "Maximum mode: every shader pairing is still to compile" : null)   // Maximum -> standard: nothing new
        ?? (r.RecordedSinceWarm is > 0 and var n ? $"{n:N0} new pipeline{(n == 1 ? "" : "s")} recorded; compile again to include them" : null);

    /// <summary>A newer planner than the warmed plan's. Its rebuild (RunItem) counts the pipelines the warm didn't compile;
    /// none marks the warm current then, so a count here is more than 0. Not rebuilt yet, or not countable (no readable
    /// warmed plan): it may compile more.</summary>
    static string? PlannerChanged(GameRecord r) =>
        r.PlanVersion == Planner.Version && r.PlanNewItems is { } n ? n > 0 ? $"SCSKiller can now compile {n:N0} more pipeline{(n == 1 ? "" : "s")} for this game" : null
        : "SCSKiller can now compile more of this game";

    /// <summary>Why the warm no longer matches what the game uses, whatever the plan: driver, exe name, game build, shaders.</summary>
    string? WarmChanged(Game g, GameRecord r) =>
        r.WarmedDriverVersion != Vendor.Gpu.DriverVersion ? $"driver changed: {r.WarmedDriverVersion} -> {Vendor.Gpu.DriverVersion}"
        : TrimmedReason(r) is { } trimmed ? trimmed
        : WarmMissesGame(r) ? MissesGameReason
        : r.AgsMissed && r.WarmedAgsApp != null ? AgsMissedReason(r)
        : WarmExeName(g, r) is var runs && (r.WarmedExeName ?? Path.GetFileName(g.ExePath)) is var warmed && runs != warmed && CaseMatters(g, r, warmed)
            ? $"the game runs as {runs}, the warm filled the cache of {warmed} (the driver keys it on the exe name's exact case)"
        : g.Version != null && r.WarmedGameVersion != null ? (g.Version != r.WarmedGameVersion ? $"game updated since the warm (build {r.WarmedGameVersion} -> {g.Version})" : IndexChanged(r))
        : r.WarmedExeStamp != ExeStamp(g) ? "game updated since the warm"   // no store version (older state, other stores): the exe
        : IndexChanged(r);

    /// <summary>Warmed, stale only because an older planner built its plan, and not rebuilt since (<see cref="CheckPlans"/>).</summary>
    bool NeedsPlanCheck(Game g, GameRecord r) =>
        r.WarmedAt != null && r.WarmedPlanVersion != Planner.Version && r.PlanVersion != Planner.Version && WarmChanged(g, r) == null;

    /// <summary>A "when idle" plan rebuild without a warm, unless the game is in the queue already (a failed or stopped
    /// item too: not retried on every scan).</summary>
    void CheckPlan(string gameId)
    {
        lock (_lock)
            if (_queue.Any(q => q.GameId == gameId && q.Stage != QueueStage.Done)) return;
        Add(gameId, whenIdle: true, planCheck: true);
    }

    /// <summary>The plan checks (<see cref="QueueItem.PlanCheck"/>) still to run or running, as the one line the queue
    /// shows for them; null = none.</summary>
    public static string? PlanCheckLine(IEnumerable<QueueItem> queue) =>
        queue.Count(q => q.PlanCheck && !Finished(q.Stage)) is var n and > 0
            ? $"Checking {n} game{(n == 1 ? "" : "s")} for more to compile (while idle)" : null;

    /// <summary>The keys (sha1 of tag + payload) of what plan.bin has the warm create: every record but the 'B' root
    /// signature blobs, which compile nothing by themselves (an item's key covers its root signature's hash). Null when it
    /// isn't a readable plan.</summary>
    static HashSet<string>? PlanKeys(string path)
    {
        try { return PlanFile.Read(path).Records.Where(r => r.Tag != 'B').Select(r => r.Key).ToHashSet(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>What a plan compiles, independent of record order and of its header (paths, times): a hash of its sorted
    /// <see cref="PlanKeys"/>.</summary>
    static string PlanFingerprint(IEnumerable<string> keys) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(string.Concat(keys.Order(StringComparer.Ordinal)))));

    /// <summary>The record keys of the plan the last complete warm replayed while plan.bin still is that plan, else null. An
    /// older record without <see cref="GameRecord.WarmedPlanItems"/>: plan.bin is it when the warmed planner version built it
    /// before the warm; its fingerprint goes into the record then.</summary>
    static HashSet<string>? WarmedPlanKeys(GameRecord r)
    {
        if (r.WarmedAt == null || r.Plan is not { } p) return null;
        bool warmed = r.WarmedPlanItems != null ? r.PlanItems == r.WarmedPlanItems : r.PlanVersion == r.WarmedPlanVersion && r.PlanBuiltAt <= r.WarmedAt;
        if (!warmed || PlanKeys(p.FilePath) is not { } keys) return null;
        r.WarmedPlanItems ??= PlanFingerprint(keys);
        return keys;
    }

    static string? IndexChanged(GameRecord r) => r.IndexContentHash != r.WarmedIndexHash ? "game shaders changed since the warm" : null;

    /// <summary>NVIDIA's DXCache key ignores the exe name's case (measured: same files in upper case); AMD's DxcCache key is
    /// case-sensitive (FNV-1a of the UTF-16 name as launched). Other vendors: unmeasured, assume it matters.</summary>
    bool CaseSensitiveCache => Vendor.Vendor != GpuVendor.Nvidia;

    /// <summary>Whether warming this game under another case of its exe name fills another cache. Only for a name-hashed
    /// key: on AMD an app profile (FF7 Rebirth, Cyberpunk, Elden Ring...) gives every case of the name one fixed key, which
    /// shows as learned D3D12 keys without the warmed name's hash (<see cref="AmdAppCache.IsNameHashed"/>), and a game that
    /// registers an AGS app name gets that name's key. Nothing learned yet: assume it matters.</summary>
    bool CaseMatters(Game g, GameRecord r, string warmedExeName) =>
        CaseSensitiveCache && r.WarmedAgsApp == null && AmdAppCache.IsNameHashed(r.CacheKeys, warmedExeName) != false;

    /// <summary>The exe file name a warm of this game stages: the name its process was seen launched with (the recorder's
    /// #session marker, the running game's module path), else the install's file name. They differ at most in case.</summary>
    public static string WarmExeName(Game g, GameRecord r) =>
        r.LaunchedExeName is { } n && n.Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase) ? n : Path.GetFileName(g.ExePath);

    /// <summary>Takes the latest sighting of how the game's process was launched (the recorder's #session marker, or a
    /// running process noted by Running) into the record; only names equal to the install's apart from case count. A new
    /// name drops a stopped warm's resume point: what it replayed went into the other name's cache. True if changed.</summary>
    bool MergeLaunched(Game g, GameRecord rec, LaunchedExe? marker)
    {
        var disk = Path.GetFileName(g.ExePath);
        var before = WarmExeName(g, rec);
        bool changed = false;
        foreach (var seen in new[] { marker, _launched.GetValueOrDefault(disk) })
        {
            if (seen == null || !seen.Name.Equals(disk, StringComparison.OrdinalIgnoreCase) || seen.At <= rec.LaunchedExeSeenAt) continue;
            (rec.LaunchedExeName, rec.LaunchedExeSeenAt, changed) = (seen.Name, seen.At, true);
        }
        if (WarmExeName(g, rec) != before && CaseMatters(g, rec, before)) rec.ResumeAt = 0;
        return changed;
    }

    /// <summary>Learns the driver-cache keys running games' own processes hold open (the same attribution as a warm's, see
    /// RunItem): on AMD the key can come from an app profile rather than the exe name, so the files the game really
    /// uses are the only authority. Returns the ids of games that got a new key (saved).</summary>
    HashSet<string> LearnKeysOfRunning(IReadOnlySet<string> running)
    {
        var learned = new HashSet<string>();
        if (AppCache == null || running.Count == 0) return learned;
        foreach (var s in Games)
        {
            var exe = Path.GetFileName(s.Game.ExePath);
            if (!running.Contains(exe) || s.AntiCheat != AntiCheat.None) continue;   // nothing near an anti-cheat game while it runs
            var rec = Store.LoadGame(s.Game.Id);
            if (LearnKeys(s.Game, rec, WarmExeName(s.Game, rec)) is var added && added.Count == 0) continue;
            Store.SaveGame(s.Game.Id, rec);
            learned.Add(s.Game.Id);
        }
        return learned;
    }

    /// <summary>Adds the keys the game's own process (named <paramref name="exe"/>) holds open to the game's record (not
    /// saved); returns the new ones and logs when the D3D12 key isn't the name's hash (an AMD app profile).</summary>
    IReadOnlySet<string> LearnKeys(Game g, GameRecord rec, string exe) => LearnKeys(g, rec, exe, OpenKeys(g, exe));

    IReadOnlySet<string> LearnKeys(Game g, GameRecord rec, string exe, IReadOnlySet<string> open)
    {
        var added = open.Where(k => !rec.GameKeys.Contains(k)).ToHashSet();
        NoteProfile(g, exe, added.Where(k => !rec.CacheKeys.Contains(k)).ToList());
        rec.CacheKeys.UnionWith(added);
        rec.GameKeys.UnionWith(added);
        return added;
    }

    IReadOnlySet<string> OpenKeys(Game g, string exe)
    {
        try { return AppCache?.KeysOpenBy(exe) ?? new HashSet<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: cache attribution: {e.Message}"); return new HashSet<string>(); }
    }

    /// <summary>Samples the keys a game holds open while it's played, until it has some open: a warm running under another
    /// identity than the game's (an Xbox game on NVIDIA) fills other keys, so only the game's own process tells its cache.</summary>
    async Task LearnWhilePlaying(Game g)
    {
        while (IsPlaying(g.Id))
        {
            var exe = WarmExeName(g, Store.LoadGame(g.Id));
            if (OpenKeys(g, exe) is { Count: > 0 } open)
            {
                var rec = Store.LoadGame(g.Id);
                if (LearnKeys(g, rec, exe, open).Count > 0) { Store.SaveGame(g.Id, rec); Refresh(g); }
                return;
            }
            await Task.Delay(AttributionInterval);
        }
    }

    /// <summary>The game's own keys are known and the last complete warm filled none of them: the warm went to another cache.</summary>
    public static bool WarmMissesGame(GameRecord r) =>
        r.GameKeys.Count > 0 && (r.WarmedKeys ?? r.CacheKeys.Except(r.GameKeys).ToHashSet()) is { Count: > 0 } warmed && !warmed.Overlaps(r.GameKeys);

    /// <summary>A warm under an AGS app name whose game's own keys aren't known, and the game's first launch after it still
    /// compiled most of its pipelines (<see cref="IsPartlyWarmed(LaunchCheck?)"/>): taken as a miss, so the next warm is plain.</summary>
    public static bool AgsLaunchMissed(GameRecord r) =>
        r.WarmedAgsApp != null && !r.GameKeys.Any(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)) && IsPartlyWarmed(r.FirstLaunch);

    static string AgsMissedReason(GameRecord r) =>
        $"the compile didn't reach this game's cache (it registered the AGS app name {r.WarmedAgsApp}"
        + (r.FirstLaunch is { } l ? $"; the first launch still compiled {l.Compiled * 100:0}% of its pipelines" : "")
        + "): the next compile runs without it";

    public const string MissesGameReason = "the compile didn't reach this game's cache: the game uses another driver-cache key";
    public const string TrimmedPartReason = "part of this game's shader cache was removed by the driver's size limit; compile again",
        TrimmedAllReason = "this game's shader cache was removed (by the driver's size limit or a shader cache reset); compile again";

    /// <summary>AMD: a file the last complete warm left in DxcCache is gone (the driver trims least recently used files past
    /// its cap, a reset removes them all).</summary>
    string? TrimmedReason(GameRecord r) =>
        r.WarmedFiles is { Count: > 0 } files && AppCache is AmdAppCache amd && amd.Missing(files) is { Count: > 0 } gone
            ? gone.Count == files.Count ? TrimmedAllReason : TrimmedPartReason
            : null;

    void NoteProfile(Game g, string exe, IEnumerable<string> keys)
    {
        if (AmdAppCache.IsNameHashed(keys, exe) == false)
            Log?.Report($"{g.Name}: the driver keys {exe}'s cache as {string.Join(", ", keys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix)))}, " +
                        $"not its name hash {AmdAppCache.DxcKey(exe)} " +
                        (Ags(g) is { } a && keys.Contains(AgsKey(g)) ? $"(its AGS app name {a.App}" : "(an app profile") + ": the exe name's case doesn't matter)");
    }

    /// <summary>On AMD, what the game probably registers with AGS (<see cref="AmdAgs.Of"/>); a hint, see <see cref="AgsFor"/>.</summary>
    AgsRegistration? Ags(Game g)
    {
        if (Vendor.Vendor != GpuVendor.Amd) return null;
        EngineInfo? engine;
        lock (_scanLock) engine = _scan?.GetValueOrDefault(g.Id)?.Engine;   // the scan's cache: Games may not hold the game yet
        return AmdAgs.Of(g, engine);
    }

    /// <summary>The AGS app-name key expected for the game (<see cref="Ags"/>), null when it doesn't apply.</summary>
    public string? AgsKey(string gameId) => AgsKey(Find(gameId).Game);

    string? AgsKey(Game g) => Ags(g) is { } a ? AmdAppCache.AgsKey(Path.GetFileName(g.ExePath), a.App) : null;

    /// <summary>The registration a warm of this game uses, only where it is proven to reach the game's cache: the game's own
    /// process was seen holding the AGS key and not its plain key, or, before the game is seen, the app name is a measured
    /// one (<see cref="AmdAppCache.ProvenAgsApp"/>) and no launch showed a miss. Else null: a plain device, as without AGS.</summary>
    AgsRegistration? AgsFor(Game g, GameRecord r) => AgsFor(Ags(g), WarmExeName(g, r), r);

    public static AgsRegistration? AgsFor(AgsRegistration? a, string warmExeName, GameRecord r)
    {
        if (a == null) return null;
        var seen = r.GameKeys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).ToList();
        if (seen.Count > 0)
            return seen.Contains(AmdAppCache.AgsKey(warmExeName, a.App)) && !seen.Contains(AmdAppCache.HintKey(warmExeName)) ? a : null;
        return !r.AgsMissed && AmdAppCache.ProvenAgsApp(a.App) ? a : null;
    }

    /// <summary>The AGS registration the next warm of this game uses (<see cref="AgsFor"/>); null = a plain device.</summary>
    public AgsRegistration? WarmAgs(string gameId) => AgsFor(Find(gameId).Game, Store.LoadGame(gameId));

    static string ExeStamp(Game g) => new FileInfo(g.ExePath) is { Exists: true } f ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";

    public IReadOnlyList<GameState> StaleGames() => Games.Where(s => s.Status == GameStatus.Stale).ToList();
    public IReadOnlyList<GameState> DriverStaleGames() => StaleGames().Where(s => s.WarmedDriverVersion != Vendor.Gpu.DriverVersion).ToList();

    // What the user skipped: the driver and the game build each stale game was offered for.
    string StaleKey(GameState s) => $"{Vendor.Gpu.DriverVersion}|{s.Game.Version ?? ExeStamp(s.Game)}";

    public const string DriverPart = "Driver cache", WindowsPart = "Windows shader cache", PipelinePart = "Game's pipeline cache",
        PrecachePart = "Game's shader precache";

    /// <summary><paramref name="gamePrecache"/>: also what the game writes itself and rebuilds at its next start (Unreal's user
    /// pipeline cache, *.ushaderprecache files): <see cref="PipelinePart"/>, <see cref="PrecachePart"/>.</summary>
    public IReadOnlyList<CachePart> GameCaches(string gameId, bool gamePrecache = false)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        var parts = new List<CachePart>();
        if (AppCache != null && DriverKeys(s.Game, rec).Keys is { Count: > 0 } keys) parts.Add(Part(DriverPart, AppCache.FilesOf(keys)));
        if (s.AntiCheat != AntiCheat.None) return parts;   // anti-cheat: nothing near the game, only the driver's cache
        parts.Add(Part(WindowsPart, D3DSCache.FoldersOf(Path.Combine(LocalAppData, "D3DSCache"), s.Game)
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles())));
        if (!gamePrecache) return parts.Where(p => p.Files.Count > 0).ToList();
        // two games with one project name share the file name: whose it is is unknown, skipped
        if (UnrealUserCache.Project(s.Game.ExePath) is { } project
            && !Games.Any(o => o.Game.Id != gameId && string.Equals(UnrealUserCache.Project(o.Game.ExePath), project, StringComparison.OrdinalIgnoreCase)))
            parts.Add(Part(PipelinePart, UnrealUserCache.FilesOf(s.Game, SavedRoots(s.Game)).Select(f => new FileInfo(f))));
        var others = Games.Where(o => o.Game.Id != gameId).SelectMany(o => UnrealUserCache.Folders(o.Game)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        parts.Add(Part(PrecachePart, UnrealUserCache.PrecacheFilesOf(s.Game, [.. SavedRoots(s.Game), ProgramData],
            UnrealUserCache.Folders(s.Game).Where(n => !others.Contains(n))).Select(f => new FileInfo(f))));
        return parts.Where(p => p.Files.Count > 0).ToList();
    }

    static CachePart Part(string name, IEnumerable<FileInfo> files)
    {
        var list = files.ToList();
        return new(name, list.Select(f => f.FullName).ToList(), list.Sum(f => f.Length));
    }

    /// <summary>An Xbox game's Saved may also be in its package's own %LOCALAPPDATA%.</summary>
    string[] SavedRoots(Game g) => g.Store == Core.Store.Xbox && g.Id.StartsWith("xbox:", StringComparison.Ordinal)
        ? [LocalAppData, MyGames, Path.Combine(LocalAppData, "Packages", g.Id["xbox:".Length..], "LocalCache", "Local")]
        : [LocalAppData, MyGames];

    /// <summary>True with driver keys attributed even if nothing is left on disk. Refuses while a process named like the exe
    /// runs (the game or another SCSKiller's warm: names only, none opened), and when another game shares a driver key
    /// (<see cref="SharedWith"/>: an AMD app profile gives several exes one key): the delete would silently cold it.</summary>
    public bool ClearGameCache(string gameId, bool gamePrecache = false)
    {
        var s = Find(gameId);
        var rec = Store.LoadGame(gameId);
        var exe = Path.GetFileName(s.Game.ExePath);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        if (ProcessNames().Contains(Path.GetFileNameWithoutExtension(exe))) throw new InvalidOperationException($"{s.Game.Name} is running");
        var keys = DriverKeys(s.Game, rec).Keys;
        var driver = AppCache != null && keys.Count > 0;
        if (driver && SharedWith(gameId, keys) is { Count: > 0 } shared)
            throw new InvalidOperationException($"{s.Game.Name}'s driver cache is shared with " +
                string.Join(", ", shared.Select(x => $"{x.Name} ({string.Join(", ", x.Keys)})")) +
                ": the driver gives their exes the same key (an app profile, or the same exe name), so clearing it would clear theirs too");
        var parts = GameCaches(gameId, gamePrecache);
        if (!driver && parts.Count == 0) return false;
        AppCacheFiles.DeleteAll(parts.SelectMany(p => p.Files).Select(f => new FileInfo(f)).ToList());   // throws "files in use by <process>"
        foreach (var dir in parts.Where(p => p.Name == WindowsPart).SelectMany(p => p.Files).Select(Path.GetDirectoryName).Distinct())
            try { Directory.Delete(dir!); }
            catch (IOException) { }   // not empty: a file appeared meanwhile, left alone
        if (!driver) return true;
        // the keys stay: they are this exe name's, so the cache the game builds by itself still counts in CacheOnDisk
        (rec.WarmedAt, rec.WarmedDriverVersion, rec.LastWarmTime, rec.LastCacheGrowthBytes, rec.ResumeAt) = (null, null, null, null, 0);
        (rec.LastWarmFailed, rec.LastWarmSkipped, rec.LastWarmNeedsRecording, rec.LastWarmCrashed) = (null, null, null, null);
        (rec.WarmedCareful, rec.FirstLaunch, rec.WarmedFiles) = (false, null, null);
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
        return true;
    }

    public void SetCarefulCompile(string gameId, bool on)
    {
        var s = Find(gameId);
        if (CarefulThreads(Vendor.Vendor) == null) throw new InvalidOperationException($"the careful compile is for AMD drivers, not {Vendor.Gpu.Name}");
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        var rec = Store.LoadGame(gameId);
        if (rec.Careful == on) return;
        (rec.Careful, rec.ResumeAt) = (on, 0);   // a stopped warm's resume point counts the other schedule's items
        Store.SaveGame(gameId, rec);
        Refresh(s.Game);
    }

    /// <summary>Other discovered games whose driver cache is (or would be) the same files as this game's: keys learned for
    /// both, or a D3D12 key that the other game's exe name maps to (<see cref="AmdAppCache.HintKey"/>: a known AMD app
    /// profile such as every "ff7rebirth*" exe sharing 6b2fcd83, or the same exact name), even before it was warmed.</summary>
    List<(string Name, List<string> Keys)> SharedWith(string gameId, IReadOnlySet<string> keys)
    {
        var shared = new List<(string, List<string>)>();
        foreach (var o in Games)
        {
            if (o.Game.Id == gameId) continue;
            var other = Store.LoadGame(o.Game.Id);
            var hint = AmdAppCache.HintKey(WarmExeName(o.Game, other));
            var ags = AgsFor(o.Game, other) != null ? AgsKey(o.Game) : null;
            var both = keys.Where(k => other.CacheKeys.Contains(k) || k == hint || k == ags).Order().ToList();
            if (both.Count > 0) shared.Add((o.Game.Name, both));
        }
        return shared;
    }

    /// <summary>The driver-cache keys Clear cache covers: the learned ones, and on AMD, unless a learned D3D12 key shows an
    /// app profile, the name hash of each case the exe was seen under that has files (the key of a name without a
    /// profile). Gap: which of the game's driver cache may be left, null when none.</summary>
    (HashSet<string> Keys, string? Gap) DriverKeys(Game g, GameRecord rec)
    {
        var keys = rec.CacheKeys.ToHashSet();
        var exe = Path.GetFileName(g.ExePath);
        if (AppCache is not AmdAppCache amd)
            return (keys, AppCache == null || keys.Count > 0 ? null
                : $"the driver cache key of {exe} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): the driver cache is not cleared");
        var names = new[] { exe, rec.LaunchedExeName, rec.WarmedExeName }.OfType<string>().Distinct().ToList();
        var dxc = keys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).ToList();
        // the AGS app name's key is only a hint: cleared once a process is seen holding it, like any key
        var ags = AgsFor(g, rec) is { } a && AgsKey(g) is { } ak && !keys.Contains(ak) && (dxc.Count == 0 || amd.FilesOf([ak]).Count > 0)
            ? $"the D3D12 key {ak} that {g.Name} gets from its AGS app name {a.App} isn't learned yet (SCSKiller learns it when it compiles the game or sees it running): it is not cleared"
            : null;
        if (dxc.Count > 0 && !names.Any(n => dxc.Contains(AmdAppCache.DxcKey(n)))) return (keys, ags);
        keys.UnionWith(names.Select(AmdAppCache.HintKey).Where(k => amd.FilesOf([k]).Count > 0));
        return (keys, dxc.Count > 0 || ags != null ? ags
            : $"the D3D12 driver cache key of {exe} isn't learned yet: clearing takes the key the driver gives that exe name; a driver app profile's key or a D3D11 key would be missed");
    }

    /// <summary>Which of the game's driver cache <see cref="ClearGameCache"/> may miss (a key not learned yet); null = none.</summary>
    public string? DriverCacheGap(string gameId) => DriverKeys(Find(gameId).Game, Store.LoadGame(gameId)).Gap;

    public bool SetEncryptionKey(string gameId, string key)
    {
        var game = Games.FirstOrDefault(s => s.Game.Id == gameId)?.Game ?? throw new ArgumentException($"unknown game {gameId}");
        return (_reader as UnrealReader ?? (_reader as EngineReaders)?.Get<UnrealReader>()) is { } u && u.SetKey(game, key);
    }

    public void DismissStale() => Store.SaveDismissed(StaleGames().ToDictionary(s => s.Game.Id, StaleKey));

    /// <summary>The driver-update notification decision: Ask mode and a driver-stale game that wasn't skipped (DismissStale)
    /// for this driver and game build. StaleGames() keeps listing skipped games for the UI.</summary>
    public bool ShouldNotifyStale()
    {
        if (Settings.OnDriverUpdate != DriverUpdateMode.Ask) return false;
        var skipped = Store.LoadDismissed();
        return DriverStaleGames().Any(s => !skipped.TryGetValue(s.Game.Id, out var k) || k != StaleKey(s));
    }

    /// <summary>Registers the logon/idle re-warm task (it runs the published CLI) for Ask and WhenIdle, removes it for Off.
    /// For the Settings page, after the user changes OnDriverUpdate.</summary>
    public void ApplyDriverUpdateMode()
    {
        if (Settings.OnDriverUpdate == DriverUpdateMode.Off) ScheduledTask.Unregister();
        else ScheduledTask.Register(ScheduledTask.TaskExe() ?? throw new FileNotFoundException(@"the command-line tool (cli\scskiller.exe) is not next to the app"));
    }

    void Refresh(Game g)
    {
        var ticket = Ticket();
        var s = Evaluate(g, false, out _);
        lock (_lock)
        {
            if (_evaluatedAt.GetValueOrDefault(g.Id) > ticket) return;   // an evaluation started later is in place
            _evaluatedAt[g.Id] = ticket;
            var i = _games.FindIndex(x => x.Game.Id == g.Id);
            if (i >= 0) _games[i] = s; else _games.Add(s);
        }
        GameChanged?.Invoke(s);
    }

    // Evaluations overlap (a scan, a game's exit, a compile's end): each takes a ticket before it reads anything, and a
    // state is stored only over one from an older ticket, so a slow one never replaces what a later one read.
    long _tickets;
    readonly Dictionary<string, long> _evaluatedAt = [];   // game id -> ticket of its state in _games; under _lock

    long Ticket() => Interlocked.Increment(ref _tickets);

    /// <summary>Under _lock: the scan's states, each game's kept only if no later evaluation was stored meanwhile.</summary>
    List<GameState> Newest(List<GameState> states, List<long> tickets)
    {
        var now = new List<GameState>(states.Count);
        for (int i = 0; i < states.Count; i++)
        {
            var id = states[i].Game.Id;
            if (_evaluatedAt.GetValueOrDefault(id) > tickets[i] && _games.Find(x => x.Game.Id == id) is { } newer) states[i] = newer;
            else _evaluatedAt[id] = tickets[i];
            now.Add(states[i]);
        }
        return now;
    }

    /// <summary>The game as its store lists it now. The scan's copy keeps the build id it had when the app started, so a game
    /// updated since then would be compiled, and its warm recorded and shared, under the old build. One store's discovery:
    /// only before a compile and after the game exits.</summary>
    Game Current(Game g)
    {
        foreach (var source in _sources.Where(s => s.Store == g.Store))
            try
            {
                if (source.Discover().FirstOrDefault(x => x.Id == g.Id) is { } now) return now;
            }
            catch (Exception e) { Log?.Report($"{source.Store}: discovery failed: {e.Message}"); }
        return g;
    }

    GameState Find(string gameId) => Games.FirstOrDefault(s => s.Game.Id == gameId)
                                     ?? throw new ArgumentException($"unknown game '{gameId}' (scan first)");

    string RecordingPath(string gameId) => Path.Combine(Store.GameDir(gameId), "recording.db");

    string RayQueryKeysPath(string gameId) => Path.Combine(Store.GameDir(gameId), "rayquery.keys");

    /// <summary>The planner's check on the union of this PC's recording and the community database's, without writing it:
    /// the union has draws when either has.</summary>
    PlanCheck CheckRecordings(Game g, EngineInfo engine)
    {
        var local = File.Exists(RecordingPath(g.Id)) ? new Recording(RecordingPath(g.Id)) : null;
        var community = CommunityInUse(g.Id) != null ? new Recording(Path.Combine(Store.GameDir(g.Id), "community.db")) : null;
        var check = _planner.Check(g, engine, local ?? community, Vendor.Caps);
        return check.Readiness == Readiness.NeedsRecording && local != null && community != null ? _planner.Check(g, engine, community, Vendor.Caps) : check;
    }

    /// <summary>The recording to plan and warm from, as a proxy db in <paramref name="work"/>: this PC's, merged with the
    /// community database's when one is in use (docs/plan-db.md §6), with every shader it names by hash read back from the
    /// install (the game's index, then the middleware DLLs next to its exe). Null = no recording.</summary>
    Recording? PrepareRecording(Game game, EngineInfo engine, ShaderIndex index, string work, CancellationToken ct)
    {
        var local = RecordingPath(game.Id);
        var community = CommunityInUse(game.Id) != null ? Path.Combine(Store.GameDir(game.Id), "community.db") : null;
        if (!File.Exists(local) && community == null) return null;
        Directory.CreateDirectory(work);
        var src = local;
        if (community != null) Community.Union(local, community, src = Path.Combine(work, "merged.db"));
        var db = Path.Combine(work, "recording.db");
        var r = Rehydrate.Run(src, db, game, engine, _reader, index, null, ct, h => Middleware.Blobs(game, h));
        if (src != local) File.Delete(src);
        if (r.Found > 0 || !r.Complete)
            Log?.Report($"{game.Name}: recording rehydrated from the install ({r.Found} shaders"
                + (r.FoundInMiddleware > 0 ? $", {r.FoundInMiddleware} of them from middleware DLLs" : "")
                + (r.Complete ? ")" : $", {r.Missing.Count} not in this install: those PSOs are skipped)"));
        return new Recording(db);
    }

    CommunityDownload? CommunityInUse(string gameId) => Settings.UseCommunityDb ? Community.Downloaded(Store.GameDir(gameId)) : null;

    /// <summary>After a scan, the setting turned on, or a sign-in with "db" (the app): in the background unless a pass is running (default: every scanned game); see <see cref="SyncCommunity(IReadOnlyList{Game})"/>.</summary>
    public void StartCommunitySync(IEnumerable<GameState>? states = null)
    {
        var games = (states ?? Games).Where(s => s.Engine != null).Select(s => s.Game).ToList();
        lock (_scanLock)
            if (CommunitySync.IsCompleted && Community != null && Settings.UseCommunityDb) CommunitySync = Task.Run(() => SyncCommunity(games));
    }

    /// <summary>The manifest, then a download for every game whose build has an entry other than the one it has; each game
    /// that got one, or whose entry the manifest check changed (signed out nothing downloads), is re-evaluated. Never throws.</summary>
    async Task SyncCommunity(IReadOnlyList<Game> games)
    {
        try
        {
            foreach (var g in games)
                if (await SyncCommunity(g, null, CancellationToken.None) || DbEntryChanged(g)) Refresh(g);
        }
        catch (Exception e) { Log?.Report($"community database: {e.Message}"); }
    }

    bool DbEntryChanged(Game g)
    {
        if (Games.FirstOrDefault(s => s.Game.Id == g.Id) is not { } s) return false;
        var m = LocalManifest();
        var e = m != null ? DbEntry(m, g, Store.LoadGame(g.Id)) : null;
        return s.InCommunityDb != (m != null ? e != null : null) || s.CommunityDbPsos != (e?.Psos ?? 0);
    }

    /// <summary>One game: by its fresh index's content hash when given, else by its store build (no build id: its last
    /// index). True when a new recording was downloaded. Without a content hash (not a compile), a warmed game's pipelines
    /// the download adds and its plan lacks count in <see cref="GameRecord.RecordedSinceWarm"/>.</summary>
    async Task<bool> SyncCommunity(Game g, string? contentHash, CancellationToken ct)
    {
        if (Community is not { } community || !Settings.UseCommunityDb || await community.ManifestAsync(ct) is not { } manifest) return false;
        var entry = contentHash != null ? manifest.Find(g, contentHash) : DbEntry(manifest, g, Store.LoadGame(g.Id));
        var dir = Store.GameDir(g.Id);
        if (entry == null || entry.Object == Community.Downloaded(dir)?.Object) return false;
        // a compile saves its own record after this, and plans and warms the download anyway
        var before = contentHash == null && Store.LoadGame(g.Id).WarmedAt != null
            ? new[] { Path.Combine(dir, "community.db"), RecordingPath(g.Id) }.Where(File.Exists).SelectMany(PipelineKeys).ToHashSet() : null;
        if (await community.DownloadAsync(entry, dir, ct) is not { } got)
        {
            if (community.Problem is { } why) Log?.Report($"{g.Name}: community database: {why}");
            return false;
        }
        Log?.Report($"{g.Name}: community recording downloaded ({got.Psos:N0} pipelines, {entry.Uploaders} contributor{(entry.Uploaders == 1 ? "" : "s")})");
        if (before != null)
        {
            var rec = Store.LoadGame(g.Id);
            var planned = rec.Plan is { } p ? PlanKeys(p.FilePath) : null;
            rec.RecordedSinceWarm += PipelineKeys(Path.Combine(dir, "community.db")).Count(k => !before.Contains(k) && planned?.Contains(k) != true);
            Store.SaveGame(g.Id, rec);
        }
        return true;
    }

    /// <summary>Queues a background pass that shares these games' recordings (default: every game) when
    /// Settings.ShareRecordings is on: after a scan, a recording import, a game's exit, a compile's index, and the setting turned on.
    /// Never blocks the caller; failures are only logged.</summary>
    public void StartSharing(IEnumerable<Game>? games = null)
    {
        if (Sharing is not { } sharing || !Settings.ShareRecordings) return;
        var list = (games ?? Games.Select(s => s.Game)).ToList();
        lock (_scanLock) SharingPass = SharingPass.ContinueWith(_ => Share(sharing, list), TaskScheduler.Default).Unwrap();
    }

    async Task Share(Sharing sharing, List<Game> games)
    {
        string? logged = null;   // a back-off's problem once per pass, not once per game
        foreach (var g in games)
            try
            {
                var rec = Store.LoadGame(g.Id);
                // Only with the content hash of exactly this build: an older build's would alias the new build to the old entry.
                // ponytail: a store without build ids (no store build key) doesn't share; add when the server takes a key without one
                if (g.Version is not { } v || rec.IndexGameVersion != v || rec.IndexContentHash is not { Length: 40 } hash) continue;
                if (await sharing.ShareAsync(Store.GameDir(g.Id), hash, () => UploadMetaOf(g, v, hash),
                        () => Middleware.Detect(g).Where(d => d.Packable).SelectMany(d => Middleware.Scan(d.Path).Containers.Keys)) is { } got)
                {
                    Log?.Report($"{g.Name}: recording shared ({got.Psos:N0} pipelines, {got.NewPsos:N0} new to the community database)");
                    Refresh(g);
                }
                else if (sharing.Problem is { } why && why != logged) Log?.Report($"{g.Name}: {logged = why}");
            }
            catch (Exception e) { Log?.Report($"{g.Name}: sharing the recording failed: {e.Message}"); }
    }

    UploadMeta UploadMetaOf(Game g, string version, string contentHash)
    {
        static string? Clip(string? s) => s is { Length: > 128 } ? s[..128] : s;
        EngineInfo? engine;   // the scan's cache: Games is still empty during an app's first scan
        lock (_scanLock) engine = _scan?.GetValueOrDefault(g.Id)?.Engine;
        return new($"{g.Id}@{version}", contentHash, Clip(engine is { } e ? $"{e.Family} {e.Version} {e.Fork}".TrimEnd() : null),
            Vendor.Vendor.ToString().ToLowerInvariant(), AppVersion.Current.ToString(),
            [.. Middleware.Detect(g).Where(d => d.Packable).Take(64).Select(d => new UploadDll(Clip(d.Name)!, Middleware.Scan(d.Path).ContentHash))]);
    }

    /// <summary>Imports the game folder's scskiller.db (the recorder's inbox) into SCSKiller's copy when it changed since the
    /// last import, then empties it once nothing has it open (<see cref="Recordings"/>). Whatever the inbox holds is merged
    /// by record key, so a recording that started over loses nothing. A copy stored before compact recordings (a proxy db,
    /// or its merge with the community's in recording.all.db) is left to <see cref="MigrateRecordings"/> unless
    /// <paramref name="migrate"/>.</summary>
    bool ImportRecording(Game g, GameRecord rec, bool migrate = false)
    {
        var inbox = new FileInfo(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "scskiller.db"));
        var store = RecordingPath(g.Id);
        var all = Path.Combine(Store.GameDir(g.Id), "recording.all.db");
        var keysFile = Path.Combine(inbox.DirectoryName!, Recordings.KeysFile);
        // keys naming more than the index's shaders with no recording (the data folder was deleted): the recorder would skip blobs nothing has
        if (!File.Exists(store) && File.Exists(keysFile) && Length(keysFile) != 8 + Math.Max(0, Length(Path.Combine(Store.GameDir(g.Id), Sharing.ShippedFile)) - 20)) WriteKeys(g, rec);
        var legacy = Legacy(g.Id);
        if (legacy && !migrate) return false;
        var stamp = inbox.Exists ? $"{inbox.Length}:{inbox.LastWriteTimeUtc.Ticks}" : null;
        var pending = inbox is { Exists: true, Length: > 0 } && stamp != rec.RecordingInbox;
        if (!pending && Length(store) == 0 && legacy)   // only a community merge (or an empty copy): nothing of this PC's to keep
        {
            try { foreach (var f in new[] { all, all + ".key", store }) File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{g.Name}: could not delete {all}: {e.Message}"); return false; }
            return true;
        }
        if (!pending && !legacy)
        {
            if (inbox is not { Exists: true, Length: > 0 } || !Recordings.Rotate(inbox.FullName, inbox.Length)) return false;   // imported while the game held it
            rec.RecordingInbox = null;
            return true;
        }
        int added;
        try
        {
            lock (RecordingLock)
            {
                Directory.CreateDirectory(Store.GameDir(g.Id));
                var shipped = Shipped(g, rec);
                var keys = Recordings.Merge(store, pending ? inbox.FullName : null, shipped == null ? null : shipped.Contains);
                File.Delete(all);
                File.Delete(all + ".key");
                // only what the current plan doesn't compile: a session re-recording planned pipelines adds nothing to compile
                var planned = keys.Count > 0 && rec.Plan is { } p ? PlanKeys(p.FilePath) : null;
                // what a community download brought was counted when it came
                var community = keys.Count > 0 && CommunityInUse(g.Id) != null ? PipelineKeys(Path.Combine(Store.GameDir(g.Id), "community.db")) : null;
                added = keys.Count(k => planned?.Contains(k) != true && community?.Contains(k) != true);
                (rec.RecordingInbox, rec.RecordingIndexHash) = (stamp, shipped != null ? rec.IndexContentHash : null);
                if (keys.Count > 0) rec.RecordingImportedAt = DateTimeOffset.Now;
                rec.RecordedSinceWarm += added;
                WriteKeys(g, rec);
                // emptied: what it gets next is new, whatever its size and write time
                if (pending && Recordings.Rotate(inbox.FullName, inbox.Length)) rec.RecordingInbox = null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log?.Report($"{g.Name}: could not import {inbox.FullName}: {e.Message}");
            return false;
        }
        if (added > 0) StartSharing([g]);   // a recording session ended
        UpdateRecorderIni(g, rec);
        return true;
    }

    /// <summary>Recording store writes (imports, compaction, migration), one at a time.</summary>
    static readonly Lock RecordingLock = new();

    /// <summary>The game's recording is stored as before compact recordings: <see cref="MigrateRecordings"/> converts it.</summary>
    bool Legacy(string gameId) => File.Exists(RecordingPath(gameId)) && !PsoDb.IsCompact(RecordingPath(gameId))
                                  || File.Exists(Path.Combine(Store.GameDir(gameId), "recording.all.db"));

    /// <summary>The install is the build last indexed, by what tells a game update (as <see cref="WarmChanged"/>): the
    /// store's build id, or without one the exe's size and write time.</summary>
    static bool IndexIsInstalled(Game g, GameRecord r) =>
        g.Version != null && r.IndexGameVersion != null ? g.Version == r.IndexGameVersion : r.IndexExeStamp == ExeStamp(g);

    /// <summary>The shaders the install gives back, by the index of exactly this build (index.shaders); null when that isn't
    /// known (the game changed since it was indexed, or never was), and the recording then keeps every shader's bytes.</summary>
    HashSet<string>? Shipped(Game g, GameRecord rec) =>
        IndexIsInstalled(g, rec) && rec.IndexContentHash is { } hash ? Sharing.Shipped(Store.GameDir(g.Id), hash) : null;

    /// <summary>Writes the recorder's <see cref="Recordings.KeysFile"/> when the recorder is ours: what it needn't record again,
    /// and the last index's shaders, which it names by hash only while the install is that build
    /// (<see cref="IndexIsInstalled"/>, noted in <see cref="GameRecord.KeysIndexHash"/>). Without the file the recorder
    /// records everything with its bytes, which the next import drops. Without the recorder the file is deleted. Returns
    /// the records it leaves out (<see cref="Recordings.WriteKeys"/>).</summary>
    int WriteKeys(Game g, GameRecord rec)
    {
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var keys = Path.Combine(dir, Recordings.KeysFile);
        try
        {
            if (IsOurProxy(Path.Combine(dir, "d3d12.dll")))
            {
                var shipped = rec.IndexContentHash is { } h ? Sharing.Shipped(Store.GameDir(g.Id), h) : null;
                var installed = shipped != null && IndexIsInstalled(g, rec);
                var left = Recordings.WriteKeys(RecordingPath(g.Id), shipped, keys, installed);
                rec.KeysIndexHash = installed ? rec.IndexContentHash : null;
                return left;
            }
            File.Delete(keys);
            rec.KeysIndexHash = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't write {keys}: {e.Message}"); }
        return 0;
    }

    /// <summary>After indexing a build the recording wasn't checked against: shader bytes the index now has are dropped, and
    /// the keys file names this build's shaders and stops naming records whose shaders it no longer has.</summary>
    void CompactRecording(Game g, GameRecord rec, ShaderIndex index)
    {
        var same = rec.RecordingIndexHash == index.ContentHash;
        if (same && rec.KeysIndexHash == index.ContentHash || Legacy(g.Id)) return;
        try
        {
            lock (RecordingLock)
            {
                if (!same && File.Exists(RecordingPath(g.Id))) Recordings.Merge(RecordingPath(g.Id), null, index.Shaders.ContainsKey);
                rec.RecordingIndexHash = index.ContentHash;
                if (WriteKeys(g, rec) is > 0 and var gone)
                    Log?.Report($"{g.Name}: {gone} recorded pipelines name shaders this build doesn't ship: they're skipped when compiling, and recorded again if the game still creates them");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log?.Report($"{g.Name}: compacting the recording failed: {e.Message}"); }
    }

    /// <summary>The keys (as <see cref="PsoDb.Rec.Key"/>) of a recording's pipeline records: every record but the 'B' blobs,
    /// the 'N' NVAPI states and a community recording's 'L' flags.</summary>
    static HashSet<string> PipelineKeys(string db) => PsoDb.Read(db).Where(r => r.Tag is not ('B' or 'N' or 'L')).Select(r => r.Key).ToHashSet();

    /// <summary>The last scan's pass converting recordings stored before compact recordings (<see cref="MigrateRecordings"/>).</summary>
    public Task RecordingMigration { get; private set; } = Task.CompletedTask;

    void StartMigration(IEnumerable<GameState> states)
    {
        var games = states.Select(s => s.Game).Where(g => Legacy(g.Id)).ToList();
        lock (_scanLock)
            if (games.Count > 0 && RecordingMigration.IsCompleted) RecordingMigration = Task.Run(() => MigrateRecordings(games));
    }

    /// <summary>Once per game: its proxy-db copy becomes a compact one, the game folder's scskiller.db is imported into it
    /// and emptied, and recording.all.db goes. Never while a compile runs (it waits for the queue) or the game runs (the next
    /// scan resumes); the old file is replaced only once the new one reads back the same (<see cref="PsoDb.WriteCompact"/>).</summary>
    void MigrateRecordings(IReadOnlyList<Game> games)
    {
        foreach (var g in games)
        {
            while (true)
            {
                Task queue;
                lock (_lock)
                {
                    if (_current == null) break;
                    queue = _worker ?? Task.CompletedTask;
                }
                queue.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(1));
            }
            if (IsPlaying(g.Id) || ProcessNames().Contains(Path.GetFileNameWithoutExtension(g.ExePath))) continue;
            try
            {
                var rec = Store.LoadGame(g.Id);
                var before = RecordingFiles(g).Sum(Length);
                if (!ImportRecording(g, rec, migrate: true)) continue;
                Store.SaveGame(g.Id, rec);
                Log?.Report($"{g.Name}: recording stored compactly ({before >> 20} MB -> {RecordingFiles(g).Sum(Length) >> 20} MB)");
                Refresh(g);
            }
            catch (Exception e) { Log?.Report($"{g.Name}: storing the recording compactly failed: {e.Message}"); }
        }
    }

    public static bool IsOurProxy(string dll) => File.Exists(dll) && File.ReadAllBytes(dll).AsSpan().IndexOf(ProxyMarker) >= 0;

    static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    // Recorder skip reasons: short and stable, the Settings line counts games by them.
    public const string SkipAntiCheat = "anti-cheat", SkipForeignDll = "another d3d12.dll is already there",
        SkipModNotChainable = "another d3d12.dll is there that stops working renamed",
        SkipVulkanMod = "vkd3d-proton runs the game on Vulkan, whose pipelines a D3D12 warm doesn't compile",
        SkipNeedsAdmin = "the game folder needs administrator", SkipNotDx12 = "not DirectX 12", SkipUnsupported = "not supported yet";

    /// <summary>Null = compatible. Writability is only known by writing (Reconcile remembers a refusal), except WindowsApps.
    /// <paramref name="modSkip"/>: <see cref="ModSkip"/>.</summary>
    public static string? RecorderSkip(GameState s, string? modSkip) =>
        s.AntiCheat != AntiCheat.None ? SkipAntiCheat   // any value but None, "Other" included
        : s.Engine == null || s.Status == GameStatus.Unsupported ? SkipUnsupported
        : !s.Engine.GraphicsApi.Contains("D3D12") ? SkipNotDx12   // the proxy is d3d12.dll; "D3D11 or D3D12" may run on it
        : modSkip != null ? modSkip   // ReShade, OptiScaler, another wrapper: never replaced, chained only when the user asks
        : s.Game.ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ? SkipNeedsAdmin
        : null;

    /// <summary>The name a mod's d3d12.dll gets when the recorder chains to it (the proxy's scskiller.ini next=). It keeps the
    /// d3d12 prefix for a wrapper that reads its role from its own name by prefix (ReShade's d3d*).</summary>
    public const string ChainName = "d3d12.scskiller-next.dll";

    /// <summary>A mod's d3d12.dll in the recorder's place (or chained to it): null = none, or the user chose to record alongside
    /// it (<see cref="GameRecord.RecordAlongsideMod"/>) and it can be chained; else the skip reason. Turning the choice off
    /// while chained gives a skip, so Reconcile removes the recorder and puts the mod back.</summary>
    static string? ModSkip(string dir, GameRecord rec, bool ours)
    {
        var dll = Path.Combine(dir, "d3d12.dll");
        var mod = rec.RecorderChained is { } c ? Path.Combine(dir, c.Name) : !ours && File.Exists(dll) ? dll : null;
        return mod == null ? null : !rec.RecordAlongsideMod ? SkipForeignDll
            : ChainBlocker(mod) switch { null => null, SkipVulkanMod => SkipVulkanMod, _ => SkipModNotChainable };
    }

    /// <summary>Why a mod's d3d12.dll can't be chained (renamed to <see cref="ChainName"/>); null = it can. OptiScaler and
    /// Special K pick their role from their own file name, so renamed they'd stop working. vkd3d-proton (its own strings
    /// name it): <see cref="SkipVulkanMod"/>.</summary>
    public static string? ChainBlocker(string dll)
    {
        string? export, product;
        bool vkd3d;
        try
        {
            (export, product) = (Middleware.ExportName(dll), FileVersionInfo.GetVersionInfo(dll).ProductName);
            vkd3d = File.ReadAllBytes(dll).AsSpan().IndexOf("vkd3d-proton"u8) >= 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"{Path.GetFileName(dll)} can't be read"; }
        foreach (var n in new[] { export, product })
            if (n != null && new[] { "OptiScaler", "SpecialK", "Special K" }.Any(m => n.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
                return $"{n} picks its role from its file name";
        return vkd3d ? SkipVulkanMod : null;
    }

    /// <summary>What the game folder's d3d12.dll that isn't SCSKiller's (or the one chained to it) calls itself, for "Record
    /// alongside &lt;mod&gt;"; null = none there.</summary>
    public string? RecorderMod(string gameId)
    {
        var dir = Path.GetDirectoryName(Find(gameId).Game.ExePath)!;
        return ModName(dir, Store.LoadGame(gameId), IsOurProxy(Path.Combine(dir, "d3d12.dll")));
    }

    static string? ModName(string dir, GameRecord rec, bool ours)
    {
        var dll = Path.Combine(dir, rec.RecorderChained?.Name ?? "d3d12.dll");
        if (!File.Exists(dll) || rec.RecorderChained == null && ours) return null;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(dll);
            return new[] { v.ProductName, v.FileDescription, Middleware.ExportName(dll) }.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? "d3d12.dll";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "d3d12.dll"; }
    }

    public void SetRecordAlongsideMod(string gameId, bool on)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            var rec = Store.LoadGame(gameId);
            rec.RecordAlongsideMod = on;
            Store.SaveGame(gameId, rec);
            if (!Reconcile(s, ProcessNames())) Refresh(s.Game);
        }
    }

    public static bool RecorderEffective(RecorderOverride o, bool recordAllGames, string? skip) =>
        skip == null && (o == RecorderOverride.On || (o == RecorderOverride.Default && recordAllGames));

    readonly object _recorderLock = new();
    // game id -> SkipNeedsAdmin (the last write was refused); game id -> the last reconcile's pending or failed change
    readonly ConcurrentDictionary<string, string> _recorderSkips = new(), _recorderNotes = new();

    public void InstallRecorder(string gameId)
    {
        SetRecorderOverride(gameId, RecorderOverride.On);
        var s = Find(gameId);
        if (!s.RecorderInstalled || s.RecorderNote != null)   // installed with a note: an update waits or failed
            throw new InvalidOperationException($"{s.Game.Name}: recorder not {(s.RecorderInstalled ? "updated" : "installed")}: {s.RecorderNote ?? s.RecorderSkip}");
    }

    public bool ClearRecording(string gameId)
    {
        var s = Find(gameId);
        lock (_lock)
            if (_current == gameId) throw new InvalidOperationException($"a compile of {s.Game.Name} is in progress");
        lock (_recorderLock)
        {
            if (IsPlaying(gameId) || ProcessNames().Contains(Path.GetFileNameWithoutExtension(s.Game.ExePath)))
                throw new InvalidOperationException($"{s.Game.Name} is running");
            var files = RecordingFiles(s.Game).Select(f => new FileInfo(f)).Where(f => f.Exists).ToList();
            if (files.Count == 0) return false;
            AppCacheFiles.DeleteAll(files);   // all or none; throws "files in use by <process>"
            var rec = Store.LoadGame(gameId);
            // a changed recording: the next compile re-plans, and the scan's planner check runs again without it
            (rec.RecordingImportedAt, rec.RecordedSinceWarm, rec.RecordingInbox, rec.RecordingIndexHash) = (DateTimeOffset.Now, 0, null, null);
            WriteKeys(s.Game, rec);   // it named the deleted copy's blobs and records
            Store.SaveGame(gameId, rec);
            RecorderLog($"{s.Game.Name}: recording cleared ({string.Join(", ", files.Select(f => f.FullName))})");
            UpdateRecorderIni(s.Game, rec);
            Refresh(s.Game);
            return true;
        }
    }

    public void UninstallRecorder(string gameId)
    {
        SetRecorderOverride(gameId, RecorderOverride.Off);
        if (Find(gameId) is { RecorderInstalled: true } s)
            throw new InvalidOperationException($"{s.Game.Name}: recorder not removed: {s.RecorderNote}");
    }

    public void SetRecorderOverride(string gameId, RecorderOverride value)
    {
        lock (_recorderLock)
        {
            var s = Find(gameId);
            var rec = Store.LoadGame(gameId);
            rec.Recorder = value;
            Store.SaveGame(gameId, rec);
            if (!Reconcile(s, ProcessNames())) Refresh(s.Game);   // the override shows either way
        }
    }

    public void ReconcileRecorders(string? gameId = null)
    {
        lock (_recorderLock)
        {
            var running = ProcessNames();
            foreach (var s in Games.Where(s => gameId == null || s.Game.Id == gameId))
                Reconcile(s, running);
        }
    }

    /// <summary>True when the game was re-evaluated; a running game's change waits for it to exit.</summary>
    bool Reconcile(GameState s, IReadOnlySet<string> running)
    {
        var (g, id) = (s.Game, s.Game.Id);
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var dll = Path.Combine(dir, "d3d12.dll");
        var rec = Store.LoadGame(id);
        bool ours = IsOurProxy(dll), changed = false;
        if (rec.Recorder == null)   // not migrated: a recorder the user installed stays theirs
        {
            rec.Recorder = ours ? RecorderOverride.On : RecorderOverride.Default;
            Store.SaveGame(id, rec);
            changed = true;
        }
        if ((ours || rec.RecorderFiles.Count > 0 || rec.RecorderChained != null) && rec.RecorderExe != g.ExePath)
        {
            rec.RecorderExe = g.ExePath;
            Store.SaveGame(id, rec);
        }
        bool want = RecorderEffective(rec.Recorder.Value, Settings.RecordAllGames, RecorderSkip(s, ModSkip(dir, rec, ours)));
        // another SCSKiller build's proxy, never a newer one's (a release's under a dev build, all 0.0.0.0)
        bool update = want && ours && ProxySha() is { } sha && Sha256(dll) != sha && FileVersion(dll) <= FileVersion(_proxyDll!);
        string? note = null;
        if (want != ours || update || (!want && (rec.RecorderFiles.Count > 0 || rec.RecorderChained != null)))
        {
            if (running.Contains(Path.GetFileNameWithoutExtension(g.ExePath)))
                note = update ? "updates when the game exits" : want ? "installs when the game exits" : "removed when the game exits";
            else
                try
                {
                    if (want) Install(g, rec); else Uninstall(g, rec);
                    _recorderSkips.TryRemove(id, out _);
                    RecorderLog($"{g.Name}: recorder {(update ? "updated" : want ? "installed" : "removed")} ({dir})");
                    changed = true;
                }
                catch (UnauthorizedAccessException e) when (update)   // the old recorder still works: not a skip
                {
                    note = "couldn't update: " + e.Message;
                    RecorderLog($"{g.Name}: recorder {note}");
                }
                catch (UnauthorizedAccessException)
                {
                    if (_recorderSkips.GetValueOrDefault(id) != SkipNeedsAdmin)   // logged once, re-tried on every reconcile
                    {
                        _recorderSkips[id] = SkipNeedsAdmin;
                        RecorderLog($"{g.Name}: recorder not installed: {SkipNeedsAdmin} ({dir})");
                        changed = true;
                    }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException)
                {
                    note = (update ? "couldn't update: " : want ? "couldn't install: " : "couldn't remove: ") + e.Message;
                    RecorderLog($"{g.Name}: recorder {note}");
                }
        }
        else if (want && !running.Contains(Path.GetFileNameWithoutExtension(g.ExePath)))
            UpdateRecorderIni(g, rec);
        if (note != _recorderNotes.GetValueOrDefault(id))
        {
            if (note == null) _recorderNotes.TryRemove(id, out _); else _recorderNotes[id] = note;
            changed = true;
        }
        if (changed) Refresh(g);
        return changed;
    }

    /// <summary>Also to recorders.log in the data folder: the app has no other log.</summary>
    void RecorderLog(string line)
    {
        Log?.Report(line);
        RecordersLog(Store.DataDir, line);
    }

    static void RecordersLog(string dataDir, string line)
    {
        try { File.AppendAllText(Path.Combine(dataDir, "recorders.log"), $"{DateTimeOffset.Now:u} {line}{Environment.NewLine}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    string? _proxySha;
    string? ProxySha() => _proxyDll == null || !File.Exists(_proxyDll) ? null : _proxySha ??= Sha256(_proxyDll);

    static Version FileVersion(string path) =>
        FileVersionInfo.GetVersionInfo(path) is var v ? new(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart) : new();

    static IReadOnlySet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p) names.Add(p.ProcessName);
        return names;
    }

    void Install(Game g, GameRecord rec)
    {
        var antiCheat = GameFiles.DetectAntiCheat(g);   // fresh, not the cached scan: a patch may have added one
        if (antiCheat != AntiCheat.None)
            throw new InvalidOperationException($"{g.Name} uses {antiCheat}: a d3d12.dll in its folder could get the account banned. Not installing.");
        var src = _proxyDll ?? throw new FileNotFoundException("the proxy d3d12.dll was not found next to the app");
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var dll = Path.Combine(dir, "d3d12.dll");
        var ini = Path.Combine(dir, "scskiller.ini");
        bool iniOurs = !File.Exists(ini) || (rec.RecorderFiles.TryGetValue("scskiller.ini", out var h) && h == Sha256(ini));
        bool chain = File.Exists(dll) && !IsOurProxy(dll);
        rec.RecorderExe = g.ExePath;
        if (chain)
        {
            if (!rec.RecordAlongsideMod)
                throw new InvalidOperationException($"{dll} already exists and is not SCSKiller's (another mod or wrapper). Not replacing it.");
            if (ChainBlocker(dll) is { } why) throw new InvalidOperationException($"{dll}: {why}. Not renaming it.");
            if (!iniOurs) throw new InvalidOperationException($"{ini} is not SCSKiller's: the chain to {dll} needs its own. Not renaming it.");
            var to = Path.Combine(dir, ChainName);
            if (File.Exists(to)) throw new InvalidOperationException($"{to} already exists. Not renaming {dll} over it.");
            rec.RecorderChained = new ChainedDll(ChainName, Sha256(dll));
            Store.SaveGame(g.Id, rec);   // before the rename: no renamed file is ever missing from the manifest
            File.Move(dll, to);
        }
        try { File.Copy(src, dll, overwrite: true); }
        catch when (chain)
        {
            File.Move(Path.Combine(dir, ChainName), dll);
            rec.RecorderChained = null;
            Store.SaveGame(g.Id, rec);
            throw;
        }
        rec.RecorderFiles["d3d12.dll"] = Sha256(dll);
        WriteKeys(g, rec);   // a recorder installed again records only what's new
        if (iniOurs)
        {
            File.WriteAllText(ini, IniText(g, rec));
            rec.RecorderFiles["scskiller.ini"] = Sha256(ini);
        }   // else: the user's own scskiller.ini, left alone and not tracked
        Store.SaveGame(g.Id, rec);
    }

    string IniText(Game g, GameRecord rec)
    {
        var dir = Path.GetDirectoryName(g.ExePath)!;
        var next = rec.RecorderChained is { } c && File.Exists(Path.Combine(dir, c.Name)) ? c.Name : null;
        return RecorderIni
            + (DbCap(g) is { } cap
                ? $"; the recording limit per game (Settings): no new records once scskiller.db has this many bytes\r\nmax_db_bytes={cap}\r\n" : "")
            + (next == null ? "" : $"; the game's own d3d12.dll (a mod), renamed by SCSKiller and put back when the recorder is removed\r\nnext={next}\r\n");
    }

    /// <summary>Rewrites SCSKiller's own scskiller.ini when its limit line is out of date (the setting changed, an import
    /// grew the copy, a clear); a user's own ini is left alone. The proxy reads it when the game starts.</summary>
    void UpdateRecorderIni(Game g, GameRecord rec)
    {
        try
        {
            var ini = Path.Combine(Path.GetDirectoryName(g.ExePath)!, "scskiller.ini");
            if (!File.Exists(ini) || !rec.RecorderFiles.TryGetValue("scskiller.ini", out var h) || h != Sha256(ini)) return;
            var text = IniText(g, rec);
            if (File.ReadAllText(ini) == text) return;
            File.WriteAllText(ini, text);
            rec.RecorderFiles["scskiller.ini"] = Sha256(ini);
            Store.SaveGame(g.Id, rec);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { RecorderLog($"{g.Name}: couldn't update the recording limit: {e.Message}"); }
    }

    /// <summary>The recorder's data files stay: the recording is imported.</summary>
    void Uninstall(Game g, GameRecord rec)
    {
        RemoveRecorder(Path.GetDirectoryName(g.ExePath)!, rec, g.Name, RecorderLog);
        ImportRecording(g, rec, migrate: true);
        Store.SaveGame(g.Id, rec);
    }

    /// <summary>Deletes the files Install wrote whose hash still matches (a dll only if it is our proxy) and any proxy of
    /// ours, and puts a chained mod back.</summary>
    static void RemoveRecorder(string dir, GameRecord rec, string name, Action<string> log)
    {
        foreach (var (file, hash) in rec.RecorderFiles.ToList())
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path) && Sha256(path) == hash && (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || IsOurProxy(path))) File.Delete(path);
            else if (File.Exists(path)) log($"{name}: left {path}: changed since SCSKiller installed it");
            rec.RecorderFiles.Remove(file);
        }
        // a recorder installed before SCSKiller tracked its files (or by hand) is still ours: the proxy carries our export
        var dll = Path.Combine(dir, "d3d12.dll");
        if (IsOurProxy(dll)) File.Delete(dll);
        if (rec.RecorderChained is { } c)
        {
            var from = Path.Combine(dir, c.Name);
            if (!File.Exists(from)) log($"{name}: {from} (the mod's d3d12.dll SCSKiller renamed) is gone");
            else if (File.Exists(dll)) log($"{name}: left {from}: another d3d12.dll is in its place");
            else
            {
                if (Sha256(from) != c.Sha256) log($"{name}: {from} changed since SCSKiller renamed it (the mod updated itself?): put back as it is");
                File.Move(from, dll);
            }
            rec.RecorderChained = null;
        }
        rec.RecorderExe = null;
    }

    static readonly TimeSpan UninstallBudget = TimeSpan.FromSeconds(20);   // Velopack kills its uninstall hook at 30 s

    /// <summary>SCSKiller's own uninstall (installer.md §5): takes the recorder out of every game folder the data folder
    /// records one in (<see cref="GameRecord.RecorderExe"/>, no scan), then its data files once the data folder's
    /// recording holds the game's scskiller.db. A running game's folder is left. What it did goes to recorders.log.</summary>
    public static void RemoveAllRecorders(AppStore store, IReadOnlySet<string>? running = null)
    {
        var clock = Stopwatch.StartNew();
        running ??= RunningProcessNames();
        void Log(string line) => RecordersLog(store.DataDir, "uninstall: " + line);
        var games = Path.Combine(store.DataDir, "games");
        var removed = new List<(string Id, string Dir)>();
        foreach (var id in (Directory.Exists(games) ? Directory.GetDirectories(games) : []).Select(d => Path.GetFileName(d)).Order())
            try
            {
                var rec = store.LoadGame(id);   // the folder name is the id with ':' replaced, which GameDir leaves as it is
                if (rec.RecorderExe is not { } exe) continue;
                var dir = Path.GetDirectoryName(exe)!;
                if (clock.Elapsed > UninstallBudget) Log($"out of time: recorder left in {dir}");
                else if (running.Contains(Path.GetFileNameWithoutExtension(exe))) Log($"{exe} is running: recorder left in {dir}");
                else
                {
                    RemoveRecorder(dir, rec, dir, Log);
                    store.SaveGame(id, rec);
                    removed.Add((id, dir));
                    Log($"recorder removed from {dir}");
                }
            }
            catch (Exception e) { Log($"{id}: {e.Message}"); }
        foreach (var (id, dir) in removed)
            try
            {
                if (clock.Elapsed > UninstallBudget) Log($"out of time: the recorder's data files left in {dir}");
                else RemoveRecorderData(dir, Path.Combine(store.GameDir(id), "recording.db"), store.LoadGame(id).RecordingInbox);
            }
            catch (Exception e) { Log($"{dir}: {e.Message}"); }
    }

    /// <summary>The files the proxy writes, and its keys file. scskiller.db goes once <paramref name="recording"/> holds it:
    /// merged into it first unless it was imported as it is (<paramref name="imported"/>: its size and write time then).</summary>
    static void RemoveRecorderData(string dir, string recording, string? imported)
    {
        if (!Directory.Exists(dir)) return;
        var src = new FileInfo(Path.Combine(dir, "scskiller.db"));
        if (src is { Exists: true, Length: > 0 } && $"{src.Length}:{src.LastWriteTimeUtc.Ticks}" != imported)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recording)!);
            lock (RecordingLock) Recordings.Merge(recording, src.FullName, null);
        }
        foreach (var f in RecorderDataFiles.Append(Recordings.KeysFile)) File.Delete(Path.Combine(dir, f));
    }

    // _queue is kept in run order: the running item first, then the waiting ones, then the finished ones (Done, Failed,
    // Stopped) until the next StartQueue. Normal items run only while QueueRunning; "when idle" items run by themselves
    // whenever the PC is idle, after any normal item that is waiting while the queue runs (one item at a time).

    public bool QueueRunning { get { lock (_lock) return _running; } }

    /// <summary>Adds the game at the end of the waiting items. Runs when the queue is started (or right away if it is
    /// running). On a queued "when idle" item: it becomes a normal item (runs with the queue, in the foreground).</summary>
    public void Enqueue(string gameId) => Add(gameId, whenIdle: false);

    /// <summary>A background rebuild (idle priority, Settings.BackgroundThreads, paused while a game runs if
    /// PauseWhileGaming) that starts once the user is idle for <see cref="IdleAfter"/>, pauses on input and resumes when
    /// idle again, whether or not the queue was started. Enqueue or ResumeQueue on it = run it in the foreground.</summary>
    public void EnqueueWhenIdle(string gameId) => Add(gameId, whenIdle: true);

    /// <summary>A waiting "when idle" item's <see cref="QueueItem.Note"/>: it doesn't run with the queue.</summary>
    public const string WhenIdleNote = "starts when the PC is idle";

    static bool Finished(QueueStage s) => s is QueueStage.Done or QueueStage.Failed or QueueStage.Stopped;

    int WaitingStart() => _queue.Count > 0 && _queue[0].GameId == _current ? 1 : 0;   // under _lock

    void Add(string gameId, bool whenIdle, bool planCheck = false)
    {
        QueueItem item;
        lock (_lock)
        {
            var i = _queue.FindIndex(q => q.GameId == gameId);
            bool queued = i >= 0 && !Finished(_queue[i].Stage);
            bool promote = !whenIdle && _whenIdle.Remove(gameId);
            if (!planCheck) promote |= _planOnly.Remove(gameId);   // a compile, not a plan check: listed with the queue
            if (queued && !promote) return;
            if (planCheck) _planOnly.Add(gameId);
            if (whenIdle) _whenIdle.Add(gameId);
            if (queued) _queue[i] = item = _queue[i] with { Error = null, Note = whenIdle ? _queue[i].Note : null, PlanCheck = false };
            else
            {
                if (i >= 0) _queue.RemoveAt(i);   // finished before: queued again
                item = new QueueItem(gameId, QueueStage.Waiting, null, null, whenIdle ? WhenIdleNote : null, planCheck);
                var end = _queue.FindIndex(q => Finished(q.Stage));
                _queue.Insert(end < 0 ? _queue.Count : end, item);
            }
            if (whenIdle || _running) _worker ??= Task.Run(Work);
        }
        QueueChanged?.Invoke(item);
    }

    /// <summary>Clears Done and Failed items, puts Stopped ones back first among the waiting (they continue where they
    /// stopped), then runs the normal waiting items in order; the queue stops when none is left. A "when idle" item that
    /// is running goes on in the foreground so the queue isn't stuck behind it.</summary>
    public void StartQueue()
    {
        List<QueueItem> changed;
        lock (_lock)
        {
            changed = _queue.Where(q => q.Stage is QueueStage.Done or QueueStage.Failed).ToList();
            var stopped = _queue.Where(q => q.Stage == QueueStage.Stopped).Select(q => q with { Stage = QueueStage.Waiting, Error = null, Note = null }).ToList();
            _queue.RemoveAll(q => Finished(q.Stage));
            _queue.InsertRange(WaitingStart(), stopped);
            if (_current != null) _whenIdle.Remove(_current);
            _running = _queue.Any(q => q.Stage == QueueStage.Waiting && !_whenIdle.Contains(q.GameId));
            if (_running) _worker ??= Task.Run(Work);
            changed.AddRange(_queue);
        }
        foreach (var q in changed) QueueChanged?.Invoke(q);   // removed ones too, so a list view refreshes
    }

    /// <summary>Moves a waiting item to position <paramref name="index"/> among the listed waiting items, plan checks left
    /// out as the lists leave them out (0 = next, clamped).</summary>
    public void MoveInQueue(string gameId, int index)
    {
        QueueItem item;
        lock (_lock)
        {
            var i = _queue.FindIndex(q => q.GameId == gameId);
            if (i < 0 || _queue[i].Stage != QueueStage.Waiting) return;
            item = _queue[i];
            _queue.RemoveAt(i);
            int first = WaitingStart();
            var listed = Enumerable.Range(first, _queue.Count - first).Where(j => _queue[j] is { Stage: QueueStage.Waiting, PlanCheck: false }).ToList();
            index = Math.Clamp(index, 0, listed.Count);
            _queue.Insert(index < listed.Count ? listed[index] : listed.Count > 0 ? listed[^1] + 1 : first, item);
        }
        QueueChanged?.Invoke(item);
    }

    /// <summary>Drops the item; the running one is stopped gracefully and the queue goes on with the next.</summary>
    public void Remove(string gameId)
    {
        QueueItem item;
        bool running;
        lock (_lock)
        {
            _whenIdle.Remove(gameId);
            _planOnly.Remove(gameId);
            var i = _queue.FindIndex(q => q.GameId == gameId);
            if (i < 0) return;
            item = _queue[i];
            _queue.RemoveAt(i);
            running = _current == gameId;
        }
        QueueChanged?.Invoke(item);
        if (running) StopCurrent();
    }

    public void PauseQueue()
    {
        _go.Reset();
        IWarmRun? run;
        lock (_lock) run = _run;
        run?.Pause();
        SetCurrent(QueueStage.Paused);
    }

    /// <summary>Also means "run it now" for a "when idle" item. A warm stays suspended while a game runs (Watch resumes it).</summary>
    public void ResumeQueue()
    {
        IWarmRun? run;
        lock (_lock)
        {
            if (_current != null) _whenIdle.Remove(_current);
            run = _run;
        }
        _go.Set();
        if (run == null) SetCurrent(_stage);   // a running warm is resumed by Watch within Poll
    }

    /// <summary>Graceful stop of the running item (Stopped, resumable), and the queue stops: the waiting items wait for
    /// the next StartQueue. "When idle" items still run by themselves.</summary>
    public void StopQueue()
    {
        lock (_lock) _running = false;
        StopCurrent();
    }

    void StopCurrent()
    {
        CancellationTokenSource? cts;
        IWarmRun? run;
        lock (_lock) cts = _itemCts;
        cts?.Cancel();
        lock (_lock) run = _run;   // read after the cancel: a run registered later sees the cancelled token (RunItem)
        run?.Stop();
    }

    void Set(QueueItem item)
    {
        lock (_lock)
        {
            var i = _queue.FindIndex(q => q.GameId == item.GameId);
            if (i < 0) return;   // removed while running
            item = item with { PlanCheck = _queue[i].PlanCheck };   // RunItem's items don't carry it; only Add changes it
            _queue.RemoveAt(i);
            _queue.Insert(Finished(item.Stage) ? _queue.Count : i, item);   // finished items go to the end
        }
        QueueChanged?.Invoke(item);
    }

    void SetCurrent(QueueStage stage, string? note = null)
    {
        QueueItem? item;
        lock (_lock) item = _queue.FirstOrDefault(q => q.GameId == _current);
        if (item != null) Set(item with { Stage = stage, Note = note });
    }

    bool UserIdle => IdleTime() >= IdleAfter;

    bool WaitsForIdle(string id)
    {
        lock (_lock) if (!_whenIdle.Contains(id)) return false;
        return !UserIdle;
    }

    async Task Work()
    {
        while (true)
        {
            string? id = null;
            QueueItem? stopped = null;
            CancellationToken ct = default;
            bool exit;
            lock (_lock)
            {
                var waiting = _queue.Where(q => q.Stage == QueueStage.Waiting).Select(q => q.GameId).ToList();
                var next = _running ? waiting.FirstOrDefault(g => !_whenIdle.Contains(g)) : null;
                if (_running && next == null) (_running, stopped) = (false, _queue.FirstOrDefault());   // nothing left: the queue stops
                var idle = waiting.Where(_whenIdle.Contains).ToList();
                if (exit = next == null && idle.Count == 0) _worker = null;
                else if ((id = next ?? (UserIdle ? idle[0] : null)) != null)
                {
                    var i = _queue.FindIndex(q => q.GameId == id);
                    var item = _queue[i];
                    _queue.RemoveAt(i);
                    _queue.Insert(0, item);   // the running item comes first
                    _current = id;
                    _itemCts = new CancellationTokenSource();
                    ct = _itemCts.Token;
                }
            }
            if (stopped != null) QueueChanged?.Invoke(stopped);   // QueueRunning changed
            if (exit) break;
            if (id == null) { await Task.Delay(Poll); continue; }   // only "when idle" items left and the user is here
            try
            {
                using (Busy.Hold()) await RunItem(id, ct);   // no update applies under a running item (Updates.cs)
            }
            finally { lock (_lock) (_current, _run, _itemCts) = (null, null, null); }
        }
        ReleaseMemory();
    }

    /// <summary>The index, plan and materialize buffers are garbage once the queue is done: give the memory back to
    /// Windows instead of keeping the peak committed while the app sits idle.</summary>
    static void ReleaseMemory() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    /// <summary>Between stages: waits while the queue is paused, or while the user is at the PC for a "when idle" item.
    /// ponytail: index/plan/materialize run to the end of the stage once started (only the warm suspends mid-way); pass
    /// a pause token into the reader/planner if a stage ever gets long.</summary>
    async Task Gate(string id, CancellationToken ct)
    {
        while (!_go.IsSet || WaitsForIdle(id)) await Task.Delay(Poll, ct);
    }

    /// <summary>index -> plan if missing/stale -> materialize into games\&lt;id&gt;\work -> warm -> record -> delete work.</summary>
    async Task RunItem(string id, CancellationToken ct)
    {
        WarmProgress? progress = null;
        long lastDone = -1, crashed = 0;
        var advanced = Stopwatch.StartNew();   // since the done count last moved (or the warm was paused)
        void Stage(QueueStage s, string? error = null)
        {
            if (s is not QueueStage.Paused) _stage = s;
            bool paused = s is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped) && (!_go.IsSet || _pauseWhy != null);
            if (paused || (progress?.Done ?? -1) != lastDone)
            {
                lastDone = progress?.Done ?? -1;
                advanced.Restart();
            }
            // a finished warm's note: what failed (the driver rejected it) and what was skipped (a shader not in this install);
            // a warm that stopped moving: for how long (the estimate would be a guess)
            var note = paused ? _pauseWhy : s is QueueStage.Done or QueueStage.Stopped && progress is { } p ? WarmCounts(p.Failed, p.Skipped, crashed)
                : s == QueueStage.Warming && advanced.Elapsed > StallAfter ? StalledNote(advanced.Elapsed)
                : s == QueueStage.Warming ? progress?.Note : null;   // e.g. "retrying ray tracing with fewer threads (8)"
            Set(new QueueItem(id, paused ? QueueStage.Paused : s, progress, error, note));
        }
        var state = Games.FirstOrDefault(s => s.Game.Id == id);
        // a game needing a recording only for its ray tracing still compiles the rest (a partial compile)
        if (state?.Engine is not { } engine || state.Status is GameStatus.Unsupported || state.Status == GameStatus.NeedsRecording && !NeedsRtRecording(state.Plan))
        {
            Stage(QueueStage.Failed, state == null ? "unknown game (scan first)" : $"not ready: {state.StatusReason}");
            return;
        }
        bool background;
        lock (_lock) background = Background || _whenIdle.Contains(id);
        var game = Current(state.Game);
        var rec = Store.LoadGame(id);
        var work = Path.Combine(Store.GameDir(id), "work");
        try
        {
            Stage(QueueStage.Indexing);
            await Gate(id, ct);
            var index = _reader.Index(game, engine, Log, ct);
            (rec.IndexContentHash, rec.ShaderCount, rec.IndexGameVersion, rec.IndexExeStamp) = (index.ContentHash, index.Shaders.Count, game.Version, ExeStamp(game));
            Sharing.SaveShipped(Store.GameDir(id), index);
            CompactRecording(game, rec, index);
            Store.SaveGame(id, rec);
            // a newer community recording for exactly this build: fetched first, within a short wait (never holds a compile up)
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                try { await SyncCommunity(game, index.ContentHash, wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Log?.Report($"{game.Name}: community database: no answer in time, compiling without it"); }
            }
            // prepared once, for the plan and for the warm: the planner reads the recorded shaders' bytes too
            var prepared = false;
            Recording? recording = null;
            Recording? Prepared()
            {
                if (prepared) return recording;
                if (Directory.Exists(work)) Directory.Delete(work, true);
                (prepared, recording) = (true, PrepareRecording(game, engine, index, work, ct));
                return recording;
            }
            // middleware packs (another game's recording may have grown one this install's DLLs match): a change re-plans
            var packs = (_planner as Planner)?.Packs;
            bool current = false;   // this rebuild found the warm still current: nothing to warm
            if (PlanIsStale(id, rec) || (packs != null && packs.Fingerprint(game) != (rec.PlanMiddleware ?? "")))
            {
                Stage(QueueStage.Planning);
                await Gate(id, ct);
                var warmedKeys = WarmedPlanKeys(rec);   // before the build overwrites plan.bin
                rec.PlanPerStage = PerStagePlans;
                rec.Plan = _planner.Build(game, engine, index, Prepared(), Vendor.Caps, Store.GameDir(id), Log, ct, Settings.MaximumPlans);
                (rec.PlanBuiltAt, rec.PlanVersion, rec.ResumeAt) = (DateTimeOffset.Now, Planner.Version, 0);
                rec.PlanMiddleware = packs?.Fingerprint(game);   // after the build: it may have promoted into a pack
                rec.PlanCommunity = CommunityInUse(id)?.Object;
                var keys = PlanKeys(rec.Plan.FilePath);
                rec.PlanItems = keys == null ? null : PlanFingerprint(keys);
                rec.PlanNewItems = keys == null || warmedKeys == null ? null : keys.Count(k => !warmedKeys.Contains(k));
                // a newer planner that compiles nothing the warm didn't (the same records, or fewer): the warm is current
                if (current = rec.PlanNewItems == 0 && rec.WarmedPlanVersion != Planner.Version && StaleReason(game, rec) == null)
                    (rec.WarmedPlanVersion, rec.WarmedPlanItems) = (rec.PlanVersion, rec.PlanItems);
                Store.SaveGame(id, rec);
            }
            bool planOnly;
            lock (_lock) planOnly = _planOnly.Remove(id);
            if (current || planOnly)
            {
                Set(new QueueItem(id, QueueStage.Done, null, null, current ? "already compiled: the new plan adds nothing"
                    : rec.WarmedAt != null && StaleReason(game, rec) is { } why ? why : "plan built"));
                return;
            }
            Stage(QueueStage.Materializing);
            await Gate(id, ct);
            Prepared();
            if (recording != null) SessionLog.WriteRayQueryKeys(recording.DbPath, RayQueryKeysPath(id));
            else File.Delete(RayQueryKeysPath(id));
            // ponytail: re-materialized on every run (also after a stop); keep work\ across a stop if that gets slow
            if (_planner is Planner planner) planner.Log = Log;   // its middleware-pack line
            AgsRegistration? agsUsed = null;
            if (_warmer is Warmer warmer) (warmer.Log, warmer.Ags) = (Log, g => agsUsed = AgsFor(g, rec));
            _planner.Materialize(rec.Plan!, game, engine, _reader, recording, work, ct);
            var skipped = Planner.SkippedIn(work);   // not replayed (a shader not in this install): reported apart from failed
            var needsRecording = Planner.NeedsRecordingIn(work);
            var cap = rec.Careful ? CarefulThreads(Vendor.Vendor) : null;
            var carefulPasses = cap != null ? WarmPasses.Write(work) : 0;   // none without recorded PSOs
            if (carefulPasses > 0) Log?.Report($"{game.Name}: careful compile: the recorded PSOs in {carefulPasses} passes on at most {cap} threads, then the rest");

            Stage(QueueStage.Warming);
            // Attribution: the driver-cache files a process named like the game (the staged warm) has open, sampled on its own
            // task while the warm runs (back to back until found, then every few seconds), never in the progress callback: a
            // sample opens every cache file (measured 6-10 s on a 42 GB NVIDIA cache), and progress lines queued up behind it.
            // ponytail: only files written since the warm started if a sample's cost ever matters beyond this.
            var exe = WarmExeName(game, rec);
            var cacheKeys = new HashSet<string>();
            Task Attribution(IWarmRun run, string exe) => AppCache is not { } cache ? Task.CompletedTask : Task.Run(async () =>
            {
                while (!run.Completion.IsCompleted)
                {
                    try { cacheKeys.UnionWith(cache.KeysOpenBy(exe)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{game.Name}: cache attribution: {e.Message}"); }
                    await Task.WhenAny(run.Completion, Task.Delay(cacheKeys.Count > 0 ? AttributionInterval : Poll));
                }
            });
            // The game and its warm must not run together: the second process of an exe name gets a second, separate set of
            // cache files (ARCHITECTURE.md), so the game would play cold and the warm would fill files it never reads. The
            // warm waits while the game runs, and stops (resumable) when it starts; then it goes on from there.
            while (true)
            {
                await WhileGameRuns(exe, game.Name, ct, () => { if (LearnKeys(game, rec, WarmExeName(game, rec)).Count > 0) Store.SaveGame(id, rec); });
                await Gate(id, ct);
                // Stage the name the game is launched with: AMD keys its cache on the exact case (the game may just have run)
                if (MergeLaunched(game, rec, null)) Store.SaveGame(id, rec);
                exe = WarmExeName(game, rec);
                if (rec.CrashKeys.Count > 0 && rec.CrashKeysDriver != Vendor.Gpu.DriverVersion)
                {
                    Log?.Report($"{game.Name}: retrying the {rec.CrashKeys.Count} pipelines that crashed driver {rec.CrashKeysDriver} on driver {Vendor.Gpu.DriverVersion}");
                    rec.CrashKeys.Clear();
                    Store.SaveGame(id, rec);
                }
                var threads = ThreadsOverride ?? (background ? Settings.BackgroundThreads : Settings.Threads);
                var options = new WarmOptions(threads, background ? WarmPriority.Idle : Settings.Priority, rec.ResumeAt, CompileMemoryGB(Settings) * 1024,
                    rec.CrashKeys.Count > 0 ? [.. rec.CrashKeys] : null, cap is { } most ? Math.Min(threads, most) : 0);
                var run = _warmer.Start(game with { ExePath = Path.Combine(Path.GetDirectoryName(game.ExePath)!, exe) }, work, options, new Reporter<WarmProgress>(p => { progress = p with { Skipped = skipped }; Stage(QueueStage.Warming); }));
                lock (_lock) _run = run;
                var attribution = Attribution(run, exe);
                if (ct.IsCancellationRequested) run.Stop();   // stopped while it was starting
                if (!_go.IsSet) run.Pause();
                var (result, yielded) = await Watch(run, id, background, exe, game.Name);
                await attribution;   // a sample may be running: its keys count (cacheKeys is read below)
                result = result with { Skipped = skipped };
                progress = new WarmProgress(result.Done, result.Total, result.Failed, progress?.PerSecond ?? 0, result.CacheGrowthBytes, result.Skipped);
                // Saved below: whatever the outcome, those files are this game's. A D3D12 key other than the name hash is an
                // AMD app profile's; kept like any other (IsNameHashed reads the case rule from it).
                NoteProfile(game, exe, cacheKeys.Where(k => !rec.CacheKeys.Contains(k)).ToList());
                rec.CacheKeys.UnionWith(cacheKeys);
                if (result.Crashed is { Count: > 0 })   // whatever the outcome: a later compile or resume must not crash on them again
                    rec.CrashKeysDriver = Vendor.Gpu.DriverVersion;
                rec.CrashKeys.UnionWith(result.Crashed ?? []);
                crashed = result.Crashed?.Count ?? 0;
                if (options.StartAt == 0) (rec.ResumeItems, rec.ResumeSeconds) = (0, 0);   // a resume point reset elsewhere drops its segments too
                var (items, seconds) = (rec.ResumeItems + result.Done - options.StartAt, rec.ResumeSeconds + result.Elapsed.TotalSeconds);
                if (yielded && result.Outcome == WarmOutcome.Stopped && !ct.IsCancellationRequested)
                {
                    (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds) = (result.Done, items, seconds);
                    Store.SaveGame(id, rec);
                    continue;
                }
                switch (result.Outcome)
                {
                    case WarmOutcome.Completed:
                        bool cold = ColdWarm(rec, Vendor.Gpu.DriverVersion);   // before the fields it reads are set to this warm
                        rec.WarmedDriverVersion = Vendor.Gpu.DriverVersion;
                        (rec.WarmedPlanVersion, rec.WarmedExeName, rec.WarmedPerStage) = (rec.PlanVersion, exe, rec.PlanPerStage);
                        (rec.WarmedAgsApp, rec.AgsMissed) = (agsUsed?.App, rec.AgsMissed && agsUsed == null);   // after a miss, AGS returns only once the game's keys prove it
                        (rec.WarmedPlanItems, rec.PlanNewItems) = (rec.PlanItems, 0);
                        (rec.WarmedAt, rec.LastWarmTime, rec.LastCacheGrowthBytes) = (DateTimeOffset.Now, TimeSpan.FromSeconds(seconds), result.CacheGrowthBytes);
                        (rec.LastWarmFailed, rec.LastWarmSkipped, rec.LastWarmNeedsRecording, rec.WarmedKeys) = (result.Failed, result.Skipped, needsRecording, [.. cacheKeys]);
                        rec.WarmedFiles = AppCache is AmdAppCache amd ? amd.D3D12FileNames(cacheKeys) : null;
                        (rec.LastWarmCrashed, rec.RecordedSinceWarm) = (crashed, 0);
                        (rec.WarmedIndexHash, rec.WarmedExeStamp, rec.WarmedGameVersion) = (index.ContentHash, ExeStamp(game), game.Version);
                        (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds) = (0, 0, 0);
                        (rec.WarmedCareful, rec.FirstLaunch) = (carefulPasses > 0, null);   // judged again by the next launch
                        if (cold && items >= 1000 && seconds > 1 && carefulPasses == 0)   // a careful warm's rate mixes both schedules
                        {
                            rec.PsoPerSecond = items / seconds;
                            SeedWarmRate(rec.PsoPerSecond.Value);
                            var n = result.Done - options.StartAt;   // the growth is the last segment's
                            if (result.CacheGrowthBytes > 0 && n > 0) rec.BytesPerPso = (double)result.CacheGrowthBytes / n;
                        }
                        Store.SaveGame(id, rec);
                        Stage(QueueStage.Done);
                        break;
                    case WarmOutcome.Stopped:
                        (rec.ResumeAt, rec.ResumeItems, rec.ResumeSeconds) = (result.Done, items, seconds);
                        Store.SaveGame(id, rec);
                        Stage(QueueStage.Stopped);
                        break;
                    default:
                        Store.SaveGame(id, rec);
                        Stage(QueueStage.Failed, $"{result.Error} (log: {result.LogPath})");
                        break;
                }
                break;
            }
        }
        catch (OperationCanceledException) { Stage(QueueStage.Stopped); }
        catch (Exception e) { Stage(QueueStage.Failed, e.Message); }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"could not delete {work}: {e.Message}"); }
            Refresh(game);
            StartSharing([game]);   // the content hash of this build is known now
            // the staged warm runs under the game's exe name, so a reconcile meanwhile waited for it
            if (ManageRecorders)
                try { ReconcileRecorders(id); }
                catch (Exception e) { Log?.Report($"{game.Name}: reconciling the recorder failed: {e.Message}"); }
        }
    }

    bool PlanIsStale(string id, GameRecord r) => r.Plan is not { } p || !File.Exists(p.FilePath) || p.IndexContentHash != r.IndexContentHash
                                      || p.VendorProfile != Vendor.Caps.Profile || r.RecordingImportedAt > r.PlanBuiltAt
                                      || r.PlanVersion != Planner.Version || r.PlanPerStage != PerStagePlans || r.PlanCommunity != CommunityInUse(id)?.Object;

    /// <summary>Plans have each stage unit once: the vendor caches per stage and Maximum mode is off. A pairing plan warmed
    /// earlier already holds every unit, so going per-stage rebuilds the plan but needs no re-warm (see StaleReason).</summary>
    bool PerStagePlans => Vendor.Caps.PerStageCache && !Settings.MaximumPlans;

    /// <summary>Waits for the run. Stops it gracefully (Yielded) when a process named like the warm's exe starts: the game
    /// itself, or another discovered game of that exe name, even while the run is paused. Keeps it suspended while the
    /// queue is paused, while another discovered game runs (background runs with PauseWhileGaming) or, for a "when idle"
    /// item, while the user is at the PC. Checked every 0.5 s, so input pauses it within a second.</summary>
    async Task<(WarmResult Result, bool Yielded)> Watch(IWarmRun run, string id, bool background, string exe, string name)
    {
        string? applied = "";   // "" = nothing applied yet; null = running
        bool yielded = false;
        while (await Task.WhenAny(run.Completion, Task.Delay(Poll)) != run.Completion)
        {
            if (yielded) continue;
            var running = Running();
            if (running.Contains(exe))
            {
                yielded = true;
                _pauseWhy = StoppedFor(name);
                SetCurrent(QueueStage.Paused, _pauseWhy);
                run.Stop();   // graceful (resumes a suspended run first): the driver writes and releases the game's files
                continue;
            }
            var playing = background && Settings.PauseWhileGaming ? GameNameIn(running) : null;
            _pauseWhy = playing != null ? $"paused while {playing} is running" : WaitsForIdle(id) ? "paused until the PC is idle" : null;
            var want = _go.IsSet ? _pauseWhy : _pauseWhy ?? "paused";
            if (want == applied) continue;
            applied = want;
            if (want == null) { run.Resume(); SetCurrent(QueueStage.Warming); }
            else { run.Pause(); SetCurrent(QueueStage.Paused, _pauseWhy); }
        }
        _pauseWhy = null;
        return (await run.Completion, yielded);
    }

    static string StoppedFor(string name) => $"stopped while {name} is running: continues when it exits";

    /// <summary>Waits while a process named like the warm's exe runs (see RunItem), shown as Paused with the reason;
    /// <paramref name="learn"/> attributes the cache files the game holds open meanwhile (every few seconds).</summary>
    async Task WhileGameRuns(string exe, string name, CancellationToken ct, Action? learn = null)
    {
        if (!Running().Contains(exe)) return;
        try
        {
            _pauseWhy = StoppedFor(name);
            SetCurrent(QueueStage.Paused, _pauseWhy);
            Stopwatch? sampled = null;
            while (Running().Contains(exe))
            {
                if (learn != null && (sampled == null || sampled.Elapsed >= AttributionInterval)) { sampled = Stopwatch.StartNew(); learn(); }
                await Task.Delay(Poll, ct);
            }
        }
        finally
        {
            _pauseWhy = null;
            SetCurrent(_go.IsSet ? _stage : QueueStage.Paused);   // the reason is gone (a stop sets Stopped next)
        }
    }

    /// <summary>Exe file names of the discovered games that are running (a case-insensitive set), SCSKiller's staged warm
    /// copies (same file name, run from under DataDir) excluded. A name is in the case the process was launched with when
    /// that is known, else the install's file name. Replaceable for tests.</summary>
    public Func<IReadOnlySet<string>> RunningGameExes { get; set; }

    /// <summary><see cref="RunningGameExes"/>, noting each name that differs in case from its game's exe file name: the
    /// game was launched under that name (read into the game's record by MergeLaunched).</summary>
    IReadOnlySet<string> Running()
    {
        var running = RunningGameExes();
        foreach (var name in running)
            if (Games.Any(s => Path.GetFileName(s.Game.ExePath) is var disk && disk != name && disk.Equals(name, StringComparison.OrdinalIgnoreCase)))
                _launched.AddOrUpdate(name, n => new LaunchedExe(n, DateTimeOffset.Now), (n, old) => old.Name == n ? old : new LaunchedExe(n, DateTimeOffset.Now));
        return running;
    }

    /// <summary>Staged warms are scskiller_warm.exe's children. The launched name's case comes from the module list (the PEB:
    /// CASEPROBE.EXE for caseProbe.exe; the kernel gives the file's case), read once per process; anti-cheat games are never opened.</summary>
    IReadOnlySet<string> DiscoveredGamesRunning()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = Games.GroupBy(s => Path.GetFileName(s.Game.ExePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Disk: g.Key, AntiCheat: g.Any(s => s.AntiCheat != AntiCheat.None)), StringComparer.OrdinalIgnoreCase);
        if (byName.Count == 0) return running;
        var all = ProcessTree.Snapshot();
        var warms = all.Where(p => p.Exe.Equals("scskiller_warm.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid).ToHashSet();
        Dictionary<(int, string), string> known;
        lock (_launchedByPid) known = new(_launchedByPid);
        var seen = new Dictionary<(int, string), string>();
        foreach (var (pid, parent, exe) in all)
        {
            if (!byName.TryGetValue(exe, out var game) || warms.Contains(parent)) continue;
            if (!known.TryGetValue((pid, exe), out var name))
            {
                name = game.Disk;
                if (!game.AntiCheat)
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (Path.GetFileName(p.MainModule?.FileName) is { } launched && launched.Equals(game.Disk, StringComparison.OrdinalIgnoreCase)) name = launched;
                    }
                    catch (Exception) { }   // access denied or exited: the install's name
            }
            seen[(pid, exe)] = name;
            running.Add(name);
        }
        lock (_launchedByPid)   // exited processes are forgotten
        {
            _launchedByPid.Clear();
            foreach (var (k, v) in seen) _launchedByPid[k] = v;
        }
        return running;
    }
    readonly Dictionary<(int Pid, string Exe), string> _launchedByPid = [];

    string? GameNameIn(IReadOnlySet<string> runningExes) =>
        Games.FirstOrDefault(s => runningExes.Contains(Path.GetFileName(s.Game.ExePath)))?.Game.Name;

    // Polled: WMI's Win32_ProcessStartTrace would push starts but needs admin (access denied unelevated).
    volatile HashSet<string> _playing = [];                 // game ids; replaced, never changed in place (read from any thread)
    readonly Dictionary<string, int> _absent = [];          // playing game id -> polls in a row its exe wasn't running
    readonly Dictionary<string, PlayWindow> _runs = [];     // playing game id -> its run so far (GameRecord.LastPlay once it exits)
    DateTimeOffset? _lastPoll;

    /// <summary>The watcher's clock. Replaceable for tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Replaceable for tests.</summary>
    public TimeSpan WatchInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Polls in a row without the exe before a game has exited: a game restarting itself isn't an exit.</summary>
    public const int ExitPolls = 2;

    public bool IsPlaying(string gameId) => _playing.Contains(gameId);

    public Task WatchGames(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { PollGames(); }
            catch (Exception e) { Log?.Report($"watching games: {e.Message}"); }
            try { await Task.Delay(WatchInterval, ct); }
            catch (OperationCanceledException) { }
        }
    });

    /// <summary>A start only raises GameChanged (nothing re-read: the recorder may be writing). One caller at a time.</summary>
    public void PollGames()
    {
        var games = Games;
        if (games.Count == 0) return;
        var at = Clock();
        var running = new HashSet<string>(Running(), StringComparer.OrdinalIgnoreCase);
        var now = games.Where(s => running.Contains(Path.GetFileName(s.Game.ExePath))).Select(s => s.Game.Id).ToHashSet();
        var was = _playing;
        var started = now.Where(id => !was.Contains(id)).ToList();
        var ended = new List<string>();
        foreach (var id in was)
            if (now.Contains(id)) _absent.Remove(id);
            else if ((_absent[id] = _absent.GetValueOrDefault(id) + 1) >= ExitPolls) ended.Add(id);
        // a run already going at the first poll has no known start: its window would take in older launches too
        foreach (var id in started) if (_lastPoll is { } before) _runs[id] = new PlayWindow(before, at);
        foreach (var id in now) if (_runs.TryGetValue(id, out var w)) _runs[id] = w with { To = at };
        _lastPoll = at;
        if (started.Count + ended.Count == 0) return;
        foreach (var id in ended)
            if (_runs.Remove(id, out var run))
            {
                var rec = Store.LoadGame(id);
                rec.LastPlay = run;
                Store.SaveGame(id, rec);
            }
        foreach (var id in ended) _absent.Remove(id);
        _playing = [.. was.Except(ended), .. started];
        foreach (var id in started)
        {
            GameState? state = null;
            lock (_lock)
                if (_games.FindIndex(x => x.Game.Id == id) is var i and >= 0) _games[i] = state = _games[i] with { Playing = true };
            if (state != null) GameChanged?.Invoke(state);
            if (AppCache != null && state is { AntiCheat: AntiCheat.None }) _ = Task.Run(() => LearnWhilePlaying(state.Game));
        }
        foreach (var s in games.Where(s => ended.Contains(s.Game.Id)))
        {
            var g = Current(s.Game);   // a store updates a game before it starts: its first exit after that shows it Stale
            Refresh(g);
            StartSharing([g]);   // also without a new recording: the command line's compile indexes a build but can't share
            if (ManageRecorders)   // a recorder change that waited for the game to exit (Reconcile)
                try { ReconcileRecorders(g.Id); }
                catch (Exception e) { Log?.Report($"{g.Name}: reconciling the recorder failed: {e.Message}"); }
        }
    }

    public void RefreshGame(string gameId) => Refresh(Find(gameId).Game);

    public void RefreshCacheSizes()
    {
        if (AppCache is not { } cache) return;
        foreach (var s in Games)
        {
            var keys = Store.LoadGame(s.Game.Id).CacheKeys;
            if (keys.Count == 0 || cache.SizeOf(keys) is var size && size == s.CacheOnDisk) continue;
            var now = s with { CacheOnDisk = size };
            lock (_lock)
            {
                var i = _games.FindIndex(x => x.Game.Id == s.Game.Id);
                if (i < 0 || _games[i] != s) continue;   // re-evaluated meanwhile: that state is newer
                _games[i] = now;
            }
            GameChanged?.Invoke(now);
        }
    }

    static TimeSpan UserIdleTime()
    {
        var info = new LastInputInfo { Size = 8 };
        // No input info (not an interactive session): don't hold background work back forever; games still pause it.
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.MaxValue;
    }

    struct LastInputInfo { public uint Size, Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);

    sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
