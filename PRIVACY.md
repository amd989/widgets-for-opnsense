# Privacy policy

Effective November 6, 2026.

Widgets for OPNsense does not collect, send or sell any personal data. There is no account, no telemetry, no analytics and no advertising.

## What the app connects to

The app connects only to the OPNsense firewall whose address you enter, using its REST API and the API key you provide. It reads DHCP leases, interface traffic counters and firewall rule statistics so it can show them in the widgets. It makes no other network connections.

## What the app stores on your device

Everything stays on your own computer:

- **API key and secret:** stored in Windows Credential Manager (on Linux, in the Secret Service keyring, or in a file only your user can read if no keyring is available).
- **Settings:** your firewall address and widget options, in `~/.opnsense-widgets/settings.json`.
- **Device history:** when each device was first seen, so new devices can be marked, in `~/.opnsense-widgets/dhcp-first-seen.json`.
- **Log:** a small log for troubleshooting, in `~/.opnsense-widgets/opnsense-widgets.log` on Windows and in your user's system journal on Linux. It does not contain your API key.

Uninstalling the app removes the program. To remove the stored data as well, delete the `~/.opnsense-widgets` folder and the "OPNsenseWidgets/ApiCredentials" entry in Windows Credential Manager.

## Third parties

No data is shared with anyone. The app is not affiliated with Deciso B.V. or the OPNsense project, and does not send them anything.

## Contact

Questions about this policy: open an issue at https://github.com/amd989/widgets-for-opnsense/issues.
