namespace OpenHW.Hardware;

/// <summary>
/// NVIDIA GPUs through NVML: temperatures, fans, power, clocks, load and memory (<see cref="NvmlBackend"/>) plus
/// the video clock, PCIe link, encoder/decoder load, P-state and limiters (<see cref="NvmlExtrasBackend"/>).
/// </summary>
public sealed class NvidiaEngine : ISensorEngine
{
    private readonly NvmlBackend _nvml = new();
    private readonly NvmlExtrasBackend _extras = new();

    public string Name => "nvidia";
    public bool Available => _nvml.Available;

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (!_nvml.Available) return [];
        var gpus = _nvml.Discover();
        _extras.UseGpus(gpus); // extras attach to the GPUs (and group labels) the main reader found
        return gpus.Concat(_extras.Discover()).ToList();
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (!_nvml.Available) return;
        _nvml.ReadInto(readings, requested);
        _extras.ReadInto(readings, requested);
    }

    /// <summary>Display name for a GPU extras row, by id suffix after "extra:".</summary>
    public static string? ExtraDisplayName(string key) => NvmlExtrasBackend.DisplayName(key);

    public void Dispose()
    {
        _extras.Dispose();
        _nvml.Dispose();
    }
}

/// <summary>
/// The helper's one <see cref="NvidiaEngine"/>, shared by every client connection: NVML calls are serialized, the
/// inventory is rescanned at most every 5 s (cards come and go around a GPU handoff), and one poll's readings are
/// reused for 0.5 s if several clients ask at once.
/// </summary>
public sealed class SharedGpuEngine(ISensorEngine? engine = null) : IDisposable
{
    private readonly ISensorEngine _engine = engine ?? new NvidiaEngine();
    private readonly object _gate = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private (IReadOnlyList<HardwareItem> Items, TimeSpan At)? _inventory;
    private (Dictionary<string, double?> Values, TimeSpan At)? _readings;

    public IReadOnlyList<HardwareItem> Discover()
    {
        lock (_gate)
        {
            if (_inventory is { } hit && _clock.Elapsed - hit.At < TimeSpan.FromSeconds(5)) return hit.Items;
            var items = _engine.Discover();
            _inventory = (items, _clock.Elapsed);
            return items;
        }
    }

    public IReadOnlyDictionary<string, double?> Read()
    {
        lock (_gate)
        {
            if (_readings is { } hit && _clock.Elapsed - hit.At < TimeSpan.FromSeconds(0.5)) return hit.Values;
            if (_inventory is null) Discover(); // NVML handles are set up by discovery
            var values = new Dictionary<string, double?>();
            _engine.ReadInto(values, null);
            _readings = (values, _clock.Elapsed);
            return values;
        }
    }

    public void Dispose() => _engine.Dispose();
}
