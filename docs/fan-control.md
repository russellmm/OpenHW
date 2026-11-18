# Fan control — design

How OpenHW controls fans on the owner's ASUS Pro WS WRX90E-SAGE SE, what the hardware actually does, and why the
design looks the way it does. Status as of 0.32.0 (2026-11-17). Plan and history: [ROADMAP.md](../ROADMAP.md)
"Fan control"; release notes: [CHANGELOG.md](../CHANGELOG.md).

## Goals (owner)

- **OpenHW takes over the fans; eventually only OpenHW runs** (OpenFan retired). OpenFan's code may be reused
  (owner, 2026-11-16: "OpenFan is our code, so we can copy it all we want").
- **One fan driver**, in openhw-helper, that every program writing fan settings goes through.
- **Spare the BMC's flash**: fan settings live in flash (below), so prefer settings the BMC runs itself, written once.
- Curves from many sensors, as in OpenFan — e.g. DIMM temperatures for the fan on CHA_FAN1, CPU temperature for
  CPU_FAN and CHA_FAN2.
- Later: the same on Windows 11 (sensors and fan control only).

## What drives the fans on this board

| Part | Role for fans | Evidence |
|---|---|---|
| **AST2600 BMC** (onboard, AMI MegaRAC firmware 2.01) | **Drives every header** (7 zones) from 5-point curves | Chassis fans ramp under load while the Super I/O's duty stays fixed; IPMI/Redfish writes move each fan |
| NCT6798 Super I/O | **Measures only** (fan1–7 tachs) | Its `pwm2` at 60 %/100 % left CPU_FAN unchanged (guarded test, 2026-11-14); all its curves follow a dead input (temp8, flat 11 °C) |
| ASUS EC (`asusec`) | Readings only (CPU_OPT, USB4, M.2, VRM heatsink fans) | No PWM controls exposed |
| BIOS Q-Fan | Configures CPU_FAN and W_PUMP+; **hides chassis-fan settings** here | Hide rule on internal Setup byte 215 (= 1; factory default 0) |

The board has no "Onboard Fan Control Source" switch (ASUS E26328 mentions one for add-in IPMI cards), so the
Super I/O can't take the fans over. Replacing the BMC firmware (OpenBMC) or writing BMC registers over ASPEED's P2A
PCIe bridge were ruled out.

### Zones

| Zone | Header(s) | Firmware label | BMC fan sensor |
|---|---|---|---|
| bmc:0 | CPU_FAN + CPU_OPT (one zone) | Zone1(CPU_FAN & CPU_OPT) | CPU_FAN |
| bmc:1–5 | CHA_FAN1–5 | Zone2–6(CHA_FANn) | CHA_FANn |
| bmc:6 | W_PUMP+ | Zone7(W_PUMP+) | W_PUMP+ |
| (7) | — | N/A | never a control |

All six connected zones were checked on the real board (each moved its own fan, curve restored byte for byte).
CHA_FAN4 has no fan.

## Interfaces, as the real BMC behaves

### IPMI (in-band, `/dev/ipmi0`, no login) — curves

ASUS OEM commands (ASUS E26328 IPMI manual V3, July 2025, §3.7.17 and appendix A), netfn 0x30:

| Command | Use | Notes |
|---|---|---|
| `30 13 01 00` | zone count | 8 |
| `30 13 03 00` | zone order | `ff 00 01 … 07` |
| `30 13 00 N` | zone table | 32-byte name, PWM source (ignore), TCC max, active curve (10 bytes), generic curve |
| `30 0f N` | read zone | mode (0x00 Generic, 0x03 Full speed, **0x04 Manual**) + 5 × (temperature, duty) |
| `30 0e 04 N` + 10 bytes | write zone curve | read back after every write |
| `30 93` | temperature source | **read refused (0xC7)** on this firmware; set form not used (it sets all 23 PWMs at once) |

