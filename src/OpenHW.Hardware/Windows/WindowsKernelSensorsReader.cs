namespace OpenHW.Hardware.Windows;

/// <summary>
/// The app's side of <see cref="WindowsKernelSensors"/>: the service's <c>kernel</c> reply as libsensors channels, for
/// <see cref="HwmonEngine"/> (chip path "hwmonN", subfeature "&lt;feature&gt;_input"). One request per poll serves
/// every channel; a failed request keeps the last values for 30 s, then the rows read empty.
/// </summary>
public sealed class WindowsKernelSensorsReader(Func<string>? request = null) : ILibSensorsReader
{
    private readonly Func<string> _request = request ?? (() => HelperClient.Request("kernel", receiveTimeoutMs: 2500, maxReplyBytes: GpuWire.MaxReplyBytes));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private Dictionary<(string Chip, int Index, string Feature), double?> _values = [];
    private TimeSpan _at = TimeSpan.FromDays(-1), _okAt = TimeSpan.FromDays(-1);

    public bool Available => true;
    public string? Error { get; private set; }

    public IReadOnlyList<LibSensorChannel> Discover()
    {
        var channels = Fetch(force: true);
        return channels?.Select(c => new LibSensorChannel(c.Chip, $"hwmon{c.Index}", c.Feature, c.Feature + "_input", c.Label, c.Type, Unit(c.Type))).ToList()
               ?? (IReadOnlyList<LibSensorChannel>)[];
    }

    public double? Read(LibSensorChannel channel)
    {
        Fetch(force: false);
        var index = int.Parse(channel.ChipPath["hwmon".Length..], System.Globalization.CultureInfo.InvariantCulture);
        return _values.GetValueOrDefault((channel.Chip, index, channel.Feature));
    }

    private List<KernelChannel>? Fetch(bool force)
    {
        if (!force && _clock.Elapsed - _at < TimeSpan.FromMilliseconds(900)) return null;
        _at = _clock.Elapsed;
        try
        {
            var channels = GpuWire.Decode<List<KernelChannel>>(_request()) ?? [];
            _values = channels.ToDictionary(c => (c.Chip, c.Index, c.Feature), c => c.Value);
            (_okAt, Error) = (_clock.Elapsed, null);
            return channels;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            Error = null; // the engine treats an Error as "use sysfs instead"; Windows has no other source
            if (_clock.Elapsed - _okAt > TimeSpan.FromSeconds(30)) _values = [];
            return null;
        }
    }

    private static string Unit(int type) => type switch { 0 => "V", 1 => "RPM", 2 => "°C", 3 => "W", 4 => "J", 5 => "A", _ => "" };

    public void Dispose() { }
}
