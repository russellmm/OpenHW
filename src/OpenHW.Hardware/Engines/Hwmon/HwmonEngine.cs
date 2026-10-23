namespace OpenHW.Hardware;

/// <summary>
/// Motherboard Super I/O, CPU (k10temp, HSMP), DIMM hubs, drive and NIC temperatures — everything the kernel
/// exposes under /sys/class/hwmon. libsensors is preferred for its labels and conversions; plain sysfs is the
/// fallback when the library is missing or fails. Read-only: PWM controls are not listed.
/// </summary>
public sealed class HwmonEngine : ISensorEngine
{
    private readonly LibSensorsBackend _libSensors = new();
    private readonly HwmonBackend _sysfs = new();
    private bool _usingLibSensors;
    private IReadOnlyDictionary<string, double> _scales = new Dictionary<string, double>();
    private readonly string _boardVendor = SysFs.ReadText("/sys/class/dmi/id/board_vendor");
    private readonly string _boardName = SysFs.ReadText("/sys/class/dmi/id/board_name");

    public string Name => "hwmon";
    public bool Available => _libSensors.Available || _sysfs.Available;

    /// <summary>Which reader is active, for the status line.</summary>
    public string ReaderDescription => _usingLibSensors ? "libsensors" : "sysfs fallback";

    public IReadOnlyList<HardwareItem> Discover()
    {
        var library = _libSensors.Available ? _libSensors.Discover() : [];
        _usingLibSensors = _libSensors.Available && _libSensors.Error is null;
        var items = _usingLibSensors ? library : _sysfs.Discover().Where(i => i.Kind != HardwareKind.Control).ToList();
        // Known boards: name the Super I/O voltage inputs and undo their dividers (BoardProfiles).
        (items, var scales) = BoardProfiles.Apply(items, _boardVendor, _boardName);
        _scales = scales;
        return items;
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (_usingLibSensors) _libSensors.ReadInto(readings, requested);
        else _sysfs.ReadInto(readings, requested);
        foreach (var (id, scale) in _scales)
            if (readings.TryGetValue(id, out var raw) && raw is { } volts) readings[id] = volts * scale;
    }

    public void Dispose()
    {
        _libSensors.Dispose();
        _sysfs.Dispose();
    }
}
