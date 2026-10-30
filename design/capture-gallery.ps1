# Renders the widget picker screenshots (ProviderAssets/*_Screenshot*.png) from the widget pages
# in demo mode, using headless Edge. Serve the repo root first:
#   python -m http.server 8766 --bind 127.0.0.1
param(
    [string]$BaseUrl = "http://127.0.0.1:8766/design/gallery-capture.html",
    [string]$Size = "medium",
    [int]$Width = 300,
    [int]$Height = 304
)

Add-Type -AssemblyName System.Drawing

$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$out = Join-Path $PSScriptRoot "..\src\OPNsenseWidgets.Package\ProviderAssets" | Resolve-Path
$profileDir = Join-Path $env:TEMP "opnsense-gallery-capture"
$raw = Join-Path $env:TEMP "opnsense-gallery-raw.png"

$widgets = @(
    @{ Id = "DhcpLeases"; Title = "DHCP Leases" },
    @{ Id = "Traffic"; Title = "Traffic" },
    @{ Id = "Firewall"; Title = "Firewall" }
)
# preferredColorScheme: 0 = dark, 1 = light. Light is the default screenshot.
$themes = @(
    @{ Suffix = ""; Scheme = 1 },
    @{ Suffix = "_Dark"; Scheme = 0 }
)
$scales = @(100, 200)

foreach ($w in $widgets) {
    foreach ($t in $themes) {
        foreach ($scale in $scales) {
            $title = [uri]::EscapeDataString($w.Title)
            $url = "${BaseUrl}?widget=$($w.Id)&title=$title&size=$Size&w=$Width&h=$Height"
            $file = Join-Path $out "$($w.Id)_Screenshot$($t.Suffix).scale-$scale.png"

            # Headless Edge clamps small windows, so render larger and crop the tile from the top-left.
            # msedge.exe returns before the capture finishes; -Wait keeps runs from sharing the profile.
            Remove-Item $raw -ErrorAction SilentlyContinue
            Start-Process $edge -Wait -WindowStyle Hidden -ArgumentList @(
                "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                "--user-data-dir=$profileDir",
                "--window-size=800,800",
                "--force-device-scale-factor=$($scale / 100)",
                "--blink-settings=preferredColorScheme=$($t.Scheme)",
                "--virtual-time-budget=5000",
                "--screenshot=$raw", $url)

            $factor = $scale / 100
            $rect = [System.Drawing.Rectangle]::new(0, 0, [int]($Width * $factor), [int]($Height * $factor))
            $src = [System.Drawing.Bitmap]::new($raw)
            try {
                $crop = $src.Clone($rect, $src.PixelFormat)
                $crop.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
                $crop.Dispose()
            }
            finally { $src.Dispose() }
            Write-Host $file
        }
    }
}
