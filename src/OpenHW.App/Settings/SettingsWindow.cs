using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Settings;

/// <summary>
/// File → Settings: poll interval, start at login, and the tray icons (which sensors, which colours, in what order).
/// Every change is saved and applied at once; there is no OK/Cancel.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SensorHub _hub;
    private readonly Action _applied;
    private readonly StackPanel _trayRows = new() { Spacing = 4 };
    // Sensor picker: a search box over a short results list (the full list runs to hundreds of rows).
    private readonly TextBox _search = new() { Width = 420, Watermark = "Search sensors — e.g. gpu power, 6000 temp, fclk, ccd" };
    private readonly ListBox _results = new() { Height = 200, Width = 560, HorizontalAlignment = HorizontalAlignment.Left, Background = Palette.RowB };
    private readonly TextBlock _matches = new() { Foreground = Palette.Footnote, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<SensorChoice> _choices = [];
    private readonly Button _add;
    private readonly TextBlock _status = new() { Foreground = Palette.Footnote, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _startInTray;

    private sealed record SensorChoice(string Id, string Group, string Name)
    {
        public string Label => Group.Length == 0 ? Name : $"{Group} › {Name}";
    }

    public SettingsWindow(AppSettings settings, SensorHub hub, Action applied)
    {
        _settings = settings;
        _hub = hub;
        _applied = applied;
        Title = "OpenHW Settings";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MinWidth = 560;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        var poll = new ComboBox { Width = 100 };
        foreach (var seconds in AppSettings.PollChoices) poll.Items.Add(new ComboBoxItem { Content = $"{seconds:0} s", Tag = seconds });
        poll.SelectedItem = poll.Items.OfType<ComboBoxItem>().First(i => (double)i.Tag! == settings.PollSeconds);
        poll.SelectionChanged += (_, _) =>
        {
            if (poll.SelectedItem is ComboBoxItem { Tag: double s } && s != _settings.PollSeconds) Save(() => _settings.PollSeconds = s);
        };

        var startAtLogin = new CheckBox { Content = "Start OpenHW when I log in", IsChecked = settings.StartAtLogin };
        _startInTray = new CheckBox
        {
            Content = "…with only the tray icons (window hidden)", IsChecked = settings.StartInTray,
            IsEnabled = settings.StartAtLogin, Margin = new Thickness(24, 0, 0, 0),
        };
        startAtLogin.IsCheckedChanged += (_, _) =>
        {
            _startInTray.IsEnabled = startAtLogin.IsChecked == true;
            Save(() => _settings.StartAtLogin = startAtLogin.IsChecked == true);
        };
        _startInTray.IsCheckedChanged += (_, _) => Save(() => _settings.StartInTray = _startInTray.IsChecked == true);

        _add = Button("Add to tray", AddSensor);
        _add.HorizontalAlignment = HorizontalAlignment.Left;
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += (_, e) =>
        {
            // Down moves into the results; Enter adds the selected (or only) match.
            if (e.Key == Avalonia.Input.Key.Down && _results.ItemCount > 0) { _results.SelectedIndex = 0; _results.Focus(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Enter) { AddSensor(); e.Handled = true; }
        };
        _results.DoubleTapped += (_, _) => AddSensor();
        _results.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { AddSensor(); e.Handled = true; }
        };
        FillPicker();
        RebuildTrayRows();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 12), Spacing = 8,
                Children =
                {
                    Heading("General"),
                    Row(new TextBlock { Text = "Poll every", VerticalAlignment = VerticalAlignment.Center, Width = 90 }, poll,
                        new TextBlock { Text = "Shorter is more responsive; every poll reads the hardware.", Foreground = Palette.Footnote, VerticalAlignment = VerticalAlignment.Center }),
                    startAtLogin, _startInTray,
                    Heading("Tray icons"),
                    new TextBlock
                    {
                        Text = $"Each icon shows one sensor's value on a coloured tile, and keeps that sensor live even when its group is collapsed. Up to {AppSettings.MaxTrayIcons}.",
                        Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
                    },
                    _trayRows,
                    Row(_search, _matches),
                    _results,
                    _add,
                    _status,
                },
            },
        };
    }

    // Every sensor the window shows (numeric ones; yes/no flags make poor icons), as "Group › Sensor".
    private IReadOnlyList<SensorChoice> Sensors()
    {
        var choices = new List<SensorChoice> { new(AppSettings.CpuTemperature, "", "CPU temperature (this machine's, found automatically)") };
        void Walk(string group, SensorView view)
        {
            if (view.Unit != "flag") choices.Add(new SensorChoice(view.Id, group, view.Name));
            foreach (var child in view.Children) Walk(group, child);
        }
        foreach (var group in SensorPresentation.Build(_hub.Inventory, _hub.Children, _hub.Machine))
            foreach (var view in group.Sensors) Walk(group.Title, view);
        return choices;
    }

    private void FillPicker()
    {
        _choices = Sensors();
        ApplyFilter();
    }

    /// <summary>Every word typed must appear in "Device › Sensor" (any order, any case): "gpu power", "ccd2 tdie".</summary>
    public static bool Matches(string label, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => label.Contains(word, StringComparison.OrdinalIgnoreCase));

    private void ApplyFilter()
    {
        var query = _search.Text ?? "";
        var shown = _choices.Where(c => Matches(c.Label, query)).ToList();
        _results.Items.Clear();
        foreach (var choice in shown) _results.Items.Add(new ListBoxItem { Content = choice.Label, Tag = choice.Id, Padding = new Thickness(8, 3) });
        _results.SelectedIndex = shown.Count > 0 ? 0 : -1;
        _matches.Text = query.Trim().Length == 0 ? $"{shown.Count} sensors" : $"{shown.Count} of {_choices.Count} sensors";
    }

    // Sensor name first: group titles are long ("CPU [#0]: AMD Ryzen Threadripper 9970X: Enhanced") and would hide it.
    private string LabelFor(string id) => Sensors().FirstOrDefault(c => c.Id == id) is { } c
        ? c.Group.Length == 0 ? c.Name : $"{c.Name}  ·  {c.Group}"
        : $"{id} (not present now)";

    private void RebuildTrayRows()
    {
        _trayRows.Children.Clear();
        for (var i = 0; i < _settings.TrayIcons.Count; i++)
        {
            var index = i;
            var icon = _settings.TrayIcons[i];
            var colour = new ComboBox { Width = 120 };
            foreach (var (name, hex) in AppSettings.Colours)
                colour.Items.Add(new ComboBoxItem
                {
                    Tag = hex,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 6,
                        Children = { new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(Color.Parse(hex)) }, new TextBlock { Text = name } },
                    },
                });
            colour.SelectedItem = colour.Items.OfType<ComboBoxItem>().First(c => ((string)c.Tag!).Equals(icon.Colour, StringComparison.OrdinalIgnoreCase));
            colour.SelectionChanged += (_, _) =>
            {
                if (colour.SelectedItem is ComboBoxItem { Tag: string hex } && hex != icon.Colour) Save(() => icon.Colour = hex);
            };
            var up = Button("↑", () => Move(index, -1));
            up.IsEnabled = index > 0;
            var down = Button("↓", () => Move(index, 1));
            down.IsEnabled = index < _settings.TrayIcons.Count - 1;
            var remove = Button("Remove", () => Save(() => _settings.TrayIcons.RemoveAt(index), rebuild: true));
            var label = new TextBlock { Text = LabelFor(icon.SensorId), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Width = 300 };
            ToolTip.SetTip(label, label.Text);
            _trayRows.Children.Add(Row(colour, label, up, down, remove));
        }
        if (_settings.TrayIcons.Count == 0)
            _trayRows.Children.Add(new TextBlock { Text = "No tray icons. Closing the window then leaves OpenHW running with nothing on screen: open it again from the app grid (it shows the running copy), or quit with File → Exit.", Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap });
        _add.IsEnabled = _settings.TrayIcons.Count < AppSettings.MaxTrayIcons;
    }

    private void AddSensor()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: string id } || _settings.TrayIcons.Count >= AppSettings.MaxTrayIcons) return;
        // Next colour not already in use, so new icons are told apart at a glance.
        var used = _settings.TrayIcons.Select(t => t.Colour).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var colour = AppSettings.Colours.Select(c => c.Hex).FirstOrDefault(h => !used.Contains(h)) ?? AppSettings.Colours[0].Hex;
        Save(() => _settings.TrayIcons.Add(new TraySensor { SensorId = id, Colour = colour }), rebuild: true);
    }

    private void Move(int index, int by)
    {
        Save(() =>
        {
            var icons = _settings.TrayIcons;
            (icons[index], icons[index + by]) = (icons[index + by], icons[index]);
        }, rebuild: true);
    }

    private void Save(Action change, bool rebuild = false)
    {
        change();
        _settings.Save();
        _applied();
        if (rebuild) RebuildTrayRows();
        _status.Text = "Saved.";
    }

    /// <summary>The tray list changed elsewhere (a sensor row's "Add to tray"): redraw it if the window is up.</summary>
    public void RefreshTrayRows()
    {
        if (IsVisible) RebuildTrayRows();
    }

    /// <summary>The sensor list changes as hardware comes and goes; refresh it each time the window opens.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FillPicker();
        RebuildTrayRows();
        _status.Text = "";
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(0, 6, 0, 0) };

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange(children);
        return row;
    }

    private static Button Button(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center,
            Background = FooterIcons.ButtonFace, BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1), Foreground = Palette.Text,
        };
        button.Click += (_, _) => onClick();
        return button;
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
