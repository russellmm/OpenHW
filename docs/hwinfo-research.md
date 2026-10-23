# How HWiNFO64 gets its readings, and how to get them without it

Research done on the TRX50 workstation under Windows 11, 2026-10-23, HWiNFO64 8.54-6090, BIOS 0617.
Raw material is in [`reference/stress-2026-10-23/`](../reference/stress-2026-10-23/); the tools are in
[`tools/loadgen/`](../tools/loadgen/).

## What HWiNFO reads on this machine

HWiNFO exposes its full sensor tree through shared memory (`Global\HWiNFO_SENS_SM2`, enabled with
`SensorsSM=1` in `HWiNFO64.INI` and a restart). [`tools/loadgen/hwinfo_shm.py`](../tools/loadgen/hwinfo_shm.py)
reads it: `dump` prints everything, and `log` writes a CSV. 37 sensor groups, 1,141 readings; the full list is in
[`windows-hwinfo-sensor-inventory.txt`](../reference/stress-2026-10-23/windows-hwinfo-sensor-inventory.txt).
Each group carries a sensor ID that names its source (e.g. `0xf7067010` Nuvoton, `0xfb19283b` XDPE19283B).

All low-level access goes through HWiNFO's own kernel driver (`HWiNFO_x64_216.sys`, extracted to `%TEMP%` and
loaded as service `HWiNFO_216`), which gives it port I/O, PCI config space, MSRs, SMBus and the SMU mailbox. HWiNFO
itself is closed source; the sources below are inferred from the chips and buses each group must come from.

| HWiNFO group | Hardware / bus | How it's read | Linux equivalent |
|---|---|---|---|
| CPU (clocks, usage, effective clocks) | MSRs (APERF/MPERF, P-state) | driver `rdmsr` per core | `msr` module (root) — on the roadmap via helper |
| CPU Enhanced: SVI3 VDDCR_VDD / SOC V, A, W, TDC/EDC, VRM TFN temps, per-core power/temps, L3/IOD temps, FCLK/UCLK, limits, DRAM bandwidth | AMD SMU **PM table** (SVI3 telemetry the SMU collects from the VRMs) | SMU mailbox → PM table in DRAM | `ryzen_smu` (out-of-tree) PM table; HSMP covers only some (power, FCLK/UCLK, limits, DRAM BW) — **no voltages in HSMP** |
| Nuvoton NCT6701D: Vcore…VHIF, temps, fans | Super I/O on LPC | port I/O (index/data at the HWM base) | `nct6775` driver (reports `nct6799`) — already used |
| ASUS EC: VRM1/2, T_Sensor, **VRM Vcore0/1 VOUT, IOUT, POUT**, VRM heatsink fans | ASUS embedded controller | EC register map per board | `asus_ec_sensors` has temps/fans only for this board; VOUT/IOUT need the register map |
| Infineon XDPE19283B: VR Loop1/2 temp, VIN, **VOUT 1.35 V**, IOUT, IIN, POUT | PMBus on the board SMBus | SMBus reads | kernel `xdpe12284`/pmbus family, must be instantiated on the right bus (root) |
| DDR5 DIMMs: SPD hub temp, TS0/TS1, **PMIC SWA/SWB/SWC rails, 1.8 V/1.0 V LDOs, VIN, power, alarms** | SPD5118 hub + PMIC on the DIMM SMBus | SMBus reads | `spd5118` gives hub temp only; PMIC needs read-only SMBus access (root) |
| AMD Chipset: Chipset 1 (xHCI) temp | Promontory chipset | PCI MMIO | none known |
| GPUs | NVIDIA | NVAPI / NVML | NVML — already used |
| Drives / S.M.A.R.T. | NVMe | NVMe admin pass-through | already via `openhw-helper` |
| USB-C PD (Realtek RTS545x) | SMBus/I²C | | none |
| Thermal Grizzly WireView Pro II (12V-2x6 per-pin V/A/W) | USB | vendor protocol | USB HID — easy win if the protocol is known |
| WHEA errors, PresentMon, network | Windows APIs | | EDAC / NIC stats — already used |

Things HWiNFO showed that we hadn't listed: the XDPE19283B's single rail is **1.35 V at ~5 A**, so it's the memory
(VDD_11_S3/VDDIO_Mem) regulator, not a CPU core VR. The ASUS EC reports **two core VRM rails** (Vcore0, Vcore1), and
the load test shows what each one feeds (below).

