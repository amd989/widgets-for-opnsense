# Widgets for OPNsense

Windows 11 native widget provider for monitoring an OPNsense router from the Widget Board (Win+W), plus the same widgets on KDE Plasma 6 and a GNOME Shell top bar indicator through a Linux daemon.

## Tech Stack

- **C# / .NET 8**: `net8.0` for Core and the Linux daemon, `net8.0-windows10.0.19041.0` for the Windows provider
- **Windows App SDK 1.8** (widget provider APIs, web content widgets, COM registration)
- **HTML/JS widget pages** (web content via `metadata.webUrl`), served from the exe
- **Adaptive Cards 1.5** (fallback for hosts without web content; SkiaSharp renders card images)
- **MSIX packaging** via WAP project (x64 + ARM64)
- **AdysTech.CredentialManager** for Windows Credential Manager (DPAPI-encrypted API keys)

## Architecture

Widget logic lives in `OPNsenseWidgets.Core` (`WidgetController`): view selection, page messages, actions, refresh. Each OS supplies an `IWidgetHost` that shows the result: the Windows provider (single MSIX, single `IWidgetProvider` COM server) or the Linux `PageHost` (WebSocket pages for Plasma). Adding a new widget (paths under `src/OPNsenseWidgets.Core/`):

1. Create `Widgets/<Name>/<Name>Widget.cs` implementing `IWebWidgetDefinition` (`WebPath`, `BuildWebData`)
2. Add the page at `Widgets/<Name>/Web/index.html` using `../../Shared/Web/widget.{css,js}` (embedded automatically)
3. Add fallback card templates under `Widgets/<Name>/Templates/`
4. Add `<Definition>` entry in `Package.appxmanifest` with `WebRequestFilter="https://opnsense-widget.test/*"`
5. Register in DI in both `Program.cs` files (Windows and Linux)
6. Add it to the `widgets` list in `linux/install.sh`
7. GNOME: add it to `DEFINITIONS` in `linux/gnome/daemon.js` and a menu section in `linux/gnome/indicator.js`
8. Re-render picker screenshots with `design/capture-gallery.ps1`

## Project Structure

```
src/
  OPNsenseWidgets.Core/         # Shared, OS-neutral (net8.0, RootNamespace OPNsenseWidgets so resource names stay put)
    Hosting/
      WidgetController.cs       # Widget state + behavior: GetView, OnPageMessage, HandleAction, RefreshAsync, PushWebData
      IWidgetHost.cs            # IWidgetHost (Update, SendToPage), IPlatformPaths, WidgetState/WidgetView
      WebAssets.cs              # Embedded Widgets/*/Web/* files by path
      WidgetInfo.cs             # Per-widget instance state (view, web ready, customization)
      TemplateHelper.cs         # Loads embedded JSON templates with caching
    Api/
      OPNsenseApiClient.cs      # HTTP client, Basic Auth, ISC/KEA auto-fallback
      Models/                   # DhcpLease, LeaseSearchResponse, InterfaceTraffic, FirewallRuleStat
    Configuration/
      ICredentialStore.cs       # Implemented per OS
      SettingsManager.cs        # JSON settings at ~/.opnsense-widgets/settings.json
      WidgetSettings.cs         # Settings model
    Widgets/
      IWidgetDefinition.cs      # Interface: DefinitionId, GetTemplate, FetchDataAsync, BuildDataJson
      IWebWidgetDefinition.cs   # Adds WebPath + BuildWebData (payload for the page)
      Shared/Web/               # widget.css/js (theme, protocol, both bridges, demo mode), settings.html, status.html
      DhcpLeases/               # DHCP leases (ISC + KEA), FirstSeenStore, KEA reservations
      Traffic/                  # Per-interface throughput, history kept in the app
      Firewall/                 # Rule evaluations, PfRuleLabel turns raw pf rules into readable names
        <each>/Web/index.html   # The widget page (demo data when opened in a normal browser)
        <each>/Templates/       # Small/Medium/Large fallback Adaptive Cards
    Templates/                  # Shared fallback cards (Configuration, Error, Unconfigured)
    Rendering/                  # SkiaSharp images for the fallback cards
    Refresh/
      RefreshService.cs         # 1 s timer; refreshes active widgets per interval, counted from the last attempt
  OPNsenseWidgets/              # Windows widget provider (C# console app)
    Program.cs                  # Entry point, COM server via CoRegisterClassObject
    Provider/
      OPNsenseWidgetProvider.cs # Board callbacks + IWidgetHost: cards, webUrl, SendMessageToContent
      WebContent.cs             # Serves WebAssets for https://opnsense-widget.test
      WidgetProviderFactory.cs  # IClassFactory COM factory, uses MarshalInspectable<T>
    Platform/                   # WindowsCredentialStore (OPNsenseWidgets/ApiCredentials), WindowsPaths
  OPNsenseWidgets.Package/      # MSIX packaging (WAP project)
    Package.appxmanifest        # COM server + widget extension declarations
  OPNsenseWidgets.Linux/        # opnsense-widgetsd (Kestrel, 127.0.0.1:47820)
    Program.cs                  # Endpoints: /widget redirect, /ws, page files; Host/Origin check
    PageHost.cs                 # IWidgetHost over WebSockets; view change = {type:"navigate"}
    Platform/                   # SecretServiceCredentialStore (secret-tool, mode-600 file fallback), LinuxPaths
linux/
  plasmoid/                     # Plasma 6 applet template (WebEngineView); install.sh stamps @...@ per widget
  gnome/                        # GNOME Shell 46+ extension opnsense-widgets@alejandro.md
    daemon.js                   # WidgetChannel: the page protocol over Soup 3 WebSockets (also used by prefs)
    indicator.js                # PanelMenu.Button: panel label, St menu sections, new-device notifications
    prefs.js                    # Adw preferences: connection (daemon settings actions), top bar options
  systemd/                      # opnsense-widgetsd user unit
  install.sh, uninstall.sh
```

