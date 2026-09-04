# Changelog

All notable changes to LanPilot are documented here. The project follows [Semantic Versioning](https://semver.org/).

## Unreleased

## [0.1.1] - 2026-09-04

### Added

- Local crash logs for unhandled WPF, process, and background-task exceptions.
- Lightweight Debug launcher without an IDE and opt-in Windows Error Reporting full dumps.
- Portable PDB files in installer builds for source line resolution.

### Fixed

- A page-unload race that could crash Overview while its application refresh was awaiting a response.
- The equivalent Applications-page lifecycle race that could leave a hidden refresh loop running.

## [0.1.0] - 2026-09-04

### Added

- Local IPv4 device discovery and live one-second throughput readings.
- Persistent device names, groups, guest defaults, overrides, schedules, and presets.
- Per-device download/upload limits and internet blocking with protected router/local-PC rows.
- Per-application throughput, download/upload policies, and blocking.
- Shared Overview, Devices, and Applications data with responsive virtualized tables.
- Windows service, local Named Pipe IPC, SQLite persistence, tray support, and automatic reconnect.
- Safe emergency pause, corrective ARP restoration, diagnostics, and JSON backup support.
- Light/dark interface, first-run authorization flow, custom dialogs, and About page.

### Known limitations

- IPv4 only; IPv6 traffic is not shaped.
- Networks using AP/client isolation or anti-ARP-spoofing controls may prevent device shaping.
- Npcap must be installed separately.
- The preview installer is unsigned.

[Unreleased]: https://github.com/aliteleb/LanPilot/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/aliteleb/LanPilot/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/aliteleb/LanPilot/releases/tag/v0.1.0
