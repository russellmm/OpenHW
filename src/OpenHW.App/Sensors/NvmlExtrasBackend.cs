using System.Runtime.InteropServices;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenHW.App.Sensors;

/// <summary>
/// GPU readings HWiNFO shows that openfan-linux's NvmlBackend does not publish: video clock, PCIe link speed /
/// width / throughput, encoder and decoder load, P-state and performance limiters. Rows join each GPU's own
/// group (same backend name and group label), so they appear under the matching "GPU [#n]" heading.
/// </summary>
public sealed class NvmlExtrasBackend : ISensorBackend
{
    // nvmlClocksEventReasons bits (nvml.h): which limiter is holding clocks below the requested level.
    private static readonly (string Key, string Name, ulong Mask)[] Limiters =
    [
        ("limit:power", "GPU Performance Limit - Power", 0x04 | 0x80),   // SW power cap | HW power brake
        ("limit:thermal", "GPU Performance Limit - Thermal", 0x20 | 0x40), // SW thermal | HW thermal slowdown
        ("limit:reliability", "GPU Performance Limit - Reliability", 0x08), // HW slowdown
        ("limit:idle", "GPU Performance Limit - Utilization", 0x01),      // idle
    ];

    private static readonly double[] GtPerSecond = [0, 2.5, 5, 8, 16, 32, 64]; // PCIe generation → GT/s per lane

    private readonly bool _initialized;
    private readonly Dictionary<string, nint> _handles = [];
    private IReadOnlyList<(string Uuid, string Group)> _gpus = [];

    public NvmlExtrasBackend()
    {
        try { _initialized = Native.nvmlInit_v2() == Native.Success; } // reference-counted alongside openfan's init
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public string Name => "nvml";
    public bool Available => _initialized;

    /// <summary>GPUs as openfan named them (uuid → group label), taken from its inventory.</summary>
    public void UseGpus(IEnumerable<HardwareItem> nvmlItems) =>
        _gpus = nvmlItems.Select(i => (Uuid: i.Id.Split(':')[1], i.Group)).Distinct().ToList();

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (!_initialized) return [];
        var items = new List<HardwareItem>();
        foreach (var (uuid, group) in _gpus)
        {
            if (!_handles.ContainsKey(uuid) && Native.nvmlDeviceGetHandleByUUID(uuid, out var handle) == Native.Success)
                _handles[uuid] = handle;
            HardwareItem Item(string key, HardwareKind kind, string unit) => new()
            {
                Id = $"nvml:{uuid}:extra:{key}", Name = key, Kind = kind, Unit = unit, Group = group,
                Backend = Name, CanSet = false, Source = "NVML",
            };
            items.Add(Item("video-clock", HardwareKind.Frequency, "MHz"));
            items.Add(Item("encoder-load", HardwareKind.Load, "%"));
            items.Add(Item("decoder-load", HardwareKind.Load, "%"));
            items.Add(Item("pstate", HardwareKind.Data, "pstate"));
            items.AddRange(Limiters.Select(l => Item(l.Key, HardwareKind.Status, "flag")));
            items.Add(Item("pcie-speed", HardwareKind.Data, "GT/s"));
            items.Add(Item("pcie-width", HardwareKind.Data, "lanes"));
            items.Add(Item("pcie-rx", HardwareKind.Data, "MB/s"));
            items.Add(Item("pcie-tx", HardwareKind.Data, "MB/s"));
        }
        return items;
    }

    /// <summary>Display name for an extras id suffix ("video-clock" → "GPU Video Clock").</summary>
    public static string? DisplayName(string key) => key switch
    {
        "video-clock" => "GPU Video Clock",
        "encoder-load" => "GPU Video Encoder Load",
        "decoder-load" => "GPU Video Decoder Load",
        "pstate" => "GPU Performance State",
        "pcie-speed" => "PCIe Link Speed",
        "pcie-width" => "PCIe Link Width",
        "pcie-rx" => "GPU PCIe Rx Rate",
        "pcie-tx" => "GPU PCIe Tx Rate",
        _ => Limiters.FirstOrDefault(l => l.Key == key).Name,
    };

    public void ReadInto(IDictionary<string, double?> readings)
    {
        foreach (var (uuid, handle) in _handles)
        {
            var prefix = $"nvml:{uuid}:extra:";
            readings[prefix + "video-clock"] = Native.nvmlDeviceGetClockInfo(handle, Native.ClockVideo, out var mhz) == Native.Success ? mhz : null;
            readings[prefix + "encoder-load"] = Native.nvmlDeviceGetEncoderUtilization(handle, out var enc, out _) == Native.Success ? enc : null;
            readings[prefix + "decoder-load"] = Native.nvmlDeviceGetDecoderUtilization(handle, out var dec, out _) == Native.Success ? dec : null;
            readings[prefix + "pstate"] = Native.nvmlDeviceGetPerformanceState(handle, out var pstate) == Native.Success && pstate < 32 ? pstate : null;

            var reasons = Native.nvmlDeviceGetCurrentClocksThrottleReasons(handle, out var mask) == Native.Success ? mask : (ulong?)null;
            foreach (var (key, _, bits) in Limiters)
                readings[prefix + key] = reasons is { } r ? ((r & bits) != 0 ? 1 : 0) : null;

            readings[prefix + "pcie-speed"] = Native.nvmlDeviceGetCurrPcieLinkGeneration(handle, out var gen) == Native.Success && gen < GtPerSecond.Length
                ? GtPerSecond[gen] : null;
            readings[prefix + "pcie-width"] = Native.nvmlDeviceGetCurrPcieLinkWidth(handle, out var width) == Native.Success ? width : null;
            // NVML samples throughput over its own 20 ms window and reports KB/s.
            readings[prefix + "pcie-rx"] = Native.nvmlDeviceGetPcieThroughput(handle, 1, out var rx) == Native.Success ? rx / 1000.0 : null;
            readings[prefix + "pcie-tx"] = Native.nvmlDeviceGetPcieThroughput(handle, 0, out var tx) == Native.Success ? tx / 1000.0 : null;
        }
    }

    public void Dispose()
    {
        if (_initialized) Native.nvmlShutdown();
    }

    private static class Native
    {
        private const string Dll = "libnvidia-ml.so.1";
        public const int Success = 0;
        public const int ClockVideo = 3;

        [DllImport(Dll)] public static extern int nvmlInit_v2();
        [DllImport(Dll)] public static extern int nvmlShutdown();
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByUUID([MarshalAs(UnmanagedType.LPStr)] string uuid, out nint device);
        [DllImport(Dll)] public static extern int nvmlDeviceGetClockInfo(nint device, int type, out uint mhz);
        [DllImport(Dll)] public static extern int nvmlDeviceGetEncoderUtilization(nint device, out uint utilization, out uint samplingPeriodUs);
        [DllImport(Dll)] public static extern int nvmlDeviceGetDecoderUtilization(nint device, out uint utilization, out uint samplingPeriodUs);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPerformanceState(nint device, out uint pstate);
        [DllImport(Dll)] public static extern int nvmlDeviceGetCurrentClocksThrottleReasons(nint device, out ulong reasons);
        [DllImport(Dll)] public static extern int nvmlDeviceGetCurrPcieLinkGeneration(nint device, out uint generation);
        [DllImport(Dll)] public static extern int nvmlDeviceGetCurrPcieLinkWidth(nint device, out uint width);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPcieThroughput(nint device, int counter, out uint kiloBytesPerSec);
    }
}
