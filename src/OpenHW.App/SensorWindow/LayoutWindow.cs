using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using OpenHW.App.Sensors;

namespace OpenHW.App.SensorWindow;

/// <summary>
/// View → Layout Settings… (and a sensor's "Layout settings…"), HWiNFO's Layout tab: every group and sensor with
/// its Monitoring state, the order of groups and of sensors within a group, show/hide, and the hidden items below.
/// Changes are saved and applied at once (sensors.json); Restore Original Order goes back to OpenHW's order.
/// Per-core rows aren't listed: they follow their parent row.
/// </summary>
public sealed class LayoutWindow : Window
{
    /// <summary>One line of either list: a group heading or a sensor under it.</summary>
    public sealed record Entry(string Key, string GroupKey, bool IsGroup, string Label, string Monitoring)
    {
        public FontWeight Weight => IsGroup ? FontWeight.Bold : FontWeight.Normal;
        public Thickness Indent => new(IsGroup ? 4 : 22, 0, 0, 0);
    }

    private readonly SensorHub _hub;
    private readonly SensorCustomizations _custom = SensorCustomizations.Current;
    private readonly ListBox _list = EntryList(360);
    private readonly ListBox _hidden = EntryList(140);
    private readonly CheckBox _monitoring = new() { Content = "Monitoring" };
    private readonly CheckBox _show = new() { Content = "Show" };
    private readonly Button _top, _up, _down, _bottom, _rename, _customize, _restore;
    private readonly TextBlock _status = new() { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<SensorGroupView> _groups = [];
    private bool _filling; // the window is setting the checkboxes: not a user change

    public LayoutWindow(SensorHub hub)
    {
        _hub = hub;
        Title = "OpenHW Layout Settings";
        Width = 640;
        Height = 760;
        MinWidth = 520;
        MinHeight = 520;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        _top = Button("⤒", "Move to the top", () => Move(int.MinValue));
        _up = Button("↑", "Move up", () => Move(-1));
        _down = Button("↓", "Move down", () => Move(1));
        _bottom = Button("⤓", "Move to the bottom", () => Move(int.MaxValue));
        _rename = Button("Rename…", null, () => _ = RenameAsync());
        _customize = Button("Customize values…", null, () => _ = CustomizeAsync());
        _restore = Button("Restore Original Order", "Back to OpenHW's order for every group and sensor", _custom.RestoreOriginalOrder);

        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is not null) _hidden.SelectedItem = null; UpdateControls(); };
        _hidden.SelectionChanged += (_, _) => { if (_hidden.SelectedItem is not null) _list.SelectedItem = null; UpdateControls(); };
        _monitoring.IsCheckedChanged += (_, _) =>
        {
            if (!_filling && Selected is { IsGroup: false } e) _custom.SetDisabled(e.Key, _monitoring.IsChecked != true);
        };
        _show.IsCheckedChanged += (_, _) =>
        {
            if (!_filling && Selected is { IsGroup: false } e) _custom.SetHidden(e.Key, _show.IsChecked != true);
        };

