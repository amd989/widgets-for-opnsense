#!/usr/bin/env bash
# Removes what install.sh added. Settings in ~/.opnsense-widgets and the keyring entry stay;
# pass --purge to remove them too.
set -uo pipefail

if command -v kpackagetool6 >/dev/null; then
  for id in md.alejandro.opnsensewidgets.dhcp md.alejandro.opnsensewidgets.traffic md.alejandro.opnsensewidgets.firewall; do
    kpackagetool6 --type Plasma/Applet --remove "$id" >/dev/null 2>&1 && echo "Removed widget: $id"
  done
fi

gnome_uuid=opnsense-widgets@alejandro.md
gnome_dir=${XDG_DATA_HOME:-$HOME/.local/share}/gnome-shell/extensions/$gnome_uuid
if [[ -d "$gnome_dir" ]]; then
  gnome-extensions disable "$gnome_uuid" 2>/dev/null
  rm -rf "$gnome_dir"
  echo "Removed GNOME extension: $gnome_uuid"
fi

systemctl --user disable --now opnsense-widgetsd.service 2>/dev/null
rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user/opnsense-widgetsd.service"
systemctl --user daemon-reload
rm -rf "$HOME/.local/lib/opnsense-widgets"
echo "Removed opnsense-widgetsd"

if [[ "${1:-}" == "--purge" ]]; then
  secret-tool clear service opnsense-widgets account api 2>/dev/null
  rm -rf "$HOME/.opnsense-widgets"
  echo "Removed settings and stored credentials"
fi
