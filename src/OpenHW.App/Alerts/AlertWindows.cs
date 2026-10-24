using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Alerts;

internal static class AlertUi
{
    public static readonly IBrush Alarm = new SolidColorBrush(Color.Parse("#F07070"));
    public static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#7BC97B"));

    public static Button Button(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center,
            Background = FooterIcons.ButtonFace, BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1), Foreground = Palette.Text,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange(children);
        return row;
    }

    public static double? ParseNumber(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            ? (double.IsFinite(v) ? v : null) : null;
}

/// <summary>
/// Sensor menu → Alert settings…: this sensor's alert rules (switch on/off, remove) and a row to add one — above/below a
/// value in the sensor's unit, held for a chosen time (Yes/No sensors: "becomes Yes/No").
/// </summary>
public sealed class AlertDialog : Window
{
    private readonly AlertMonitor _monitor;
    private readonly SensorHub _hub;
    private readonly HardwareItem _item;
    private readonly string _unit;
    private readonly StackPanel _rules = new() { Spacing = 4 };
    private readonly TextBlock _current = new() { Foreground = Palette.Footnote };

    public string SensorId => _item.Id;

    public AlertDialog(AlertMonitor monitor, SensorHub hub, HardwareItem item)
    {
        _monitor = monitor;
        _hub = hub;
        _item = item;
        _unit = SensorPresentation.DisplayUnit(item);
        Title = $"Alerts — {SensorPresentation.DisplayName(item)}";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        var flag = _unit == "flag";
        var condition = new ComboBox { Width = 130 };
        condition.Items.Add(new ComboBoxItem { Content = flag ? "becomes Yes" : "is above", Tag = AlertCondition.Above });
        condition.Items.Add(new ComboBoxItem { Content = flag ? "becomes No" : "is below", Tag = AlertCondition.Below });
        condition.SelectedIndex = 0;
        var value = new TextBox
        {
            Width = 100, IsVisible = !flag,
            Text = AlertMonitor.DisplayValue(hub, item) is { } now ? AlertMonitor.ThresholdText(Math.Round(now)) : "",
        };
        var unit = new TextBlock { Text = flag ? "" : _unit, VerticalAlignment = VerticalAlignment.Center };
        var duration = new ComboBox { Width = 90 };
        foreach (var s in AlertRule.DurationChoices) duration.Items.Add(new ComboBoxItem { Content = s == 0 ? "at once" : AlertRule.Duration(s), Tag = s });
        duration.SelectedIndex = 2; // 10 s: one spike doesn't fire it
        var error = new TextBlock { Foreground = AlertUi.Alarm, IsVisible = false };
        var add = AlertUi.Button("Add", () =>
        {
            double? threshold = flag ? 0.5 : AlertUi.ParseNumber(value.Text);
            if (threshold is null)
            {
                error.Text = "Enter a number for the threshold.";
                error.IsVisible = true;
                return;
            }
            error.IsVisible = false;
            monitor.Store.Add(new AlertRule
            {
                SensorId = item.Id, Condition = (AlertCondition)((ComboBoxItem)condition.SelectedItem!).Tag!, Threshold = threshold.Value,
                ForSeconds = (int)((ComboBoxItem)duration.SelectedItem!).Tag!,
            });
        });

        Content = new StackPanel
        {
            Margin = new Thickness(16, 12, 16, 18), Spacing = 8,
            Children =
            {
                new TextBlock { Text = SensorPresentation.DisplayName(item), FontWeight = FontWeight.SemiBold, FontSize = 14 },
                _current,
                new TextBlock { Text = "Rules", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                _rules,
                new TextBlock { Text = "Add a rule", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                AlertUi.Row(new TextBlock { Text = "Alert when it", VerticalAlignment = VerticalAlignment.Center }, condition, value, unit,
                            new TextBlock { Text = "for", VerticalAlignment = VerticalAlignment.Center }, duration, add),
                error,
                new TextBlock
                {
                    Text = "An alert shows a desktop notification (kept until dismissed) and turns the sensor's Current value red; " +
                           "a second notification says when it's back to normal. The sensor is read every poll while it has an alert.",
                    Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        monitor.Store.Changed += Rebuild;
        Closed += (_, _) => monitor.Store.Changed -= Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        _rules.Children.Clear();
        foreach (var rule in _monitor.Store.For(_item.Id).ToList())
        {
            var enabled = new CheckBox { IsChecked = rule.Enabled, Content = rule.Describe(_unit) };
            enabled.IsCheckedChanged += (_, _) => _monitor.Store.Update(() => rule.Enabled = enabled.IsChecked == true);
            var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Tag = rule };
            _rules.Children.Add(AlertUi.Row(enabled, state, AlertUi.Button("Remove", () => _monitor.Store.Remove(rule))));
        }
        if (_rules.Children.Count == 0) _rules.Children.Add(new TextBlock { Text = "None yet.", Foreground = Palette.Footnote });
        Refresh();
    }

    /// <summary>Current value and each rule's state. UI thread, every poll.</summary>
    public void Refresh()
    {
        _current.Text = AlertMonitor.DisplayValue(_hub, _item) is { } v ? $"Now: {SensorPresentation.Format(v, _unit)}" : "Now: no reading";
        foreach (var state in _rules.LogicalDescendants<TextBlock>().Where(t => t.Tag is AlertRule))
        {
            var rule = (AlertRule)state.Tag!;
            var firing = _monitor.IsFiring(rule);
            state.Text = !rule.Enabled ? "off" : firing ? "ALERT" : "OK";
            state.Foreground = !rule.Enabled ? Palette.Footnote : firing ? AlertUi.Alarm : AlertUi.Ok;
        }
    }
}

/// <summary>File → Alerts…: every rule, its sensor and live state, with Edit… (that sensor's Alert Settings) and Remove.</summary>
public sealed class AlertsWindow : Window
{
    private readonly AlertMonitor _monitor;
    private readonly SensorHub _hub;
    private readonly Action<string> _edit;
    private readonly StackPanel _list = new() { Spacing = 4 };

    public AlertsWindow(AlertMonitor monitor, SensorHub hub, Action<string> edit)
    {
        _monitor = monitor;
        _hub = hub;
        _edit = edit;
        Title = "OpenHW Alerts";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        MinWidth = 560;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        Content = new ScrollViewer
        {
            MaxHeight = 700,
            Content = new StackPanel
            {
                Margin = new Thickness(16, 12), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Alerts", FontWeight = FontWeight.SemiBold, FontSize = 14 },
                    new TextBlock
                    {
                        Text = "Add one from any sensor: right-click it → Alert settings…. Each fires a desktop notification when " +
                               "its condition holds for the chosen time, and turns the sensor's value red until it clears.",
                        Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
                    },
                    _list,
                },
            },
        };
        monitor.Store.Changed += Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var items = _hub.Inventory.ToDictionary(i => i.Id, StringComparer.Ordinal);
        foreach (var rule in _monitor.Store.Snapshot())
        {
            items.TryGetValue(rule.SensorId, out var item);
            var unit = item is null ? "" : SensorPresentation.DisplayUnit(item);
            var enabled = new CheckBox { IsChecked = rule.Enabled };
            ToolTip.SetTip(enabled, "On / off");
            enabled.IsCheckedChanged += (_, _) => _monitor.Store.Update(() => rule.Enabled = enabled.IsChecked == true);
            var name = new TextBlock
            {
                Text = item is null ? $"{rule.SensorId} (not present now)" : SensorPresentation.DisplayName(item),
                Width = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(name, name.Text);
            var what = new TextBlock { Text = rule.Describe(unit), Width = 190, VerticalAlignment = VerticalAlignment.Center };
            var state = new TextBlock { Width = 50, VerticalAlignment = VerticalAlignment.Center, Tag = rule };
            var edit = AlertUi.Button("Edit…", () => _edit(rule.SensorId));
            edit.IsEnabled = item is not null;
            _list.Children.Add(AlertUi.Row(enabled, name, what, state, edit, AlertUi.Button("Remove", () => _monitor.Store.Remove(rule))));
        }
        if (_list.Children.Count == 0)
            _list.Children.Add(new TextBlock { Text = "No alerts yet.", Foreground = Palette.Footnote });
        Refresh();
    }

    /// <summary>Each rule's live state. UI thread, every poll.</summary>
    public void Refresh()
    {
        foreach (var state in _list.LogicalDescendants<TextBlock>().Where(t => t.Tag is AlertRule))
        {
            var rule = (AlertRule)state.Tag!;
            var firing = _monitor.IsFiring(rule);
            state.Text = !rule.Enabled ? "off" : firing ? "ALERT" : "OK";
            state.Foreground = !rule.Enabled ? Palette.Footnote : firing ? AlertUi.Alarm : AlertUi.Ok;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}

internal static class LogicalHelpers
{
    // Rows are built in code, so walk the logical tree (works before the window is shown, unlike the visual tree).
    public static IEnumerable<T> LogicalDescendants<T>(this Control root) where T : Control
    {
        foreach (var child in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root))
            if (child is T match) yield return match;
    }
}
