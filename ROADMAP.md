# OpenHW roadmap

Planned work, roughly in priority order within each section. openfan-linux already implements most write
features; port the logic (copy, never reference) and expose it as validated `openhw-helper` commands, keeping
the GUI unprivileged.

## Write capabilities (via openhw-helper)

Each write is a fixed helper command with strict argument validation, a readback check, and a clear error
when firmware clamps or rejects the value. Nothing a client sends may become a path or raw register.

- [ ] **GPU power limit** — NVML `nvmlDeviceSetPowerManagementLimit` within the card's min/max constraints;
      persisted and re-applied at startup. Source: openfan-linux `NvmlBackend.SetPowerLimit`, helper `power`.
- [ ] **CPU socket power limit (PPT)** — `amd_hsmp_hwmon` `power1_cap` (HSMP SET_SOCKET_POWER_LIMIT), live
      limit plus optional "keep after reboot" boot service. Source: `CpuPowerControl`, `HsmpLimitConfig`,
      `CbsSetupReader` (BIOS default), helper `cpupower` / `cpuboot`.
- [ ] **CPU maximum frequency** — cpufreq `scaling_max_freq` ceilings per policy (never boost, governor, EPP
      or minimum). Source: `CpuFrequencyControl`, `CpuFrequencyMonitor`, helper `cpufreq` / `cpufreqbatch`.
- [ ] **Fan control** (to decide) — hwmon PWM and NVIDIA fan speed with restore-to-auto on disconnect.
      Source: openfan-linux `HwmonBackend` / `NvmlFanWriter` / `FanController`.

## Root-only readings (via openhw-helper)

- [ ] **RAPL** package/core energy → power (`/sys/class/powercap/intel-rapl:*/energy_uj`, root-only).
- [ ] **MSR** reads: effective clocks (APERF/MPERF), per-core power, package C-states.

## Resource use

Measured 2026-10-22 on the TRX50 workstation (per 2 s poll, background thread): NVIDIA ~156 ms wall / ~37 ms CPU,
hwmon ~74 ms wall / ~4 ms CPU, everything else under 2 ms combined. Two reads dominate:
NVML PCIe throughput (~42 ms per GPU — NVML samples each counter for 20 ms) and NVMe hwmon temperatures (5–19 ms
per drive — each read is an admin command to the drive, which can also wake it from a low-power state).

- [ ] **Collapsed groups stop reading.** When a group (or a collapsible row such as Core Clocks) is collapsed,
      skip its hardware reads on later polls, unless one of its sensors is shown as a tray icon (or, later,
      drives a fan curve). Biggest savings: collapsed GPU groups (PCIe throughput) and drives (fewer NVMe
      wake-ups). Engines take a "requested ids" set, as openfan-linux's backends already do (`ReadInto(readings,
      requested)`). Min/Max/Average pause while collapsed; show that (e.g. dimmed values) rather than mixing
      in a gap silently. Persist collapsed state so the saving survives restarts.
- [ ] Read PCIe throughput on a slower cadence than the rest of the GPU rows (or only while visible).

## Window and tray

- [ ] Choose which sensors get tray icons, with per-icon colours (HWiNFO "Tray Icon" settings tab).
- [ ] Settings dialog modelled on `reference/` (polling period, units, shown columns, row shading, highlight).
- [ ] Column resizing; remember window size, expanded groups and hidden sensors.
- [ ] CSV logging.
- [ ] Single instance (a second launch should show the running window, not add a second tray icon).
- [ ] Optional start at login (GNOME autostart entry), minimised to tray.
- [ ] Friendly labels for Super I/O inputs (`in0`…`in17`, `AUXTIN*`) on known boards.
