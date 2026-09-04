# Architecture

LanPilot separates the unprivileged desktop UI from privileged network operations.

## Components

```text
LanPilot.App (WPF)
    │  local ACL-protected Named Pipe
    ▼
LanPilot.Service (Windows service)
    ├── NetworkScanner / Npcap
    ├── TrafficEngine / packet forwarding
    ├── ApplicationTrafficController / WinDivert + Windows policies
    └── SqliteStore / %ProgramData%\LanPilot
```

`LanPilot.Contracts` owns the shared message and model contract. Pipe messages use versioned JSON with a four-byte little-endian length prefix and a 1 MB maximum frame size.

## Safety model

- Only the selected local IPv4 subnet is accepted, with a maximum `/24` scope.
- The gateway and the local computer cannot receive device block or shaping rules.
- Network control is disabled by default on unknown networks.
- Emergency pause and service shutdown restore ARP mappings before packet forwarding stops.
- An active-session journal supports corrective recovery after an interrupted engine process.
- Packet processing, persistence, and UI rendering run asynchronously.

## Device traffic

LanPilot discovers devices passively and actively. When a device is controlled, the service becomes the authorized forwarding path between that device and the gateway, counts traffic direction, applies independent rate policies, and forwards allowed Ethernet frames.

## Application traffic

The service associates local flows with executables, reports current per-application byte rates, applies outbound Windows QoS policies, uses WinDivert for inbound shaping, and creates program-specific Windows Firewall rules for blocking.

## Storage

SQLite stores device identity, policy, group, schedule, preset, settings, and aggregated usage information. Live one-second samples remain in memory; persisted usage is aggregated. See [Privacy](PRIVACY.md).
