# Crash diagnostics

LanPilot keeps application crash logs under `%LocalAppData%\LanPilot\Diagnostics`. The newest fatal exception is also copied to `latest-crash.txt`. These logs include exception types, stack traces, runtime details, and source line numbers when matching PDB files are present.

## Reproduce in Visual Studio

Run:

```powershell
.\tools\Run-Debug.ps1
```

Visual Studio opens the Debug executable. Press `F5`, open **Debug → Windows → Exception Settings**, and enable **Common Language Runtime Exceptions → Break on thrown**. Visual Studio will then stop at the original throw site instead of only showing the final crash.

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
