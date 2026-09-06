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
- Emergency pause, Exit (not tray minimize), service shutdown and health failures use the same suspension path for device and application control. Saved policies are retained.
- Fault suspension is persisted in `control-safety.json` and requires explicit manual resume, including after a service restart. UI reconnect does not override it. Normal Exit permits saved application policies to resume on the next UI launch; device auto-control still requires a trusted network setting.
- Recovery steps are independent. ARP restoration sends, capture shutdown, application queue shutdown, Firewall removal and QoS removal are checked separately; failed cleanup is reported as partial. The legacy `RestorationComplete` field describes cleanup completion, not verified neighbor-table acceptance or restored Internet connectivity.
- 0.1.3 correction (2026-09-06): every ARP frame uses the local transmitter's Ethernet source. Recovery advertises the real neighbor only inside the ARP payload; synthetic inbound local-cache correction frames are removed. This avoids teaching the switch remote MACs on the PC port; actual router acceptance still needs isolated validation.
- The normal service host acquires an exclusive machine-wide `service-instance.lock` file handle before initializing shared state. Installed UI startup uses SCM via `sc.exe start`, with direct EXE startup limited to uninstalled development layouts. Maintenance acquires the same lease after stopping SCM. Old binaries without a lease must still be stopped before migration.
- An active-session journal supports corrective recovery after an interrupted engine process.
- The journal is extended before redirecting newly discovered peers, retaining previously redirected identities until restoration. A one-second health worker reacts to stopped forwarding, send timeout or three failures in a rolling five-second window, not to idle traffic.
- Packet processing, persistence, and UI rendering run asynchronously.

## Device traffic

LanPilot discovers devices passively and actively. When a device is controlled, the service becomes the authorized forwarding path between that device and the gateway, counts traffic direction, applies independent rate policies, and forwards allowed Ethernet frames.

The forwarding table is published as one complete snapshot. Local-PC packets are
counted but never reinjected; peer address/MAC and gateway direction must match.
Low-rate token buckets allow a full frame by accounting for debt rather than
permanently rejecting frames larger than their burst allowance. Network identity
changes suspend control; neighbor probes use the selected adapter's source IP.

## Application traffic

The service associates local flows with executables, reports current per-application byte rates, applies outbound Windows QoS policies, uses WinDivert for inbound shaping, and creates program-specific Windows Firewall rules for blocking.

Passive monitoring and shaping share an identity registry. Windows TCP tables
backfill existing connections every five seconds. Unknown connections, truncated
packets and unsupported/fragmented transport headers pass unclassified rather
than inheriting a guessed application policy. IPv6 extension walking is bounded.
Optional monitoring failure clears availability/rates and has at most three
delayed retries; it does not restart control.

Queued packet payloads (including the packet being paced/sent) are capped at
4 MiB per application and 32 MiB total. These are payload budgets, not a process
working-set limit: runtime, native capture queues and metadata are additional.
Send cancellation has a two-second deadline. Shutdown stops accepting new
WinDivert packets, drains briefly, then cancels outstanding I/O. A native I/O
that does not finish retains its resources and prevents reuse by a new session.

Application policy writes share the lifecycle gate. Windows changes are verified
before storage; failure rolls back the previous runtime policy, or suspends all
control if rollback fails. Windows child commands have ten-second deadlines,
bounded concurrent output readers and process-tree termination on cancellation.

The IPC client removes cancelled requests and discards partially written
connections. Each server connection has one bounded latest-event slot instead
of accumulating snapshot tasks. Slow clients can miss intermediate notifications;
the next snapshot contains current state. Emergency/Exit use a separate connection.

## Build and installer lifecycle

`build.ps1` writes a fresh `artifacts/releases/<version>-<guid>` directory and
checks every native command exit status. It never deletes a prior package.
The installer runs its own extracted maintenance payload, requests suspension,
waits up to 30 seconds for the real service state to become Stopped, and removes
only LanPilot-owned policies before replacing files. Failure aborts replacement.
Uninstall uses the same maintenance entry point before asking whether to retain
settings. See [0.1.2 release validation](releases/v0.1.2.md).

The managed WinDivert interface is built from the narrowly patched LGPL source
in `third_party/Divert.Windows`, as a separate replaceable DLL. Its operation
pools reuse receive/send resources and release native state before reuse. The
signed WinDivert driver is unchanged. See [memory diagnostics](DIAGNOSTICS.md#service-memory-growth)
for the regression tests and an isolated real-packet memory probe.

## Storage

SQLite stores device identity, policy, group, schedule, preset, settings, and aggregated usage information. Live one-second samples remain in memory; persisted usage is aggregated. See [Privacy](PRIVACY.md).
