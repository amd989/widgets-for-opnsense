#!/usr/bin/env bash
# Installs Widgets for OPNsense for the current user:
#   - opnsense-widgetsd   ~/.local/lib/opnsense-widgets/, run as a systemd user service
#   - KDE Plasma 6: three widgets (DHCP Leases, Traffic, Firewall) via kpackagetool6
#   - GNOME Shell 46+: a top bar indicator extension (opnsense-widgets@alejandro.md)
# Each desktop part is installed when that desktop is present.
#
# Usage: ./install.sh [path/to/opnsense-widgetsd]
#   The daemon binary defaults to ./opnsense-widgetsd next to this script. Build it with:
#   dotnet publish src/OPNsenseWidgets.Linux -c Release -r linux-x64 -o linux/dist
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
daemon=${1:-$here/opnsense-widgetsd}
lib_dir=$HOME/.local/lib/opnsense-widgets
unit_dir=${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user
gnome_uuid=opnsense-widgets@alejandro.md
gnome_dir=${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/$gnome_uuid

# id|definition|name|description|icon
widgets=(
  "md.alejandro.opnsensewidgets.dhcp|DhcpLeases_Widget|OPNsense DHCP Leases|Devices with DHCP leases on your OPNsense router, newest first|network-server"
  "md.alejandro.opnsensewidgets.traffic|Traffic_Widget|OPNsense Traffic|Live download and upload throughput of your OPNsense router|office-chart-area"
  "md.alejandro.opnsensewidgets.firewall|Firewall_Widget|OPNsense Firewall|Busiest firewall rules on your OPNsense router|security-high"
)

[[ -f "$daemon" ]] || { echo "Daemon binary not found: $daemon" >&2; exit 1; }

has_plasma=false; command -v kpackagetool6 >/dev/null && has_plasma=true
has_gnome=false; command -v gnome-shell >/dev/null && has_gnome=true
if ! $has_plasma && ! $has_gnome; then
  echo "Neither KDE Plasma 6 (kpackagetool6) nor GNOME Shell found." >&2
  exit 1
fi

# Runtime dependencies (Fedora package names; other distros name them differently).
missing=()
if $has_plasma && ! ls /usr/lib*/qt6/qml/QtWebEngine/qmldir >/dev/null 2>&1; then
  missing+=("Qt WebEngine QML module for the Plasma widgets (Fedora: qt6-qtwebengine)")
fi
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

install_plasma() {
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

  echo "KDE Plasma: add the widgets from the desktop: right-click > Add or Manage Widgets > search \"OPNsense\"."
  echo "  Widgets already on screen pick up QML changes after: systemctl --user restart plasma-plasmashell"
}

install_gnome() {
  local updated=false
  [[ -d "$gnome_dir" ]] && updated=true
  rm -rf "$gnome_dir"
  mkdir -p "$gnome_dir"
  cp -r "$here/gnome/." "$gnome_dir/"
  glib-compile-schemas "$gnome_dir/schemas"
  if $updated; then echo "Updated GNOME extension: $gnome_uuid"; else echo "Installed GNOME extension: $gnome_uuid"; fi

  # A running GNOME Shell only sees a new extension after logging out and in again (Wayland).
  if gnome-extensions enable "$gnome_uuid" 2>/dev/null; then
    echo "GNOME: enabled. Changes to an enabled extension load after logging out and in again."
  else
    echo "GNOME: log out and in again, then run: gnome-extensions enable $gnome_uuid"
  fi
  echo "  Set up the connection from the top bar button > Settings."
}

$has_plasma && install_plasma
$has_gnome && install_gnome
echo
echo "Done."
