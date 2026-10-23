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
