#!/bin/bash
# One guarded test: does the WRX90E's CPU_FAN header follow the NCT6798's pwm2? (owner-approved, 2026-11-14)
# Run as root. Stops OpenFan's helper so only one program touches the fan, raises pwm2 (never lowers it) for a few
# seconds at each step, and always restores: pwm2_enable back to its mode, the duty back, OpenFan's helper started.
set -u
H=$(grep -l '^nct67' /sys/class/hwmon/hwmon*/name | head -1 | xargs dirname)
[ -n "$H" ] || { echo "No NCT67xx chip found"; exit 1; }
EN0=$(cat "$H/pwm2_enable"); PWM0=$(cat "$H/pwm2")
OPENFAN=0
if systemctl is-active --quiet openfan-helper.service; then OPENFAN=1; fi

restore() {
    echo "$EN0" > "$H/pwm2_enable" 2>/dev/null
    [ "$EN0" = "1" ] && echo "$PWM0" > "$H/pwm2" 2>/dev/null
    echo "Restored pwm2_enable=$(cat "$H/pwm2_enable") pwm2=$(cat "$H/pwm2")"
    if [ "$OPENFAN" = 1 ]; then systemctl start openfan-helper.service && echo "Started openfan-helper again"; fi
}
trap restore EXIT INT TERM

[ "$OPENFAN" = 1 ] && systemctl stop openfan-helper.service && echo "Stopped openfan-helper for the test"
sample() { for i in 1 2 3 4 5; do echo "  $1: pwm2=$(cat "$H/pwm2") enable=$(cat "$H/pwm2_enable") CPU_FAN(fan2)=$(cat "$H/fan2_input") RPM"; sleep 2; done; }

echo "Start: pwm2_enable=$EN0 pwm2=$PWM0"
sample "before (auto)"
echo 1 > "$H/pwm2_enable"
for duty in 153 255; do   # 60 %, then 100 % — only ever above the 20 % it runs at now
    echo "$duty" > "$H/pwm2"
    sample "manual $duty/255"
done
