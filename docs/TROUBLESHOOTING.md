# Troubleshooting

For crashes, exact stack traces, Debug builds, and optional native memory dumps, see [Crash diagnostics](DIAGNOSTICS.md).

## Service offline

LanPilot automatically attempts to restart and reconnect to its service. Approve the Windows Administrator prompt when using a development build. An installed build registers `LanPilotService` for delayed automatic start and configures restart recovery.

If it remains offline:

1. Restart LanPilot as Administrator.
2. Open `services.msc`, find **LanPilot Service**, and select **Start**.
3. Check Windows Event Viewer → Windows Logs → Application for **LanPilot Service** entries.

## Devices are missing

- Install the latest [Npcap](https://npcap.com/#download).
- Select the active Wi-Fi or Ethernet adapter and scan again.
- Confirm that the router does not enable AP/client isolation.
- LanPilot `v0.1.0` supports IPv4 networks up to `/24`; IPv6-only devices are not controlled.

## Device speed stays at zero

Remote-device traffic becomes visible while control/monitoring is active and the network permits forwarding. The local PC is measured directly. Verify the selected adapter and use a sustained transfer for several seconds.

## Internet connectivity changes unexpectedly

Use **Emergency pause** immediately. It stops control and sends corrective ARP replies. If necessary, stop the LanPilot Service and reconnect Wi-Fi/Ethernet. Include a diagnostics export when filing a private or sanitized report.

## SmartScreen warning

The preview installer is not code-signed. Download it only from the official GitHub release, verify `SHA256SUMS.txt`, and inspect/build the source if you are uncertain.
