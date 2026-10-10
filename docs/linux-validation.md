# Linux and shared-core validation

Validation environment: Linux x86_64, native KDE Wayland, Python 3.14 and the repository-pinned .NET SDK 10.0.401. Physical devices: MOMENTUM 5 model M5AEBT Black on firmware 6.21.5 and BTD700 on firmware 3.11.0. Windows-native build/UI and physical firmware flashing/recovery remain unverified locally.

## Automated coverage

Protocol tests cover framing, bounded parsing, model-aware control codecs, captured exchanges, USB reports, HID descriptors and firmware containers. Core tests cover capability revocation, serialized transactions, uncertain writes, cancellation/deadlines, noise sibling preservation, EQ topology/partial writes, delayed sound-mode convergence, codec transitions and simulated firmware transfers.

CoreHost tests cover typed private envelopes and prepared-plan ownership, consumption and expiry. Linux tests exercise BlueZ/HID ownership, actual Python-to-C# child processes, EOF/backpressure, cancellation/reaping, GUI editing/queues, CLI, local preferences, desktop integration and transactional installation. Captures are retained under `tests/CapturedPackets/M5/`; protocol and transaction behavior is implemented/tested in C#, not a second Python device engine.

Run from the repository root:

```sh
dotnet test --project tests/Momentum4.Protocol.Tests/Momentum4.Protocol.Tests.csproj -c Release
dotnet test --project tests/Momentum4.Core.Tests/Momentum4.Core.Tests.csproj -c Release
dotnet test --project tests/Momentum4.CoreHost.Tests/Momentum4.CoreHost.Tests.csproj -c Release
./linux/build.sh
PYTHONPATH=linux QT_QPA_PLATFORM=offscreen python3 -m unittest discover -s linux/tests -v
```

The recorded successful run contains 216 Protocol tests (plus one existing documentation-absence skip), 449 Core tests, 14 CoreHost tests and 334 Linux tests. Portable production/test projects passed format verification. The self-contained helper ran from `/` with an empty environment, without a separately installed .NET runtime.

## Hardware and desktop checks

The installed shared backend read M5 identity/capabilities and verified reversible changes to behavior/call settings, prompts, timer, sidetone, battery protection, immersive audio/head tracking, bass boost, sound mode, anti-wind, adaptive ANC, transparency, EQ and name. Setting values, EQ and name were restored after readback. Paired-device priority can change during a connection, so complete peer-list equality is not used as evidence of setting restoration; no peer-connect or personalization writes were requested.

Native Qt checks navigated all nine pages with the real headset and dongle at 460×780, found no horizontal overflow, and verified clean worker/helper shutdown. The BTD700 remained the default audio output. USB status and an explicit official firmware check succeeded; current/latest were both 3.11.0. No update was applied.

All 29 managed installation payload hashes were verified, including source/binary equality and executable permissions. Installer tests exercise missing/modified/oversized/symlinked files, rollback and uninstall, including Ctrl+C before/after filesystem mutation and interruption during rollback. Failed or interrupted rollback retains outstanding originals. The desktop entry, shell scripts and scoped udev rule passed their validators.

## Limits

- M5 writable controls are restricted to the verified model family and firmware 6.21.5; other profiles remain read-only.
- BTD700 headset-command forwarding is not established; see [transport findings](btd700-control-transport.md).
- Firmware transfer tests use simulated devices. Physical interruption/recovery is not verified.
- Real GeoClue entry/exit, manufacturer Sound Personalization generation/activation, M5 peer connection and headset maintenance writes are not established.
- An in-flight synchronous kernel HID feature ioctl cannot be forcibly interrupted in userspace; its descriptor/lease remains owned until return.
- Portable tests do not substitute for Windows-native CI and hardware testing.
