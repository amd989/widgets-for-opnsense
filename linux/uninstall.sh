#!/usr/bin/env bash
# Removes what install.sh added. Settings in ~/.opnsense-widgets and the keyring entry stay;
# pass --purge to remove them too.
set -uo pipefail

for id in md.alejandro.opnsensewidgets.dhcp md.alejandro.opnsensewidgets.traffic md.alejandro.opnsensewidgets.firewall; do
  kpackagetool6 --type Plasma/Applet --remove "$id" >/dev/null 2>&1 && echo "Removed widget: $id"
done

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
