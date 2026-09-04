# Security policy

## Supported versions

LanPilot is currently a preview. Security fixes are provided for the newest published release only.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability. Use GitHub's **Report a vulnerability** option under the repository Security tab. Include the affected version, reproduction steps, impact, and any suggested mitigation.

Do not include packet captures, credentials, private IP inventories, device names, or other sensitive network data unless specifically requested through a private channel.

## Security boundaries

- The UI talks to the service through a local ACL-protected Named Pipe.
- No remote control HTTP API or listening TCP/UDP port is created.
- The service requires Administrator privileges because packet capture and Windows policy changes are privileged operations.
- LanPilot does not record packet payloads, websites, domains, or DNS queries.
