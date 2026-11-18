using System.Buffers.Binary;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// AMD's HSMP mailbox on Windows through AMD's own driver ("AMD Host System Management Port", AmdHSMPDriver.sys,
/// WHQL-signed, bound to ACPI\AMDI0097 — the device Linux's amd_hsmp uses; installed with AMD's chipset drivers). It
/// has no published user-mode interface; found on 2026-11-17 by reading the driver: device interface
/// {dc142d67-9b96-4778-a0b3-b6d6eca590be}, one IOCTL 0x2203C8 (buffered) whose input and output are a 48-byte message
/// in Linux's <c>struct hsmp_message</c> layout — msg_id u32 @0, num_args u16 @4, response_sz u16 @6, args u32[8] @8,
/// sock_ind u16 @40 — the reply's args holding the response. The device allows SYSTEM and Administrators only, so it is
/// used by the OpenHW Helper service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsHsmp : IDisposable
{
    public static readonly Guid Interface = new("dc142d67-9b96-4778-a0b3-b6d6eca590be");
    private const uint Ioctl = 0x2203C8;
    private const int MessageSize = 48;
    private const uint GenericReadWrite = 0x80000000 | 0x40000000;

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _device;
    private readonly object _gate = new();

    private WindowsHsmp(Microsoft.Win32.SafeHandles.SafeFileHandle device) => _device = device;

    /// <summary>The driver's device, or null with the reason (driver not installed, HSMP off in the BIOS, not elevated).</summary>
    public static WindowsHsmp? Open(out string? reason)
    {
        var path = Win32.InterfacePaths(Interface).FirstOrDefault();
        if (path is null)
        {
            reason = "AMD's HSMP driver isn't installed or its device isn't present (AMD chipset drivers; HSMP enabled in the BIOS).";
            return null;
        }
        var handle = Win32.CreateFile(path, GenericReadWrite, Win32.FileShareReadWrite, 0, Win32.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            handle.Dispose();
            reason = error == 5 ? "AMD's HSMP device needs Administrator (the OpenHW Helper service)." : $"AMD's HSMP device: error {error}";
            return null;
        }
        reason = null;
        return new WindowsHsmp(handle);
    }

    /// <summary>The 48-byte request: Linux's struct hsmp_message, padded.</summary>
    public static byte[] Request(uint message, IReadOnlyList<uint> args, int responseWords, ushort socket = 0)
    {
        if (args.Count > 8 || responseWords > 8) throw new ArgumentException("HSMP carries at most 8 words");
        var buffer = new byte[MessageSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, message);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)args.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), (ushort)responseWords);
        for (var i = 0; i < args.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8 + 4 * i), args[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(40), socket);
        return buffer;
    }

    /// <summary>Sends one message; returns the response words (args of the reply).</summary>
    public uint[] Send(uint message, IReadOnlyList<uint> args, int responseWords)
    {
        var request = Request(message, args, responseWords);
        var reply = new byte[MessageSize];
        lock (_gate)
        {
            fixed (byte* input = request)
            fixed (byte* output = reply)
                if (!Win32.DeviceIoControl(_device, Ioctl, input, MessageSize, output, MessageSize, out _, 0))
                    throw new IOException($"HSMP message {message}: error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
        return Enumerable.Range(0, responseWords).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8 + 4 * i))).ToArray();
    }

    /// <summary>The raw reply, for the helper's diagnostic.</summary>
    public byte[] SendRaw(byte[] request)
    {
        var reply = new byte[MessageSize];
        lock (_gate)
        {
            fixed (byte* input = request)
            fixed (byte* output = reply)
                if (!Win32.DeviceIoControl(_device, Ioctl, input, MessageSize, output, MessageSize, out _, 0))
                    throw new IOException($"HSMP: error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
        return reply;
    }

    public void Dispose() => _device.Dispose();
}
