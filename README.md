<div align="center">
  <img src="docs/assets/lanpilot-logo.png" width="150" alt="LanPilot logo">
  <h1>LanPilot</h1>
  <p>Open-source network and application bandwidth control for Windows.</p>

  [![Build](https://github.com/aliteleb/LanPilot/actions/workflows/build.yml/badge.svg)](https://github.com/aliteleb/LanPilot/actions/workflows/build.yml)
  [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
  [![Release](https://img.shields.io/github/v/release/aliteleb/LanPilot?include_prereleases)](https://github.com/aliteleb/LanPilot/releases)
</div>

> [!IMPORTANT]
> Use LanPilot only on networks you own or are explicitly authorized to administer. The first release is a preview and should be tested on a non-critical network before regular use.

## What LanPilot does

LanPilot is a native Windows desktop application for seeing devices on a local IPv4 network, monitoring live traffic, and applying download, upload, or blocking policies. It also provides per-application controls for software running on the local computer.

- Live discovery for home and small-office networks up to `/24`.
- Persistent device names, groups, guest defaults, and device overrides.
- Per-device download/upload limits and internet blocking.
- Per-application live throughput, independent limits, and blocking.
- Schedules and reusable rule presets.
- One-second UI updates with shared readings across screens.
- Safe pause, corrective ARP restoration, tray operation, and automatic reconnect.
- Local-only SQLite storage; no cloud account or remote HTTP API.

## Requirements

- Windows 10 22H2 or Windows 11, x64.
- Administrator permission for the LanPilot service and network controls.
- [Npcap](https://npcap.com/#download) installed separately for full device discovery and control. Npcap is not included in the installer.
- An IPv4 network with prefix `/24` through `/30`.

IPv6 traffic can be detected but is not shaped in `v0.1.0`. Networks using client/AP isolation, protected switches, or ARP-spoofing defenses may prevent device control.

## Install

1. Download `LanPilot-Setup-0.1.0.exe` and `SHA256SUMS.txt` from the [v0.1.0 preview release](https://github.com/aliteleb/LanPilot/releases/tag/v0.1.0).
2. Verify the SHA-256 checksum.
3. Install [Npcap](https://npcap.com/#download) if it is not already installed.
4. Run the installer as Administrator.
5. Confirm that you own or are authorized to manage the selected network.

The preview installer is not digitally signed, so Microsoft SmartScreen may display a warning. Inspect the source and checksum before running it.

## Architecture

| Project | Responsibility |
| --- | --- |
| `LanPilot.App` | WPF/WPF-UI desktop interface, tray integration, and local client. |
| `LanPilot.Service` | Elevated Windows service for capture, forwarding, policies, and storage. |
| `LanPilot.Contracts` | Versioned JSON-over-Named-Pipe contracts. |
| `LanPilot.Tests` | Network math, policy, persistence, packet, and IPC tests. |

The UI and service communicate through the ACL-protected local pipe `LanPilot.Control.v1`; LanPilot opens no HTTP server or network control port. See [Architecture](docs/ARCHITECTURE.md) for details.

## Privacy

LanPilot stores device identities, user-defined policies, and aggregated byte totals under `%ProgramData%\LanPilot`. UI preferences are stored under `%LocalAppData%\LanPilot`. Packet payloads, websites, domains, and DNS queries are not recorded. See [Privacy](docs/PRIVACY.md).

## Build from source

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run:

```powershell
.\build.ps1
```

This restores packages, builds with warnings as errors, runs tests, publishes self-contained untrimmed `win-x64` output, creates the installer, and generates its SHA-256 checksum under `artifacts\`.

To build without the installer:

```powershell
.\build.ps1 -SkipInstaller
```

## Contributing and support

- Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes.
- Use [GitHub Issues](https://github.com/aliteleb/LanPilot/issues) for reproducible bugs and feature requests.
- Report security issues according to [SECURITY.md](SECURITY.md), not in a public issue.
- Third-party licenses are listed in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

## License

Copyright © 2026 [Ali Teleb](https://github.com/aliteleb).

LanPilot is licensed under the [MIT License](LICENSE). SelfishNet source code and assets are not included; LanPilot is an independent clean-room implementation inspired only by its user-visible purpose.
