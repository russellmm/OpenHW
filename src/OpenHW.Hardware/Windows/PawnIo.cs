using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// One PawnIO module (namazso's signed kernel driver, https://pawnio.eu), loaded by OpenHW itself — the Windows
/// counterpart of the Linux kernel drivers OpenHW reads through hwmon and /dev/i2c. Each instance is one handle with
/// one module; <see cref="Execute"/> calls a function the module exports. The modules are PawnIO.Modules' signed
/// release builds (LGPL-2.1), shipped beside openhw-helper.exe in <c>pawnio\</c>; PawnIO's own installer provides the
/// driver and PawnIOLib.dll. Administrator only (the OpenHW Helper service).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PawnIo : IDisposable
{
    private nint _handle;
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle? _test; // OpenHW's own test build of the driver
    private readonly object _gate = new();

    public string Module { get; }

    /// <summary>True when the module runs on OpenHW's unsigned test build of PawnIO, not the official driver.</summary>
    public bool OnTestDriver => _test is not null;

    private PawnIo(nint handle, string module) => (_handle, Module) = (handle, module);
    private PawnIo(Microsoft.Win32.SafeHandles.SafeFileHandle test, string module) => (_test, Module) = (test, module);

    /// <summary>
    /// OpenHW's test build of PawnIO (PawnIO's source built with PAWNIO_NAME=OpenHWPawnIOTest and
    /// PAWNIO_UNRESTRICTED): its own device beside the official one, loading modules PawnIO's author hasn't signed
    /// yet (AMDI2C). Only present while the owner tests it, on a boot with driver signature enforcement off.
    /// </summary>
    public const string TestDevice = @"\\.\OpenHWPawnIOTest";

    // PawnIO's IOCTLs: CTL_CODE(0xA1B2, function, METHOD_BUFFERED, FILE_ANY_ACCESS).
    private const uint IoctlLoad = (0xA1B2u << 16) | (0x821u << 2), IoctlExecute = (0xA1B2u << 16) | (0x841u << 2);
    private const int FunctionNameLength = 32;

    /// <summary>Where the module files are: <c>pawnio\</c> beside the running program.</summary>
    public static string ModuleDir => Path.Combine(AppContext.BaseDirectory, "pawnio");

    /// <summary>Opens PawnIO and loads <paramref name="module"/> (e.g. "AMDFamily17"); null with the reason if it can't.</summary>
    public static PawnIo? Open(string module, out string? reason)
    {
        var file = Path.Combine(ModuleDir, module + ".bin");
        if (!File.Exists(file)) { reason = $"PawnIO module {module} isn't installed ({file})."; return null; }
        try
        {
            var hr = Native.pawnio_open(out var handle);
            if (hr < 0) { reason = $"PawnIO isn't available (0x{hr:X8}): install PawnIO (https://pawnio.eu)."; return null; }
            var blob = File.ReadAllBytes(file);
            hr = Native.pawnio_load(handle, blob, (nuint)blob.Length);
            if (hr < 0)
            {
                Native.pawnio_close(handle);
                // Not signed by PawnIO's author (0xC0000428 STATUS_INVALID_SIGNATURE, as an HRESULT): OpenHW's test
                // driver, if it's running, may load it.
                if (OpenOnTestDriver(module, blob) is { } test) { reason = null; return test; }
                reason = $"PawnIO refused module {module} (0x{hr:X8}): unsigned, or this CPU or chipset isn't one it supports.";
                return null;
            }
            reason = null;
            return new PawnIo(handle, module);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            reason = "PawnIO isn't installed (PawnIOLib.dll not found): install PawnIO (https://pawnio.eu).";
            return null;
        }
    }

    private static PawnIo? OpenOnTestDriver(string module, byte[] blob)
    {
        var device = Win32Raw.CreateFileW(TestDevice, 0xC0000000, 3, 0, 3, 0, 0);
        if (device.IsInvalid) { device.Dispose(); return null; }
        if (!Win32Raw.DeviceIoControl(device, IoctlLoad, blob, blob.Length, null, 0, out _, 0)) { device.Dispose(); return null; }
        return new PawnIo(device, module);
    }

    /// <summary>Calls the module's <paramref name="function"/>; returns its output words. Throws IOException on failure.</summary>
    public ulong[] Execute(string function, ReadOnlySpan<ulong> input, int outputCount)
    {
        var output = new ulong[outputCount];
        var arguments = input.ToArray();
        if (_test is not null)
            lock (_gate)
            {
                // The driver's request: the function name in 32 bytes, then the input words.
                var request = new byte[FunctionNameLength + 8 * arguments.Length];
                System.Text.Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, FunctionNameLength - 1), request, 0);
                Buffer.BlockCopy(arguments, 0, request, FunctionNameLength, 8 * arguments.Length);
                var reply = new byte[8 * outputCount];
                if (!Win32Raw.DeviceIoControl(_test, IoctlExecute, request, request.Length, reply, reply.Length, out var written, 0))
                    throw new IOException($"PawnIO (test driver) {Module}.{function}: error {Marshal.GetLastPInvokeError()}");
                Buffer.BlockCopy(reply, 0, output, 0, Math.Min(written, reply.Length));
                return written / 8 < outputCount ? output[..(written / 8)] : output;
            }
        lock (_gate)
        {
            if (_handle == 0) throw new ObjectDisposedException(nameof(PawnIo));
            var hr = Native.pawnio_execute(_handle, function, arguments, (nuint)arguments.Length, output, (nuint)output.Length, out var returned);
            if (hr < 0) throw new IOException($"PawnIO {Module}.{function}: 0x{hr:X8}");
            return returned < (nuint)outputCount ? output[..(int)returned] : output;
        }
    }

    public void Dispose()
    {
        _test?.Dispose();
        lock (_gate)
        {
            if (_handle != 0) Native.pawnio_close(_handle);
            _handle = 0;
        }
    }

    private static class Win32Raw
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, nint overlapped);
    }

    // PawnIOLib.dll is found by the assembly's resolver (NativeLibraries in WindowsMachine.cs).
    private static class Native
    {
        [DllImport("PawnIOLib")] public static extern int pawnio_open(out nint handle);
        [DllImport("PawnIOLib")] public static extern int pawnio_load(nint handle, byte[] blob, nuint size);
        [DllImport("PawnIOLib", CharSet = CharSet.Ansi)]
        public static extern int pawnio_execute(nint handle, string name, ulong[] input, nuint inputSize, ulong[] output, nuint outputSize, out nuint returned);
        [DllImport("PawnIOLib")] public static extern int pawnio_close(nint handle);
    }
}

/// <summary>
/// The named mutexes Windows hardware tools share so two of them never drive the same bus at once (HWiNFO, OpenFan,
/// LibreHardwareMonitor and others use the same names): PCI configuration space (SMN), SMBus, the ISA bus (Super I/O)
/// and the embedded controller.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HardwareMutex
{
    public const string Pci = @"Global\Access_PCI", Smbus = @"Global\Access_SMBUS.HTP.Method",
        IsaBus = @"Global\Access_ISABUS.HTP.Method", EmbeddedController = @"Global\Access_EC";

    private static readonly Dictionary<string, Mutex> Mutexes = [];

    /// <summary>Runs <paramref name="action"/> holding <paramref name="name"/>; throws IOException if another tool holds it for over 1 s.</summary>
    public static T Hold<T>(string name, Func<T> action)
    {
        Mutex mutex;
        lock (Mutexes)
            if (!Mutexes.TryGetValue(name, out mutex!)) Mutexes[name] = mutex = new Mutex(false, name);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(1))) throw new IOException($"{name} is busy (another hardware tool)");
        }
        catch (AbandonedMutexException) { } // its owner exited: ours now
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }
}
