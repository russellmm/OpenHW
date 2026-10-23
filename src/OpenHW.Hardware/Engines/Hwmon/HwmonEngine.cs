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

    public string Name => "hwmon";
    public bool Available => _libSensors.Available || _sysfs.Available;

    /// <summary>Which reader is active, for the status line.</summary>
    public string ReaderDescription => _usingLibSensors ? "libsensors" : "sysfs fallback";

    public IReadOnlyList<HardwareItem> Discover()
    {
        var library = _libSensors.Available ? _libSensors.Discover() : [];
        _usingLibSensors = _libSensors.Available && _libSensors.Error is null;
        return _usingLibSensors ? library : _sysfs.Discover().Where(i => i.Kind != HardwareKind.Control).ToList();
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (_usingLibSensors) _libSensors.ReadInto(readings, requested);
        else _sysfs.ReadInto(readings, requested);
    }

    public void Dispose()
    {
        _libSensors.Dispose();
        _sysfs.Dispose();
    }
}
