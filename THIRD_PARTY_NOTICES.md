# Third-party notices

This project is an independent, clean-room implementation. It contains **no** source code, binaries, firmware, audio,
graphics or other assets from Sennheiser, Sonova, or Qualcomm.

## Protocol references

The GAIA/MOMENTUM 4 protocol knowledge was derived from publicly available reverse-engineering projects. Only
**facts** (command identifiers, payload formats, value ranges) were taken from them — no source code was copied. They
are credited here in gratitude and, where they are MIT-licensed, in keeping with the spirit of that license.

| Project | License |
|---|---|
| [Zhengyang-Liu/m4-companion](https://github.com/Zhengyang-Liu/m4-companion) | MIT |
| [DanSmith888/omarchy-momentum4](https://github.com/DanSmith888/omarchy-momentum4) | MIT |
| [jarek102/ohr](https://github.com/jarek102/ohr) | MIT |
| [arjun1194/momentum-control-macos](https://github.com/arjun1194/momentum-control-macos) | MIT |
| [SilentSoulsSr/SenheiserMomentum4](https://github.com/SilentSoulsSr/SenheiserMomentum4) | MIT |
| [gjabell/momentumctl](https://github.com/gjabell/momentumctl) | MIT |
| [zaval/sennheiser-desktop-client](https://github.com/zaval/sennheiser-desktop-client) | no stated license — only facts (e.g. command IDs) were used, no code or assets |

The MIT License grants permission to use, copy and modify the software provided the copyright and permission notice
are retained. This project did not copy those works; it re-implemented the interface from documented facts. The full
MIT text of each project is available in its own repository.

## Interoperability analysis

The official Android app *Sennheiser Smart Control Plus* (`com.sonova.chb.control`) was analyzed locally to obtain
interface information not covered by the projects above (§ 69e UrhG / Directive 2009/24/EC). Only interoperability
information was taken — command IDs, payload formats and value ranges. No program code, text, graphics, sounds or other
protected expression was copied, and no encrypted or otherwise protected content was decrypted. The app is not included
in, or redistributed by, this repository. See the **Legal** section of the [README](README.md).

## Runtime / build dependencies

The app builds on .NET, WinUI 3 / Windows App SDK and NuGet packages (CommunityToolkit.Mvvm and others). Their
licenses are those declared by the respective packages and are restored from NuGet at build time; they are not
vendored into this repository.
