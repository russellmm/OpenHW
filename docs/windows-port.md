# Porting OpenHW to Windows 11 (to replace HWiNFO and OpenFan)

Status (2026-11-18): **phases 0–2 done, phase 3 (kernel access) largely done with PawnIO's signed modules, CPU and
GPU power limits done** on the owner's Windows 11 Pro install of the same workstation. Not released: no version bump,
the Windows build is installed by script (below). Written as a plan on 2026-11-17 against 0.38.2; the plan sections
further down are kept, with what changed marked. Commits: 41db191 (phases 0–1) … afd9f4b and later.

**Owner's rules for the port** (2026-11-17):
- **The Linux features stay exactly as they are.** Every Windows change is behind `OperatingSystem.IsWindows()` /
  `IsLinux()`; Linux keeps its code paths and call order. Windows-only code lives in `OpenHW.Hardware/Windows/`,
  `OpenHW.Helper/WindowsHelper.cs`, `LanBinding.cs` and `Windows*` classes in the app.
- **No GPU Lending on Windows** (nor CPU frequency caps / power profiles).
- OpenHW replaces **both HWiNFO and OpenFan** on Windows → **CPU and GPU power limits are in scope**; the CPU limit is
  set by OpenHW itself, not Ryzen Master.
- **Don't limit the design because something needs Administrator** — the helper service runs as LocalSystem.
- **Every Windows build is mirrored to `C:\StandAlone\OpenHW`**, where the owner runs OpenHW from (build.ps1 does it).
- Redfish to the BMC must **bypass Tailscale**.

## Status at a glance