## Build & Deploy

```bash
dotnet build src/OPNsenseWidgets/OPNsenseWidgets.csproj
```

Deploy via Visual Studio: right-click `OPNsenseWidgets.Package` > Deploy. Requires Developer Mode enabled on Windows.

Linux (KDE Plasma 6, GNOME 46+): `dotnet publish src/OPNsenseWidgets.Linux -c Release -r linux-x64 -o linux/dist`, copy `linux/` to the machine, run `linux/install.sh linux/dist/opnsense-widgetsd` (installs the Plasma widgets and/or the GNOME extension, whichever desktop is present). Log: `journalctl --user -u opnsense-widgetsd`. QML changes to widgets already on screen need `systemctl --user restart plasma-plasmashell`.

## KDE Plasma (Linux)

- **Same pages, same protocol**: the page talks to `PageHost` over `ws://127.0.0.1:<port>/ws?def=&id=&path=`; `widget.js` picks the WebView2 bridge or the WebSocket bridge.
- **View switches navigate**: the board swaps pages itself; on Linux the daemon sends `{type:"navigate", path}` and the page reloads from that path, keeping its query.
- **Page query**: `def` (DefinitionId), `id` (`kde-<Plasmoid.id>`), `host=kde` (12px top padding instead of the 44px title bar, size from viewport), `theme=dark|light` (Plasma theme; `widget.js` rewrites the light media rules because QtWebEngine may not report it).
- **Security**: loopback only, and requests must have Host (and Origin, when present) of `127.0.0.1:<port>` or `localhost:<port>`, which blocks DNS rebinding and other sites.
- **Active = page connected**: polling runs only while a page holds a socket, like "only while the board is open" on Windows.

## GNOME (Linux)

- **No web content in GNOME Shell**, so the extension draws the widgets natively (St) from the same `data` payloads. It connects to `/ws` like a page (`id=gnome-dhcp|gnome-traffic|gnome-firewall`) and reconnects with the new path on `navigate`.
- **Channels only while needed**: Traffic and Firewall poll every second, so they connect only while the menu is open (Traffic also when the panel shows throughput). DHCP stays connected for the count and the new-device notification (first reading after login is the baseline).
- **Preferences reuse the settings actions**: `prefs.js` connects as `gnome-prefs` on `Shared/Web/settings.html` without following `navigate`, sends `openSettings`, then `testConnection` (saves and tests), `importKeyFile`, and `cancelConfig` on close. A good save bumps the `config-saved` key so the indicator refreshes at once.
- **GNOME 46+ only** (ESM, `MessageTray.Notification({source,...})`); St.BoxLayout uses `orientation` from 48, `vertical` before.
- Updating an enabled extension on Wayland needs a log out and in. Log: `journalctl --user -b /usr/bin/gnome-shell`.

## Key Design Decisions

- **COM registration**: `CoRegisterClassObject` with `MarshalInspectable<T>.FromManaged()` — do NOT use `Marshal.GetComInterfaceForObject` (produces non-WinRT CCW that fails QI)
- **Do NOT set `WindowsPackageType=None`** in csproj — causes `MddBootstrapInitialize` crash in packaged apps
- **ISC/KEA auto-detect**: tries `/api/dhcpv4/leases/searchLease` first, falls back to `/api/kea/leases4/search` if 404 or 0 rows
- **KEA vs ISC field differences**: `DhcpLease` model has both sets of fields with `Effective*` properties that normalize (e.g., `EffectiveMac` returns `mac` or `hwaddr`, `EffectiveLeaseStart` computes from `starts` or `expire - valid_lifetime`)
- **Settings path**: `~/.opnsense-widgets/` (not `%LOCALAPPDATA%`) so settings survive MSIX reinstall. Credentials already survive via Windows Credential Manager.
- **Self-signed certs default ON** — home OPNsense installs nearly always use self-signed
- **EmptyEvent lifecycle**: process blocks on `ManualResetEventSlim`; only `DeleteWidget` sets it when no widgets remain
- **Widget canvas sizes** are fixed by Windows (small/medium/large) — templates must fit without scrolling