        var order = new Border
        {
            BorderBrush = Palette.Separator, BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 8),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Order", Foreground = Palette.Footnote },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _top, _up, _down, _bottom } },
                },
            },
        };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 18,
            Children =
            {
                new StackPanel { Spacing = 2, Children = { _monitoring, _show } },
                order,
                new StackPanel { Spacing = 6, Children = { _rename, _customize } },
            },
        };
        var content = new DockPanel { Margin = new Thickness(12) };
        void Top(Control c) { DockPanel.SetDock(c, Dock.Top); content.Children.Add(c); }
        void Bottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); content.Children.Add(c); }
        Top(Header("Entry"));
        Bottom(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { _restore, _status } });
        Bottom(_hidden);
        Bottom(new TextBlock { Text = "Hidden items:", Margin = new Thickness(0, 10, 0, 4) });
        Bottom(new Border { Margin = new Thickness(0, 10, 0, 0), Child = controls });
        content.Children.Add(_list);
        Content = content;

        _custom.Changed += Refill;
        _hub.InventoryChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Refill);
        Refill();
    }

    /// <summary>Shows the window with <paramref name="sensorId"/> (a row's "Layout settings…") selected.</summary>
    public void ShowFor(string? sensorId)
    {
        Refill();
        if (sensorId is not null)
        {
            var target = Entries(_list).Concat(Entries(_hidden)).FirstOrDefault(e => !e.IsGroup && e.Key == sensorId);
            if (target is not null)
            {
                var list = Entries(_list).Contains(target) ? _list : _hidden;
                list.SelectedItem = target;
                list.ScrollIntoView(target);
            }
        }
        Show();
        Activate();
    }

    internal IReadOnlyList<Entry> ListEntries => Entries(_list).ToList();
    internal IReadOnlyList<Entry> HiddenEntries => Entries(_hidden).ToList();
    internal void Select(Entry entry) => (Entries(_list).Contains(entry) ? _list : _hidden).SelectedItem = entry;
    internal void MoveSelected(int by) => Move(by);

    private static IEnumerable<Entry> Entries(ListBox list) => list.Items.OfType<Entry>();
    private Entry? Selected => (_list.SelectedItem ?? _hidden.SelectedItem) as Entry;

    // Rebuilds both lists from the live layout, keeping the selection.
    private void Refill()
    {
        var selected = Selected;
        _groups = SensorPresentation.Build(_hub.Inventory, _hub.Children, _hub.Machine);
        string Monitoring(string id) => _custom.IsDisabled(id) ? "No" : "Yes";
        var shown = new List<Entry>();
        var hidden = new List<Entry>();
        foreach (var group in _groups)
        {
            var visible = group.Sensors.Where(s => !_custom.IsHidden(s.Id)).ToList();
            var gone = group.Sensors.Where(s => _custom.IsHidden(s.Id)).ToList();
            var monitored = visible.Any(s => !_custom.IsDisabled(s.Id)) ? "Yes" : "No";
            if (visible.Count > 0)
            {
                shown.Add(new Entry(group.Key, group.Key, true, $"[{group.Title}]", monitored));
                shown.AddRange(visible.Select(s => new Entry(s.Id, group.Key, false, s.Name, Monitoring(s.Id))));
            }
            if (gone.Count > 0)
            {
                hidden.Add(new Entry(group.Key, group.Key, true, $"[{group.Title}]", ""));
                hidden.AddRange(gone.Select(s => new Entry(s.Id, group.Key, false, s.Name, Monitoring(s.Id))));
            }
        }
        _list.ItemsSource = shown;
        _hidden.ItemsSource = hidden;
        if (selected is not null)
        {
            var again = shown.FirstOrDefault(e => e.Key == selected.Key && e.IsGroup == selected.IsGroup);
            var hiddenAgain = again is null ? hidden.FirstOrDefault(e => e.Key == selected.Key && e.IsGroup == selected.IsGroup) : null;
            _list.SelectedItem = again;
            _hidden.SelectedItem = hiddenAgain;
        }
        _restore.IsEnabled = _custom.HasCustomOrder;
        UpdateControls();
    }

    private void UpdateControls()
    {
        var entry = Selected;
        var inMainList = _list.SelectedItem is not null; // Selected prefers the main list
        var sensor = entry is { IsGroup: false } ? _hub.Inventory.FirstOrDefault(i => i.Id == entry.Key) : null;
        _filling = true;
        _monitoring.IsEnabled = _show.IsEnabled = sensor is not null;
        _monitoring.IsChecked = sensor is not null && !_custom.IsDisabled(sensor.Id);
        _show.IsChecked = sensor is not null && !_custom.IsHidden(sensor.Id);
        _filling = false;
        _top.IsEnabled = _up.IsEnabled = _down.IsEnabled = _bottom.IsEnabled = inMainList;
        _rename.IsEnabled = sensor is not null;
        _customize.IsEnabled = sensor is not null && SensorPresentation.CanCustomize(sensor);
        _status.Text = entry is null ? "Select a group or sensor." : entry.IsGroup ? "Moves the whole group." : "";
    }

    // Moves the selection among what the main list shows: a group among groups, a sensor within its group.
    private void Move(int by)
    {
        if (_list.SelectedItem is not Entry entry) return;
        MoveShown(_custom, _groups, Entries(_list).Select(e => (e.Key, e.IsGroup, e.GroupKey)).ToList(), entry.Key, entry.IsGroup, entry.GroupKey, by);
    }

    /// <summary>
    /// Moves a group among the shown groups, or a sensor within its group, and saves the new order (also the gear's
    /// Sensor Settings → Layout). Hidden rows keep their places (Arrange fills only the positions of the rows it names).
    /// by: -1/+1 one step, int.MinValue/MaxValue to the ends.
    /// </summary>
    internal static void MoveShown(SensorCustomizations custom, IReadOnlyList<SensorGroupView> groups,
        IReadOnlyList<(string Key, bool IsGroup, string GroupKey)> shown, string key, bool isGroup, string groupKey, int by)
    {
        if (isGroup)
        {
            var keys = shown.Where(e => e.IsGroup).Select(e => e.Key).ToList();
            if (Shift(keys, key, by)) custom.SetGroupOrder(SensorCustomizations.Arrange(groups.Select(g => g.Key).ToList(), k => k, keys));
        }
        else if (groups.FirstOrDefault(g => g.Key == groupKey) is { } group)
        {
            var ids = shown.Where(e => !e.IsGroup && e.GroupKey == group.Key).Select(e => e.Key).ToList();
            if (Shift(ids, key, by)) custom.SetRowOrder(group.Key, SensorCustomizations.Arrange(group.Sensors.Select(s => s.Id).ToList(), k => k, ids));
        }
    }

    // False when nothing moves.
    private static bool Shift(List<string> keys, string key, int by)
    {
        var from = keys.IndexOf(key);
        var to = by switch { int.MinValue => 0, int.MaxValue => keys.Count - 1, _ => Math.Clamp(from + by, 0, keys.Count - 1) };
        if (from < 0 || from == to) return false;
        keys.RemoveAt(from);
        keys.Insert(to, key);
        return true;
    }

    private async Task RenameAsync()
    {
        if (Selected is not { IsGroup: false } e || _hub.Inventory.FirstOrDefault(i => i.Id == e.Key) is not { } item) return;
        if (await RenameDialog.AskAsync(this, SensorPresentation.DisplayName(item), SensorPresentation.BuiltInName(item)) is { } name)
            _custom.Rename(item.Id, name);
    }

    private async Task CustomizeAsync()
    {
        if (Selected is not { IsGroup: false } e || _hub.Inventory.FirstOrDefault(i => i.Id == e.Key) is not { } item) return;
        var reading = _hub.Get(item.Id).Current is { } raw ? SensorPresentation.BuiltInScale(item, raw) : (double?)null;
        if (await CustomizeDialog.AskAsync(this, item, reading) is not { } result) return;
        _custom.Rename(item.Id, result.Name);
        _custom.Customize(item.Id, result.Values);
    }

    private static ListBox EntryList(double minHeight)
    {
        var list = new ListBox
        {
            MinHeight = minHeight, Background = Palette.RowB, BorderBrush = Palette.Separator, BorderThickness = new Thickness(1),
            ItemTemplate = new FuncDataTemplate<Entry>((_, _) =>
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90") };
                var label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                label.Bind(TextBlock.TextProperty, new Binding(nameof(Entry.Label)));
                label.Bind(TextBlock.FontWeightProperty, new Binding(nameof(Entry.Weight)));
                label.Bind(MarginProperty, new Binding(nameof(Entry.Indent)));
                var monitoring = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
                monitoring.Bind(TextBlock.TextProperty, new Binding(nameof(Entry.Monitoring)));
                Grid.SetColumn(monitoring, 1);
                grid.Children.Add(label);
                grid.Children.Add(monitoring);
                return grid;
            }, supportsRecycling: true),
        };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(4, 1)), new Setter(MinHeightProperty, 0.0) },
        });
        return list;
    }

    private static Control Header(string text) => new Grid
    {
        ColumnDefinitions = new ColumnDefinitions("*,90"), Background = Palette.ColumnHeader,
        Children =
        {
            new TextBlock { Text = text, Margin = new Thickness(6, 3) },
            new TextBlock { Text = "Monitoring", Margin = new Thickness(0, 3), HorizontalAlignment = HorizontalAlignment.Center, [Grid.ColumnProperty] = 1 },
        },
    };

    private static Button Button(string text, string? tip, Action onClick)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(10, 4), MinWidth = 36, HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = FooterIcons.ButtonFace, BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1), Foreground = Palette.Text,
        };
        if (tip is not null) ToolTip.SetTip(button, tip);
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
