using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>Talks to openhw-helper's fan driver (protocol 13) as one client with its own random token.</summary>
public sealed class FanClient(Func<string, string>? request = null)
{
    private readonly Func<string, string> _request = request ?? (line =>
        HelperClient.Request(line, receiveTimeoutMs: 15000, maxReplyBytes: GpuWire.MaxReplyBytes));

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public FanDriverStatus? Status(bool asOwner = false) => GpuWire.Decode<FanDriverStatus>(_request(asOwner ? "fans " + Token : "fans"));
    public string Set(string zone, int percent) => _request($"fanset {Token} {zone} {percent.ToString(CultureInfo.InvariantCulture)}");
    public string Release(string zone = "all") => _request($"fanrelease {Token} {zone}");
    public string Beat() => _request("fanbeat " + Token);

    // Configured curves and sources (protocol 14): written once, run by the BMC.
    public string SetCurve(string zone, IReadOnlyList<FanCurvePoint> curve) =>
        _request($"fancurve {zone} {string.Join(',', curve.Select(p => $"{p.TemperatureC}:{p.DutyPercent}"))}");
    public string RestoreOriginal(string zone) => _request("fanoriginal " + zone);
    public IReadOnlyDictionary<int, long> Sources() => GpuWire.Decode<Dictionary<int, long>>(_request("fansources")) ?? [];
    public string SetSource(string zone, long mask) => _request($"fansource {zone} {mask.ToString(CultureInfo.InvariantCulture)}");

    // Fan alarm thresholds (protocol 17): the BMC's lower three, in RPM.
    public IReadOnlyList<BmcFanThresholds> Thresholds() => GpuWire.Decode<List<BmcFanThresholds>>(_request("bmcthresh")) ?? [];
    public BmcThresholds? SetThresholds(string sensor, double nonRecoverable, double critical, double nonCritical) =>
        GpuWire.Decode<BmcThresholds>(_request(FormattableString.Invariant($"bmcthresh {sensor} {nonRecoverable:0} {critical:0} {nonCritical:0}")));
    public BmcThresholds? RestoreThresholds(string sensor) => GpuWire.Decode<BmcThresholds>(_request("bmcthreshoriginal " + sensor));

    // NVIDIA fans (protocol 15): ids "nvml:<uuid>:fan:<n>"; 0 % = the card's automatic control.
    public GpuFanDriverStatus? GpuStatus() => GpuWire.Decode<GpuFanDriverStatus>(_request("gpufans " + Token));
    public string GpuSet(string id, int percent) => _request($"gpufanset {Token} {id} {percent.ToString(CultureInfo.InvariantCulture)}");
    public string GpuRelease(string id = "all") => _request($"gpufanrelease {Token} {id}");

    /// <summary>The user the helper's BMC login is for, or null when none is stored.</summary>
    public string? LoginUser() => _request("bmclogin") is var user && user != "none" ? user : null;
    public string SetLogin(string user, string password) =>
        _request($"bmclogin {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user))} {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password))}");
    public string Logout() => _request("bmclogout");

    /// <summary>The BMC temperature sensors a fan can follow: number 1–18 → name (bit = number − 1 in a source mask).</summary>
    public IReadOnlyList<(int Number, string Name)> SourceSensors()
    {
        var bmc = GpuWire.Decode<BmcWireReply>(_request("bmc"));
        return bmc?.Sensors.Where(x => x.Kind == HardwareKind.Temperature && x.Number is >= 1 and <= BmcRedfish.SourceBits)
            .Select(x => ((int)x.Number, x.Name)).OrderBy(x => x.Item1).ToList() ?? [];
    }

    public static string DescribeSource(long mask, IReadOnlyList<(int Number, string Name)> sensors)
    {
        if (mask <= 0 || mask >= 1L << BmcRedfish.SourceBits) return "–";
        var names = Enumerable.Range(1, BmcRedfish.SourceBits).Where(n => (mask & 1L << (n - 1)) != 0)
            .Select(n => sensors.FirstOrDefault(x => x.Number == n).Name ?? $"sensor {n}").ToList();
        return names.Count == 1 ? names[0] : "max of " + string.Join(", ", names);
    }

