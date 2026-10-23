namespace OpenHW.Hardware;

/// <summary>
/// NVMe drives: SMART health (endurance, spare, lifetime data, error counters) and live activity (rates and
/// busy %). Drive temperatures come from the hwmon engine; the window joins both under one "Drive:" heading.
/// SMART needs root, so it is read through openhw-helper (see <see cref="NvmeSmartClient"/>).
/// </summary>
public sealed class NvmeEngine : ISensorEngine
{
    private readonly NvmeHealthBackend _health = new();
    private readonly NvmeActivityBackend _activity = new();

    public string Name => "nvme";
    public bool Available => _health.Available;

    /// <summary>Drives found at the last discovery (model, PCI address, hwmon link).</summary>
    public IReadOnlyList<NvmeDevice> Devices => _health.Devices;

    public IReadOnlyList<HardwareItem> Discover() => _health.Discover().Concat(_activity.Discover()).ToList();

    public void ReadInto(IDictionary<string, double?> readings)
    {
        _health.ReadInto(readings);
        _activity.ReadInto(readings);
    }

    public void Dispose()
    {
        _health.Dispose();
        _activity.Dispose();
    }
}
