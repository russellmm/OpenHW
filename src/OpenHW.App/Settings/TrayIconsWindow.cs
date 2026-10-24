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
/// File → Tray Icons (also the tray menu and a sensor's "Tray icon settings…"): which sensors get tray icons, their
/// colors and order, the custom color palette, and a searchable list to add sensors. Every change is saved and applied
/// at once; there is no OK/Cancel.
/// </summary>
public sealed class TrayIconsWindow : Window
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
    private readonly WrapPanel _customChips = new();
    private readonly TextBox _newHex = new() { Width = 110, Watermark = "#RRGGBB", FontFamily = new FontFamily("monospace") };
    private readonly Border _newSwatch = new() { Width = 28, Height = 22, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly Button _addHex;

    private sealed record SensorChoice(string Id, string Group, string Name)
    {
        public string Label => Group.Length == 0 ? Name : $"{Group} › {Name}";
    }

    public TrayIconsWindow(AppSettings settings, SensorHub hub, Action applied)
    {
        _settings = settings;
        _hub = hub;
        _applied = applied;
        Title = "OpenHW Tray Icons";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MinWidth = 560;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        _addHex = Button("Add color", () => AddCustom(ColorDialog.Parse(_newHex.Text)));
        _addHex.IsEnabled = false;
        _newHex.TextChanged += (_, _) =>
        {
            var hex = ColorDialog.Parse(_newHex.Text);
            _newSwatch.Background = hex is null ? Brushes.Transparent : new SolidColorBrush(Color.Parse(hex));
            _addHex.IsEnabled = hex is not null;
        };
        _newHex.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { AddCustom(ColorDialog.Parse(_newHex.Text)); e.Handled = true; }
        };
        var pick = Button("Pick…", async () => AddCustom(await ColorDialog.PickAsync(this, ColorDialog.Parse(_newHex.Text) ?? "#2E86DE")));
        ToolTip.SetTip(pick, "Choose a color on a color wheel (or type its hex there)");
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
                    Heading("Tray icons"),
                    new TextBlock
                    {
                        Text = $"Each icon shows one sensor's value on a colored tile, and keeps that sensor live even when its group is collapsed. Up to {AppSettings.MaxTrayIcons}.",
                        Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
                    },
                    _trayRows,
                    Heading("Custom colors"),
                    new TextBlock
                    {
                        Text = "Your own tile colors, offered after the presets for every icon (here and in each icon's tray menu). " +
                               "Type a hex code or pick one; colors picked from an icon's \"Custom…\" are added here too.",
                        Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
                    },
                    _customChips,
                    Row(new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center }, _newHex, _newSwatch, _addHex, pick),
                    Heading("Add a sensor to the tray"),
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

    /// <summary>
    /// Whether this sensor already has a tray icon. "CPU temperature (automatic)" and the row it resolves to are the
    /// same sensor, so each blocks the other.
    /// </summary>
    private bool InTray(string id)
    {
        var cpu = _hub.CpuTemperatureId();
        string Resolve(string sensor) => sensor == AppSettings.CpuTemperature ? cpu ?? sensor : sensor;
        return _settings.TrayIcons.Any(t => t.SensorId == id || Resolve(t.SensorId) == Resolve(id));
    }

    // Sensors already in the tray stay listed (so a search still finds them) but grayed, and can't be added again.
    private void ApplyFilter()
    {
        var query = _search.Text ?? "";
        var shown = _choices.Where(c => Matches(c.Label, query)).ToList();
        _results.Items.Clear();
        foreach (var choice in shown)
        {
            var inTray = InTray(choice.Id);
            var item = new ListBoxItem
            {
                Content = inTray ? choice.Label + "   (in tray)" : choice.Label, Tag = choice.Id, Padding = new Thickness(8, 3), IsEnabled = !inTray,
            };
            if (inTray)
            {
                ToolTip.SetTip(item, "Already in the tray — change or remove it above, or from its own tray menu");
                ToolTip.SetShowOnDisabled(item, true);
            }
            _results.Items.Add(item);
        }
        _results.SelectedItem = _results.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.IsEnabled);
        _matches.Text = query.Trim().Length == 0 ? $"{shown.Count} sensors" : $"{shown.Count} of {_choices.Count} sensors";
    }

    // Sensor name first: group titles are long ("CPU [#0]: AMD Ryzen Threadripper 9970X: Enhanced") and would hide it.
    private string LabelFor(string id) => Sensors().FirstOrDefault(c => c.Id == id) is { } c
        ? c.Group.Length == 0 ? c.Name : $"{c.Name}  ·  {c.Group}"
        : $"{id} (not present now)";

    private void RebuildTrayRows()
    {
        RebuildCustomChips();
        _trayRows.Children.Clear();
        for (var i = 0; i < _settings.TrayIcons.Count; i++)
        {
            var index = i;
            var icon = _settings.TrayIcons[i];
            var colour = ColorBox(icon);
            var up = Button("↑", () => Move(index, -1));
            up.IsEnabled = index > 0;
            ToolTip.SetTip(up, "Move left in the tray (the top of this list is leftmost)");
            var down = Button("↓", () => Move(index, 1));
            down.IsEnabled = index < _settings.TrayIcons.Count - 1;
            ToolTip.SetTip(down, "Move right in the tray");
            var remove = Button("Remove", () => Save(() => _settings.TrayIcons.RemoveAt(index), rebuild: true));
            var label = new TextBlock { Text = LabelFor(icon.SensorId), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Width = 300 };
            ToolTip.SetTip(label, label.Text);
            _trayRows.Children.Add(Row(colour, label, up, down, remove));
        }
        if (_settings.TrayIcons.Count == 0)
            _trayRows.Children.Add(new TextBlock { Text = "No tray icons. Closing the window then leaves OpenHW running with nothing on screen: open it again from the app grid (it shows the running copy), or quit with File → Exit.", Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap });
        _add.IsEnabled = _settings.TrayIcons.Count < AppSettings.MaxTrayIcons;
    }

    // The presets, the icon's own custom color when it has one, and "Custom…" to pick any color.
    private ComboBox ColorBox(TraySensor icon)
    {
        static ComboBoxItem Swatch(string label, string hex) => new()
        {
            Tag = hex,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(Color.Parse(hex)) }, new TextBlock { Text = label } },
            },
        };
        var box = new ComboBox { Width = 130, MaxDropDownHeight = 420 };
        var all = _settings.AllColours();
        foreach (var (label, hex) in all) box.Items.Add(Swatch(label, hex));
        // An icon can keep a custom color that was later removed from the list: still show it.
        if (!all.Any(c => c.Hex.Equals(icon.Colour, StringComparison.OrdinalIgnoreCase)))
            box.Items.Add(Swatch(icon.Colour.ToUpperInvariant(), icon.Colour));
        box.Items.Add(new ComboBoxItem { Content = "Custom…", Tag = "custom" });
        var current = box.Items.OfType<ComboBoxItem>().First(c => ((string)c.Tag!).Equals(icon.Colour, StringComparison.OrdinalIgnoreCase));
        box.SelectedItem = current;
        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: string tag } || tag.Equals(icon.Colour, StringComparison.OrdinalIgnoreCase)) return;
            if (tag != "custom")
            {
                Save(() => icon.Colour = tag, rebuild: true);
                return;
            }
            var picked = await ColorDialog.PickAsync(this, icon.Colour);
            if (picked is null) box.SelectedItem = current; // cancelled: back to what it was
            else Save(() =>
            {
                _settings.AddCustomColour(picked); // kept for the other icons and the tray menus
                icon.Colour = picked;
            }, rebuild: true);
        };
        return box;
    }

    private void AddCustom(string? hex)
    {
        if (hex is null) return;
        if (AppSettings.PresetName(hex) is { } preset)
        {
            _status.Text = $"{hex} is the {preset} preset — it's already in every color list.";
            return;
        }
        Save(() => _settings.AddCustomColour(hex), rebuild: true);
        _newHex.Text = "";
    }

    // One chip per custom color: swatch, hex code, and ✕ to take it off the list (icons using it keep it).
    private void RebuildCustomChips()
    {
        _customChips.Children.Clear();
        foreach (var hex in _settings.CustomColours)
        {
            var remove = Button("✕", () => Save(() => _settings.CustomColours.Remove(hex), rebuild: true));
            remove.Padding = new Thickness(6, 0);
            ToolTip.SetTip(remove, "Remove from the list (icons using it keep it)");
            _customChips.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(3), Background = Palette.RowB,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children =
                    {
                        new Rectangle { Width = 16, Height = 16, Fill = new SolidColorBrush(Color.Parse(hex)), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = hex, FontFamily = new FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center },
                        remove,
                    },
                },
            });
        }
        if (_settings.CustomColours.Count == 0)
            _customChips.Children.Add(new TextBlock { Text = "None yet.", Foreground = Palette.Footnote });
    }

    private void AddSensor()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: string id } || _settings.TrayIcons.Count >= AppSettings.MaxTrayIcons || InTray(id)) return;
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
        if (rebuild)
        {
            RebuildTrayRows();
            ApplyFilter(); // what's grayed follows what's in the tray
        }
        _status.Text = "Saved.";
    }

    /// <summary>The tray list changed elsewhere (a sensor row's "Add to tray"): redraw it if the window is up.</summary>
    public void RefreshTrayRows()
    {
        if (!IsVisible) return;
        RebuildTrayRows();
        ApplyFilter();
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
