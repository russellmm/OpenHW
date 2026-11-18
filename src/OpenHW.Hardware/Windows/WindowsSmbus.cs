using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The AMD chipset's SMBus (PIIX4-compatible FCH) through PawnIO's SmbusPIIX4 module, as an <see cref="ISmbus"/> —
/// so the Linux readers (<see cref="Ddr5Reader"/>'s PMIC and thermal-sensor logic, <see cref="BoardVrReader"/>) run
/// unchanged. The adapter number is the FCH port (0 = the main port; 1 has its own base address). Every transaction
/// holds the shared SMBus mutex, selects its port and puts the previous port back, so HWiNFO and others that expect
/// port 0 aren't disturbed. Failed transactions (no device, NACK) throw IOException, as on Linux.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSmbusProvider(PawnIo pawn) : ISmbusProvider, IDisposable
{
    public static WindowsSmbusProvider? Open(out string? reason) =>
        PawnIo.Open("SmbusPIIX4", out reason) is { } pawn ? new WindowsSmbusProvider(pawn) : null;

    public ISmbus Open(int adapter) => new WindowsSmbus(pawn, adapter);

    public void Dispose() => pawn.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsSmbus(PawnIo pawn, int port) : ISmbus
{
    private const int Quick = 0, Byte = 1, ByteData = 2, WordData = 3, BlockData = 5;
    private const ulong Read = 1, Write = 0;

    public byte ReadByte(int address, byte command) => (byte)Transfer(address, Read, command, ByteData, [], 1)[0];

    public ushort ReadWord(int address, byte command) => (ushort)Transfer(address, Read, command, WordData, [], 1)[0];

    /// <summary>SMBus block read: the length byte, then up to 32 data bytes packed eight to a word.</summary>
    public byte[] ReadBlock(int address, byte command)
    {
        var words = Transfer(address, Read, command, BlockData, [], 5);
        var bytes = words.SelectMany(BitConverter.GetBytes).ToArray();
        var length = Math.Min((int)bytes[0], 32);
        return bytes.AsSpan(1, Math.Min(length, bytes.Length - 1)).ToArray();
    }

    public void WriteByte(int address, byte command, byte value) => Transfer(address, Write, command, ByteData, [value], 1); // the module wants an output buffer even for writes

    /// <summary>A send-byte (one byte, no command): how an I2C bus switch (PCA954x) takes its channel mask.</summary>
    public void SendByte(int address, byte value) => Transfer(address, Write, value, Byte, [value], 1); // the value goes as the command; the module still wants a data word

    /// <summary>A receive-byte (no command): what i2cdetect -r uses to find devices without writing anything.</summary>
    public byte ReceiveByte(int address) => (byte)Transfer(address, Read, 0, Byte, [], 1)[0];

    private ulong[] Transfer(int address, ulong direction, byte command, int protocol, ulong[] data, int outputs) =>
        HardwareMutex.Hold(HardwareMutex.Smbus, () =>
        {
            var previous = (long)pawn.Execute("ioctl_piix4_port_sel", [(ulong)port], 1)[0];
            try { return pawn.Execute("ioctl_smbus_xfer", [(ulong)address, direction, command, (ulong)protocol, .. data], outputs); }
            finally { if (previous >= 0 && previous != port) pawn.Execute("ioctl_piix4_port_sel", [(ulong)previous], 1); }
        });

    public void Dispose() { }
}
