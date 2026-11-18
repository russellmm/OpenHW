# HWiNFO parity on Windows — WRX90E-SAGE SE, 2026-11-18

HWiNFO64 8.54 (sensors-only, run alone — OpenHW's app and service stopped) read through its shared memory
(`tools/loadgen/hwinfo_shm.py dump`: 44 groups, 1,212 readings) against `openhw --dump` (22 groups) taken just
before. Rows were matched by name; per-core / per-thread rows are counted once. Values were idle. Serial numbers are
left out.

## Closed the same day (03bc3d0, checked live against HWiNFO's values)

- **System**: Virtual Memory Available, Page File Total (586,752 MB, = HWiNFO), Page File Used.
- **CPU**: per-thread Utility (`% Processor Utility`) and Total CPU Utility; per-thread effective clocks under each
  core's row; **Core n Ratio and Core n VID** from MSR_HARDWARE_PSTATE_STATUS (PawnIO's AMDFamily17 read list; Zen 5
  layout — FID × 5 MHz, VID 0.245 V + 5 mV/step — matches HWiNFO: 45.25× / 0.995 V, 25× / 0.765 V).
- **GPU** (`WindowsGpuExtras`, wrapping the NVML engine; Linux unchanged): power limit rated / max (180 / 198 W on the
  5060 Ti, 600 / 600 W on the PRO 6000s, = HWiNFO), Total GPU Power [% of TDP], GPU Memory Usage, PCIe replay count;
  per-engine usage (D3D, Video Decode/Encode 0…n, Computing Compute_0/1/Cuda, VR, Graphics) and D3D memory dedicated /
  dynamic from Windows' GPU Engine / Adapter Memory counters, matched to each card by adapter LUID → PCI bus.
- **UPS**: Battery Voltage (27.3 V — CyberPower reports it in PowerSummary), Charging / Discharging.
- **WireView**: HWiNFO's six fault rows (chip/sensor temperature, total/wire over-current, power, imbalance) under
  Faults (Status) and Faults (Log); OpenHW's own imbalance % is now "Wire Current Imbalance".
- **HSMP**: nothing to add — CPU Package Power (HSMP) is HWiNFO's CPU PPT, the "Frequency Limited by …" rows cover the
  throttle flags; HSMP gives neither UCLK nor the read/write bandwidth split.

- **Super I/O NCT6798D** (2079199, owner-approved): voltages, fans and SYSTIN, named by the WRX90E board profile as on
  Linux; within an ADC step of HWiNFO. HWiNFO's Temp2/5/9, CPU, CPU (Weighted Value), VIN9 and VHIF aren't read
  (registers not confirmed).

**Left, and why**: Memory Timings — the UMC timing registers' SMN offsets for Zen 5 aren't documented to us (no guessing at
SMN reads); the configured timings show in BIOS Setup. EC VRM Vcore voltage / current — the WRX90E EC's register
offsets aren't documented to us. DIMMs — the AMDI2C one-boot test. Not reachable with signed modules or NVML: the
PM-table rows, the chipset temperature, NVIDIA's private-API rows. PresentMon — out of scope.

Legend: **✓** covered · **≈** covered under other names · **gap** missing (with what it would take).

