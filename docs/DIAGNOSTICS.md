# Diagnostics

## Intermittent loss of Internet (bundle format 2)

Use Settings → **Export diagnostics while the outage is happening**, before
pausing control or exiting. Note the local time, whether it affects the PC or
other LAN devices, the application version, and whether Emergency Pause restores
connectivity. Export a second ZIP after recovery. In 0.1.2, Emergency Pause also
suspends application shaping and removes LanPilot-owned Firewall/QoS policies.
Do not delay emergency recovery to wait for an export if connectivity is critical.

The updated **service** must be running: replacing only the desktop executable
does not enable collection. A v1 ZIP / published v0.1.1 does not have this history.

The ZIP contains:

- `diagnostics.json`: format/build ID, service/runtime/native-library versions,
  current process memory/GC/CPU/threads/handles, network interface state and error
  counters, selected adapter, tick heartbeat, forwarding state, application
  observer/limiter task status, effective policies, and schedules.
- The coordinator's `safety` state records suspension reason, UTC update time,
  manual-resume requirement, application control state and restoration result.
  `FailureReason` identifies a health-triggered suspension. A false restoration
  result means controls may remain and must not be read as successful recovery.
- `flight-recorder.json`: up to 360 samples at five-second intervals (about
  30 minutes) and 1,000 recent structured events. Packet counters are cumulative
  per service session; last receive/send ages help distinguish idle traffic from
  a stalled forwarding path. CPU time is cumulative, not a percentage. Tick
  values use the same monotonic clock as `currentTick`.
- `history/service-{0,1,2}.jsonl`: newest-first rolling files, each at most
  2 MiB, under `%ProgramData%\LanPilot\Diagnostics`. These survive service
  restarts; session IDs separate runs. Flushes occur every five seconds, so an
  abrupt crash can lose the most recent seconds. Retention depends on event
  volume, not a guaranteed number of days.
- `windows-policies.json`: read-only status of relevant Windows services/drivers
  and LanPilot-owned Firewall/QoS rules, Firewall profile defaults, default
  routes, and cached gateway neighbor mappings (to investigate ARP/VPN routing).
  A hidden PowerShell query runs only on
  export, with a ten-second timeout and bounded output. Permission/query errors
  or timeout are reported as unavailable; they do not prove no rules exist.
- `README.txt`: collection, interpretation, and privacy notes.

Device `limited` counts rate-limit drops; application `limited` counts packets
sent to pacing queues, and `queueFull` counts dropped queue submissions. Queue
depth excludes the packet currently being paced/sent; `queuedBytes` includes it
and is the value to compare with the memory budget. `errors` count processing
or reinjection failures, not just intentional policy drops. An empty/quiet link
is not by itself evidence of a fault. Task status `RanToCompletion` while other
loops remain active can reveal an observer that stopped after a caught error.

This recorder does not force garbage collections, send connectivity probes,
change policies, or restart drivers. Packet callbacks update fixed-size atomic
counters only. Background sampling is independent of the coordinator's tick so
a delayed tick remains visible. Recent policy lists are capped at 32; the full
export caps device/application policies and schedules at 512. Queue details are
capped at 128, network interfaces at 32, and Windows rules at 128 per kind.
Samples over 32 KiB are replaced by an explicit omission entry. Events have
bounded text, repeated adjacent errors are coalesced for five seconds, and disk
backlog is capped at 256 entries. Omission/coalescing counts and storage failures
are included. Disk failure leaves in-memory export available.

Logs retain message templates, exception types/HResults and method-only stacks,
not formatted arguments, exception messages, source paths, packet payloads,
remote flow addresses, DNS queries, or browsing content. The ZIP still contains
local network/adapter identifiers, configured policy IDs and local network names;
**share it privately**, not in a public issue. Existing desktop crash logs and
memory dumps remain separate and are not automatically bundled.

## Crash diagnostics

LanPilot keeps application crash logs under `%LocalAppData%\LanPilot\Diagnostics`. The newest fatal exception is also copied to `latest-crash.txt`. These logs include exception types, stack traces, runtime details, and source line numbers when matching PDB files are present.

