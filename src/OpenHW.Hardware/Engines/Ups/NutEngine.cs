using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace OpenHW.Hardware;

/// <summary>
/// UPSes through Network UPS Tools (NUT): upsd on 127.0.0.1:3493 (its default, local only), the plain-text protocol's
/// read commands <c>LIST UPS</c> and <c>LIST VAR &lt;ups&gt;</c> — no login, no root, nothing that sets or runs
/// anything (no INSTCMD/SET). One group per UPS NUT knows: input/output voltage and frequency, load, real and apparent
/// power, battery charge/voltage/runtime, and the status flags (on battery, low battery, …).
/// </summary>
public sealed class NutEngine(Func<string, IReadOnlyList<string>>? query = null) : ISensorEngine
{
    public const string IdPrefix = "nut:";
    public const string GroupPrefix = "nut:";
    public const int DefaultPort = 3493;

    private static readonly TimeSpan ReplyFresh = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(60);

    private readonly Func<string, IReadOnlyList<string>> _query = query ?? Query;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private List<NutUps>? _last;
    private TimeSpan _lastAt = TimeSpan.FromDays(-1), _failedAt = TimeSpan.FromDays(-1);

    public string Name => "nut";
    public bool Available => _last is { Count: > 0 };

    /// <summary>One UPS: its NUT name, description and variables.</summary>
    public sealed record NutUps(string Name, string Description, IReadOnlyDictionary<string, string> Vars);

    private sealed record Row(string Key, string Label, HardwareKind Kind, string Unit, Func<IReadOnlyDictionary<string, string>, double?> Value);

    private static double? Num(IReadOnlyDictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static double? Flag(IReadOnlyDictionary<string, string> v, string token) =>
        v.TryGetValue("ups.status", out var s) ? (s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token) ? 1 : 0) : null;

    // NUT variable → row; a row appears only when the UPS reports its variable (status flags: when it reports a status).
    private static readonly Row[] Rows =
    [
        new("input.voltage", "Input Voltage", HardwareKind.Voltage, "V", v => Num(v, "input.voltage")),
        new("input.frequency", "Input Frequency", HardwareKind.Frequency, "Hz", v => Num(v, "input.frequency")),
        new("output.voltage", "Output Voltage", HardwareKind.Voltage, "V", v => Num(v, "output.voltage")),
        new("output.frequency", "Output Frequency", HardwareKind.Frequency, "Hz", v => Num(v, "output.frequency")),
        new("ups.load", "UPS Load", HardwareKind.Load, "%", v => Num(v, "ups.load")),
        new("ups.realpower", "UPS Output Power", HardwareKind.Power, "W", v => Num(v, "ups.realpower")),
        new("ups.power", "UPS Apparent Power", HardwareKind.Power, "VA", v => Num(v, "ups.power")),
        new("ups.realpower.nominal", "UPS Power Rating", HardwareKind.Power, "W", v => Num(v, "ups.realpower.nominal")),
        new("battery.charge", "Battery Charge", HardwareKind.Load, "%", v => Num(v, "battery.charge")),
        new("battery.voltage", "Battery Voltage", HardwareKind.Voltage, "V", v => Num(v, "battery.voltage")),
        new("battery.runtime", "Battery Runtime", HardwareKind.Data, "min", v => Num(v, "battery.runtime") / 60),
        new("ups.temperature", "UPS Temperature", HardwareKind.Temperature, "°C", v => Num(v, "ups.temperature")),
        new("status:OB", "On Battery", HardwareKind.Data, "flag", v => Flag(v, "OB")),
        new("status:LB", "Low Battery", HardwareKind.Data, "flag", v => Flag(v, "LB")),
        new("status:RB", "Replace Battery", HardwareKind.Data, "flag", v => Flag(v, "RB")),
        new("status:OVER", "UPS Overloaded", HardwareKind.Data, "flag", v => Flag(v, "OVER")),
    ];

    private static string NutKey(Row r) => r.Key.StartsWith("status:", StringComparison.Ordinal) ? "ups.status" : r.Key;

