#!/bin/sh
# One-off cleanup (2026-11-17): removes what RTXinVM left behind now that OpenHW 0.33 has its own GPU Lending hook,
# and the old board's per-slot lines from nvidia-power-limit.service. Everything removed or changed is archived first.
# Run with sudo. Undo: sudo tar -xzpPf <archive>   then   sudo systemctl daemon-reload
set -eu
[ "$(id -u)" = 0 ] || { echo "run with sudo"; exit 1; }

archive=/var/backups/openhw-rtxinvm-cleanup-$(date +%Y%m%d-%H%M%S).tar.gz
unit=/etc/systemd/system/nvidia-power-limit.service
set --
for p in /usr/local/libexec/windows-gpu-handoff /var/lib/windows-gpu-handoff \
         /etc/systemd/system/windows-gpu-boot-test.service /etc/systemd/system/windows-gpu-session-start.service \
         /etc/openhw/lending-off "$unit"; do
    [ -e "$p" ] && set -- "$@" "$p"
done
tar -czpf "$archive" -P "$@"
echo "Backed up to $archive"

# RTXinVM's units (one disabled, one started by nothing) and its scripts and state.
systemctl disable windows-gpu-boot-test.service 2>/dev/null || true
rm -f /etc/systemd/system/windows-gpu-boot-test.service /etc/systemd/system/windows-gpu-session-start.service
rm -rf /usr/local/libexec/windows-gpu-handoff /var/lib/windows-gpu-handoff
# RTXinVM's old hook launcher and udev rule, put aside by OpenHW.
rm -rf /etc/openhw/lending-off

# The old board's per-slot power lines (01:00.0, 11:00.0, E1:00.0); "-pm 1" stays. OpenHW's per-card lines live in
# its drop-in (nvidia-power-limit.service.d/openhw.conf), which OpenHW rewrites without them afterwards.
sed -i '/^ExecStart=.*nvidia-smi -i .* -pl /d' "$unit"

systemctl daemon-reload
echo "Done. $unit now runs:"
grep '^ExecStart=' "$unit"