| Area | Windows status | How |
|---|---|---|
| Window, layout, graphs, Customize, logging, alerts, single instance, start at login | **done** | Avalonia as is; `%APPDATA%\openhw`; named pipe; `HKCU\…\Run`; balloon/toast alerts |
| Tray icons | **done** (on screen; digits fill the 24 px tile; positions kept, uID from the sensor) | `Win32TrayIcon` (Shell_NotifyIcon v4), `TrayIconRenderer.DrawTall` |
| System: memory, page file, per-core clocks/usage, C-states | **done** | `WindowsSystemEngine` (PDH, GetPerformanceInfo) |
| NVIDIA GPUs (all rows) | **done** | NVML in the app (`nvml.dll`) |
| NVMe: SMART, temperatures, activity (8 drives) | **done**, unelevated | `WindowsNvmeEngine` (query-only IOCTLs); ids = Linux's (to confirm on Linux) |
| Network | **done** | `WindowsNetworkEngine` (GetIfTable2) |
| BMC sensors, states, SEL, thresholds | **done** (read live) | service + `WmiIpmiTransport` |
| CPU temperature (Tctl, CCD1–4) | **done** (PawnIO) | `K10Temp` (SMN, k10temp's map) as hwmon chip `k10temp` through `WindowsKernelSensorsReader` → `HwmonEngine` |
| CPU Enhanced (HSMP: package power, PPT limit, FCLK/MCLK, frequency limit + sources, C0, DRAM bandwidth, PROCHOT) | **done** | `WindowsHsmpSensors` (service, `hsmpread`) → `WindowsHsmpEngine`, Linux's `hsmp:*` ids |
| Fan configuration | **shared with Linux** (`C:\StandAlone\OpenHW\configs` = `/mnt/DriveC/StandAlone/OpenHW/configs`) | `OsFiles.SharedConfigDir`, `FanCurveStore` |
| BMC fan zones, curves, alarm thresholds | **available, not written yet** | same `FanDriver` over WMI IPMI |
| Redfish (fan sources, BIOS settings, access check) | **working; login saved**; sources read once per service run | BMC LAN address from IPMI, bound to whichever wired port reaches it (never Tailscale) |
| CPU power limit (PPT) | **done, write tested** | AMD's own HSMP driver (`WindowsHsmp`) |
| GPU power limits | **done, not written yet** | NVML in the service, kept by UUID |
| NVIDIA fan control | **running** (both RTX PRO 6000 on their shared curves since 21:35) | `GpuFanDriver` (NVML) |
| BIOS Setup values (UEFI variables) | to do | `GetFirmwareEnvironmentVariableExW` in the service |
| WireView Pro II | **done** (in the app, COM port) | `WindowsWireView` (+ `WindowsSerialLink`) feeding `WireViewEngine` |
| UPS | **done** (USB HID, CyberPower CP2000PFCRM2U) | `WindowsHidUps` answers NUT's LIST UPS/VAR for `NutEngine` |
| Service start/stop from OpenHW | **done** | Settings → Driver Management (Start/Stop); `--install` gives OpenHW Users start/stop rights |
| Board VR (Infineon XDPE19283B, VDDIO_Mem) | **done** (Windows only so far) | `WindowsSmbusProvider` (PawnIO SmbusPIIX4) → Linux's `BoardVrReader`/`BoardVrEngine`; `BoardVrs.WindowsKnown` |
| DIMM SPD hub/PMIC | **waiting on a test boot** | on the FCH DesignWare I2C controllers (AMDI0010), not the SMBus: OpenHW's module `AMDI2C` is written and compiles; the official PawnIO won't load it until its author signs it; a test build of the driver is ready |
| RAPL package + cores power | **done** | `WindowsRapl` (AMDFamily17 MSRs) → Linux's `RaplEngine` rows |
| Effective clocks per core + average | **done** | `WindowsEffectiveClocks` (APERF per logical CPU) → `WindowsEffectiveClockEngine` |
| ASUS EC: VRM1, VRM2, USB thermistor | **done** (WRX90E only) | `AsusEc` (LpcACPIEC, registers 0x34/0x33/0x42 from OpenFan) as hwmon chip `asusec` |
| Hardware errors (WHEA: corrected since boot / 30 days, fatal 30 days) | **done** (user mode) | `WindowsWheaEngine` — Linux's EDAC counterpart |
| Super I/O NCT6798D | to do (PawnIO LpcIO) | low value here: the BMC reads the rails, temperatures and fans |
| Chipset (xHCI) temperature, SVI3 telemetry, per-core/L3 temperatures | not reachable with signed modules | chipset MMIO / SMU PM table |
| Installer | phase 4 | scripts for now; Inno Setup is on this PC |

## How it's installed on this PC

- **OpenHW** runs from `C:\StandAlone\OpenHW\openhw.exe` (self-contained win-x64; no .NET install needed).
- **OpenHW Helper** service (`OpenHWHelper`, LocalSystem, automatic, restarts on failure) runs from
  `C:\Program Files\OpenHW\openhw-helper.exe` — a SYSTEM service must not run from a user-writable folder.
- Pipe `\\.\pipe\openhw-helper`: SYSTEM, Administrators, group **"OpenHW Users"**, and accounts listed by SID in
  `%ProgramData%\OpenHW\pipe-users` (`--install` adds the installing user, so no sign-out is needed). The first pipe
  instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE (no squatting).
- Helper state in `%ProgramData%\OpenHW` (SYSTEM + Administrators only): `fan-restore.json`, `fan-original.json`,
  `bmc-thresholds-original.json`, `bmc-login`, `bmc-cert.sha256`, `bmc-url` (optional override),
  `gpu-power-limits.json`, `cpu-power-limit.json`, `pipe-users`. `fan-original.json` and
  `bmc-thresholds-original.json` were copied from Linux's `/var/lib/openhw` (same machine identity), so Restore
  original on Windows goes back to the BMC's real originals, not to what Linux had written.
- **Fan configuration shared with Linux** in `C:\StandAlone\OpenHW\configs` (`fan-curves.json`, `fan-profiles\`):
  Linux sees it as `/mnt/DriveC/StandAlone/OpenHW/configs` (ntfs-3g; Fast Startup is off, so Linux can write it).
  `build.ps1`'s mirror excludes `configs`.
- **Build**: `packaging\windows\build.ps1` → `dist\win-x64`, mirrored to `C:\StandAlone\OpenHW` (closes an OpenHW
  running from either). **Install/update the service**: `packaging\windows\install.ps1` elevated → stops the service and
  waits for its process (fans go back to their curves), copies to Program Files, `openhw-helper --install` (service,
  group, pipe-users), starts it. **Remove**: `"C:\Program Files\OpenHW\openhw-helper.exe" --uninstall` (elevated).
- Helper diagnostics (elevated): `--console` (foreground), `--once <command>`, `--ipmi-check` (Get Device ID and
  request-data variants), `--hsmp-check` (HSMP test message, version, power and limits; sets nothing).

## What was done and found, by phase

- **Phase 0 — builds and tests on Windows.** Assembly-wide `[SupportedOSPlatform("linux")]` removed; Windows classes
  marked per class. `OsFiles`: config root (XDG / `%APPDATA%`), helper state dir, owner-only files (0600 / private ACL).
  `SensorHub` picks engines per platform. Tests on Windows: 304 pass, 69 Linux-only skipped (`[LinuxFact]`,
  `[LinuxAvaloniaFact]`: lending, card filter, sysfs fakes with ':' names, tests needing Tctl/FCLK, RTXinVM autostart);
  Windows-only tests are `[WindowsFact]`. **Not yet run on Linux** after these changes.
- **Phase 1 — user-mode readers** (no elevation), same sensor ids as Linux so layouts/tray/alerts/logs carry over:
  - System: GetPerformanceInfo + PDH `\Processor Information(*)` (% Processor Time, Frequency × % Performance,
    % C1/C2/C3 Time), cores from GetLogicalProcessorInformationEx. Clocks are Windows' estimate, not MSR readings.
  - NVMe: a handle opened with no access answers Identify Controller (the real serial — the device descriptor's
    "serial" is the EUI-64), the SMART/Health log page and IOCTL_DISK_PERFORMANCE. PCI address from the controller's
    device node → identity = SHA-256(PCI + model + serial), as Linux.
  - Network: GetIfTable2, hardware interfaces with a connector, not-present (phantom) adapters dropped.
  - Tray: one icon per sensor at the taskbar's icon size; native tooltip (≤127 chars) and menu; re-added after an
    Explorer restart. Windows 11 keeps new icons in the hidden-icons flyout until the user drags them out.
  - Tray tiles on Windows: one more pixel of margin and the unit measured by advance width (Windows' hinting draws
    glyphs wider than their outlines).
  - **Larger tray digits (owner: "far too small")**: Windows draws notification icons only at the small-icon size
    (`SM_CXSMICON`: 24 px at this PC's 150 %; a bigger icon is just scaled down), so `TrayIconRenderer.DrawTall`
    (Windows only) fills it: digits the full height less a pixel or two, narrowed sideways (to 62 %, 48 % for four
    characters) before being made smaller, the unit smaller and raised, dropped after three characters ("100", "250";
    the tooltip has it), greyscale anti-aliasing (ClearType fringes on a solid tile). Whole numbers only, as on Linux
    (`TrayText`: up to 9999, then "13k").
  - **Icons keep the place the user drags them to**: Explorer remembers each icon (shown or hidden, position) by
    exe path + uID (`HKCU\Control Panel\NotifyIconSettings`). The uIDs were list positions and every tray change
    deleted and re-added all icons, so drags didn't stick. Now the uID is a 16-bit hash of the sensor id + copy number
    (`Win32TrayIcon.IdFor`, 0x100–0xFEFF; the notifier's own icon is 0xFFF0) and `RebuildWindowsTray` keeps icons that
    are still configured (new menu only), removing/adding just the changed ones. Move left/right are hidden on
    Windows (drag instead). Old slot-numbered entries stay in Explorer's list as stale "other tray icons".
- **Phase 2 — service and BMC:**
  - `WmiIpmiTransport` (`Microsoft_IPMI.RequestResponse`, in-box `ipmidrv.sys`, Administrator only): `ResponseData`
    starts with the completion code (Get Device ID `00 20 81 02 01 02 BF 3F 0A 00 93 11 22 00 00 00`, AST2600 fw 2.01).
    Live: 55 sensors, states, SEL, thresholds; FanDriver reports all 7 zones available. Machine identity = SMBIOS UUID
    (lowercase, as Linux's `product_uuid`).
  - **HWiNFO collides** on the BMC's KCS interface: with HWiNFO running 1 of 5 full reads succeeded (undefined
    completion codes 0x02 / 0x2D, "didn't answer"); closed, 5 of 5. The transport retries undefined codes (5 tries);
    not re-checked with HWiNFO running.
  - **Redfish**: the service asks the BMC for its LAN addresses (IPMI Get LAN Configuration, channels 1–8: 10.10.10.10
    and 10.0.0.200) and uses the one on a subnet this PC is wired to; `bmc-url` overrides. Tailscale accepts a
    10.0.0.0/24 subnet route with a lower metric than the X710 ports, so `LanBinding` binds the connection to the
    wired port's address (10.0.0.224 → 10.0.0.200, direct, 6 ms). Over the LAN the BMC wants the `openhw` login.
  - **GPU power limits**: Power → Power Limits (`WindowsPowerLimitsWindow`) → `gpupower` (live), `gpupowersave` (kept
    by UUID); the service reapplies kept limits when it starts (what OpenFan did). Read live: RTX 5060 Ti 180 W (range
    150–198), both RTX PRO 6000 at 300 W (default 600, range 150–600).
  - **CPU power limit**: AMD's chipset package installs **AmdHSMPDriver.sys** (1.0.0.7, WHQL, "AMD Host System
    Management Port", on `ACPI\AMDI0097` like Linux's amd_hsmp; device ACL SYSTEM + Administrators). No published
    interface; read from the driver: interface `{dc142d67-9b96-4778-a0b3-b6d6eca590be}`, IOCTL **0x2203C8** (buffered),
    48-byte in/out in Linux's `struct hsmp_message` layout (msg_id u32 @0, num_args u16 @4, response_sz u16 @6,
    args u32[8] @8, sock_ind u16 @40). Confirmed with HSMP's test message (0x1234 → 0x1235); interface version 7.
    `WindowsCpuPowerLimits`: messages 4/5/6/7, Linux's rules (50 W to the SMU maximum, 2000 W here; read back).
    Commands `cpupptread`, `cpuppt <mW|boot>`, `cpupptsave <W|none>`; the kept value is applied at service start.
    **Write tested 21:01** with the owner's go-ahead: 350 → 300 → 350 W, each read back; 40 W, 2500 W, "boot" with
    nothing kept and "abc" refused. Left at 350 W, nothing kept.

- **Shared fan configuration (2026-11-17 night).** The BMC keeps the curves the last OS wrote, so each OS must work
  from one file. `OsFiles.SharedConfigDir` = `C:\StandAlone\OpenHW\configs` / `/mnt/DriveC/StandAlone/OpenHW/configs`
  (used when OpenHW's StandAlone folder is there; else the per-user folder as before; `OPENHW_SHARED_CONFIG` overrides,
  tests set `off`). The file in use is saved relative (`fan-profiles/fans.json`); an absolute one from the other OS
  is found by name in `fan-profiles`. First start copies the OS's own files in unless the folder has a live file.
  Seeded from Linux (mounted read-only through WSL). Every curve source is a BMC sensor and GPU fans are keyed by
  NVIDIA UUID, so the file means the same thing on both. Effect on Windows: the app now drives both RTX PRO 6000
  cards' fans on their curves (30 % at idle); BMC zones are only written by Apply.

- **BMC sensors disappearing while Fan Control was open (2026-11-18).** Fan Control asked the service for the fans'
  Redfish sources every 2 s; on Windows each read is an HTTPS login + GET + logout over the LAN (~1 s), and the
  service builds a session per request that also read the fan zones over IPMI. Under that load `bmc` replies took
  1.5–3.2 s; the app's `BmcEngine` gives up at 2.5 s and then hid the whole group for a minute. Fixes (Windows only):
  `FanSourcesCache` (sources read once per service run; cleared by `fansource`, `fanoriginal`, BMC login changes),
  sessions no longer call `FanDriver.Status`, one process-wide lock for every `WmiIpmiTransport` (sensor reader, fan
  control and the address lookup each open one — two requests on KCS at once fail like HWiNFO's), thread pool minimum
  32 in the service, and `BmcEngine.WindowsGrace` (a failed request keeps the group 30 s and retries every poll).
  Measured with Fan Control's request load for 3 minutes: 90/90 `bmc` replies, median 1 ms, slowest 15 ms, 36 sensors
  every time; one HTTPS read in total.
- **Why HTTPS stays for sources**: ASUS E26328 appendix A — IPMI `30 93` sets one byte per PWM (CPU temperature,
  T-sensor, TR1–TR3, for 23 PWMs at once) and this firmware refuses to read it (0xC7). The owner's curves follow
  DIMMA1–H1 and PCIE01/PCIE05, which only Redfish's sensor bitmask can express. So HTTPS is used once per service run
  to read, and only when an Apply changes a fan's sensors to write. The owner's BMC_Control project (`F:\Claude\BMC_Control`,
  reference only) confirmed the curve/mode commands over local IPMI; its finding that the BMC ignores stored curves
  in **Generic** mode is noted: OpenHW's fan driver refuses a BMC not in Manual (it is in Manual now).

- **Kernel access, own PawnIO (2026-11-18).** OpenHW loads PawnIO.Modules' signed release 0.2.12 itself
  (`packaging/windows/pawnio/`: AMDFamily17, SmbusPIIX4, LpcIO, LpcACPIEC, LGPL-2.1, copied beside openhw-helper.exe;
  PawnIO's driver and PawnIOLib.dll come from its installer, already here via OpenFan). No LibreHardwareMonitor: a probe
  showed it misreads this CPU (Tctl 85 °C vs 37 °C, bogus SVI2, no CCDs) and has no EC/DIMM/VR support here. The
  service's `kernel` command reports hwmon-style chips (`KernelChannel`), the app's `WindowsKernelSensorsReader` is an
  `ILibSensorsReader`, so `HwmonEngine` (board profiles, names, ids) is Linux's. Shared mutexes `Global\Access_PCI`,
  `…SMBUS.HTP.Method`, `…ISABUS.HTP.Method`, `Global\Access_EC` (HWiNFO/OpenFan use the same). Live: Tctl 41.6 °C,
  CCD 42.3/29.5/28.9/31.0 °C; HSMP FCLK 1950, MCLK 2600, limit 4529 MHz, 71 W.
- **WireView** (COM5, VID 0483/PID 5740 from the registry) and the **UPS** (HID Power Device pages 0x84/0x85; CGS
  units: exponent 7 = volts/watts) read in the app, no service.

- **SMBus map of the WRX90E-SAGE SE (2026-11-18, `openhw-helper --smbus-scan`, read-only receive-byte):** port 0:
  0x0c, **0x20 = XDPE19283B** (IC_DEVICE_ID 01 95, same as the TRX50's), 0x56 (MR0 0xFF, not a hub). Port 1: DIMM
  thermal sensors 0x12/13/16/17, hubs 0x52/53/56/57 (MR0 0x8E), 0x73 (probably a bus switch). Hardware Check on
  Windows now shows the BIOS version and memory size, and Windows notes instead of Linux commands.

- **Port 1 bus switch (owner-approved, 2026-11-18, `openhw-helper --smbus-switch`):** 0x73 is a 4-channel switch
  (masks 0x10–0x80 read back 0), left by the BMC at **0x04** and restored to it. Channel 0 and 2: devices at
  0x12/13/16/17 + 0x52/53/56/57; channel 1: 0x12/13 + 0x52/53; channel 3: nothing. On every channel every register of
  every device reads one repeating word (ch0 `03 00`, ch1 `90 DC`, ch2 `8E DC`): the devices answer their address but
  not the register pointer — likely 2-byte addressing (SPD5118 MR11) or I3C mode set by the BMC. Reading them would
  need a write to each hub's mode register on a bus the BMC uses: not done; needs research and the owner's go-ahead.
  PawnIO's `ioctl_smbus_xfer` wants an output buffer and a data word even for writes (else 0x8007007A).

- **Where HWiNFO reads the DIMMs (its own Debug Mode log, HWiNFO64.DBG, 2026-11-18):** not the PIIX4 SMBus OpenHW
  scanned, but the FCH's **I2C controllers** (AMD DesignWare I2C, "FCH I2C Host", HWiNFO's SMBus hosts 1–3 beside the
  SMBus at 0xB00). There hub 0x50 reads `51 18`, SPD checksums OK; HWiNFO finds DIMM devices on hosts 1 and 2 (≈6 of the
  8 DIMMs): hub temperature, TS0/TS1 and the PMIC. PawnIO has no module for these controllers (and loads only modules
  signed by its author), so OpenHW can't reach them yet. Port 1 of the PIIX4 is a different bus.
- **Crash, 2026-11-18 10:15:** `openhw-helper --hsmp-dimm` sent HSMP 0x18 GetDimmThermal (then 0x17/0x16) to all 256
  DIMM addresses. The SMU took the machine down: WHEA fatal hardware error, Kernel-Power 41, hard reset; the reset also
  zeroed WindowsHelper.cs on disk (restored from git). The probe is gone and must not come back: **never sweep firmware
  mailbox arguments** — find the documented DIMM address encoding (AMD PPR / esmi) and test one value with the owner's
  go-ahead. After the reboot the BMC reader failed its first start (BMC/IPMI driver not ready) and stayed failed;
  `BmcReader` now retries every 30 s on Windows (`WindowsRetryAfter`).

- **The DIMMs' bus, settled (2026-11-18):** the WRX90E-SAGE SE's DDR5 hubs, TS5111s and PMICs are on the FCH's
  **DesignWare I2C controllers** (ACPI `AMDI0010`), not its SMBus: Linux reads them as `AMDI0010:01`/`:02`
  (i2c_designware), HWiNFO probes three "FCH I2C Host" pads (ACPI 0x1E4D/0x1E4F/0x1E51) before its hosts 1–3, and
  `openhw-helper --spd-scan 2` finds no hub on SMBus port 2 (port 0: VR only; port 1: the ASF/BMC-side bus behind the
  0x73 switch). OpenFan never read SPD itself — its DIMM values came from HWiNFO's shared memory (`HwinfoBackend`), and
  on the TRX50 the hubs were on the SMBus PawnIO's SmbusPIIX4 covers. No signed PawnIO module reaches DesignWare I2C:
  the ways forward are a DesignWare module submitted to PawnIO.Modules (tested once with driver signature enforcement
  off for one boot), or a Microsoft-signed driver of our own (EV certificate).

- **RAPL (2026-11-18):** `WindowsRapl` reads MSR_PWR_UNIT, MSR_PKG_ENERGY_STAT and each core's MSR_CORE_ENERGY_STAT
  (pinned to the core) through PawnIO's signed AMDFamily17 (these MSRs are on its read list), totals the wrapping
  32-bit counters and answers `rapl` in Linux's format: package 58.8 W vs HSMP's 56.5 W, cores 5.8 W.
- **Fan Control froze (2026-11-18):** after the reboot the BMC answered on **Ethernet 5** (10.0.0.208) but not Ethernet 4
  (ARP unreachable), which the service had bound to; every `fansources` timed out (~10 s) and Fan Control asks every
  2 s. `LanBinding` now tries every wired port on the BMC's subnet (2 s connect limit, last good first — never the
  Tailscale route, which Chrome uses), and `FanSourcesCache` keeps a failure 60 s. Fan Control still calls the
  service on its UI thread — a slow service shows as lag there (to move off the UI thread).

- **More sensors without new drivers (2026-11-18):** RAPL (package 58.8 W vs HSMP 56.5 W), effective clocks (idle
  2–700 MHz per core, ~49 MHz average), the ASUS EC's VRM1/VRM2/USB thermistor (39/36/46 °C vs HWiNFO's 38/35/45; three
  known registers only, ACPI EC read command, median of three, every 5 s; M.2/USB4 fans and T_Sensor are already in the
  BMC group), and WHEA counts (0 since boot, 2 corrected in 30 days, 1 fatal — the 10:15 crash).

## Next steps

In order; anything that writes to the BMC, the CPU, the GPUs or a bus device only with the owner's go-ahead and with
the owner present. **Never sweep** firmware mailboxes, MSRs, EC/Super I/O registers or bus addresses (the 2026-11-18
crash): one documented value at a time, and commit before any hardware test.

1. **On the next Linux boot**: run both test suites (expected unchanged — every Windows change is gated); check OpenHW
   there loads the shared fan configuration `/mnt/DriveC/StandAlone/OpenHW/configs` (Fan Control's title shows
   `fans.json`); compare a drive's `nvme-health:<id>` with the Windows ids.
2. **DIMMs — the one-boot test (owner)**: Memory Integrity off (Windows Security → Core isolation) and restart; restart
   into Startup Settings and press 7 (driver signature enforcement off for that boot); then elevated
   `packaging\windows\pawnio-test\test-dimm-read.ps1 install`, `read` (I2C1 0x50 MR0/MR1 — expect `51 18`), `remove`;
   Memory Integrity back on, normal restart. If it reads: Windows DIMM discovery (hubs 0x50–0x57 on I2C1/I2C2, SPD
   header via the MR11 page select Linux's spd5118 uses), the per-DIMM rows through `Ddr5Reader`, then submit
   `packaging/windows/pawnio-src/AMDI2C.p` to PawnIO.Modules for signing.
3. **Fan control live** (owner present): one fixed duty on one zone and release; one curve and restore the original;
   one alarm threshold and restore — the same flash-write rules as Linux.
4. **GPU power limit live**: one card, Apply then Default; "Apply and keep" then a service restart to see it reapplied.
5. **OpenFan vs OpenHW on Windows**: OpenFan is still running and OpenHW drives both RTX PRO 6000 cards' fans — confirm
   OpenFan isn't also driving them (`GpuFanDriver`'s OpenFan guard only knows OpenFan's Linux socket); retire OpenFan
   once 3–4 are confirmed.
6. **Fan Control's service calls off the UI thread**: it asks the service on its window thread, so any slow answer
   shows as lag (seen 2026-11-18 while Redfish timed out).
7. **BIOS Setup values**: UEFI variables through the service (`uefivars`, SeSystemEnvironmentPrivilege).
8. **Super I/O NCT6798D** through PawnIO's LpcIO (Linux's nct6775 decoding + the WRX90E board profile) — low value here,
   the BMC reads the rails, temperatures and fans.
9. **HWiNFO coexistence**: re-check BMC, SMBus and EC reads with HWiNFO running (the shared mutexes, the IPMI retries).
10. **Phase 4, installer** (Inno Setup is installed): the app to StandAlone or Program Files, the service (start/stop
    rights for OpenHW Users), the group, autostart, a PawnIO check; uninstall gives fans back.
11. **Phase 5, parity with HWiNFO**, then uninstall HWiNFO; retire OpenFan on Windows.

Not reachable with signed modules (left unless a module appears): the chipset (xHCI) temperature, SVI3 telemetry and
per-core/L3 temperatures (the SMU's PM table — the RyzenSMU module works through the SMU mailbox; only with AMD/PawnIO
documentation for this CPU and the owner present).

## Goal and scope (owner, 2026-11-16)

Run OpenHW on **Windows 11 on the same workstation** (the owner dual-boots; HWiNFO 64 8.54 is installed there and was
studied on 2026-10-23 — [hwinfo-research.md](hwinfo-research.md)) so HWiNFO is no longer needed:

- **In scope**: the sensor window (HWiNFO layout, groups, Current/Min/Max/Average, graphs, row menu, Layout / Customize
  values, Hardware Check), **tray icons** (with real hover tooltips — Windows has them natively), **CSV logging**,
  **alerts**, **fan control** (BMC zones + NVIDIA fans, curves, alarm thresholds), the **BMC status / event log**,
  **BIOS Setup** (read-only), the **WireView Pro II** and **UPS** groups.
- **Out of scope**: GPU Lending (eBPF card filter, libvirt — the Windows install *is* where a lent card would go),
  CPU frequency caps, power profiles.
- **Added 2026-11-17**: CPU and GPU **power limits** (OpenHW replaces OpenFan on Windows too; the CPU limit set by
  OpenHW, not Ryzen Master).

Success = the owner can uninstall HWiNFO: every group they use there has an OpenHW equivalent that reads the same
values (compare side by side with HWiNFO's shared memory, `tools/loadgen/hwinfo_shm.py`).

## Where the code stood (the plan, 2026-11-17 morning)

The sections from here on are the original plan, kept for its reasoning; "Status at a glance" above says what is done.

- **UI**: Avalonia 11.3 / .NET 8 — runs on Windows as is. `Platform.cs` in OpenHW.App, OpenHW.Hardware and the
  helper declares `[assembly: SupportedOSPlatform("linux")]`; that has to become per-file/per-class (done).
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

**Update 2026-11-17**: this PC runs Windows 11 Pro with Memory Integrity (HVCI) **on**, and PawnIO is **already
installed and loaded** (by OpenFan for Windows, whose `AMDFamily17.bin` module reads SMN for Tctl/CCD) — so PawnIO
works with HVCI here, and direct PawnIO modules (option 2, as OpenFan does) are now the simpler start. The **HSMP
mailbox doesn't need PawnIO at all**: AMD's own WHQL driver (AmdHSMPDriver.sys) serves it — that is how the CPU power
limit works on Windows.

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
  it Redfish is reachable only over the LAN. **Done**: the service asks the BMC for its LAN address (IPMI) and binds
  to the wired port on that subnet (bypassing Tailscale's subnet route); `%ProgramData%\OpenHW\bmc-url` overrides.
- **Other fan/BMC users on Windows**: OpenFan for Windows exists and runs on this PC (NVIDIA fans, ASUS EC, GPU
  limits), HWiNFO polls the BMC's KCS interface (collides with OpenHW's IPMI), and ASUS's tools (Armoury Crate,
  AI Suite) can drive fans — use one fan controller at a time; Hardware Check should warn when another one is running.
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

1. ~~Windows edition and HVCI~~ — answered by looking: Windows 11 Pro 26200, Memory Integrity on.
2. OK to depend on PawnIO (already installed by OpenFan; a third-party signed driver) — and LibreHardwareMonitorLib or
   only PawnIO's modules — for Super I/O / Tctl / SMBus? (Owner: "don't limit the design because it needs
   Administrator"; this one is about trusting a third-party driver.)
3. ~~One fan configuration or two~~ — one, shared (owner, 2026-11-17): `C:\StandAlone\OpenHW\configs`.
4. Is code signing wanted (certificate cost), or is an unsigned installer fine for personal use?
5. ~~BMC LAN address~~ — OpenHW discovers it over IPMI (10.0.0.200 on this network); `bmc-url` overrides.
6. When to retire OpenFan on Windows (after fan control and GPU limits are confirmed in OpenHW)?

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