| HWiNFO group | OpenHW | Gaps |
|---|---|---|
| System: ASUS (9) | ✓ System (7) | Virtual Memory Available, Page File Total / Used — easy (Windows memory APIs) |
| CPU [#0] 9975WX (293) | ≈ CPU (135): core clocks, effective clocks (per core), usage per thread | Core VID ×32, Core Ratio ×32, Bus Clock (≈99.8 MHz), effective clock **per thread** ×64, "Utility" per thread ×64 (PDH `% Processor Utility`) |
| C-State Residency (97) | ✓ (132 rows) | Package C6 residency; per-core CC6 ×32 (OpenHW shows per thread) |
| Enhanced (111) | ≈ Enhanced (25): Tctl, CCD1–4, RAPL, PPT limit, HSMP limits/clocks | **Not reachable with signed modules** (SMU PM table): per-core and L3 temperatures, CPU die average, IOD hotspot/average, SVI3 rails (VDDCR_CPU/SOC/VDDIO temperatures, voltages, currents, powers), per-core power ×32, TDC/EDC and their limits, thermal-limit %. **Possibly via HSMP**: CPU PPT (power), UCLK, DRAM read/write bandwidth split, HTC/PROCHOT flags (HSMP has throttle reasons — check against its message list) |
| Memory Timings (9) | — | Memory clock/ratio, tCL/tRCD/tRP/tRAS/tRC/tRFC, command rate — UMC registers over SMN (check AMDFamily17's SMN allowlist first); low value |
| Nuvoton NCT6798D (29) | — | Super I/O (next steps #8): mostly duplicates the BMC's rails and fans; unique: Motherboard temp, Temp1–3, CPU (weighted), VIN rails, VHIF, AVSB/3VCC/VTT. PawnIO's signed LpcIO module |
| AMD Chipset (1) | — | Chipset (xHCI) temperature — not reachable with signed modules |
| Infineon XDPE19283B (11) | ✓ VR (9) — page 1 since 2026-11-18 | none by name |
| ASUS EC (11) | ≈ EC (3): VRM1/VRM2 temperature, USB thermistor | T_Sensor (BMC has TR1), **VRM Vcore1/2 voltage and current, VRM Vcore temperature** (more EC registers — need the WRX90E's offsets; read-only, one documented register at a time), M.2 fan / USB4 fan (in the BMC group) |
| DDR5 DIMM ×6 (13 each) | — | DIMMs (next steps #2: the AMDI2C one-boot test). HWiNFO reads **6 of 8** (channels A, B, C, D, F, G): hub + 2 thermal sensors, PMIC rails (SWA/SWB/SWC, 1.8 V/1.0 V LDOs, VIN), total power, PMIC flags. The BMC group has all 8 DIMM temperatures |
| S.M.A.R.T. ×8 | ≈ Drive groups (28–31 rows each, more than HWiNFO) | names only: Remaining Life (= 100 − Endurance Used), Drive Failure / Warning (= Critical Warning Flags), Total Host Writes / Reads (= Lifetime Data Written / Read) |
| Drive ×8 | ✓ (rates, activity) | Read / Write Total since boot — OpenHW has "Read Total Since Device Start"; add "Write Total" if missing |
| IPMI: ASUS (23) | ≈ BMC (36, more than HWiNFO) | names only (HWiNFO appends "(System Board)" / "(Fan)") |
| dGPU RTX PRO 6000 ×2 (122 each) | ≈ GPU (37 each): temp, fans, power, limit, clocks, loads, memory, PCIe link/errors, perf limits | **NVML doesn't expose**: memory junction, hot spot, per-chip memory temperatures (16 front/back), core voltage, PCIe-slot / 12VHPWR input voltages and powers, NVVDD/MSVDD rail powers, measured clocks, crossbar/system clocks — HWiNFO reads these through NVIDIA's private APIs. **Feasible**: power limit rated / max (NVML constraints), per-engine video decode/encode, D3D usage and D3D dedicated/dynamic memory (Windows GPU Engine / Adapter Memory counters), total power % of TDP, PCIe recovery count and per-lane errors (NVML field values, check), Bus Load |
| dGPU RTX 5060 Ti (91) | ≈ GPU (35) | as above |
| dGPU ASPEED AST2600 (1) | — | the BMC's video adapter; not worth a group |
| Battery + UPS (11) | ✓ UPS (12) | Battery Voltage (27.3 V — a HID usage, easy); AC Power / Charging / Discharging flags (≈ On Battery) |
| Thermal Grizzly WireView Pro II (36) | ✓ (HWiNFO layout since 2026-11-18; missing from this dump only because the running app held the COM port) | fault rows: HWiNFO lists each fault (chip/sensor temperature, total/wire over-current, over-power, imbalance) as status and log Yes/No; OpenHW shows two counts with the names in the tooltip |
| PresentMon (23) | — | frame rates / frame times (a gaming overlay feature) — out of scope unless wanted |
| Network X710-TL (8) | ✓ (Ethernet 4 + 5) | none |
| WHEA (1) | ✓ (3) | none (HWiNFO: Total Errors; OpenHW: corrected since boot / 30 days, fatal 30 days) |

## Suggested order (owner to choose)

1. **Quick, Windows-only, no new hardware access**: page file and virtual-memory rows; UPS battery voltage and AC /
   charging flags; GPU power limit rated/max, per-engine decode/encode, D3D usage and memory; per-thread effective
   clock and utility; drive name aliases where HWiNFO's names are clearer.
2. **HSMP check**: which of PPT power, UCLK, DRAM read/write and throttle flags HSMP's documented messages give.
3. **EC VRM Vcore voltage/current/temperature**: needs the WRX90E EC's register offsets (compare LibreHardwareMonitor's
   ASUS EC tables); read-only, one documented register at a time.
4. **Super I/O** (#8) and **Memory Timings**: low value.
5. **DIMMs**: the AMDI2C one-boot test (#2).
6. Not reachable with signed modules: PM-table rows (per-core temps and power, SVI3 rails, IOD), chipset temperature,
   NVIDIA's private-API rows.
