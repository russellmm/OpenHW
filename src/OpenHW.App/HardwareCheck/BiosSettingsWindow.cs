using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.HardwareCheck;

/// <summary>
/// File → BIOS Settings…: every BIOS Setup option the BIOS publishes to the BMC (Setup → "Publish HII Resources"),
/// read over Redfish through openhw-helper — its label, current value, default and help text. Searchable; "Only
/// changed from default" narrows it to what was set by hand. Read-only: OpenHW never changes BIOS settings.
/// </summary>
public sealed class BiosSettingsWindow : Window
{
    private readonly Func<(BiosSettingsReply? Value, string? Error)> _fetch;
    private readonly TextBox _search = new() { Watermark = "Search labels, values, help and names", Width = 360 };
    private readonly CheckBox _changedOnly = new() { Content = "Only changed from default", Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { Background = Palette.Window };
    private BiosSettingsReply? _reply;
    private bool _loading;

    /// <param name="fetch">Reads the settings (tests pass a fake); called off the UI thread.</param>
    public BiosSettingsWindow(Func<(BiosSettingsReply? Value, string? Error)>? fetch = null)
    {
        _fetch = fetch ?? (() => BmcInfo.BiosSettings());
        Title = "BIOS Settings";
        Width = 960;
        Height = 760;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        _list.ItemTemplate = new FuncDataTemplate<BiosSetting>((s, _) => Row(s), supportsRecycling: false);
        _search.TextChanged += (_, _) => Filter();
        _changedOnly.IsCheckedChanged += (_, _) => Filter();
        var top = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(14, 10, 14, 6),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _search, _changedOnly, new Border { Width = 12 }, AlertUi.Button("Refresh", Refresh) } },
                _status,
            },
        };
        var foot = new TextBlock
        {
            Text = "Read from the BMC, which the BIOS fills when Setup → \"Publish HII Resources\" is on; values are those of the " +
                   "current boot. Read-only: OpenHW never changes BIOS settings.",
            Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 6, 14, 10),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(foot);
        dock.Children.Add(_list);
        Content = dock;
        Refresh();
    }

    public void Refresh()
    {
        if (_loading) return;
        _loading = true;
        _status.Text = "Reading the BIOS settings from the BMC…";
        _ = Task.Run(() =>
        {
            var (value, error) = _fetch();
            Dispatcher.UIThread.Post(() =>
            {
                _loading = false;
                if (error is not null)
                {
                    _status.Text = "Can't read the BIOS settings: " + error;
                    _status.Foreground = AlertUi.Alarm;
                    return;
                }
                _reply = value;
                _status.Foreground = Palette.Footnote;
                Filter();
            });
        });
    }

    /// <summary>The settings shown for a search text and the "changed only" switch (all words must match, any field).</summary>
    public static List<BiosSetting> Matching(IEnumerable<BiosSetting> settings, string? search, bool changedOnly)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return settings.Where(s => !changedOnly || !s.IsDefault)
            .Where(s => words.All(w => s.Display.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                       (s.Value?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) || (s.Help?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();
    }

    private void Filter()
    {
        if (_reply is null) return;
        var shown = Matching(_reply.Settings, _search.Text, _changedOnly.IsChecked == true);
        _list.ItemsSource = shown;
        var changed = _reply.Settings.Count(s => !s.IsDefault);
        _status.Text = $"{_reply.Settings.Count} options · {changed} changed from default · showing {shown.Count} · registry {_reply.Registry}";
    }

    private static Control Row(BiosSetting? s)
    {
        if (s is null) return new Border(); // Avalonia builds the template once without an item
        var head = new DockPanel();
        var value = new SelectableTextBlock
        {
            Text = s.Value ?? "—", FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Right, MaxWidth = 380,
            TextWrapping = TextWrapping.Wrap, Foreground = s.IsDefault ? Palette.Text : AlertUi.Ok,
        };
        DockPanel.SetDock(value, Dock.Right);
        head.Children.Add(value);
        head.Children.Add(new SelectableTextBlock { Text = s.Display, TextWrapping = TextWrapping.Wrap });
        var details = s.IsDefault ? s.Name : $"{s.Name} · default {s.Default}";
        var body = new StackPanel { Spacing = 1, Children = { head, new TextBlock { Text = details, Foreground = Palette.Footnote, FontSize = 11 } } };
        if (s.Help is { Length: > 0 } help && help != s.Display)
            body.Children.Add(new TextBlock { Text = help, Foreground = Palette.Footnote, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Padding = new Thickness(4, 4), BorderBrush = Palette.Separator, BorderThickness = new Thickness(0, 0, 0, 1), Child = body,
        };
    }
}
