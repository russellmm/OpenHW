#!/usr/bin/env bash
# Run loadgen while logging every hwmon reading once a second — the Linux half of the cross-OS comparison.
# Output: <outdir>/hwmon.csv (t, then one column per hwmon file) and <outdir>/loadgen-phases.txt.
# Compare with compare.py against reference/stress-2026-10-23/windows-hwinfo-phase-medians.csv.
#
#   tools/loadgen/linux-capture.sh [outdir] [seconds-per-phase]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-/tmp/openhw-stress-$(date +%Y%m%d-%H%M%S)}
secs=${2:-30}
mkdir -p "$out"

gcc -O2 -mavx2 -mfma -pthread "$here/loadgen.c" -o "$out/loadgen"

# Every numeric input of the chips that matter for the comparison (Super I/O, CPU, EC, HSMP, DIMMs).
files=()
for d in /sys/class/hwmon/hwmon*; do
    name=$(cat "$d/name")
    case "$name" in nct67*|k10temp|asus_ec_sensors|amd_hsmp_hwmon|spd5118|zenpower|asus_wmi_sensors) ;; *) continue ;; esac
    for f in "$d"/{in,temp,fan,power,curr,energy}*_input; do [[ -r $f ]] && files+=("$f"); done
done
{
    printf 't'
    for f in "${files[@]}"; do d=$(dirname "$f"); printf ',%s/%s' "$(cat "$d/name")" "$(basename "$f")"; done
    echo
} > "$out/hwmon.csv"

total=$((secs * 7 + 5))
(
    t0=$(date +%s%N)
    end=$((t0 + total * 1000000000))
    while (( $(date +%s%N) < end )); do
        ms=$((($(date +%s%N) - t0) / 1000000))
        line=$(printf '%d.%02d' $((ms / 1000)) $((ms % 1000 / 10)))
        for f in "${files[@]}"; do line+=",$(cat "$f" 2>/dev/null || true)"; done
        echo "$line" >> "$out/hwmon.csv"
        sleep 1
    done
) &
logger=$!
date +%s > "$out/t0.txt"
sleep 2
"$out/loadgen" "$secs" | tee "$out/loadgen-phases.txt"
wait "$logger"
echo "wrote $out"
