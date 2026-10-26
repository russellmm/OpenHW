# OpenHW

HWiNFO64-style hardware monitor for Ubuntu (GNOME): a sensor status window plus per-sensor tray icons.
Design references are in [`reference/`](reference/) (HWiNFO64 v8.54 captures).

Built with Avalonia 11 / .NET 8. Self-contained: the sensor code started as a copy of openfan-linux's read paths
and now lives here, so changes to openfan-linux cannot break OpenHW.

## Install

As a Debian package (app and root helper together, system-wide):

```bash
packaging/build-deb.sh                                   # builds dist/openhw_<version>_amd64.deb, no root
sudo apt install ./dist/openhw_<version>_amd64.deb
```

The package installs the app in `/usr/lib/openhw`, `/usr/bin/openhw`, the menu entry and the `openhw-helper`
service, creates the `openhw` group and adds the installing user to it (reboot once after the first install). It
replaces a helper installed earlier with `install-helper.sh`. Remove with `sudo apt remove openhw`.

For a quick per-user build while developing:

```bash
packaging/install-app.sh
```

This one publishes a Release build to `~/.local/lib/openhw` (on the system drive, so it runs without the source tree),
links `~/.local/bin/openhw`, and adds an "OpenHW" entry with icon to the app grid. No root; re-run to update.
Drive health also needs the root helper — see [Root helper](#root-helper).

## Build and run from source

```bash
dotnet build
dotnet src/OpenHW.App/bin/Debug/net8.0/openhw.dll
```

## Tray icons

`src/OpenHW.App/Tray/` implements the StatusNotifierItem and dbusmenu D-Bus interfaces directly, so each
icon is a picture we draw (value on a colored tile, like HWiNFO). GNOME shows them through the
`ubuntu-appindicators` extension. Each icon has its own session-bus connection. Which sensors get icons, and their
colors, are chosen in **File → Tray Icons…** (up to 8; default one CPU-temperature icon on red); tray sensors stay
live even inside collapsed groups. Digits are drawn in a narrow bold face sized to the tile, followed by a short
unit when there is one — ° for temperatures (full size, "32°"), a small W, V, A or % ("55W", "87%"); other units
(MHz, RPM, MB…) are left off because the values are already shortened ("4.5k"). **Show units on the tiles** in Tray
Icons turns units off for all icons. Tiles stay square: GNOME's tray fits every pixmap icon into a square, so a wider
tile would only be shrunk (wide images work only for icons loaded from files, which would mean a new PNG per poll).

Each icon's right-click menu starts with its sensor: the reading, min/max/average, the device it belongs to and a
plain-language explanation — then **Move left** / **Move right**, **Remove from tray** and a **Color** submenu
(presets; a custom color shows ticked, and **Custom color…** opens Tray Icons). The info lines update in place
(dbusmenu `ItemsPropertiesUpdated`), so an open menu or submenu stays open. Icons appear in the panel in the Tray Icons window's
order, top = leftmost: GNOME's extension inserts each new icon left of the previous ones, so OpenHW registers them
last-first. On GNOME the icon size is the extension's `icon-size` setting (22 px on the workstation).

**Hover details.** The same information is the icon's tooltip (the SNI `ToolTip` property, re-sent after every
poll). KDE shows it on hover by itself; GNOME's AppIndicator extension never shows tooltips, so OpenHW ships its own
small GNOME Shell extension, `openhw-tray-hover@russellmm.github.io` (`packaging/gnome-extension/`, installed by the
.deb in `/usr/share/gnome-shell/extensions`). It finds OpenHW's icons in the panel (SNI ids `openhw-tray-*`), and
after a short delay shows the tooltip under the icon — the reading, min/max/avg, device and full explanation —
updating live while the pointer stays and hiding when the icon's menu opens. Turn it on or off with **File → Tray
Icons… → Hover**; the checkbox is GNOME's own extension state (the Extensions app shows the same switch). On Wayland,
GNOME only finds a newly installed extension at login, so after the first install it needs one logout/login; ticking
the box before that makes it start on at that login.

Every sensor row's tooltip in the main window also starts with what the reading means and when it matters
(`SensorExplanations`), before its source and id.

`Tmds.DBus.Protocol`'s `MessageWriter` is a `ref struct`: any helper that writes to it must take it by `ref`,
or its output is silently lost and the bus disconnects the app for sending a malformed reply.

## Settings and tray icons

