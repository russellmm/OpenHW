using System.Globalization;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Power → Power Limits on Windows: the CPU socket limit (PPT) and each NVIDIA card's limit, live and kept. Like the Linux window, every change goes
/// through the helper (here the OpenHW Helper service), which checks the range and reads the result back. A live limit
/// lasts until the driver resets (reboot); "Keep" stores it by card UUID in the service, which applies it every time it
/// starts — Windows has no nvidia-power-limit.service or hsmp-control. The CPU limit goes through AMD's own HSMP driver
/// in the service (<see cref="OpenHW.Hardware.Windows.WindowsCpuPowerLimits"/>), with the Linux rules (50 W to the SMU's maximum).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerLimitsWindow : Window
{
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F07070"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#7BC97B"));

    private readonly IGpuPowerLimitReader _gpus;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8) };
    private readonly List<(Func<string> Describe, TextBlock Text)> _live = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<GpuPowerLimit> _gpusNow = [];
    private IReadOnlyDictionary<string, long> _saved = new Dictionary<string, long>();
    private CpuPowerLimit? _cpuNow;
    private string? _cpuError;
    private bool _busy;

    public WindowsPowerLimitsWindow(IGpuPowerLimitReader gpus)
    {
        _gpus = gpus;
        Title = "OpenHW Power Limits";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MinWidth = 520;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        Read();
        var sections = new StackPanel { Spacing = 2 };
        sections.Children.Add(CpuSection());
        var index = 0;
        foreach (var g in _gpusNow)
        {
            var uuid = g.Uuid;
            sections.Children.Add(Section($"GPU{index++}: {g.Name}  ({ShortBus(g.BusId)})", uuid,
                () => _gpusNow.FirstOrDefault(x => x.Uuid == uuid) is { } now
                    ? $"Limit {W(now.LimitMW)} · drawing {(now.DrawMW is { } d ? W(d) : "–")} · default {W(now.DefaultMW)} · range {W(now.MinMW)}–{W(now.MaxMW)}"
                    : "Not available",
                g));
        }
        if (_gpusNow.Count == 0)
            sections.Children.Add(new TextBlock
            {
                Text = "No NVIDIA power limits: the OpenHW Helper service isn't answering, or there's no NVIDIA card.", Margin = new Thickness(12),
                TextWrapping = TextWrapping.Wrap,
            });

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.Footnote, Margin = new Thickness(12, 10, 12, 4),
            Text = "Apply sets a live limit, which lasts until the driver resets (a reboot). \"Keep\" stores it for the card " +
                   "(by its UUID, so it follows the card to any slot); the OpenHW Helper service applies it every time it starts.",
        };
        Content = new StackPanel { Children = { note, sections, _status } };

        _timer.Tick += (_, _) => RefreshLive();
        Opened += (_, _) => _timer.Start();
        RefreshLive();
    }

    // The CPU socket limit (PPT) through AMD's HSMP driver, in the service. Without it, the reason.
    private Control CpuSection()
    {
        const string title = "CPU socket power limit (PPT)";
        var text = new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
        var children = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, text } };
        var border = new Border
        {
            Background = Palette.RowB, Margin = new Thickness(8, 4), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(3), Child = children,
        };
        if (_cpuNow is not { } c)
        {
            text.Text = "Not available: " + (_cpuError ?? "the OpenHW Helper service isn't answering.") +
                        " It uses AMD's HSMP driver (AMD chipset drivers; HSMP enabled in the BIOS).";
            return border;
        }
        _live.Add((() => _cpuNow is { } now
            ? $"Limit {W(now.LimitMW)} · drawing {(now.DrawMW is { } d ? W(d) : "–")} · range {W(CpuPowerLimit.MinMW)}–{W(now.MaxMW)}"
            : "Not available", text));
        var keptText = new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
        _live.Add((() => _cpuNow?.BootMW is { } k ? $"Kept: {W(k)}, applied when the service starts" : "Kept: none (the firmware's limit at boot)", keptText));
        var value = new NumericUpDown
        {
            Width = 130, Increment = 5, FormatString = "0", Minimum = CpuPowerLimit.MinMW / 1000m, Maximum = c.MaxMW / 1000m,
            Value = (c.BootMW ?? c.LimitMW) / 1000m, VerticalAlignment = VerticalAlignment.Center,
        };
        children.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Limit (W)", VerticalAlignment = VerticalAlignment.Center }, value,
                TextButton("Apply", () =>
                {
                    if (value.Value is { } watts) _ = RunAsync([$"cpuppt {(long)Math.Round(watts * 1000m)}"], $"CPU: limit set to {watts:0} W.");
                }),
                TextButton("Apply and keep", () =>
                {
                    if (value.Value is { } watts)
                        _ = RunAsync([$"cpuppt {(long)Math.Round(watts * 1000m)}", $"cpupptsave {(long)Math.Round(watts)}"], $"CPU: {watts:0} W now and at every start.");
                }),
                TextButton("Don't keep", () => _ = RunAsync(["cpupptsave none"], "CPU: nothing kept; the firmware's limit applies from the next boot.")),
            },
        });
        children.Children.Add(keptText);
        return border;
    }

    private Control Section(string title, string uuid, Func<string> describe, GpuPowerLimit card)
    {
        var text = new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
        _live.Add((describe, text));
        var keptText = new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
        _live.Add((() => _saved.TryGetValue(uuid, out var w) ? $"Kept: {w} W, applied when the service starts" : "Kept: none (the driver's default at boot)", keptText));
        var value = new NumericUpDown
        {
            Width = 130, Increment = 5, FormatString = "0", Minimum = card.MinMW / 1000m, Maximum = card.MaxMW / 1000m,
            Value = (_saved.TryGetValue(uuid, out var kept) ? kept * 1000 : card.LimitMW) / 1000m, VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Limit (W)", VerticalAlignment = VerticalAlignment.Center }, value,
                TextButton("Apply", () =>
                {
                    if (value.Value is { } watts) _ = RunAsync([$"gpupower {uuid} {(long)Math.Round(watts * 1000m)}"], $"{title}: limit set to {watts:0} W.");
                }),
                TextButton("Apply and keep", () =>
                {
                    if (value.Value is { } watts)
                        _ = RunAsync([$"gpupower {uuid} {(long)Math.Round(watts * 1000m)}", $"gpupowersave {uuid} {(long)Math.Round(watts)}"],
                            $"{title}: {watts:0} W now and at every start.");
                }),
                TextButton("Default", () => _ = RunAsync([$"gpupower {uuid} default", $"gpupowersave {uuid} none"], $"{title}: driver default restored and nothing kept.")),
            },
        };
        return new Border
        {
            Background = Palette.RowB, Margin = new Thickness(8, 4), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(3),
            Child = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, text, row, keptText } },
        };
    }

    private async Task RunAsync(string[] commands, string done)
    {
        if (_busy) return;
        _busy = true;
        Show("Applying…", Palette.Footnote);
        try
        {
            foreach (var command in commands) await Task.Run(() => HelperClient.Request(command, receiveTimeoutMs: 8000));
            Show(done + " Verified by readback.", Good);
        }
        catch (IOException ex)
        {
            Show($"Not changed: {ex.Message.Replace("openhw-helper: err ", "")}", Bad);
        }
        finally
        {
            _busy = false;
            RefreshLive();
        }
    }

    private void Read()
    {
        _gpusNow = _gpus.Read();
        try
        {
            _cpuNow = GpuWire.Decode<CpuPowerLimit>(HelperClient.Request("cpupptread", receiveTimeoutMs: 4000));
            _cpuError = null;
        }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException)
        {
            _cpuNow = null;
            _cpuError = ex.Message.Replace("openhw-helper: err ", "").Replace("openhw-helper: ", "");
        }
        try { _saved = GpuWire.Decode<Dictionary<string, long>>(HelperClient.Request("gpupowersaved")) ?? []; }
        catch (Exception ex) when (ex is IOException or FormatException or System.Text.Json.JsonException) { }
    }

    private void RefreshLive()
    {
        Read();
        foreach (var (describe, text) in _live) text.Text = describe();
    }

    private void Show(string text, IBrush colour)
    {
        _status.Text = text;
        _status.Foreground = colour;
    }

    private static string W(long milliwatts) => string.Create(CultureInfo.CurrentCulture, $"{milliwatts / 1000.0:0.#} W");

    private static string ShortBus(string bus) => bus.Count(ch => ch == ':') == 2 ? bus[(bus.IndexOf(':') + 1)..] : bus;

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
