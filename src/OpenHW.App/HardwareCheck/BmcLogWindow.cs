using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.HardwareCheck;

/// <summary>
/// File → BMC Status &amp; Event Log…: the BMC's discrete sensors (CPU_ECC, PSU presence, watchdog, hardware/firmware
/// change…) with their asserted states, and its System Event Log, newest first. Read-only (helper protocol 16):
/// nothing here clears the log or changes the BMC.
/// </summary>
public sealed class BmcLogWindow : Window
{
    private readonly Func<string, string> _request;
    private readonly StackPanel _body = new() { Spacing = 4, Margin = new Thickness(14, 10) };
    private bool _loading;

    /// <param name="request">One helper request → the reply after "ok " (tests pass a fake).</param>
    public BmcLogWindow(Func<string, string>? request = null)
    {
        _request = request ?? (line => HelperClient.Request(line, receiveTimeoutMs: 30000, maxReplyBytes: GpuWire.MaxReplyBytes));
        Title = "BMC Status & Event Log";
        Width = 900;
        Height = 700;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        Content = new ScrollViewer { Content = _body };
        Line("Reading the BMC…", Palette.Footnote);
        Refresh();
    }

    /// <summary>Reads both lists off the UI thread (a changed event log takes a few seconds over KCS), then shows them.</summary>
    public void Refresh()
    {
        if (_loading) return;
        _loading = true;
        _ = Task.Run(() =>
        {
            var states = Fetch<List<BmcStateWire>>("bmcstates");
            var sel = Fetch<BmcSelReply>("bmcsel");
            Dispatcher.UIThread.Post(() => { _loading = false; Show(states, sel); });
        });
    }

    private (T? Value, string? Error) Fetch<T>(string command) where T : class
    {
        try { return (GpuWire.Decode<T>(_request(command)), null); }
        catch (IOException ex) when (ex.Message.Contains("unknown command", StringComparison.Ordinal))
        { return (null, "needs openhw-helper protocol 16: install the current OpenHW .deb"); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        { return (null, ex.Message.Replace("openhw-helper: err ", "")); }
    }

    /// <summary>Words that make an asserted state worth red.</summary>
    public static bool IsBad(string state) =>
        !state.Contains("deasserted", StringComparison.Ordinal) &&
        new[] { "failure", "uncorrectable", "error", "critical", "lost", "expired", "intrusion", "IERR", "thermal trip", "disabled",
                "incompatib", "invalid", "non-recoverable", "hang", "NMI", "PERR", "SERR", "full", "limit reached", "exceeded" }
            .Any(w => state.Contains(w, StringComparison.OrdinalIgnoreCase));

    private void Show((List<BmcStateWire>? Value, string? Error) states, (BmcSelReply? Value, string? Error) sel)
    {
        _body.Children.Clear();

        Heading("Status");
        if (states.Error is { } e1) Line("Can't read the BMC's status sensors: " + e1, AlertUi.Alarm);
        else if (states.Value is { Count: 0 }) Line("The BMC lists no status sensors.");
        else if (states.Value is { } list)
        {
            var grid = Table(["Sensor", "Type", "State"], 180, 170);
            foreach (var s in list)
            {
                var text = !s.Readable ? "no reading" : s.States.Count == 0 ? "—" : string.Join(", ", s.States);
                var brush = !s.Readable ? Palette.Footnote : s.States.Any(IsBad) ? AlertUi.Alarm : Palette.Text;
                AddRow(grid, [(s.Name, Palette.Text), (s.SensorType, Palette.Footnote), (text, brush)],
                    $"IPMI sensor {s.Number:X2}h · {s.Entity} · state bits 0x{s.Bits:x4}");
            }
            _body.Children.Add(grid);
            if (list.Any(s => s.SensorType == "Power supply" && s.States.Any(IsBad)))
                Line("PSU states: the BMC reads PSUs over PMBus. When none of its PSU readings (power, temperature, fans) read, " +
                     "the PSU most likely isn't connected to the BMC, and these flags don't describe it.", Palette.Footnote);
        }

        Heading("Event log");
        if (sel.Error is { } e2) Line("Can't read the event log: " + e2, AlertUi.Alarm);
        else if (sel.Value is { } log)
        {
            var info = $"{log.Entries} entr{(log.Entries == 1 ? "y" : "ies")} · {log.FreeBytes / 16} free";
            if (log.LastAddUtc is { } add) info += $" · last added {add.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            if (log.LastEraseUtc is { } erase) info += $" · last cleared {erase.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            if (log.Newest.Count < log.Entries) info += $" · showing the newest {log.Newest.Count}";
            Line(info, Palette.Footnote);
            if (log.Newest.Count == 0) Line("The event log is empty.");
            else
            {
                var grid = Table(["Time", "Sensor", "Type", "Event"], 150, 170, 140);
                foreach (var entry in Enumerable.Reverse(log.Newest))
                {
                    var time = entry.TimeUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : entry.Time;
                    AddRow(grid, [(time, Palette.Footnote), (entry.Sensor, Palette.Text), (entry.SensorType, Palette.Footnote),
                        (entry.Event, IsBad(entry.Event) ? AlertUi.Alarm : Palette.Text)], $"Record {entry.Id:X4}h · {entry.Raw}");
                }
                _body.Children.Add(grid);
            }
        }

        _body.Children.Add(new Border { Height = 8 });
        _body.Children.Add(new TextBlock
        {
            Text = "Read-only: OpenHW never clears the BMC's event log. Times are read as UTC and shown in local time; if the BMC clock is set to local time, they are off by the UTC offset.",
            Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(AlertUi.Row(AlertUi.Button("Refresh", Refresh), AlertUi.Button("Close", Close)));
    }

    private static Grid Table(string[] headers, params double[] widths)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var w in widths) grid.ColumnDefinitions.Add(new ColumnDefinition(w, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        AddRow(grid, headers.Select(h => (h, Palette.Footnote)).ToArray(), null, bold: true);
        return grid;
    }

    private static void AddRow(Grid grid, (string Text, IBrush Brush)[] cells, string? tip, bool bold = false)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (row > 0 && row % 2 == 0)
        {
            var band = new Border { Background = Palette.RowB };
            Grid.SetRow(band, row);
            Grid.SetColumnSpan(band, cells.Length);
            grid.Children.Add(band);
        }
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = new SelectableTextBlock
            {
                Text = cells[i].Text, Foreground = cells[i].Brush, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(2, 2, 8, 2),
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center,
            };
            if (tip is not null) ToolTip.SetTip(cell, tip);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
    }

    private void Heading(string text) =>
        _body.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 14, Margin = new Thickness(0, 10, 0, 2) });

    private void Line(string text, IBrush? brush = null) =>
        _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Palette.Text });
}
