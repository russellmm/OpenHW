using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// CPU socket power limit (PPT) and each NVIDIA card's power limit, live and at boot. Readings need no root; every
/// change goes through openhw-helper, which checks the range and reads the result back. A live limit lasts until
/// reboot; the "At boot" row writes the owner's boot services' config (see <see cref="BootPowerLimits"/>).
/// </summary>
public sealed class PowerLimitsWindow : Window
{
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F07070"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#7BC97B"));
    private static readonly string[] BootUnits = ["nvidia-power-limit.service", "hsmp-control-apply.service"];

    private readonly IGpuPowerLimitReader _gpus;
    private readonly CpuPowerLimits _cpu;
    private readonly BootPowerLimits _boot;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8) };
    private readonly List<(Func<string> Describe, TextBlock Text)> _live = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CpuPowerLimit? _cpuNow;
    private IReadOnlyList<GpuPowerLimit> _gpusNow = [];
    private bool _busy;

    public PowerLimitsWindow(IGpuPowerLimitReader gpus, CpuPowerLimits cpu, BootPowerLimits? boot = null)
    {
        _gpus = gpus;
        _cpu = cpu;
        _boot = boot ?? new BootPowerLimits();
        Title = "OpenHW Power Limits";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        MinWidth = 520;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        Read();
        var sections = new StackPanel { Spacing = 2 };
        if (_cpuNow is { } c)
        {
            sections.Children.Add(Section("CPU socket power limit (PPT)",
                () => _cpuNow is { } now
                    ? $"Limit {W(now.LimitMW)} · drawing {(now.DrawMW is { } d ? W(d) : "–")}" +
                      (now.BootMW is { } boot ? $" · boot value {W(boot)}" : "") +
                      (now.ActiveLimitMHz is { } mhz ? $"\nCores limited to {mhz:N0} MHz now, by: {now.LimitSource ?? "unknown"}" : "")
                    : "Not available",
                CpuPowerLimit.MinMW, c.MaxMW, c.LimitMW,
                mw => $"cpuppt {mw}", c.BootMW is null ? null : ("Boot value", "cpuppt boot"),
                BootRow("CPU", _boot.CpuServiceInstalled,
                    () => _boot.CpuBootMW() is { } b ? $"At boot: {W(b)} (hsmp-control)" : "At boot: firmware default",
                    $"At boot: firmware default ({BootPowerLimits.CpuUnit} isn't installed)",
                    BootPowerLimits.CpuMinMW, BootPowerLimits.CpuMaxMW, _boot.CpuBootMW() ?? c.LimitMW,
                    w => $"cpupptboot {w * 1000}", ("Firmware default", "cpupptboot none"))));
        }
        var index = 0;
        foreach (var g in _gpusNow)
        {
            var uuid = g.Uuid;
            sections.Children.Add(Section($"GPU{index++}: {g.Name}  ({ShortBus(g.BusId)})",
                () => _gpusNow.FirstOrDefault(x => x.Uuid == uuid) is { } now
                    ? $"Limit {W(now.LimitMW)} · drawing {(now.DrawMW is { } d ? W(d) : "–")} · default {W(now.DefaultMW)} · range {W(now.MinMW)}–{W(now.MaxMW)}"
                    : "Not available",
                g.MinMW, g.MaxMW, g.LimitMW,
                mw => $"gpupower {uuid} {mw}", ("Default", $"gpupower {uuid} default"),
                BootRow($"GPU{index - 1}", _boot.GpuServiceInstalled,
                    () => _boot.GpuBoot().TryGetValue(g.BusId, out var b)
                        ? $"At boot: {W(b.MilliWatts)} ({(b.Source == BootLimitSource.OpenHW ? "OpenHW, via its drop-in" : BootPowerLimits.GpuUnit)})"
                        : "At boot: driver default",
                    $"At boot: driver default ({BootPowerLimits.GpuUnit} isn't installed)",
                    g.MinMW, g.MaxMW, _boot.GpuBoot().TryGetValue(g.BusId, out var current) ? current.MilliWatts : g.LimitMW,
                    w => $"gpupowerboot {uuid} {w}", ("Unit value", $"gpupowerboot {uuid} default"))));
        }
        if (sections.Children.Count == 0)
            sections.Children.Add(new TextBlock { Text = "No CPU or NVIDIA power limits found.", Margin = new Thickness(12) });

        var units = BootUnits.Where(u => File.Exists(Path.Combine("/etc/systemd/system", u))).ToArray();
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.Footnote, Margin = new Thickness(12, 10, 12, 4),
            Text = "Apply sets a live limit, which lasts until reboot. The \"At boot\" row sets the value applied at every " +
                   "boot — " + (units.Length > 0
                       ? $"stored in {string.Join(" and ", units)}'s configuration (OpenHW adds no boot service of its own)."
                       : "but no boot service for it is installed, so firmware sets its own values at boot."),
        };
        Content = new StackPanel { Children = { note, sections, _status } };

        _timer.Tick += (_, _) => RefreshLive();
        Opened += (_, _) => _timer.Start();
        RefreshLive();
    }

    /// <summary>The "At boot" line and row: whole watts, set or cleared through the helper.</summary>
    private Control BootRow(string what, bool available, Func<string> describe, string unavailable,
                            long minMW, long maxMW, long currentMW, Func<long, string> setCommand, (string Label, string Command) clear)
    {
        var text = new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 6, Children = { text } };
        if (!available)
        {
            text.Text = unavailable;
            return panel;
        }
        _live.Add((describe, text));
        var value = new NumericUpDown
        {
            Width = 130, Increment = 5, FormatString = "0", Minimum = minMW / 1000m, Maximum = maxMW / 1000m,
            Value = Math.Clamp(currentMW, minMW, maxMW) / 1000m, VerticalAlignment = VerticalAlignment.Center,
        };
        var save = TextButton("Keep after reboot", () =>
        {
            if (value.Value is { } watts) _ = RunAsync(setCommand((long)Math.Round(watts)), $"{what}: {watts:0} W from the next boot.");
        });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Text = "At boot (W)", VerticalAlignment = VerticalAlignment.Center }, value, save,
                TextButton(clear.Label, () => _ = RunAsync(clear.Command, $"{what}: boot limit back to the {clear.Label.ToLowerInvariant()}.")),
            },
        });
        return panel;
    }

    private Control Section(string title, Func<string> describe, long minMW, long maxMW, long currentMW,
                            Func<long, string> applyCommand, (string Label, string Command)? reset, Control? bootRow = null)
    {
        var text = new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap };
        _live.Add((describe, text));
        var value = new NumericUpDown
        {
            Width = 130, Increment = 5, FormatString = "0", Minimum = minMW / 1000m, Maximum = maxMW / 1000m,
            Value = currentMW / 1000m, VerticalAlignment = VerticalAlignment.Center,
        };
        var apply = TextButton("Apply", () =>
        {
            if (value.Value is { } watts) _ = RunAsync(applyCommand((long)Math.Round(watts * 1000m)), $"{title}: limit set to {watts:0} W.");
        });
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = "Limit (W)", VerticalAlignment = VerticalAlignment.Center }, value, apply },
        };
        if (reset is { } r)
            row.Children.Add(TextButton(r.Label, () => _ = RunAsync(r.Command, $"{title}: {r.Label.ToLowerInvariant()} restored.", value)));
        return new Border
        {
            Background = Palette.RowB, Margin = new Thickness(8, 4), Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(3),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, text, row, bootRow ?? new Panel() },
            },
        };
    }

    private async Task RunAsync(string command, string done, NumericUpDown? showResultIn = null)
    {
        if (_busy) return;
        _busy = true;
        Show("Applying…", Palette.Footnote);
        try
        {
            var reply = await Task.Run(() => HelperClient.Request(command, receiveTimeoutMs: 8000));
            if (showResultIn is not null && long.TryParse(reply, NumberStyles.None, CultureInfo.InvariantCulture, out var mw)) showResultIn.Value = mw / 1000m;
            Show(done + " Verified by readback.", Good);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            Show(ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old for power limits: run sudo packaging/install-helper.sh"
                : $"Not changed: {ex.Message.Replace("openhw-helper: err ", "")}", Bad);
        }
        finally
        {
            _busy = false;
            RefreshLive();
        }
    }

    private void Read()
    {
        _cpuNow = _cpu.Read();
        _gpusNow = _gpus.Read();
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

    private static string W(long milliwatts) => $"{milliwatts / 1000.0:0.#} W";

    // "00000000:11:00.0" → "11:00.0", as nvidia-smi shows it.
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
