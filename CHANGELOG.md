# Changelog

All notable changes to LanPilot are documented here. The project follows [Semantic Versioning](https://semver.org/).

## [0.1.3] - 2026-09-06 (prerelease)

### Fixed

- Device Start/Pause and row monitoring state no longer mistake application-policy activation for device capture. Devices now show control status, progress, and recovery actions directly; local-PC readings are also marked unmonitored when capture is stopped.
- Installed UI startup requests the Windows service instead of launching a second standalone host. A machine-wide lifetime lease rejects duplicate service instances before they can access shared rules or recovery state.
- Removed synthetic local ARP correction frames advertising remote Ethernet sources. Recovery now keeps the transmitting computer's Ethernet source separate from the genuine neighbor address in the ARP payload, preventing those frames from teaching a switch that remote MACs moved to the PC port.
- Cleanup no longer claims Internet connectivity was verified. This remains unverified without a separate connectivity check.

Validation: 75 automated tests pass and the Release solution builds. Native network recovery on ZXHN H188A has **not** been validated; do not repeat outage tests on the affected user's live network. These changes first ship in the 0.1.3 Setup. See [release notes](docs/releases/v0.1.3.md).

## [0.1.2] - 2026-09-06 (prerelease)

Real-network, hour-long soak and installer-matrix testing are delegated to the
maintainer's friend; they have not passed a local acceptance run. The reported
remote outage cause is not yet confirmed. See `docs/releases/v0.1.2.md`.

### Added

- Diagnostics v2: bounded five-second service health history, memory/GC/thread metrics, forwarding/drop/queue counters, worker liveness, policy snapshots, and structured command/error events.
- Three rotating 2 MiB service journals survive restarts; exports include loaded-library/build identity and best-effort read-only Windows Firewall/QoS/driver status with a ten-second timeout. No packet content, browsing destinations, executable paths, or exception arguments are added to the recorder.

### Fixed

- Unified device/application fail-open suspension, persisted manual resume after faults, and full cleanup on Exit without deleting saved policies.
- Atomic forwarding tables, source/destination identity checks, adapter-bound discovery, recovery journaling for newly redirected peers, and full-frame pacing at low rates.
- Bounded application packet memory, cancellable pacing/sends, shared flow identity/TCP backfill, monitor availability and bounded restart attempts.
- Serialized application policy changes with verification/rollback, bounded Windows commands, cancelled IPC request cleanup and coalesced client updates.
- Isolated release output directories and a safe installer/uninstaller maintenance path that verifies service shutdown and aborts on incomplete recovery.
- Service memory growth during application traffic monitoring: repaired Divert.Windows 3.0.0 receive-operation pooling instead of retaining an operation per packet.
- Released native completion state and pinned packet buffers before pooled operations can be reused, preventing completion/reuse races.
- Installed-service application discovery no longer drops every user application because service session 0 cannot see interactive window handles.
- Npcap detection now uses its driver service and native library paths and refreshes automatically after installation.

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
