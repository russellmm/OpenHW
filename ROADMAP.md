# OpenHW roadmap

Planned work, roughly in priority order within each section. openfan-linux already implements most write
features; port the logic (copy, never reference) and expose it as validated `openhw-helper` commands, keeping
the GUI unprivileged.

## Write capabilities (via openhw-helper)

Each write is a fixed helper command with strict argument validation, a readback check, and a clear error
when firmware clamps or rejects the value. Nothing a client sends may become a path or raw register.

- [x] **GPU power limit** — NVML, within each card's constraints, read back (helper `gpupower`). Live until reboot.
- [x] **CPU socket power limit (PPT)** — `amd_hsmp_hwmon` `power1_cap`, 50 W to the SMU's reported max, read back
      (helper `cpuppt`); shows the active frequency limit and its source. Live until reboot.
- [ ] **Keep power limits after reboot** (optional) — today `nvidia-power-limit.service` and `hsmp-control-apply`
      own the boot values; OpenHW could edit their config instead of adding a third boot path.
- [ ] **Settings window** (File or Tools menu) — tray sensors, colours, start at login, single instance.
- [x] **Power modes** — switch power-profiles-daemon's profile from the footer and tray, and lift the base-clock
      cap left after power-saver (helper `cpufreqrestore`). See README "Power modes and CPU frequency caps".
- [x] **CPU maximum frequency** — all-core and per-core `scaling_max_freq` caps (helper `cpufreqmax`), with
      readback and rollback; caps set here survive power-saver. Live until reboot.
- [ ] **Keep CPU caps after reboot** (optional) — re-apply the saved caps at boot or at OpenHW start.
- [x] **Where the 4.0 GHz clamp comes from**: not the boot sequence (booted in power-saver, switched to Performance
      with both apps closed → full 5.49 GHz). It's a 4.0 GHz value stored while boost is off, e.g. OpenFan's
      Hardware maximum pressed in power-saver.
- [ ] **Fan control** (to decide) — hwmon PWM and NVIDIA fan speed with restore-to-auto on disconnect.
      Source: openfan-linux `HwmonBackend` / `NvmlFanWriter` / `FanController`.

## Root-only readings (via openhw-helper)

- [ ] **RAPL** package/core energy → power (`/sys/class/powercap/intel-rapl:*/energy_uj`, root-only).
- [ ] **MSR** reads: effective clocks (APERF/MPERF), per-core power, package C-states.

## More sensors (HWiNFO parity)

Compared against the HWiNFO captures in `reference/`. Method for board mappings: [docs/board-profiles.md](docs/board-profiles.md).

- [x] **Super I/O voltages named and scaled** on the TRX50-SAGE (in0–in16; in17 hidden) — `BoardProfiles`.
- [x] **in6 CPU Core1 confirmed** (×1.11) by a per-CCD load sweep — see docs/board-profiles.md.
- [ ] **Confirm in13 CPU VSOC** (×1.11) with a BIOS SoC-voltage change. in15 VIN9 follows temperature, not load.
- [ ] **Name the Super I/O temperatures and fans** on this board. HWiNFO shows Motherboard, Temp2, CPU (Weighted
      Value), CPU Package, CPU, Temp9, TSI0 (CPU), Temp14 and fans Chassis 1, CPU, Chassis 2–4, Water Pump+.
      Fan speeds differ between OSes (different fan control), so match by behaviour (stop/start a fan, or
      watch which header follows a known fan) rather than by value alone.
- [ ] Show the chip as HWiNFO names it (Nuvoton **NCT6701D**; the Linux driver reports `nct6799`).
- [ ] Profiles for more boards (same method; contributions need a HWiNFO capture + raw Linux values).
- [ ] **CPU "Enhanced" telemetry** (HWiNFO reads AMD SMU/SVI3, not the Super I/O):
  - AMD HSMP metrics: the kernel already exposes many without root on the HSMP platform device
    (`/sys/devices/platform/AMDI0097:00/`): `fclk_input`, `mclk_input`, `cclk_freq_limit_input`, `clk_fmax`/`clk_fmin`,
    `c0_residency_input`, `ddr_max_bw`/`ddr_utilised_bw_input`, `prochot_status`, `pwr_current_active_freq_limit`
    and its `_source` (used by Power Limits) — add them as sensors. The full table (`metrics_bin`) is root-only.
  - SVI3 rail telemetry (VDDCR_VDD / VDDCR_SOC voltage, current, power, TDC/EDC, VRM temperatures): research
    `ryzen_smu` (out-of-tree module, PM table) support for Threadripper 9000 before committing to it.
  - Per-core and L3 temperatures, IOD hotspot/average, per-core power, L3 clocks.
