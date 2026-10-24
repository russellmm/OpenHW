# Changelog

Versioning: semantic, pre-1.0 — **minor** for a round of new features, **patch** for fixes only. The version is set
once, in `Directory.Build.props`; each release is tagged `vX.Y.Z`. The app shows `X.Y.Z · <git revision>`.

## 0.5.0 — 2026-10-23

**Alerts**
- Per-sensor rules: above / below a value (in the sensor's unit) for a chosen time, or "becomes Yes/No" for Yes/No
  sensors. Firing shows a desktop notification kept until dismissed and turns the sensor's Current cell red; a "back
  to normal" notification follows. Sensor menu → Alert settings…; File → Alerts… lists every rule with its state.

**Sensors**
- Core effective clocks and their average (kernel APERF/MPERF average, no root).
- CPU Package Power (RAPL) and CPU Cores Power (RAPL), through openhw-helper v6 (`rapl`).
- GPU: 1 s average power and the PCIe error counters (NVML field values). Hotspot and memory-junction temperatures
  aren't available through NVML on these cards.
- The Super I/O shows as Nuvoton NCT6701D, as HWiNFO names it.

## 0.4.0 — 2026-10-23

- Logging is opt-in: no sensor is logged until you choose Enable Logging on it, so logs only hold what you want.
  Starting a log with none chosen explains how to pick sensors instead of writing an empty file.
- Tray icons have their own window, File → Tray Icons… (also in the tray menu and a sensor's "Tray icon
  settings…"): icons, order, colors, custom colors and the sensor search. Settings keeps the OpenHW-wide options
  (poll interval, start at login).

## 0.3.1 — 2026-10-23

- Fix: column header edges couldn't be dragged. The handles had no visual template, so they never received the
  pointer; they're now hit areas that show a faint line on hover. Tested with a simulated mouse drag.

## 0.3.0 — 2026-10-23

**Sensor right-click menu, filled in**
- Rename (F2): your own name for a sensor, shown in the window, tray, Settings, graphs and logs; sorting and
  explanations keep OpenHW's name, and the tooltip notes the original.
- Hide / Unhide (Shift+Del); View → Show Hidden Sensors and Unhide All Sensors.
- Disable / Enable monitoring (Del / Ins): the sensor isn't read and its values are blank.
- Show Graph: a live 30-minute graph per sensor (history kept for every sensor read).
- Enable / Disable Logging, with File → Start Logging… / Stop Logging writing a CSV (Date, Time, every monitored and
  logged sensor in window order), one row per poll.
- Choices are saved in `~/.config/openhw/sensors.json`.

**Window**
- Column resizing: drag a header edge (double-click for the default); long names trim with "…".
- The window's size, position and maximized state are remembered.

**Other**
- `install-helper.sh` asks for a reboot only on a first install.
- The version stamp shows the commit hash even on a tagged commit.

## 0.2.0 — 2026-10-23

**Controls (through openhw-helper)**
- Power modes: switch power-profiles-daemon's profile from the footer, Power menu and tray; when boost comes back
  on, cores return to their OpenHW cap or the hardware maximum (fixes the 4.0 GHz clamp after power-saver).
- CPU frequency caps, all-core or per core (both SMT threads), with readback and rollback; optional re-apply when
  OpenHW starts.
- Power limits: CPU socket PPT (HSMP, 50 W to the SMU's maximum) and each NVIDIA card's limit (NVML), live and at
  boot (hsmp-control's `ppt_mw`; an OpenHW drop-in for `nvidia-power-limit.service`). Shows what limits the CPU now.
- Helper v5: `cpufreqmax`, `cpufreqrestore`, `gpupower`, `cpuppt`, `cpupptboot`, `gpupowerboot`.

**Sensors**
- HSMP engine: FCLK, MCLK, core clock limit, active frequency limit and its 8 limit sources, C0 residency, DRAM
  bandwidth, PROCHOT.
- Nuvoton temperatures and fans named like HWiNFO (matched by behaviour); open pins hidden.
- in6 confirmed as CPU Core1 (Vcore1) by a per-CCD load sweep.
- Every sensor's tooltip explains what the reading means.

**Window and tray**
- Menu bar (File, View, Power, Help) with shortcuts; footer icon shortcuts; About window.
- HWiNFO's sensor right-click menu (Add/Remove from tray working, the rest marked "To be implemented").
- Settings: poll interval, start at login (optionally tray-only), single instance, tray icons with a searchable
  sensor list (no duplicates), order and colors, saved custom colors by hex code or color picker.
- Tray icons: up to 8, in Settings' order, bigger digits; each icon's menu shows its sensor's reading, min/max/avg
  and explanation, plus Move left/right, Remove and Color. Menus no longer close by themselves.

## 0.1.0 — 2026-10-22

- First release: HWiNFO-style Sensors Status window (device groups, Current/Min/Max/Average, collapsible groups,
  changed-value highlight, gray stale rows), one sensor engine per suite (hwmon, NVIDIA, NVMe, EDAC, system,
  network), CPU-temperature tray icon, openhw-helper for NVMe SMART, Super I/O voltages named and scaled for the
  TRX50-SAGE, user install script and app-grid entry.
