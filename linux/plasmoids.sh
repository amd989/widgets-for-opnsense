#!/usr/bin/env bash
# Writes the three KDE Plasma widgets into OUT_DIR/<plugin id>/, stamped from the plasmoid/
# template. Used by install.sh (per user, through kpackagetool6) and by the deb/rpm packages
# (system wide, /usr/share/plasma/plasmoids).
#
# Usage: ./plasmoids.sh OUT_DIR
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
out=${1:?usage: plasmoids.sh OUT_DIR}

# id|definition|name|description|icon
widgets=(
  "md.alejandro.opnsensewidgets.dhcp|DhcpLeases_Widget|OPNsense DHCP Leases|Devices with DHCP leases on your OPNsense router, newest first|network-server"
  "md.alejandro.opnsensewidgets.traffic|Traffic_Widget|OPNsense Traffic|Live download and upload throughput of your OPNsense router|office-chart-area"
  "md.alejandro.opnsensewidgets.firewall|Firewall_Widget|OPNsense Firewall|Busiest firewall rules on your OPNsense router|security-high"
)

mkdir -p "$out"
for widget in "${widgets[@]}"; do
  IFS='|' read -r id definition name description icon <<<"$widget"
  pkg=$out/$id
  rm -rf "$pkg"
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
  echo "$id|$name"
done
