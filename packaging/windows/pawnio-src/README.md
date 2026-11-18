# OpenHW's PawnIO module sources

`AMDI2C.p` — AMD FCH I2C (Synopsys DesignWare) controllers, for the DDR5 DIMMs' SPD hubs, thermal sensors and
PMICs on boards where they aren't on the PIIX4 SMBus (ASUS Pro WS WRX90E-SAGE SE: ACPI `AMDI0010`, 0xFEDC2000…).
LGPL-2.1-or-later like PawnIO.Modules, written to be submitted there; until PawnIO's author signs it the
official PawnIO driver refuses to load it (`STATUS_INVALID_SIGNATURE`), and OpenHW simply doesn't read the DIMMs.

## Build

The same compiler and flags as PawnIO.Modules' CI (Pawn 4.1.7152, from that repo's `_pawn/*.x86_64.rpm`):

    pawncc AMDI2C.p -i<PawnIO.Modules>/include -C64 '-;+' '-(+' -p

`build-module.sh <dir> <file.p> <out.amx>` runs it from the unpacked RPM (WSL). Checked 2026-11-18: this
toolchain rebuilds the official `SmbusPIIX4.bin` byte for byte (a release `.bin` is a 4-byte signature length,
the 512-byte signature, then the `.amx`).

## API

- `ioctl_identity` → out[0] = mask of usable controllers (bit n = I2Cn, those whose IC_COMP_TYPE reads
  0x44570140), out[1] = 6.
- `ioctl_i2c_xfer` in = [controller 0–5, address, read 1/write 0, command, protocol, data] → out[0]:
  protocols 1 (receive byte), 2 (byte data, read or write), 3 (word data read). NACK = STATUS_NO_SUCH_DEVICE,
  controller mid-transfer = STATUS_DEVICE_BUSY. Hold `Global\Access_SMBUS.HTP.Method` around each call.

Every transfer masks the controller's interrupts and restores IC_CON, IC_TAR, IC_INTR_MASK and IC_ENABLE, so
Windows' own amdi2c driver, which owns these controllers, isn't disturbed.
