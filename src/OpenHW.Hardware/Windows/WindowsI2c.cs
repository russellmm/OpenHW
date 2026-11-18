using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The AMD chipset's DesignWare I2C controllers (ACPI AMDI0010, I2C0–I2C5) through OpenHW's PawnIO module AMDI2C
/// (packaging/windows/pawnio-src), as <see cref="ISmbus"/> — where the WRX90E-SAGE SE's DIMM hubs, thermal sensors and
/// PMICs are, so Linux's <see cref="Ddr5Reader"/> logic can run on Windows. The adapter number is the controller (1 =
/// I2C1 at 0xFEDC3000). Every transfer holds the shared SMBus mutex. Until PawnIO's author signs AMDI2C, it loads only
/// on OpenHW's test build of the driver (<see cref="PawnIo.TestDevice"/>); without it <see cref="Open(out string?)"/>
/// returns null and nothing is read.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsI2cProvider(PawnIo pawn) : ISmbusProvider, IDisposable
{
    public static WindowsI2cProvider? Open(out string? reason) =>
        PawnIo.Open("AMDI2C", out reason) is { } pawn ? new WindowsI2cProvider(pawn) : null;

    /// <summary>The controllers the module found (bit n = I2Cn).</summary>
    public int Controllers => (int)pawn.Execute("ioctl_identity", [], 2)[0];

    public bool OnTestDriver => pawn.OnTestDriver;

    public ISmbus Open(int adapter) => new WindowsI2c(pawn, adapter);

    public void Dispose() => pawn.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsI2c(PawnIo pawn, int controller) : ISmbus
{
    private const int Byte = 1, ByteData = 2, WordData = 3;
    private const ulong Read = 1, Write = 0;

    public byte ReceiveByte(int address) => (byte)Transfer(address, Read, 0, Byte, 0);

    public byte ReadByte(int address, byte command) => (byte)Transfer(address, Read, command, ByteData, 0);

    public ushort ReadWord(int address, byte command) => (ushort)Transfer(address, Read, command, WordData, 0);

    public byte[] ReadBlock(int address, byte command) => throw new IOException("SMBus block reads aren't supported on the FCH I2C controllers");

    public void WriteByte(int address, byte command, byte value) => Transfer(address, Write, command, ByteData, value);

    private ulong Transfer(int address, ulong direction, byte command, int protocol, byte data) =>
        HardwareMutex.Hold(HardwareMutex.Smbus, () =>
            pawn.Execute("ioctl_i2c_xfer", [(ulong)controller, (ulong)address, direction, command, (ulong)protocol, data], 1)[0]);

    public void Dispose() { }
}
