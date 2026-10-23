# OpenHW

HWiNFO64-style hardware monitor for Ubuntu (GNOME): a sensor status window plus per-sensor tray icons.
Design references are in [`reference/`](reference/) (HWiNFO64 v8.54 captures).

Built with Avalonia 11 / .NET 8. Self-contained: the sensor code started as a copy of openfan-linux's read paths
and now lives here, so changes to openfan-linux cannot break OpenHW.

## Build and run

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
OpenHW is read-only: the engines contain no fan or power-limit writes.

NVMe SMART needs root, so `NvmeEngine` reads it through openfan-linux's helper socket (`/run/openfan/helper.sock`,
override with `OPENFAN_HELPER_SOCKET`); without that helper, drive health rows stay empty. Not yet read (root only):
RAPL package energy and the MSRs (effective clocks, per-core power).

`dotnet src/OpenHW.App/bin/Debug/net8.0/openhw.dll --dump` prints the sensor tree as the window shows it, without
a display — useful for checking changes headlessly.

## Status

Sensor tree window (HWiNFO layout: device groups, collapsible per-core rows, Current/Min/Max/Average, changed-value
highlight, expand/collapse all, reset) and one CPU-temperature tray icon. Not yet: settings dialog, logging,
per-sensor tray icon selection, column resizing.
