# Changelog

All notable changes to the SCSKiller app and command line. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.1.1] - 2026-10-03

### Added

- The Witcher 3: Wild Hunt's DirectX 12 build compiles without a recording on NVIDIA: SCSKiller reads its shader caches
  and the pipelines its materials use.

### Changed

- Clicking the Library's refresh button also fetches everything from the server, whatever its age (at most every 10
  minutes): the known-stutter and tested-engine lists, community recordings and shared packs, your supporter status and
  the update check.
- Every game's compile plan is rebuilt once after this update, while the PC is idle; a compiled game whose new plan adds
  pipelines says how many more it can compile. Plans for games with tessellation and geometry shaders, or with ray
  tracing, cover more pipelines.

### Fixed

- A game started through its launcher, such as The Witcher 3's REDprelauncher, is found by the exe the launcher starts,
  so its recorder goes where the game loads it. A recorder already next to the launcher moves there once neither runs,
  with the last session's report and frame times.
- A GPU driver updated while SCSKiller runs is noticed within minutes: the driver shown, the games that need compiling
  again and the driver-update notification follow it without a restart. A GPU of another vendor asks for a restart.
- The app, the command line and the scheduled task no longer compile the same game at once: the second one waits.
- A compile that was stopped and then continued reports the pipelines that failed before the stop, and starts over
  after a driver update.
- A game updated since the last scan is compiled with the engine and checks of the installed build.
- NVIDIA: a compiled game whose shader cache was removed (a shader cache reset) shows as needing a compile again.
- A failed compile's message points to a log that is still there.
- A recorder chained to a mod's d3d12.dll whose install was cut off gets its settings file back, so the mod loads.
- A launch played while a recompile ran no longer judges the compile that ends after it.
- A recording imported just before SCSKiller was closed or crashed is always taken into the next compile.
- Turning "Share anonymous shader hashes" off stops a sharing pass already under way: nothing more is uploaded.
- Signing out and in again while the app renews its sign-in in the background no longer signs the new sign-in out.
- A damaged record in a game's scskiller.db fails only that game's import, not the whole library scan.
- A known-stutter or tested-engines list from the server with an empty entry is ignored instead of stopping the other
  list from updating.
- While the community database refuses or can't be reached, the app waits before asking again instead of asking once
  per game.
- Clear cache also finds a game's D3D shader cache when its path has non-ASCII characters (an accented user name), and
  deletes read-only cache files too.
- Stable updates are found while the SCSKiller server is down, also with an expired sign-in. Beta and alpha also offer a
  newer stable release.
- A game update that changes only its shipped pipeline list or its inline shaders gets a new plan.
- Games are found more reliably: an Epic game whose manifest names itself as the main game, Xbox games on any drive,
  and games installed under a folder named like Setup or Redist.
- A RE Engine game with a patch file that can't be read shows as unsupported instead of compiling outdated shaders.
- Frame times: failed presents no longer count as frames, and a frame file that can't be written no longer shifts the
  times after it. Games that create their device through `ID3D12DeviceFactory` are recorded.
- A compile that fails while it is being watched no longer leaves its process running.

## [1.1.0] - 2026-10-02

### Added

- Shared packs for the FidelityFX and XeSS upscalers: a game that ships an upscaler DLL version someone has recorded on
  the same GPU vendor gets those shaders compiled, free and without an account or a recording of its own. Packs are
  uploaded only with "Share anonymous shader hashes" on.

### Changed

- The installer is `SCSKiller-Setup.exe`, without the version, so its download link always gets the latest release.
- Every game's compile plan is rebuilt once after this update, while the PC is idle, and games compiled before ask for
  one more compile; it mostly finds the shaders already in the driver cache.
- Unreal games: plans also use the pipeline list the game ships, so shader pairs its files alone can't match are
  compiled too. Unreal Engine 5.4 is on the list of tested engine versions.

### Fixed

- The recorder no longer crashes a game when an overlay hooks its presents after the recorder and the game makes another
  swap chain, when the game unloads d3d12.dll after creating a device, when NVAPI is called while the recorder hooks it,
  or when `next=` in scskiller.ini names the recorder itself.
- A damaged scskiller.db no longer makes the game allocate gigabytes when it starts; a full disk, or a second process
  recording in the same folder, no longer leaves a recording that can't be read past that point.
- The recorder no longer goes through the C runtime when it ends a launch's timings at exit, which could stop a game's
  exit if another thread had died holding its lock.
- Stopping a compile after a GPU driver fault or hang no longer makes the next compile skip the pipelines that were
  still unfinished.
- A compile whose process hangs at exit is always ended after the exit limit, also when its last pipelines finished
  right after it started.
- DirectX 11 hull shaders that take all 32 input registers compile instead of counting as failed.
- "Last time you played" no longer counts the shaders a game compiles while it starts up as compiles during play.
- Turning the recorder off removes all its files from the game folder, the last session's frame times included (an
  `scskiller.ini` you edited stays), and files an earlier version left there go at the next scan.
- Frame times: a slow load from the game's own pipeline library is no longer a shader stutter, a shader freeze of 5 s
  or more counts in the 1% low, and a launch never takes another launch's frames.