Firmware rules: temperatures rise, the **last point is at 100 °C**, duties 0–100 but **any 0 % point is refused
(0xD9)**. Point 4 is free: the BMC web console edits points A–D (D's temperature too) and shows E at 100 °C (owner's
screenshot, 2026-11-17). Up to 0.24.x OpenHW, like OpenFan, also required point 4 = 70 °C only because every curve
it had read looked like that — never tested. **Confirmed 2026-11-17**: CHA_FAN1 written with 50:30 55:52 60:74 80:100
100:100 through IPMI, read back identical. (The app's refit-at-70 fallback, `FourthAt70`, stays as a safety net.)
Identity checked before any write: board vendor/name, ASUS 2623 / product 0x1193, firmware
2.01, aux bytes `22 00 00 00`, zone count/order, each table's name and curve temperatures.

### Redfish (BMC web API) — temperature sources

- Reached over the BIOS's **Redfish Host Interface**: a USB network link inside the machine, the BMC at
  **https://169.254.0.17**. The Linux interface name changes every boot (`enx` + random MAC) — never pin it. Under
  Windows the same service answers on the BMC's LAN address (10.0.0.200 on the owner's network).
- **Reads need no login over the host interface** (even bogus credentials pass); over the LAN they do. So only a
  session login (POST `SessionService/Sessions`: 201 / 401) proves a password. Why (found 2026-11-17): the host
  interface is in **`AuthNone`** mode with credential bootstrapping as `HostInterfaceAdministrator` — the owner keeps
  it that way (Hardware Check notes it). Since 0.36.0 `BmcRedfish.Get` retries a read in a session with the stored
  login on 401/403, so locking it down later breaks nothing. More in [bmc.md](bmc.md).
- `Chassis/Self/Thermal` → `Oem.Mode` ("Manual") and `Oem.PWM[8]` with `Index`, `Source`, `Temperature1–5`,
  `FanDuty1–5`. `Allow: GET, PATCH`; carries an ETag.
- **`Source` is a bitmask over the BMC's temperature sensors: bit = SDR sensor number − 1** — CPU Package Temp (1) →
  bit 0, TR1 = T_Sensor header (2) → bit 1, LAN Temp (3) → bit 2, DIMMA1–H1 (4–11) → bits 3–10, PCIE01–07 (12–18) →
  bits 11–17. Several bits = several sensors (the hottest counts). Unused slot 7 reads 0xFFFFFF. Decoded with the
  owner in the BMC web console (CHA_FAN1 → DIMMA1 = 8, + DIMMB1 = 24). The BIOS's Q-Fan source codes (0 CPU,
  17 DIMM Group 1, …) are a different numbering and don't apply to the BMC-owned chassis fans.
- **PATCH format the BMC accepts** (learned 0.24.0–0.24.2): `{"Oem": {"Mode": <as read>, "PWM": [<one complete
  entry as read, Source changed>]}}` with `If-Match: <ETag>`. Without `Mode`: 400 "Oem/Mode is a required property".
  An entry without all fields: 400 "Oem/PWM/n/Temperature1 is a required property". The whole list: refused over
  slot 7's values (while the valid entries are still applied).
- `Systems/Self/Bios` exposes ~1,300 Setup attributes once BIOS "Publish HII Resources" is on (File → BIOS Settings
  from the BMC…, 0.36.0); ASUS Q-Fan items aren't among them. The full BIOS screen (File → BIOS Setup…, 0.37.0) reads
  the machine's UEFI variables instead — [bios-setup.md](bios-setup.md).

### Flash

Fan settings are **persistent**: a flat 70 % on CHA_FAN1 survived `ipmitool mc reset warm` (BMC gone ~30 s) and
`mc reset cold` (~50 s). So every curve or source write is a flash write. No volatile "set duty now" command exists
in ASUS's documentation. AMI BMCs keep settings on a log-structured flash filesystem (wear spread across the
partition), but endurance isn't documented — hence the write discipline below.

## Architecture

```
OpenHW app ──(Unix socket, protocol 18)──► openhw-helper (root, sandboxed)
  Power → Fan Control… (one screen)            FanDriver ── AsusBmcFans ── IIpmiTransport ── /dev/ipmi0   (BMC fans)
    FanCurveRunner: BMC-run curves → Apply     BmcRedfish ── HTTPS (pinned cert) ── 169.254.0.17         (sources)
                    driven curves → Tick        GpuFanDriver ── LiveNvmlFans ── NVML                       (NVIDIA fans)
  openhw --fan-test
OpenFan: retired from fan control (its helper stopped 2026-11-17); OpenHW refuses to share fans with it.
```

