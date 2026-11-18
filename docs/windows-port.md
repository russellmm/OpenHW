# Porting OpenHW to Windows 11 (to replace HWiNFO)

Status: written 2026-11-17 against OpenHW 0.38.2 as a plan; **phase 0 done and most of phase 1 done on 2026-11-17**
(on the owner's Windows 11 Pro install of the same workstation). This is the working document for the port: what
moves as is, what needs a Windows implementation, the order to do it in, and what to check first.

## Progress (2026-11-17)

Rule from the owner: **the Linux features stay exactly as they are** — every Windows change is behind
`OperatingSystem.IsWindows()` / `IsLinux()`, Linux keeps its code paths and call order. GPU Lending isn't wanted on
Windows.

- **Phase 0 — done.** The assembly-wide `[SupportedOSPlatform("linux")]` is gone; Windows code is in
  `OpenHW.Hardware/Windows/` and marked per class. `OsFiles` (Hardware): config root (`%APPDATA%\openhw`; XDG on Linux),
  owner-only files (0600 on Linux, a private ACL on Windows). Single instance: a per-user named pipe on Windows.
  `SensorHub` picks engines per platform. CPU Frequency, Power Limits, power profiles and GPU Lending are hidden on
  Windows (menus, tray menu, startup). Tests: 297 pass on Windows; 69 Linux-only ones (`[LinuxFact]`,
  `[LinuxAvaloniaFact]`: lending, card filter, sysfs fakes with ':' names, tests needing Tctl/FCLK) are skipped there.
- **Phase 1 — mostly done, all without elevation:**
  - System (`WindowsSystemEngine`): memory/commit/page file (GetPerformanceInfo, PDH), per-core clocks
    (Processor Frequency × % Processor Performance), per-thread usage, C1/C2/C3 residency. Same ids as Linux.
  - NVIDIA: NVML read in the app (`nvml.dll` mapped by a DllImport resolver), all three cards with every extra row.
  - **NVMe (`WindowsNvmeEngine`)**: a query-only handle answers the SMART/Health log, Identify Controller and
    IOCTL_DISK_PERFORMANCE unelevated (checked on all 8 drives) → SMART rows, composite + sensor temperatures, activity.
    Drive identity = SHA-256(PCI address + model + serial) exactly as Linux (serial from Identify Controller, PCI
    address from the controller's device node) — **to confirm on Linux** that the ids match (compare a drive's
    `nvme-health:<id>` there with the Windows `--dump`/settings).
  - Network (`WindowsNetworkEngine`): GetIfTable2, hardware interfaces with a connector, not-present adapters dropped.
  - Tray (`Win32TrayIcon`): Shell_NotifyIcon v4, one icon per sensor, tile rendered at the taskbar's exact icon size,
    native tooltip (≤127 chars), native right-click menu from the same `TrayMenuItem`s, Explorer-restart re-add.
    Windows 11 puts new icons in the hidden-icons flyout until the user drags them out.
  - Alerts (`WindowsNotifier`): balloons on a tray icon, shown as toasts by Windows 11 (no buttons; click = default).
  - Start at login: `HKCU\…\Run` "OpenHW" (respects Task Manager's Startup apps disable). Tests use a scratch key.
  - CSV logging works as is (Windows opens the file shared so it can be read while logging).
  - **Not yet**: WireView in the app (COM port), UPS checked live (NUT client is portable), WHEA error count.
- Next: phase 2 (helper as a Windows service on a named pipe, `WmiIpmiTransport`, BMC sensors/fans) — needs an
  elevated install; then the kernel-access decision (open questions below).

## Goal and scope (owner, 2026-11-16)

Run OpenHW on **Windows 11 on the same workstation** (the owner dual-boots; HWiNFO 64 8.54 is installed there and was
studied on 2026-10-23 — [hwinfo-research.md](hwinfo-research.md)) so HWiNFO is no longer needed:

- **In scope**: the sensor window (HWiNFO layout, groups, Current/Min/Max/Average, graphs, row menu, Layout / Customize
  values, Hardware Check), **tray icons** (with real hover tooltips — Windows has them natively), **CSV logging**,
  **alerts**, **fan control** (BMC zones + NVIDIA fans, curves, alarm thresholds), the **BMC status / event log**,
  **BIOS Setup** (read-only), the **WireView Pro II** and **UPS** groups.
- **Out of scope**: GPU Lending (eBPF card filter, libvirt — the Windows install *is* where a lent card would go),
  CPU frequency caps, power profiles, CPU/GPU power limits (owner hasn't asked for them on Windows).

Success = the owner can uninstall HWiNFO: every group they use there has an OpenHW equivalent that reads the same
values (compare side by side with HWiNFO's shared memory, `tools/loadgen/hwinfo_shm.py`).

## Where the code stands

- **UI**: Avalonia 11.3 / .NET 8 — runs on Windows as is. `Platform.cs` in OpenHW.App, OpenHW.Hardware and the
  helper declares `[assembly: SupportedOSPlatform("linux")]`; that has to become per-file/per-class.
- **Seams that already exist** (keep the platform-free logic above them):

  | Interface | Linux implementation | Windows implementation to write |
  |---|---|---|
  | `ISensorEngine` | one per suite (hwmon, HSMP, NVML, NVMe, BMC, …) | per suite, below |
  | `IIpmiTransport` | `LinuxIpmiTransport` (`/dev/ipmi0` ioctls) | `WmiIpmiTransport` (`root\WMI` `Microsoft_IPMI.RequestResponse`) |
  | `IFirmwareVariables` | efivarfs | `GetFirmwareEnvironmentVariableExW` |
  | `IWireViewLink` | termios on `/dev/ttyACM*` | `System.IO.Ports.SerialPort` on `COMn` |
  | `ISmbus` | `/dev/i2c-N` ioctls | a kernel-access driver (see "Kernel access") |
  | Redfish (`BmcRedfish`) | host interface 169.254.0.17 | the BMC's **LAN address** (10.0.0.200 on the owner's network) with the `openhw` login, or the host-interface NIC if its Windows driver is installed |
  | `HelperClient` / `HelperServer` | Unix socket `/run/openhw/helper.sock` | named pipe `\\.\pipe\openhw-helper` |

- **Linux-only files** (from a grep for sysfs/proc/dev paths, libc P/Invoke, D-Bus, systemd, Unix sockets — ~50 files):
  - *Shared logic mixed with Linux I/O* — split into a portable core + a Linux reader: `Engines/Hwmon/*`
    (libsensors, BoardProfiles is portable), `Engines/Nvme/*`, `Engines/System/SystemEngine.cs`,
    `Engines/Network/NetworkEngine.cs`, `Engines/Nvidia/NvmlBackend.cs` (NVML itself is cross-platform; only the
    library name and device discovery differ), `Engines/Rapl`, `Engines/Edac`, `Engines/Hsmp`, `Bmc/Ipmi.cs`,
    `Fans/FanDriver.cs`, `Fans/GpuFanDriver.cs`, `Smbus/Smbus.cs`, `WireView/WireView.cs`, `Bios/FirmwareVariables.cs`,
    `Helper/HelperClient.cs`, `Helper/HelperServer.cs`, `OpenHW.Helper/Program.cs`, `HardwareCheck/*`, `SensorHub.cs`.
  - *Linux desktop only* — replace on Windows: `Tray/*` (StatusNotifierItem over D-Bus, the GNOME hover extension),
    `Alerts/DesktopNotifier.cs` (freedesktop notifications), `Settings/SingleInstance.cs` (Unix socket),
    `Program.cs` (execve GC trick, systemd restart), autostart (`.desktop` files).
  - *Out of scope on Windows* — exclude from the Windows build: `CardFilter/*`, `Power/GpuLendingWindow.cs`,
    `Power/LendCoordinator.cs`, `Power/AppLaunches.cs`, `Power/CpuCaps.cs`, `Power/PowerProfiles.cs`,
    `Power/PowerLimitsWindow.cs`, `CpuFrequency/*`, `PowerLimits/*`.

## Architecture on Windows

```
OpenHW.exe (user, no elevation)                       OpenHW Helper (Windows service, LocalSystem)
  Avalonia window, tray (Shell_NotifyIcon),   ──pipe──►  same HelperSession command set (protocol N):
  logging, alerts, fan UI, BIOS Setup                     IPMI via WMI (BMC sensors, SEL, fan zones, thresholds),
  user-mode engines: NVML reads, PDH/WMI system,          NVMe SMART (IOCTL), kernel-access sensors (Super I/O,
  network, WireView (COM), UPS (NUT TCP / HID)            MSR/SMN, SMBus), UEFI variables, NVML fan writes
```

- **Keep the helper split**: things needing Administrator/SYSTEM (IPMI WMI, NVMe IOCTLs on `\\.\PhysicalDriveN`,
  firmware variables, the kernel driver, NVML fan control) live in a **Windows service**; the window stays
  unelevated, so no UAC prompt at login. The `HelperSession` command handlers are already transport-independent — add a
  named-pipe server beside the Unix-socket one, with a pipe ACL for Administrators + a local group "OpenHW Users"
  (the Windows twin of the `openhw` group). Same "ok …/err …" line protocol, same validation, so the app's clients
  (`FanClient`, `BmcInfo`, the engines) don't change.
- **Install**: a per-machine installer (WiX MSI or Inno Setup) that installs the app, registers the service
  (auto-start), creates the group, adds the user, and an autostart entry for the window (`HKCU\…\Run` with `--tray`).
  Publish self-contained `win-x64` so .NET needn't be installed.
- **Settings**: `%APPDATA%\OpenHW\` (replaces `~/.config/openhw/`); the helper's state (fan journal, saved originals,
  BMC login, cert pin) in `%ProgramData%\OpenHW\` with an ACL for SYSTEM + Administrators only.

## Subsystem by subsystem

| Subsystem | Linux today | Windows plan | Needs | Effort |
|---|---|---|---|---|
| Window, layout, graphs, Customize, logging | Avalonia | unchanged | — | small (path + platform attributes) |
| Tray icons | StatusNotifierItem over D-Bus + GNOME hover extension | Avalonia `TrayIcon` (Shell_NotifyIcon) or a small Win32 NotifyIcon: icon per sensor, menu, **native tooltips** | — | medium (rewrite `Tray/`, reuse `TrayIconRenderer`) |
| Desktop notifications (alerts) | freedesktop D-Bus | Windows toast (Windows App SDK / `ToastNotification`, needs an AppUserModelID from the installer) | — | small |
| Single instance | Unix socket | named mutex + named pipe "show window" | — | small |
| System (CPU usage, memory, uptime) | /proc | PDH counters (`\Processor Information(*)\% Processor Utility`), `GlobalMemoryStatusEx`, `GetSystemTimes` | — | small |
| Network | /sys/class/net | `GetIfTable2` / `System.Net.NetworkInformation` statistics | — | small |
| NVIDIA GPUs | NVML (`libnvidia-ml.so`) via the helper | NVML (`nvml.dll`, ships with the driver) — reads in the app directly (no card filter to hide from) | — | small |
| NVIDIA fans | NVML via helper `gpufan*` | same NVML calls in the service (setting fan speed needs Administrator) | service | small |
| NVMe SMART / health | helper `nvmesmart` (admin pass-through ioctl) | `IOCTL_STORAGE_QUERY_PROPERTY`, `StorageDeviceProtocolSpecificProperty`, `NVMeDataTypeLogPage` page 2 on `\\.\PhysicalDriveN` | service (admin handle) | medium |
| NVMe activity | /sys/block stats | PDH `PhysicalDisk` counters | — | small |
| Drive temperature | hwmon `nvme` | from the SMART log (composite temp) | service | small |
| **BMC sensors, SEL, discrete states** | `/dev/ipmi0` (`LinuxIpmiTransport`) | `WmiIpmiTransport`: `Microsoft_IPMI.RequestResponse(Command, LUN, NetworkFunction, RequestData, ResponderAddress=0x20)` → completion code + response bytes | in-box IPMI driver (`ipmidrv.sys`, "Microsoft Generic IPMI Compliant Device"); service | medium — all of `IpmiCommands`, `BmcReader`, `IpmiEvents` carry over |
| **BMC fan zones, curves, alarm thresholds** | ASUS OEM IPMI `30 0e/0f`, Set Sensor Thresholds | same commands over `WmiIpmiTransport`; journal/originals in ProgramData | service | small once the transport works |
| Fan sources (Redfish) | host interface 169.254.0.17 | LAN 10.0.0.200 (make the base URL a setting; Hardware Check note if unreachable) | BMC LAN | small |
| BIOS Setup values | efivarfs (no root) | `GetFirmwareEnvironmentVariableExW` — needs `SeSystemEnvironmentPrivilege` (elevated) → read in the service, a `uefivars` helper command | service | small |
| BIOS Settings from the BMC, BMC access check | Redfish host interface | Redfish over LAN | BMC LAN | small |
| **Super I/O (NCT6798)**: fan RPM, voltages, temps | `nct6775` kernel driver via libsensors | port I/O to the Super I/O → **kernel-access driver** (below); `BoardProfiles` (WRX90E names/dividers) reused | driver | large |
| ASUS EC (T_Sensor, VRM temps, USB4/M.2/VRM fans) | `asus_ec_sensors` | ACPI EC reads via the kernel-access driver (register map as in the Linux driver) or drop (the BMC has T_Sensor and fans) | driver | medium / optional |
| CPU temps (Tctl/Tccd), clocks, effective clocks | k10temp, cpufreq, HSMP sysfs | SMN (Tctl) and MSRs (APERF/MPERF) via the kernel-access driver | driver | medium |
| CPU package power | RAPL via helper | AMD RAPL MSRs (C001_0299 unit, C001_029B package energy) via the driver | driver | small once the driver works |
| HSMP extras (FCLK, MCLK, limits, DRAM bandwidth) | `amd_hsmp` sysfs | no Windows driver; HSMP mailbox via SMN needs the driver — **later / maybe** | driver | large |
| DDR5 SPD hubs, PMIC rails | SMBus via helper `ddr5` | SMBus (PIIX4/DesignWare) via the driver — careful, shared with the BMC | driver | large |
| EDAC (memory errors) | /sys/devices/system/edac | WHEA: corrected-error events in the System log (`Microsoft-Windows-WHEA-Logger`), counted | — | small |
| WireView Pro II | helper reads `/dev/ttyACM*` (root:dialout) | `SerialPort` on the COM port found by VID/PID 0483:5740 (SetupAPI / WMI `Win32_PnPEntity`) — **no admin needed**, can live in the app | — | small (`WireViewSample.Parse`, identity logic reuse) |
| UPS | NUT upsd on 127.0.0.1:3493 | keep the NUT client (host configurable: NUT for Windows, or a NUT server elsewhere); fallback: the UPS as a battery (`Win32_Battery` / HID power device) gives charge/runtime/on-battery only | — | small |
| Hardware Check | DMI from /sys, drivers loaded | DMI via WMI (`Win32_BaseBoard`, `Win32_BIOS`, `Win32_Processor`); "what can't be read" notes become Windows-specific (driver not installed, service stopped, IPMI device missing) | — | medium |
| GPU Lending, CPU caps, power profiles, power limits | — | **not ported** (excluded from the Windows build) | — | — |

## Kernel access (the big decision)

HWiNFO uses its own signed kernel driver (`HWiNFO_x64_216.sys`) for port I/O, PCI config space, MSRs, SMBus and the SMU
mailbox. On Windows OpenHW can't reach the Super I/O, MSRs or SMBus from user mode either. Options:

1. **LibreHardwareMonitorLib** as a library (MPL-2.0 — file-level copyleft, fine to use as a NuGet dependency; verify
   the license and its NCT6798 / Zen 5 Threadripper support before relying on it). It wraps the driver and already
   knows the Nuvoton, Ryzen SMN/MSR and SMBus paths. Fastest route to Super I/O + CPU readings; OpenHW maps its sensors
   onto `HardwareItem`s and applies the WRX90E board profile itself.
2. **PawnIO** directly — the driver LibreHardwareMonitor and FanControl moved to after Microsoft Defender started
   flagging WinRing0 (2025: signed, but with a known vulnerability, and on Microsoft's vulnerable-driver blocklist,
   which is on by default since Windows 11 22H2). PawnIO runs small signed modules (e.g. Super I/O, SMBus PIIX4, MSR)
   instead of giving raw access. **To verify first**: PawnIO's signing, HVCI/Memory Integrity compatibility, its module
   list, and license.
3. **Own driver** — not realistic (EV certificate + Microsoft attestation signing; and it would be one more driver
   to trust).

**Never use WinRing0.** Recommended: start with option 1 (it also brings PawnIO), and fall back to option 2's
modules directly only if a reading needs a path LHM doesn't expose. Phase 3 begins with a spike to confirm
LHM + PawnIO read the NCT6798 and the 9975WX's Tctl/Tccd under Windows 11 with Memory Integrity on.

The BMC needs **no** third-party driver (in-box IPMI driver), and it already supplies most board readings (CPU
package, DIMM and slot temperatures, every fan header, the main rails). So the port is useful **before** the kernel
part: the BMC + NVML + NVMe + WireView + UPS already cover most of what the owner watches in HWiNFO.

## Phases

0. **Builds on Windows** — split `SupportedOSPlatform` per file; Windows CI (`windows-latest`: build + the headless
   UI tests, which use Avalonia's headless Skia and no platform APIs); the app starts with only the platform-free
   parts (window, settings in `%APPDATA%`, single instance, empty groups). Exclude out-of-scope features.
1. **User-mode sensors** — System, Network, NVML reads, NVMe activity, WireView (COM), UPS (NUT), WHEA count; tray
   (`Shell_NotifyIcon` with tooltips) and toast alerts; CSV logging; autostart.
2. **Service + BMC** — the Windows service and named pipe; `WmiIpmiTransport`; BMC sensors, SEL / status window, fan
   zones, curves, alarm thresholds (same flash-write rules); Redfish over LAN; NVMe SMART; UEFI variables → BIOS Setup;
   NVIDIA fan control. First real-hardware check: Get Device ID and the SDR over WMI match the Linux reading.
3. **Kernel-access sensors** — the LHM/PawnIO spike, then Super I/O (WRX90E profile), CPU temps/clocks/RAPL; then
   SMBus (DIMM SPD/PMIC) only if the BMC's DIMM temperatures aren't enough.
4. **Installer** — MSI/Inno: app + service + group + autostart + uninstall that restores BMC fan curves (as the Linux
   helper does on stop); code-signing if available (unsigned works but SmartScreen warns).
5. **Parity check against HWiNFO** — run both, log HWiNFO via shared memory (`hwinfo_shm.py log`) and OpenHW via its
   CSV logging under the same load (`tools/loadgen`), compare per group; list what's still missing. Then uninstall
   HWiNFO.

## Things that behave differently on Windows

- **BMC host interface**: under Windows it needs a driver for the USB NIC (AMI/ASUS "Redfish host interface"); without
  it Redfish is reachable only over the LAN — make the Redfish base URL a setting, don't hard-code 169.254.0.17.
- **Fan writes are shared with nothing**: OpenFan doesn't exist on Windows; but ASUS's own tools (Armoury Crate,
  AI Suite) and HWiNFO/FanControl can also drive fans — Hardware Check should warn when one is running.
- **The same BMC is used by both OSes**: curves written under Linux stay in the BMC's flash and run under Windows (and
  the other way round). Keep one fan configuration and share it (e.g. export/import the configuration file), or the
  two installs will overwrite each other's curves — count flash writes accordingly.
- **Alarm thresholds, BMC SEL, fan originals** live in the BMC, so they're already common to both OSes; the saved
  originals file should be the same format on both (copy it across once).
- **Time stamps**: Windows keeps the RTC in local time by default, Linux in UTC — the BMC SEL is UTC either way.
- **NVMe drive order**: `PhysicalDriveN` order differs from `nvmeN`; identify drives by model + serial hash (as
  Hardware Check already does), never by index.
- **GPU identity**: NVML UUIDs are the same on both OSes — keep using them (owner rule: never PCI addresses in saved
  config).
- **Paths and line endings**: settings files are JSON; keep them portable (no absolute Linux paths inside).

## Testing

- Most tests already use fakes (fake IPMI BMC, fake Redfish, fake WireView link, fake upsd, fake firmware variables)
  and run anywhere `dotnet test` runs — add a Windows CI job.
- New fakes: a fake `Microsoft_IPMI` (byte-level, reusing `BmcTests.FakeBmc`), a fake NVMe IOCTL buffer, a fake PDH.
- Real-hardware checks under Windows on the owner's machine, read-only first (same rule as on Linux), and anything
  that writes (fan curves, thresholds) only with the owner's go-ahead.
- Reference: HWiNFO's sensor inventory and phase medians in `reference/stress-2026-10-23/`.

## Open questions for the owner

1. Windows edition and Memory Integrity (HVCI) setting on the Windows install (affects which drivers load).
2. OK to depend on LibreHardwareMonitorLib + PawnIO (a third-party signed driver) for the Super I/O/CPU/SMBus readings?
3. One fan configuration shared between Linux and Windows, or separate ones?
4. Is code signing wanted (certificate cost), or is an unsigned installer fine for personal use?
5. Is the BMC LAN address fixed (10.0.0.200) or should OpenHW discover it (IPMI "Get LAN Configuration" over WMI)?

## References

- Microsoft: [Microsoft_IPMI class](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ipmiprv/microsoft-ipmi),
  [RequestResponse](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ipmiprv/microsoft-ipmi-requestresponse),
  [In-band management of IPMI with PowerShell](https://learn.microsoft.com/en-us/archive/blogs/wmi/in-band-management-of-ipmi-with-powershell),
  [Working with NVMe drives](https://learn.microsoft.com/en-us/windows/win32/fileio/working-with-nvme-devices),
  [STORAGE_PROTOCOL_NVME_DATA_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ne-winioctl-storage_protocol_nvme_data_type).
- WinRing0 / Defender: [Neowin](https://www.neowin.net/news/windows-1110-is-flagging-winring0-on-your-pc-monitoring-fan-control-apps-heres-why/),
  [FanControl discussion](https://github.com/Rem0o/FanControl.Releases/discussions/3017).
- OpenHW: [hwinfo-research.md](hwinfo-research.md) (what HWiNFO reads and how), [fan-control.md](fan-control.md),
  [bmc.md](bmc.md), [bios-setup.md](bios-setup.md), [power-devices.md](power-devices.md), ROADMAP "Windows 11".
