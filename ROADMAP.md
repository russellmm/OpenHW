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

- [x] **Collapsed groups stop reading.** Rows not on screen (collapsed groups and per-core lists, plus limits and
      alarm bits the window never shows) are not read; their Min/Max/Average freeze and show dimmed until
      expanded. Tray sensors stay live. Collapse state persists in `~/.config/openhw/view.json`.
      Measured per poll: all open 71 ms wall / 32 ms CPU; GPU + drive groups collapsed 3.7 ms / 2 ms.
- [x] PCIe throughput sampled every 10 s instead of every poll.
- [ ] Pause reads while the window is hidden (tray only), optionally — HWiNFO keeps statistics running.

## Window and tray

- [ ] Choose which sensors get tray icons, with per-icon colours (HWiNFO "Tray Icon" settings tab).
- [ ] Settings dialog modelled on `reference/` (polling period, units, shown columns, row shading, highlight).
- [ ] Column resizing; remember window size, expanded groups and hidden sensors.
- [ ] CSV logging.
- [ ] Single instance (a second launch should show the running window, not add a second tray icon).
- [ ] Optional start at login (GNOME autostart entry), minimised to tray.
- [ ] Friendly labels for Super I/O inputs (`in0`…`in17`, `AUXTIN*`) on known boards.
