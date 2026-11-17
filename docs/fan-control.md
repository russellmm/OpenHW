# Fan control — design

How OpenHW controls fans on the owner's ASUS Pro WS WRX90E-SAGE SE, what the hardware actually does, and why the
design looks the way it does. Status as of 0.24.3 (2026-11-16). Plan and history: [ROADMAP.md](../ROADMAP.md)
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

Firmware rules: temperatures rise, the last two points are fixed at **70 and 100 °C**, duties 0–100 but **any 0 %
point is refused (0xD9)**. Identity checked before any write: board vendor/name, ASUS 2623 / product 0x1193, firmware
2.01, aux bytes `22 00 00 00`, zone count/order, each table's name and curve temperatures.

### Redfish (BMC web API) — temperature sources

- Reached over the BIOS's **Redfish Host Interface**: a USB network link inside the machine, the BMC at
  **https://169.254.0.17**. The Linux interface name changes every boot (`enx` + random MAC) — never pin it. Under
  Windows the same service answers on the BMC's LAN address (10.0.0.200 on the owner's network).
- **Reads need no login over the host interface** (even bogus credentials pass); over the LAN they do. So only a
  session login (POST `SessionService/Sessions`: 201 / 401) proves a password.
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
- `Systems/Self/Bios` exposes ~1,300 AMD CBS/PBS attributes without a login once BIOS "Publish HII Resources" is on;
  ASUS Q-Fan items aren't among them.

### Flash

Fan settings are **persistent**: a flat 70 % on CHA_FAN1 survived `ipmitool mc reset warm` (BMC gone ~30 s) and
`mc reset cold` (~50 s). So every curve or source write is a flash write. No volatile "set duty now" command exists
in ASUS's documentation. AMI BMCs keep settings on a log-structured flash filesystem (wear spread across the
partition), but endurance isn't documented — hence the write discipline below.

## Architecture

```
OpenHW app ──(Unix socket, protocol 14)──► openhw-helper (root, sandboxed)
  Power → Fan Control…                         FanDriver ── AsusBmcFans ── IIpmiTransport ── /dev/ipmi0
  openhw --fan-test                            BmcRedfish ── HTTPS (pinned cert) ── 169.254.0.17
OpenFan (until retired) ─ (to become a client)
```

- **`AsusBmcFans`** (`src/OpenHW.Hardware/Fans/`): identity checks, `Read`, `Write` (+ readback). Ported from OpenFan
  0.4.6's `AsusBmcController` onto OpenHW's own IPMI transport (no `ipmitool`).
- **`FanDriver`**, two kinds of write:
  1. **Takeover** (stage 1; for tests and, later, OpenHW-driven curves): a client token asks for a fixed duty
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
| 14 | `fancurve <zone> <t:d,t:d,t:d,70:d,100:d>` / `fanoriginal <zone>` | configured curve / restore original |
| 14 | `fansources` / `fansource <zone> <mask>` | read / set sources (Redfish) |
| 14 | `bmclogin [b64 user b64 password]` / `bmclogout` | which user is stored / store (after a real login) / forget |

### Sandbox

The helper unit allows network **only** to the BMC's link-local host interface: `RestrictAddressFamilies=AF_UNIX
AF_INET`, `IPAddressDeny=any`, `IPAddressAllow=169.254.0.0/16` (was `PrivateNetwork=true` before 0.24.0).
`DeviceAllow=char-ipmidev rw`; `StateDirectory=openhw` (0700) for the journal and saved originals.

## UI

Power → Fan Control… lists every zone: RPM (BMC sensor), who controls it (BMC curve / a client's fixed duty), which
BMC sensors it follows, the active curve, writes; a BMC-login line; **Edit…** per fan: sensor checkboxes (the BMC's
own list, as in its web console), five points (points 4/5 fixed at 70/100 °C), Apply (writes only what changed),
Restore original. Every dialog opens centred on the primary monitor the first time and then where it was last
moved (owner rule).

## The hybrid plan (owner, 2026-11-16)

1. **Fans whose curve follows a sensor the BMC knows** — CPU Package, T_Sensor, LAN, any DIMM slot, any PCIe slot —
   are **BMC-run**: OpenHW writes the curve and the sources once. Done in 0.24.x.
2. **Fans that follow sensors the BMC can't see** (GPU core temperatures, NVMe, mixes) get **OpenHW-driven duties**
   through takeover, under a write budget (coarse duty steps, wide hysteresis, slow-downs held, a visible daily
   count), with the curve loop in the helper so it runs without the window. To decide first: whether the PCIe slot
   sensors (PCIE01–07) track the GPUs well enough to make this unnecessary. Part 2 (0.25.0), with an import of the
   owner's OpenFan profile (`/home/russellm/Documents/openfan11_14.json`: CPU, GPU Slot 1/5, RTX5060 and Memory
   graph curves, max/min mixes, flat floors, a pump mix) re-pointed to the BMC zones.
3. OpenFan becomes a client of the driver, then is retired.

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
- `tests/OpenHW.App.Tests/FanControlTests.cs`: the window and the Edit… dialog (sends only what changed).
- Real-hardware checks run by the owner: `openhw --fan-test bmc:N <duty> --seconds N`,
  `tools/fan-probe/cpu-fan-test.sh` (the Super I/O test), `tools/bmc-probe/` (`dump.sh`, `match.py`,
  `login-test.sh`).