- **`AsusBmcFans`** (`src/OpenHW.Hardware/Fans/`): identity checks, `Read`, `Write` (+ readback). Ported from OpenFan
  0.4.6's `AsusBmcController` onto OpenHW's own IPMI transport (no `ipmitool`).
- **`FanDriver`**, two kinds of write:
  1. **Takeover** (stage 1; `--fan-test` and OpenHW-driven curves): a client token asks for a fixed duty
     (flat curve). The zone's curve is journaled first (`/var/lib/openhw/fan-restore.json`) and restored when the
     client releases it, stops renewing its lease (15 s), the helper stops, or — after a crash — the helper starts.
     One owner per zone; nothing taken over while OpenFan's own BMC journal exists. **Flash limits**: faster duty at
     once; slower only 30 s after the last write and ≥ 3 % lower ("pending" meanwhile).
  2. **Configured curve** (stage 2): `SetCurve` writes a zone's own 5 points permanently; the BMC runs it from then
     on. The curve before OpenHW's first change is saved once in `/var/lib/openhw/fan-original.json` (machine
     identity checked) for `RestoreOriginal`. **Floors**: CPU_FAN ≥ 20 %, W_PUMP+ ≥ 50 %. Refused while a client
     holds the zone. Identical curve → no write.
  Writes per zone are counted and shown.
- **`BmcRedfish`**: `ReadSources` (no login), `SetSource` (session login → GET + ETag → PATCH one complete entry
  with Mode → GET verify → DELETE session), `CheckLogin`. The BMC's certificate is pinned on first use
  (`/etc/openhw/bmc-cert.sha256`).
- **`BmcLogin`**: the helper's BMC user for Redfish writes, `/etc/openhw/bmc-login` (root, 0600). Handed over from
  Fan Control (typed, or a two-line file: user, password); checked with a real session before it's kept; never
  echoed. The owner's BMC user for this is `openhw` (Administrator; Redfish works, the web console refuses it — an
  AMI web-access setting, not needed). Claude doesn't sign in to the BMC itself; authenticated checks are the
  owner's (`tools/bmc-probe/login-test.sh`).

### Helper commands

| Protocol | Command | Effect |
|---|---|---|
| 13 | `fans [token]` | zones: owner, target/pending, active and journaled curves, writes, saved original, floor |
| 13 | `fanset <token> <zone> <1–100>` / `fanrelease <token> <zone\|all>` / `fanbeat <token>` | takeover, lease |
| 14 | `fancurve <zone> <t:d,t:d,t:d,t:d,100:d>` / `fanoriginal <zone>` | configured curve / restore original |
| 14 | `fansources` / `fansource <zone> <mask>` | read / set sources (Redfish) |
| 14 | `bmclogin [b64 user b64 password]` / `bmclogout` | which user is stored / store (after a real login) / forget |
| 15 | `gpufans [token]` / `gpufanset <token> <nvml:uuid:fan:n> <0–100>` / `gpufanrelease <token> <id\|all>` | NVIDIA fans (NVML) |
| 17 | `bmcthresh` / `bmcthresh <sensor> <lnr> <lcr> <lnc>` / `bmcthreshoriginal <sensor>` | fan alarm thresholds (RPM) |

(Protocol 16 `bmcstates`/`bmcsel` and 18 `biossettings`/`bmcaccess` are read-only BMC commands — [bmc.md](bmc.md).)

### Sandbox

