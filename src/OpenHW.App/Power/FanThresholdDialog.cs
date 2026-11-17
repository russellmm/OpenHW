using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Fan Control → ⋮ → Alarm thresholds…: one BMC fan's lower thresholds — the RPM below which the BMC logs it as slow
/// (non-critical), failing (critical) and stopped (non-recoverable). The BMC stores them in steps (120 RPM on the
/// WRX90E), so only those values are offered. Apply writes the three once and reads them back; the thresholds the fan
/// had before OpenHW first changed them are kept for Restore original. On this board a crossing was only seen to add log entries.
/// </summary>
public sealed class FanThresholdDialog : Window
{
    private readonly FanClient _client;
    private readonly string _sensor;
    private readonly StackPanel _body = new() { Spacing = 8, Margin = new Thickness(14, 10) };
    private ComboBox _nonCritical = Combo(), _critical = Combo(), _nonRecoverable = Combo();
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private BmcFanThresholds? _fan;

    /// <summary>Highest value offered: well above any idle chassis fan, far below the BMC's 30,600 RPM ceiling.</summary>
    public const double OfferUpTo = 3000;

    public FanThresholdDialog(FanClient client, string sensor)
    {
        _client = client;
        _sensor = sensor;
        Title = $"Alarm thresholds — {sensor}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        Content = _body;
        Load();
    }

    private static ComboBox Combo() => new() { Width = 130, MaxDropDownHeight = 360 };

    private void Load()
    {
        _body.Children.Clear();
        (_nonCritical, _critical, _nonRecoverable) = (Combo(), Combo(), Combo()); // fresh: the old ones still sit in the old grid
        try { _fan = _client.Thresholds().FirstOrDefault(f => f.Name == _sensor); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            var why = ex.Message.Replace("openhw-helper: err ", "");
            Note(why.Contains("unknown command", StringComparison.Ordinal) ? "Needs openhw-helper protocol 17: install the current OpenHW .deb." : "Can't read the BMC: " + why, AlertUi.Alarm);
            _body.Children.Add(AlertUi.Row(AlertUi.Button("Close", Close)));
            return;
        }
        if (_fan is null || !_fan.Settable)
        {
            Note(_fan is null ? $"The BMC has no fan sensor named {_sensor}." : $"The BMC doesn't let {_sensor}'s thresholds be changed.", AlertUi.Alarm);
            _body.Children.Add(AlertUi.Row(AlertUi.Button("Close", Close)));
            return;
        }
        var f = _fan;
        Note($"Now: {Rpm(f.Reading)}. The BMC adds an entry to its event log when the fan drops below a threshold " +
             "(and another when it's back above). On this board that is all a crossing was seen to do: the fans kept their curves.", Palette.Text);

        var max = Math.Min(f.Max, OfferUpTo);
        foreach (var box in new[] { _nonCritical, _critical, _nonRecoverable })
            for (var v = 0.0; v <= max + 1e-6; v += f.Step)
                box.Items.Add(new ComboBoxItem { Content = Rpm(v), Tag = v });
        Select(_nonCritical, f.NonCritical);
        Select(_critical, f.Critical);
        Select(_nonRecoverable, f.NonRecoverable);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,140,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); c.Margin = new Thickness(0, 2); }
        (string Label, ComboBox Box, double? Original)[] rows =
        [
            ("Slow (lower non-critical)", _nonCritical, f.Original?[2]),
            ("Failing (lower critical)", _critical, f.Original?[1]),
            ("Stopped (lower non-recoverable)", _nonRecoverable, f.Original?[0]),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            Put(new TextBlock { Text = rows[i].Label, VerticalAlignment = VerticalAlignment.Center }, i, 0);
            Put(rows[i].Box, i, 1);
            if (rows[i].Original is { } o)
                Put(new TextBlock { Text = "original " + Rpm(o), Foreground = Palette.Footnote, VerticalAlignment = VerticalAlignment.Center }, i, 2);
        }
        _body.Children.Add(grid);
        Note($"The BMC stores these in {Rpm(f.Step)} steps; each must be at or below the one above it. " +
             "Apply writes them to the BMC once (its settings live in flash); whether they survive a BMC reset or power loss " +
             "depends on the firmware.", Palette.Footnote);

        var apply = AlertUi.Button("Apply", Apply);
        var restore = AlertUi.Button("Restore original", () => Run(() => _client.RestoreThresholds(_sensor), "Original thresholds restored."));
        restore.IsEnabled = f.Original is not null;
        ToolTip.SetTip(restore, f.Original is null ? "OpenHW hasn't changed this fan's thresholds." : "Puts back the thresholds this fan had before OpenHW first changed them.");
        _body.Children.Add(AlertUi.Row(apply, restore, AlertUi.Button("Close", Close)));
        _body.Children.Add(_result);
    }

    private static void Select(ComboBox box, double? value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (item.Tag is double v && value is { } x && Math.Abs(v - x) < 1e-6) { box.SelectedItem = item; return; }
    }

    private static double? Value(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: double v } ? v : null;

    public static string Rpm(double? value) => value is { } v ? v.ToString("#,0", CultureInfo.CurrentCulture) + " RPM" : "no reading";

    private void Apply()
    {
        if (Value(_nonCritical) is not { } lnc || Value(_critical) is not { } lcr || Value(_nonRecoverable) is not { } lnr)
        {
            Show("Pick all three values.", true);
            return;
        }
        if (!(lnr <= lcr && lcr <= lnc))
        {
            Show("Stopped ≤ Failing ≤ Slow: each threshold must be at or below the one above it.", true);
            return;
        }
        Run(() => _client.SetThresholds(_sensor, lnr, lcr, lnc), "Written and read back.");
    }

    private void Run(Func<BmcThresholds?> write, string done)
    {
        try
        {
            var t = write();
            Load();
            Show(t is null ? done : $"{done} Slow {Rpm(t.LowerNonCritical)}, failing {Rpm(t.LowerCritical)}, stopped {Rpm(t.LowerNonRecoverable)}.", false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            Show(ex.Message.Replace("openhw-helper: err ", ""), true);
        }
    }

    private void Show(string text, bool bad)
    {
        _result.Text = text;
        _result.Foreground = bad ? AlertUi.Alarm : AlertUi.Ok;
    }

    private void Note(string text, IBrush brush) =>
        _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush });
}
