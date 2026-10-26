#!/bin/bash
# One-time identification for OpenHW's DIMM and VR sensors (run with sudo; needs i2c-tools).
# Reads only, except PMIC register 0x30 (ADC select), which is restored to its original value at the end.
#  1. Board SMBus (i2c-6): which address is the Infineon XDPE19283B? Standard PMBus reads at the addresses
#     i2cdetect found, skipping 0x70-0x77 (RGB controller / mux range).
#  2. DIMM bus (i2c-0): one DIMM's PMIC (0x48) status, meters and ADC rails, and its TS0/TS1 sensors.
set -u
[[ $EUID -eq 0 ]] || { echo "run with sudo" >&2; exit 1; }

echo "== i2c-6 PMBus identification"
for a in 0x40 0x46 0x49 0x4c 0x60 0x62 0x65 0x66 0x68 0x6f; do
    rev=$(i2cget -y 6 $a 0x98 b 2>&1)      # PMBUS_REVISION
    mfr=$(i2cget -y 6 $a 0x99 s 2>&1)      # MFR_ID (block)
    dev=$(i2cget -y 6 $a 0xad s 2>&1)      # IC_DEVICE_ID (block)
    echo "$a rev=$rev mfr=[$mfr] dev=[$dev]"
done

echo "== i2c-6 PMBus telemetry (page 0) at addresses that answered PMBUS_REVISION"
for a in 0x40 0x46 0x49 0x4c 0x60 0x62 0x65 0x66 0x68 0x6f; do
    i2cget -y 6 $a 0x98 b >/dev/null 2>&1 || continue
    echo "$a page=$(i2cget -y 6 $a 0x00 b 2>&1) vout_mode=$(i2cget -y 6 $a 0x20 b 2>&1)" \
         "vin=$(i2cget -y 6 $a 0x88 w 2>&1) iin=$(i2cget -y 6 $a 0x89 w 2>&1) vout=$(i2cget -y 6 $a 0x8b w 2>&1)" \
         "iout=$(i2cget -y 6 $a 0x8c w 2>&1) temp1=$(i2cget -y 6 $a 0x8d w 2>&1) pout=$(i2cget -y 6 $a 0x96 w 2>&1)" \
         "pin=$(i2cget -y 6 $a 0x97 w 2>&1)"
done

echo "== i2c-0 PMIC 0x48"
r() { i2cget -y 0 0x48 $1 b 2>&1; }
echo "vendor=$(r 0x3c) $(r 0x3d) status08-0b=$(r 0x08) $(r 0x09) $(r 0x0a) $(r 0x0b) meters0c/0e/0f=$(r 0x0c) $(r 0x0e) $(r 0x0f)" \
     "r1a=$(r 0x1a) r1b=$(r 0x1b) r30=$(r 0x30) r33=$(r 0x33)"
orig=$(r 0x30)
for sel in 0 2 3 5 8 9; do                 # SWA SWB SWC VIN_BULK VLDO_1.8V VLDO_1.0V
    i2cset -y 0 0x48 0x30 $(( 0x80 | (sel << 3) | (orig & 3) )) b
    sleep 0.05
    echo "adc sel=$sel raw=$(r 0x31)"
done
i2cset -y 0 0x48 0x30 $orig b
echo "r30 restored=$(r 0x30)"

echo "== i2c-0 thermal sensors"
for a in 0x10 0x30; do echo "$a mr0-1=$(i2cget -y 0 $a 0x00 b 2>&1) $(i2cget -y 0 $a 0x01 b 2>&1) temp=$(i2cget -y 0 $a 0x31 w 2>&1)"; done
