# Contributing

Contributions are welcome. A couple of ground rules keep the project clean, both technically and legally.

## Clean-room / interface-only

This project is a clean-room implementation. Please **do not** contribute any of the following:

- source code, binaries, firmware, audio, graphics or other assets from Sennheiser, Sonova or Qualcomm;
- content obtained by decrypting or otherwise circumventing a technical protection measure;
- enum blocks, tables or text copied verbatim from a manufacturer's software.

What is fine: independently observed **facts** — command identifiers, payload formats and value ranges — obtained
within the limits of § 69e UrhG / Directive 2009/24/EC, ideally confirmed against real hardware and documented with a
source and a test status.

## New commands

The app only ever sends commands from `Momentum4.Protocol/Catalog/CommandCatalog`, and only ones that were verified on
hardware. Anything that can take the headset offline (`0x06xx` unknowns, DFU, factory reset, deleting pairings) stays
`Blocked`. A test checks the command catalog against the captures under `tests/CapturedPackets/`.

By contributing, you confirm your contribution is your own work and that you may submit it under the project's MIT
license.
