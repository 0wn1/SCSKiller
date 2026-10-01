# SCSKiller architecture

SCSKiller compiles a game's pipelines into the GPU driver's shader cache before the game runs, so the game finds them
cached instead of compiling them mid-play. It reads the game's shaders from its files (or from a recording of a play
session), plans which pipelines to create, and replays them in a separate process that the driver treats as the game.

## Contents

- [How it works](#how-it-works)
- [Source layout](#source-layout)
- [Data locations](#data-locations)
- [Driver caches](#driver-caches)
- [Engine readers](#engine-readers)
- [Planner](#planner)
- [Readiness rules](#readiness-rules)
- [Recorder](#recorder)
- [scskiller_warm.exe protocol](#scskiller_warmexe-protocol)
- [Ray tracing state objects](#ray-tracing-state-objects)
- [scskiller_creates.csv](#scskiller_createscsv)
- [Plan file](#plan-file)
- [Clear cache](#clear-cache)

## How it works

1. **Discover.** An `IGameSource` per store finds installed games and flags anti-cheat.
2. **Index.** An `IEngineReader` per engine family detects the engine and lists every shader the build ships (stage,
   SHA-1, signatures, root signature if embedded), grouped in shader maps that say which shaders can be drawn together.
3. **Plan.** The planner (`IPlanner`) turns the index, a recording if there is one, and the GPU vendor's `VendorCaps`
   into a hash-only plan: pipeline templates, root signatures and items naming shaders by SHA-1.
4. **Materialize.** The plan's shaders are read from the install into a work folder. Nothing of the game is stored
   beyond that folder, which is deleted after the warm.
5. **Warm.** `scskiller_warm.exe` stages a copy of itself named like the game's exe and creates every item on a D3D12
   (or D3D11) device. The driver keys its cache on the exe name, so the game finds those compiles cached.

Games whose files don't say enough (engines that build root signatures at run time, AMD's state-dependent cache) need a
recording: the optional recorder `d3d12.dll` captures what the game creates while it's played, and the plan replays it.

Everything vendor- or engine-specific sits behind one interface: a new GPU vendor is an `IGpuVendorBackend` and its
`VendorCaps`, a new engine an `IEngineReader`. The planner and the warmer read only the caps.

## Source layout

| Path | What |
|---|---|
| `src/SCSKiller.Core/Contracts.cs` | Shared interfaces and records |
| `src/SCSKiller.Core/Games/` | `IGameSource` for Steam, Epic Games, EA app, GOG, Ubisoft Connect, Xbox (PC) and Battle.net; anti-cheat detection |
| `src/SCSKiller.Core/Unreal/` | `IEngineReader` for Unreal Engine (through CUE4Parse) |
| `src/SCSKiller.Core/Unity/` | `IEngineReader` for Unity |
| `src/SCSKiller.Core/FromSoft/` | `IEngineReader` for FromSoftware games |
| `src/SCSKiller.Core/ReEngine/` | `IEngineReader` for Capcom's RE Engine |
| `src/SCSKiller.Core/Carved/` | `IEngineReader` for any game that ships raw DXBC/DXIL containers in its files |
| `src/SCSKiller.Core/Planning/` | The planner, root-signature rules, the plan and recording formats, materialization |
| `src/SCSKiller.Core/Vendors/` | NVIDIA and AMD backends and their per-application cache (`IAppCache`) |
| `src/SCSKiller.Core/Warming/` | `IWarmer`: runs `scskiller_warm.exe` and parses its output |
| `src/SCSKiller.Core/App/` | The `ScsKiller` facade, state store, queue, driver-update check, recorder install, account and community database |
| `src/SCSKiller.Cli/` | The `scskiller` command line |
| `src/SCSKiller.App/` | The WinUI 3 app (unpackaged) |
| `proxy/` | Native code: the recorder `d3d12.dll`, `scskiller_warm.exe`, and `selftest.exe`, which measures driver cache behaviour |
| `tests/SCSKiller.Tests/` | xUnit tests, one folder per area |

Building and running the tests: [CONTRIBUTING.md](CONTRIBUTING.md).

## Data locations

Everything lives under `%LOCALAPPDATA%\SCSKiller\`:

- `settings.json`.
- `games\<game id, ':' replaced by '_'>\`:
  - `state.json`: the game's record (status, learned cache keys, last warm). A save writes only the fields its
    holder changed since loading the record, onto the stored one re-read under a lock across processes (sets merge
    by what was added and removed), so a long compile never puts back what a game's exit saved meanwhile;
  - `plan.bin`: the plan (hash-only);
  - `index.*`: the cached shader index; `index.shaders`: the build's shader SHA-1s, which uploads are checked against;
  - `recording.db`: the game's recording, the only durable copy of what the recorder captured (see
    [Recorder](#recorder));
  - `community.db`: the community database's hash-only recording for the game's build, merged with `recording.db` in
    `work\` when a compile plans;
  - `work\`: the materialized plan, deleted after a warm;
  - keys found for the game, kept locally only: `aes.key` (an Unreal pak key), `archive.keys` (FromSoftware archive
    keys, with the SHA-256 of the exe they came from), `pak.modulus` (RE Engine table key);
  - `inline.idx`: for an Unreal game without shader libraries, where each shader sits in its package.
- `packs\<vendor>\<dll name>-<dll sha1>.pack`: middleware packs (see [Middleware packs](#middleware-packs)).
- `community\`: the community database's manifest and downloaded recordings.
- `recorders.log`: what recorder installs and removals did.

Nothing is written into a game folder except the recorder (see [Recorder](#recorder)).

## Driver caches

What each driver keys its cache on decides everything else. These are measured with `selftest` and on games' own
pipelines, not taken from documentation. `VendorCaps` holds the result per vendor.

### NVIDIA, D3D12

- **Keyed on the exe file name**, not its path or contents, case-insensitively. A staged copy of `scskiller_warm.exe`
  named like the game fills that game's cache (`CacheKeyedByExeName`).
- **A packaged (Xbox) game is keyed on its package identity** instead (`PackageKeyed`). Its warm runs with the game's
  identity: `scskiller_warm --package <app user model id>` starts the staged copy through the desktop app activator.
  That process inherits no handles and has no console, so it finds its parent by process id and writes its output
  through named pipes the parent relays. If activation fails, the warm runs under the exe name alone, and the game shows
  as not reached once its own key is seen.
- **An NVAPI shader-extension slot is part of the key**: a PSO or ray tracing collection compiled with a slot set misses
  when created without it, and the reverse. How the slot was set (device-wide, per thread, or a PSO extension) is not
  part of the key. The recorder captures it and the warm recreates it (see [`'N'` records](#ray-tracing-state-objects)).
- **Per stage, on shaders + root signature only** (`StateIndependentCache`, `PerStageCache`). Blend, rasterizer,
  depth-stencil, render-target formats, MSAA, topology type, input layout and stream output don't change the compiled
  result; any change to the root signature's bytes recompiles every stage. A VS and a PS compiled with other partners
  link at cache-hit cost. So synthesized pipeline state is fine. One exception: view instancing recompiles, so a game
  drawing with it needs its recorded pipelines.
- Files: `%LOCALAPPDATA%\NVIDIA\DXCache\TTTTa91dKKKKKKKK.nvph`, where `KKKKKKKK` is a 32-bit hash of the exe name
  (observed, not derivable). `fc52` is the shader cache shared with D3D11, `0002` is D3D12, `c54e` holds ray tracing
  state objects. The driver keeps them open while the device lives, which is how `NvidiaAppCache` learns a game's keys.
  Files are pre-sized in powers of two, so on-disk size is an upper bound; no size cap or eviction shows up to 12 GB.
- **A second process with the same name running at the same time gets its own files** (key + 1). So a game and its warm
  must never run together: the queue doesn't start a warm while the game runs, and stops a running warm gracefully when
  the game starts, resuming from its `done` afterwards.
- The records store an opaque 128-bit key over the whole root signature plus the compiled code; none of the D3D12
  inputs (root signature bytes, shader containers or their hashes) can be recovered from the cache.
- After a driver update, no D3D12 cache file older than the install remains, which is why SCSKiller recompiles after
  one.
- The D3D12 runtime version is not part of the key: pipelines compiled under the system runtime hit under a game's
  Agility SDK runtime and the reverse. The warm uses the system runtime. A game's Agility runtime older than the system's
  isn't loaded at all: the loader takes the newer one.
- Cached creates take about 0.2-1 ms, cold ones 6-70 ms. A warm replays roughly 450-1350 PSOs a second on 30 threads.
- **A compute PSO using inline ray tracing (RayQuery) is never a full hit**: once cached (by a warm, the game or an
  earlier create in the same process) it still costs about 7-15% of its cold create, 9-35 ms for an Unreal 5.6
  game's, and nothing new is written. Heap-indexed root signatures (`ResourceDescriptorHeap[]`) hit normally
  (`selftest bindless`). A compile writes the keys of the recording's RayQuery PSOs (`rayquery.keys`); the session
  log counts their creates up to `SessionLog.RayQueryFloorMs` (60 ms: in play the floor stretches, SILENT HILL:
  Townfall's to 12-88 ms, its cold creates to 75 ms and more) apart, not as compiles.

### AMD, D3D12

- **Keyed on the exe file name**, in `%LOCALAPPDATA%\AMD\DxcCache\<app>.<f2>.<kind>.<build>.<slot>.parc`. `app` is
  the FNV-1a-32 of the exe name's UTF-16LE bytes, **case-sensitive**, path-independent (`AmdAppCache.DxcAppHash`).
  The name is taken as launched, so the app learns the launched case (`GameRecord.LaunchedExeName`) from the
  recorder's `#session` marker or a running game's main module path, and warms under it.
- **Driver application profiles override the key** for some games: a fixed key for any exe whose name matches, or, for
  some profiles, only when the launch path ends in the game's install layout. The staged warm therefore mirrors the
  install folder's name and the exe's path inside it (`--stage-path`). Since the name hash is only a hint, a game's
  real keys are **learned** from the cache files a process named like it holds open, during its warms and while it's
  played (`GameRecord.CacheKeys`). A game whose learned keys were never warmed is Stale: "the compile didn't reach
  this game's cache: the game uses another driver-cache key".
- **A device created through AGS with an app name is keyed on that name** (`agsDriverExtensionsDX12_CreateDevice`,
  non-empty `pAppName`): FNV-1a-32 of its UTF-16LE bytes, case-sensitive, whatever the exe's name or path. The engine
  name, versions and AGS build don't change it. A profile matched on the app name wins over everything (`Phoenix`:
  `d32786a7`; `OakGame`: `f2f80824`, even as `Wonderlands.exe`, whose own profile is `85c2b2e5`), and an exe-name
  profile wins over an unprofiled app name (`AmdAppCache.AgsKey`). Unreal 4.25-5.6 creates its device this way on AMD
  with the project name as `pAppName`; 4.20-4.24 create a plain device (only `agsInit`), though every version links
  AGS and exports its functions. Tiny Tina's Wonderlands (a 4.20 fork, project `OakGame`) holds `85c2b2e5`; SILENT
  HILL: Townfall (5.6) holds `dc72f790`, FNV-1a of `Townfall`. A warm registers the game's names (`scskiller_warm
  --ags`, `AmdAgs.Of`: Unreal 4.25 or later, AGS linked, project name known) only where that is proven
  (`ScsKiller.AgsFor`): the game's own process was seen holding that key and not its plain key, or, before the game
  is seen, the app name is a measured one (`AmdAppCache.ProvenAgsApp`). Otherwise it warms a plain device. If the
  game's keys aren't known and its first launch after an AGS warm still compiled more than `PartlyWarmedShare` of its
  pipelines, the warm counts as a miss (`GameRecord.AgsMissed`): the game is Stale ("the compile didn't reach this
  game's cache") and the next warm is plain. After an AGS warm the exe name's case doesn't matter.
- The driver writes `.parc` files **through a memory map, so their modification time doesn't change** when entries are
  added. Sizes (powers of two, doubling as they fill) do. Never use mtimes for attribution or growth.
- **The driver caps the whole DxcCache folder at 16 GiB** (`AmdAppCache.DxcCacheCap`): every `.parc` file counts, of
  every app and driver build. The cap is fixed: Adrenalin, the registry and ADLX have no size setting (only the Shader
  Cache mode, `UMD\ShaderCache`, and Reset Shader Cache). It is checked only when a D3D12 device is created; a running
  process can take the folder past it. The driver then deletes files, least recently used first by their NTFS
  LastAccessTime, one file at a time (a key can lose one of its files), until the folder is just under 16 GiB. Files a
  live device holds open are neither counted nor deleted. A device created or ending under a name refreshes that key's
  files; anything that reads a file's contents refreshes it too, so the app only lists names and sizes and queries
  attributes and handles. A warmed game with a warm's file gone is Stale (`GameRecord.WarmedFiles`), and the queue
  warns when its estimated growth (`ScsKiller.CacheGrowth`) is more than the room left (`AmdAppCache.QueueWarning`).
  `selftest dxcfill` grows the cache under a throwaway name to measure this.
- Entries are written during the run, not at exit.
- **The cache is per stage but not state-independent** (`StateIndependentCache = false`, `PerStageCache = true`). With
  the stages cached, a change costs one of three things:

  | Class | Cost | Fields |
  |---|---|---|
  | Recompile a stage | like a cold compile | the VS's declared input elements (format, offset, slot, per-instance step rate, even when unread); topology type; root signature layout (visibility, parameter order, static samplers, added parameters); a PS export format of another shape (1- and 2-channel formats, RGBA16_UNORM); write mask 0; logic op; dual-source blending; a VS-only (depth) pipeline vs VS + PS |
  | Relink | about 0.5 ms, about 3 ms with real game shaders | a VS and a PS never linked together; a render-target format of the same shape (RGBA16F, R10G10B10A2, sRGB, BGRA8, R11G11B10); RT count; MSAA; DSV format; blend enable and factors; write mask RGB |
  | Free | like a re-create | input elements the VS doesn't declare; element order; all rasterizer fields; all depth-stencil fields; blend op; sample mask; strip cut value; root signature deny flags and serialization version |

  One relink field left different costs the whole relink, so the planner copies a recorded blend, depth-stencil and DSV
  together (`ExactLayouts.Link`).
- **A VS is compiled for how its PS consumes it**: whether the PS has a render target, the PS's read masks, and its
  interpolation modes. A VS unit is keyed on those (`UnitPolicy.PartnerReads`) and on the whole root signature.
- Unreal points the UV channels a mesh lacks at its last one, so one VS meets several offsets for the same elements.
  `ExactLayouts.UvClampVariants` adds those layouts for the VS's recorded layouts.
- A cache hit under a profiled name costs up to a few ms, so on AMD "hit" means **under 3 ms**; the recorder and
  `SessionLog` use that threshold.
- Creating a D3D12 device alone opens the name's cache files, so an open file says nothing about whether a warm's
  pipelines compiled: check `failed`.
- **What the driver stores for a PSO depends on the compiling process**, not only on its desc: which stage and pipeline
  entries it keeps, and under which keys, varies with concurrency and with which sibling of a shader set compiles first.
  For some games a warm on many threads leaves most of the game's own creates compiling. So:
  - **Judged by the first launch**: after a complete AMD warm, the first recorder session that ends and holds at least
    `ScsKiller.MinJudgedCreates` creates is kept (`GameRecord.FirstLaunch`). If more than `ScsKiller.PartlyWarmedShare`
    (20%) of them compiled, the game shows "Partly warmed" and offers a careful compile.
  - **Careful compile** (`GameRecord.Careful`, the game's "Careful compile" switch, CLI `compile <game> --careful`):
    the recorded PSOs are split into passes so that no pass holds two with the same shader set (`Warming.WarmPasses`,
    at most `WarmPasses.MaxPasses` passes); each pass is its own `scskiller_warm --pass` process on at most
    `ScsKiller.AmdCarefulThreads` (4) threads, then the plan's other items run in one process at the usual thread count.
    Without a recording it's an ordinary warm. The choice persists, so a driver update's re-warm is careful too.
- Ray tracing and D3D11 are cached per name too. `%LOCALAPPDATA%\D3DSCache` is the Windows runtime's cache, not AMD's.

### D3D11

Keyed on the exe file name on both vendors. Creating a shader is lazy: the driver compiles at the first draw or
dispatch that uses it, and caches per shader, not per pipeline. On NVIDIA blend, render-target format, input layout,
depth, MSAA and SRV formats don't change the result, so SCSKiller warms D3D11 games on NVIDIA only (`Planner.D3D11Cache`).
On AMD the input layout recompiles the VS, so a D3D11 warm there would need the game's real layouts.

### Ray tracing

Both vendors cache ray tracing state objects on disk per exe name, and both hit on an exact repeat of the same object
(and of the same `AddToStateObject` chain). They differ otherwise (`VendorCaps.RtCacheGranularity`):

- **NVIDIA (`Collection`)**: collections are cached individually, and linking cached collections costs under 1 ms even
  for a combination never linked before. The key holds the library bytes, the global and local root signatures, and
  the payload and attribute sizes; not hit group or export names, nor the recursion depth. A cached state object still
  costs about 1.5 ms per shader or collection. Collections and flat pipelines share nothing. So the planner can
  synthesize one collection per DXIL library.
- **AMD (`WholeObject`)**: the key is the whole linked object. Cached parts don't help a different whole, and
  `AddToStateObject` hits only as an exact repeat. Only recorded objects can be warmed.

### Other vendors and Vulkan

Intel and other vendors are unmeasured, so SCSKiller reports them as unsupported.

Vulkan games aren't supported. NVIDIA's driver keeps Vulkan pipelines keyed on the exe name (`NVIDIA\GLCache`), which
Steam can redirect per game; AMD's Vulkan cache (`AMD\VkCache`) is keyed on the exe's full path, so a staged warm can't
reach it there.

## Engine readers

Each reader detects its engine from the game's files, builds the shader index and reads shader bytes on demand. They
open game files read-only and never launch or attach to the game.

- **Unreal Engine** (`Unreal/`): shader libraries and shader maps through CUE4Parse, including the version-1 archives of
  UE 4.20/4.21 (`UnrealReader.OpenV1`) and games that keep shaders inline in their packages. Encrypted paks need the
  game's AES key (`aes.key`, given by the user).
- **Unity** (`Unity/`): Shader objects in serialized files and UnityFS bundles. Their compiled programs are one LZ4 blob
  per platform; the reader finds the blob by its shape and carves it, which avoids depending on each Unity version's
  serialized layout. Windows builds ship DXBC for the `d3d11` platform, which both the D3D11 and D3D12 players
  run. The API comes from `Player.log` of the last run, else the build's API list. Unity builds root signatures at run
  time, so D3D12 Unity games need a recording.
- **FromSoftware** (`FromSoft/`): BHD5/BDT archives, DCX (zlib, Oodle, zstd) and BND3/BND4 binders. The archives'
  public RSA keys are read from the game's exe (`SoulsKeys`), else downloaded from a pinned commit of UXM, and kept in
  `archive.keys`. Oodle comes from CUE4Parse's download, never from the game's DLL.
- **RE Engine** (`ReEngine/`): KPKA packages with encrypted entry tables. The table key needs the game's public RSA
  modulus, which isn't on disk in the clear; it's downloaded from a pinned commit of ree-pak-rs, or given by hand in
  `pak.modulus`. Shaders are in master material files, found by their magic since file names are hashes. RE Engine
  builds root signatures at run time, so D3D12 games need a recording.
- **Carved** (`Carved/`): any other game that ships raw DXBC/DXIL containers. Files are carved, each container
  validated and reflected; a file of pipeline records becomes one shader map per record.

Shaders are hashed the same way everywhere: SHA-1 of the container sliced to its declared size. `ShaderContainer.Parse`
reads each container's signatures, including read masks and interpolation modes, and its embedded root signature.

## Planner

`Planning/Planner` and `PlanBuilder` build the plan; `Planner.Version` is stored with each plan.

### Root signatures

Most engines don't ship root signatures: Unreal builds them from per-stage resource counts. `RootSig` rebuilds them
byte for byte from Epic's rules for UE 4.20 to 5.7, plus engine forks, detected from the shaders, never from the game's
name:

- 10-byte resource counts, whose field order is decided by the shaders' own register use (`ShaderContainer.WideCounts`);
- unbounded bindless SRV ranges in dedicated spaces, which get one table each (`RootSig.BindlessTables`,
  `RootSig.SpaceBindlessTables`);
- a raised MAX_SRVS when a shader binds more SRVs than the stock table (`RootSig.MaxSrvsFor`).

`RootSig.Verified` holds for the versions and forks a real game has confirmed (`ConfirmedEngines`:
`confirmed-engines.json`, embedded, plus the entries of the copy the server serves as a content file); any other gets
the source-derived rule and the note "not tested on this engine version yet". With a recording, the rule is checked
against it first, and the
planner falls back to a lookup learned from the recording when it doesn't rebuild.

### Stage sets and the pre-emit guard

`StageSets` pairs shaders inside one shader map by linkage (`Planner.Links`): VS/MS → PS, VS → GS (→ PS), VS → HS →
DS (→ GS) (→ PS), AS → MS (→ PS). A VS or MS without `SV_Position` is never drawn alone or with a PS
(`Planner.Rasterizable`). Unreal 5's Nanite material pixel shaders, which no VS in their own map feeds, are paired with
the global VSs whose outputs link them.

A mesh shader's per-primitive outputs sit in its `Outputs` from `ShaderContainer.PrimitiveRow` up, and a PS packs them
after its per-vertex inputs; `Planner.MeshFeeds` matches them by semantic, index, component type and mask. In Unreal 5,
Nanite and material draws also pair shaders from different maps: a default material's MS or PS with another map's
shaders. A shader present in at least 1 of every 50 of the platform's maps is taken as shared, and `SharedAcrossMaps`
pairs every MS with every PS it feeds, and every VS with every shared PS it links to, where either side is shared
(Unreal 5 only).

The **pre-emit guard** (`PlanBuilder.Covers`, `RootSig.Uncovered`) drops a stage set whose root signature doesn't give
a shader every resource it declares; the plan log counts them (`rs_uncovered`). Compute shaders the runtime would
reject on the current vendor (a `[WaveSize]` the GPU doesn't run, AMD AGS extensions elsewhere) are left out
(`vendor_extension`). Stream output and view instancing are never synthesized; recorded pipelines keep them.

### Per-stage cover

Both vendors cache per stage, so a plan needs each stage unit once, not every VS × PS pair. `UnitPolicy` defines a unit
per vendor: NVIDIA, shader + root signature; AMD, the VS also with its declared input elements, topology, and its PS's
render-target binding and consumption. `ExactLayouts` resolves each shader's state from the recording (exact, inferred
from a shader with the same signature, or guessed), and `UnitCover` picks the fewest pipelines that cover every unit,
seeded with the recording's own units.

### Ray tracing collections

Where the vendor caches per collection (NVIDIA), `RtCollections` synthesizes one collection per DXIL library, with the
engine's global and local root signatures rebuilt from the library's resource counts and its RDAT function table.
Rules exist for UE 4.26/4.27, UE 5.1 and the forks the root-signature rules cover; with a recording, its collections'
rule is used if at least 99% of them rebuild. FromSoftware games get a guessed rule (`RtCollections.GuessedFamilies`),
and the plan log says so.

### Middleware packs

Middleware (FidelityFX, OptiScaler, XeSS, DirectStorage, Streamline plugins) creates pipelines from shaders embedded in
its DLL, which no engine index contains. `Planning/Middleware.cs`:

- **Detection**: known DLL names next to the exe (OptiScaler by its PE exports, whatever its file name); never for
  anti-cheat games. Every embedded container is hashed like a game shader.
- **Promotion**: a recorded pipeline whose stages all come from one detected DLL goes into that DLL version's pack
  (vendor, DLL name, SHA-1 of the DLL), with its root signature if the blob is a serialized root signature only.
- **Seeding**: every pack for a DLL version next to a game's exe seeds its plan (record `'M'`), whichever game's
  recording filled it. Shader bytes come from the install's own copy of the DLL at materialize time.

### Hash-only recordings and skipped items

A hash-only recording (the form shared with the community database) is rehydrated from the install before planning,
like the shaders `recording.db` names by hash: from the engine reader, then from middleware DLLs (`Rehydrate.Run`).

`Planner.Materialize` never hands the warm an item naming a shader the install doesn't have. Those are **skipped**, not
failed, and counted in `work\skipped.txt`. **Failed** means the driver rejected it. Skipped items that a community
recording flags as built at run time or by a mod (`'L'`) are counted apart: they need a recording on this PC.

### Planner updates

When a new planner version rebuilds a warmed game's plan, the new plan's records are compared with the plan the last
warm replayed (`GameRecord.PlanItems`, `WarmedPlanItems`). A plan that adds nothing keeps the game Warmed; one that
adds pipelines marks it Stale with the count ("SCSKiller can now compile N more pipelines for this game"). The app
rebuilds such plans while the PC is idle, as plan-only queue items (`QueueItem.PlanCheck`) that queue lists leave out
and count in one line instead (`ScsKiller.PlanCheckLine`: "Checking N games for more to compile (while idle)").

## Readiness rules

The planner's `Check` decides a game's status:

- Engine unsupported, or files it can't read → `Unsupported` with the reason.
- The vendor has `StateIndependentCache` and the engine version has a root-signature rule → `Ready` without a
  recording.
- A recording exists → `Ready`. Without `StateIndependentCache` (AMD) it must contain draws.
- Otherwise → `NeedsRecording`.
- A vendor without `CacheKeyedByExeName` would need an in-game warm, which isn't implemented → `Unsupported`.

After a build:

- **Partial plan**: when the pre-emit guard left out more than 10% of the stage sets, the game stays Ready but its
  reason says a recording compiles the rest (`ScsKiller.IsPartial`).
- **Ray tracing**: when more than 10% of the index's DXIL libraries have no synthesized collection and no recording has
  ray tracing, the game needs a recording for ray-traced effects; the rest still compiles (`ScsKiller.NeedsRtRecording`).
  On AMD this always applies, since only recorded objects can be warmed.
- **New recorded pipelines**: each import counts the pipeline records it added that the current plan doesn't have
  (`GameRecord.RecordedSinceWarm`, reset by a complete warm). A warmed game with any is Stale: "N new pipelines
  recorded; compile again to include them".
- **Partly warmed** (AMD): see [AMD, D3D12](#amd-d3d12).

## Recorder

The recorder is `proxy/`'s `d3d12.dll`, placed next to the game's exe with a `scskiller.ini`. It forwards to the system
`d3d12.dll` and records every pipeline, root signature and ray tracing state object the game creates into
`scskiller.db`, with timings in [`scskiller_creates.csv`](#scskiller_createscsv).

- **Where it's installed** (`ReconcileRecorders`, app only): in every compatible game when "Record in all compatible
  games" is on, unless the game's own switch says otherwise. Compatible means D3D12, supported, no anti-cheat of any
  kind, no foreign `d3d12.dll` (unless chained, below), and a folder writable without elevation. A running game's
  folder is left alone until it exits. Removal deletes exactly the files installed, checked by hash; the recording is
  imported first.
- **Uninstall** (Velopack's uninstall hook, `ScsKiller.RemoveAllRecorders`): removes the recorder the same way from
  every folder a `GameRecord.RecorderExe` names, then the recorder's `scskiller.db` once merged into `recording.db`,
  and its csv, frame log, log and keys file. A running game's folder is left, and `recorders.log` says so.
- **Recording alongside a mod**: a foreign `d3d12.dll` (ReShade or another wrapper) is chained only when the user turns
  that on for the game. The mod is renamed to `d3d12.scskiller-next.dll` (bytes untouched, its SHA-256 saved), and the
  recorder loads it via `next=` in `scskiller.ini`. The recorder hooks both the device the mod returns and the system
  device under it, so it records what the driver actually compiles, including a mod's replaced shaders. Those shaders
  exist in no game file, so this PC's recording is their only source. Removal renames the mod back and never
  overwrites another file that took its place. OptiScaler, Special K (they pick their role from their file name) and
  vkd3d-proton (it runs the game on Vulkan) are refused.
- **Import** (`Recordings`): the game folder's `scskiller.db` is an inbox. When it changed since the last import
  (`GameRecord.RecordingInbox`, its size and write time), its records are merged into `recording.db` by record key,
  after the ones already there. Once `recording.db` is written, the inbox is emptied, but only while nothing has it
  open (the recorder holds it for the whole session) and only at the length that was read; once emptied, whatever it
  gets next is imported. `recording.db` is written to a temp file that is read back and compared before it replaces the
  old one.
- **Keys file**: next to `scskiller.ini` the app writes `scskiller.keys` ("SCSKKEY1", then 20-byte hashes): the shaders
  of the last index (`index.shaders`), the blobs `recording.db` holds and the key of every record that replays from the
  two. The recorder loads it in record mode and treats those as already recorded, so the emptied inbox only gets what's
  new, and a new pipeline's shipped shaders go in by hash, without their bytes: the install gives them back. A shader
  in no file of the game (built at run time, a mod's, a middleware DLL's) is recorded with its bytes. It is written
  when the recorder is installed, after an import, after a compile indexed another build and after Clear recording. A
  game never indexed, or one whose index isn't kept (`Sharing.SaveShipped`), has no shipped shaders in it and is
  recorded with every shader's bytes. A record whose shader `recording.db` names by hash and the last index no longer
  has is left out, so the recorder records it again with its bytes if the game still creates it. The shipped shaders
  are named only while the install is the build last indexed (`IndexIsInstalled`: the store's build id, or without one
  the exe's size and write time, the signals that mark a warm stale after a game update). A scan that finds another
  build rewrites the file without them (`GameRecord.KeysIndexHash` notes whose it names), and new pipelines are
  recorded with every shader's bytes until the next compile's index. A game updated and played before SCSKiller looks
  at it again records with the previous build's file: a record naming a shader the new build doesn't ship then has no
  bytes, the compile skips that record alone, and its index takes it out of the file so it is recorded again. A
  Battle.net or Ubisoft game, which has no build id, patched without its exe changing looks unchanged. Measured on
  three recordings taken as a first session, `scskiller.db` is 2-13% of its size with every shader's bytes, nearly all
  of it the records themselves; an index of 286,000 shaders makes a 5.7 MB file, which the recorder keeps as a sorted
  array (40 ms to load).
- **Stored form** (`PsoDb.WriteCompact`): `recording.db` is `\0SCSKREC`, a version byte, the length of the proxy db it
  holds, then that db Brotli-compressed (quality 6). Readers (`PsoDb.Read`) take either form; `PsoDb.CopyRaw` gives the
  proxy a plain db. A shader blob that a record names and the build's index has is left out: the install gives it back.
  Root signatures, shaders in no file of the game (built at run time, a mod's, middleware DLLs') and blobs no record
  names keep their bytes. Shaders are dropped by the index only when it's of the build installed now
  (`GameRecord.IndexGameVersion` and `index.shaders`); otherwise every byte is kept until a compile indexes the build,
  which drops what that index has (`GameRecord.RecordingIndexHash`) and rewrites the keys file. Measured on five
  recordings, 87-98% of the bytes were shaders the index has, and the rest compresses about 20 times.
- **Planning and warming from it**: a compile prepares `work\recording.db` once, before planning: `recording.db`,
  merged with `community.db` when one is in use (`Community.Union`), with every shader it names by hash read from the
  install (`Rehydrate.Run`: the engine reader, then the middleware DLLs). The planner and the warm both use that file,
  so they see the same bytes as when the recording kept them. The readiness check reads records only, from
  `recording.db` or `community.db`, without merging them.
- **Migration**: a `recording.db` stored as a plain proxy db, or a `recording.all.db` beside it, is converted once in
  the background after a scan (`ScsKiller.MigrateRecordings`), never while the game or a compile runs: the inbox is
  imported, `recording.all.db` is deleted, and the old file stays until the new one reads back the same.
- **Recording limit** (`Settings.RecordingLimitMB`, one of `ScsKiller.RecordingLimits`, default 256 MB, 0 = unlimited;
  a stored value that isn't a choice becomes the next choice up, or unlimited): covers the game folder's
  `scskiller.db` and `recording.db`. The app writes what `recording.db` leaves as
  `max_db_bytes` into its own `scskiller.ini` (`ScsKiller.DbCap`); the recorder stops appending once the db reaches it,
  a record going in whole or not at all, and still writes the csv and log. The game then shows "Recording paused: limit
  reached" (`GameState.RecordingPaused`) until an import empties the db.
- **Clear recording** (`IScsKiller.ClearRecording`, CLI `record clear <game>`): deletes the recording's files, all or
  none, never while the game runs or compiles; the recorder and its ini stay, and the keys file is rewritten. The next compile
  plans from the game's files.
- **Frame times** (`scskiller_frames.bin`, read by `FrameLog`): at the first device the recorder takes a factory from
  the process's `dxgi.dll` and hooks its `CreateSwapChain*` slots, then `Present` / `Present1` of every swap chain it
  creates, keeping the original per vtable (a wrapper's swap chain and the real one differ). A frame is the QPC at which
  the outermost present of a thread returns, as PresentMon's `FrameTime` counts (measured equal to PresentMon frame for
  frame); nested presents and `DXGI_PRESENT_TEST` aren't frames. The hook queues the timestamp and a thread writes the
  file once a second. The file is u32 records: `0xFFFFFFFF` + u64 unix ms, u64 microseconds since the recorder loaded
  (the csv's `t_ms` clock), u64 QPC, u64 QPC frequency opens a launch; top 4 bits 0-14 = a frame of that swap chain,
  the low 28 bits the microseconds since the previous record; top 4 bits 15 = no frame for the low 28 bits'
  milliseconds. The file holds the last launch that presented, replaced at its first frame (3 hours at 300 FPS is
  13 MB; a launch stops writing at 32 MB). It isn't part of the recording: not in the recording limit or the recording's
  size, never shared; Clear recording and removing the recorder delete it with the csv. `frames=0` in `scskiller.ini` turns it off (diagnostics
  only). The game page's last session (`GameState.LastFrames`) reads the last launch with the creates csv of the
  same launch (the `#session` stamped with it). A frame is **cold-filled** when its overlapping creates of 100 ms or
  more (not a RayQuery PSO at the floor) sum to half its length or more: a compiled run's load creates stay under
  100 ms, cold compiles take 159 ms at the median. A frame of 50 ms or more is, in order:
  - during startup (from the first create to the first 3 s with fewer than 30 creates, extended over any second of 100
    creates or more within 10 s after that, a title screen's second precompile, and over a slow frame with no create
    that spans its end or starts within 2 s of it, loading a save): **loading, compiling shaders** if cold-filled, else
    **loading**;
  - in the last 10 s before the last frame: **quitting**;
  - cold-filled: a **shader stutter**, however many creates overlap it (a level load that compiles is shader cost);
  - overlapped by 100 creates or more: **loading** (a load whose creates are fast);
  - overlapped by a create of 10 ms or more (not a RayQuery PSO at the floor, a ray tracing state object only from
    100 ms): a **shader stutter**;
  - else an **other hitch**, left out from 5 s (a pause).

  Play is what lies between startup and quitting; the 1% low is of its frames. A compile on a worker thread that the
  render thread waits for is still a shader stutter, so the csv's `presents` column isn't used.
- The recorder loads NVAPI only on NVIDIA and only in games without anti-cheat, to record the shader-extension state
  pipelines are created with.

## scskiller_warm.exe protocol

```
scskiller_warm.exe <workdir> <game exe file name> [--threads N] [--priority below|idle] [--start N]
                   [--stop-event <name>] [--adapter-luid <hex>] [--rt-threads N] [--skip i,j,...] [--memory-mb N]
                   [--package <app user model id>] [--stage-path <install folder>\<exe dir in the install>\<exe>]
                   [--skip-keys <sha1 hex>,...] [--isolate i,j,...] [--pass K]
                   [--ags <amd_ags_x64.dll> --ags-app <name> --ags-engine <name>]
```

- `workdir` holds `scskiller.db` (the recording, may be missing) and `scskiller_gen.db` (the plan's templates and
  items). Staging goes to `<workdir>\stage\`: a copy of `scskiller_warm.exe` named `<game exe>`, the proxy `d3d12.dll`,
  and the two databases. The staged copy runs as a child process and prints the output; its log is
  `<workdir>\stage\scskiller.log`.
- `--stage-path`: a relative path ending in `<game exe>`, without `.` or `..` parts, for AMD's path-matched profiles.
  The child is staged and launched under `<workdir>\stage\<its folders>\`, and its outputs move up to `stage\` when it
  exits. A path that would reach MAX_PATH stages without it. Ignored with `--package`.
- `--ags <dll> --ags-app <name> --ags-engine <name>` (AMD): the child creates its device through that AGS 6 DLL with
  those names, as Unreal does (see [AMD, D3D12](#amd-d3d12)). The DLL's imports are resolved from System32 only. If AGS
  fails, a stderr line says why and the child creates a plain device. The app passes it only where the registration is
  proven (`ScsKiller.AgsFor`), with the game's own `amd_ags_x64.dll` when it is AGS 6, else the one in `native\` (AMD's
  signed release, pinned by hash).
- `--pass K` (AMD careful compile): `<workdir>\scskiller_pass.bin` holds one byte per item, its pass (1-32, or 255 for
  the rest). The process creates only pass K's items and counts the others done, so `done`, `total` and `--start` keep
  their meaning.
- stdout carries one JSON object per line and nothing else (usage and other text go to stderr):
  - `{"event":"start","total":117197,"adapter":"<DXGI adapter description>","exe":"game.exe"}`
  - `{"event":"progress","done":64210,"total":117197,"failed":3,"rate":480.2}` about every 500 ms
  - `{"event":"done","done":117197,"total":117197,"failed":5,"seconds":288.1,"stopped":false,"crashed":[]}`
  - `{"event":"retry","from":F,"rtThreads":N,"failedItem":X,...}` (see [Faults and retries](#faults-and-retries))
  - `{"event":"error","message":"..."}`
- `done` counts from item 0, including items skipped by `--start`; `failed` and `rate` (items per second) are this
  run's. Replay order is deterministic (database file order), so after a stop, pass its `done` as the next `--start`.
- `--stop-event`: a named manual-reset event. When set, workers stop taking items, in-flight compiles finish, `done` is
  printed with `"stopped":true`, exit code 0. Never kill the process to stop it: the driver may not write its cache.
- Pause: the caller suspends the process. The child sees the heartbeat stop within about a second and its workers wait.
  If `scskiller_warm.exe` dies, the child stops gracefully.
- `--threads N`: default logical CPUs - 2. `--priority below` (default): below-normal worker threads; `idle`: idle
  priority class and threads.
- Heap: on NVIDIA the warmer runs `native\segheap\scskiller_warm.exe`, the same code whose manifest selects the segment
  heap (`Warmer.ExeFor`); it stages the proxy from `native\`. On the NT heap NVIDIA's compiler threads wait on the
  process heap's lock: a 60,000-pipeline warm on 32 threads took 742 s at 27% CPU, 227 s at 87% on the segment heap.
  AMD's compiler doesn't wait there, and on the segment heap an 84,000-pipeline AMD warm took about 25% longer (147 s
  against 118 s), so AMD and other vendors keep the NT heap.
- `--adapter-luid <hex>`: `(HighPart << 32) | LowPart`; default the hardware adapter with the most dedicated VRAM.
- `--package`: run the staged copy with that app's package identity (see [NVIDIA, D3D12](#nvidia-d3d12)). The child
  then gets none of the caller's environment.
- Exit codes: 0 completed or stopped; 1 failure, after an `error` line; 3 after a `retry` line.
- D3D12 failures are counted by record kind and cause at the end of the log. `SCSKILLER_D3D12_DEBUG=1` turns on the
  D3D12 debug layer, `SCSKILLER_D3D11_DEBUG=1` the D3D11 one.

### Memory budget

`--memory-mb N` (0 = none) limits the staged process's private memory, checked every 100 ms. Shader bytes aren't loaded:
both databases are mapped read-only and each blob is a view into the file. Parked ray tracing objects get a quarter of
the budget (at least 256 MB) and are released when it's exceeded; if the process is still over, the number of running
workers drops by one every 5 s and rises again after 30 s under 80% of the budget. Loading the records and the device
take about 1.3 GB, the floor of what a budget can hold; the driver's cache write at exit comes on top.

The app passes `Settings.MaxCompileMemoryGB`, whose Auto value scales with physical memory: 2 GB up to 20 GB of RAM,
4 up to 28, 6 up to 40, 8 up to 56, else 16 (`ScsKiller.AutoCompileMemoryGB`).

### Faults and retries

A driver fault or hang in a ray tracing state object never stops the warm or skips items silently:

- State objects are never released on worker threads during the run: they are parked and released in batches on one
  thread while no create runs (`SCSKILLER_WARM_RT_PARK_MB`, default 2048). This avoids a driver race on concurrent
  releases.
- Every driver call on a state object is SEH-guarded. After the first fault or hang, the process's driver isn't trusted
  for ray tracing: later state objects are left unfinished, PSOs go on, and the run ends with a `retry` line. The warmer
  then starts a new process from `from` with a quarter of the ray tracing threads (32 → 8 → 2 → 1). Only an object that
  faults with one ray tracing thread counts as failed, and later processes skip it (`--skip`).
- A worker stuck in one item for over 60 s (2 s once stopping) is abandoned; a stuck PSO counts as failed and a new
  worker carries on, at most 8 times.
- **A removed device** (`DXGI_ERROR_DEVICE_REMOVED`) makes every later create in that process fail. The replay checks
  `GetDeviceRemovedReason` after a failed create and every 100 ms; once removed, it takes no more items and ends with
  a `retry` line whose `reason` is `removed`. The items whose create failed on the removed device are the suspects: one
  alone is blamed and its record key listed in `crashed`; several go to `isolate`, and the next process (`--isolate`)
  creates them one at a time before its workers start, to find the one that removes the device. `--skip-keys` items are
  never created and count in `done` but not in `failed`. The warmer relaunches with every key seen so far, at most
  `Warmer.MaxRecoveries` (20) times per compile. The app keeps the keys per game and driver (`GameRecord.CrashKeys`),
  skips them in every later compile on that driver, and reports them apart ("N skipped (they crash the GPU driver)").
  A compile on another driver gives each one retry.
- After its last line the child signals `Local\SCSKiller.Final.<pid>`; if it hasn't exited `SCSKILLER_WARM_EXIT_S`
  (default 600) seconds later, it's terminated and the run returns 3.

### D3D11 items

D3D11 items live in `scskiller_gen.db` only and are replayed after all D3D12 items, with the same counting, stop and
resume rules.

- `'1'` (24 bytes): `u32 stage` (1 VS, 2 PS, 3 DS, 4 HS, 5 GS, 6 CS) + the SHA-1 of a `'B'` blob holding a DXBC
  container. One item per shader.
- `'2'` (40 bytes): a HS SHA-1 + a DS SHA-1, a tessellation pair from one shader map whose control points link. Every
  HS and DS is warmed in at least one pair.
- Each item is one draw or dispatch into a 1×1 target (`proxy/warm11.cpp`): a VS with an input layout built from its
  input signature, a PS behind a generated pass-through VS, a pair behind a pass-through VS with the right patch list.
  Every declared resource slot gets a dummy of its kind. The draw uses `DrawInstancedIndirect` / `DispatchIndirect`
  with zero counts in a GPU buffer, so the driver compiles but no game shader runs.
- One device per thread, at most 4. A call taking over 60 s abandons its worker, as for D3D12.

## Ray tracing state objects

The recorder hooks `ID3D12Device5::CreateStateObject` and `ID3D12Device7::AddToStateObject`; their vtable slots are
checked against `d3d12.h` at compile time (`proxy/vtslots.cpp`). Records are replayed exactly, in file order.

- `'R'` = CreateStateObject: u32 `D3D12_STATE_OBJECT_TYPE`, u32 n, then n subobjects in the game's order, each u32
  `D3D12_STATE_SUBOBJECT_TYPE` + its canonical form:
  - `STATE_OBJECT_CONFIG` u32 flags; `NODE_MASK` u32; `RAYTRACING_PIPELINE_CONFIG` u32 depth; `..._CONFIG1` u32 depth,
    u32 flags; `RAYTRACING_SHADER_CONFIG` u32 payload, u32 attributes;
  - `GLOBAL_` / `LOCAL_ROOT_SIGNATURE`: root signature sha1[20] (a `'B'` blob);
  - `DXIL_LIBRARY`: library sha1[20] (a `'B'` blob, hashed like a shader) + exports;
  - `EXISTING_COLLECTION`: the record key[20] of the collection's own `'R'` + exports;
  - `SUBOBJECT_TO_EXPORTS_ASSOCIATION`: u32 index of the associated subobject + names;
    `DXIL_SUBOBJECT_TO_EXPORTS_ASSOCIATION`: str subobject name + names;
  - `HIT_GROUP`: str export, u32 type, str any hit, str closest hit, str intersection;
  - exports = u32 n + n × (str name, str rename, u32 flags); names = u32 n + n × str; str = u32 UTF-16 length
    (0xFFFFFFFF = null) + the characters.
- `'A'` = AddToStateObject: the grown object's record key[20] (an `'R'` or another `'A'`) + the addition's body as
  above. The result takes the `'A'` record's key, so chains are recorded link by link.
- `'N'` = the NVAPI state a record (of any tag) was created with: its key[20], u32 shader-extension slot (~0u = none),
  u32 space, u32 scope (1 device, 2 thread, 3 PSO extension), u32 `NvAPI_D3D12_SetCreatePipelineStateOptions` flags.
  Written once per record and state, after the record. The warm sets the state on the creating thread around that
  create.
- `'L'` (hash-only recordings only) = a record's key[20] whose shaders the build doesn't ship: built at run time or by a
  mod.
- Not recorded: work graphs, generic programs, unknown subobjects, and anything built on an object the recorder
  didn't record.
- Replay: each record is created once, by the worker that takes it or earlier by one whose record builds on it, so
  parallel workers and `--start` work. A record whose dependency failed fails too. On a device without `Device5` /
  `Device7` every one fails with `E_NOINTERFACE`.

### Stream output

A pipeline with a stream-output declaration (`NumEntries` > 0) stores it in its record: a `'G'` desc appends it after
`Flags`, a stream writes it as subobject `0x10007` (`PsoDb.SoDecl`): u32 n, n × (u32 stream, u32 semantic length
(0xFFFFFFFF = a gap) + ASCII, u32 index, start component, component count, output slot), u32 stride count + strides,
u32 rasterized stream. Without entries the record is unchanged. `SCSKILLER_WARM_ROUNDTRIP=1` makes the warm
re-serialize every decoded record and compare it with the original.

## scskiller_creates.csv

Written by the recorder next to the game's exe. Rows are `t_ms,kind,known,tuple_known,ms,key,proxy_ms,tid,presents`:

- `t_ms`: when the create returned, in ms since the recorder loaded; `ms`: the driver call alone;
- `key`: the record key in hex (SHA-1 of tag + canonical payload, as `PsoDb.Rec.Key`); a key the db or the keys file
  held when the game started has `known` 1; the db gets the record of every other key, until it reaches `max_db_bytes`;
- `proxy_ms`: the recorder's own time outside `ms`;
- `tid`: the creating thread's id; `presents` 1 when that thread had presented a frame before the create (a create there
  holds up the frame), 0 otherwise, including a render thread's creates before its first frame and every create with
  `frames=0`.

Readers ignore lines starting with `#` and accept extra fields. Two markers give real play time:

- `#session,<unix_ms_utc>,<exe file name>` when the recorder starts. The name is `GetModuleFileNameW(NULL)`'s, exactly
  as launched: the case AMD's cache key uses.
- `#end,<unix_ms_utc>` at process detach, best effort.

A staged warm writes neither marker.

## Plan file

`plan.bin` holds templates (canonical pipeline payloads in the recorder's database encoding, recorded or synthesized),
root signature blobs (generated, not game content), and plan items (template key, root signature hash, stage → shader
SHA-1, optional input layout), plus the plan header. It never contains game shader bytes: those are read from the
install at materialize time.

## Clear cache

`ClearGameCache` makes a game's next run cold with one all-or-nothing delete of:

- its driver cache files, by the keys learned for it (see [Driver caches](#driver-caches));
- its Windows shader cache folders under `%LOCALAPPDATA%\D3DSCache`, identified by reading the exe paths in their
  SQLite databases (read-only, with Windows' own `winsqlite3.dll`);
- only when asked (CLI `cache clear <game> --game-precache`, the app's "Also delete the game's own shader cache"), the
  caches the game writes itself: Unreal's `<Project>_<ShaderPlatform>.upipelinecache` in its Saved folder and
  `*.ushaderprecache` files. The game rebuilds them at its next start, which then takes longer. The caches shipped in
  the install are never touched.

It's refused while the game runs (by process name; no process is opened), and on AMD when another game shares one of
its cache keys. Anti-cheat games get their driver cache cleared only. Clearing also resets the first-launch judgement
on AMD.
