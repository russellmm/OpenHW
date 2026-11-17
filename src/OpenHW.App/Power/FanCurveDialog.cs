using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Fan Control → Edit…: one BMC fan's temperature sources (the BMC's own sensors, several = the hottest counts) and its
/// 5-point curve. The firmware fixes the last two points at 70 and 100 °C; duties stop at the fan's floor. Apply writes
/// what changed — the sources (Redfish, needs the BMC login) and the curve (IPMI) — once.
/// </summary>
public sealed class FanCurveDialog : Window
{
    private readonly FanClient _client;
    private readonly FanZoneStatus _zone;
    private readonly long _source;
    private readonly List<(int Number, CheckBox Box)> _boxes = [];
    private readonly NumericUpDown[] _temps = new NumericUpDown[5], _duties = new NumericUpDown[5];
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };

    public FanCurveDialog(FanClient client, FanZoneStatus zone, long source, IReadOnlyList<(int Number, string Name)> sensors, bool canSetSource)
    {
        _client = client;
        _zone = zone;
        _source = source;
        Title = $"Fan Control — {(zone.Name == "CPU_FAN" ? "CPU_FAN + CPU_OPT" : zone.Name)}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };
        body.Children.Add(new TextBlock { Text = "Follows (the hottest of the ticked sensors):", FontWeight = FontWeight.SemiBold });
        var boxes = new WrapPanel();
        foreach (var (number, name) in sensors)
        {
            // A TextBlock, not a string: Avalonia would read the "_" in "DIMMA1_Temp" as an access-key marker.
            var box = new CheckBox { Content = new TextBlock { Text = name }, IsChecked = (source & 1L << (number - 1)) != 0, IsEnabled = canSetSource, Width = 170 };
            _boxes.Add((number, box));
            boxes.Children.Add(box);
        }
        body.Children.Add(boxes);
        if (!canSetSource)
            body.Children.Add(new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
                Text = "Changing sensors needs the BMC login (Fan Control → Give OpenHW the BMC login…). The curve can be changed without it." });

        body.Children.Add(new TextBlock { Text = "Curve:", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("60,150,150"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        Put(new TextBlock { Text = "Point", Foreground = Palette.Footnote }, 0, 0);
        Put(new TextBlock { Text = "Temperature °C", Foreground = Palette.Footnote }, 0, 1);
        Put(new TextBlock { Text = "Duty %", Foreground = Palette.Footnote }, 0, 2);
        var curve = zone.Active ?? [new(20, 20), new(45, 40), new(65, 70), new(70, 100), new(100, 100)];
        for (var i = 0; i < 5; i++)
        {
            Put(new TextBlock { Text = (i + 1).ToString(CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center }, i + 1, 0);
            _temps[i] = new NumericUpDown { Minimum = 0, Maximum = 69, Increment = 1, FormatString = "0", Value = curve[i].TemperatureC, Width = 135, Margin = new Thickness(0, 2) };
            if (i >= 3) { _temps[i].Minimum = _temps[i].Maximum = _temps[i].Value!.Value; _temps[i].IsEnabled = false; }
            _duties[i] = new NumericUpDown { Minimum = zone.MinDuty, Maximum = 100, Increment = 5, FormatString = "0", Value = Math.Max(zone.MinDuty, curve[i].DutyPercent), Width = 135, Margin = new Thickness(0, 2) };
            Put(_temps[i], i + 1, 1);
            Put(_duties[i], i + 1, 2);
        }
        body.Children.Add(grid);
        body.Children.Add(new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
            Text = $"The firmware fixes points 4 and 5 at 70 and 100 °C. Lowest duty on this fan: {zone.MinDuty} %." +
                   (zone.SavedOriginal is { } o ? $" Original curve: {FanClient.Describe(o)}." : "") +
                   " Apply writes only what changed, once; the BMC keeps it." });

        var apply = AlertUi.Button("Apply", Apply);
        var restore = AlertUi.Button("Restore original", () => Run(() => _client.RestoreOriginal(_zone.Id), "Original curve restored."));
        restore.IsEnabled = zone.SavedOriginal is not null;
        ToolTip.SetTip(restore, zone.SavedOriginal is null ? "OpenHW hasn't changed this fan's curve yet." : "Puts back the curve this fan had before OpenHW first changed it.");
        body.Children.Add(AlertUi.Row(apply, restore, AlertUi.Button("Close", Close)));
        body.Children.Add(_result);
        Content = body;
    }

    public long SelectedSource() => _boxes.Where(b => b.Box.IsChecked == true).Aggregate(0L, (mask, b) => mask | 1L << (b.Number - 1));

    public IReadOnlyList<FanCurvePoint> SelectedCurve() =>
        Enumerable.Range(0, 5).Select(i => new FanCurvePoint((int)(_temps[i].Value ?? 0), (int)(_duties[i].Value ?? 0))).ToList();

    private void Apply()
    {
        var mask = SelectedSource();
        if (_boxes.Count > 0 && mask == 0) { Show("Tick at least one sensor.", true); return; }
        var curve = SelectedCurve();
        var notes = new List<string>();
        try
        {
            if (_boxes.Count > 0 && mask != _source && _boxes.Any(b => b.Box.IsEnabled))
            {
                _client.SetSource(_zone.Id, mask);
                notes.Add("sensors set");
            }
            if (_zone.Active is null || !curve.SequenceEqual(_zone.Active))
            {
                _client.SetCurve(_zone.Id, curve);
                notes.Add("curve written");
            }
            Show(notes.Count == 0 ? "Nothing changed." : char.ToUpperInvariant(notes[0][0]) + string.Join(", ", notes)[1..] + ".", false);
        }
        catch (IOException ex) { Show(ex.Message.Replace("openhw-helper: err ", ""), true); }
    }

    private void Run(Func<string> action, string done)
    {
        try { action(); Show(done, false); }
        catch (IOException ex) { Show(ex.Message.Replace("openhw-helper: err ", ""), true); }
    }

    private void Show(string text, bool bad)
    {
        _result.Text = text;
        _result.Foreground = bad ? AlertUi.Alarm : AlertUi.Ok;
    }
}

/// <summary>
/// Fan Control → Give OpenHW the BMC login…: a BMC user for Redfish writes (fan temperature sources). Typed in, or read
/// from a file with the user on line 1 and the password on line 2. The helper checks it with a real login and keeps it
/// root-only (/etc/openhw/bmc-login); it's never shown again.
/// </summary>
public sealed class BmcLoginDialog : Window
{
    private readonly FanClient _client;
    private readonly TextBox _user = new() { Width = 260, Watermark = "BMC user, e.g. openhw" };
    private readonly TextBox _password = new() { Width = 260, PasswordChar = '•', Watermark = "Password" };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };

    public BmcLoginDialog(FanClient client)
    {
        _client = client;
        Title = "BMC login";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = "OpenHW's helper needs a BMC login to change which sensors a fan follows (the BMC's Redfish service). " +
                   "It checks the login with the BMC, then keeps it readable by root only. Use a BMC user just for OpenHW." });
        body.Children.Add(AlertUi.Row(new TextBlock { Text = "User", Width = 70, VerticalAlignment = VerticalAlignment.Center }, _user));
        body.Children.Add(AlertUi.Row(new TextBlock { Text = "Password", Width = 70, VerticalAlignment = VerticalAlignment.Center }, _password));
        body.Children.Add(AlertUi.Row(AlertUi.Button("Save", () => Save(_user.Text ?? "", _password.Text ?? "")),
            AlertUi.Button("Read from file…", () => _ = FromFileAsync()), AlertUi.Button("Forget", Forget), AlertUi.Button("Close", Close)));
        body.Children.Add(_result);
        Content = body;
    }

    private async Task FromFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "BMC login file (user on line 1, password on line 2)" });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            var lines = File.ReadLines(path).Take(2).Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count < 2) { Show("The file needs the user on line 1 and the password on line 2.", true); return; }
            Save(lines[0], lines[1]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Show(ex.Message, true); }
    }

    private void Save(string user, string password)
    {
        if (user.Length == 0 || password.Length == 0) { Show("Enter a user and a password.", true); return; }
        try
        {
            var stored = _client.SetLogin(user, password);
            _password.Text = "";
            Show($"Login checked with the BMC and kept for {stored}.", false);
        }
        catch (IOException ex) { Show(ex.Message.Replace("openhw-helper: err ", ""), true); }
    }

    private void Forget()
    {
        try { _client.Logout(); Show("The helper no longer keeps a BMC login.", false); }
        catch (IOException ex) { Show(ex.Message.Replace("openhw-helper: err ", ""), true); }
    }

    private void Show(string text, bool bad)
    {
        _result.Text = text;
        _result.Foreground = bad ? AlertUi.Alarm : AlertUi.Ok;
    }
}
