using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Power → GPU Lending…: the card filter's setup page (ROADMAP "GPU lending to a VM", step 3). One NVIDIA card is
/// kept away from the programs on a list — so it can be lent to a VM without logging out — while every other Linux
/// program keeps using it. Shows which programs have the card open now (with the list entry that would cover each),
/// and the list with "Hide now" (until reboot) and "Hide from boot". Everything goes through openhw-helper's card*
/// commands; nothing here touches the card itself.
/// </summary>
public sealed class GpuLendingWindow : Window
{
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F07070"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#7BC97B"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F0B429"));

    private readonly Func<string, string> _request;
    private readonly Func<bool> _rtxinvmGuarding;
    private readonly ComboBox _card = new() { MinWidth = 420, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _enabled = new() { Content = "Keep this card away from the hidden programs" };
    private readonly TextBlock _filterState = new() { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _holders = new() { Spacing = 4 };
    private readonly StackPanel _entries = new() { Spacing = 4 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CardFilterStatus? _now;
    private bool _showing, _busy;

    public GpuLendingWindow(Func<string, string>? request = null, Func<bool>? rtxinvmGuarding = null)
    {
        _request = request ?? (line => HelperClient.Request(line, receiveTimeoutMs: 8000, maxReplyBytes: GpuWire.MaxReplyBytes));
        _rtxinvmGuarding = rtxinvmGuarding ?? (() => File.Exists("/run/rtxinvm/status.json"));
        Title = "OpenHW GPU Lending";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        MinWidth = 620;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        _card.SelectionChanged += (_, _) =>
        {
            if (_showing || _card.SelectedItem is not ComboBoxItem { Tag: string pci }) return;
            if (pci != (_now?.Card ?? "none")) _ = RunAsync($"cardselect {pci}", pci == "none" ? "No card is lent." : $"Card {Short(pci)} chosen.");
        };
        _enabled.IsCheckedChanged += (_, _) =>
        {
            if (_showing) return;
            var on = _enabled.IsChecked == true;
            _ = RunAsync($"cardfilter {(on ? "on" : "off")}", on ? "Filter on: hidden programs can't open the card." : "Filter off: every program can open the card.");
        };

        Content = new ScrollViewer
        {
            MaxHeight = 900,
            Content = new StackPanel
            {
                Children =
                {
                    Note("Keep one NVIDIA card away from the programs listed below — so it can be lent to a virtual machine " +
                         "without logging out — while every other program on Linux (terminals, vLLM, llama.cpp, nvidia-smi…) " +
                         "can still use it. Hiding stops a program from opening the card; one that already has it open keeps " +
                         "it until it's restarted."),
                    Section("Card", new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Lend", VerticalAlignment = VerticalAlignment.Center }, _card } },
                            _enabled, _filterState,
                        },
                    }),
                    Section("Using the card now", _holders),
                    Section("Hidden programs", _entries),
                    _status,
                },
            },
        };
        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => { Refresh(); _timer.Start(); };
        Refresh();
    }

    /// <summary>Re-reads the helper's status and redraws (every 2 s while open).</summary>
    public void Refresh()
    {
        if (_busy) return;
        try { _now = GpuWire.Decode<CardFilterStatus>(_request("cardfilter")); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        {
            _now = null;
            Show(ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old for GPU lending: install the current OpenHW .deb."
                : $"openhw-helper isn't answering: {ex.Message.Replace("openhw-helper: err ", "")}", Bad);
        }
        Draw();
    }

    private void Draw()
    {
        _showing = true;
        try
        {
            var s = _now;
            if (_card.Items.Count != (s?.Cards.Count ?? 0) + 1)
            {
                _card.Items.Clear();
                _card.Items.Add(new ComboBoxItem { Content = "None", Tag = "none" });
                foreach (var c in s?.Cards ?? []) _card.Items.Add(new ComboBoxItem { Content = $"{c.Name} ({Short(c.Pci)})", Tag = c.Pci });
            }
            _card.SelectedItem = _card.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == (s?.Card ?? "none"));
            _card.IsEnabled = s is not null && !s.Enabled; // another card only while the filter is off
            ToolTip.SetTip(_card, s?.Enabled == true ? "Turn the filter off to choose another card." : null);
            _enabled.IsEnabled = s?.Card is not null;
            _enabled.IsChecked = s?.Enabled == true;
            _filterState.Text = s is null ? "" : FilterState(s);
            _filterState.Foreground = s is { Enabled: true, Attached: false } ? Bad : Palette.Footnote;
            DrawHolders(s);
            DrawEntries(s);
        }
        finally { _showing = false; }
    }

    private string FilterState(CardFilterStatus s)
    {
        if (s.Card is null) return "Choose the card to lend first.";
        var nodes = s.Nodes.Count == 0 ? "its device files aren't there (lent to a VM, or not on the NVIDIA driver)" : string.Join(", ", s.Nodes.Select(n => n.Path));
        var state = s switch
        {
            { Enabled: true, Attached: true } => "On: hidden programs are refused " + nodes + ".",
            { Enabled: true } => "On, but the filter isn't attached — check openhw-helper's log.",
            _ => "Off: every program can open " + nodes + ".",
        };
        return _rtxinvmGuarding() && !s.Enabled
            ? "Off. RTXinVM's guard is protecting this card at the moment (it hides it from everything except programs " +
              "started with rtxgpu); leave this off until OpenHW takes over lending."
            : state;
    }

    private void DrawHolders(CardFilterStatus? s)
    {
        _holders.Children.Clear();
        if (s?.Card is null) { _holders.Children.Add(Footnote("No card chosen.")); return; }
        if (s.Holders.Count == 0) { _holders.Children.Add(Footnote("Nothing has the card open.")); return; }
        foreach (var group in s.Holders.GroupBy(h => h.Pattern ?? "").OrderBy(g => g.Key == "" ? 1 : 0).ThenBy(g => EntryName(g.Key, g), StringComparer.OrdinalIgnoreCase))
        {
            var pattern = group.Key;
            var programs = string.Join(", ", group.GroupBy(h => h.Program).Select(p => p.Count() == 1 ? $"{p.Key} ({p.First().Pid})" : $"{p.Key} ×{p.Count()}"));
            // Text on the left (name, programs, any warning under them); buttons on the right only when there's an action.
            var text = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = pattern == "" ? "Not in the desktop session" : EntryName(pattern, group), FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = programs, Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap },
                },
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { text } };
            if (pattern == "" && group.All(h => h.Program == "openhw-helper"))
            {
                ((TextBlock)text.Children[0]).Text = "OpenHW's helper (reads the GPU sensors)";
                text.Children.Add(Footnote("Lets go of the card for the handoff and picks it up again afterwards."));
            }
            else if (pattern == "")
                text.Children.Add(Footnote("System services can't be hidden; stop them before lending the card.", Warn));
            else if (group.All(h => h.Hidden))
                text.Children.Add(Footnote(pattern == "session.slice"
                    ? "Hidden, but the desktop still has the card open: log out once to free it."
                    : "Hidden, but still has the card open: restart it.", Warn));
            else
            {
                var name = EntryName(pattern, group);
                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        TextButton("Hide now", () => _ = RunAsync($"cardhide {pattern} now {name}", $"{name}: hidden until reboot; restart it to let go of the card.")),
                        TextButton("Hide from boot", () => _ = RunAsync($"cardhide {pattern} both {name}", $"{name}: hidden now and from every boot; restart it to let go of the card.")),
                    },
                };
                Grid.SetColumn(buttons, 1);
                row.Children.Add(buttons);
            }
            _holders.Children.Add(row);
        }
    }

    private void DrawEntries(CardFilterStatus? s)
    {
        _entries.Children.Clear();
        if (s is null || s.Entries.Count == 0) { _entries.Children.Add(Footnote(s?.Card is null ? "No card chosen." : "Nothing is hidden.")); return; }
        foreach (var e in s.Entries)
        {
            var pattern = e.Pattern;
            var label = e.Name.Length > 0 ? e.Name : pattern;
            var now = new CheckBox { Content = "Hide now", IsChecked = e.HiddenNow, VerticalAlignment = VerticalAlignment.Center };
            var boot = new CheckBox { Content = "Hide from boot", IsChecked = e.HideAtBoot, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(now, "Hidden until the next reboot. Programs already holding the card keep it until restarted.");
            ToolTip.SetTip(boot, pattern == "session.slice"
                ? "Hidden from the start of every boot, before the login screen. Untick to let the GNOME desktop see the card from the next boot — then the VM can't start until you log out."
                : "Hidden from the start of every boot, before the login screen.");
            now.IsCheckedChanged += (_, _) =>
            {
                if (_showing) return;
                var hide = now.IsChecked == true;
                _ = RunAsync($"{(hide ? "cardhide" : "cardshow")} {pattern} now", $"{label}: {(hide ? "hidden until reboot" : "can open the card now")}.");
            };
            boot.IsCheckedChanged += (_, _) =>
            {
                if (_showing) return;
                var hide = boot.IsChecked == true;
                _ = RunAsync($"{(hide ? "cardhide" : "cardshow")} {pattern} boot", $"{label}: {(hide ? "hidden from every boot" : "not hidden at boot any more")}.");
            };
            _entries.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new StackPanel
                    {
                        Width = 330,
                        Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, Footnote(pattern) },
                    },
                    now, boot,
                    TextButton("Remove", () => _ = RunAsync($"cardforget {pattern}", $"{label} removed from the list.")),
                },
            });
        }
    }

    private async Task RunAsync(string command, string done)
    {
        if (_busy) return;
        _busy = true;
        Show("Applying…", Palette.Footnote);
        try
        {
            await Task.Run(() => _request(command));
            Show(done, Good);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            Show($"Not changed: {ex.Message.Replace("openhw-helper: err ", "")}", Bad);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // The list's own name, else a readable one from the unit pattern, else the programs holding the card.
    private string EntryName(string pattern, IEnumerable<CardHolder> holders) =>
        _now?.Entries.FirstOrDefault(e => e.Pattern == pattern && e.Name.Length > 0)?.Name
        ?? pattern switch
        {
            "session.slice" => "GNOME desktop",
            _ when pattern.StartsWith("user-", StringComparison.Ordinal) && pattern.EndsWith(".slice", StringComparison.Ordinal) => "Login screen",
            _ when pattern.StartsWith("vte-spawn-", StringComparison.Ordinal) => "Terminal windows",
            _ => Capitalize(holders.Select(h => h.Program).FirstOrDefault() ?? pattern),
        };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // "0000:e1:00.0" → "E1:00.0", as nvidia-smi and the GPU groups show it.
    private static string Short(string pci) => pci.Count(ch => ch == ':') == 2 ? pci[(pci.IndexOf(':') + 1)..].ToUpperInvariant() : pci;

    private void Show(string text, IBrush colour)
    {
        _status.Text = text;
        _status.Foreground = colour;
    }

    private static Control Section(string title, Control body) => new Border
    {
        Background = Palette.RowB, Margin = new Thickness(8, 4), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(3),
        Child = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, body } },
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Footnote, Margin = new Thickness(12, 10, 12, 4),
    };

    private static TextBlock Footnote(string text, IBrush? colour = null) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = colour ?? Palette.Footnote, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Button TextButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center,
            Background = FooterIcons.ButtonFace, BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1),
            Foreground = Palette.Text,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            _timer.Stop();
            Hide();
        }
        base.OnClosing(e);
    }
}
