# Widgets for OPNsense

Windows 11 Widget Board (Win+W) widgets for keeping an eye on an OPNsense router without opening its web UI. The same widgets also run on KDE Plasma 6 (see [KDE Plasma (Linux)](#kde-plasma-linux)), and on GNOME as a top bar button (see [GNOME (Linux)](#gnome-linux)).

The app is a native widget provider (C#, .NET 8, Windows App SDK) packaged as an MSIX. It talks to the OPNsense REST API and shows each widget as an HTML page served from the app itself. Hosts that can't show web content get Adaptive Cards instead, with charts drawn by SkiaSharp.

**Install:** [Microsoft Store](https://apps.microsoft.com/detail/9PD1Z0HV5L04) (Windows 11, x64 and ARM64). Project site with a live demo: https://amd989.github.io/widgets-for-opnsense/

> [OPNsense](https://opnsense.org)® is a registered trademark of Deciso B.V. This project is not affiliated with or endorsed by Deciso B.V. or the OPNsense project. The name is used with their permission.

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

The widgets then show up in the Widget Board under **Widgets for OPNsense**.

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

## KDE Plasma (Linux)

The same three widgets run on KDE Plasma 6. A small user service, `opnsense-widgetsd`, does what the Windows provider does: it polls OPNsense and serves the widget pages on `127.0.0.1:47820`. Each Plasma widget shows its page in an embedded browser.

Requirements: Plasma 6, Qt WebEngine for QML (Fedora: `qt6-qtwebengine`), and `secret-tool` (Fedora: `libsecret`) to keep the API key in KWallet or GNOME Keyring.

Build the daemon (from Windows or Linux, any machine with the .NET 8 SDK), then copy the `linux/` folder to the Linux machine and run the installer there:

```bash
dotnet publish src/OPNsenseWidgets.Linux -c Release -r linux-x64 -o linux/dist
```

```bash
./linux/install.sh linux/dist/opnsense-widgetsd
```

The installer puts the daemon in `~/.local/lib/opnsense-widgets/`, enables the `opnsense-widgetsd` systemd user service and installs three widgets: **OPNsense DHCP Leases**, **OPNsense Traffic** and **OPNsense Firewall**. Add them from the desktop with right-click > **Add or Manage Widgets**. On the desktop they show the widget itself; in a panel they show an icon that opens the widget.

Setup works like on Windows: a new widget shows **Connect your router**; click **Set up**. To change the connection later, right-click the widget > **OPNsense Connection**.

| What            | Where (Linux) |
|-----------------|---------------|
| API key/secret  | Desktop keyring through Secret Service (`secret-tool lookup service opnsense-widgets account api`). Without one, `~/.opnsense-widgets/credentials.json` (mode 600) |
| Settings        | `~/.opnsense-widgets/settings.json` |
| Log             | `journalctl --user -u opnsense-widgetsd` |

Notes:

- The service only answers on loopback and rejects requests addressed to other host names or coming from other sites, so web pages can't read your lease list through it.
- Widgets only refresh while a page is open (on the desktop, or a panel popup that is showing).
- To use another port, set `OPNSENSE_WIDGETS_PORT` in the service (`systemctl --user edit opnsense-widgetsd`) and the same port in each widget's settings.
- `./linux/uninstall.sh` removes the widgets and the service; `--purge` also removes settings and the stored key.

## GNOME (Linux)

GNOME has no desktop widgets, so on GNOME the widgets become a top bar button. It shows the device count (or WAN throughput, or just the icon) and a dot while new devices are around. Its menu has the newest devices, live traffic per interface and the busiest firewall rules. A notification pops up when a device joins the network for the first time.

It uses the same `opnsense-widgetsd` service as Plasma. Requirements: GNOME Shell 46 or newer and `secret-tool` (Fedora: `libsecret`). The same `install.sh` installs the GNOME Shell extension (`opnsense-widgets@alejandro.md`) when it finds GNOME Shell:

```bash
./linux/install.sh linux/dist/opnsense-widgetsd
```

Log out and in again so GNOME Shell sees the new extension, then enable it (the installer does this when it can):

```bash
gnome-extensions enable opnsense-widgets@alejandro.md
```

Set up the connection from the top bar button > **Settings…** (or the Extensions app). It has the same fields as the widgets' settings, including **Import Key from Downloads**. **Save and Test** saves and checks the connection in one step. The **Top Bar** page chooses what the button shows and turns notifications on or off.

Notes:

- Devices stay connected, so the count and notifications stay current. Traffic and Firewall only poll the router while the menu is open (Traffic also when the button shows throughput).
- Updating an enabled extension takes effect after logging out and in again.
- If the service uses another port, set the same port on the **Top Bar** page.

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

1. Create `src/OPNsenseWidgets.Core/Widgets/<Name>/<Name>Widget.cs` implementing `IWebWidgetDefinition`.
2. Add the page under `Widgets/<Name>/Web/index.html`, using `../../Shared/Web/widget.css` and `widget.js` (files under `Web/` are embedded automatically). Add `SmallTemplate.json`, `MediumTemplate.json` and `LargeTemplate.json` (the fallback cards) under `Widgets/<Name>/Templates/`.
3. Add a `<Definition>` entry in `src/OPNsenseWidgets.Package/Package.appxmanifest` with `WebRequestFilter="https://opnsense-widget.test/*"`, so the board asks the app for the page.
4. Register it in both `Program.cs` files (Windows and Linux): `services.AddSingleton<IWidgetDefinition, <Name>Widget>();`
5. Add a line to the `widgets` list in `linux/install.sh` for the Plasma widget.
6. For GNOME, add it to `DEFINITIONS` in `linux/gnome/daemon.js` and give it a section in `linux/gnome/indicator.js` (GNOME draws the menu natively from the same payload).
7. Re-render the widget picker screenshots with `design/capture-gallery.ps1` (serve the repo root on port 8766 first).

Keep in mind:

- Widget sizes are fixed by Windows (small 302x148, medium 302x306, large 302x464) and nothing scrolls. Everything must fit, in both the page and the fallback cards.
- The Widget Board already shows the widget name, so templates don't need a title.
- Set `RefreshIntervalSeconds` on the definition to override the global interval (Traffic and Firewall use 1).

## Project structure

```
src/
  OPNsenseWidgets.Core/         Shared by Windows and Linux (net8.0)
    Hosting/                    WidgetController (views, page messages, actions, refresh), IWidgetHost, WebAssets
    Api/                        OPNsense HTTP client (Basic Auth) and response models
    Configuration/              Settings file, credential store interface
    Widgets/                    One folder per widget: definition class, web page, fallback Adaptive Cards
    Templates/                  Shared fallback cards: Configuration, Error, Unconfigured
    Rendering/                  SkiaSharp renderers for the fallback cards
    Refresh/                    Background timer that refreshes visible widgets
    Logging/                    File logger for the debug log
  OPNsenseWidgets/              Windows widget provider (COM server, console app)
    Program.cs                  Entry point, DI setup, COM registration
    Provider/                   IWidgetProvider + IWidgetHost for the Widget Board, COM factory
    Platform/                   Credential Manager store, Downloads folder
  OPNsenseWidgets.Package/      MSIX packaging project (x64 + ARM64)
    Package.appxmanifest        COM server and widget definitions
  OPNsenseWidgets.Linux/        opnsense-widgetsd: Kestrel on 127.0.0.1, pages + WebSocket (PageHost)
linux/
  plasmoid/                     Plasma 6 widget template (install.sh stamps one per widget)
  gnome/                        GNOME Shell extension: top bar indicator, preferences, schema
  systemd/                      User service for opnsense-widgetsd
  install.sh, uninstall.sh
```

## Tech stack

- C# / .NET 8 (`net8.0-windows10.0.19041.0` for Windows, `net8.0` for Core and the Linux daemon)
- Windows App SDK 1.8 (widget provider APIs, web content widgets)
- HTML/CSS/JS widget pages, embedded in the app
- Adaptive Cards 1.5 as the fallback
- SkiaSharp for the fallback cards' chart and card images
- AdysTech.CredentialManager for Windows Credential Manager
- Linux: ASP.NET Core (Kestrel, WebSockets), Plasma 6 QML with Qt WebEngine, GNOME Shell extension (GJS, St, libsoup 3, libadwaita preferences), libsecret `secret-tool`

## License

MIT. See [LICENSE](LICENSE).

[OPNsense](https://opnsense.org)® is a registered trademark of Deciso B.V. This project is not affiliated with or endorsed by Deciso B.V. or the OPNsense project. The name is used with their permission.

Privacy: see [PRIVACY.md](PRIVACY.md). The widgets only talk to your own firewall.
