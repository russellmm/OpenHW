#!/usr/bin/env bash
# Builds dist/openhw_<version>_amd64.deb: the app and openhw-helper, installed system-wide. No root needed to build.
#   /usr/lib/openhw/app/          the published app            /usr/bin/openhw         command on PATH
#   /usr/lib/openhw/helper/       the published helper         openhw-helper.service   systemd unit (enabled on install)
#   menu entry + icon             "OpenHW" in the GNOME app grid
#   /usr/share/gnome-shell/extensions/openhw-tray-hover@…   hover details for the tray icons (each user turns it
#                                 on in File → Tray Icons; GNOME finds it at the next login)
# Install with:  sudo apt install ./dist/openhw_<version>_amd64.deb
# The version comes from Directory.Build.props; a commit other than the release tag gets "+git.<hash>" appended, so
# test packages sort after the release they follow. Uses the system .NET 8 runtime (dotnet-runtime-8.0).
set -euo pipefail

if [[ $EUID -eq 0 ]]; then
    echo "Run as your normal user (no sudo): packaging/build-deb.sh" >&2
    exit 1
fi

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"

# The app shows the git revision; a package built from uncommitted edits would say "-dirty" forever.
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then # untracked files leave describe clean
    echo "Commit first: the working tree has uncommitted changes." >&2
    exit 1
fi

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"
if [[ "$(git describe --exact-match --tags HEAD 2>/dev/null || true)" != "v$version" ]]; then
    version="$version+git.$(git rev-parse --short=7 HEAD)"
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
root="$work/root"

dotnet publish src/OpenHW.App/OpenHW.App.csproj -c Release -r linux-x64 --self-contained false \
    -o "$root/usr/lib/openhw/app" --nologo -v quiet
dotnet publish src/OpenHW.Helper/OpenHW.Helper.csproj -c Release -r linux-x64 --self-contained false \
    -o "$root/usr/lib/openhw/helper" --nologo -v quiet

install -d "$root/usr/bin" "$root/usr/lib/systemd/system" "$root/usr/share/applications" \
    "$root/usr/share/icons/hicolor/scalable/apps" "$root/usr/share/doc/openhw" "$root/usr/share/gnome-shell/extensions" \
    "$root/DEBIAN"
ln -s ../lib/openhw/app/openhw "$root/usr/bin/openhw"
sed 's:^ExecStart=.*:ExecStart=/usr/lib/openhw/helper/openhw-helper:' packaging/openhw-helper.service \
    > "$root/usr/lib/systemd/system/openhw-helper.service"
install -m 644 packaging/openhw.svg "$root/usr/share/icons/hicolor/scalable/apps/openhw.svg"
install -m 644 CHANGELOG.md README.md "$root/usr/share/doc/openhw/"
cp -r packaging/gnome-extension/. "$root/usr/share/gnome-shell/extensions/"

cat > "$root/usr/share/applications/openhw.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=OpenHW
Comment=Hardware sensor monitor (HWiNFO-style)
Exec=/usr/bin/openhw
Icon=openhw
Terminal=false
Categories=System;Monitor;
StartupWMClass=openhw
DESKTOP

# dotnet publish leaves files group-writable under some umasks; packages ship 755/644.
find "$root" -type d -exec chmod 755 {} +
find "$root" -type f -exec chmod 644 {} +
chmod 755 "$root/usr/lib/openhw/app/openhw" "$root/usr/lib/openhw/helper/openhw-helper"

cat > "$root/DEBIAN/control" <<CONTROL
Package: openhw
Version: $version
Architecture: amd64
Maintainer: russellmm <russellmm@users.noreply.github.com>
Homepage: https://github.com/russellmm/OpenHW
Section: utils
Priority: optional
Installed-Size: $(du -sk --exclude=DEBIAN "$root" | cut -f1)
Depends: dotnet-runtime-8.0, libsensors5, libfontconfig1, libx11-6, libice6, libsm6, libxi6, libxrandr2, libxcursor1
Description: HWiNFO-style hardware sensor monitor
 Sensors Status window with current, minimum, maximum and average readings for CPU, GPU, board, drives and memory,
 plus tray icons (with hover details on GNOME), logging, alerts, power profiles, CPU frequency caps and CPU/GPU
 power limits. The openhw-helper service does the reads and writes that need root, through fixed, range-checked
 commands.
CONTROL

install -m 755 /dev/stdin "$root/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
[ "$1" = configure ] || exit 0

getent group openhw >/dev/null || addgroup --system openhw

# Earlier installs came from packaging/install-helper.sh: a unit in /etc (which would override the packaged one) and
# the helper in /usr/local. Both are OpenHW's own files.
old=/etc/systemd/system/openhw-helper.service
if [ -f "$old" ] && grep -q '^ExecStart=/usr/local/lib/openhw/' "$old"; then
    systemctl stop openhw-helper.service 2>/dev/null || true
    rm -f "$old" /etc/systemd/system/multi-user.target.wants/openhw-helper.service
    rm -rf /usr/local/lib/openhw
fi

# Boot-time GPU limits go in OpenHW's own drop-in; the helper's sandbox can write there but can't create the directory.
if [ -f /etc/systemd/system/nvidia-power-limit.service ]; then
    install -d -m 755 /etc/systemd/system/nvidia-power-limit.service.d
fi

# The user who ran sudo (or pkexec, for graphical installers) gets the group that may talk to the helper.
user="${SUDO_USER:-}"
[ -z "$user" ] && [ -n "${PKEXEC_UID:-}" ] && user="$(getent passwd "$PKEXEC_UID" | cut -d: -f1)"
if [ -n "$user" ] && [ "$user" != root ]; then
    if ! id -nG "$user" | tr ' ' '\n' | grep -qx openhw; then
        usermod -aG openhw "$user"
        echo "Added '$user' to the openhw group: reboot (or log out and back in) once for it to take effect."
    fi
else
    echo "Add each OpenHW user to the openhw group: sudo usermod -aG openhw <user>"
fi

if [ -d /run/systemd/system ]; then
    systemctl daemon-reload
    systemctl enable openhw-helper.service
    systemctl restart openhw-helper.service
fi
POSTINST

install -m 755 /dev/stdin "$root/DEBIAN/prerm" <<'PRERM'
#!/bin/sh
set -e
if [ "$1" = remove ] && [ -d /run/systemd/system ]; then
    systemctl disable --now openhw-helper.service || true
fi
PRERM

install -m 755 /dev/stdin "$root/DEBIAN/postrm" <<'POSTRM'
#!/bin/sh
set -e
[ -d /run/systemd/system ] && systemctl daemon-reload || true
POSTRM

mkdir -p dist
deb="dist/openhw_${version}_amd64.deb"
dpkg-deb --root-owner-group -Zxz --build "$root" "$deb" >/dev/null
echo "Built $deb ($(du -h "$deb" | cut -f1))"
