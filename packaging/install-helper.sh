#!/usr/bin/env bash
# Installs openhw-helper (root daemon: NVMe SMART reads, CPU frequency ceiling restore) and adds the invoking user to the openhw group.
# Run from the repository root:  sudo packaging/install-helper.sh
set -euo pipefail

if [[ $EUID -ne 0 || -z "${SUDO_USER:-}" ]]; then
    echo "Run with sudo from your normal account: sudo packaging/install-helper.sh" >&2
    exit 1
fi

repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$(sudo -u "$SUDO_USER" mktemp -d)"
trap 'rm -rf "$out"' EXIT

# Build as the invoking user so no root-owned files land in the working tree.
sudo -u "$SUDO_USER" dotnet publish "$repo/src/OpenHW.Helper/OpenHW.Helper.csproj" -c Release -o "$out" --nologo -v quiet

groupadd -f openhw
usermod -aG openhw "$SUDO_USER"

install -d -m 755 /usr/local/lib/openhw
rm -rf /usr/local/lib/openhw/*
cp -r "$out"/. /usr/local/lib/openhw/
chown -R root:root /usr/local/lib/openhw
chmod -R go-w /usr/local/lib/openhw

# Boot-time GPU limits go in OpenHW's own drop-in; the helper's sandbox can write there but can't create the directory.
if [[ -f /etc/systemd/system/nvidia-power-limit.service ]]; then
    install -d -m 755 /etc/systemd/system/nvidia-power-limit.service.d
fi

install -m 644 "$repo/packaging/openhw-helper.service" /etc/systemd/system/openhw-helper.service
systemctl daemon-reload
systemctl enable openhw-helper.service
systemctl restart openhw-helper.service
systemctl --no-pager --lines=3 status openhw-helper.service || true

echo
if [[ -e "/var/lib/systemd/linger/$SUDO_USER" ]]; then
    echo "Installed. Reboot once so '$SUDO_USER' picks up the openhw group (lingering keeps the old session's groups)."
else
    echo "Installed. Log out and back in once so '$SUDO_USER' picks up the openhw group (check with: id)."
fi
