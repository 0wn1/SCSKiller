# Contributing

Bugs and game requests go in Issues (use the templates). Security problems go through [SECURITY.md](SECURITY.md),
never a public issue.

## Anti-cheat games

SCSKiller never touches a game with anti-cheat beyond reading its files: no recorder, no injection, no opening its
processes. Changes that work around this aren't accepted.

## How pull requests are merged

Pull requests are reviewed here in public. This repository is updated once per release, so an accepted PR isn't merged
here: the maintainers apply it to the development tree with your authorship kept, and it ships in the next release. The
release commit credits you with a `Co-authored-by` line, the changelog thanks you by handle, and the PR is closed with a
link to that commit.

## Sign-off (DCO)

Every commit needs a `Signed-off-by` line, which certifies the [Developer Certificate of Origin](https://developercertificate.org/):
that you wrote the change or otherwise have the right to submit it under this project's licence.

```
git commit -s
```

Use the same name and email as the commit author (your GitHub noreply address is fine). Patches without a sign-off
can't be merged. By contributing you agree that your contribution is licensed under GPL-3.0-or-later with the
additional permission in [LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt).

## Build

Visual Studio 2022 with the C++ desktop workload (MSVC x64, CMake), and the .NET 10 SDK.

```
cmake -S proxy -B proxy/build -A x64
cmake --build proxy/build --config Release
dotnet build SCSKiller.slnx -c Release
```

The CMake configure downloads AMD's `amd_ags_x64.dll` (pinned by SHA-256). Offline, the build goes on without it and
warms of AGS games on AMD fall back to a plain device.

## Tests

```
dotnet test tests/SCSKiller.Tests -c Release --filter "Needs!=Gpu&Needs!=Game"
```

This is what CI runs, on a clean Windows machine with no games and no GPU, and it must pass. Tests that need more carry
a trait:

- `[Trait("Needs", "Gpu")]`: drives a real GPU and its driver cache.
- `[Trait("Needs", "Game")]`: reads installed games, launchers or recordings.

Give a new test the right trait, and return early when the game or GPU it needs isn't there. Never write a machine
path into a test: `TestEnv` finds games through Steam's library list, the `XboxGames` folders and GOG, plus
`SCSKILLER_TEST_GAMES_ROOT` (`;`-separated folders holding game install folders). GPU tests take a lock in
`SCSKILLER_DEV_DIR` (default `%TEMP%\scskiller-test`). When your change touches that area, run the GPU and game tests
locally too (`dotnet test tests/SCSKiller.Tests -c Release`). Tests must never write into a game folder, change the
driver's cache size or settings, or register scheduled tasks.

## Style

- Match the code around your change. Keep changes small, one topic per PR.
- Comment only what the code can't say: a non-obvious reason, an invariant, a driver or engine workaround, a file-format
  detail, a measured fact. No commented-out code.
- Add a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for anything a user would notice.
- Don't commit game files, shader dumps, recordings (`*.db`) or third-party binaries.
