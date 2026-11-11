#!/usr/bin/env bash
# Lays out the system-wide install tree that the deb and the rpm both package:
#   /usr/lib/opnsense-widgets/opnsense-widgetsd        the daemon (self-contained publish)
#   /usr/lib/opnsense-widgets/user-managers            helper for the package scripts
#   /usr/lib/systemd/user/opnsense-widgetsd.service    user unit, enabled globally on install
#   /usr/share/plasma/plasmoids/<id>/                  the three KDE Plasma widgets
#   /usr/share/gnome-shell/extensions/<uuid>/          the GNOME Shell extension
#   /usr/share/glib-2.0/schemas/<schema>.gschema.xml   its settings; the distro trigger compiles it
#
# Usage: stage.sh DAEMON ROOT
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
linux=$(dirname "$here")
daemon=${1:?usage: stage.sh DAEMON ROOT}
root=${2:?usage: stage.sh DAEMON ROOT}
gnome_uuid=opnsense-widgets@alejandro.md

[[ -f "$daemon" ]] || { echo "Daemon binary not found: $daemon" >&2; exit 1; }
rm -rf "$root"
mkdir -p "$root"

install -Dm755 "$daemon" "$root/usr/lib/opnsense-widgets/opnsense-widgetsd"
install -Dm755 "$here/user-managers" "$root/usr/lib/opnsense-widgets/user-managers"

unit=$root/usr/lib/systemd/user/opnsense-widgetsd.service
install -Dm644 "$linux/systemd/opnsense-widgetsd.service" "$unit"
sed -i 's|^ExecStart=.*|ExecStart=/usr/lib/opnsense-widgets/opnsense-widgetsd|' "$unit"
grep -q '^ExecStart=/usr/lib/opnsense-widgets/opnsense-widgetsd$' "$unit"

bash "$linux/plasmoids.sh" "$root/usr/share/plasma/plasmoids" >/dev/null

ext=$root/usr/share/gnome-shell/extensions/$gnome_uuid
mkdir -p "$ext"
cp -r "$linux/gnome/." "$ext/"
# A system extension finds its schema in the system schema directory; no compiled copy here.
install -Dm644 -t "$root/usr/share/glib-2.0/schemas" "$ext"/schemas/*.gschema.xml
rm -rf "$ext/schemas"

install -Dm644 "$linux/../LICENSE" "$root/usr/share/doc/opnsense-widgets/LICENSE"

# Explicit modes, whatever the checkout reports (Windows mounts show every file as 755).
find "$root" -type d -exec chmod 755 {} +
find "$root" -type f -exec chmod 644 {} +
chmod 755 "$root/usr/lib/opnsense-widgets/opnsense-widgetsd" "$root/usr/lib/opnsense-widgets/user-managers"
