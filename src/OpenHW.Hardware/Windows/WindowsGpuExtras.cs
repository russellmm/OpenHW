using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// Windows only (HWiNFO parity, 2026-11-18): the NVIDIA engine plus the GPU rows HWiNFO shows that NVML or Windows
/// can give without NVIDIA's private APIs — the rated and maximum power limits, power as % of TDP, memory usage %,
/// the PCIe replay count (NVML), and Windows' own view of each card: per-engine usage (D3D, video decode/encode,
/// compute) and D3D dedicated/shared memory (the "GPU Engine" and "GPU Adapter Memory" performance counters, matched
/// to the card through its adapter LUID's PCI bus). Rows use "nvml:&lt;uuid&gt;:win:…" ids; the NVML engine itself, shared
/// with Linux, is unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsGpuExtras(ISensorEngine nvidia) : ISensorEngine
{
    private const string Engines = @"\GPU Engine(*)\Utilization Percentage";
    private const string Dedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string Shared = @"\GPU Adapter Memory(*)\Shared Usage";

    private Pdh? _pdh;
    private readonly Dictionary<string, (nint Handle, uint Bus)> _cards = [];
    private readonly Dictionary<string, string> _luidToUuid = [];
    // Engines seen per card ("VideoDecode:2" …), kept so a row doesn't vanish while no process uses that engine.
    private readonly Dictionary<string, SortedSet<string>> _engines = [];

    public string Name => nvidia.Name;
    public bool Available => nvidia.Available;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Children => nvidia.Children;

    [GeneratedRegex(@"luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_(\d+)(?:_eng_(\d+)_engtype_(.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Instance();

    public IReadOnlyList<HardwareItem> Discover()
    {
        var items = nvidia.Discover().ToList();
        var gpus = items.Where(i => i.Backend == "nvml" && i.Id.StartsWith("nvml:", StringComparison.Ordinal))
            .Select(i => (Uuid: i.Id.Split(':')[1], i.Group)).Distinct().ToList();
        if (gpus.Count == 0) return items;
        _pdh ??= new Pdh([Engines, Dedicated, Shared]);
        _pdh.Collect();
        foreach (var (uuid, _) in gpus)
            if (!_cards.ContainsKey(uuid) && Nvml.nvmlDeviceGetHandleByUUID(uuid, out var handle) == 0)
            {
                var pci = new NvmlNative.PciInfo();
                _cards[uuid] = (handle, NvmlNative.nvmlDeviceGetPciInfo(handle, ref pci) == 0 ? pci.bus : uint.MaxValue);
            }
        MapAdapters();
        ScanEngines();

        foreach (var (uuid, group) in gpus)
        {
            if (!_cards.TryGetValue(uuid, out var card)) continue;
            HardwareItem Row(string key, string name, HardwareKind kind, string unit, string source) => new()
            {
                Id = $"nvml:{uuid}:win:{key}", Name = name, Kind = kind, Unit = unit, Group = group, Backend = "nvml",
                CanSet = false, Source = source,
            };
            if (NvmlNative.nvmlDeviceGetPowerManagementDefaultLimit(card.Handle, out _) == 0)
            {
                items.Add(Row("limit-rated", "GPU Power Limit (rated)", HardwareKind.Power, "W", "NVML default power limit"));
                items.Add(Row("power-tdp", "Total GPU Power [% of TDP]", HardwareKind.Load, "%", "NVML power ÷ default limit"));
            }
            if (NvmlNative.nvmlDeviceGetPowerManagementLimitConstraints(card.Handle, out _, out _) == 0)
                items.Add(Row("limit-max", "GPU Power Limit (max)", HardwareKind.Power, "W", "NVML power limit constraints"));
            items.Add(Row("memory-usage", "GPU Memory Usage", HardwareKind.Load, "%", "NVML memory used ÷ total"));
            if (Nvml.nvmlDeviceGetPcieReplayCounter(card.Handle, out _) == 0)
                items.Add(Row("pcie-replay", "GPU PCIe Replay Count", HardwareKind.Counter, "", "NVML PCIe replay counter"));
            if (!_luidToUuid.ContainsValue(uuid)) continue;
            foreach (var (engine, name) in EngineNames(_engines.GetValueOrDefault(uuid) ?? []))
                items.Add(Row("eng:" + engine, name, HardwareKind.Load, "%", "Windows GPU Engine counters"));
            items.Add(Row("d3d-dedicated", "GPU D3D Memory Dedicated", HardwareKind.Data, "MB", "Windows GPU Adapter Memory counters"));
            items.Add(Row("d3d-shared", "GPU D3D Memory Dynamic", HardwareKind.Data, "MB", "Windows GPU Adapter Memory counters (shared)"));
        }
        return items;
    }

    /// <summary>
    /// A card's engines ("VideoDecode:3", Windows' type and engine index) with HWiNFO's names: video engines numbered
    /// 0, 1, 2… within their type, as HWiNFO counts them (Windows' own indices run on across types: decode 2–5, encode 9–12).
    /// </summary>
    public static IEnumerable<(string Engine, string Name)> EngineNames(IEnumerable<string> engines) =>
        engines.GroupBy(e => e[..e.LastIndexOf(':')])
            .SelectMany(type => type.OrderBy(e => int.Parse(e[(e.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture))
                .Select((engine, ordinal) => (engine, EngineName(type.Key, ordinal))))
            .Where(e => e.Item2 is not null).Select(e => (e.engine, e.Item2!));

    /// <summary>HWiNFO's names for Windows' GPU engine types; null = not shown.</summary>
    public static string? EngineName(string type, int index)
    {
        return type switch
        {
            "3D" => "GPU D3D Usage",
            "VideoDecode" => $"GPU Video Decode {index} Usage",
            "VideoEncode" => $"GPU Video Encode {index} Usage",
            "Cuda" => "GPU Computing (Cuda) Usage",
            "VR" => "GPU VR Usage",
            "Graphics_1" => "GPU Graphics Usage",
            _ when type.StartsWith("Compute_", StringComparison.Ordinal) => $"GPU Computing ({type}) Usage",
            _ => null, // Copy, Security, OFA…: not in HWiNFO's list
        };
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        nvidia.ReadInto(readings, requested);
        if (_cards.Count == 0) return;
        var collected = _pdh?.Collect() == true;
        foreach (var (uuid, card) in _cards)
        {
            var prefix = $"nvml:{uuid}:win:";
            if (requested is not null && !requested.Any(id => id.StartsWith(prefix, StringComparison.Ordinal))) continue;
            double? rated = NvmlNative.nvmlDeviceGetPowerManagementDefaultLimit(card.Handle, out var def) == 0 ? def / 1000.0 : null;
            readings[prefix + "limit-rated"] = rated;
            readings[prefix + "limit-max"] = NvmlNative.nvmlDeviceGetPowerManagementLimitConstraints(card.Handle, out _, out var max) == 0 ? max / 1000.0 : null;
            readings[prefix + "power-tdp"] = rated is > 0 && NvmlNative.nvmlDeviceGetPowerUsage(card.Handle, out var mw) == 0 ? mw / 1000.0 / rated * 100 : null;
            var memory = new NvmlNative.MemoryUsage();
            readings[prefix + "memory-usage"] = NvmlNative.nvmlDeviceGetMemoryInfo(card.Handle, ref memory) == 0 && memory.Total > 0
                ? 100.0 * memory.Used / memory.Total : null;
            readings[prefix + "pcie-replay"] = Nvml.nvmlDeviceGetPcieReplayCounter(card.Handle, out var replays) == 0 ? replays : null;
        }
        if (!collected || _pdh is null) return;

        // Engine use: the sum over processes of each (card, engine), capped at 100 %.
        var use = new Dictionary<(string Uuid, string Engine), double>();
        foreach (var (name, value) in _pdh.Values(Engines))
            if (Instance().Match(name) is { Success: true } m && m.Groups[5].Success && _luidToUuid.TryGetValue(Luid(m), out var uuid))
            {
                var key = (uuid, $"{m.Groups[5].Value}:{m.Groups[4].Value}");
                use[key] = use.GetValueOrDefault(key) + value;
            }
        foreach (var (uuid, engines) in _engines)
            foreach (var engine in engines)
                readings[$"nvml:{uuid}:win:eng:{engine}"] = Math.Min(100, use.GetValueOrDefault((uuid, engine)));
        void Memory(string path, string key)
        {
            foreach (var (name, bytes) in _pdh.Values(path))
                if (Instance().Match(name) is { Success: true } m && _luidToUuid.TryGetValue(Luid(m), out var uuid))
                    readings[$"nvml:{uuid}:win:{key}"] = bytes / (1024 * 1024);
        }
        Memory(Dedicated, "d3d-dedicated");
        Memory(Shared, "d3d-shared");
    }

    private static string Luid(Match m) => (m.Groups[1].Value + m.Groups[2].Value).ToLowerInvariant();

    /// <summary>Each adapter LUID in the counters → its PCI bus (D3DKMT) → the NVML card on that bus.</summary>
    private void MapAdapters()
    {
        if (_pdh is null) return;
        foreach (var name in _pdh.Values(Dedicated).Keys)
        {
            if (Instance().Match(name) is not { Success: true } m) continue;
            var luid = Luid(m);
            if (_luidToUuid.ContainsKey(luid)) continue;
            var high = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var low = uint.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (Bus(low, high) is not { } bus) continue;
            var card = _cards.FirstOrDefault(c => c.Value.Bus == bus);
            if (card.Key is not null) _luidToUuid[luid] = card.Key;
        }
    }

    private void ScanEngines()
    {
        if (_pdh is null) return;
        foreach (var name in _pdh.Values(Engines).Keys)
            if (Instance().Match(name) is { Success: true } m && m.Groups[5].Success && _luidToUuid.TryGetValue(Luid(m), out var uuid))
            {
                var engine = $"{m.Groups[5].Value}:{m.Groups[4].Value}";
                if (EngineName(m.Groups[5].Value, 0) is null) continue;
                (_engines.TryGetValue(uuid, out var set) ? set : _engines[uuid] = new SortedSet<string>(StringComparer.Ordinal)).Add(engine);
            }
    }

    // ── D3DKMT: an adapter LUID to its PCI address ────────────────────────────────────────────────────────────────

    private static uint? Bus(uint low, int high)
    {
        var open = new OpenAdapterFromLuid { LowPart = low, HighPart = high };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return null;
        try
        {
            var address = new AdapterAddress();
            var size = (uint)Marshal.SizeOf<AdapterAddress>();
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var query = new QueryAdapterInfo { Adapter = open.Adapter, Type = 6 /* KMTQAITYPE_ADAPTERADDRESS */, Data = buffer, Size = size };
                if (D3DKMTQueryAdapterInfo(ref query) != 0) return null;
                address = Marshal.PtrToStructure<AdapterAddress>(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return address.Bus;
        }
        finally
        {
            var close = new CloseAdapter { Adapter = open.Adapter };
            D3DKMTCloseAdapter(ref close);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapterFromLuid { public uint LowPart; public int HighPart; public uint Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint Adapter; public int Type; public nint Data; public uint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct AdapterAddress { public uint Bus, Device, Function; }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Adapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);

    private static class Nvml
    {
        private const string Dll = "libnvidia-ml.so.1"; // mapped to nvml.dll on Windows (WindowsMachine's resolver)
        [DllImport(Dll)] public static extern int nvmlDeviceGetHandleByUUID([MarshalAs(UnmanagedType.LPStr)] string uuid, out nint device);
        [DllImport(Dll)] public static extern int nvmlDeviceGetPcieReplayCounter(nint device, out uint value);
    }

    public void Dispose()
    {
        _pdh?.Dispose();
        nvidia.Dispose();
    }
}
