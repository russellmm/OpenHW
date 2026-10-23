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
/// CPU frequency caps: one ceiling for every core, or one per physical core (both SMT threads share a clock, so a
/// core's cap goes to both of its cpufreq policies). Writes go through openhw-helper; caps last until reboot and the
/// power-mode fix keeps them across power-saver. Live clocks refresh once a second while the window is open.
/// </summary>
public sealed class CpuFrequencyWindow : Window
{
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F0B429"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F07070"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#7BC97B"));
    private const string Columns = "150,90,150,*";

    private readonly PowerModeController _power;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Foreground = Warn, IsVisible = false };
    private readonly NumericUpDown _all = new() { Width = 130, Increment = 100, FormatString = "0", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _applyCores;
    private readonly List<CoreRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;

    private sealed record CoreRow(CpuCore Core, TextBlock Clock, TextBlock Cap, TextBox Edit)
    {
        public bool Dirty { get; set; }
        public bool Filling { get; set; }
    }

    public CpuFrequencyWindow(PowerModeController power)
    {
        _power = power;
        Title = "OpenHW CPU Frequency";
        Width = 560;
        Height = 820;
        MinWidth = 460;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;

        var applyAll = TextButton("Apply to all cores", () => _ = ApplyAllAsync(_all.Value));
        var removeAll = TextButton("Remove all caps", () => _ = ApplyAllAsync(null));
        ToolTip.SetTip(removeAll, "Every core back to the hardware maximum.");
        _applyCores = TextButton("Apply core caps", () => _ = ApplyCoresAsync());
        _applyCores.IsEnabled = false;

        var top = new StackPanel
        {
            Margin = new Thickness(12, 10, 12, 6), Spacing = 8,
            Children =
            {
                _summary, _note,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = "All cores (MHz)", VerticalAlignment = VerticalAlignment.Center }, _all, applyAll, removeAll },
                },
            },
        };

        var list = new StackPanel();
        var alternate = false;
        foreach (var core in power.Cores)
        {
            var row = new CoreRow(core,
                new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) },
                new TextBlock { VerticalAlignment = VerticalAlignment.Center },
                new TextBox { Width = 90, Height = 24, MinHeight = 24, Padding = new Thickness(4, 2), FontSize = 12, Watermark = "MHz", HorizontalAlignment = HorizontalAlignment.Left });
            row.Edit.TextChanged += (_, _) =>
            {
                if (row.Filling) return;
                row.Dirty = true;
                _applyCores.IsEnabled = !_busy;
            };
            _rows.Add(row);
            var name = new TextBlock
            {
                Text = $"Core {core.Index}  ·  CCD{core.Ccd}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            ToolTip.SetTip(name, $"Threads {string.Join(", ", core.Threads)}");
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 28, Background = alternate ? Palette.RowB : Palette.RowA };
            alternate = !alternate;
            grid.Children.Add(name);
            grid.Children.Add(Cell(row.Clock, 1));
            grid.Children.Add(Cell(row.Cap, 2));
            grid.Children.Add(Cell(row.Edit, 3));
            list.Children.Add(grid);
        }

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Height = 24, Background = Palette.ColumnHeader };
        header.Children.Add(new TextBlock { Text = "Core", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        header.Children.Add(Cell(new TextBlock { Text = "Clock (MHz)", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) }, 1));
        header.Children.Add(Cell(new TextBlock { Text = "Cap (MHz)", VerticalAlignment = VerticalAlignment.Center }, 2));
        header.Children.Add(Cell(new TextBlock { Text = "Set cap (MHz)", VerticalAlignment = VerticalAlignment.Center }, 3));

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 8),
            Children = { _applyCores, _status },
        };

        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        Content = new DockPanel { Children = { top, header, bottom, new ScrollViewer { Content = list } } };

        _timer.Tick += (_, _) =>
        {
            _power.Refresh();
            RefreshFromController();
        };
        Opened += (_, _) => _timer.Start();
        RefreshFromController();
        FillEditors(force: true);
    }

    /// <summary>Redraws from the controller's last reading. UI thread.</summary>
    public void RefreshFromController()
    {
        var cpu = _power.Cpu;
        var hw = cpu.HardwareMaxKHz;
        var floor = cpu.FloorKHz;
        _summary.Text = cpu.Policies.Count == 0
            ? "No cpufreq policies found."
            : $"{_power.Cores.Count} cores · caps from {floor / 1000.0:N0} to {hw / 1000.0:N0} MHz · boost {(cpu.BoostOff ? "off" : "on")}" +
              (_power.ActiveProfile is { } profile ? $" · {PowerModeController.Label(profile)}" : "") +
              ". Caps last until reboot.";
        _note.IsVisible = cpu.BoostOff;
        _note.Text = "Power saver has boost off, so caps above the base clock aren't available. Switch to Balanced or " +
                     "Performance for the full range; caps set here are kept when you do.";

        if (hw > 0)
        {
            _all.Minimum = floor / 1000m;
            _all.Maximum = hw / 1000m;
            _all.Value ??= cpu.LowestMaxKHz / 1000m;
        }

        foreach (var row in _rows)
        {
            var policies = row.Core.Threads.Select(cpu.Get).OfType<CpuFrequencyPolicy>().ToArray();
            if (policies.Length == 0) continue;
            row.Clock.Text = policies.Max(p => p.CurrentKHz) is { } cur ? (cur / 1000.0).ToString("N0", CultureInfo.CurrentCulture) : "–";
            var max = policies.Min(p => p.MaxKHz);
            var mine = row.Core.Threads.Select(_power.CapOf).OfType<long>().ToArray();
            row.Cap.Text = (max / 1000.0).ToString("N0", CultureInfo.CurrentCulture) +
                           (max < policies.Max(p => p.HardwareMaxKHz) ? mine.Length > 0 ? "  (OpenHW)" : "  (other)" : "");
            row.Cap.Foreground = max < policies.Max(p => p.HardwareMaxKHz) && mine.Length == 0 ? Warn : Palette.Text;
        }
        FillEditors(force: false);
    }

    // Boxes show each core's current cap until the user types in them.
    private void FillEditors(bool force)
    {
        foreach (var row in _rows)
        {
            if (row.Dirty && !force || row.Edit.IsFocused) continue;
            var max = row.Core.Threads.Select(_power.Cpu.Get).OfType<CpuFrequencyPolicy>().Select(p => p.MaxKHz).DefaultIfEmpty(0).Min();
            row.Filling = true;
            row.Edit.Text = max > 0 ? (max / 1000).ToString(CultureInfo.InvariantCulture) : "";
            row.Filling = false;
            row.Dirty = false;
        }
        _applyCores.IsEnabled = !_busy && _rows.Any(r => r.Dirty);
    }

    private async Task ApplyAllAsync(decimal? mhz)
    {
        var khz = mhz is { } value ? (long)Math.Round(value * 1000m) : (long?)null;
        await ApplyAsync(_power.Cpu.Policies.ToDictionary(p => p.Id, _ => khz),
            khz is { } k ? $"All cores capped at {k / 1000.0:N0} MHz." : "All caps removed.");
        foreach (var row in _rows) row.Dirty = false;
        FillEditors(force: true);
    }

    private async Task ApplyCoresAsync()
    {
        var targets = new Dictionary<int, long?>();
        var edited = _rows.Where(r => r.Dirty).ToArray();
        foreach (var row in edited)
        {
            if (!decimal.TryParse(row.Edit.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var mhz) &&
                !decimal.TryParse(row.Edit.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out mhz))
            {
                Show($"Core {row.Core.Index}: enter a frequency in MHz. No caps changed.", Bad);
                return;
            }
            var khz = (long)Math.Round(mhz * 1000m);
            foreach (var thread in row.Core.Threads)
            {
                if (_power.Cpu.Get(thread) is not { } policy) continue;
                if (khz < policy.MinKHz || khz > policy.HardwareMaxKHz)
                {
                    Show($"Core {row.Core.Index}: {mhz:N0} MHz is outside {policy.MinKHz / 1000.0:N0}–{policy.HardwareMaxKHz / 1000.0:N0} MHz. No caps changed.", Bad);
                    return;
                }
                targets[thread] = khz >= policy.HardwareMaxKHz ? null : khz;
            }
        }
        if (await ApplyAsync(targets, $"Caps applied to {edited.Length} core{(edited.Length == 1 ? "" : "s")}."))
        {
            foreach (var row in edited) row.Dirty = false;
            FillEditors(force: true);
        }
    }

    private async Task<bool> ApplyAsync(IReadOnlyDictionary<int, long?> targets, string done)
    {
        if (_busy || targets.Count == 0) return false;
        _busy = true;
        _applyCores.IsEnabled = false;
        Show("Applying…", Palette.Footnote);
        try
        {
            var error = await _power.SetCapsAsync(targets);
            Show(error ?? done + " Verified by kernel readback.", error is null ? Good : Bad);
            return error is null;
        }
        finally
        {
            _busy = false;
            RefreshFromController();
        }
    }

    private void Show(string text, IBrush colour)
    {
        _status.Text = text;
        _status.Foreground = colour;
    }

    private static Control Cell(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }

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

    // Closing keeps the window (and its unsent edits) for next time; the timer only runs while it's shown.
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
