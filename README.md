# OPNsense Widgets

Windows 11 Widget Board (Win+W) widgets for keeping an eye on an OPNsense router without opening its web UI.

The app is a native widget provider (C#, .NET 8, Windows App SDK) packaged as an MSIX. It talks to the OPNsense REST API and renders Adaptive Cards, with charts drawn by SkiaSharp.

## Widgets

Every widget comes in small, medium and large sizes. Pick the size from the widget's menu in the Widget Board.

### DHCP Leases

Shows the devices on your network, newest lease first, so you can see at a glance what just connected. Works with both ISC DHCP and KEA DHCP, detected automatically.

| Size   | Shows |
|--------|-------|
| Small  | Device count, number of new devices, one device card per page |
| Medium | Device count, new and active counts, 24h activity sparkline, 6 device cards per page |
| Large  | Same as medium plus reserved count, 10 device cards per page, MAC address on each card |

- Each card shows the hostname (or IP when there is no hostname), IP, vendor, and how long ago the lease started (`now`, `5m`, `3h`, `2d`).
- Cards are paged in a carousel, up to 5 pages.
- Status colors: **new** (under 5 minutes), **active** (under 1 hour), **reserved** (static mapping), **stale** (older).
- The sparkline is built from the widget's own refreshes, so it fills in over time while the widget runs.

### Traffic

Live throughput per interface. The WAN interface is detected by name (first interface with "WAN" in it, otherwise the first one).

| Size   | Shows |
|--------|-------|
| Small  | WAN download and upload speed with a small chart |
| Medium | WAN chart and LAN chart (LAN is the first interface with "LAN" in its name, otherwise the first non-WAN one) |
| Large  | Download and upload charts for every interface with a legend, current WAN speed, and total WAN bytes in and out |

Traffic samples every second and keeps the last 20 samples. Speeds are computed from byte counter deltas, so the first reading shows "Collecting data..." until there are two samples.

### Firewall

Top firewall rules by number of evaluations, as a donut chart. Rules outside the top N are folded into an "Other" slice.

| Size   | Shows |
|--------|-------|
| Small  | Donut plus the top 3 rules with their share |
| Medium | Donut, legend for the top 5 rules, total evaluations |
| Large  | Larger donut, two column legend for the top 10 rules, total evaluations and packets |

- Rule labels are resolved to the rule descriptions you set in OPNsense. Internal rules that only have a UUID with no matching rule are hidden.
- Only rules that apply to the interfaces the Traffic widget sees (or rules with no interface) are counted.
- Refreshes every second.

## Requirements

- Windows 11 with **Developer Mode** enabled (Settings > System > For developers), needed to deploy an unsigned MSIX
- Visual Studio 2026 with the **.NET desktop development** and **Windows App SDK** workloads
- .NET 8 SDK or later
- An OPNsense router reachable from your PC, and an API key for it (see [OPNsense API user](#opnsense-api-user))

## Build and deploy

Build the provider:

```bash
dotnet build src/OPNsenseWidgets/OPNsenseWidgets.csproj
```

Deploy the MSIX:

1. Open `OPNsenseWidgets.slnx` in Visual Studio.
2. Pick the platform (`x64` or `ARM64`).
3. Right-click **OPNsenseWidgets.Package** > **Deploy**.

The widgets then show up in the Widget Board under **OPNsense Widgets**.

## Setup

1. Press **Win+W**, click **+** (Add widgets) and add **DHCP Leases**, **Traffic** or **Firewall**.
2. A new widget shows a welcome card. Open the widget's **...** menu > **Customize**.
3. Fill in the form:

   | Field             | What to enter |
   |-------------------|---------------|
   | URL               | Router address, for example `https://192.168.1.1` |
   | Key               | API key |
   | Secret            | API secret (masked) |
   | Self-signed certs | Leave on unless your router has a certificate Windows trusts. On by default. |
   | Interval          | Refresh interval in seconds, 10 to 300 (default 30) |

4. Click **Test** to check the connection, then **Save**.

**Import Key:** instead of typing the key and secret, download the API key file from OPNsense and click **Import Key**. It reads the newest `*_apikey.txt` in your Downloads folder (format `key=...` and `secret=...` on separate lines) and stores the values.

Notes:

- All widgets share one router configuration. Configuring one configures them all.
- The Interval setting applies to the DHCP Leases widget. Traffic and Firewall always refresh every second.
- Widgets only refresh while they are visible in the Widget Board.
- **Test** always queries the DHCP lease API, so the API user needs the DHCP privilege for Test to pass, even when you are configuring Traffic or Firewall.

### Where things are stored

| What            | Where |
|-----------------|-------|
| API key/secret  | Windows Credential Manager, generic credential `OPNsenseWidgets/ApiCredentials` (encrypted by Windows) |
| Settings        | `%USERPROFILE%\.opnsense-widgets\settings.json` |
| Debug log       | `%USERPROFILE%\Desktop\opnsense-widget-debug.log` |

Settings and credentials live outside the MSIX package, so they survive reinstalling the app.

`settings.json` example:

```json
{
  "hostUrl": "https://192.168.1.1",
  "refreshIntervalSeconds": 30,
  "allowSelfSignedCertificates": true,
  "dhcpBackend": "auto"
}
```

`dhcpBackend` is not used yet. The backend is always auto-detected.

## OPNsense API user

Don't use the root account. Create a user that can only read what the widgets need:

1. **System > Access > Groups**: create a group, for example `API Widgets`.
2. Edit the group and add the privileges below under **Assigned Privileges**.
3. **System > Access > Users**: create a user (or edit one) and add it to the group.
4. In that user, create an API key. OPNsense downloads it as a `*_apikey.txt` file, which **Import Key** can read.

Privileges per widget:

| Widget      | Endpoints called | Privilege to assign |
|-------------|------------------|---------------------|
| DHCP Leases (KEA) | `/api/kea/leases4/search` | **Services: DHCP: Kea(v4)** (`page-dhcp-kea-v4`) |
| DHCP Leases (ISC) | `/api/dhcpv4/leases/searchLease` | Search "DHCP" in the privilege picker. Usually **Status: DHCP Leases** or **Services: ISC DHCPv4** |
| Traffic     | `/api/diagnostics/interface/getInterfaceStatistics` | **Diagnostics: Netstat** (`page-diagnostics-netstat`) |
| Firewall    | `/api/firewall/filter/searchRule` | **Firewall: Rules [new]** (`page-filter-api`) |
|             | `/api/diagnostics/firewall/pf_statistics/rules` (falls back to `pf_statistics?section=rules`) | **Diagnostics: Firewall statistics** (`page-diagnostics-pf-info`) |
|             | `/api/diagnostics/interface/getInterfaceStatistics` | **Diagnostics: Netstat** (`page-diagnostics-netstat`) |

Privilege names change between OPNsense versions. If a widget shows an authentication error, search the privilege picker for the area (DHCP, Netstat, Firewall) and check the endpoint above against its URL patterns.

DHCP backend detection: the widget tries the ISC endpoint first. If it returns 404 or zero leases, it falls back to KEA.

## Troubleshooting

Start with the debug log on your Desktop (`opnsense-widget-debug.log`). It records startup, configuration changes and every error with its full exception.

| Message in the widget | Likely cause |
|-----------------------|--------------|
| Authentication failed. Check your API key and secret. | Wrong key/secret, or the API user is missing a privilege (OPNsense returns 401/403 for both) |
| SSL certificate error. Enable 'Allow self-signed certificates' in settings. | Router uses a self-signed certificate and **Self-signed certs** is off |
| Cannot reach ... Check the URL and ensure OPNsense is running. | Wrong URL, router down, or blocked by a firewall |
| Request timed out. | Router busy or unreachable |
| DHCP lease API not available. | Neither ISC nor KEA returned leases. Check that a DHCP server is enabled and the user has the DHCP privilege |
| No *_apikey.txt found in Downloads. | Import Key found no key file in your Downloads folder |

Other tips:

- **Widget doesn't appear in Win+W:** confirm the Deploy step succeeded and that Developer Mode is on. Signing out and back in restarts the Widget Board.
- **Traffic or Firewall empty:** they need a couple of samples first. Firewall shows "No firewall activity yet..." until rules have evaluations.
- **Reset everything:** delete `%USERPROFILE%\.opnsense-widgets\` and remove `OPNsenseWidgets/ApiCredentials` in Credential Manager (Control Panel > Credential Manager > Windows Credentials).

## Adding a widget

1. Create `src/OPNsenseWidgets/Widgets/<Name>/<Name>Widget.cs` implementing `IWidgetDefinition`.
2. Add `SmallTemplate.json`, `MediumTemplate.json` and `LargeTemplate.json` under `Widgets/<Name>/Templates/`.
3. Add a `<Definition>` entry in `src/OPNsenseWidgets.Package/Package.appxmanifest`.
4. Register it in `Program.cs`: `services.AddSingleton<IWidgetDefinition, <Name>Widget>();`

Keep in mind:

- Widget canvas sizes are fixed by Windows and Adaptive Cards can't scroll. Everything must fit.
- The Widget Board already shows the widget name, so templates don't need a title.
- Set `RefreshIntervalSeconds` on the definition to override the global interval (Traffic and Firewall use 1).

## Project structure

```
src/
  OPNsenseWidgets/              Widget provider (COM server, console app)
    Program.cs                  Entry point, DI setup, COM registration
    Provider/                   IWidgetProvider implementation, COM factory, template loading
    Api/                        OPNsense HTTP client (Basic Auth) and response models
    Configuration/              Settings file and Credential Manager access
    Widgets/                    One folder per widget: definition class + Adaptive Card templates
    Templates/                  Shared templates: Configuration, Error, Unconfigured
    Rendering/                  SkiaSharp renderers: device cards, sparkline, traffic charts, donut
    Refresh/                    Background timer that refreshes visible widgets
    Logging/                    File logger for the debug log
  OPNsenseWidgets.Package/      MSIX packaging project (x64 + ARM64)
    Package.appxmanifest        COM server and widget definitions
```

## Tech stack

- C# / .NET 8 (`net8.0-windows10.0.19041.0`)
- Windows App SDK 1.8 (widget provider APIs)
- Adaptive Cards 1.5
- SkiaSharp for chart and card images
- AdysTech.CredentialManager for Windows Credential Manager
