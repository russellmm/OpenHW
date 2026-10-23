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
| in6 | 0.672 V | CPU Core1 | 0.746–1.252 V | ×1.11 | **unconfirmed** (BIOS pin SIO_VIN4; tracks EC VRM Vcore1 on Windows, but not on Linux — see below) |
| in7 | 3.392 V | 3VSB | 3.392 V | ×1 | exact |
| in8 | 3.296 V | VBAT | 3.296 V | ×1 | exact |
| in9 | 3.296 V | VTT | 3.296 V | ×1 | exact |
| in10 | 1.056 V | CHIPSET1 1.0V | 1.056 V | ×1 | exact |
| in11 | 1.216 V | VDD_11_S3 / MC | 1.350 V | ×1.11 | ratio 1.110 |
| in12 | 1.048 V | VIN2 | 1.048–1.056 V | ×1 | in range |
| in13 | 0.824 V | CPU VSOC | 0.915 V | ×1.11 | **likely** (BIOS pin SIO_VIN3; ×1.11 matches SVI3 VDDCR_SOC; fixed under load — BIOS change to confirm) |
| in14 | 1.624 V | CHIPSET1 1.8V | 1.803 V | ×1.11 | ratio 1.110 |
| in15 | 0.784 V | VIN9 | 0.800–0.944 V | ×1 | unlabeled pin (BIOS doesn't name it; flat under load) |
| in16 | 3.392 V | VHIF | 3.392 V | ×1 | exact |
| in17 | 0.304 V | — | not shown by HWiNFO | — | hidden |

To confirm: change SoC voltage in the BIOS and check in13 (×1.11) follows; resolve the Linux core-rail mismatch
below for in0/in6.

### Linux load test (2026-10-23)

`tools/loadgen/linux-capture.sh` + `compare.py` against `reference/stress-2026-10-23/windows-hwinfo-phase-medians.csv`,
run unpinned and under `taskset -c 0-7` / `taskset -c 8-15`, in both the power-saver and performance profiles
(OpenFan running; spot checks repeated with it closed gave the same numbers).

- **Every rail except in0/in6 matches Windows** within a few mV in every phase: in1–in5, in7–in12, in14, in16, and
  in13 (0.915 V both OSes, flat). in15 VIN9 reads 0.792–0.800 V on Linux vs 0.824–0.832 V in HWiNFO, flat in both.
- **loadgen is light on Linux.** Its all-core phases draw ~127 W (scalar) / ~150 W (AVX2) in either profile, vs
  223 / 250 W on Windows. Plain busy loops on 64 threads reach the 250 W HSMP cap at ~3.9 GHz even in power-saver,
  so the CPU isn't limited — the Linux build of loadgen is. In the performance profile in0/in6 sat flat at
  0.872 / 0.986 V (scaled) through every loadgen phase.
- **in0/in6 read far lower than HWiNFO at the same power.** Busy-loop spot checks (raw sysfs values):

| Load | Package power | in0 raw | in6 raw (×1.11) | HWiNFO, comparable load |
|---|---|---|---|---|
| idle | 55 W | 0.79–0.80 V | 0.72–0.79 V (0.80–0.88 V) | Vcore 0.968 V, Core1 0.764 V |
| 64 threads | 250 W | 0.864 V | 0.864 V (0.959 V) | Vcore 1.234 V, Core1 1.252 V |
| CPUs 0–7 only | 99 W | 0.840 V | 0.888 V (0.986 V) | 8 threads on cores 0–7: Core1 stays 0.755 V |
| CPUs 8–15 only | 101 W | 0.840 V | 0.896 V (0.995 V) | — |

  in6 rises the same whichever CCD is loaded, unlike Windows, where it stayed flat while only cores 0–7 ran. So
  in6 stays `Confirmed: false`, and in0's ×1 "confirmed by range" is in doubt too.

Open questions:

1. Does the `nct6799` driver read the NCT6701D's in0/in6 the way HWiNFO does (register, ADC scaling, an input mux
   the BIOS/EC sets)? Every other input agrees, so it's specific to the core rails.
2. Is HWiNFO's "Vcore" / "CPU Core1" really the raw Nuvoton pin, or a value it combines with the EC VRM / SVI3
   telemetry? Linux has no second Vcore source to check against (`asusec` reports only temperatures and fans;
   SVI3 needs `ryzen_smu`).
3. Why loadgen draws half the power on Linux (compiler / code generation vs the Windows build).
4. Note: `linux-capture.sh` filters on `asus_ec_sensors`, but the chip registers as `asusec` here, so EC readings
   were never captured (they're temperatures and fans only anyway).

Without HWiNFO, the BIOS image names the rails and their Nuvoton pins, and a load test separates the core rails.
See [hwinfo-research.md](hwinfo-research.md).
