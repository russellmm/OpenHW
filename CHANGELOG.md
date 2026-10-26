# Changelog

Versioning: semantic, pre-1.0 — **minor** for a round of new features, **patch** for fixes only. The version is set
once, in `Directory.Build.props`; each release is tagged `vX.Y.Z`. The app shows `X.Y.Z · <git revision>`.

## 0.16.2 — 2026-10-26

- GPU Lending: **a notification when a new app opens the lent card.** With the filter on, any app that isn't on the
  list can open the card just by drawing with the GPU — and then the card can't be lent. The first time an app
  nobody has decided on holds it, OpenHW asks once: **Hide it** (now and from boot), **Close & reopen when
  lending**, or **Leave it** (it may use the card; no more questions). A click on the notification opens GPU Lending.
- Fixed: Hide didn't work for apps whose unit name systemd escapes (e.g. a "weather-widget" app runs as
  `app-gnome-weather\x2dwidget-….scope`): the helper refused the backslash.

## 0.16.1 — 2026-10-26

- Fixed: the card filter couldn't be turned on by the installed helper — its sandbox (`ProtectKernelTunables`) left
  the kernel's BPF filesystem read-only, where the filter keeps its program. The unit now lists the kernel tunables
  it protects as read-only paths instead (`/proc/sys`, `/sys/kernel`, …). Checked with `tools/sandbox-bpffs-test.sh`.
- Fixed: a failed "turn the filter on" was still remembered as on (and retried at every boot); it now stays off.

## 0.16.0 — 2026-10-26

**Ready to replace RTXinVM (GPU lending, step 5 — code)**
- openhw-helper now starts before the login screen and tells systemd when it's ready (`Type=notify`). With the card
  filter on, it first waits (up to 60 s) for the lent card's device files, so the filter is in force before GNOME
  starts — "hide from boot" is airtight from the first login. With the filter off, it's ready at once; it always
  reports ready, even after an error, so it can never hold up the login screen.
