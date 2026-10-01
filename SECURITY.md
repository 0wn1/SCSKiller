# Security policy

## Reporting a vulnerability

Please don't open a public issue. Report it privately through GitHub's private vulnerability reporting: the
**Security** tab of this repository, then **Report a vulnerability**.

Include what you found, the SCSKiller version (the app's About page shows it) and the steps to reproduce. You'll get a
first answer within 7 days. Please give us reasonable time to ship a fix before you disclose it.

## Scope

- The SCSKiller app, the `scskiller` command line and the native tools (`scskiller_warm.exe`, the recorder
  `d3d12.dll`).
- The recorder: anything it does in a game's process, and anything written outside SCSKiller's own folders or the
  folder of a game the recorder is installed in.
- The updater and the update feed signature: a tampered feed or package that the app accepts.
- The sign-in flow and the tokens the app stores.

Only the latest stable release is supported.
