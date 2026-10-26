using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenHW.Hardware;

/// <summary>
/// The NVIDIA GPUs as the window sees them, read by openhw-helper (<c>gpus</c>, <c>gpuread</c>) rather than in the app.
/// The helper is a system service, so a device filter on the desktop session (the GPU lent to a VM) never hides a
/// card from it, and the app never opens a card itself: it can't hold one a VM needs, and it doesn't load the
/// NVIDIA driver's libraries. The NVML code is <see cref="NvidiaEngine"/>, running in the helper.
/// While the helper is away (restarting around a GPU handoff, or not installed) the last GPU rows stay, without
/// readings, and the inventory is asked for again at most every 10 s.
/// </summary>
public sealed class HelperGpuEngine(Func<string, string>? request = null) : ISensorEngine
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    private readonly Func<string, string> _request = request ?? (line => HelperClient.Request(line, maxReplyBytes: GpuWire.MaxReplyBytes));
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IReadOnlyList<HardwareItem> _items = [];
    private TimeSpan? _failedAt;

    public string Name => "nvidia";
    public bool Available => _items.Count > 0;

    public IReadOnlyList<HardwareItem> Discover()
    {
        if (_failedAt is { } failed && _clock.Elapsed - failed < RetryAfterFailure) return _items;
        try
        {
            _items = GpuWire.DecodeItems(_request("gpus"));
            _failedAt = null;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or JsonException or FormatException)
        {
            _failedAt = _clock.Elapsed; // keep the last rows: they go stale instead of disappearing
        }
        return _items;
    }

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (_items.Count == 0) return;
        if (requested is not null && !_items.Any(i => requested.Contains(i.Id))) return;
        IReadOnlyDictionary<string, double?> values;
        try { values = GpuWire.DecodeReadings(_request("gpuread")); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or JsonException or FormatException) { return; }
        foreach (var (id, value) in values)
            if (requested is null || requested.Contains(id)) readings[id] = value;
    }

    public void Dispose() { }
}

/// <summary>The Power Limits window's GPU list from openhw-helper (<c>gpulimits</c>); empty without the helper.</summary>
public sealed class HelperGpuPowerLimits(Func<string, string>? request = null) : IGpuPowerLimitReader
{
    private readonly Func<string, string> _request = request ?? (line => HelperClient.Request(line, maxReplyBytes: GpuWire.MaxReplyBytes));

    public IReadOnlyList<GpuPowerLimit> Read()
    {
        try { return GpuWire.Decode<List<GpuPowerLimit>>(_request("gpulimits")) ?? []; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or JsonException or FormatException) { return []; }
    }
}

/// <summary>
/// How GPU data crosses the helper socket: UTF-8 JSON (units like "°C" and names with "·"), base64 so the reply
/// stays one ASCII line.
/// </summary>
public static class GpuWire
{
    /// <summary>Three GPUs' rows with their details come to ~40 KB; the client still refuses anything unbounded.</summary>
    public const int MaxReplyBytes = 1 << 20;

    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // a NaN reading must not break the reply
    };

    public static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, Options));

    public static T? Decode<T>(string reply) => JsonSerializer.Deserialize<T>(Convert.FromBase64String(reply.Trim()), Options);

    public static IReadOnlyList<HardwareItem> DecodeItems(string reply) => Decode<List<HardwareItem>>(reply) ?? [];

    public static IReadOnlyDictionary<string, double?> DecodeReadings(string reply) =>
        Decode<Dictionary<string, double?>>(reply) ?? [];

    /// <summary>For logs and tests: the JSON inside a reply.</summary>
    public static string Json(string reply) => Encoding.UTF8.GetString(Convert.FromBase64String(reply.Trim()));
}
