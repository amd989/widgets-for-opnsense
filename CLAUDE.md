# OPNsense Widgets

Windows 11 native widget provider for monitoring an OPNsense router from the Widget Board (Win+W).

## Tech Stack

- **C# / .NET 8** targeting `net8.0-windows10.0.19041.0`
- **Windows App SDK 1.6** (widget provider APIs, COM registration)
- **Adaptive Cards 1.5** (JSON templates rendered by the Widget Board)
- **MSIX packaging** via WAP project (x64 + ARM64)
- **AdysTech.CredentialManager** for Windows Credential Manager (DPAPI-encrypted API keys)

## Architecture

Single MSIX package, single `IWidgetProvider` COM server, multiple widget definitions. Adding a new widget:

1. Create `Widgets/<Name>/<Name>Widget.cs` implementing `IWidgetDefinition`
2. Add size templates under `Widgets/<Name>/Templates/`
3. Add `<Definition>` entry in `Package.appxmanifest`
4. Register in `OPNsenseWidgetProvider` constructor

## Project Structure

```
src/
  OPNsenseWidgets/              # Main widget provider (C# console app)
    Program.cs                  # Entry point, COM server via CoRegisterClassObject
    Provider/
      OPNsenseWidgetProvider.cs # IWidgetProvider + IWidgetProvider2, routes by DefinitionId
      WidgetProviderFactory.cs  # IClassFactory COM factory, uses MarshalInspectable<T>
      WidgetInfo.cs             # Per-widget instance state
      TemplateHelper.cs         # Loads embedded JSON templates with caching
    Api/
      OPNsenseApiClient.cs      # HTTP client, Basic Auth, ISC/KEA auto-fallback
      Models/                   # DhcpLease, LeaseSearchResponse
    Configuration/
      CredentialStore.cs        # Windows Credential Manager (target: OPNsenseWidgets/ApiCredentials)
      SettingsManager.cs        # JSON settings at ~/.opnsense-widgets/settings.json
      WidgetSettings.cs         # Settings model
    Widgets/
      IWidgetDefinition.cs      # Interface: DefinitionId, GetTemplate, FetchDataAsync, BuildDataJson
      DhcpLeases/               # DHCP leases widget (ISC + KEA)
        DhcpLeasesWidget.cs
        Templates/              # Small/Medium/Large Adaptive Card JSON
    Templates/                  # Shared templates (Configuration, Error, Unconfigured)
    Refresh/
      RefreshService.cs         # Timer-based background refresh for all active widgets
  OPNsenseWidgets.Package/      # MSIX packaging (WAP project)
    Package.appxmanifest        # COM server + widget extension declarations
```

## Build & Deploy

```bash
dotnet build src/OPNsenseWidgets/OPNsenseWidgets.csproj
```

Deploy via Visual Studio: right-click `OPNsenseWidgets.Package` > Deploy. Requires Developer Mode enabled on Windows.

## Key Design Decisions

- **COM registration**: `CoRegisterClassObject` with `MarshalInspectable<T>.FromManaged()` — do NOT use `Marshal.GetComInterfaceForObject` (produces non-WinRT CCW that fails QI)
- **Do NOT set `WindowsPackageType=None`** in csproj — causes `MddBootstrapInitialize` crash in packaged apps
- **ISC/KEA auto-detect**: tries `/api/dhcpv4/leases/searchLease` first, falls back to `/api/kea/leases4/search` if 404 or 0 rows
- **KEA vs ISC field differences**: `DhcpLease` model has both sets of fields with `Effective*` properties that normalize (e.g., `EffectiveMac` returns `mac` or `hwaddr`, `EffectiveLeaseStart` computes from `starts` or `expire - valid_lifetime`)
- **Settings path**: `~/.opnsense-widgets/` (not `%LOCALAPPDATA%`) so settings survive MSIX reinstall. Credentials already survive via Windows Credential Manager.
- **Self-signed certs default ON** — home OPNsense installs nearly always use self-signed
- **EmptyEvent lifecycle**: process blocks on `ManualResetEventSlim`; only `DeleteWidget` sets it when no widgets remain
- **Widget canvas sizes** are fixed by Windows (small/medium/large) — templates must fit without scrolling

## OPNsense API

- Auth: HTTP Basic — API key as username, API secret as password
- ISC DHCP: `GET /api/dhcpv4/leases/searchLease?current={page}&rowCount={count}`
- KEA DHCP: `GET /api/kea/leases4/search?current={page}&rowCount={count}`
- API key files from OPNsense are `key=...\nsecret=...` format — the Import Key button parses these from Downloads

## COM GUID

Provider CLSID: `D40B2E99-B044-4758-9AB5-212C01B368EE` (used in Program.cs, WidgetProviderFactory, and Package.appxmanifest — must match)
