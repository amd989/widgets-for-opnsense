#!/usr/bin/env bash
# Builds opnsense-widgets-<version>-1.<dist>.<arch>.rpm from a tree made by stage.sh. Run it on
# EL9 (rockylinux:9) so older rpm versions can read and verify the package.
#
# Usage: build-rpm.sh VERSION ARCH ROOT OUT_DIR    (ARCH: x86_64 | aarch64)
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
version=${1:?usage: build-rpm.sh VERSION ARCH ROOT OUT_DIR}
arch=${2:?}
root=$(cd "${3:?}" && pwd)
out=${4:?}

# rpm versions cannot contain "-"; 1.2.0-rc.1 sorts before 1.2.0 as 1.2.0~rc.1.
rpm_version=${version//-/\~}

top=$(mktemp -d)
trap 'rm -rf "$top"' EXIT
rpmbuild -bb --quiet \
  --target "$arch" \
  --define "_topdir $top" \
  --define "_version $rpm_version" \
  --define "_stageroot $root" \
  "$here/rpm/opnsense-widgets.spec"

mkdir -p "$out"
cp "$top"/RPMS/*/*.rpm "$out/"
ls "$out"/*.rpm
