# Changelog

All notable changes to the SCSKiller app and command line. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
