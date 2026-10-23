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
`ubuntu-appindicators` extension. Each icon has its own session-bus connection.

`Tmds.DBus.Protocol`'s `MessageWriter` is a `ref struct`: any helper that writes to it must take it by `ref`,
or its output is silently lost and the bus disconnects the app for sending a malformed reply.

## Sensors

`src/OpenHW.Hardware` has one engine per sensor suite (`ISensorEngine`), each with its own discovery and reads:

| Engine | Source |
|---|---|
| `HwmonEngine` | /sys/class/hwmon via libsensors (sysfs fallback): Super I/O, k10temp, HSMP, SPD5118, NVMe and NIC temps |
| `NvidiaEngine` | NVML: temps, fans, power, clocks, load, memory, video clock, PCIe link/throughput, encoder/decoder, P-state, limiters |
| `NvmeEngine` | NVMe SMART health and live activity |
| `EdacEngine` | Memory-controller corrected/uncorrected error counts |
| `SystemEngine` | Memory/swap, pressure stall info, load average, per-core clocks and usage, C-state residency |
| `NetworkEngine` | Download/upload rate and totals per physical interface |

`src/OpenHW.App/Sensors/SensorHub.cs` samples every engine once per 2 s poll; the window and tray share that sample.
The engines are read-only: no fan or power-limit writes. Write features (GPU/CPU power limits, CPU max frequency)
are planned through the helper — see [ROADMAP.md](ROADMAP.md).

NVMe SMART needs root, so `NvmeEngine` reads it through OpenHW's own helper (below); without it, drive health
rows stay empty and everything else keeps working. Not yet read (root only): RAPL package energy and the MSRs
(effective clocks, per-core power) — they will go through the same helper.

## Power modes

The window footer and the tray menu switch power-profiles-daemon's profile (Power saver / Balanced / Performance —
the same setting as GNOME's power menu; no root needed). Power saver turns CPU boost off and the kernel holds every
cpufreq ceiling (`scaling_max_freq`) at the base clock. Leaving it normally restores the previous ceiling, but if
the stored ceiling was the base clock (seen on the 9970X after a session that started in power-saver), Performance
silently stays at 4.0 GHz. So whenever a profile change — from OpenHW, GNOME or `powerprofilesctl` — turns boost
back on and the CPU is still capped, OpenHW asks the helper to restore each ceiling to `cpuinfo_max_freq`. A cap
present for any other reason (another tool, or already there when OpenHW started) is shown as **CPU capped at …**
in the footer and the tray menu, and lifted only when clicked. Code: `src/OpenHW.App/Power/`,
`src/OpenHW.Hardware/CpuFrequency/`.

## Root helper

`openhw-helper` (`src/OpenHW.Helper`) is a small daemon for what Linux reserves for root. It listens on
`/run/openhw/helper.sock` (`0660 root:openhw`) and answers only fixed commands: `ping`,
`nvmesmart <controller> <identity>` (both arguments must match a drive the system enumerates), and
`cpufreqrestore` — no arguments: raises every cpufreq policy whose `scaling_max_freq` is below its
`cpuinfo_max_freq` back to that maximum (see [Power modes](#power-modes)). No client input becomes a path or a
value. It caps request lines at 256 bytes, reads each drive at most once per 10 s, and runs under a hardened
systemd unit (only `CAP_SYS_ADMIN` + `CAP_CHOWN`, read-only filesystem except `/sys/devices/system/cpu`, no
network, NVMe devices only). Re-run the install below after updating OpenHW when the helper changes.

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
working, and the root helper for NVMe SMART and the CPU frequency ceiling restore.
Planned work is tracked in [ROADMAP.md](ROADMAP.md).
