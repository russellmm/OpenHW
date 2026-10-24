using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.SensorWindow;

namespace OpenHW.App.Settings;

/// <summary>
/// File → Settings: OpenHW-wide options — poll interval and start at login. Tray icons have their own window
/// (File → Tray Icons…) so this one stays about the app as a whole. Changes are saved and applied at once.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _applied;
    private readonly TextBlock _status = new() { Foreground = Palette.Footnote, Margin = new Thickness(0, 6, 0, 0) };

    public SettingsWindow(AppSettings settings, Action applied, Action showTrayIcons)
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
                startAtLogin, startInTray,
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

    private void Save(Action change)
    {
        change();
        _settings.Save();
        _applied();
        _status.Text = "Saved.";
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