## How the Nuvoton pins were named without HWiNFO

HWiNFO's names come from a per-board database that ASUS supplies. Three sources independent of HWiNFO give the
same mapping, so another board can be done the same way:

### 1. The BIOS image names the rails (and which pin each is on)

The BIOS for this board (`Pro-WS-TRX50-SAGE-WIFI-A-ASUS-0617.CAP`, UEFIExtract dump under
`F:\deepseek-linux\projects\tr9970x-hsmp\analysis-0617\`) contains:

- **Monitor page labels** (Setup module `899407D7-…`, UTF-16 strings in display order):
  `CPU Core0 Voltage, 12V Voltage, 5V Voltage, 3.3V Voltage, CPU VDDIO, CPU Core1 Voltage, CHIPSET1 1.0V,
  VDD_11_S3 / MC Voltage, CPU VSOC Voltage, 1.8V_RUN`.
- **The setup-variable struct for those rails**, with Nuvoton pin names (`ASUS_HWM_SETUP_ONEOF`, embedded C
  definitions in the setup data):
  `V12, V5, V3_3, VCORE, SIO_VIN4, SIO_VIN3, VDD2, SIO_VIN5, SIO_VIN6, SIO_VIN7`.

The Nuvoton datasheet's voltage registers (bank 4, 0x80–0x8F) are in the order the Linux driver numbers them:

| Linux | in0 | in1 | in2 | in3 | in4 | in5 | in6 | in7 | in8 | in9 | in10 | in11 | in12 | in13 | in14 | in15 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Nuvoton pin | VCORE | VIN1 | AVSB | 3VCC | VIN0 | VIN8 | **VIN4** | 3VSB | VBAT | VTT | VIN5 | VIN6 | VIN2 | **VIN3** | VIN7 | VIN9 |

Matching BIOS pin names to that order gives in6 = SIO_VIN4 = **CPU Core1**, in13 = SIO_VIN3 = **CPU VSOC**,
in10 = VIN5 = CHIPSET1 1.0V, in11 = VIN6 = VDD_11_S3, in14 = VIN7 = 1.8V_RUN (HWiNFO: "CHIPSET1 1.8V"), the same
as HWiNFO. The two pins the BIOS doesn't label (in12 = VIN2, in15 = VIN9) are exactly the two HWiNFO shows with
generic names, which supports the register order. (The pin order is from the Nuvoton datasheet; check it against
the NCT6796D/6798D datasheet when convenient.)

The **dividers** aren't stored as strings: they're constants in the BIOS hardware-monitor code. Without a
disassembler, get them from steady rails (nominal ÷ raw: 12 V → ×12, 5 V → ×5), or by reading the BIOS Monitor
page and the raw `inX_input` at the same moment (the BIOS shows scaled values). The ×1.11 group (VDDIO, VDD_11_S3,
1.8 V, Core1, VSOC) is consistent across five rails, and against the SVI3/EC readings below.

Tools: [`tools/bios/biosgrep.py`](../tools/bios/biosgrep.py) `<dump dir> <regex>` searches UTF-16 strings in
every module body, and [`tools/bios/strings_around.py`](../tools/bios/strings_around.py) `<body.bin> <string> <n>`
prints the strings next to a match. Together they give these lists in a few minutes.

### 2. Cross-checking against other sensors that measure the same rail

| Pin (×scale) | Idle reading | Independent reading of the same rail |
|---|---|---|
| in0 Vcore (×1) | 0.968 V | EC VRM Vcore0 VOUT 0.976 V; SVI3 VDDCR_VDD 0.973 V |
| in6 CPU Core1 (×1.11) | 0.764 V | EC VRM Vcore1 VOUT 0.763 V |
| in13 CPU VSOC (×1.11) | 0.915 V | SVI3 VDDCR_SOC 0.921 V |
| in11 VDD_11_S3 (×1.11) | 1.350 V | XDPE19283B VOUT 1.352 V; DIMM PMIC VDD 1.350 V |
| in4 +12V (×12) | 11.904 V | XDPE VIN 12.03 V; DIMM PMIC VIN 12.04 V; WireView 12.03 V |

On Linux the SVI3 values need `ryzen_smu`, and the EC/XDPE/PMIC values need root SMBus/EC access, so this check is
mostly a Windows-side tool. HSMP can't help here (it reports no voltages).

### 3. A load test (definitive for moving rails)

`tools/loadgen/loadgen.c` runs seven 30 s phases: idle, 1 thread scalar, 1 thread AVX2, 8 threads scalar, all
threads scalar, all threads AVX2, idle. Medians from 6 s into each phase (HWiNFO polling at 2 s):

| | idle | scalar-1t | avx2-1t | scalar-8t | scalar-all | avx2-all | idle-after |
|---|---|---|---|---|---|---|---|
| in0 Vcore | 0.968 | 0.968 | 0.968 | **1.048** | **1.234** | 1.172 | 0.977 |
| EC VRM Vcore0 VOUT | 0.976 | 0.974 | 1.050 | 1.048 | 1.238 | 1.167 | 0.976 |
| SVI3 VDDCR_VDD | 0.973 | 0.974 | 0.999 | 1.048 | 1.233 | 1.170 | 0.978 |
| in6 CPU Core1 | 0.764 | 0.755 | 0.755 | **0.755** | **1.252** | 1.172 | 0.764 |
| EC VRM Vcore1 VOUT | 0.763 | 0.765 | 0.761 | 0.765 | 1.261 | 1.171 | 0.765 |
| in13 CPU VSOC | 0.915 | 0.915 | 0.915 | 0.915 | 0.915 | 0.915 | 0.915 |
| SVI3 VDDCR_SOC | 0.921 | 0.921 | 0.921 | 0.921 | 0.921 | 0.921 | 0.921 |
| in15 VIN9 | 0.832 | 0.824 | 0.824 | 0.824 | 0.824 | 0.824 | 0.824 |
| EC Vcore0 / Vcore1 IOUT (A) | 7 / 3 | 8 / 3 | 10 / 3 | 22 / 3 | 75 / 66 | 93 / 80 | 7 / 3 |
| CPU PPT (W) | 57 | 58 | 61 | 74 | 224 | 250 (limit) | 58 |
| Tctl (°C) | 44 | 44 | 47 | 50 | 60 | 71 | 54 |
| Effective clock, CCD1 / CCD2–4 (MHz) | | | | ~4.5 G / idle | 5000 / ~4900 | 5000 / ~4700 | |

Findings:

- **in6 is CPU Core1, confirmed.** With 8 threads (Windows put them on CCD1), Vcore0 rose and Core1 stayed at
  0.755 V. Core1 only moved once CCD2–4 were loaded, and it tracks EC VRM Vcore1 to within 10 mV in every phase.
  So Vcore0 feeds CCD1 (and is the rail SVI3 "VDDCR_VDD" reports), and Vcore1 feeds the other CCDs.
- in0 Vcore tracks Vcore0 and VDDCR_VDD exactly; avx2-all is lower than scalar-all because PPT hit its 250 W limit.
- **in13 VSOC can't be confirmed by CPU load**: SoC voltage is fixed (0.921 V by SVI3), so it doesn't move. The
  BIOS pin name (SIO_VIN3) and the steady ×1.11 match to SVI3 are strong evidence. For a definitive test, change
  SoC voltage in the BIOS and watch in13.
- in15 VIN9 is flat (one LSB of movement) and unlabeled by the BIOS, so it's an unused or unnamed pin; ×1 is
  as good as any scale.
- 1T-AVX2 put the thread on a CCD1 core and moved Vcore0 / VDDCR_VDD (1.05 V by EC), but in0's 2 s median missed
  it. Transient rails need a faster poll to compare.

## Running the same test on Linux

```bash
tools/loadgen/linux-capture.sh /tmp/openhw-stress 30      # builds loadgen, logs all hwmon inputs at 1 s
python3 tools/loadgen/compare.py /tmp/openhw-stress reference/stress-2026-10-23/windows-hwinfo-phase-medians.csv
```

`compare.py` applies the board-profile scales to each `nct6799/inX_input` and prints Linux next to HWiNFO for
every phase. Expect in6 to stay near 0.755 V through scalar-8t and jump to ~1.25 V in scalar-all. The Linux
scheduler may spread the 8 threads across CCDs, which would also lift Core1 in scalar-8t. If so, pin with
`taskset -c 0-7` to reproduce the Windows placement. Fans differ between OSes, so temperatures will too.
