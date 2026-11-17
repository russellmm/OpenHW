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

    public static string Describe(IReadOnlyList<FanCurvePoint>? curve) =>
        curve is null ? "–" : string.Join("  ", curve.Select(p => $"{p.TemperatureC}°→{p.DutyPercent}%"));

    public static string Controller(FanZoneStatus z) =>
        z.Target is { } t ? $"OpenHW: {t} %" + (z.Pending is { } p ? $" (→ {p} % when allowed)" : "") : "BMC curve";
}

/// <summary>
/// Power → Fan Control… (stage 1): the fan driver's zones — live RPM, who controls each (the BMC's own curve or a
/// client's fixed duty), the active curve and how many writes the BMC has taken since the helper started. Read-only:
/// curves come in the next stage; zone checks run from <c>openhw --fan-test</c>.
/// </summary>
public sealed class FanControlWindow : Window
{
    private readonly Sensors.SensorHub _hub;
    private readonly FanClient _client;
    private readonly StackPanel _body = new() { Spacing = 6, Margin = new Thickness(14, 10) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public FanControlWindow(Sensors.SensorHub hub, FanClient? client = null)
    {
        _hub = hub;
        _client = client ?? new FanClient();
        Title = "Fan Control";
        Width = 900;
        SizeToContent = SizeToContent.Height;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        Content = _body;
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    public void Refresh()
    {
        FanDriverStatus? status = null;
        string? error = null;
        try { status = _client.Status(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            error = ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old for fan control: install the current OpenHW .deb."
                : "openhw-helper isn't answering: " + ex.Message.Replace("openhw-helper: err ", "");
        }
        Draw(status, error);
    }

    private void Draw(FanDriverStatus? status, string? error)
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.Footnote,
            Text = "OpenHW's fan driver (in openhw-helper) is the one program that writes fan settings. A program takes a fan " +
                   "over by asking for a duty; the fan goes back to the BMC's own curve when that program lets go or stops. " +
                   "To spare the BMC's flash, a faster duty is written at once and a slower one only after 30 s. Curves of your " +
                   "own come in the next version.",
        });
        if (error is not null || status is null)
        {
            _body.Children.Add(new TextBlock { Text = error ?? "No answer.", Foreground = AlertUi.Alarm, TextWrapping = TextWrapping.Wrap });
            return;
        }
        if (!status.Available)
            _body.Children.Add(new TextBlock { Text = "Fan control is off: " + status.Reason, Foreground = AlertUi.Alarm, TextWrapping = TextWrapping.Wrap });
        if (status.OtherController is { } other)
            _body.Children.Add(new TextBlock { Text = other + " OpenHW won't take any fan over until it lets go.", Foreground = AlertUi.Alarm, TextWrapping = TextWrapping.Wrap });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,80,260,*,60"), Margin = new Thickness(0, 6, 0, 0) };
        void Cell(int row, int col, string text, IBrush? brush = null, FontWeight weight = FontWeight.Normal)
        {
            var cell = new TextBlock { Text = text, Foreground = brush ?? Palette.Text, FontWeight = weight, Margin = new Thickness(0, 2, 10, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headers = ["Fan", "RPM", "Controlled by", "Active curve (°C → duty)", "Writes"];
        for (var c = 0; c < headers.Length; c++) Cell(0, c, headers[c], Palette.Footnote, FontWeight.SemiBold);
        var row = 1;
        foreach (var z in status.Zones)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var rpm = _hub.Get(z.RpmSensorId).Current is { } r ? r.ToString("N0", CultureInfo.CurrentCulture) : "–";
            var name = z.Name == "CPU_FAN" ? "CPU_FAN + CPU_OPT" : z.Name;
            Cell(row, 0, name);
            Cell(row, 1, rpm);
            Cell(row, 2, FanClient.Controller(z), z.Target is null ? Palette.Footnote : Palette.Text);
            Cell(row, 3, FanClient.Describe(z.Active));
            Cell(row, 4, z.Writes.ToString(CultureInfo.CurrentCulture));
            row++;
        }
        _body.Children.Add(grid);
        _body.Children.Add(new TextBlock
        {
            Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = $"Writes counted since the helper started ({status.SinceUtc.ToLocalTime():yyyy-MM-dd HH:mm}). " +
                   "Check a fan: openhw --fan-test bmc:<n> <1–100> --seconds 10 (it takes the fan, holds the duty, then gives it back " +
                   "and compares the curve).",
        });
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
