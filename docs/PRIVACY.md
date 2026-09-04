# Privacy

LanPilot is local-first. It does not require an account and does not send telemetry to the project author.

## Stored locally

- Device MAC address, last IPv4 address, custom name, type, group, and first/last seen times.
- Device and application limits, block state, schedules, and presets.
- Aggregated upload/download byte totals and configured retention settings.
- UI preferences such as theme and first-run completion.

## Not stored

- Packet payloads or file contents.
- Browsing history, websites, domains, or DNS queries.
- Credentials or communication contents.

Service data is stored under `%ProgramData%\LanPilot`; UI preferences are stored under `%LocalAppData%\LanPilot`. The uninstaller asks before deleting the shared service database.

Diagnostic exports can contain adapter names, local addresses, status, and rule metadata. Review an export before sharing it publicly.
