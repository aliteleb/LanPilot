## Summary

Describe the user-facing change and why it is needed.

## Verification

- [ ] `dotnet build LanPilot.slnx -c Release --no-restore`
- [ ] `dotnet test LanPilot.slnx -c Release --no-build --no-restore`
- [ ] Network safety and Emergency Pause behavior were considered.
- [ ] No packet payloads, credentials, databases, logs, or private network data are included.
- [ ] UI changes were checked in Light and Dark themes when applicable.
