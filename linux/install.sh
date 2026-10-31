#!/usr/bin/env bash
# Installs the OPNsense widgets for KDE Plasma 6 for the current user:
#   - opnsense-widgetsd   ~/.local/lib/opnsense-widgets/, run as a systemd user service
#   - three Plasma widgets (DHCP Leases, Traffic, Firewall) via kpackagetool6
#
# Usage: ./install.sh [path/to/opnsense-widgetsd]
#   The daemon binary defaults to ./opnsense-widgetsd next to this script. Build it with:
#   dotnet publish src/OPNsenseWidgets.Linux -c Release -r linux-x64 -o linux/dist
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
daemon=${1:-$here/opnsense-widgetsd}
lib_dir=$HOME/.local/lib/opnsense-widgets
unit_dir=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user

# id|definition|name|description|icon
widgets=(
  "md.alejandro.opnsensewidgets.dhcp|DhcpLeases_Widget|OPNsense DHCP Leases|Devices with DHCP leases on your OPNsense router, newest first|network-server"
  "md.alejandro.opnsensewidgets.traffic|Traffic_Widget|OPNsense Traffic|Live download and upload throughput of your OPNsense router|office-chart-area"
  "md.alejandro.opnsensewidgets.firewall|Firewall_Widget|OPNsense Firewall|Busiest firewall rules on your OPNsense router|security-high"
)

[[ -f "$daemon" ]] || { echo "Daemon binary not found: $daemon" >&2; exit 1; }
command -v kpackagetool6 >/dev/null || { echo "kpackagetool6 not found; KDE Plasma 6 is required." >&2; exit 1; }

# Runtime dependencies (Fedora package names; other distros name them differently).
missing=()
if ! ls /usr/lib*/qt6/qml/QtWebEngine/qmldir >/dev/null 2>&1; then missing+=("Qt WebEngine QML module (Fedora: qt6-qtwebengine)"); fi
if ! command -v secret-tool >/dev/null; then missing+=("secret-tool (Fedora: libsecret), else credentials go to a mode 600 file"); fi
if ((${#missing[@]})); then
  echo "Missing:"; printf '  - %s\n' "${missing[@]}"
fi

echo "Installing daemon to $lib_dir"
systemctl --user stop opnsense-widgetsd.service 2>/dev/null || true
install -Dm755 "$daemon" "$lib_dir/opnsense-widgetsd"
install -Dm644 "$here/systemd/opnsense-widgetsd.service" "$unit_dir/opnsense-widgetsd.service"
systemctl --user daemon-reload
systemctl --user enable --now opnsense-widgetsd.service

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

for widget in "${widgets[@]}"; do
  IFS='|' read -r id definition name description icon <<<"$widget"
  pkg=$stage/$id
  cp -r "$here/plasmoid" "$pkg"
  for file in "$pkg/metadata.json" "$pkg/contents/ui/main.qml"; do
    sed -i \
      -e "s|@PLUGIN_ID@|$id|g" \
      -e "s|@DEFINITION@|$definition|g" \
      -e "s|@NAME@|$name|g" \
      -e "s|@DESCRIPTION@|$description|g" \
      -e "s|@ICON@|$icon|g" \
      "$file"
  done

  if kpackagetool6 --type Plasma/Applet --show "$id" >/dev/null 2>&1; then
    kpackagetool6 --type Plasma/Applet --upgrade "$pkg" >/dev/null
    echo "Updated widget: $name"
  else
    kpackagetool6 --type Plasma/Applet --install "$pkg" >/dev/null
    echo "Installed widget: $name"
  fi
done

echo
echo "Done. Add the widgets from the desktop: right-click > Add or Manage Widgets > search \"OPNsense\"."
echo "Widgets already on screen pick up QML changes after: systemctl --user restart plasma-plasmashell"
