# Contributing to LanPilot

Thank you for helping improve LanPilot.

## Before opening a change

1. Search existing issues and discussions.
2. Open an issue for large behavioral or protocol changes.
3. Keep network safety fail-open: unexpected failures must restore normal connectivity.
4. Never add packet payload, website, domain, or DNS-content logging.
5. Do not bundle Npcap; users must obtain it under its own license.

## Development setup

- Windows 10 22H2 or Windows 11 x64
- .NET 10 SDK
- Npcap for capture/control testing
- Inno Setup 6 only when packaging

```powershell
dotnet restore LanPilot.slnx --configfile NuGet.Config
dotnet build LanPilot.slnx -c Release --no-restore
dotnet test LanPilot.slnx -c Release --no-build --no-restore
```

## Pull requests

- Keep changes focused and explain safety implications.
- Add tests for protocol, policy, persistence, and packet-path changes.
- Test Light/Dark UI states and keyboard interaction for visible changes.
- Never commit `artifacts/`, databases, logs, dumps, or captured traffic.
- Confirm that stopping control restores connectivity before submitting network-engine changes.

By contributing, you agree that your contribution is licensed under the MIT License.
