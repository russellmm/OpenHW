# OpenHW

HWiNFO64-style hardware monitor for Ubuntu (GNOME): a sensor status window plus per-sensor tray icons.
Design references are in [`reference/`](reference/) (HWiNFO64 v8.54 captures).

Built with Avalonia 11 / .NET 8. Self-contained: the sensor code started as a copy of openfan-linux's read paths
and now lives here, so changes to openfan-linux cannot break OpenHW.

## Install

```bash
packaging/install-app.sh
```

Publishes a Release build to `~/.local/lib/openhw` (on the system drive, so it runs without the source tree),
links `~/.local/bin/openhw`, and adds an "OpenHW" entry with icon to the app grid. No root; re-run to update.
Drive health also needs the root helper — see [Root helper](#root-helper).

## Build and run from source

```bash
dotnet build
dotnet src/OpenHW.App/bin/Debug/net8.0/openhw.dll
```

## Tray icons

`src/OpenHW.App/Tray/` implements the StatusNotifierItem and dbusmenu D-Bus interfaces directly, so each
icon is a picture we draw (value on a coloured tile, like HWiNFO). GNOME shows them through the
`ubuntu-appindicators` extension. Each icon has its own session-bus connection. Which sensors get icons, and their
colours, are chosen in **File → Settings** (up to 8; default one CPU-temperature icon on red); tray sensors stay
live even inside collapsed groups. Digits are drawn in a narrow bold face sized to the tile.

Each icon's right-click menu starts with its sensor: the reading, min/max/average, the device it belongs to and a
plain-language explanation — then **Remove from tray** and a **Colour** submenu for that icon. GNOME's AppIndicator extension never shows tooltips, so that menu is where the
expanded information appears there; the same text is published as the icon's tooltip for hosts that show one
(e.g. KDE). On GNOME the icon size is the extension's `icon-size` setting (22 px on the workstation).

Every sensor row's tooltip in the main window also starts with what the reading means and when it matters
(`SensorExplanations`), before its source and id.

## Settings

**File → Settings** (also in the tray menu), saved in `~/.config/openhw/settings.json` and applied at once: poll
interval (1–10 s, default 2 s), start at login (a freedesktop autostart entry, `~/.config/autostart/openhw.desktop`,
optionally with `--tray` to start with only the tray icons), and the tray icons. The sensor picker is a search box:
every word typed must appear in "Device › Sensor" ("gpu power", "6000 temp", "fclk"); Enter or double-click adds.
Sensors already in the tray are listed grayed with "(in tray)" and can't be added twice (from Settings or a row's
right-click menu); a settings file with duplicates keeps the first of each. OpenHW runs once per session: a
second launch (app grid, or the command) shows the running window and exits.

`Tmds.DBus.Protocol`'s `MessageWriter` is a `ref struct`: any helper that writes to it must take it by `ref`,
or its output is silently lost and the bus disconnects the app for sending a malformed reply.

## Sensors

`src/OpenHW.Hardware` has one engine per sensor suite (`ISensorEngine`), each with its own discovery and reads:

| Engine | Source |
|---|---|
| `HwmonEngine` | /sys/class/hwmon via libsensors (sysfs fallback): Super I/O, k10temp, HSMP power, SPD5118, NVMe and NIC temps |
| `HsmpEngine` | AMD HSMP platform device (no root): FCLK, MCLK, core clock limit, active frequency limit and its source, C0 residency, DRAM bandwidth, PROCHOT |
| `NvidiaEngine` | NVML: temps, fans, power, clocks, load, memory, video clock, PCIe link/throughput, encoder/decoder, P-state, limiters |
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
rows stay empty and everything else keeps working. Not yet read (root only): RAPL package energy and the MSRs
(effective clocks, per-core power) — they will go through the same helper.

## Menu and shortcuts

The main window has a menu bar — **File** (Hide Window Ctrl+W, Exit Ctrl+Q), **View** (Expand All Ctrl+E,
Collapse All Ctrl+Shift+E, Reset Min/Max/Average Ctrl+R), **Power** (the power profiles, CPU Frequency… Ctrl+F,
Power Limits… Ctrl+L) and **Help** (About). New features get a menu entry first; the footer keeps icon shortcuts
for the common ones (expand/collapse, power mode, CPU frequency, power limits, reset, close), and the tray menu
mirrors the Power menu.

Right-clicking a sensor row opens HWiNFO's sensor menu, item for item: **Add to tray** / **Remove from tray** and
**Tray icon settings…** work today; the rest (Show Graph, monitoring on/off, Rename, Hide, logging, layout,
customise, OSD, RTSS, alerts, LG LCD) are grayed with a "To be implemented" tooltip until they land.

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

The window footer and the tray menu switch power-profiles-daemon's profile (Power saver / Balanced / Performance —
the same setting as GNOME's power menu; no root needed).

**CPU frequency** (footer button, or *CPU Frequency…* in the tray) caps every core at once or each physical core
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
way is shown as **Capped at …** in the footer and the tray menu, and fixed only when clicked. Code:
`src/OpenHW.App/Power/`, `src/OpenHW.Hardware/CpuFrequency/`.

## Root helper

`openhw-helper` (`src/OpenHW.Helper`) is a small daemon for what Linux reserves for root. It listens on
`/run/openhw/helper.sock` (`0660 root:openhw`) and answers only fixed commands: `ping`,
`nvmesmart <controller> <identity>` (both arguments must match a drive the system enumerates),
`cpufreqmax <all|id,id,…> <kHz|max>` (every id must be an existing cpufreq policy and every value lie within that
policy's minimum and hardware maximum; `max` is each policy's `cpuinfo_max_freq`) and `cpufreqrestore` (every capped
policy to its hardware maximum) — see [Power modes and CPU frequency caps](#power-modes-and-cpu-frequency-caps) —
`gpupower <uuid> <mW|default>` (an enumerated card, within NVML's constraints), `cpuppt <mW|boot>`, and the boot
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

## Status

Sensor tree window (HWiNFO layout: device groups, collapsible per-core rows, Current/Min/Max/Average, changed-value
highlight, expand/collapse all, reset), one CPU-temperature tray icon, power-mode switching that keeps boost
working, all-core and per-core CPU frequency caps, CPU and GPU power limits, a menu bar, and the root helper for NVMe SMART,
cpufreq ceilings and power limits.
Planned work is tracked in [ROADMAP.md](ROADMAP.md).
