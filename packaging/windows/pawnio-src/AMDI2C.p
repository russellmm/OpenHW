//  PawnIO Modules - Modules for various hardware to be used with PawnIO.
//  Copyright (C) 2026  OpenHW contributors
//
//  This library is free software; you can redistribute it and/or
//  modify it under the terms of the GNU Lesser General Public
//  License as published by the Free Software Foundation; either
//  version 2.1 of the License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
//
//  SPDX-License-Identifier: LGPL-2.1-or-later

#include <pawnio.inc>

// PawnIO AMD FCH I2C (Synopsys DesignWare APB I2C) driver.
//
// AMD's chipsets carry DesignWare I2C controllers at fixed MMIO addresses (ACPI AMDI0010). On boards such as
// the ASUS Pro WS WRX90E-SAGE SE the DDR5 DIMMs' SPD hubs, thermal sensors and PMICs are on these controllers,
// not on the PIIX4 SMBus. Polled master transfers in SMBus style (the same call shape as SmbusPIIX4's
// ioctl_smbus_xfer), modelled on Linux's drivers/i2c/busses/i2c-designware-master.c.
//
// Windows' own AMD I2C driver (amdi2c) usually owns these controllers. Each transfer therefore:
//  - refuses with STATUS_DEVICE_BUSY when the controller is mid-transfer,
//  - masks the controller's interrupts so the owning driver never sees ours, and polls the raw status instead,
//  - restores IC_CON, IC_TAR, IC_INTR_MASK and IC_ENABLE to exactly what they were.
// Bus timing (SCL counts, speed) is left as the firmware/driver configured it.

#define PCI_VENDOR_ID_AMD           0x1022

#define I2C_SMBUS_READ              1
#define I2C_SMBUS_WRITE             0
#define I2C_SMBUS_BYTE              1
#define I2C_SMBUS_BYTE_DATA         2
#define I2C_SMBUS_WORD_DATA         3
#define I2C_SMBUS_ADDR_MAX          0x7F

// DesignWare register offsets
#define DW_IC_CON                   0x00
#define DW_IC_TAR                   0x04
#define DW_IC_DATA_CMD              0x10
#define DW_IC_INTR_MASK             0x30
#define DW_IC_RAW_INTR_STAT         0x34
#define DW_IC_CLR_INTR              0x40
#define DW_IC_CLR_TX_ABRT           0x54
#define DW_IC_CLR_STOP_DET          0x60
#define DW_IC_ENABLE                0x6C
#define DW_IC_STATUS                0x70
#define DW_IC_RXFLR                 0x78
#define DW_IC_TX_ABRT_SOURCE        0x80
#define DW_IC_ENABLE_STATUS         0x9C
#define DW_IC_COMP_TYPE             0xFC
#define DW_IC_MAP_SIZE              0x100

#define DW_IC_COMP_TYPE_VALUE       0x44570140

#define DW_IC_CON_MASTER            0x01
#define DW_IC_CON_10BITADDR_MASTER  0x10
#define DW_IC_CON_RESTART_EN        0x20
#define DW_IC_CON_SLAVE_DISABLE     0x40

#define DW_IC_INTR_TX_ABRT          0x040
#define DW_IC_INTR_STOP_DET         0x200

#define DW_IC_STATUS_ACTIVITY       0x01
#define DW_IC_STATUS_MST_ACTIVITY   0x20

#define DW_IC_CMD_READ              0x100
#define DW_IC_CMD_STOP              0x200

// Polling: 20 us steps; a byte at 100 kHz takes ~90 us, a three-byte transfer well under 1 ms.
#define POLL_US                     20
#define ENABLE_TIMEOUT_POLLS        250     // 5 ms for the controller to (de)activate
#define XFER_TIMEOUT_POLLS          1000    // 20 ms for one transfer

// The AMD FCH I2C controllers (I2C0-I2C5); only these addresses are ever mapped.
new const fch_i2c_bases[] = [0xFEDC2000, 0xFEDC3000, 0xFEDC4000, 0xFEDC5000, 0xFEDC6000, 0xFEDCB000];
#define FCH_I2C_COUNT 6

new bus_va[FCH_I2C_COUNT];

NTSTATUS:rd(bus, offset, &value) {
    return virtual_read_dword(VA:(bus_va[bus] + offset), value);
}

NTSTATUS:wr(bus, offset, value) {
    return virtual_write_dword(VA:(bus_va[bus] + offset), value);
}

