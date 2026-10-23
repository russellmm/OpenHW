#!/usr/bin/env bash
# Builds OpenHW (Release) and installs it for the current user on the system drive — no root:
#   ~/.local/lib/openhw/          the published app (runs without the source tree mounted)
#   ~/.local/bin/openhw           command on PATH
#   menu entry + icon             "OpenHW" in the GNOME app grid
# Re-run after pulling changes to update. Uses the system .NET 8 runtime (/usr/lib/dotnet).
set -euo pipefail

if [[ $EUID -eq 0 ]]; then
    echo "Run as your normal user (no sudo): packaging/install-app.sh" >&2
    exit 1
fi

repo="$(cd "$(dirname "$0")/.." && pwd)"
lib="$HOME/.local/lib/openhw"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

dotnet publish "$repo/src/OpenHW.App/OpenHW.App.csproj" -c Release -r linux-x64 --self-contained false \
    -o "$out" --nologo -v quiet

rm -rf "$lib"
mkdir -p "$lib" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/scalable/apps"
cp -r "$out"/. "$lib"/
ln -sfn "$lib/openhw" "$HOME/.local/bin/openhw"
install -m 644 "$repo/packaging/openhw.svg" "$HOME/.local/share/icons/hicolor/scalable/apps/openhw.svg"

cat > "$HOME/.local/share/applications/openhw.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=OpenHW
Comment=Hardware sensor monitor (HWiNFO-style)
Exec=$HOME/.local/bin/openhw
Icon=openhw
Terminal=false
Categories=System;Monitor;
StartupWMClass=openhw
DESKTOP

update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
gtk-update-icon-cache -q "$HOME/.local/share/icons/hicolor" 2>/dev/null || true

echo "Installed OpenHW to $lib"
echo "Run it with: openhw   (or from the app grid)"
