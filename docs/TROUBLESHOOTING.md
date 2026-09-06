# Troubleshooting

For crashes, exact stack traces, Debug builds, and optional native memory dumps, see [Crash diagnostics](DIAGNOSTICS.md).

## Service offline

LanPilot automatically attempts to restart and reconnect to its service. Approve the Windows Administrator prompt when using a development build. An installed build registers `LanPilotService` for delayed automatic start and configures restart recovery.

Reconnecting the UI is different from resuming traffic control. Version 0.1.2
keeps fault suspension across restarts; use **Start control** explicitly only
after reviewing the recovery result. Do not repeatedly restart a failing driver.

If it remains offline:

1. Restart LanPilot as Administrator.
2. Open `services.msc`, find **LanPilot Service**, and select **Start**.
3. Check Windows Event Viewer → Windows Logs → Application for **LanPilot Service** entries.

## Devices are missing

- Install the latest [Npcap](https://npcap.com/#download).
- Select the active Wi-Fi or Ethernet adapter and scan again.
- Confirm that the router does not enable AP/client isolation.
- The LanPilot `v0.1` preview series supports IPv4 networks up to `/24`; IPv6-only devices are not controlled.

## Device speed stays at zero

Remote-device traffic becomes visible while control/monitoring is active and the network permits forwarding. The local PC is measured directly. Verify the selected adapter and use a sustained transfer for several seconds.

## Internet connectivity changes unexpectedly

Use **Emergency pause** immediately. In 0.1.2 it suspends device and application
control, restores ARP mappings and removes only LanPilot-owned Windows policies.
Saved rules remain. **Exit** performs the same cleanup; minimizing to the tray
does not. A partial-restoration warning means success was not confirmed: export
diagnostics and retry recovery. If needed, reconnect Wi-Fi/Ethernet after stopping
control. Never remove unrelated Firewall/QoS rules. Share before/after diagnostics
privately, not as raw public issue attachments.

## Application readings unavailable

The application list can still work when passive traffic monitoring fails. An
unavailable-monitor warning means zero rates are not a reliable measurement; it
does not by itself prove an Internet outage. TCP identity backfill can take up to
five seconds. Unknown UDP flows and fragmented/unclassifiable traffic are not
assigned to a guessed executable.

## Installer refuses to continue

The 0.1.2 installer aborts if safe suspension, actual service stop (30-second
deadline), or owned-policy cleanup cannot be confirmed. Keep the installed
version and export diagnostics; do not manually overwrite live service files.
The prerelease's real installation/upgrade matrix still needs external testing.

## SmartScreen warning

The preview installer is not code-signed. Download it only from the official GitHub release, verify `SHA256SUMS.txt`, and inspect/build the source if you are uncertain.
