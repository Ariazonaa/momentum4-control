# BTD700 control transport findings

Tested with a physical BTD700 running firmware 3.11.0 and the Sennheiser Dongle Control Windows application version 1.0.5. The distinction is between sending USB commands to the dongle and forwarding headset settings through its Bluetooth link.

## What works

The installed Linux adapter and shared C# core sent eight 64-byte USB HID requests on report `0x34`, usage page `0xFFA2`, and decoded the corresponding firmware, state, transport, supported-codec, active-codec and audio-quality responses. The hardware reported firmware 3.11.0, active streaming, aptX Adaptive and supported-codec mask 15. These are real device exchanges, not simulated responses.

The control message has a report identifier, request marker, command identifier, payload length and bounded payload. The examined application's commands configure or query dongle audio, codecs, broadcasting and connection state. Its reconnect operation takes a connection toggle, not a remote service UUID/channel or an arbitrary Bluetooth packet.

The current USB configuration exposes five interfaces: two HID interfaces and three USB audio interfaces. Linux binds `usbhid` and `snd-usb-audio`; it does not expose this configuration as an HCI controller that BlueZ can use to open a headset RFCOMM connection.

## What remains unverified

No headset-addressing, GAIA/RFCOMM forwarding, or ANC/EQ tunnel was found in the examined desktop application's command paths. Its other application HID path queries dongle application version and participates in maintenance. The firmware-update path uses a separate feature handshake and transfer/response reports; it is not evidence of a headset-control channel.

Additional vendor HID reports exist beyond those used by the examined application, including input/output reports `0x07`/`0x08` on the second HID interface. Their behavior has not been established. A descriptor proves that reports exist, not that they forward Bluetooth data. The distributed firmware's large partitions have high entropy and no useful GAIA/RFCOMM string markers in this inspection. That does not prove that forwarding code is absent or identify the encoding/protection format.

Consequently, an undocumented route cannot be ruled out. A usable Linux transport would require evidence of an existing forwarding protocol: destination binding, framing, response/error routing and a successful read-only headset query through USB while the direct PC Bluetooth control connection is absent. If stock firmware has no such handler, dongle firmware support would be necessary. Merely writing headset packets to an arbitrary HID report would not establish support.

No unknown opcodes, reset, pairing, broadcast credentials, mode-switch experiments or firmware flash were used in this investigation. The app retains its explicit known-command allowlist. BTD700 audio can run alongside a separate PC Bluetooth GAIA control connection to the headset.

## Sources and reproducibility

Only interface facts are recorded here; no manufacturer implementation, firmware or assets are included.

- [Sennheiser's description of Dongle Control](https://support.sennheiser-hearing.com/hc/en-jp/articles/34917447761181-Do-I-need-to-install-software-to-use-the-BTD-700) describes transmission settings and dongle firmware management.
- [DuroCodes/btd700](https://github.com/durocodes/btd700/blob/main/src/lib/btd700.ts) implements the same main WebHID command family.
- [sobalap/btd700ctl](https://github.com/sobalap/btd700ctl) provides another Linux implementation of dongle controls.
- Local inspection used the hardware's sysfs USB/HID descriptors, the supplied Windows application's HID send/receive call sites, and the official firmware container. The extracted application assembly was checked byte-for-byte against its enclosing executable.

Input executable SHA-256: `e176f1ab7d4aae40308c152b0bd95227ac5a16fedb99efef7e9229345d8014c0`.

Firmware 3.11.0 image SHA-256: `2152b04bcfe910a5e611bea3811109cce748b1c4b425ccfdfe7b9cfe74c3f2fe`.