/// Sets IC_ENABLE and waits for IC_ENABLE_STATUS to follow.
NTSTATUS:dw_set_enable(bus, enable) {
    new NTSTATUS:status = wr(bus, DW_IC_ENABLE, enable ? 1 : 0);
    if (!NT_SUCCESS(status))
        return status;
    for (new i = 0; i < ENABLE_TIMEOUT_POLLS; i++) {
        new en_status;
        status = rd(bus, DW_IC_ENABLE_STATUS, en_status);
        if (!NT_SUCCESS(status))
            return status;
        if ((en_status & 1) == (enable ? 1 : 0))
            return STATUS_SUCCESS;
        microsleep(POLL_US);
    }
    return STATUS_IO_TIMEOUT;
}

/// One polled transfer: queue the commands, wait for STOP (or an abort), read what came back.
/// cmds[] holds IC_DATA_CMD words; reads come back in rx[] (at most 2).
NTSTATUS:dw_transfer(bus, address, const cmds[], ncmds, rx[2], &nrx) {
    new NTSTATUS:status;
    nrx = 0;

    new st;
    status = rd(bus, DW_IC_STATUS, st);
    if (!NT_SUCCESS(status))
        return status;
    if (st & DW_IC_STATUS_MST_ACTIVITY)
        return STATUS_DEVICE_BUSY; // the owning driver is mid-transfer

    // Save what the owning driver set.
    new old_con, old_tar, old_mask, old_enable;
    rd(bus, DW_IC_CON, old_con);
    rd(bus, DW_IC_TAR, old_tar);
    rd(bus, DW_IC_INTR_MASK, old_mask);
    rd(bus, DW_IC_ENABLE, old_enable);

    status = dw_set_enable(bus, false);
    if (!NT_SUCCESS(status))
        goto restore;

    wr(bus, DW_IC_INTR_MASK, 0);
    wr(bus, DW_IC_TAR, address & I2C_SMBUS_ADDR_MAX);
    wr(bus, DW_IC_CON, (old_con | DW_IC_CON_MASTER | DW_IC_CON_SLAVE_DISABLE | DW_IC_CON_RESTART_EN) & ~DW_IC_CON_10BITADDR_MASTER);
    new dummy;
    rd(bus, DW_IC_CLR_INTR, dummy);

    status = dw_set_enable(bus, true);
    if (!NT_SUCCESS(status))
        goto restore;

    for (new i = 0; i < ncmds; i++)
        wr(bus, DW_IC_DATA_CMD, cmds[i]);

    status = STATUS_IO_TIMEOUT;
    for (new i = 0; i < XFER_TIMEOUT_POLLS; i++) {
        new raw;
        rd(bus, DW_IC_RAW_INTR_STAT, raw);
        if (raw & DW_IC_INTR_TX_ABRT) {
            new source;
            rd(bus, DW_IC_TX_ABRT_SOURCE, source);
            rd(bus, DW_IC_CLR_TX_ABRT, dummy);
            status = STATUS_NO_SUCH_DEVICE; // address or data NACK
            break;
        }
        if (raw & DW_IC_INTR_STOP_DET) {
            status = STATUS_SUCCESS;
            break;
        }
        microsleep(POLL_US);
    }

    if (NT_SUCCESS(status)) {
        new level;
        rd(bus, DW_IC_RXFLR, level);
        while (level > 0 && nrx < 2) {
            new data;
            rd(bus, DW_IC_DATA_CMD, data);
            rx[nrx++] = data & 0xFF;
            rd(bus, DW_IC_RXFLR, level);
        }
    }
    rd(bus, DW_IC_CLR_STOP_DET, dummy);
    rd(bus, DW_IC_CLR_INTR, dummy);

restore:
    // Put everything back as the owning driver left it.
    dw_set_enable(bus, false);
    wr(bus, DW_IC_CON, old_con);
    wr(bus, DW_IC_TAR, old_tar);
    wr(bus, DW_IC_INTR_MASK, old_mask);
    if (old_enable & 1)
        dw_set_enable(bus, true);
    return status;
}

/// Identify the controllers.
///
/// @param in Unused
/// @param in_size Unused
/// @param out [0] = Bit mask of usable controllers (bit n = I2Cn), [1] = 6 (controller count)
/// @param out_size Must be 2
/// @return An NTSTATUS
DEFINE_IOCTL_SIZED(ioctl_identity, 0, 2) {
    new mask = 0;
    for (new i = 0; i < FCH_I2C_COUNT; i++)
        if (bus_va[i] != 0)
            mask |= 1 << i;
    out[0] = mask;
    out[1] = FCH_I2C_COUNT;
    return STATUS_SUCCESS;
}

