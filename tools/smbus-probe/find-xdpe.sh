#!/bin/bash
# Find the Infineon XDPE19283B (run with sudo). Reads only.
# i2cdetect -r saw 0x0c on i2c-4: the SMBus Alert Response Address, answered when a device holds SMBALERT#.
# Reading it returns the alerting device's address (<<1); PMBus VRs often NACK the plain receive-byte that
# i2cdetect -r uses, so this is how one can be found without probing writes.
set -u
[[ $EUID -eq 0 ]] || { echo "run with sudo" >&2; exit 1; }

pmbus() { # bus addr
    local b=$1 a=$2
    echo "i2c-$b $a rev=$(i2cget -y $b $a 0x98 b 2>&1) mfr=[$(i2cget -y $b $a 0x99 s 2>&1)] dev=[$(i2cget -y $b $a 0xad s 2>&1)]" \
         "page=$(i2cget -y $b $a 0x00 b 2>&1) vout_mode=$(i2cget -y $b $a 0x20 b 2>&1) vin=$(i2cget -y $b $a 0x88 w 2>&1)" \
         "iin=$(i2cget -y $b $a 0x89 w 2>&1) vout=$(i2cget -y $b $a 0x8b w 2>&1) iout=$(i2cget -y $b $a 0x8c w 2>&1)" \
         "temp1=$(i2cget -y $b $a 0x8d w 2>&1) temp2=$(i2cget -y $b $a 0x8e w 2>&1) pout=$(i2cget -y $b $a 0x96 w 2>&1)" \
         "pin=$(i2cget -y $b $a 0x97 w 2>&1) status=$(i2cget -y $b $a 0x79 w 2>&1)"
}

for b in 4 5 6; do
    ara=$(i2cget -y $b 0x0c 2>&1)          # receive byte from the Alert Response Address
    echo "i2c-$b ARA -> $ara"
    if [[ $ara =~ ^0x[0-9a-f]+$ ]]; then pmbus $b $(printf '0x%02x' $(( ara >> 1 ))); fi
done
pmbus 4 0x20
