# Momentum Control for Linux

A native Qt desktop app and command line for paired Sennheiser headsets. It matches the Windows app’s compact dark interface: a black background, rounded cards, a My devices screen, compact device sections, noise modes, vertical EQ sliders and control switches. The interface uses the existing PySide6 widgets and system font, with no added UI libraries. Windows and Linux share `Momentum4.Core` and `Momentum4.Protocol`; each keeps its native frontend. Linux bundles the same C# core in a private helper executable. See [architecture and adding features](../docs/architecture.md).

MOMENTUM 4 uses the upstream verified command formats and firmware gates. MOMENTUM 5 has a limited hardware-verified profile for firmware **6.21.5**, tested on the raw model **M5AEBT Black**. The profile enables touch controls, bass boost, smart pause, comfort call, audio prompts, wind reduction, adaptive ANC, ANC level, transparency, ANC enablement, sound mode, equalizer, on-head detection, auto-answer, pause on transparency, auto power off, call transparency, battery protection, immersive audio and head tracking. Renaming is also verified. Other Momentum 5 firmware remains read-only; a Bluetooth name containing “Momentum 5” does not establish compatibility.

On the tested Momentum 5, wind reduction supports **Off** and **Maximum**. **Automatic** is unavailable: its command acknowledges success but reads back Maximum. The equalizer reports eight bands at **90, 325, 1500, 2500, 4500, 8000, 13000 and 16000 Hz**, with **±6 dB** gain limits. Compatibility is limited to the individually verified controls.

## Parity status

The Linux companion covers the main Windows controls and feasible mobile workflows. It does not reproduce every manufacturer feature.

| Area | Linux implementation | Remaining differences |
| --- | --- | --- |
| Windows headset controls | Noise modes and sliders, EQ and saved presets, behavior and call settings, timer, rename, battery and device information | M5 Automatic wind reduction is unavailable on the tested firmware |
| Windows desktop features | Optional tray menu, autostart, minimized launch, reconnect, transparency restoration, English/German, diagnostics and project release checks | No Windows-style tray quick-panel window, OS battery fallback when GAIA is disconnected, or CLI forwarding through the running GUI; release checks do not update Linux |
| Multipoint | Paired-device list, connection state and capacity; guarded M4 connect action | M5 connect remains read-only; no disconnect or pairing deletion |
| M5 immersive audio | Verified immersive audio and head tracking with readback | Requires M5 firmware 6.21.5; incompatible with Personalization mode |
| Mobile Sound Check | Local bass/mid/treble A/B EQ comparison and preset saving | Independent fixed comparisons, not the manufacturer's algorithm |
| Mobile Sound Zones | Private local zones applying compatible EQ presets through GeoClue | No phone/cloud synchronization; real location entry/exit remains unverified |
| Sound Personalization | Reports device state; avoids unsupported calibration writes | Profile generation and activation are not implemented; the native fitting/calibration contract remains unverified |
| BTD 700 | Independent USB status, supported codec selection and experimental firmware updater | Does not transport headset control commands; physical firmware flashing/recovery remains unverified |
| Headset maintenance | Firmware version, name and status | No headset firmware updater, factory reset or pairing management |