    public IReadOnlyList<HardwareItem> Discover()
    {
        var all = Fetch();
        if (all is null) return [];
        var items = new List<HardwareItem>();
        foreach (var ups in all)
        {
            var model = Model(ups);
            var details = $"{model} · NUT \"{ups.Name}\" ({ups.Vars.GetValueOrDefault("driver.name", "?")}) · firmware {ups.Vars.GetValueOrDefault("ups.firmware", "?")}" +
                          (ups.Vars.TryGetValue("ups.test.result", out var test) ? $" · last self-test: {test}" : "");
            foreach (var r in Rows.Where(r => ups.Vars.ContainsKey(NutKey(r))))
                items.Add(new HardwareItem
                {
                    Id = $"{IdPrefix}{ups.Name}:{r.Key}", Name = r.Label, Kind = r.Kind, Unit = r.Unit, Group = GroupPrefix + ups.Name,
                    Backend = "openhw", CanSet = false, Source = model, Details = details,
                    Decimals = 0, // whole numbers (owner, 2026-11-17); Customize values can still change a row
                });
        }
        return items;
    }

    /// <summary>"CyberPower CP2000PFCRM2U": the description NUT was configured with, else maker + model.</summary>
    public static string Model(NutUps ups) =>
        ups.Description.Length > 0 && ups.Description != "Description unavailable" ? ups.Description
            : $"{ups.Vars.GetValueOrDefault("device.mfr", ups.Vars.GetValueOrDefault("ups.mfr", ""))} {ups.Vars.GetValueOrDefault("device.model", ups.Vars.GetValueOrDefault("ups.model", ups.Name))}".Trim();

    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        if (requested is not null && !requested.Any(id => id.StartsWith(IdPrefix, StringComparison.Ordinal))) return;
        if (Fetch() is not { } all) return;
        foreach (var ups in all)
            foreach (var r in Rows)
                readings[$"{IdPrefix}{ups.Name}:{r.Key}"] = r.Value(ups.Vars);
    }

    private List<NutUps>? Fetch()
    {
        if (_last is not null && _clock.Elapsed - _lastAt < ReplyFresh) return _last;
        if (_clock.Elapsed - _failedAt < RetryAfterFailure) return _last;
        try
        {
            var list = new List<NutUps>();
            foreach (var line in _query("LIST UPS"))
            {
                // UPS <name> "<description>"
                if (!line.StartsWith("UPS ", StringComparison.Ordinal)) continue;
                var rest = line[4..];
                var space = rest.IndexOf(' ');
                var name = space < 0 ? rest : rest[..space];
                var desc = space < 0 ? "" : Unquote(rest[(space + 1)..]);
                var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var v in _query("LIST VAR " + name))
                {
                    // VAR <ups> <var> "<value>"
                    var parts = v.Split(' ', 4);
                    if (parts.Length == 4 && parts[0] == "VAR") vars[parts[2]] = Unquote(parts[3]);
                }
                list.Add(new NutUps(name, desc, vars));
            }
            _last = list;
            _lastAt = _clock.Elapsed;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            _failedAt = _clock.Elapsed;
            _last = null;
        }
        return _last;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    /// <summary>One read-only request to upsd on localhost; the lines between BEGIN and END (or an ERR as IOException).</summary>
    public static IReadOnlyList<string> Query(string command)
    {
        if (!command.StartsWith("LIST ", StringComparison.Ordinal)) throw new ArgumentException("only LIST commands", nameof(command));
        using var client = new TcpClient { ReceiveTimeout = 1500, SendTimeout = 1500 };
        if (!client.ConnectAsync("127.0.0.1", DefaultPort).Wait(500)) throw new TimeoutException("upsd didn't answer");
        using var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes(command + "\nLOGOUT\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        string? line;
        var inList = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("ERR ", StringComparison.Ordinal)) throw new IOException("upsd: " + line);
            if (line.StartsWith("BEGIN ", StringComparison.Ordinal)) { inList = true; continue; }
            if (line.StartsWith("END ", StringComparison.Ordinal)) break;
            if (inList) lines.Add(line);
        }
        return lines;
    }

    public void Dispose() { }
}
