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
OpenHW is read-only today: the engines contain no fan or power-limit writes. Write features (GPU/CPU power
limits, CPU max frequency) are planned through the helper — see [ROADMAP.md](ROADMAP.md).

NVMe SMART needs root, so `NvmeEngine` reads it through OpenHW's own helper (below); without it, drive health
rows stay empty and everything else keeps working. Not yet read (root only): RAPL package energy and the MSRs
(effective clocks, per-core power) — they will go through the same helper.

## Root helper

`openhw-helper` (`src/OpenHW.Helper`) is a small read-only daemon for the readings Linux reserves for root. It
listens on `/run/openhw/helper.sock` (`0660 root:openhw`) and answers only fixed queries — today `ping` and
`nvmesmart <controller> <identity>`, where both arguments must match a drive the system enumerates. It has no
write commands, caps request lines at 256 bytes, reads each drive at most once per 10 s, and runs under a
hardened systemd unit (only `CAP_SYS_ADMIN` + `CAP_CHOWN`, read-only filesystem, no network, NVMe devices only).

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

`dotnet src/OpenHW.App/bin/Debug/net8.0/openhw.dll --dump` prints the sensor tree as the window shows it, without
a display — useful for checking changes headlessly.

## Status

Sensor tree window (HWiNFO layout: device groups, collapsible per-core rows, Current/Min/Max/Average, changed-value
highlight, expand/collapse all, reset), one CPU-temperature tray icon, and the root helper for NVMe SMART.
Planned work is tracked in [ROADMAP.md](ROADMAP.md).
