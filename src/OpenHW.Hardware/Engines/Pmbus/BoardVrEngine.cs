namespace OpenHW.Hardware;

/// <summary>
/// HWiNFO's board VR group (e.g. "Infineon XDPE19283B"): the regulator's loop temperatures, VIN/VOUT, IIN/IOUT,
/// input/output power and efficiency, through openhw-helper's <c>vr</c> command (<see cref="BoardVrReader"/>). Only on
/// boards with a known VR (<see cref="BoardVrs"/>); readings are blank while the chip's PAGE isn't the rail's page.
/// </summary>
public sealed class BoardVrEngine(Func<string>? request = null, BoardVr? vr = null) : ISensorEngine
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);

    private readonly Func<string> _request = request ?? (() => HelperClient.Request("vr"));
    private readonly BoardVr? _vr = vr ?? BoardVrs.ForThisBoard();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _present;
    private TimeSpan? _failedAt;

    public string Name => "board-vr";
    public bool Available => _present;

    public string Group => "board-vr:" + _vr?.Slug;

    private static readonly (string Key, string Name, HardwareKind Kind, string Unit, string Register)[] Rows =
    [
        ("loop1", "VR Loop1", HardwareKind.Temperature, "°C", "READ_TEMPERATURE_1 (0x8D)"),
        ("loop2", "VR Loop2", HardwareKind.Temperature, "°C", "READ_TEMPERATURE_2 (0x8E)"),
        ("vout", "VR VOUT", HardwareKind.Voltage, "V", "READ_VOUT (0x8B, VOUT_MODE exponent)"),
        ("vin", "VR VIN", HardwareKind.Voltage, "V", "READ_VIN (0x88)"),
        ("iout", "Current (IOUT)", HardwareKind.Current, "A", "READ_IOUT (0x8C)"),
        ("iin", "Current (IIN)", HardwareKind.Current, "A", "READ_IIN (0x89)"),
        ("pout", "Power (POUT)", HardwareKind.Power, "W", "READ_POUT (0x96)"),
        ("pin", "Power (Input)", HardwareKind.Power, "W", "READ_PIN (0x97)"),
        ("efficiency", "VRM Efficiency", HardwareKind.Load, "%", "READ_POUT ÷ READ_PIN"),
    ];

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (_vr is null) return [];
        if (!_present && (_failedAt is null || _clock.Elapsed - _failedAt > RetryAfterFailure)) _present = Request() is not null;
        if (!_present) return [];
        return Rows.Select(r => new HardwareItem
        {
            Id = $"vr:{_vr.Slug}:{r.Key}", Name = r.Name, Kind = r.Kind, Unit = r.Unit, Group = Group,
            Backend = "openhw", Source = $"{_vr.Chip} PMBus (openhw-helper)", CanSet = false,
            Details = $"{r.Register}, page {_vr.RailPage} · {_vr.Adapter}, address 0x{_vr.Address:x2}",
        }).ToList();
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (!_present || _vr is null) return;
        if (requested is not null && !requested.Any(id => id.StartsWith("vr:", StringComparison.Ordinal))) return;
        var sample = Request();
        var onRail = sample is not null && sample.Page == _vr.RailPage; // another page holds another loop's values
        foreach (var (key, value) in Values(sample))
            readings[$"vr:{_vr.Slug}:{key}"] = onRail ? value : null;
    }

    public static IEnumerable<(string Key, double? Value)> Values(PmbusSample? s)
    {
        if (s is null) yield break;
        var pout = Pmbus.Linear11(s.Pout);
        var pin = Pmbus.Linear11(s.Pin);
        yield return ("loop1", Pmbus.Linear11(s.Temp1));
        yield return ("loop2", Pmbus.Linear11(s.Temp2));
        yield return ("vout", Pmbus.Linear16(s.Vout, s.VoutMode));
        yield return ("vin", Pmbus.Linear11(s.Vin));
        yield return ("iout", Pmbus.Linear11(s.Iout));
        yield return ("iin", Pmbus.Linear11(s.Iin));
        yield return ("pout", pout);
        yield return ("pin", pin);
        // The two meters are separately quantized, so at light load POUT can read above PIN; HWiNFO caps it at 100 %.
        yield return ("efficiency", pin > 0 ? Math.Min(100, pout / pin * 100) : null);
    }

    private PmbusSample? Request()
    {
        try
        {
            var sample = BoardVrReader.Parse(_request());
            _failedAt = sample is null ? _clock.Elapsed : null;
            return sample;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            _failedAt = _clock.Elapsed;
            return null;
        }
    }

    public void Dispose() { }
}
