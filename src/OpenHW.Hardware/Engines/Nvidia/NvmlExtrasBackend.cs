using System.Runtime.InteropServices;

namespace OpenHW.Hardware;

/// <summary>
/// GPU readings HWiNFO shows that <see cref="NvmlBackend"/> does not publish: video clock, PCIe link speed /
/// width / throughput, encoder and decoder load, P-state, performance limiters, and NVML field values (1 s average
/// power, PCIe error counters) where the card supports them. Hotspot and memory-junction temperatures are not in
/// NVML's public interface (memory temperature, field 82, is "not supported" on the RTX 5060 Ti and RTX PRO 6000). Rows join each GPU's own
/// group (same backend name and group label), so they appear under the matching "GPU [#n]" heading.
/// </summary>
internal sealed class NvmlExtrasBackend : ISensorEngine
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

    // NVML field values (nvml.h NVML_FI_DEV_*): only those a card answers at discovery become rows.
    private static readonly (string Key, uint Field, string Name, HardwareKind Kind, string Unit, double Scale)[] Fields =
    [
        ("power-average", 185, "GPU Power (1 s Average)", HardwareKind.Power, "W", 0.001),          // mW
        ("pcie-correctable", 173, "GPU PCIe Correctable Errors", HardwareKind.Counter, "errors", 1),
        ("pcie-nak-received", 174, "GPU PCIe NAKs Received", HardwareKind.Counter, "errors", 1),
        ("pcie-receiver", 175, "GPU PCIe Receiver Errors", HardwareKind.Counter, "errors", 1),
        ("pcie-bad-tlp", 176, "GPU PCIe Bad TLPs", HardwareKind.Counter, "errors", 1),
        ("pcie-nak-sent", 177, "GPU PCIe NAKs Sent", HardwareKind.Counter, "errors", 1),
        ("pcie-bad-dllp", 178, "GPU PCIe Bad DLLPs", HardwareKind.Counter, "errors", 1),
        ("pcie-non-fatal", 179, "GPU PCIe Non-Fatal Errors", HardwareKind.Counter, "errors", 1),
        ("pcie-fatal", 180, "GPU PCIe Fatal Errors", HardwareKind.Counter, "errors", 1),
        ("pcie-unsupported", 181, "GPU PCIe Unsupported Requests", HardwareKind.Counter, "errors", 1),
        ("pcie-lcrc", 182, "GPU PCIe LCRC Errors", HardwareKind.Counter, "errors", 1),
    ];
    private readonly Dictionary<string, (string Key, uint Field, string Name, HardwareKind Kind, string Unit, double Scale)[]> _fields = [];

    /// <summary>How often PCIe throughput is actually sampled.</summary>
    public static readonly TimeSpan PcieThroughputPeriod = TimeSpan.FromSeconds(10);

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<string, (double? Rx, double? Tx, TimeSpan At)> _throughput = [];
    private readonly bool _initialized;
    private readonly Dictionary<string, nint> _handles = [];
    private IReadOnlyList<(string Uuid, string Group)> _gpus = [];

    public NvmlExtrasBackend()
    {
        try { _initialized = Native.nvmlInit_v2() == Native.Success; } // reference-counted alongside NvmlBackend's init
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public string Name => "nvml";
    public bool Available => _initialized;

    /// <summary>GPUs as NvmlBackend named them (uuid → group label), taken from its inventory.</summary>
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
            if (_handles.TryGetValue(uuid, out var h))
            {
                if (!_fields.ContainsKey(uuid))
                    _fields[uuid] = Fields.Where((f, i) => ReadFields(h, Fields)[i] is not null).ToArray(); // asked once per card
                items.AddRange(_fields[uuid].Select(f => Item(f.Key, f.Kind, f.Unit)));
            }
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
        _ => Limiters.FirstOrDefault(l => l.Key == key).Name ?? Fields.FirstOrDefault(f => f.Key == key).Name,
    };

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        var now = _clock.Elapsed;
        foreach (var (uuid, handle) in _handles)
        {
            var prefix = $"nvml:{uuid}:extra:";
            if (requested is not null && !requested.Any(id => id.StartsWith(prefix, StringComparison.Ordinal))) continue;

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
            // Throughput is the one slow NVML query (~20 ms per direction, ~42 ms per GPU), so it is refreshed
            // every PcieThroughputPeriod and the last value is repeated in between.
            if (requested is null || requested.Contains(prefix + "pcie-rx") || requested.Contains(prefix + "pcie-tx"))
            {
                if (!_throughput.TryGetValue(uuid, out var last) || now - last.At >= PcieThroughputPeriod)
                {
                    // NVML reports KB/s.
                    double? rx = Native.nvmlDeviceGetPcieThroughput(handle, 1, out var r) == Native.Success ? r / 1000.0 : null;
                    double? tx = Native.nvmlDeviceGetPcieThroughput(handle, 0, out var t) == Native.Success ? t / 1000.0 : null;
                    _throughput[uuid] = last = (rx, tx, now);
                }
                readings[prefix + "pcie-rx"] = last.Rx;
                readings[prefix + "pcie-tx"] = last.Tx;
            }
            if (_fields.TryGetValue(uuid, out var fields) && fields.Length > 0)
            {
                var values = ReadFields(handle, fields); // one NVML call for all of this card's fields
                for (var i = 0; i < fields.Length; i++) readings[prefix + fields[i].Key] = values[i] * fields[i].Scale;
            }
        }
    }

    // nvmlFieldValue_t.valueType → the union member to read (nvml.h nvmlValueType_t).
    private static double?[] ReadFields(nint handle, IReadOnlyList<(string Key, uint Field, string Name, HardwareKind Kind, string Unit, double Scale)> fields)
    {
        var request = fields.Select(f => new Native.FieldValue { FieldId = f.Field }).ToArray();
        var result = new double?[fields.Count];
        if (Native.nvmlDeviceGetFieldValues(handle, request.Length, request) != Native.Success) return result;
        for (var i = 0; i < request.Length; i++)
        {
            var v = request[i];
            if (v.Return != Native.Success) continue;
            result[i] = v.ValueType switch
            {
                0 => BitConverter.Int64BitsToDouble((long)v.Value),
                1 => (uint)v.Value,
                2 or 3 => v.Value,
                4 => (long)v.Value,
                5 => (int)(uint)v.Value,
                _ => null,
            };
        }
        return result;
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
        [DllImport(Dll)] public static extern int nvmlDeviceGetFieldValues(nint device, int count, [In, Out] FieldValue[] values);

        /// <summary>nvmlFieldValue_t (nvml.h): 40 bytes; the value is an 8-byte union read by <see cref="ValueType"/>.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FieldValue
        {
            public uint FieldId;
            public uint ScopeId;
            public long Timestamp;
            public long LatencyUsec;
            public int ValueType;
            public int Return;
            public ulong Value;
        }
    }
}