/// SMBus-style transfer on one FCH I2C controller.
///
/// @param in [0] = Controller (0-5 = I2C0-I2C5), [1] = Address, [2] = Read(1)/Write(0), [3] = Command,
///           [4] = Protocol (1 byte, 2 byte data, 3 word data), [5] = Data (writes)
/// @param in_size Must be 6
/// @param out [0] = Data read (byte, or word low byte first)
/// @param out_size Must be 1
/// @return An NTSTATUS
/// @warning You should acquire the "\BaseNamedObjects\Access_SMBUS.HTP.Method" mutant before calling this
DEFINE_IOCTL_SIZED(ioctl_i2c_xfer, 6, 1) {
    new bus = in[0];
    new address = in[1];
    new read_write = in[2];
    new command = in[3] & 0xFF;
    new protocol = in[4];
    new data = in[5];

    if (bus < 0 || bus >= FCH_I2C_COUNT || bus_va[bus] == 0)
        return STATUS_INVALID_PARAMETER;
    if (address < 0 || address > I2C_SMBUS_ADDR_MAX)
        return STATUS_INVALID_PARAMETER;
    if (read_write != I2C_SMBUS_READ && read_write != I2C_SMBUS_WRITE)
        return STATUS_INVALID_PARAMETER;

    new cmds[3];
    new ncmds = 0;
    if (protocol == I2C_SMBUS_BYTE && read_write == I2C_SMBUS_READ) {
        cmds[ncmds++] = DW_IC_CMD_READ | DW_IC_CMD_STOP;                // receive byte
    } else if (protocol == I2C_SMBUS_BYTE_DATA && read_write == I2C_SMBUS_READ) {
        cmds[ncmds++] = command;                                        // write the register, restart, read 1
        cmds[ncmds++] = DW_IC_CMD_READ | DW_IC_CMD_STOP;
    } else if (protocol == I2C_SMBUS_WORD_DATA && read_write == I2C_SMBUS_READ) {
        cmds[ncmds++] = command;                                        // write the register, restart, read 2
        cmds[ncmds++] = DW_IC_CMD_READ;
        cmds[ncmds++] = DW_IC_CMD_READ | DW_IC_CMD_STOP;
    } else if (protocol == I2C_SMBUS_BYTE_DATA && read_write == I2C_SMBUS_WRITE) {
        cmds[ncmds++] = command;                                        // write the register, then its value
        cmds[ncmds++] = (data & 0xFF) | DW_IC_CMD_STOP;
    } else {
        return STATUS_NOT_SUPPORTED;
    }

    new rx[2];
    new nrx;
    new NTSTATUS:status = dw_transfer(bus, address, cmds, ncmds, rx, nrx);
    if (!NT_SUCCESS(status))
        return status;

    if (read_write == I2C_SMBUS_READ) {
        new expected = protocol == I2C_SMBUS_WORD_DATA ? 2 : 1;
        if (nrx != expected)
            return STATUS_DEVICE_PROTOCOL_ERROR;
        out[0] = protocol == I2C_SMBUS_WORD_DATA ? (rx[0] | (rx[1] << 8)) : rx[0];
    } else {
        out[0] = 0;
    }
    return STATUS_SUCCESS;
}

NTSTATUS:main() {
    if (get_arch() != ARCH_X64)
        return STATUS_NOT_SUPPORTED;

    // An AMD system: the host bridge at 00:00.0 is AMD's.
    new vid;
    new NTSTATUS:status = pci_config_read_word(0, 0, 0, 0x00, vid);
    if (!NT_SUCCESS(status) || vid != PCI_VENDOR_ID_AMD)
        return STATUS_NOT_SUPPORTED;

    // Map each FCH I2C controller that identifies as DesignWare; leave the others alone.
    new found = 0;
    for (new i = 0; i < FCH_I2C_COUNT; i++) {
        bus_va[i] = 0;
        new VA:va = io_space_map(fch_i2c_bases[i], DW_IC_MAP_SIZE);
        if (va == NULL)
            continue;
        new comp_type;
        if (NT_SUCCESS(virtual_read_dword(VA:(_:va + DW_IC_COMP_TYPE), comp_type)) && comp_type == DW_IC_COMP_TYPE_VALUE) {
            bus_va[i] = _:va;
            found++;
        } else {
            io_space_unmap(va, DW_IC_MAP_SIZE);
        }
    }
    return found > 0 ? STATUS_SUCCESS : STATUS_NOT_SUPPORTED;
}

public NTSTATUS:unload() {
    for (new i = 0; i < FCH_I2C_COUNT; i++)
        if (bus_va[i] != 0) {
            io_space_unmap(VA:bus_va[i], DW_IC_MAP_SIZE);
            bus_va[i] = 0;
        }
    return STATUS_SUCCESS;
}