    public static string Describe(IReadOnlyList<FanCurvePoint>? curve) =>
        curve is null ? "–" : string.Join("  ", curve.Select(p => $"{p.TemperatureC}°→{p.DutyPercent}%"));

    public static string Controller(FanZoneStatus z) =>
        z.Target is { } t ? $"OpenHW: {t} %" + (z.Pending is { } p ? $" (→ {p} % when allowed)" : "") : "BMC curve";
}

/// <summary>A yes/no question with the costs spelled out; true when confirmed.</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string text, string yes, bool warn)
    {
        CurveUi.Dialog(this, title, 560);
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(14, 12) };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = warn ? CurveUi.Warn : Palette.Text });
        var buttons = AlertUi.Row(AlertUi.Button("Cancel", () => Close(false)), AlertUi.Button(yes, () => Close(true)));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(buttons);
        Content = body;
        Settings.WindowPlaces.Track(this, "fan-confirm");
    }
}

/// <summary><c>openhw --fan-test bmc:N PERCENT [--seconds S]</c>: one guarded zone check from a terminal.</summary>
public static class FanTest
{
    public static int Run(string[] args)
    {
        var i = Array.IndexOf(args, "--fan-test");
        if (i < 0 || i + 2 >= args.Length || !int.TryParse(args[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
        {
            Console.Error.WriteLine("usage: openhw --fan-test bmc:<0-6> <1-100> [--seconds <5-120>]");
            return 2;
        }
        var zone = args[i + 1];
        var s = Array.IndexOf(args, "--seconds");
        var seconds = s >= 0 && s + 1 < args.Length && int.TryParse(args[s + 1], out var v) ? Math.Clamp(v, 5, 120) : 10;
        var client = new FanClient();
        var before = client.Status()?.Zones.FirstOrDefault(z => z.Id == zone);
        if (before is null) { Console.Error.WriteLine($"No fan zone {zone}."); return 2; }
        Console.WriteLine($"{zone} {before.Name}: {FanClient.Controller(before)}; curve {FanClient.Describe(before.Active)}");
        double? Rpm()
        {
            try
            {
                var bmc = GpuWire.Decode<BmcWireReply>(HelperClient.Request("bmc", receiveTimeoutMs: 15000, maxReplyBytes: GpuWire.MaxReplyBytes));
                return bmc?.Sensors.FirstOrDefault(x => x.Name == before.Name)?.Value;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException) { return null; }
        }
        Console.WriteLine($"  before: {Rpm()?.ToString("N0", CultureInfo.InvariantCulture) ?? "–"} RPM");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            Console.WriteLine("  set: " + client.Set(zone, percent));
            for (var t = 0; t < seconds && !stop.IsCancellationRequested; t += 2)
            {
                stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                client.Beat();
                Console.WriteLine($"  +{t + 2,3} s: {Rpm()?.ToString("N0", CultureInfo.InvariantCulture) ?? "–"} RPM");
            }
        }
        catch (IOException ex) { Console.Error.WriteLine("  " + ex.Message.Replace("openhw-helper: err ", "")); }
        finally
        {
            try { Console.WriteLine("  release: " + client.Release(zone)); }
            catch (IOException ex) { Console.Error.WriteLine("  release failed (the 15 s lease will restore it): " + ex.Message); }
        }
        var after = client.Status()?.Zones.FirstOrDefault(z => z.Id == zone);
        var same = after?.Active is { } a && before.Active is { } b && a.SequenceEqual(b);
        Console.WriteLine($"  after: {FanClient.Controller(after!)}; curve {FanClient.Describe(after?.Active)} — " +
                          (same ? "matches the curve before the test" : "DIFFERS from the curve before the test"));
        stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(4));
        Console.WriteLine($"  4 s later: {Rpm()?.ToString("N0", CultureInfo.InvariantCulture) ?? "–"} RPM");
        return same ? 0 : 1;
    }
}
