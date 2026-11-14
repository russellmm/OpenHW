#!/bin/bash
# One-time, read-only survey of the BMC over the in-band KCS interface (/dev/ipmi0).
# Run as root: sudo tools/bmc-probe/dump.sh [outdir]. Writes text files only; changes nothing on the BMC.
set -u
out="${1:-$(dirname "$0")/out}"
mkdir -p "$out"
ipmitool mc info              > "$out/mc-info.txt" 2>&1
ipmitool sdr elist all        > "$out/sdr-elist.txt" 2>&1
ipmitool sdr -v list all      > "$out/sdr-verbose.txt" 2>&1
ipmitool sensor               > "$out/sensor.txt" 2>&1
ipmitool fru print            > "$out/fru.txt" 2>&1
ipmitool sdr info             > "$out/sdr-info.txt" 2>&1
chown -R "${SUDO_UID:-0}:${SUDO_GID:-0}" "$out"
echo "Wrote $(ls "$out" | wc -l) files to $out"
wc -l "$out"/*.txt