- [ ] **VR controllers**: Infineon XDPE19283B (VR loop temperatures, VIN, VOUT, IOUT, POUT) over PMBus — Linux has
      PMBus drivers for the XDPE family; devices must be instantiated on the right I²C bus (root, via the helper
      or a one-time udev/modprobe setup). Read-only.
- [ ] **DDR5 PMIC rails** per DIMM (VDD/VDDQ/VPP, 1.8V/1.0V VOUT, VIN, total power, PMIC alarms) and the TS0/TS1
      sensors HWiNFO shows; Linux's `spd5118` driver exposes the hub temperature only. Research a safe read-only
      path (SMBus reads of the PMIC are root-only and must never write).
- [ ] **ASUS EC VRM data**: VRM Vcore0/1 VOUT, IOUT, POUT (HWiNFO shows them); Linux's `asus_ec_sensors` exposes
      only temperatures and fans on this board — needs the EC register map for this model.
- [ ] **GPU extras** HWiNFO shows: hotspot and memory-junction temperatures, per-rail voltages/powers, effective
      clocks, PCIe error counters — check NVML field values (`nvmlDeviceGetFieldValues`) on the 615 driver.

## Resource use

Measured 2026-10-22 on the TRX50 workstation (per 2 s poll, background thread): NVIDIA ~156 ms wall / ~37 ms CPU,
hwmon ~74 ms wall / ~4 ms CPU, everything else under 2 ms combined. Two reads dominate:
NVML PCIe throughput (~42 ms per GPU — NVML samples each counter for 20 ms) and NVMe hwmon temperatures (5–19 ms
per drive — each read is an admin command to the drive, which can also wake it from a low-power state).

- [x] **Collapsed groups stop reading.** Rows not on screen (collapsed groups and per-core lists, plus limits and
      alarm bits the window never shows) are not read; their Min/Max/Average freeze and show dimmed until
      expanded. Tray sensors stay live. Collapse state persists in `~/.config/openhw/view.json`.
      Measured per poll: all open 71 ms wall / 32 ms CPU; GPU + drive groups collapsed 3.7 ms / 2 ms.
- [x] PCIe throughput sampled every 10 s instead of every poll.
- [ ] Pause reads while the window is hidden (tray only), optionally — HWiNFO keeps statistics running.

### Memory

Measured 2026-10-22 (b68a473, everything collapsed, window open): ~0.7% of one core, ~310 MB resident
(~190 MB private). Breakdown: ~130 MB .NET runtime (GC heap, JIT code, 35 MB W^X double-mapping), ~70 MB
NVIDIA OpenGL driver mappings (Avalonia renders through GL), ~98 MB shared libraries.

- [ ] **Runtime settings first** (no code changes; measure before/after with the same collapse state):
      disable W^X double-mapping (`DOTNET_EnableWriteXorExecute=0` via the launcher, ~35 MB),
      `<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` and `<TieredPGO>false</TieredPGO>`,
      GC conserve-memory (`System.GC.ConserveMemory`), optionally a heap hard limit. Expect 40–60 MB less.
- [ ] Optional software rendering (`X11RenderingMode.Software`) as a setting: drops most of the ~70 MB driver
      mappings, at some CPU cost while the window is visible — measure both.
- [ ] **Native AOT later**: compile ahead of time to remove the JIT and most runtime overhead (large memory and
      startup win). Needs trim-safe code first: replace reflection `Binding("Name")` in the row templates with
      compiled bindings, check Tmds.DBus and System.Text.Json (source-generated context for `ViewState`) and
      the P/Invoke declarations for AOT warnings.

## Window and tray

- [ ] Choose which sensors get tray icons, with per-icon colours (HWiNFO "Tray Icon" settings tab).
- [ ] Settings dialog modelled on `reference/` (polling period, units, shown columns, row shading, highlight).
- [ ] Column resizing; remember window size, expanded groups and hidden sensors.
- [ ] CSV logging.
- [ ] Single instance (a second launch should show the running window, not add a second tray icon).
- [ ] Optional start at login (GNOME autostart entry), minimised to tray.
