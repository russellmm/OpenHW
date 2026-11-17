# Power devices: WireView Pro II and UPS (0.38.0)

Two USB devices on the owner's workstation that measure power, each shown as its own sensor group.

## Thermal Grizzly WireView Pro II

A meter that sits between the PSU's 12V-2x6 cable and a GPU's power connector (on the owner's machine: the RTX PRO
6000 in slot 5, C1:00.0 — but OpenHW doesn't tie it to a GPU: owner, 2026-11-17, "if I swap the WireView over to the
slot 1 GPU it should still show readings"). The group is keyed by the device's **USB serial** (`208E31635445`).

### Device

- USB **0483:5740** "STMicroelectronics STM32 Virtual ComPort", driver `cdc_acm` → `/dev/ttyACM0`
  (`/dev/serial/by-id/usb-STMicroelectronics_STM32_Virtual_ComPort_<serial>-if00`), `root:dialout 0660`. The owner's
  account isn't in `dialout`, so **openhw-helper reads it** (unit: `DeviceAllow=char-ttyACM rw` — a serial port must be
  opened read-write to send even read commands).
- Raw 115200 8N1 (termios via libc: `cfmakeraw`, `cfsetspeed`; no System.IO.Ports dependency).
- Identity: command 0x01 → 3 bytes vendor/product/firmware = **EF 05** (Pro II) or EF 06 (Noctua Edition); 0x0D →
  build info (3 + 32-byte product name + 32-byte build string + 1): "Thermal Grizzly WireView Pro II" /
  "TG-WV-PRO2-FW_20260430_1838" on the owner's unit. The RTS-triggered welcome string returned nothing in the probe;
  identity relies on 0x01/0x0D. Any other STM32 serial device is skipped.

### Protocol (learned for interoperability from the community wireview-linux project)

One command byte out, a fixed-size binary reply back. **OpenHW sends only 0x01, 0x04 and 0x0D.** The device also has
config read/write, calibration, SPI flash, screen, fault clearing (0x0E), reset (0xF0), bootloader (0xF1) and NVM
commands — never sent. wireview-linux's device library is derived from ElmorLabs/Thermal Grizzly code and **isn't
MIT**: OpenHW's code is its own; only the protocol facts (command numbers, frame layout) were taken.

Sensor frame (0x04), **100 bytes**, little-endian, 4-byte packed:

| Offset | Field |
|---|---|
| 0–7 | 4 × int16 temperatures, 0.1 °C: connector in (PSU side), connector out (GPU side), external probe 1, 2 (−100.0 = none) |
| 8 | uint16 device supply, mV |
| 10 | uint8 fan duty, % |
| 11 | padding (0) |
| 12 + 12·i | wire i (0–5): int16 mV, 2 padding, uint32 mA, uint32 mW |
| 84 | uint32 total power, mW |
| 88 | uint32 total current, mA |
| 92 | uint16 average voltage, mV |
| 94 | uint8 PSU capability from the sense pins: 0 = 600 W, 1 = 450, 2 = 300, 3 = 150 |
| 95 | padding (0) |
| 96 / 98 | uint16 fault status / fault log: bit 0 chip over-temp, 1 connector over-temp, 2 over-current, 3 wire over-current, 4 over-power, 5 current imbalance |

**No framing or checksum.** A reply read out of step is recognised by what a real frame always has — byte 10 ≤ 100,
bytes 11 and 95 zero — and dropped; the next command's input flush realigns the stream.

### Code

- `src/OpenHW.Hardware/WireView/WireView.cs`: `WireViewSample.Parse`, `LinuxSerialLink`, `WireViewReader` (sysfs scan
  every 10 s for 0483:5740 ttys, identity once per port, a sample per second while a client asked within a minute).
- Helper **protocol 19**: `wireview` → JSON list of devices (serial, product, firmware, port, sample, problem).
- `src/OpenHW.Hardware/Engines/WireView/WireViewEngine.cs`: group `wireview:<serial>`, 29 rows (connector temps, 6 ×
  V/A/W, totals, **current imbalance** = busiest wire's excess over the average in % — blank below 0.5 A a wire —, PSU
  limit, fault counts now/logged, supply, fan). Title "Thermal Grizzly WireView Pro II (12V-2x6)".
- `tools/wireview-probe/probe.py`: standalone read-only probe (sudo), the first contact with the device.

### Live (2026-11-17, idle)

6 wires at 12.02–12.03 V, 0.18–0.40 A each, 18–23 W total; connector 27.1 / 27.8 °C; PSU 600 W; faults 0/0. The card
also draws from the PCIe slot (up to ~66 W), which the WireView doesn't see — its total is below nvidia-smi's board
power only by that.

## UPS via Network UPS Tools

The CyberPower **CP2000PFCRM2U** (USB 0764:0601) is driven by NUT (`usbhid-ups`, `upsd`, `upsmon`; the owner's
`ups-notify.sh`). OpenHW reads **upsd on 127.0.0.1:3493** with `LIST UPS` / `LIST VAR <ups>` only (`NutEngine.Query`
refuses anything else — no `INSTCMD`, `SET`, logins) — no root, no helper, works on any NUT setup.

Rows (shown when the UPS reports the variable): Input/Output Voltage, Input/Output Frequency, UPS Load, UPS Output Power
(`ups.realpower`), UPS Apparent Power (`ups.power`, VA), UPS Power Rating (`ups.realpower.nominal`), Battery Charge,
Battery Voltage, Battery Runtime (min), UPS Temperature, and flags from `ups.status`: On Battery (OB), Low Battery (LB),
Replace Battery (RB), UPS Overloaded (OVER). Details carry the driver, firmware and last self-test result. Group "UPS:
CyberPower CP2000PFCRM2U (NUT)"; whole numbers (`Decimals = 0`, owner's choice, 0.38.2). Live: 120 V in/out, ~20 % load, ~240 W of 1,200 W, battery 100 % / 26.3 V / ~32 min.

## Tests

`tests/OpenHW.Tests/WireViewTests.cs` (frame decode from the owner's readings, out-of-step frames, identity and
"only read commands" on fake links, engine rows, helper command); `tests/OpenHW.App.Tests/PowerDeviceTests.cs` (UPS
parsing and flags from fake upsd lines, group titles, a specific tooltip for every WireView and UPS row).
