#!/usr/bin/env bash
# Assembles the GitHub Pages site into _site/. Used by .github/workflows/pages.yml and for local preview:
#   bash site/build.sh && python -m http.server 8767 --directory _site
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/_site"
rm -rf "$out"
mkdir -p "$out/assets" "$out/widgets"

cp "$root/site/index.html" "$root/site/site.css" "$root/site/site.js" "$out/"

# The real widget pages. In a normal browser they run on demo data, and their relative paths
# (Widgets/<Name>/Web/index.html -> ../../Shared/Web/widget.js) are kept.
for w in Shared DhcpLeases Traffic Firewall; do
  mkdir -p "$out/widgets/$w"
  cp -R "$root/src/OPNsenseWidgets.Core/Widgets/$w/Web" "$out/widgets/$w/"
done

cp "$root/design/logo/widgets-for-opnsense.svg" "$out/assets/logo.svg"
cp "$root/design/logo/store/Screenshot-Dark.png" "$out/assets/"

# Plain static files, no Jekyll processing
touch "$out/.nojekyll"
echo "Built $out"