- GPU Lending: **Close and reopen when lending**, per app. An app holding the card no longer has to be closed by
  hand: tick it (e.g. OpenFan's window) and lending closes it gracefully (by stopping its app scope), reopens it
  with the same command once the card is handed over, and again when the card comes back. The new scope is named
  like the original, so the list entry covering it still does. Apps that aren't ticked still block the lend.

## 0.15.1 — 2026-10-25

First real lend (Windows-10-LTSC on the RTX 6000) worked; two fixes from it:
- Fixed: a user service started with `systemd-run` (e.g. OpenFan's window under RTXinVM) wasn't started again after
  the lend. systemd reports a FragmentPath even for transient units, so it was taken for a unit file and only
  `systemctl start`ed — but a stopped transient unit is gone. Transient units (`Transient=yes`) are now recreated
  with the same command, slice and description, after the lend and after the return.
- Fixed: while the card was lent, the page showed the libvirt hook's handoff record as a problem; the hook keeps it
  on purpose until the return.

## 0.15.0 — 2026-10-25

**Lend the card to a VM (GPU lending, step 4)**
- Power → GPU Lending… now has a **Lend** section: choose the VM (only libvirt VMs with the card assigned are
  offered), see what stands in the way, then **Lend to <VM>** and later **Return**.
- Lend refuses while anything would stop the handoff — the VM not off, a monitor plugged into the card (or a
  framebuffer from one at boot), a leftover handoff record, or an app holding the card (close it, or hide it and
  restart it) — and names it. What can be paused is paused for you and started again afterwards: the desktop user's
  services holding the card (by OpenHW's window; transient `systemd-run` units are recreated with the same command
  and slice) and system services (by openhw-helper, which also lets go of its own NVML handles and turns
  persistence off so nvidia-persistenced lets go). Then it puts back the card's power limit and persistence for the
  libvirt hook to record and starts the VM; any failure puts everything back.
- Return asks the VM for a normal shutdown (guest agent, else ACPI; never forced). However the VM is shut down, the
  helper notices the card is back, reopens NVML and restarts what it paused; OpenHW restarts the user's services
  so they list the card again (even with the GPU Lending window closed).
- Helper protocol 10: `lendstatus`, `cardvms`, `cardvm`, `lend`, `vmreturn`.

## 0.14.1 — 2026-10-25

- Fixed: the GPU Lending page still couldn't see the desktop user's programs holding the card — listing another
  user's `/proc/<pid>/fd` (mode 0500) needs `CAP_DAC_READ_SEARCH` as well as `CAP_SYS_PTRACE`; the unit grants both.
- Programs outside the desktop session are listed one per program, so OpenHW's own helper and other services each
  get their own line.

## 0.14.0 — 2026-10-25

**GPU Lending page (GPU lending, step 3)**
- Power → **GPU Lending…** (and the tray menu): choose the card to lend, turn the card filter on or off, see what
  has the card open right now — grouped by the list entry that would cover it, with **Hide now** / **Hide from
  boot** — and edit the list (Hide now, Hide from boot, Remove). Programs that are hidden but still hold the card
  are flagged ("restart it", or for the desktop "log out once"); system services that hold it are named; OpenHW's
  own helper is explained (it lets go of the card for the handoff).
- While RTXinVM's guard is running, the page says it is protecting the card and the filter should stay off.
- Fixed: the helper couldn't see which of the desktop user's programs had the card open (reading another user's
  `/proc/<pid>/fd` needs `CAP_SYS_PTRACE`, which the unit now grants for that), so the list showed only its own use.

## 0.13.0 — 2026-10-25

**Card filter (GPU lending, step 2) — built and tested, off until the switch-over**
- openhw-helper can now keep one NVIDIA card away from listed programs, as RTXinVM's guard does today, but with an
  editable list instead of "everyone except rtxgpu": a small eBPF cgroup device program (assembled in C#, ported
  from RTXinVM) refuses opens of that card's device nodes to listed programs; everything else — terminals, vLLM,
  llama.cpp, nvidia-smi — keeps seeing it. A program that starts before the helper has looked at it is refused
  until it has, so hiding is airtight; new apps are classified within about 60 ms (inotify on the cgroup tree).
- The list starts with the GNOME desktop, the login screen and OpenHW's own window, each "hide from boot"; entries
  can also be hidden or shown just until reboot ("now"). The filter stays in force across helper restarts.
- Helper protocol 9: `cardfilter [on|off]`, `cardselect <pci|none>`, `cardhide|cardshow <pattern> <now|boot|both>
  [name]`, `cardforget <pattern>`; the status lists every process holding the card and the entry that covers it.
- `openhw-helper --selftest-card-filter <pci>` (root) checks it all in a throwaway cgroup beside the desktop: 11 of 11
  pass on this machine. It is **not enabled** here: RTXinVM's guard keeps protecting the card until OpenHW can also
  lend it (step 4). The setup page (step 3) comes next.

## 0.12.1 — 2026-10-25

- Fixed: **Exit (tray menu) crashed OpenHW** on the way out — an unhandled exception, sometimes an abort (exit code
  134) — which could also stop Settings → Restart OpenHW now from starting the new instance. Two causes: Avalonia's
  own session-bus connection (its settings watcher) reported its closing to a UI thread that had already stopped,
  and a queued power-mode update tried to refresh tray menus that had just been removed. OpenHW now closes that
  connection while the UI thread still runs, and late tray updates are dropped. Checked with a new test,
  `tools/tray-exit-test/run.sh` (private display and session bus, Exit clicked over D-Bus as GNOME does): 14 of 14
  runs failed before, 12 of 12 exit cleanly now.
- Avalonia's global-menu export over D-Bus is off (GNOME doesn't show it; the menu bar is in the window).

## 0.12.0 — 2026-10-25

**The window no longer opens any GPU** (helper protocol 8 — install the .deb)
- Every NVIDIA reading (the GPU groups, and the Power Limits window's list) now comes from openhw-helper, which runs
  NVML as a system service; the window asks it over the socket (`gpus`, `gpuread`, `gpulimits`). The window doesn't
  load NVML at all.
- Why: a system service is never hidden from a card by a device filter on the desktop session, so OpenHW sees every
  GPU however it's started — RTXinVM no longer needs to launch it through `rtxgpu`, and the window can't hold a card
  that's about to be lent to a VM. While the helper restarts (e.g. around a GPU handoff) the GPU rows stay and go
  gray instead of disappearing.
- First step of folding RTXinVM's GPU lending into OpenHW (ROADMAP "GPU lending to a VM").

## 0.11.1 — 2026-10-25

- Fixed: hovering a footer button dropped its frame and flattened it (Avalonia's default). It now keeps the frame
  and lightens the face, as HWiNFO's do; pressed darkens it.

## 0.11.0 — 2026-10-25

**Footer like HWiNFO's**
- The bottom bar now matches HWiNFO's: expand all / collapse all on the left; the elapsed time, reset (clock),
  **settings (gear, new)** and close on the right. The icons are redrawn after HWiNFO's — light-blue outward arrows,
  steel-blue inward arrows, a white clock face, a silver gear, a glossy blue X — on larger buttons.
- The gear opens File → Settings for now; it will lead to a HWiNFO-style settings menu next.
- Removed from the footer: the power-mode list, CPU frequency and power limit buttons and the "Capped at …" button.
  They're all in the Power menu (and the tray menu); a failed power action still shows a short message in the footer.

## 0.10.1 — 2026-10-25

**Works with RTXinVM's launcher**
- Fixed: saving any setting rewrote OpenHW's login entry, replacing a launcher another tool had put there — RTXinVM's
  `rtxgpu --app openhw`, which starts OpenHW in the slice that can see the VM's GPU and lets RTXinVM stop it before
  switching the card. OpenHW now keeps such an entry and never rewrites it; "Start OpenHW when I log in" turns it
  off and on in place, and Settings shows the command it runs.
- Fixed: Settings → Restart OpenHW now, under a systemd user service such as `rtxgpu-openhw.service`, would have
  closed OpenHW for good (systemd stops the unit and kills what's left in it when the main process exits). A small
  waiter outside the unit now starts the same unit again — same name, slice and description — once it has stopped.

## 0.10.0 — 2026-10-25

**DIMM and VR sensors** (helper protocol 7 — install the .deb)
- Each DDR5 DIMM is now its own group, as in HWiNFO ("DDR5 DIMM [#0]: Kingston KF556R28-32 (AMDI0010:01 / 0x50)"):
  SPD hub temperature, TS0 and TS1 temperatures, the PMIC's VDD (SWA), VDDQ (SWB) and VPP (SWC) rails, 1.8 V and
  1.0 V LDOs, VIN, total power (or per-rail current/power, as the PMIC is set), and its high-temperature,
  over-voltage and under-voltage alarms.
- The board's memory-controller regulator, **Infineon XDPE19283B** (TRX50-SAGE): loop temperatures, VOUT, VIN, IOUT,
  IIN, output and input power, efficiency.
- Both are read by openhw-helper over the SMBus (new `ddr5` and `vr` commands; the unit may now open `/dev/i2c-*`).
  Reads only, except the DIMM PMIC's ADC select register, which is restored after each reading and only touched on
  a PMIC matching the module's SPD. `openhw-helper --once <command>` prints one reply, for checking with sudo.
- Not done: SVI3 telemetry (needs the SMU PM table; the kernel's HSMP metric table doesn't cover this CPU) and the
  ASUS EC's VRM voltage/current (needs the EC register map) — see ROADMAP.

## 0.9.0 — 2026-10-25

**Window scale**
- Fixed: when OpenHW starts at login with only its tray icons, the window opened from the tray ("Show sensors") was
  drawn at 100 % instead of the desktop's scale. OpenHW starts before GNOME publishes the scale to X11 apps
  (`Xft.dpi`, about a second later), and Avalonia 11.2 noticed the change but kept its cached screens. Avalonia is
  now 11.3.22, which re-reads them, so the window follows the scale whenever it arrives (reproduced and checked on a
  private X server: 1× before, 2× after).
- File → Settings → **Window scale**: Automatic (the desktop's scale, shown as "now N %") or a fixed 100–400 % for
  every OpenHW window. Applies at the next start; a **Restart OpenHW now** button next to it does that at once.

## 0.8.0 — 2026-10-24

**Tray units**
- Tray tiles show a short unit after the digits: a degree sign for temperatures (32°, also °F set in Customize
  values), and a small W, V, A or % for power, voltage, current and load (55W, 87%). Other units (MHz, RPM, MB,
  MB/s…) stay digits only: they're long, and their values are already shortened ("4.5k"). The tile stays square —
  GNOME's tray squeezes wider icons — so the digits get a little smaller to make room.
- File → Tray Icons… → "Show units on the tiles" (on by default) turns it off again for every icon.

## 0.7.0 — 2026-10-24

**Tray hover**
- Hovering over a tray icon on GNOME shows its sensor's details — reading, min/max/average, device and the full
  explanation — updated live while the pointer stays; the menu still opens on a click and hides it. GNOME's tray
  shows no tooltips, so the package now installs a small GNOME Shell extension,
  `openhw-tray-hover@russellmm.github.io`, which shows the tooltip OpenHW already publishes.
- File → Tray Icons… → Hover: on/off (GNOME's own extension switch; off until you tick it). After the first install,
  log out and back in once — Wayland only finds new extensions at login; a box ticked before then is on at that login.
- The tooltip's explanation is one paragraph (the menu keeps its short lines), so long explanations aren't cut off.

## 0.6.0 — 2026-10-24

**Layout and values**
- View → Layout Settings… (and a sensor's "Layout settings…"): HWiNFO's Layout tab — every group and sensor with
  its monitoring state, Monitoring / Show for the selected sensor, move sensors within a group and groups among
  groups (top, up, down, bottom), the hidden sensors listed below, Restore Original Order. New sensors keep
  OpenHW's place.
- Sensor menu → Customize values…: label, unit, multiplier, offset and decimal places, with a live preview. Used
  everywhere the value appears (window, graphs, tray, logs, alert thresholds). "Multiply by" multiplies (HWiNFO's
  takes the inverse) and accepts "." or ",".

**Settings**
- Pause sensors while the window is hidden (off by default): only tray, alerted, logged and graphed sensors are
  read while the window is hidden or minimized.
- Draw windows in software (no OpenGL), from the next start: about 65 MB less resident memory, a little more CPU
  while a window is open.

**Memory**
- Runtime options: TieredPGO off, GC conserve-memory, and a 4 MB gen0 budget (the launcher re-executes itself to
  set `DOTNET_GCgen0size`). With the window open: 322 → 293 MB resident, 219 → 187 MB private.

**Packaging**
- Debian package: `packaging/build-deb.sh` builds `dist/openhw_<version>_amd64.deb` with the app and openhw-helper
  installed system-wide (`/usr/lib/openhw`, `/usr/bin/openhw`, the helper service, the menu entry). Each release
  ships one from now on.
- Start at login falls back to `/usr/bin/openhw` when there's no per-user launcher.

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
