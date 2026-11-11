#!/usr/bin/env bash
# Builds opnsense-widgets_<version>-1_<arch>.deb from a tree made by stage.sh. One deb per
# architecture serves every Debian and Ubuntu release (no +bookworm/+trixie suffix).
#
# Usage: build-deb.sh VERSION ARCH ROOT OUT_DIR    (ARCH: amd64 | arm64)
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
version=${1:?usage: build-deb.sh VERSION ARCH ROOT OUT_DIR}
arch=${2:?}
root=${3:?}
out=${4:?}

# 1.2.0-rc.1 sorts before 1.2.0 as 1.2.0~rc.1.
deb_version="${version//-/\~}-1"

pkg=$(mktemp -d)
trap 'rm -rf "$pkg"' EXIT
cp -a "$root/." "$pkg/"
mkdir -p "$pkg/DEBIAN"

size=$(du -sk --exclude=DEBIAN "$pkg" | cut -f1)
sed -e "s|@VERSION@|$deb_version|" -e "s|@ARCH@|$arch|" -e "s|@SIZE@|$size|" \
  "$here/deb/control" > "$pkg/DEBIAN/control"
install -m755 "$here/deb/postinst" "$here/deb/prerm" "$pkg/DEBIAN/"
(cd "$pkg" && find usr -type f -exec md5sum {} + | sort -k2) > "$pkg/DEBIAN/md5sums"

mkdir -p "$out"
file=$out/opnsense-widgets_${deb_version}_${arch}.deb
dpkg-deb --root-owner-group -Zxz --build "$pkg" "$file"
echo "$file"