**File → Settings** (also in the tray menu), saved in `~/.config/openhw/settings.json` and applied at once: poll
interval (1–10 s, default 2 s) and start at login (a freedesktop autostart entry, `~/.config/autostart/openhw.desktop`,
optionally with `--tray` to start with only the tray icons). If another tool has put its own launcher in that
entry — e.g. a wrapper that starts OpenHW in a particular systemd slice — OpenHW
keeps it: the box only turns the entry on or off (`X-GNOME-Autostart-enabled`/`Hidden`), and Settings names the
command. **Restart OpenHW now** works under such a launcher too: when OpenHW runs as a systemd user service, a
small waiter starts the same unit again (same name and slice) once the old one has stopped. OpenHW runs once per
session: a second launch (app grid, or the command) shows the running window and exits.

- **Pause sensors while the window is hidden** (off by default): while the window is hidden or minimized, only
  sensors in the tray, with an alert, being logged or with a graph open are read — as if every group were
  collapsed. Your expanded/collapsed layout is unchanged; when the window comes back, the other rows are gray for one
  poll and then live again. Their minimum, maximum, average and graph history skip the hidden time (HWiNFO keeps
  everything running, hence off by default). It saves the most when groups are left expanded.
- **Window scale** (default Automatic): Automatic follows the desktop — on GNOME, the display scale times the text
  scaling factor (GNOME publishes it to X11 apps as `Xft.dpi`). A fixed 100–400 % sizes every OpenHW window, menu and
  dialog instead. It applies when OpenHW starts, so a **Restart OpenHW now** button appears next to it after a
  change. (OpenHW sets Avalonia's `AVALONIA_GLOBAL_SCALE_FACTOR` / `AVALONIA_SCREEN_SCALE_FACTORS` for itself; with
  Automatic, ones you export yourself still apply.)
- **Draw windows in software (no OpenGL)** (off by default, applies at the next start): OpenHW doesn't load the GPU
  driver's OpenGL libraries. Measured 2026-10-24 with the window open: 293 → 228 MB resident, about 0.8% more of
  one core while a window is visible (nothing extra while hidden).