- Quit waits for a compile you just removed from the queue to save its cache, like any other running compile.
- Choosing another update channel no longer installs an update downloaded from the one before.
- The driver-update notification's buttons work after SCSKiller was closed.
- "Compile queue" also starts games queued to compile when the PC is idle.
- On AMD, the library's cache bar compares the DirectX 12 cache with its limit, not both caches.
- `scskiller compile` refuses an option it doesn't know or one missing its value, and Ctrl+C before a compile
  starts is no success.
- Anti-cheat files and folders are found anywhere in a game's install, hidden or system ones too, and an install with a
  folder SCSKiller can't list counts as anti-cheat, so the recorder is never offered for those games. An update that
  adds anti-cheat while the recorder is being installed takes it out again.
- Clear cache deletes only the Windows shader cache's own files in a game's D3DSCache folder, and on NVIDIA refuses
  while another installed game has the same exe name (the driver gives both one cache).
- A recording imported by the app while the command line compacts it, or the other way round, keeps every record.
- SCSKiller never writes into a game's folder while the game runs, also when a launcher started it under another exe
  name, and a game whose launcher id isn't a valid folder name gets a data folder of its own.
- A malformed root signature in a recording or a community download no longer breaks a game's whole plan: only the
  pipelines that use it are left out.
- An update download stops at the size the signed feed gives, and gives up when no data arrives for two minutes (the
  next check tries again).

## [1.0.0] - 2026-10-01

The first public release.

### Added

- Compile a game's shaders into the NVIDIA or AMD driver cache before you play, so the game doesn't stutter while it
  compiles them. Works from the app or the `scskiller` command line.
- An installer (`SCSKiller-<version>-Setup.exe`) and a portable version (`SCSKiller-<version>-Portable.zip`); both
  update themselves.
- DirectX 12 games on NVIDIA and AMD, and DirectX 11 games on NVIDIA.
- Finds installed games from Steam, Epic Games, EA app, GOG, Ubisoft Connect, Xbox (PC) and Battle.net.
- Reads shaders straight from the game files for Unreal Engine, Unity, FromSoftware and RE Engine games, and scans
  other games' files for shader containers.
- An optional recorder for games whose files aren't enough: it captures the pipelines a game builds while you play, so
  the next compile covers them. It can record alongside a `d3d12.dll` mod such as ReShade when you turn that on for the
  game. It is never offered for games with anti-cheat.
- The recorder also times each frame. A game's page shows the last session's frame-time graph, with the stutters from
  shader compiles told apart from other hitches, the 1% low and a list of slow frames. Frame times stay on your PC.
- A recording keeps only the hash of a shader the game ships, and reads the shader back from the game files. Recording
  limit per game: 32 MB, 128 MB, 256 MB (the default), 512 MB, 1 GB or Unlimited, in Settings. The app shows the space
  each game's recording uses and has a Clear recording button. A game that recorded new pipelines since its last
  compile says so.
- Uninstalling SCSKiller removes the recorder and its files from every game folder, and puts back a mod it was
  recording alongside.
- On AMD, the driver's shader cache is fixed at 16 GB. The queue warns when the games in it won't fit, and a game
  whose cache the driver trimmed to make room shows as needing a rebuild.
- On AMD, games whose first launch still compiled many pipelines show "Partly warmed" and offer a careful compile,
  which is slower but reaches more of them.
- On NVIDIA, compiles run about three times faster, and ray-traced pipelines that the driver partly recompiles at
  every launch are listed on their own, not counted as stutters.
- Recompiles after a GPU driver update, on a schedule or by hand.
- Notices a game update, also one installed while SCSKiller runs: the game shows as needing a rebuild, and the next
  compile covers the installed version.
- A Play button in the Library and on a game's page for Steam, Epic Games, Xbox, GOG (with GOG Galaxy) and Ubisoft
  Connect games. It starts the game through its store, never on anti-cheat games, and waits while the game compiles.
- A game's page shows how long the last session lasted, also for games that close without a clean exit, as Unreal
  games do.
- A community database of shader hashes for Patreon supporters: games compile from other players' recordings without
  recording them yourself. Without a membership, a game that needs a recording still says when the database has one
  for its version, and how many pipelines it holds.
- "Share my shader hashes" (opt-in): uploads the hash-only form of your recordings (no shader code) under an anonymous
  device, never linked to your account.
- A notification when compiled games have new pipelines to compile (recorded while playing, or from the shared shader
  hashes), with Compile now and Show. It comes once per change and never while the game runs; Settings can turn it off.
- An anonymous daily check that counts active installs. Once a day the app tells the server that an install is active,
  with the app's version and the GPU vendor (NVIDIA, AMD or other) and no identifier: nothing about the PC or its
  games. "Send an anonymous daily check" in Settings turns it off.
- Status texts in plain words, such as "no recording needed" or "turn on recording and play for about 5 minutes". The
  list of engine versions SCSKiller has tested updates from the server, like the list of games known to stutter.
- A minimum window size at which every page still fits.
- Clear cache: removes a game's driver cache, its Windows shader cache and Unreal's own pipeline cache, so the next run
  starts cold.
