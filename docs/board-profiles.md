# Board profiles: naming Super I/O voltages

Linux reports a Super I/O chip's voltage inputs as `in0`…`inN`, measured at the pin. The chip only reads up to
about 2 V, so boards put resistor dividers in front of higher rails, and which rail goes to which pin is up to
the board maker. HWiNFO names and scales them from a per-board database; OpenHW does the same through
`src/OpenHW.Hardware/Engines/Hwmon/BoardProfiles.cs`.

## Method

1. **Order.** HWiNFO lists a chip's voltages in register order (Vcore, +5V, AVSB, 3VCC, +12V, …), which lines
   up with `in0`, `in1`, … Inputs HWiNFO hides (unconnected pins) leave gaps or trailing extras.
2. **Value match and scale.** On the same machine, read the raw values in Linux
   (`cat /sys/class/hwmon/hwmonN/inX_input`, millivolts) and compare with a HWiNFO capture. For steady rails,
   HWiNFO value ÷ raw value is the divider. Several rails sharing one ratio (e.g. ×1.11) confirm each other.
3. **Confirm moving rails** (Vcore, SoC, per-CCD core rails) one of two ways:
   - run the same load in both OSes and compare the min/max ranges, or
   - change that voltage in the BIOS and see which input follows (definitive).
4. **Record** the mapping with `Confirmed: false` until step 3 is done, and keep the evidence table below current.

## ASUS Pro WS TRX50-SAGE WIFI A — Nuvoton NCT6701D (Linux driver: nct6799)

Raw values read on Linux, 2026-10-22; HWiNFO 8.54 values from `reference/` captures on the same machine.

| Pin | Raw | HWiNFO name | HWiNFO value | Scale | Status |
|---|---|---|---|---|---|
| in0 | 0.760 V | Vcore | 0.755–1.225 V | ×1 | confirmed by range |
| in1 | 1.024 V | +5V | 5.120 V | ×5 | exact |
| in2 | 3.392 V | AVSB | 3.392 V | ×1 | exact |
| in3 | 3.296 V | 3VCC | 3.296 V | ×1 | exact |
| in4 | 0.992 V | +12V | 11.904 V | ×12 | exact |
| in5 | 0.968 V | CPU VDDIO | 1.074 V | ×1.11 | ratio 1.109 |
| in6 | 0.672 V | CPU Core1 | 0.746–1.252 V | ×1.11 | **unconfirmed** (raw × 1.11 = HWiNFO min) |
| in7 | 3.392 V | 3VSB | 3.392 V | ×1 | exact |
| in8 | 3.296 V | VBAT | 3.296 V | ×1 | exact |
| in9 | 3.296 V | VTT | 3.296 V | ×1 | exact |
| in10 | 1.056 V | CHIPSET1 1.0V | 1.056 V | ×1 | exact |
| in11 | 1.216 V | VDD_11_S3 / MC | 1.350 V | ×1.11 | ratio 1.110 |
| in12 | 1.048 V | VIN2 | 1.048–1.056 V | ×1 | in range |
| in13 | 0.824 V | CPU VSOC | 0.915 V | ×1.11 | **unconfirmed** (ratio 1.110, but SoC can move) |
| in14 | 1.624 V | CHIPSET1 1.8V | 1.803 V | ×1.11 | ratio 1.110 |
| in15 | 0.784 V | VIN9 | 0.800–0.944 V | ×1 | **unconfirmed** (just below HWiNFO's min) |
| in16 | 3.392 V | VHIF | 3.392 V | ×1 | exact |
| in17 | 0.304 V | — | not shown by HWiNFO | — | hidden |

To confirm: run a CPU load in Linux and check in6 tracks per-CCD core voltage; change SoC voltage in the BIOS and
check in13 (×1.11) follows; compare VIN9 under the same load in both OSes.