**Memory**: OpenHW sets three .NET runtime options for a smaller footprint — TieredPGO off and GC conserve-memory
(in the runtimeconfig), and a 4 MB gen0 allocation budget (`DOTNET_GCgen0size`; the launcher re-executes itself
once to set it, since the runtime reads it only from the environment — set the variable yourself to override, `0`
for the GC's default). Measured 2026-10-24: 322 → 293 MB resident, 219 → 187 MB private, no extra CPU.

**File → Tray Icons…** (also in the tray menu and a sensor's **Tray icon settings…**) has its own window: the icons —
sensor, color and order (↑ / ↓ = left / right in the panel) — the **custom colors** (add by hex code, `#1E90FF` or
`1e90ff`, or with **Pick…**, a color dialog with a hex field kept in step with the wheel; offered after the presets in
every icon's color list and tray Color submenu, labelled with their hex; ✕ removes one, icons using it keep it), and
a searchable sensor list: every word typed must appear in "Device › Sensor" ("gpu power", "6000 temp", "fclk");
Enter or double-click adds. Sensors already in the tray are grayed "(in tray)" and can't be added twice. **Show
units on the tiles** and **Hover** turn the tiles' units and the hover details on or off (see Tray icons).

## Sensors

`src/OpenHW.Hardware` has one engine per sensor suite (`ISensorEngine`), each with its own discovery and reads:

| Engine | Source |
|---|---|
| `HwmonEngine` | /sys/class/hwmon via libsensors (sysfs fallback): Super I/O, k10temp, HSMP power, SPD5118, NVMe and NIC temps |
| `HsmpEngine` | AMD HSMP platform device (no root): FCLK, MCLK, core clock limit, active frequency limit and its source, C0 residency, DRAM bandwidth, PROCHOT |
| `RaplEngine` | RAPL package and core energy → power, through openhw-helper (`rapl`; the counters are root-only) |
| `DimmEngine` | Per DDR5 DIMM: TS0/TS1, PMIC rails (VDD, VDDQ, VPP, 1.8 V, 1.0 V, VIN), power, alarms — helper `ddr5` (SMBus) |
| `BoardVrEngine` | A known board's PMBus VR (TRX50-SAGE: Infineon XDPE19283B, memory controller rail) — helper `vr` (SMBus) |
| `NvidiaEngine` | NVML: temps, fans, power, clocks, load, memory, video clock, PCIe link/throughput, encoder/decoder, P-state, limiters — runs in openhw-helper; the window gets it through `HelperGpuEngine` (`gpus`, `gpuread`) |
| `NvmeEngine` | NVMe SMART health and live activity |
| `EdacEngine` | Memory-controller corrected/uncorrected error counts |
| `SystemEngine` | Memory/swap, pressure stall info, load average, per-core clocks and usage, C-state residency |
| `NetworkEngine` | Download/upload rate and totals per physical interface |

`src/OpenHW.App/Sensors/SensorHub.cs` samples every engine once per poll (2 s by default); the window and tray share
that sample. The engines are read-only; writes (caps, power limits) go through the helper.

Super I/O inputs are named per board (`BoardProfiles`): on the Pro WS TRX50-SAGE WIFI A the voltages, temperatures
and fans carry HWiNFO's names, matched by measurement — evidence in [docs/board-profiles.md](docs/board-profiles.md).
Open-pin inputs HWiNFO doesn't list are hidden; mappings still inferred are flagged in their tooltips.

NVMe SMART needs root, so `NvmeEngine` reads it through OpenHW's own helper (below); without it, drive health
rows stay empty and everything else keeps working. RAPL package and core energy also come through the helper;
effective clocks come from the kernel's APERF/MPERF average (no root). Not read: per-core power (MSRs, would need
CAP_SYS_RAWIO in the helper).

**DIMMs and the board VR** are on the SMBus (`/dev/i2c-N`, root-only), so they come through the helper too. Each
DIMM is its own group, as in HWiNFO: the hub temperature (spd5118, no root), then TS0/TS1 and the PMIC. The helper
only uses buses where the kernel's spd5118 driver has bound a hub, and DDR5's fixed offsets from it (PMIC 0x48+n,
TS0 0x10+n, TS1 0x30+n). The PMIC's rail voltages need its ADC channel selected (register R30); that is the one
register OpenHW writes, only on a PMIC whose vendor id matches what the module's SPD declares, and it is put back
after each sweep. The board VR is read with PMBus reads only, after its IC_DEVICE_ID matches the board profile; its
PAGE is never changed. `tools/smbus-probe/` holds the one-time, read-only scripts that found these devices.

## Menu and shortcuts

The main window has a menu bar — **File** (Hide Window Ctrl+W, Start/Stop Logging…, Alerts…, Tray Icons…,
Settings…, Exit Ctrl+Q), **View** (Expand All Ctrl+E, Collapse All Ctrl+Shift+E, Show Hidden Sensors, Unhide All
Sensors, Layout Settings…, Reset Min/Max/Average Ctrl+R), **Power** (the power profiles, CPU Frequency… Ctrl+F,
Power Limits… Ctrl+L) and **Help** (About). New features get a menu entry first. The footer is HWiNFO's bar: expand
all and collapse all on the left; the time since the last reset, reset Min/Max/Average (clock), settings (gear) and
close on the right — power controls are in the Power menu and the tray menu only.

Right-clicking a sensor row opens HWiNFO's sensor menu, item for item:
- **Show Graph** — a live window per sensor with its last 30 minutes (OpenHW keeps that history for every sensor it
  reads; unread stretches are gaps) and Current/Min/Max/Average.
- **Add to tray / Remove from tray**, **Tray icon settings…**.
- **Disable / Enable monitoring** (Del / Ins) — the sensor isn't read and its values go blank (a tray icon or open
  graph keeps its own sensor live).
- **Rename** (F2) — your name everywhere the sensor appears; the tooltip keeps the original.
- **Hide** (Shift+Del) — View → **Show Hidden Sensors** brings hidden rows back marked "(hidden)" (right-click →
  Unhide), View → **Unhide All Sensors** clears them.
- **Enable / Disable Logging** — which sensors the next CSV log includes (opt-in: none until you enable them).
- **Layout settings…** — opens View → Layout Settings with this sensor selected (below).
- **Customize values…** — label, unit, multiplier, offset and decimal places for this sensor (below).
- **Alert settings…** — see [Alerts](#alerts).
- OSD, OSD (RTSS) and LG LCD settings are grayed "To be implemented" (Windows-only in HWiNFO).

These choices are kept in `~/.config/openhw/sensors.json`.

**Layout Settings** (View menu, or a sensor's **Layout settings…**) is HWiNFO's Layout tab: every group and sensor
with its Monitoring state, **Monitoring** / **Show** checkboxes for the selected sensor, and the order — ⤒ ↑ ↓ ⤓ move
a sensor within its group, or a whole group among groups. Hidden sensors are listed below (select one and tick Show
to bring it back); Rename… and Customize values… act on the selection; **Restore Original Order** goes back to
OpenHW's order. Per-core rows follow their parent row. A sensor or group that appears later (a new drive, a driver
update) keeps OpenHW's place for it.

**Customize values** (a sensor's **Customize values…**, or the button in Layout Settings) shows the sensor as
reading × multiplier + offset, in a unit and with decimal places of your choice ("Automatic" keeps OpenHW's) — e.g.
°C to °F: unit °F, multiply by 1.8, add 32. The dialog previews the current reading. The customized value is used
everywhere: the window, graphs, tray icons and menus, CSV logs and alert thresholds (an alert's threshold is in the
unit shown, so check alerts after changing a sensor's scale). Unlike HWiNFO, "Multiply by" really multiplies and
takes either decimal separator. Yes/No, P-state and link-width readings can't be customized. **OpenHW's values**
resets the dialog.

**CSV logging**: File → **Start Logging…** asks where to save (default `~/Documents/OpenHW/openhw-<date>-<time>.csv`)
and writes Date, Time and every sensor you enabled logging for (still monitored), in window order ("Device: Sensor [unit]"), one row per poll,
flushed as it goes; the footer shows "● Logging". Logged sensors are read every poll while logging. File → **Stop
Logging** ends it.

**Columns and window**: drag a column header's right edge to resize the column everywhere (double-click it for the
default width); long names trim with "…". Column widths and the window's size, position and maximized state are
saved in `~/.config/openhw/view.json`.

## Alerts

Right-click a sensor → **Alert settings…** to add rules: alert when it **is above** / **is below** a value (in the unit
the window shows) **for** a chosen time (at once, 5 s … 5 min — so one spike doesn't fire it), or, for Yes/No sensors,
when it **becomes Yes** / **becomes No**. When a rule fires OpenHW shows a desktop notification (critical, so GNOME
keeps it until dismissed) and turns the sensor's Current cell red; when it clears, a "back to normal" notification
replaces it. One notification per episode; sensors with alerts are read every poll. **File → Alerts…** lists every
rule with its live state (OK / ALERT / off), Edit… and Remove. Rules are kept in `~/.config/openhw/alerts.json`.

## Power limits

**Power Limits…** sets the CPU socket power limit (PPT) and each NVIDIA card's power limit, and shows, for the CPU,
what is limiting the cores right now (HSMP's active frequency limit and its source — e.g. "OPN Max", or "PPT" when
the power limit is what bites). Ranges come from the hardware: NVML's min/max per card, and for the CPU 50 W up to
the SMU's reported maximum (`power1_cap_max`); every write is read back, so a value firmware clamps shows as an
error, not as a limit that isn't real. **Default** puts a card back to NVML's default limit; **Boot value** puts
the CPU back to `/etc/hsmp-control/ppt_mw`. A live limit lasts until reboot.

**At boot** sets the value applied at every boot, in the existing boot services rather than a new one: the CPU's goes
in `/etc/hsmp-control/ppt_mw` for `hsmp-control-apply.service` (100–300 W, the range hsmp-control accepts — a value
outside it would silently not apply), and a card's goes in OpenHW's own drop-in,
`/etc/systemd/system/nvidia-power-limit.service.d/openhw.conf`, which repeats the unit's `nvidia-smi` lines with the
changed cards. The unit file itself is never edited; **Unit value** (or deleting the drop-in) returns to it. Without
the boot service installed, the row says so and stays read-only.

## Power modes and CPU frequency caps

The Power menu and the tray menu switch power-profiles-daemon's profile (Power saver / Balanced / Performance —
the same setting as GNOME's power menu; no root needed).

**CPU frequency** (Power → CPU Frequency…, or *CPU Frequency…* in the tray) caps every core at once or each physical core
separately; a core's cap goes to both of its SMT threads' cpufreq policies (`scaling_max_freq`). The window shows each
core's CCD, live clock and current cap ("(OpenHW)" for caps set here, "(other)" for caps from another tool). Caps
are written by the helper, checked against each policy's minimum and hardware maximum, read back, and rolled back
if the kernel doesn't keep them. They last until reboot; OpenHW remembers the ones it set for the current boot in
`~/.config/openhw/cpufreq-caps.json`. With **Re-apply these caps when OpenHW starts**, they are written back each time OpenHW starts —
after a reboot too, when Start at login is on (Settings); only the cores with a saved cap are touched.

Power saver turns CPU boost off and the kernel holds every ceiling at the base clock (4.0 GHz on the 9970X).
Leaving it normally restores the previous ceiling, but a ceiling stored while boost was off — e.g. a "hardware
maximum" written in power-saver, which is the base clock then — survives, and Performance silently stays at base
clock. So whenever a profile change (from OpenHW, GNOME or `powerprofilesctl`) turns boost back on, OpenHW puts
every core back to its OpenHW cap or, without one, the hardware maximum. A ceiling lower than that found any other
way is offered as **Remove Unexpected CPU Cap** in the Power menu and **Capped at …** in the tray menu, and fixed
only when clicked. Code:
`src/OpenHW.App/Power/`, `src/OpenHW.Hardware/CpuFrequency/`.

## Root helper

`openhw-helper` (`src/OpenHW.Helper`) is a small daemon for what Linux reserves for root. It listens on
`/run/openhw/helper.sock` (`0660 root:openhw`) and answers only fixed commands: `ping`,
`nvmesmart <controller> <identity>` (both arguments must match a drive the system enumerates),
`cpufreqmax <all|id,id,…> <kHz|max>` (every id must be an existing cpufreq policy and every value lie within that
policy's minimum and hardware maximum; `max` is each policy's `cpuinfo_max_freq`) and `cpufreqrestore` (every capped
policy to its hardware maximum) — see [Power modes and CPU frequency caps](#power-modes-and-cpu-frequency-caps) —
`gpupower <uuid> <mW|default>` (an enumerated card, within NVML's constraints), `cpuppt <mW|boot>`, the read-only
`gpus`, `gpuread` and `gpulimits` (every NVIDIA reading the window shows — the desktop app never opens a GPU itself,
so a device filter on the desktop session can't hide a card from OpenHW, and the app can't hold a card a VM needs),
`ddr5`, `vr`, `rapl`, the card filter (`cardfilter`, `cardselect`, `cardhide`, `cardshow`, `cardforget` — see
ROADMAP "GPU lending to a VM"; off unless turned on), and the boot
values `cpupptboot <mW|none>` and `gpupowerboot <uuid> <W|default>` (see [Power limits](#power-limits)). No client input becomes a path. It caps request lines at 1024 bytes, reads each drive
at most once per 10 s, and runs under a hardened systemd unit (only `CAP_SYS_ADMIN` + `CAP_CHOWN`; read-only
filesystem except `/sys/devices/system/cpu`, `/sys/devices/platform` (HSMP power cap), `/etc/hsmp-control` and
the nvidia-power-limit drop-in directory; no network; devices
limited to NVMe (read) and the NVIDIA control/card nodes NVML needs). Re-run the install below after updating OpenHW when the helper changes.

Install once, then log out and back in so your account joins the `openhw` group (reboot instead if systemd
lingering is enabled for your account — `ls /var/lib/systemd/linger` — since the user manager keeps its boot-time
groups):

```bash
sudo packaging/install-helper.sh
```

`OPENHW_HELPER_SOCKET` relocates the socket for both ends (the tests use a private socket).

## Tests

```bash
dotnet test
```

Headless: the helper protocol, its validation and the client/server socket round trip run without root or a display.
`tests/OpenHW.App.Tests` renders the real window with Avalonia's headless Skia backend (no display) to check what
rows draw — e.g. that frozen rows render dimmed; it reads this machine's live sensors.

`dotnet src/OpenHW.App/bin/Debug/net8.0/openhw.dll --dump` prints the sensor tree as the window shows it, without
a display — useful for checking changes headlessly.

## Versions

Semantic versioning, pre-1.0: the **minor** number goes up with each round of new features, the **patch** number for
fixes only. The version is set once in `Directory.Build.props` (app, helper and tests share it), every release gets a
[CHANGELOG.md](CHANGELOG.md) entry and a `vX.Y.Z` git tag, and the window footer / Help → About show
`OpenHW X.Y.Z · <git revision>` ("-dirty" if built with uncommitted changes — commit before installing).

## Status

Sensor tree window (HWiNFO layout: device groups, collapsible per-core rows, Current/Min/Max/Average, changed-value
highlight, expand/collapse all, reset) with HWiNFO's sensor menu (graphs, rename, hide, monitoring, logging, alerts,
layout and value customization), up to 8 tray icons, CSV logging, alerts with desktop notifications, power-mode
switching that keeps boost working, all-core and per-core CPU frequency caps, CPU and GPU power limits, and the
root helper for NVMe SMART, RAPL, DIMM and VR sensors, cpufreq ceilings and power limits. Ships as a .deb (see [Install](#install)).
Planned work is tracked in [ROADMAP.md](ROADMAP.md).
