# Renders the Microsoft Store listing screenshots (design/logo/store/Screenshot-*.png, 1920x1080)
# from design/store-screenshot.html in demo mode, using headless Edge. Serve the repo root first:
#   python -m http.server 8766 --bind 127.0.0.1
param([string]$Url = "http://127.0.0.1:8766/design/store-screenshot.html")

Add-Type -AssemblyName System.Drawing
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$out = Join-Path $PSScriptRoot "logo\store"
New-Item -ItemType Directory -Force $out | Out-Null
$profileDir = Join-Path $env:TEMP "opnsense-store-capture"
$raw = Join-Path $env:TEMP "opnsense-store-raw.png"

# preferredColorScheme: 0 = dark, 1 = light
foreach ($t in @(@{ Name = "Dark"; Scheme = 0 }, @{ Name = "Light"; Scheme = 1 })) {
    Remove-Item $raw -ErrorAction SilentlyContinue
    # Headless Edge clamps small windows, so render larger and crop 960x540 (x2) from the top-left.
    Start-Process $edge -Wait -WindowStyle Hidden -ArgumentList @(
        "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
        "--user-data-dir=$profileDir", "--window-size=1200,800", "--force-device-scale-factor=2",
        "--blink-settings=preferredColorScheme=$($t.Scheme)", "--virtual-time-budget=8000",
        "--screenshot=$raw", $Url)
    $file = Join-Path $out "Screenshot-$($t.Name).png"
    $src = [System.Drawing.Bitmap]::new($raw)
    try {
        $crop = $src.Clone([System.Drawing.Rectangle]::new(0, 0, 1920, 1080), $src.PixelFormat)
        $crop.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
        $crop.Dispose()
    }
    finally { $src.Dispose() }
    Write-Host $file
}
