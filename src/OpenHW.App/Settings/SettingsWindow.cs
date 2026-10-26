using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.SensorWindow;

namespace OpenHW.App.Settings;

/// <summary>
/// File → Settings: OpenHW-wide options — poll interval, start at login, pausing reads while hidden, window scale, rendering. Tray icons have their own window
/// (File → Tray Icons…) so this one stays about the app as a whole. Changes are saved and applied at once.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _applied;
    private readonly TextBlock _status = new() { Foreground = Palette.Footnote, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _scale = new() { Width = 220 };
    private readonly Button _restart;

    public SettingsWindow(AppSettings settings, Action applied, Action showTrayIcons, Action? restart = null)
    {
        _settings = settings;
        _applied = applied;
        Title = "OpenHW Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MinWidth = 480;
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
        var startInTray = new CheckBox
        {
            Content = "…with only the tray icons (window hidden)", IsChecked = settings.StartInTray,
            IsEnabled = settings.StartAtLogin, Margin = new Thickness(24, 0, 0, 0),
        };
        startAtLogin.IsCheckedChanged += (_, _) =>
        {
            startInTray.IsEnabled = startAtLogin.IsChecked == true;
            Save(() => _settings.StartAtLogin = startAtLogin.IsChecked == true);
        };
        startInTray.IsCheckedChanged += (_, _) => Save(() => _settings.StartInTray = startInTray.IsChecked == true);

        var pauseWhenHidden = new CheckBox { Content = "Pause sensors while the window is hidden", IsChecked = settings.PauseWhenHidden };
        pauseWhenHidden.IsCheckedChanged += (_, _) => Save(() => _settings.PauseWhenHidden = pauseWhenHidden.IsChecked == true);
        var softwareRendering = new CheckBox { Content = "Draw windows in software (no OpenGL)", IsChecked = settings.SoftwareRendering };
        softwareRendering.IsCheckedChanged += (_, _) =>
            Save(() => _settings.SoftwareRendering = softwareRendering.IsChecked == true, "Saved. Takes effect the next time OpenHW starts.");

        // Window scale: Avalonia sets it once at start, so a change waits for a restart (offered right here).
        foreach (var percent in WindowScale.Choices) _scale.Items.Add(new ComboBoxItem { Content = WindowScale.Label(percent), Tag = percent });
        _scale.SelectedItem = _scale.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag! == settings.WindowScale);
        _restart = new Button
        {
            Content = "Restart OpenHW now", Padding = new Thickness(10, 4), Background = FooterIcons.ButtonFace,
            BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1), Foreground = Palette.Text,
            IsVisible = false, IsEnabled = restart is not null,
        };
        _restart.Click += (_, _) => restart?.Invoke();
        _scale.SelectionChanged += (_, _) =>
        {
            if (_scale.SelectedItem is not ComboBoxItem { Tag: int percent } || percent == _settings.WindowScale) return;
            Save(() => _settings.WindowScale = percent, "Saved. Takes effect when OpenHW restarts.");
            _restart.IsVisible = percent != (WindowScale.Fixed ?? 0); // back to what's running: nothing to restart for
            if (!_restart.IsVisible) _status.Text = "Saved.";
        };

        var trayIcons = new Button
        {
            Content = "Tray Icons…", Padding = new Thickness(10, 4), Background = FooterIcons.ButtonFace,
            BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(1), Foreground = Palette.Text,
        };
        trayIcons.Click += (_, _) => showTrayIcons();

        Content = new StackPanel
        {
            Margin = new Thickness(16, 12), Spacing = 8,
            Children =
            {
                new TextBlock { Text = "General", FontWeight = FontWeight.SemiBold, FontSize = 14 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = "Poll every", VerticalAlignment = VerticalAlignment.Center, Width = 90 }, poll },
                },
                new TextBlock
                {
                    Text = "Shorter is more responsive; every poll reads the hardware.", Foreground = Palette.Footnote,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(98, -4, 0, 4),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = "Window scale", VerticalAlignment = VerticalAlignment.Center, Width = 90 }, _scale, _restart },
                },
                new TextBlock
                {
                    Text = "Automatic follows GNOME's display scale and text size. A fixed size applies to every " +
                           "OpenHW window and menu after a restart.",
                    Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(98, -4, 0, 4),
                },
                startAtLogin, startInTray,
                pauseWhenHidden,
                Note("Only sensors in the tray, with an alert, being logged or with a graph open keep reading, as if " +
                     "every group were collapsed. Saves the most when groups are left expanded. Statistics of the " +
                     "other sensors (minimum, maximum, average, graph history) skip the time the window was hidden."),
                softwareRendering,
                Note("Uses less memory (no GPU driver in OpenHW) and a little more CPU while a window is open. " +
                     "Applies the next time OpenHW starts."),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = "Tray icons, their colors and order have their own window:", Foreground = Palette.Footnote, VerticalAlignment = VerticalAlignment.Center },
                        trayIcons,
                    },
                },
                _status,
            },
        };
    }

    // "Automatic" names the scale it gives right now, read from this window once it is on a screen.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (WindowScale.Fixed is null && _scale.Items[0] is ComboBoxItem automatic)
            automatic.Content = $"Automatic (now {Math.Round(RenderScaling * 100):0} %)";
    }

    private void Save(Action change, string status = "Saved.")
    {
        change();
        _settings.Save();
        _applied();
        _status.Text = status;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text, Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, -6, 0, 4),
    };

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
