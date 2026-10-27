# OpenHW roadmap

Planned work, roughly in priority order within each section. Writes are validated `openhw-helper` commands,
keeping the GUI unprivileged. Behaviour is derived from the hardware's own interfaces (ranges, readback) — not
copied from openfan-linux, which OpenHW never references.

## GPU lending to a VM (replaces RTXinVM) — agreed with the owner 2026-10-25

Goal: one root helper, no `rtxgpu` launcher. When the VM isn't using the card, every Linux program can use both RTX
6000s (vLLM, Unsloth Studio, llama.cpp, nvidia-smi…); the card is kept away only from programs the owner lists.

1. [x] **The window reads all GPUs through openhw-helper** (0.12.0; RTXinVM no longer launches OpenHW) (a system service, never blocked by a device filter).
   The app then never opens a card, runs as plain `/usr/bin/openhw --tray`, and keeps running through a handoff
   (the lent card's rows go stale, then return). Saves the app the NVIDIA driver's memory.
2. [x] **Card filter in openhw-helper** (0.13.0; built and self-tested, **off** until the switch-over in step 4/5 —
   RTXinVM's guard keeps doing the job until then): blocks *new* opens of the chosen
   card's nodes (`/dev/nvidiaN`, `/dev/dri/cardN`, `renderD*`) for listed programs. Unlike RTXinVM's allow-list, the
   program refuses any cgroup without a verdict and the helper marks every *leaf unit* not on the list allowed the
   moment it appears (inotify on cgroupfs + 2 s rescan) — so hiding is airtight and everything else still sees the
   card. Helper protocol 9: `cardfilter [on|off]`, `cardselect`, `cardhide|cardshow <pattern> <now|boot|both> [name]`,
   `cardforget`. Root self-test: `openhw-helper --selftest-card-filter <pci>` (throwaway cgroup beside user.slice).
   Still to do at switch-over: start the helper before the login screen (unit ordering + `Type=notify`). Granularity = how programs are
   launched: an app from the grid/dock (`app-<id>-*.scope`), the GNOME desktop (`org.gnome.Shell@*.service`, which
   includes Xwayland), a terminal window's scope. A program that already has the card keeps it until it exits.
3. [x] **Setup page** (0.14.0: Power → GPU Lending…, also in the tray menu) (from the gear's settings menu / Power menu): the GPU to lend and the VM; **what's using it
   now** (live, every process with its nodes open, from the helper); per entry **Hide now** (until reboot) and
   **Hide from boot** (applied by the helper before the login screen). GNOME desktop: Hide from boot ticked by
   default; unticked → GNOME sees the card from the next boot (and the VM can't start until a logout frees it).
   Unhide is immediate for programs started afterwards. Shows "still holding the card — restart it" where needed.
4. [x] **Lend to Windows / Return** (0.15.0; `CardLending` in the helper, `LendCoordinator` in the app) (owner's rule: the VM does not start while anything still has the card open; the
   page names what). Optional: pause OpenFan if it's installed (independence rule: optional, no dependency). The
   libvirt hook keeps doing the handoff; return refreshes the helper's GPU list.
5. [x] **Retire RTXinVM** — switched over 2026-10-26 (0.16.1: helper before the login screen with `Type=notify`
      and a wait for the card's nodes; "Close and reopen when lending" for apps such as OpenFan's window; filter on;
      `rtxinvm uninstall` run). Verified 2026-10-26: after a reboot the desktop never opens the card and a terminal
      lists three GPUs; a full Lend → Return from the page with 0.16.3 (10:17 → 10:20) closed and reopened OpenFan,
      Chrome and the weather widget, and the hook restored 300 W + persistence.
6. [x] **Install / Uninstall from the page** (0.17.0, owner's request 2026-10-26): uninstalled, nothing of lending
      is in the kernel or at boot (filter, boot-order drop-in, GNOME udev rule, libvirt hook put away); install puts
      it all back with the owner's choices. Verified 2026-10-26: uninstall → reboot (plain boot: no drop-in, rule, hook or
      filter; GNOME used E1) → install → reboot (helper ordered before gdm, filter attached, desktop off E1) → Lend → Return.

## Write capabilities (via openhw-helper)

Each write is a fixed helper command with strict argument validation, a readback check, and a clear error
when firmware clamps or rejects the value. Nothing a client sends may become a path or raw register.

- [x] **GPU power limit** — NVML, within each card's constraints, read back (helper `gpupower`). Live until reboot.
- [x] **CPU socket power limit (PPT)** — `amd_hsmp_hwmon` `power1_cap`, 50 W to the SMU's reported max, read back
      (helper `cpuppt`); shows the active frequency limit and its source. Live until reboot.
- [x] **Keep power limits after reboot** — in the existing boot services' config: hsmp-control's `ppt_mw` (100–300 W)
      and an OpenHW drop-in for `nvidia-power-limit.service` (helper `cpupptboot` / `gpupowerboot`).
- [x] **Settings window** — poll interval, start at login (`--tray`), tray icons (sensor + colour, up to 8),
      single instance.
- [x] **Power modes** — switch power-profiles-daemon's profile from the footer and tray, and lift the base-clock
      cap left after power-saver (helper `cpufreqrestore`). See README "Power modes and CPU frequency caps".
- [x] **CPU maximum frequency** — all-core and per-core `scaling_max_freq` caps (helper `cpufreqmax`), with
      readback and rollback; caps set here survive power-saver. Live until reboot.
- [x] **Keep CPU caps after reboot** — optional; OpenHW re-applies saved caps when it starts (with Start at login).
- [x] **Where the 4.0 GHz clamp comes from**: not the boot sequence (booted in power-saver, switched to Performance
      with both apps closed → full 5.49 GHz). It's a 4.0 GHz value stored while boost is off, e.g. OpenFan's
      Hardware maximum pressed in power-saver.
- [ ] **Fan control** (to decide) — hwmon PWM and NVIDIA fan speed with restore-to-auto on disconnect, from the
      hwmon `pwmN`/`pwmN_enable` interface and NVML's fan-control calls.

## Root-only readings (via openhw-helper)

- [x] **RAPL** package/core energy → power, through the helper (0.5.0).
- [x] **Effective clocks** from the kernel's APERF/MPERF average (`cpuinfo_avg_freq`, no root) (0.5.0).
- [ ] **Per-core power** (AMD core-energy MSR) — needs CAP_SYS_RAWIO in the helper; owner to decide.

## More sensors (HWiNFO parity)

Compared against the HWiNFO captures in `reference/`. Method for board mappings: [docs/board-profiles.md](docs/board-profiles.md).

- [x] **Super I/O voltages named and scaled** on the TRX50-SAGE (in0–in16; in17 hidden) — `BoardProfiles`.
- [x] **in6 CPU Core1 confirmed** (×1.11) by a per-CCD load sweep — see docs/board-profiles.md.
- [ ] **Confirm in13 CPU VSOC** (×1.11) with a BIOS SoC-voltage change. in15 VIN9 follows temperature, not load.
- [x] **Super I/O temperatures and fans named** on this board, by behaviour (docs/board-profiles.md). Still inferred:
      CPU (PECI/TSI calibration) and CPU (Weighted Value) (Virtual_TEMP) — confirm with a longer cross-OS run.
- [x] Show the chip as HWiNFO names it (Nuvoton **NCT6701D**) (0.5.0).
- [ ] Profiles for more boards (same method; contributions need a HWiNFO capture + raw Linux values).
- [ ] **CPU "Enhanced" telemetry** (HWiNFO reads AMD SMU/SVI3, not the Super I/O):
  - AMD HSMP metrics: the kernel already exposes many without root on the HSMP platform device
    (`/sys/devices/platform/AMDI0097:00/`): `fclk_input`, `mclk_input`, `cclk_freq_limit_input`, `clk_fmax`/`clk_fmin`,
    `c0_residency_input`, `ddr_max_bw`/`ddr_utilised_bw_input`, `prochot_status`, `pwr_current_active_freq_limit`
    and its `_source` — **done** (`HsmpEngine`, CPU Enhanced group). The full table (`metrics_bin`) is root-only.
  - SVI3 rail telemetry (VDDCR_VDD / VDDCR_SOC voltage, current, power, TDC/EDC, VRM temperatures): **deferred**
    (owner, 2026-10-25). Checked: `metrics_bin` answers "Operation not supported" here — the kernel only decodes
    the HSMP metric table for protocol 6 (MI300); this CPU is protocol 7. The SMU PM table needs the out-of-tree
    `ryzen_smu` module; research its Threadripper 9000 support before committing to it.
  - Per-core and L3 temperatures, IOD hotspot/average, per-core power, L3 clocks.
- [x] **VR controllers**: Infineon XDPE19283B (loop temperatures, VIN, VOUT, IIN, IOUT, PIN, POUT, efficiency) over
      PMBus through the helper's `vr`, PMBus reads only (0.10.0). No kernel driver covers the 19283B; its place
      (PIIX4 port 0, 0x20) is in `BoardVrs`, confirmed by IC_DEVICE_ID on every read.
- [x] **DDR5 PMIC rails** per DIMM (VDD/VDDQ/VPP, 1.8V/1.0V VOUT, VIN, total power, PMIC alarms) and the TS0/TS1
      sensors, through the helper's `ddr5` (0.10.0). Reads, plus the PMIC's ADC select (R30) — owner-approved, the
      one register HWiNFO also sets — only on a PMIC whose vendor id matches the SPD's, restored after each sweep.
- [ ] **ASUS EC VRM data**: VRM Vcore0/1 VOUT, IOUT, POUT (HWiNFO shows them); Linux's `asus_ec_sensors` exposes
      only temperatures and fans on this board — needs the EC register map for this model, and a way to call the
      board's ACPI BREC method for registers the driver doesn't list (acpi_call DKMS or a driver patch). Deferred
      (owner, 2026-10-25).
- [x] **GPU extras** where NVML has them: 1 s average power, PCIe error counters (0.5.0). Hotspot / memory-junction
      temperatures aren't in NVML's public interface (memory temperature reports "not supported" on these cards).

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
- [x] Pause reads while the window is hidden, optionally (Settings; off by default — HWiNFO keeps statistics running) (0.6.0).

### Memory

Measured 2026-10-22 (b68a473, everything collapsed, window open): ~0.7% of one core, ~310 MB resident
(~190 MB private). Breakdown: ~130 MB .NET runtime (GC heap, JIT code, 35 MB W^X double-mapping), ~70 MB
NVIDIA OpenGL driver mappings (Avalonia renders through GL), ~98 MB shared libraries.

- [x] **Runtime settings** (0.6.0; measured 2026-10-24, window open, 90 s median): TieredPGO off, GC
      conserve-memory 7 and a 4 MB gen0 budget (environment only — the launcher re-executes itself to set it):
      322 → 293 MB resident, 219 → 187 MB private, no extra CPU. Tried and dropped: W^X off (the 34 MB
      double-mapping just moves into anonymous memory), non-concurrent GC (no gain), heap hard limit and
      `MALLOC_ARENA_MAX=2` (no gain); `System.GC.Gen0Size` in runtimeconfig is ignored.
- [x] Optional software rendering (Settings, next start) (0.6.0): 293 → 228 MB resident, +0.8% of a core while a
      window is visible.
- [ ] **Native AOT later**: compile ahead of time to remove the JIT and most runtime overhead (large memory and
      startup win). Needs trim-safe code first: replace reflection `Binding("Name")` in the row templates with
      compiled bindings, check Tmds.DBus and System.Text.Json (source-generated context for `ViewState`) and
      the P/Invoke declarations for AOT warnings.

## Window and tray

- [x] Choose which sensors get tray icons, with per-icon colours.
- [x] Tray icon menus: Move left/right, Remove, Color submenu; saved custom colors (hex entry or color picker); panel order follows
      Settings; Settings sensor picker is searchable.
- [x] Sensor row right-click menu in HWiNFO's shape (Add/Remove from tray work; others grayed "To be implemented").
- [x] Row menu: Show Graph, Disable/Enable monitoring, Rename, Hide/Unhide, Enable/Disable Logging (0.3.0).
- [x] Alerts: per-sensor rules, desktop notifications, File → Alerts… (0.5.0).
- [x] Row menu: Layout settings (View → Layout Settings…: order, show/hide, monitoring) and Customize values
      (label, unit, multiply, offset, decimals) (0.6.0).
- [ ] OSD / RTSS / LG LCD are Windows-specific in HWiNFO — decide whether they have a Linux meaning.
- [x] Tray icon menus start with the sensor's reading, min/max/average and explanation; sensor tooltips explain
      every reading.
- [ ] Hover info for tray icons on GNOME (the AppIndicators extension shows no tooltips): a left-click info popup,
      or an OpenHW GNOME Shell extension with real hover tooltips. Deferred by the owner.
- [x] **HWiNFO settings menu, scaffolding** (0.18.0): footer gear → Sensor Settings (General, Layout, Customize,
      Tray Icon, Alerts) → Main Settings (General / User Interface, Safety, SMBus / I2C, Driver Management), laid out
      from `reference/settings_menu/`; tabs without a capture grayed. Nothing saved yet.
- [x] **Connect what OpenHW already has** (0.19.0): Polling Period, Layout, Customize, Tray Icon and Alerts items,
      Auto Start / Minimize on Startup, helper status — two-way with the older windows, which stay for now (owner).
- [ ] **Connect the rest of the settings menu** item by item (owner-led): each control to the matching OpenHW option, or grayed
      "To be implemented" where there is none yet; decide which Windows-only items (PresentMon, ADL, Intel ME, CSMI
      SAS, NVAPI, Driver Management) get a Linux meaning, a new name, or are grayed. Then decide what happens to
      File → Settings, Tray Icons… and Layout Settings… (they overlap with these tabs).
- [ ] More settings modelled on `reference/` (units, shown columns, row shading, highlight) — most are on the
      General tab now, waiting to be connected.
- [x] Column resizing; remember window size/position, expanded groups and hidden sensors.
- [x] CSV logging.
- [x] Single instance (a second launch shows the running window).
- [x] Optional start at login (autostart entry), optionally in the tray only.
