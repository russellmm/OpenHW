# The BMC: status, event log and Redfish (WRX90E-SAGE SE, AST2600)

What OpenHW reads from the board management controller besides its threshold sensors and fan zones (those are in
[fan-control.md](fan-control.md)), and how the BMC behaves. Board: ASUS Pro WS WRX90E-SAGE SE, BIOS 1405, ASPEED
AST2600, BMC firmware 2.01, AMI MegaRAC (Redfish server "AMI Redfish Server", Redfish 1.11, RTP 13.03).

## Paths in

| Path | From Linux | Login | Used for |
|---|---|---|---|
| IPMI over KCS, `/dev/ipmi0` | root only (the helper) | none | SDR sensors, discrete states, SEL, fan curves, alarm thresholds |
| Redfish over the host interface, `https://169.254.0.17` | any process | **none needed** (AuthNone, see below) | fan sources, BIOS attributes, access check |
| Redfish over the LAN (10.0.0.200 on the owner's network) | — | yes | the Windows port, later |
| UEFI variables, `/sys/firmware/efi/efivars` | any process (read) | — | BIOS Setup's current values ([bios-setup.md](bios-setup.md)) |

The host-interface NIC (`enx` + random MAC, 169.254.x) is renamed every boot: never pin it.

## Discrete sensors (0.34.0, helper protocol 16 `bmcstates`)

Besides the threshold sensors, the SDR holds **19 discrete sensors** (Full 01h / Compact 02h records whose
event/reading type isn't 01h): PSU1/2 Slow FAN1, AC Lost, Over Temp, Detect, Over Curr; CPU_ECC; Memory_Train_ERR
(OEM type C5h); BIOS OOB, CPU_Hardware, Mem_Hardware, PCIE_Hardware (version change); Watchdog2. Get Sensor Reading
returns their asserted-state bits (offsets 0–14) in bytes 3–4; `IpmiEvents.States` turns them into words with the
IPMI 2.0 generic (table 42-2) and sensor-specific (42-3) tables. Read every sweep with the threshold sensors.

Live (2026-11-17): CPU_ECC "presence detected"; **PSU1/PSU2 Detect "failure detected"** while every PSU reading is
empty — the PSU isn't on the BMC's PMBus, so these flags don't describe it (the window says so); the rest clear.

## System Event Log (0.34.0, `bmcsel`)

- Get SEL Info (0Ah/40h) → entries, free bytes, last add / last erase times; Get SEL Entry (0Ah/43h) with reservation
  0000 and whole 16-byte records, walked by record id. Re-walked only when last-add/erase/count change (218 entries in
  < 1 s). The newest 500 go to the window.
- Type 02h records decode to time, generator, sensor (by SDR number → name), event/reading type and offset; data1
  bits 7:6 = 01b means byte 2 is the trigger reading. C0h–DFh / E0h–FFh are OEM (shown raw).
- **Timestamps are UTC** (checked: newest entry = this boot's start in PST). Values ≤ 0x20000000 are "seconds since
  BMC start". The log was last cleared 2026-08-01.
- **Nothing clears or adds to the SEL.** File → BMC Status & Event Log… is read-only.

## Redfish over the host interface (researched 2026-11-17)

- **`AuthenticationModes: ["AuthNone"]`, `CredentialBootstrapping.Enabled = true` as `HostInterfaceAdministrator`**:
  any program on the PC can read — and most likely write — Redfish without a password. **The owner keeps it this way**
  (2026-11-17); Hardware Check shows it as a plain "BMC access" note (0.36.1). The BIOS's own Redfish options
  (REDF000 "Redfish" Enabled, REDF001 "Authentication mode" = Basic) don't control it: they are how the *BIOS* signs
  in to publish its data. If it's ever locked down, OpenHW keeps working: `BmcRedfish.Get` retries reads in a session
  with the stored `openhw` login on 401/403 (0.36.0); writes always use a session.
- **The Manager Audit Log** (`Managers/Self/LogServices/AuditLog`) keeps **150 entries** and logs every in-band
  request (user "in-band-user"). A 700-page crawl on 2026-11-17 wrapped it — **crawl sparingly**.
- What's there (crawl of 700 resources, saved locally, not committed):
  - `Systems/Self/Bios` — **1,295 Setup options** with current values once Setup → "Publish HII Resources" is on;
    registry `BiosAttributeRegistryA5496.14.5.0` (`Registries/…`, file served as **stored gzip**, ~0.57 MB, no
    Content-Encoding). Setup checkboxes come as true/false values while the registry claims choices "None"/"Disabled".
    Some strings carry bytes after a NUL. Parsed by `BiosSettingsParser` (File → BIOS Settings from the BMC…).
  - `Systems/Self/Memory` — 8 × KF556R28-32 at 5200 MT/s, ranks, part numbers (not used yet; ROADMAP).
  - `Systems/Self/Storage` — NVMe inventory as the BIOS saw it at boot (OpenHW reads drives directly).
  - Memory / Processor Metrics: **404**. `Chassis/Self/PCIeSlots`: garbled (C1 listed under "slot 1" twice, 11:00
    under an M.2 slot) — not usable for slot → GPU mapping.
  - `Chassis/Self/Power`: 20 W "consumed", both PSUs absent. Thermal / Sensors / Telemetry: the same as IPMI.
  - Log services: IPMI SEL (as above), Audit Log, empty Event/BIOS logs.

## Code

- `src/OpenHW.Hardware/Bmc/BmcEvents.cs` — `BmcDiscreteSensor`, `IpmiEvents` (states, SEL info/walk/decode, tables).
- `src/OpenHW.Hardware/Bmc/BmcReader.cs` — helper side: SDR once, background sweeps, `States`, `Sel`, fan thresholds.
- `src/OpenHW.Hardware/Bmc/BiosSettings.cs` — Redfish BIOS attributes + registry → `BiosSettingsReply`; `BmcAccessReply`.
- `src/OpenHW.Hardware/Fans/BmcRedfish.cs` — `Get` (open, else session), `ReadAccess`, `ReadBiosSettings`, sources.
- `src/OpenHW.App/HardwareCheck/BmcLogWindow.cs`, `BiosSettingsWindow.cs`, `BmcInfo.cs`, `Coverage.cs` (access note).
- Tests: `BmcTests` (discrete, SEL, thresholds on a fake KCS BMC), `BiosSettingsTests` (fake AMI Redfish: AuthNone vs
  locked, gzip registry, checkboxes), `BmcLogWindowTests`, `BiosSettingsWindowTests`.
