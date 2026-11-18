using System.IO.Ports;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// WireView Pro II on Windows: its USB serial port (STM32 CDC-ACM, VID 0483 / PID 5740) is a COM port any user may
/// open, so the app reads it itself — no service. The same <see cref="WireViewReader"/> (read-only commands only) as
/// the Linux helper, with a COM-port link and finder; <see cref="Request"/> answers exactly like the helper's
/// <c>wireview</c> command, so <see cref="WireViewEngine"/> works unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsWireView
{
    private static readonly Lazy<WireViewReader> Reader = new(() => new WireViewReader(Find, port => new WindowsSerialLink(port)));

    /// <summary>The helper's <c>wireview</c> reply (without "ok "), for <see cref="WireViewEngine"/>'s request.</summary>
    public static string Request(int timeoutMs) => GpuWire.Encode(Reader.Value.Read(TimeSpan.FromMilliseconds(Math.Min(timeoutMs, 5000))));

    /// <summary>WireViews plugged in now: (USB serial number, COM port), from the USB device's registry entry.</summary>
    public static IReadOnlyList<(string Serial, string Port)> Find()
    {
        var found = new List<(string, string)>();
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_0483&PID_5740");
        if (usb is null) return found;
        var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var serial in usb.GetSubKeyNames())
        {
            using var parameters = usb.OpenSubKey(serial + @"\Device Parameters");
            if (parameters?.GetValue("PortName") is string port && present.Contains(port)) found.Add((serial, port));
        }
        return found.OrderBy(f => f.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>A WireView's COM port: raw 115200 8N1, one command byte out, a fixed-size reply back (1 s at most).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSerialLink : IWireViewLink
{
    private readonly SerialPort _port;

    public WindowsSerialLink(string port)
    {
        _port = new SerialPort(port, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 1000, WriteTimeout = 1000, DtrEnable = true };
        try { _port.Open(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _port.Dispose();
            throw new IOException($"{port}: {ex.Message}", ex);
        }
    }

    public byte[] Command(byte command, int replySize)
    {
        _port.DiscardInBuffer();
        _port.Write([command], 0, 1);
        var buffer = new byte[replySize];
        var got = 0;
        var deadline = Environment.TickCount64 + 1000;
        try
        {
            while (got < replySize && Environment.TickCount64 < deadline)
                got += _port.Read(buffer, got, replySize - got);
        }
        catch (TimeoutException) { }
        return got == replySize ? buffer : buffer[..got];
    }

    public void Dispose() => _port.Dispose();
}
