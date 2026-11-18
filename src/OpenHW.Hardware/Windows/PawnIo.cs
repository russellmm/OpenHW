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
    private readonly object _gate = new();

    public string Module { get; }

    private PawnIo(nint handle, string module) => (_handle, Module) = (handle, module);

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
                reason = $"PawnIO refused module {module} (0x{hr:X8}): this CPU or chipset isn't one it supports.";
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

    /// <summary>Calls the module's <paramref name="function"/>; returns its output words. Throws IOException on failure.</summary>
    public ulong[] Execute(string function, ReadOnlySpan<ulong> input, int outputCount)
    {
        var output = new ulong[outputCount];
        var arguments = input.ToArray();
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
        lock (_gate)
        {
            if (_handle != 0) Native.pawnio_close(_handle);
            _handle = 0;
        }
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
