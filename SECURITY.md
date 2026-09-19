# Security policy

This is a hobby project maintained in my spare time, with no warranty (see the README's **Legal** section).

## Scope

The app runs entirely on your machine. It talks to the headset over Bluetooth and stores settings, presets and logs
under `%LOCALAPPDATA%\Momentum4Control\`. The only optional network call is the update check against the GitHub
releases API, which is off by default. There is no server, account or telemetry.

Commands known to be dangerous to the headset (firmware update, factory reset, disconnecting or deleting pairings) are
deliberately blocked in every send policy and cannot be triggered from the app.

## Reporting

If you find a security problem, please open an issue describing it. For something you'd rather not post publicly, mark
the issue accordingly or use GitHub's private vulnerability reporting for this repository. Please give me reasonable
time to look into it before disclosing details publicly.

Because binaries are unsigned, only download releases from this repository, and verify the SHA-256 hashes printed by
the release workflow if in doubt.
