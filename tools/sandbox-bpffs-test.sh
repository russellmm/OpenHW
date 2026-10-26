#!/bin/bash
# Which systemd sandbox settings let openhw-helper write to bpffs (/sys/fs/bpf)? Run with sudo. Each variant is a
# throwaway transient service that creates and removes /sys/fs/bpf/openhw-rwtest and prints RW or RO.
set -u
[[ $EUID -eq 0 ]] || { echo "run with sudo" >&2; exit 1; }
probe='mkdir /sys/fs/bpf/openhw-rwtest 2>/dev/null && rmdir /sys/fs/bpf/openhw-rwtest && echo RW || echo RO'
try() {
    local name=$1; shift
    printf '%-58s ' "$name:"
    systemd-run --quiet --wait --pipe --collect -p ProtectSystem=strict "$@" /bin/sh -c "$probe" 2>&1 | tail -1
}
try "A ProtectKernelTunables + ReadWritePaths (as shipped)" -p ProtectKernelTunables=yes -p ReadWritePaths=/sys/fs/bpf
try "B A + BindPaths=/sys/fs/bpf"                          -p ProtectKernelTunables=yes -p ReadWritePaths=/sys/fs/bpf -p BindPaths=/sys/fs/bpf
try "C no ProtectKernelTunables, ReadWritePaths"           -p ReadWritePaths=/sys/fs/bpf
rm -rf /sys/fs/bpf/openhw-rwtest 2>/dev/null
