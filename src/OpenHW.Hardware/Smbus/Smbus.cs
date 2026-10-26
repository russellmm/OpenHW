using System.Runtime.InteropServices;

namespace OpenHW.Hardware;

/// <summary>
/// SMBus transactions on one adapter, as root (openhw-helper). Reads only, plus <see cref="WriteByte"/>, which the
/// callers restrict to the one register they're allowed to set. Each call addresses its device itself; a failed
/// transaction (no device, NACK) throws <see cref="IOException"/>.
/// </summary>
public interface ISmbus : IDisposable
{
    byte ReadByte(int address, byte command);
    ushort ReadWord(int address, byte command);
    byte[] ReadBlock(int address, byte command);
    void WriteByte(int address, byte command, byte value);
}

/// <summary>Opens SMBus adapters by number; tests substitute a fake bus.</summary>
public interface ISmbusProvider
{
    ISmbus Open(int adapter);
}

/// <summary>/dev/i2c-N through the kernel's I2C_SMBUS ioctl (what i2c-tools uses), one transaction per call.</summary>
public sealed class LinuxSmbus : ISmbus
{
    private const uint I2C_SLAVE = 0x0703, I2C_SMBUS = 0x0720;
    private const byte Read = 1, Write = 0;
    private const int ByteData = 2, WordData = 3, BlockData = 5;

    private readonly int _fd;
    private int _address = -1;

    public LinuxSmbus(int adapter)
    {
        _fd = Native.open($"/dev/i2c-{adapter}", 2 /* O_RDWR */ | 0x80000 /* O_CLOEXEC */);
        if (_fd < 0) throw new IOException($"cannot open /dev/i2c-{adapter} (errno {Marshal.GetLastPInvokeError()})");
    }

    public byte ReadByte(int address, byte command) => Transfer(address, Read, command, ByteData)[0];

    public ushort ReadWord(int address, byte command)
    {
        var data = Transfer(address, Read, command, WordData);
        return (ushort)(data[0] | data[1] << 8);
    }

    public byte[] ReadBlock(int address, byte command)
    {
        var data = Transfer(address, Read, command, BlockData);
        var length = Math.Min(data[0], (byte)32);
        return data.AsSpan(1, length).ToArray();
    }

    public void WriteByte(int address, byte command, byte value) => Transfer(address, Write, command, ByteData, value);

    private unsafe byte[] Transfer(int address, byte readWrite, byte command, int size, byte value = 0)
    {
        if (address != _address)
        {
            // I2C_SLAVE (not _FORCE): refused with EBUSY if a kernel driver owns the address, so nothing bound is touched.
            if (Native.ioctl(_fd, I2C_SLAVE, (nint)address) < 0)
                throw new IOException($"I2C address 0x{address:x2} unavailable (errno {Marshal.GetLastPInvokeError()})");
            _address = address;
        }
        var data = new byte[34]; // union i2c_smbus_data: byte, word or block[I2C_SMBUS_BLOCK_MAX + 2]
        data[0] = value;
        fixed (byte* buffer = data)
        {
            var args = new SmbusIoctlData { ReadWrite = readWrite, Command = command, Size = (uint)size, Data = (nint)buffer };
            if (Native.ioctl(_fd, I2C_SMBUS, (nint)(&args)) < 0)
                throw new IOException($"SMBus transfer to 0x{address:x2} cmd 0x{command:x2} failed (errno {Marshal.GetLastPInvokeError()})");
        }
        return data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmbusIoctlData
    {
        public byte ReadWrite;
        public byte Command;
        public uint Size;
        public nint Data;
    }

    public void Dispose() => Native.close(_fd);

    private static class Native
    {
        [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int ioctl(int fd, uint request, nint arg);
        [DllImport("libc")] public static extern int close(int fd);
    }
}

public sealed class LinuxSmbusProvider : ISmbusProvider
{
    public ISmbus Open(int adapter) => new LinuxSmbus(adapter);
}

/// <summary>SMBus adapters as sysfs lists them: number, name and the controller device they hang off.</summary>
public sealed record SmbusAdapter(int Number, string Name, string Controller);

public static class SmbusAdapters
{
    public static IReadOnlyList<SmbusAdapter> List(string sysRoot = "/sys")
    {
        var root = Path.Combine(sysRoot, "bus", "i2c", "devices");
        if (!Directory.Exists(root)) return [];
        var adapters = new List<SmbusAdapter>();
        foreach (var dir in Directory.EnumerateDirectories(root, "i2c-*"))
        {
            if (!int.TryParse(Path.GetFileName(dir)[4..], out var number)) continue;
            var physical = new DirectoryInfo(dir).ResolveLinkTarget(true) as DirectoryInfo ?? new DirectoryInfo(dir);
            adapters.Add(new SmbusAdapter(number, SysFs.ReadText(Path.Combine(dir, "name")), physical.Parent?.FullName ?? ""));
        }
        return adapters.OrderBy(a => a.Number).ToList();
    }
}
