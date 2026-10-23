# OpenHW

HWiNFO64-style hardware monitor for Ubuntu (GNOME): a sensor status window plus per-sensor tray icons.
Design references are in [`reference/`](reference/) (HWiNFO64 v8.54 captures).

Built with Avalonia 11 / .NET 8. Sensor reading is shared with
[openfan-linux](../../ChatGPT/openfan-linux) by project reference (`OpenFan.Core`, `OpenFan.Linux.Hw`), not copied.

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

`src/OpenHW.App/Sensors/SensorHub.cs` samples every backend once per 2 s poll; the window and tray share that
sample. On top of openfan-linux's backends, OpenHW adds:

- `SystemBackend` — memory/swap, pressure stall info, load average, per-core clocks and usage, C-state residency
- `NvmlExtrasBackend` — GPU video clock, PCIe link speed/width/throughput, encoder/decoder load, P-state, limiters
- `NetworkBackend` — download/upload rate and totals per physical interface

Not yet read (root only): RAPL package energy and the MSRs (effective clocks, per-core power).

`dotnet src/OpenHW.App/bin/Debug/net8.0/openhw.dll --dump` prints the sensor tree as the window shows it.

## Status

Sensor tree window (HWiNFO layout: device groups, collapsible per-core rows, Current/Min/Max/Average, changed-value
highlight, expand/collapse all, reset) and one CPU-temperature tray icon. Not yet: settings dialog, logging,
per-sensor tray icon selection, column resizing.
