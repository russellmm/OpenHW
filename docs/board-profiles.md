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
| in6 | 0.672 V | CPU Core1 | 0.746–1.252 V | ×1.11 | **confirmed** (BIOS pin SIO_VIN4; tracks EC VRM Vcore1 on Windows and CCD1/CCD2 load on Linux — see below) |
| in7 | 3.392 V | 3VSB | 3.392 V | ×1 | exact |
| in8 | 3.296 V | VBAT | 3.296 V | ×1 | exact |
| in9 | 3.296 V | VTT | 3.296 V | ×1 | exact |
| in10 | 1.056 V | CHIPSET1 1.0V | 1.056 V | ×1 | exact |
| in11 | 1.216 V | VDD_11_S3 / MC | 1.350 V | ×1.11 | ratio 1.110 |
| in12 | 1.048 V | VIN2 | 1.048–1.056 V | ×1 | in range |
| in13 | 0.824 V | CPU VSOC | 0.915 V | ×1.11 | **likely** (BIOS pin SIO_VIN3; ×1.11 matches SVI3 VDDCR_SOC; fixed under load — BIOS change to confirm) |
| in14 | 1.624 V | CHIPSET1 1.8V | 1.803 V | ×1.11 | ratio 1.110 |
| in15 | 0.784 V | VIN9 | 0.800–0.944 V | ×1 | unlabeled pin (BIOS doesn't name it; drifts 0.800→0.832 V with temperature, not load) |
| in16 | 3.392 V | VHIF | 3.392 V | ×1 | exact |
| in17 | 0.304 V | — | not shown by HWiNFO | — | hidden |

To confirm: change SoC voltage in the BIOS and check in13 (×1.11) follows.

### Linux load tests (2026-10-23)

**Rail layout (from HWiNFO on Windows):** the EC VRM and per-core VIDs show two core rails — **Vcore0 feeds CCD0 and
CCD3** (cores 0–7, 24–31), **Vcore1 feeds CCD1 and CCD2** (cores 8–23). HWiNFO's Nuvoton "Vcore" follows Vcore0 and
"CPU Core1" follows Vcore1.

**First attempt — misleading.** `linux-capture.sh` + `compare.py` showed in0/in6 ~0.4 V below HWiNFO at the same
250 W and in6 rising for either CCD. Cause: every CPU's `scaling_max_freq` was stuck at 4.0 GHz (base clock), so the
cores never boosted. power-profiles-daemon 0.30 turns per-policy `boost` off in power-saver; the kernel then clamps
`scaling_max_freq` to base, and switching to performance re-enables boost **without lifting the clamp**. Fix (root):
write `cpuinfo_max_freq` back into each policy's `scaling_max_freq`. loadgen's Linux build is also light (~127 W
all-core vs 223 W on Windows); `tools/loadgen/sweep.py` uses plain busy loops instead.

**Sweep at full boost** (`tools/loadgen/sweep.py`, performance profile, cap lifted; data in
`reference/stress-2026-10-23/linux-sweep-boost.csv`, the 4 GHz-capped power-saver run in `linux-sweep-powersaver.csv`).
Only **in0 and in6** respond to load; every other input stays within 1–2 ADC steps except in15 (temperature drift).

| Load | Package | Clock | in0 raw | in6 ×1.11 |
|---|---|---|---|---|
| idle | 69 W | 1.2 GHz | 1.112 V | 1.123 V |
| 1t CCD0 | 86 W | 5.41 GHz | 1.160 V | 1.030 V |
| 1t CCD1 | 86 W | 5.04 GHz | 1.136 V | **1.323 V** |
| 1t CCD2 | 85 W | 5.04 GHz | 1.104 V | **1.314 V** |
| 1t CCD3 | 84 W | 5.03 GHz | **1.184 V** | 0.986 V |
| 8t CCD1 | 168 W | 4.75 GHz | 1.128 V | **1.252 V** |
| 8t CCD2 | 166 W | 4.82 GHz | 1.120 V | **1.225 V** |
| 8t CCD3 | 158 W | 4.77 GHz | 1.112 V | 0.986 V |
| 64t all threads | 250 W | 3.97 GHz | 0.856 V | 0.950 V |

- **in6 = Vcore1, confirmed**: it rises to 1.22–1.32 V only when CCD1 or CCD2 is loaded and stays ~0.99 V with CCD3
  loaded — the same split as HWiNFO's Core1 / EC Vcore1 (1.252 V all-core at 4.95 GHz). Loading CCD0 alone also
  lifts in6, most likely because unpinned background work (including the sampler) moves to CCD1 and boosts there.
- **in0 = Vcore0**: highest with CCD3/CCD0 loaded. ×1 stays as is.
- At 64 threads both rails drop to ~0.86 V because the 250 W HSMP cap holds the clock near 4.0 GHz with this load.
- **in15 VIN9** follows temperature, not load: 0.800 V cold → 0.832 V warm, still 0.824 V after the load stopped.
  Windows (warm) read 0.824–0.832 V. Stays unconfirmed/unnamed.
- The `asusec` hwmon chip (not `asus_ec_sensors`, which `linux-capture.sh` filters for) reports only temperatures
  and fans — Linux has no second Vcore source to cross-check against.

### Temperatures and fans (2026-10-23)

Matched on behaviour: the same idle → 1 thread → 8 threads → all threads → idle run on Linux (busy loops) as the
Windows loadgen run, every Nuvoton input logged once a second, plus a 90 s full-load watch for the slow ones.

| Input | Linux label | Linux behaviour | HWiNFO name | HWiNFO behaviour | Status |
|---|---|---|---|---|---|
| temp1 | SYSTIN | 38 °C flat | Motherboard | 35 °C flat | confirmed |
| temp2 | CPUTIN | 39.5 → 48 °C, lags minutes | — | not listed | kept, chip name |
| temp3 | AUXTIN0 | 37.5 °C, **drops** to 32 °C under load | Temp2 | 44 °C, **drops** to 19–23 °C under load | confirmed (same odd behaviour) |
| temp4–6 | AUXTIN1–3 | 23 / 14 / −9 °C flat | — | not listed | hidden (open pins) |
| temp7 | AUXTIN4 | 28 °C flat | Temp9 | 28 °C flat | confirmed |
| temp8 | PECI/TSI Agent 0 Calibration | 39 → 56 °C, ~11 °C below Tctl | CPU | 39 → 48/60 °C, ~12 °C below | **likely** |
| temp9 | Virtual_TEMP | 45 → 47 °C over 90 s full load, slow back | CPU (Weighted Value) | 35 °C flat | **likely** (heavily averaged) |
| temp10 | AUXTIN5 | 97 °C flat | Temp14 | 98 °C flat | confirmed (open pin, shown as HWiNFO does) |
| temp11–12 | PCH_CHIP_* | 0 °C | — | not listed | hidden |
| temp13 | TSI0_TEMP | equals k10temp Tctl | TSI0 (CPU) | equals CPU Package | confirmed |

HWiNFO's "CPU Package" read the same as TSI0 throughout; Linux has no separate register for it, and TSI0 and
CPU (Tctl/Tdie) show the same value.

Fans are in HWiNFO's register order. CPU (fan2: 519 vs 517 RPM) and Water Pump+ (fan6: 3,524 vs 3,479 RPM) aren't
under software control and match within 2 %; Chassis 1–4 (fan1, fan3–5) run different curves in each OS but keep
the same order.

| Input | HWiNFO name | Linux RPM | HWiNFO RPM |
|---|---|---|---|
| fan1 | Chassis 1 | 1,439 | 1,350 |
| fan2 | CPU | 519 | 517 |
| fan3 | Chassis 2 | 574 | 340 |
| fan4 | Chassis 3 | 667 | 400 |
| fan5 | Chassis 4 | 2,209 | 1,497 |
| fan6 | Water Pump+ | 3,524 | 3,479 |

Without HWiNFO, the BIOS image names the rails and their Nuvoton pins, and a load test separates the core rails.
See [hwinfo-research.md](hwinfo-research.md).