Smart Control Plus APK research used public plaintext configuration and narrowly selected protocol/UI code for interoperability. No manufacturer code, preset tables, assets or credentials are bundled. Sennheiser documents M5 spatial audio and head tracking in its [MOMENTUM 5 support information](https://support.sennheiser-hearing.com/hc/en-th/articles/36444900304157-MOMENTUM-5-Wireless-Before-You-Start); the official Android package is [Smart Control Plus](https://play.google.com/store/apps/details?id=com.sonova.chb.control).

## Requirements

- Python 3.11 or newer, PySide6, dbus-python and PyGObject (`gi`).
- To build from source: the .NET SDK pinned in `global.json`. The installed app bundles its runtime and does not require a separately installed .NET runtime.
- For headset controls: BlueZ and a PC Bluetooth adapter. Pair the headset in your desktop's Bluetooth settings first.
- For BTD 700 status/firmware: the USB dongle and the scoped hidraw permission rule below. USB discovery works independently of BlueZ.
- A powered-on headset advertising the GAIA Bluetooth service.

Install the Python packages from your distribution:

```sh
# Debian 13, Ubuntu 25.04 and newer
sudo apt install python3-pyside6.qtcore python3-pyside6.qtgui python3-pyside6.qtwidgets python3-dbus python3-gi
# Fedora, Nobara
sudo dnf install python3-pyside6 python3-dbus python3-gobject
# Arch
sudo pacman -S pyside6 python-dbus python-gobject
```

Ubuntu 24.04 does not package PySide6. Install the other two packages with apt and PySide6 from PyPI for the system
interpreter:

```sh
sudo apt install python3-pip python3-dbus python3-gi libegl1 libgl1 libxkbcommon0 libfontconfig1
/usr/bin/python3 -m pip install --user --break-system-packages PySide6-Essentials
```

Debian 12 supplies Python 3.11, which meets the Python requirement, but has no distribution PySide6 package and is not covered by the installation checks. Ubuntu 22.04 defaults to Python 3.10, below the required Python 3.11. Neither is currently in the supported/tested installation set.

This is an instruction for the user; the installer does not install system packages or require root.

**BTD 700:** a separate USB audio card reports the dongle's actual connection/streaming state, firmware and reported audio quality. It does not identify which headset is paired or expose its ANC/EQ controls. The dongle is not a BlueZ Bluetooth controller. No headset-command forwarding path is established; see the [USB transport findings](../docs/btd700-control-transport.md). Keep using it for sound, and explicitly establish a second Bluetooth connection between the headset and the PC's Bluetooth adapter by opening its device card or using **Connect controls** in Info. Opening a headset card explicitly connects its controls; the app keeps your audio output unchanged. The headset has a limited number of simultaneous connections; disconnect another control device if necessary.

## Install and run

From a release archive (`Momentum4Control-<version>-linux-x64.tar.gz`, or `-arm64` for 64-bit ARM), which already
contains the built helper, so no .NET SDK is needed:

```sh
tar -xzf Momentum4Control-<version>-linux-x64.tar.gz
cd momentum-control-linux-<version>
./install.sh
~/.local/bin/momentum-control
~/.local/bin/momentumctl --help
```

Or from the repository root, building the helper yourself:

```sh
./linux/build.sh
./linux/install.sh
```

“Momentum Control” also appears in the application launcher. Add `~/.local/bin` to your shell's PATH if you want to use the command names directly.

The installer preflights dependencies using `/usr/bin/python3`, checks that a private copy of the bundled helper starts and reports the supported protocol within ten seconds, copies the application into `~/.local/share/momentum4-control-linux`, and installs the GUI/CLI launchers, desktop entry and icon under `~/.local`. It does not link to the checkout, create a virtual environment, enable autostart, pair devices, or modify global configuration. Moving or deleting the checkout does not affect the installed app.

Release builds use `./linux/build.sh VERSION` followed by `./linux/package.sh VERSION`; the archive label, executable and `core/version.txt` must agree. A source checkout without built version metadata reports `development`. Diagnostics report the installed release version.

After source changes, rebuild with `./linux/build.sh` and re-run `./linux/install.sh` to update a managed installation. The manifest records the installed files and their hashes. An existing unrelated installation, modified managed file, unknown file in the application directory, or symlink in a target path causes refusal. Keep personal files outside the application directory. Install and update stage the complete payload and manifest before replacing managed files; ordinary write or rename failures and catchable interruptions such as Ctrl+C restore the previous files and modes and remove staging files. If rollback itself fails or is interrupted, unrestored original backups are retained. Uninstall similarly restores managed files if removal fails. This rollback covers caught filesystem errors, not a process crash or power loss.

To remove the app:

```sh
/usr/bin/python3 ~/.local/share/momentum4-control-linux/install.py --uninstall
```

Or, from the checkout: `./linux/uninstall.sh`. Removal deletes only verified application files and leaves unrelated files in `~/.local` intact. For rollback, uninstall, check out the previous application version, and run its installer again. Headset settings already applied are stored by the headset; uninstalling the app does not undo them.

For an isolated installation: `./linux/install.sh --prefix '/tmp/test prefix'`, then `./linux/uninstall.sh --prefix '/tmp/test prefix'`. `--home '/tmp/test home'` instead uses that directory's `.local`; it does not change HOME.

## Desktop controls

1. Turn Bluetooth on, power on the paired headset, and choose **Refresh devices**.
2. Open its **My devices** card to connect and read model, firmware, battery and settings. Open a BTD 700 card for USB audio and firmware.
3. Headset sections are **Noise**, **Equalizer**, **Controls** and **Info**; dongle sections are **Audio** and **Firmware**. Each device remembers its last section during this app session.
4. **My devices** and section navigation send no device commands and preserve queued writes. Target switching is disabled until work finishes. Discovery retains the same physical selection; a missing device is cleared rather than replaced. Use **Refresh** in Info after changing headset settings elsewhere.

ANC strength uses the same display scale as the Windows app: **100 is strongest ANC; 0 is weakest ANC**. The app maps this to the headset’s inverse device level. Strength appears only in custom ANC mode. **Full transparency** selects the full Transparent Hearing preset. On the verified Momentum 5 firmware 6.21.5 profile, a separate transparency amount slider uses the dedicated level getter and requires complete verified noise state. It stays visible after partial changes and is disabled while noise is off; it never enables noise automatically. Each level write briefly selects full transparency before applying the requested 0–100 amount. The tested firmware couples this amount to the ANC level register: partial amounts clear the full-transparency flag and preserve the adaptive flag, so the displayed preset follows actual flags rather than claiming a persistent transparency mode. Both level registers and related noise flags are verified after writing; the ANC strength control retains its existing inverse display scale. Mode changes run as one busy operation, check every required capability before sending a command, and display the new mode only after verified readback. A failed sequence reports an incomplete change; it does not promise rollback of headset settings. Sound mode provides Neutral, Equalizer and Podcast; Sound Personalization is displayed read-only. EQ gains remain stored but are audible only in Equalizer mode. Wind reduction offers Off, Maximum and Automatic where supported; Automatic is disabled on the verified Momentum 5 profile. Audio prompts map to off, tones, tones and voice. EQ uses the device's reported band frequencies and count; the sliders allow a conservative ±6 dB adjustment in 0.1 dB steps and the backend enforces the headset's actual limits. Dragging sends only the released value; keyboard changes commit on key release. Mouse-wheel input scrolls the page instead of changing gains.

Bluetooth and USB operations run on one persistent worker thread. Connection, discovery, manual refresh and dongle/firmware actions remain exclusive. During ordinary setting changes, noise-mode changes and background polls, supported editors stay interactive and a fixed status line shows applying/pending work. Different edits are retained; repeated pending changes to the same setting or EQ band replace the older intent. At most 32 pending keys are retained. The verified snapshot remains separate from displayed pending values, and noise modes change selection only after verified readback. Queued edits recheck current capabilities and EQ band mapping before execution; errors, device changes and closing discard them. Uncommitted slider drafts survive unrelated readback but are cancelled on focus loss or session changes. Stable band mappings retain the same EQ widgets and focus, and operations do not reset the user's scroll position. Settings reuse unrelated display metadata for an unchanged session, while model/firmware and the affected setting group are freshly read before and after each write. EQ writes also verify the full frequency topology before sending a band value. Full Refresh reads everything. Request-count and hardware timing measurements are separate; no latency estimate is implied here. Closing the window cancels pending reads/checks, closes the connection on the owning worker, and quits. During firmware apply, closing is refused until the operation finishes so the transfer worker remains alive. An optional tray icon and reconnect behavior are configured in App settings. Ordinary close still exits by default.

The Linux companion does not expose headset firmware updates, resets, pairing deletion, raw commands or Sound Personalization generation/writes. Manufacturer account services and vendor Sound Zone synchronization are unavailable. Local Sound Zones are described below. BTD 700 firmware is handled separately below. Unsupported controls are disabled with an explanation.

**M5 sound and behavior:** Immersive audio and head tracking require the verified M5 firmware profile. Head tracking is disabled while immersive audio is off, and Personalization cannot be combined with immersive audio; the app never changes sound modes automatically. Auto power off offers Never, 15, 30 or 60 minutes (0/900/1800/3600 seconds). Call transparency uses the verified sidetone range 0–5. Pause on transparency and battery protection follow fresh headset capabilities.

**Presets:** Equalizer offers Flat and user presets saved from confirmed gains. Save/Apply/Delete require an idle worker, no queued edits and no slider draft. Presets match the raw device model and the complete ordered frequency tuple. Applying sends one serialized full-curve request, verifies the complete readback and never switches the sound mode. An interrupted curve change can be partial; no rollback is promised.

**Sound check (local EQ):** Equalizer includes an independent A/B comparison dialog for bass, midrange and treble while you play your own music. It uses fixed bounded ±2 dB comparisons, preserves each stage’s baseline, and saves the last confirmed curve through the ordinary preset store. It is not the manufacturer’s fitting engine or Sound Personalization. Opening requires verified Equalizer mode and idle EQ without drafts or pending edits; it never switches sound modes, changes volume or plays audio. Every preview and explicit Restore uses one guarded full-curve request and waits for matching readback. Closing keeps the last applied EQ and sends no restore. Automatic polling, reconnect and zone application pause while the dialog is open; pending zone entries are skipped so manual comparison wins.

**Info:** The verified device name can be edited within the backend UTF-8 byte limit (16 on M5, 30 on M4). Renaming never changes the selected Bluetooth address. Paired devices display connection status, this-PC identity and capacity. M5 peer connection remains read-only until live verification; M4 connection requires verified identity and free capacity. There is no disconnect or pairing-delete action.

**App settings:** My devices contains refresh interval (Manual/10/30/60 seconds), English/German (applied on restart), reconnect, tray, close to tray, start minimized, managed autostart, transparency restoration, project-release checks and redacted diagnostics. Preferences use a dedicated `momentum-control` directory under Qt's application configuration location. Invalid existing preferences are reported and never overwritten by defaults. All optional behavior is off by default; refresh defaults to an idle-only 30 seconds.

Reconnect resolves only the explicitly chosen or validated stored address against fresh discovery, at most once every 10 seconds. It never opens a device page or chooses the first available headset. Optional restoration remembers only confirmed full transparency during the current session; it restores after reconnecting the same target only when fresh noise capabilities are present and ANC is already enabled. Partial transparency and a different device revoke the intent. Failed mutations are not replayed.

Tray actions use the same command queue. Close-to-tray and minimized startup work only with an enabled available tray; otherwise the window stays visible and closing exits. `momentum-control --minimized` honors the same fallback. Tray Quit bypasses close-to-tray, cancels work, closes the client on its worker and ends the process. Firmware application refuses both closing and quitting until the transfer finishes.

Project release checks are manual unless explicitly enabled once per run. They query the fixed public GitHub release endpoint with verified HTTPS, refused redirects and bounded response/time budgets. The result links to the project release containing Linux and Windows downloads. Linux updates are manual: download the archive for your architecture and run its installer. The check itself downloads no release artifacts and changes no installed files. Diagnostics export uses an explicit save dialog and contains only allowlisted device settings and at most 2000 enum action outcomes; it omits names, addresses, serials, peers, raw exceptions and packet data.

**Local Sound Zones:** App settings opens a separate local zone editor with explicit enablement, name, latitude/longitude, radius (100–50000 m), and an embedded verified EQ preset. Save captures only idle, confirmed EQ with the exact selected address/model/frequencies; overlapping zones for the same device are refused. Coordinates are private local data, excluded from diagnostics. There is no manufacturer account, phone synchronization or cloud service.

Location is requested only by **Use current location** or previously enabled automation after its exact configured headset is selected and verified. The lazy GeoClue provider runs on the existing worker. Missing permission or service is reported without changing headset state. Pending fixes are sampled no faster than 15 seconds, valid fixes no faster than 60 seconds. One-shot requests expire after 30 seconds and close on completion, cancellation, navigation, error or timeout. Disabling automation, changing targets, shutdown and long exclusive operations close the provider on its owning worker.

A confident zone entry submits the existing guarded full-curve EQ operation only when idle and still compatible. User edits during acquisition take precedence; a skipped entry is consumed rather than applied later. Leaving a zone keeps current settings. Uncertain/stale location fixes do not trigger changes, and failed EQ writes are not retried. Real permission prompts and GPS entry/exit remain unverified in automated/native UI checks, which use injected providers exclusively.

Named CLI commands use the same backend guards:

```sh
momentumctl set immersive_audio true
momentumctl set head_tracking false
momentumctl set pause_on_transparency true
momentumctl set auto_power_off 1800
momentumctl set sidetone 3
momentumctl set battery_protection true
momentumctl rename 'My Momentum'
momentumctl peers --json
momentumctl connect-peer 1 --expected-name 'Phone'
```

Add `--device ADDRESS` when more than one headset is present. `connect-peer` requires the exact current peer name and refuses unsupported model/firmware, stale identity or unavailable capacity.

## BTD 700 USB permissions and firmware

The installer includes `assets/70-momentum-btd700.rules` in its owned application payload; it does not alter `/etc` or install system rules. To grant the active local desktop user access only to the BTD 700 vendor HID interfaces:

```sh
sudo install -m 0644 ~/.local/share/momentum4-control-linux/assets/70-momentum-btd700.rules /etc/udev/rules.d/70-momentum-btd700.rules
sudo udevadm control --reload-rules
sudo udevadm trigger --action=change --subsystem-match=hidraw --property-match=ID_VENDOR_ID=3542 --property-match=ID_MODEL_ID=3001 --property-match=ID_USB_INTERFACE_NUM=00
sudo udevadm trigger --action=change --subsystem-match=hidraw --property-match=ID_VENDOR_ID=3542 --property-match=ID_MODEL_ID=3001 --property-match=ID_USB_INTERFACE_NUM=01
```

The targeted triggers use udev’s recorded vendor/product/interface properties. Unplug/replug the BTD 700 if those properties are not populated yet. No root application or world-writable device permissions are required. Removing the app leaves this separately installed rule in place. To remove it:

```sh
sudo rm /etc/udev/rules.d/70-momentum-btd700.rules
sudo udevadm control --reload-rules
```

Then unplug/replug the dongle to discard its existing ACL. These are user-operated system changes, separate from `install.py`.

**Refresh devices** and **Refresh audio** read USB status only; they never contact the internet or begin an upgrade session. **Check for updates** explicitly queries the official Sennheiser consumer firmware service and prepares a strictly newer, compatible package when one exists. Check results show current/latest versions and failures. The observed dongle firmware and public latest are both **3.11.0**, so no update is offered. No physical flash was performed.

A prepared newer update enables **Update firmware** only for that same selected dongle with a known non-streaming state. Stop playback and calls first. Switching devices or refreshing invalidates preparation. Confirmation names both versions and explains audio interruption/reboot, missing rollback support and hardware-unverified recovery. The transfer is **experimental**: fake protocol peers test the host state machine, but physical flashing and interrupted-update recovery have not been verified. Failures retain the failed stage; failures after an update starts report recovery required; there is no automatic reset, retry, resume or rollback. Keep the dongle connected until the operation terminates. Success requires the updater's final firmware/config verification.

CLI commands are independent of the Bluetooth headset commands:

```sh
momentumctl dongle-status --json
momentumctl dongle-codec aptx-adaptive --json
momentumctl dongle-check --json
momentumctl dongle-update --confirm
```

The Audio section shows the observed active codec separately from the requested choice. **Apply codec** uses only known codecs in a freshly reported supported mask and requires a standard connected audio mode; calls, broadcast, disconnected or unknown states disable it. Switching can briefly reconnect audio. Verification errors clear the active claim and require another status read. CLI choices are `sbc`, `aptx-classic`, `aptx-adaptive`, `aptx-lossless`, `aptx-lite` and `lc3`; numeric/raw masks are rejected.

With multiple dongles, select a one-based discovery index using `--dongle 1`. Status/check never flash. Update without `--confirm` rejects before discovery or firmware access. `--confirm` accepts the same experimental-transfer, audio interruption/reboot, no-rollback and unverified-recovery conditions shown in the GUI. The CLI offers no arbitrary command, local firmware file, URL override, downgrade or same-version reinstall.

## Troubleshooting

- **No devices found:** enable the PC Bluetooth adapter, pair the headset in system settings, and ensure the headset is powered on. A BTD 700 alone cannot provide the control connection.
- **USB status unavailable:** check the scoped uaccess rule and the active local desktop session; close other apps using the dongle. USB failures do not hide paired Bluetooth headsets, and unavailable BlueZ does not hide the dongle.
- **Connection fails or times out:** move the headset closer, check that another app is not holding the GAIA control channel, and free a Bluetooth connection if the headset is at its connection limit. Select Connect controls to try again.
- **Read-only or disabled controls:** the actual model/firmware/feature has not met the verified capability gate, or the setting could not be read. The verified Momentum 5 profile is limited to firmware 6.21.5 and the controls listed above.
- **Missing Python dependency:** install the packages above using the system interpreter, then retry. Dependencies in a separate virtual environment do not satisfy the installed launchers.
- **Installer refuses an existing file:** inspect the named target; it belongs to something else or differs from the managed manifest. The installer does not remove it automatically.

## Development verification

```sh
dotnet test --project tests/Momentum4.Protocol.Tests/Momentum4.Protocol.Tests.csproj -c Release
dotnet test --project tests/Momentum4.Core.Tests/Momentum4.Core.Tests.csproj -c Release
dotnet test --project tests/Momentum4.CoreHost.Tests/Momentum4.CoreHost.Tests.csproj -c Release
./linux/build.sh
PYTHONPATH=linux QT_QPA_PLATFORM=offscreen python3 -m unittest discover -s linux/tests -v
python3 -m py_compile linux/momentum_control/*.py linux/install.py
desktop-file-validate linux/assets/momentum-control.desktop
```

Shared C# tests use captured protocol frames (including `tests/CapturedPackets/M5/`), injected transports and fake update peers. Python tests cover Linux adapters, the frontend and a real child process running the shared core. Build the helper before running Python tests. No test performs live USB transfers or flashing. Desktop tests exercise thread ownership, busy/error/write-denial states, stale results, peer-disconnect retry, supported dropdown values, and shutdown. Installer tests use temporary prefixes, verify actual launcher behavior, and cover modified files, unrelated files, symlink refusal and rollback after injected write, rename or removal failures. Hardware access is not needed for these tests.

Startup selects Qt’s Unix event dispatcher before creating the application. This keeps GUI events separate from the default GLib context used by the Bluetooth worker; sharing that context can cause native startup crashes. The setting is process-local and requires no shell configuration. The subprocess startup test enters the real Qt event loop and checks the dispatcher.

After building the helper, run from source with `PYTHONPATH=linux python3 -m momentum_control`; use `PYTHONPATH=linux python3 -m momentum_control.cli --help` for the CLI. Bluetooth operations require the user's normal desktop session. No root daemon is installed.