The helper unit allows network **only** to the BMC's link-local host interface: `RestrictAddressFamilies=AF_UNIX
AF_INET`, `IPAddressDeny=any`, `IPAddressAllow=169.254.0.0/16` (was `PrivateNetwork=true` before 0.24.0).
`DeviceAllow=char-ipmidev rw`; `StateDirectory=openhw` (0700) for the journal and saved originals.

## UI

Power → Fan Control… (also in the tray menu) is one screen, laid out like OpenFan's Home — see "Curves" below for the
details: **fan cards** (BMC zones and NVIDIA fans: Curve box + curve, duty/RPM, where it runs, Apply, friendly name,
⋮ move / Edit BMC curve… / Restore original), **curve cards** (flat, graph incl. bulk sensors, mix with its BMC preview),
New/Duplicate, drag-and-drop order, and configurations in the window's ⋮. A BMC fan's own 5-point curve can still be
edited directly (⋮ → Edit BMC curve…: sensor checkboxes as in the web console, five points with point 5 at 100 °C,
Apply writes only what changed). Every dialog opens centred on the primary monitor the first time and then where it was
last moved (owner rule).

## Alarm thresholds (0.35.0)

The BMC logs a fan as slow / failing / stopped (SEL "lower non-critical / critical / non-recoverable going low") when
its RPM drops below the SDR thresholds — **360 / 360 / 0 RPM** on every header by default, which a quiet fan at a
20 % floor trips after each boot (CHA_FAN2 at 360–374 RPM, CHA_FAN3). The SDR marks the three lower thresholds
**settable** on CPU_FAN, CPU_OPT, CHA_FAN1–5 and W_PUMP+ (not USB4/VRM HS/M.2/PSU fans), stored in **120 RPM steps**
(M = 120).

- **Write**: IPMI Set Sensor Thresholds (04h/26h), mask = settable lower bits, then Get Sensor Thresholds to read back
  (`IpmiCommands.SetLowerThresholds`, `BmcReader.SetFanThresholds`). Only values the conversion maps exactly to a raw
  byte are accepted; order non-recoverable ≤ critical ≤ non-critical; same values → no write. Every write is logged in
  the helper's journal ("CHA_FAN2 lower thresholds 0/360/360 → 0/120/240 RPM").
- **Originals**: saved once per fan before the first change (`/var/lib/openhw/bmc-thresholds-original.json`, keyed by
  the DMI product UUID) for Restore original.
- **UI**: fan card ⋮ → Alarm thresholds… (`FanThresholdDialog`): dropdowns of storable values up to 3,000 RPM.
- **Live (2026-11-17)**: CHA_FAN2 and CHA_FAN3 set to 0 / 120 / 240; the BMC deasserted "lower critical" right away.
  **Not yet known**: whether they survive a BMC reset or AC loss — check after the next cold boot.
- Effects: on this board a crossing was only seen to add SEL entries (the fans kept their curves).

## App-side behaviour added in 0.34.0

- **Own-core stand-in**: while a GPU fan's curve misses only BMC slot sensors (`bmc:PCIE…`, e.g. ~20 s after a start
  while the BMC's SDR read runs, or after a BMC reset), those read the fan's own card's NVML core temperature
  (`FanCurveRunner.OwnTempReading`). Assumes the curve follows its own card's slot (true for "GPU Slot 1/5"); logged
  both ways ("no BMC slot reading yet…", "BMC slot reading back…").
- **SIGTERM/SIGINT** run the normal exit (`PosixSignalRegistration`), so every driven fan is released at once
  ("released N fan(s) (exiting)"); a 5 s fallback releases and exits if the UI thread is stuck.

## PCIe slot sensors = GPU core temperatures (measured 2026-11-17)

`tools/gpu-slot-test/` loaded each GPU in turn (FMA loop at its 300 W / 180 W limit) and logged nvidia-smi next to the
BMC's slot sensors every 5 s. Each slot sensor read **its GPU's core temperature within about 1 °C, rising and falling
within one sample** (the BMC almost certainly reads the GPU over the slot's SMBus), and the others didn't move:

| Slot (owner) | Card | BMC sensor | Idle → load |
|---|---|---|---|
| 1 | RTX PRO 6000 (F1:00.0) | PCIE01 (bit 11) | 41 → 64 °C |
| 4 | RTX 5060 Ti (11:00.0) | PCIE04 (bit 14) | 44 → 70 °C |
| 5 | RTX PRO 6000 (C1:00.0) | PCIE05 (bit 15) | 33 → 62 °C |

So GPU-following curves run on the BMC; OpenHW-driven duties are only needed for sensors the BMC really can't see.
Not yet checked: whether a slot still reads while its card is lent to the VM.

## Curves (0.25.0–0.32.0)

- **Model** (`Fans/FanCurves.cs`, ported from OpenFan.Core): `FanCurve` = Graph (points over one sensor or the hottest of
  several, linear, flat outside, max speed, hysteresis °C/s) | Flat | Mix (max/min/average of non-mix curves). `FanCurveMath.Evaluate`,
  `FanHysteresisGate` (OpenFan's HysteresisGate). Saved in `fan-curves.json` (`FanCurveStore`: curves,
  zone → curve, `FourthAt70`, writes per day) — since 2026-11-17 in the folder Linux and Windows share
  (`OsFiles.SharedConfigDir`: `C:\StandAlone\OpenHW\configs` = `/mnt/DriveC/StandAlone/OpenHW/configs`; the file in
  use is saved relative to it; first start copies this OS's own files there unless it already has a live file).
- **BMC fit** (`Fans/BmcCurveFit.cs`): a curve runs on the BMC when every sensor it reads is a BMC temperature sensor
  (`bmc:<SDR name>` with an SDR number 1–18). The BMC follows the hottest of its sensors through one curve, so the
  target is g(T) = the curve with every sensor at T (rising: running max; at least the zone's floor) — for rising
  child curves that's an upper bound of any max/min/average mix. The fit tries 4-knot combinations (≤ 36 candidate
  temperatures between where g starts to rise and where it tops out) plus the fixed 100 °C point, raises segments
  until the BMC line (flat before point 1, linear between) is ≥ g everywhere, and keeps the one with the least excess.
  Owner's curves: GPU Slot 5 within 1 %, the big CPU+GPUs+35 % mix within 6 %.
- **Runner** (`App/Power/FanCurveRunner.cs`): BMC-run → `ApplyBmc` (Redfish source if different, IPMI curve if
  different; once). OpenHW-driven → `Tick` every poll (app side, timer thread): evaluate, round up to 5 %, hysteresis
  gate, faster at once, slower only ≥ 5 % and ≥ 60 s after the last change (the driver's 30 s / 3 % rules still apply
  below), counted per day; lease renewed by `fanbeat`; no readings for 20 s → release; app exit → release. Each start
  and stop of a driven fan costs a write (takeover / restore).
- **UI (0.26.0, owner: "a single screen like OpenFan has")**: Power → Fan Control… = OpenFan's Home — control cards
  (BMC zones + NVIDIA fans; Curve box + curve dropdown, duty/RPM, where it runs, Apply when a BMC-run curve isn't on the
  BMC yet, ⋮ Edit BMC curve… / Restore original) and curve cards (flat −/+, graph with source, output, mini preview with
  the live reading as a dot, Edit → graph editor with the green dashed BMC version; mix with function, add, ×). A BMC
  assignment asks first (what's written, or the flash-write warning). 0.27–0.28: Fan Control… in the tray menu;
  + Flat/Graph/Mix menus (New / Duplicate); friendly fan names (store `Names`, tooltip = original + id).
  0.29.0: configurations (`FanCurveStore.Export` / `Import` / `StartNew`, `ConfigFile` = the file in use, auto-saved on
  every change as in OpenFan; default folder `~/.config/openhw/fan-profiles/`); loading never writes the BMC — BMC fans
  show Apply, and Apply all writes the pending ones after one confirm. 0.30.0: card order (`ControlOrder`, card ⋮ Move
  left/right/start/end; absent fans keep their place; part of configurations). 0.31.0: drag and drop for fan and curve
  cards (`CardDrag`: plain pointer events, 6 px threshold, orange side bar on the target; `CurveOrder`).
- **Bulk sensors (0.32.0)**: `FanCurve.SensorIds` (`Inputs`; `SensorId` = the first, for older files) — a graph's input is
  the hottest reading (`FanCurveMath.Hottest`); on the BMC that's the source bitmask with all of them, so it's exact.
  Picker: `SensorPicker` (checkbox flyout, BMC first, All DIMMs / All PCIe slots).
- **Mix cards (0.32.0)**: preview of g(T) and the fitted BMC curve (`FanCurveRunner.PlanCurve`, cached), and
  `MixWarning`: Min/Average over different sensors, or Max over different curves on different sensors, can't be kept by
  the BMC (never slower; can be faster). Same curve on different sensors = bulk sensor = exact.
- **NVIDIA fans (0.26.0)**: `GpuFanDriver` in the helper (protocol 15: `gpufans`, `gpufanset <token> nvml:<uuid>:fan:<n>
  <0–100>`, `gpufanrelease`; `fanbeat` renews both drivers' leases). NVML `nvmlDeviceSetFanSpeed_v2` / `…SetDefaultFanSpeed_v2`
  (0 % = automatic, others ≥ the card's minimum, from `nvmlDeviceGetMinMaxFanSpeed`). Released fans, expired leases
  (15 s), helper stop and a lend (`Suspend` before NVML closes) all go back to automatic. Refused while
  `/run/openfan/helper.sock` exists (OpenFan writes GPU fans too). App side: always OpenHW-driven, the curve's
  hysteresis is the only gate (as in OpenFan), no steps or budget.

## The hybrid plan (owner, 2026-11-16)

1. **Fans whose curve follows a sensor the BMC knows** — CPU Package, T_Sensor, LAN, any DIMM slot, any PCIe slot —
   are **BMC-run**: OpenHW writes the curve and the sources once. Done in 0.24.x.
2. **Fans that follow sensors the BMC can't see** (GPU core temperatures, NVMe, mixes) get **OpenHW-driven duties**
   through takeover, under a write budget (coarse duty steps, wide hysteresis, slow-downs held, a visible daily
   count). **Done in 0.25.0, app-side** (the app runs from login in the tray; if it stops, the lease gives the fan back);
   the PCIe slot sensors turned out to read the GPUs, so GPU curves don't need this. (The planned import of the
   owner's OpenFan profile was dropped: owner, 2026-11-17, "build the capability, don't copy my curves".)
3. ~~OpenFan becomes a client of the driver~~ — not needed: OpenHW took over BMC and NVIDIA fans (0.26.0) and the
   owner stopped OpenFan's helper; what's left is disabling it for good.

## Windows 11 (later)

Same driver logic behind the existing seams: `IIpmiTransport` → Windows' IPMI driver (WMI `Microsoft_IPMI`,
RequestResponse), Redfish → the BMC's LAN address with the `openhw` login. Only the sensor window and fan control are
wanted there (no GPU Lending, CPU frequency or power limits).

## Testing

- `tests/OpenHW.Tests/FanDriverTests.cs`: a fake BMC built from OpenFan's capture of this firmware
  (`Fixtures/asus-wrx90-bmc.json`, no credentials or serials) — identity, takeover/release, leases, flash limits,
  crash recovery, journal identity, refused/wrong writes; configured curves, floors, saved originals.
- `tests/OpenHW.Tests/BmcRedfishTests.cs`: a fake AMI Redfish service that enforces what the real one does (Mode
  required, complete entries, slot 7 refused, ETag) — request sequence, readback, logins; `BmcRedfishLiveTests` reads
  the real BMC read-only when `OPENHW_LIVE_BMC=1`.
- `tests/OpenHW.Tests/FanCurveFitTests.cs`: the BMC fit on the owner's curves (never slower, point 4 free, bulk sensors
  exact, point-4-at-70 refit); `GpuFanDriverTests.cs`: NVIDIA fan leases, clamping, OpenFan/lend refusals, helper commands.
- `tests/OpenHW.Tests/BmcTests.cs`: thresholds on a fake BMC (steps, order, not-settable, read-back, originals per
  machine, helper validation); `tests/OpenHW.App.Tests/FanThresholdDialogTests.cs`: the dialog.
- `tests/OpenHW.App.Tests/FanControlTests.cs` / `FanCurveTests.cs` (incl. the own-core stand-in): the screen (BMC + GPU cards, curve cards, mix
  warnings, names, configurations, Apply all, headless mouse drags), the runner's write rules, the Edit BMC curve dialog.
- `tools/gpu-slot-test/`: BMC slot sensors vs GPU core temperatures under load.
- Real-hardware checks run by the owner: `openhw --fan-test bmc:N <duty> --seconds N`,
  `tools/fan-probe/cpu-fan-test.sh` (the Super I/O test), `tools/bmc-probe/` (`dump.sh`, `match.py`,
  `login-test.sh`).