## Reproduce without an IDE

Run:

```powershell
.\tools\Run-Debug.ps1
```

The script closes an existing LanPilot desktop process, builds with Debug symbols, and starts the Debug executable directly. No IDE is opened. Reproduce the crash normally, then inspect `%LocalAppData%\LanPilot\Diagnostics\latest-crash.txt` and the newest session log.

## Capture native crashes

Managed exception logging cannot catch every native driver or access-violation crash. Enable Windows Error Reporting full dumps for the desktop app with:

```powershell
.\tools\Enable-CrashDumps.ps1
```

To include the Windows service, run an elevated PowerShell window and use:

```powershell
.\tools\Enable-CrashDumps.ps1 -IncludeService
```

Disable capture later with `Disable-CrashDumps.ps1`. Full dump files can contain private in-memory data such as paths and current application content. Do not attach raw dumps to a public GitHub issue; share them privately or inspect them locally in Visual Studio.

## What to collect

- The exact action that triggered the crash.
- `latest-crash.txt`, if it exists.
- The matching session log from the same time.
- A `.dmp` file for native crashes.
- Windows Event Viewer entries for `LanPilot Service` if the service stopped.

## Service memory growth

The receive-only application monitor exposed a pooling defect in Divert.Windows
3.0.0: every received packet created an operation that was returned to the send
pool, which the monitor never consumed. A small LGPL-licensed source patch now
lives in `third_party/Divert.Windows`. Do not replace it with the unpatched NuGet
package. Its README records the upstream revision and the exact local fixes.

`DivertMemoryTests` runs without administrator rights or a network driver and
checks 100,000 receive completions, pool separation, cancellation reuse, and
native/pinned-buffer cleanup before a continuation can reuse the operation.

For an optional real-packet check, run from the repository root in an elevated
PowerShell window (no IDE needed):

```powershell
dotnet run --project tools/LanPilot.MemoryProbe -c Release -- artifacts/memory-probe.json
dotnet run --project tools/LanPilot.MemoryProbe -c Release -- artifacts/memory-probe-forward.json --forward
```

The probe sends 2,000 warmup and 100,000 measured UDP packets to its own loopback
port. It never captures user application/LAN traffic or changes policy. The
forward mode reinjects only the probe's packets. Garbage collections are forced
**only in the isolated probe** to measure retained objects; the production
service does not force collections or periodically restart itself.

For comparison, build the probe with `-p:UseUpstreamDivert=true` and a separate
`--artifacts-path` to use the original 3.0.0 package. On 2026-09-06, the old
receive-only probe retained an additional 43,673,512 bytes per 100,000 packets;
the patched probe retained 11,464 bytes (7,056 bytes in forward mode).
These are controlled-test results, not a fixed memory budget for every network.

After an update, verify the running service actually loaded file version
`3.0.0.2` of `Divert.Windows.dll` for the full 0.1.2 lifecycle update (the earlier
memory-only hotfix used `3.0.0.1`); a rebuilt desktop app alone does not update an
already-running service. Restore peer network state with Emergency Pause before
stopping/replacing a service that has active traffic control. Keep a backup of
the old DLL. Do not stop the Npcap driver or replace WinDivert's signed driver to
apply this managed-library fix.

## 0.1.2 validation and friend testing

Automated tests use synthetic packets, a fake Windows policy boundary and
test-owned temporary databases. They do not manipulate the current LAN or firewall.
They cover low-rate full-frame pacing, memory-budget concurrency, IPv4/IPv6
parsing, identity backfill, database failure/rollback, pause during a policy
operation, partial restoration, and 100 simulated control cycles.

The one-hour real-traffic soak, real driver lifecycle cycles, Ethernet/Wi-Fi/VPN
switching, and clean-install/0.1.1-upgrade/uninstall matrix were **not executed**
locally. On 2026-09-06 the maintainer requested that the friend test the prerelease
instead; this is not evidence those acceptance checks passed. Export before/after
ZIPs when practical and report whether the PC alone or other devices lost access.
The original friend's outage cause is still unconfirmed.