## Web Widgets

- **Sizes** (CSS px at dpr 1): small 302x148, medium 302x306, large 302x464. The board draws its title bar over the top 44px of the page (`--title-bar` in widget.css). The Customize dialog is 300x462.
- **Serving**: `webUrl` points at the made-up origin `https://opnsense-widget.test/<Name>/Web/index.html`; the manifest `WebRequestFilter` routes every request (including the page itself) to `OnResourceRequested`. URLs mirror the source tree so pages also open from disk for preview.
- **Response stream must be a native `InMemoryRandomAccessStream`** (with a deferral). A managed `MemoryStream.AsRandomAccessStream()` gives "There was an error loading."
- **Protocol** (see header of `Shared/Web/widget.js`): page posts `ready` (on load, every 3 s until data, after resize), `refresh`, `action {verb, data}`; provider sends `{type:"data", view, size, payload}`. Page actions use the same verbs as the card `Action.Execute` buttons (`HandleAction`).
- **Never call `SendMessageToContent` inside a board callback or before the page posted `ready`**: it deadlocks WidgetService. Always send from `Task.Run` (`SendToContent`). Recovery: `Stop-Process -Name WidgetService,WidgetBoard,OPNsenseWidgets`.
- **Push data, don't resend the template**: a changed template reloads the page. `UpdateWidget` only resets `WebReady` when the template changes.
- **Every state needs a page**: the board fails to load plain Adaptive Cards for a definition that uses web content. Settings, error and unconfigured states use `Shared/Web/settings.html` and `status.html`; the cards stay as fallback only.
- **The board reloads pages on every open**, so history (Traffic samples, Firewall rates, DHCP first-seen) lives in the app. Polling only runs while the board is open, so readings after a gap are skipped and charts break lines at gaps.
- **Customize opens a board dialog** (widget resized to large) that the provider cannot close. Save reports the result in place; the dialog closing shows up as `Deactivate`, which ends customization. The status page's Settings button edits in place instead.
- **Web widgets are EEA-only per Microsoft** (works on the dev machine in the US); keep fallback cards working.
- **Picker screenshots**: `ProviderAssets/<Name>_Screenshot[_Dark].scale-{100,200}.png` at 300x304, rendered by `design/capture-gallery.ps1` (serve repo root on port 8766 first). Headless Edge clamps narrow windows, so the script renders larger and crops.

## OPNsense Data Notes

- **KEA lease start is the last renewal** (`expire - valid_lifetime`), so it means "online", not "joined". "New" devices come from `FirstSeenStore` (`~/.opnsense-widgets/dhcp-first-seen.json`).
- **KEA leases don't flag reservations**: reserved status comes from `/api/kea/dhcpv4/searchReservation` (matched by MAC or IP, cached 60 s). Reserved devices without a current lease are not counted.
- **KEA returns some numeric fields as JSON numbers** (e.g. `valid_lifetime`); lease JSON is read leniently.
- **Firewall rule labels** are raw pf text when the rule has no description; `PfRuleLabel.Pretty` maps devices to interface names (vmx0 to WAN) and rules that read the same are merged.

## OPNsense API

- Auth: HTTP Basic — API key as username, API secret as password
- ISC DHCP: `GET /api/dhcpv4/leases/searchLease?current={page}&rowCount={count}`
- KEA DHCP: `GET /api/kea/leases4/search?current={page}&rowCount={count}`
- API key files from OPNsense are `key=...\nsecret=...` format — the Import Key button parses these from Downloads

## COM GUID

Provider CLSID: `D40B2E99-B044-4758-9AB5-212C01B368EE` (used in Program.cs, WidgetProviderFactory, and Package.appxmanifest — must match)

## Code Exploration Policy
Use `cymbal` CLI for code navigation — prefer it over Read, Grep, Glob, or Bash for code exploration.
- **New to a repo?**: `cymbal structure` — entry points, hotspots, central packages. Start here.
- **To understand a symbol**: `cymbal investigate <symbol>` — returns source, callers, impact, or members based on what the symbol is.
- **To understand multiple symbols**: `cymbal investigate Foo Bar Baz` — batch mode, one invocation.
- **To trace an execution path**: `cymbal trace <symbol>` — follows the call graph downward (what does X call, what do those call).
- **To assess change risk**: `cymbal impact <symbol>` — follows the call graph upward (what breaks if X changes).
- Before reading a file: `cymbal outline <file>` or `cymbal show <file:L1-L2>`
- Before searching: `cymbal search <query>` (symbols) or `cymbal search <query> --text` (grep)
- Before exploring structure: `cymbal ls` (tree) or `cymbal ls --stats` (overview)
- To disambiguate: `cymbal show path/to/file.go:SymbolName` or `cymbal investigate file.go:Symbol`
- The index auto-builds on first use — no manual indexing step needed. Queries auto-refresh incrementally.
- All commands support `--json` for structured output.