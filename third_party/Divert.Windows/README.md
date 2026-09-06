# Divert.Windows — LanPilot maintenance patch

Upstream: https://github.com/gdlol/Divert.Windows

Imported from commit `8e7b5ea2fd613a5684dc0fe97cfe3ff209041ddd`, the source
revision recorded in the Divert.Windows 3.0.0 NuGet package. Original source
and notices are retained. This directory is LGPL-3.0-only, **not** covered by
LanPilot's MIT license. See `COPYING.LESSER` and the accompanying `COPYING`.

## Changes by Ali Teleb / LanPilot, 2026-09-06

- `DivertService.GetVts` returns completed operations to the pool they were
  rented from. Upstream always used `sendVtsPool`, so receive-only sniffers
  retained one operation (including native completion resources) per packet.
- `IOCompletionOperation` releases cancellation/native-overlapped state before
  notifying continuations. Reuse must not race the previous completion's cleanup.
- `DivertValueTaskSource` unpins and clears packet/address buffers before
  publishing completion, and clears them on disposal.
- `DivertService.Shutdown` exposes the existing native shutdown operation while
  retaining the SafeHandle, allowing receive queues to stop accepting packets
  before outstanding I/O buffers are released.
- The project builds as a separate, replaceable DLL with the existing public
  API. WinDivert's signed native DLL/driver are unchanged. No reflection-based
  patching or periodic forced garbage collection is used in production.

The upstream test-only executor delay hook is not enabled in LanPilot builds.
Reflection is used only in LanPilot regression tests to inspect private pools.

## Build / replace

Requires the .NET 10 SDK. This project specifies its dependencies and can also
be built independently of the LanPilot solution:

```powershell
dotnet build Divert.Windows.csproj -c Release
```

The resulting DLL identifies itself as `3.0.0-lanpilot.2` (file version
`3.0.0.2`, assembly version `3.0.0.0`). The earlier memory-only hotfix was
`3.0.0-lanpilot.1`. To use a modified compatible build,
gracefully stop LanPilot's traffic control and service first, then replace
`Divert.Windows.dll` in the service directory. Do not modify the signed
`WinDivert64.sys` driver. The corresponding library source and license texts
are included under `third_party/Divert.Windows` in published service packages.

Regression tests: `tests/LanPilot.Tests/DivertMemoryTests.cs` in LanPilot.
They exercise 100,000 receive completions, separate send/receive pools,
cancellation reuse, buffer unpinning, and reentrant native completion cleanup
without a driver, administrator rights, or network traffic.
