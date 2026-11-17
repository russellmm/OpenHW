using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenHW.App.Alerts;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.HardwareCheck;

/// <summary>Everything the window shows, built fresh each time it opens or after a change.</summary>
public sealed record HardwareCheckModel(HardwareSnapshot? Before, HardwareSnapshot Now, List<HardwareChange> Changes,
    List<StaleReference> Stale, List<CoverageNote> Notes)
{
    /// <summary>Worth opening by itself at start: the hardware changed, or saved choices point at sensors that are gone.</summary>
    public bool NeedsAttention => Changes.Count > 0 || Stale.Count > 0 || Notes.Any(n => n.Warning);

    /// <summary>
    /// A fingerprint of what needs attention (changes, stale settings, warnings). The check opens by itself at start only
    /// when this differs from the one the user last closed the window on — something new — not for what they've seen.
    /// </summary>
    public string Signature
    {
        get
        {
            var parts = Changes.Select(c => "c " + c.Area + c.Text).Concat(Stale.Select(s => "s " + s.Id))
                .Concat(Notes.Where(n => n.Warning).Select(n => "w " + n.Area + n.Text)).Order(StringComparer.Ordinal);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..16];
        }
    }

    public static HardwareCheckModel Build(SensorHub hub, HardwareSnapshot? before, Settings.AppSettings settings,
        Alerts.AlertStore alerts, bool gpuLent, BiosSettingsReply? bios = null, BmcAccessReply? access = null)
    {
        var inventory = hub.Inventory;
        var bmc = hub.Bmc.Controller is { } c ? c.Controller : "";
        var now = HardwareSnapshot.Take(inventory, hub.Machine, bmc, SensorPresentation.BuiltInName, bios);
        // The BMC didn't answer this time: keep the saved BIOS settings rather than forget them.
        if (bios is null && before is not null) (now.BiosSettings, now.BiosLabels) = (before.BiosSettings, before.BiosLabels);
        var changes = before is null ? [] : HardwareDiff.Compare(before, now, gpuLent);
        var custom = SensorCustomizations.Current;
        var stale = StaleReferences.Find(SavedChoices(settings, alerts, custom), inventory, before, now,
            SensorPresentation.BuiltInName, custom.Names, gpuLent);
        var notes = Coverage.Check(now, inventory, hub.Bmc.Available, Coverage.Live, before, access);
        return new HardwareCheckModel(before, now, changes, stale, notes);
    }

    /// <summary>Every saved choice that names a sensor: tray icons, alert rules, renames, logging, hidden, monitoring off, custom values.</summary>
    public static IEnumerable<StaleReferences.Saved> SavedChoices(Settings.AppSettings settings, Alerts.AlertStore alerts, SensorCustomizations custom)
    {
        foreach (var icon in settings.TrayIcons) yield return new(icon.SensorId, "Tray icon");
        foreach (var rule in alerts.Snapshot())
            yield return new(rule.SensorId, $"Alert {(rule.Condition == AlertCondition.Above ? "above" : "below")} {AlertMonitor.ThresholdText(rule.Threshold)}");
        foreach (var (id, name) in custom.Names) yield return new(id, $"Renamed \"{name}\"");
        foreach (var id in custom.Logged) yield return new(id, "Logged");
        foreach (var id in custom.Hidden) yield return new(id, "Hidden");
        foreach (var id in custom.Disabled) yield return new(id, "Monitoring off");
        foreach (var id in custom.Values.Keys) yield return new(id, "Custom value");
    }

    /// <summary>The same check as text (openhw --hardware-check).</summary>
    public string Report()
    {
        var w = new System.Text.StringBuilder();
        w.AppendLine($"{Now.BoardVendor} {Now.Board} · BIOS {Now.Bios} · {Now.Cpu} · {Now.MemoryGiB} GiB" + (Now.Bmc.Length > 0 ? $" · BMC {Now.Bmc}" : ""));
        w.AppendLine(Before is null ? "No saved hardware snapshot yet (first run of 0.21.0 or later)." : $"Changes since {Before.Taken:yyyy-MM-dd HH:mm}: {(Changes.Count == 0 ? "none" : "")}");
        foreach (var c in Changes) w.AppendLine($"  {c.Area}: {c.Text}");
        w.AppendLine($"Saved settings for sensors that aren't here: {(Stale.Count == 0 ? "none" : "")}");
        foreach (var s in Stale)
            w.AppendLine($"  {s.OldName} [{s.Id}] — {string.Join(", ", s.Uses)} → {s.Suggestion ?? "no suggestion"}");
        w.AppendLine($"What OpenHW can't fully read here: {(Notes.Count == 0 ? "nothing known" : "")}");
        foreach (var n in Notes)
        {
            w.AppendLine($"  {(n.Warning ? "! " : "")}{n.Area}: {n.Text}");
            if (n.Fix is { } fix) w.AppendLine($"      {fix}");
        }
        return w.ToString();
    }
}

/// <summary>What the window can ask the app to do.</summary>
public interface IHardwareCheckActions
{
    HardwareCheckModel Build();
    IReadOnlyList<HardwareItem> Inventory { get; }
    SensorPresentation.MachineInfo Machine { get; }
    void Move(StaleReference stale, string newId);
    void Remove(StaleReference stale);
    void Accept();
    void ShowGpuLending();
    void ShowPowerLimits();
}

/// <summary>
/// File → Hardware Check…: what changed since OpenHW last saw this machine, saved choices (tray icons, alerts,
/// logging, names) that point at sensors no longer present — with the sensor each most likely became — and what OpenHW
/// can't fully read here. Opens by itself at start when something needs attention.
/// </summary>
public sealed class HardwareCheckWindow : Window
{
    private readonly IHardwareCheckActions _actions;
    private readonly StackPanel _body = new() { Spacing = 6, Margin = new Thickness(14, 10) };

    public HardwareCheckWindow(IHardwareCheckActions actions)
    {
        _actions = actions;
        Title = "Hardware Check";
        Width = 860;
        Height = 680;
        Background = Palette.Window;
        Foreground = Palette.Text;
        FontSize = 12.5;
        Content = new ScrollViewer { Content = _body };
        Refresh();
    }

    public void Refresh()
    {
        var model = _actions.Build();
        _titles = SensorPresentation.Build(_actions.Inventory, new Dictionary<string, IReadOnlyList<string>>(), _actions.Machine)
            .SelectMany(g => g.Sensors.Select(v => (v.Id, g.Title))).ToDictionary(x => x.Id, x => x.Title, StringComparer.Ordinal);
        _body.Children.Clear();

        // ── What changed ──
        Heading(model.Before is null ? "This machine" : $"Changes since {model.Before.Taken:yyyy-MM-dd HH:mm}");
        Line($"{model.Now.BoardVendor} {model.Now.Board} · BIOS {model.Now.Bios} · {model.Now.Cpu} · {model.Now.MemoryGiB} GiB" +
             (model.Now.Bmc.Length > 0 ? $" · BMC {model.Now.Bmc}" : ""), Palette.Footnote);
        if (model.Before is null)
            Line("OpenHW hasn't saved this machine's hardware before (new in 0.21.0), so it can't list what changed. " +
                 "\"Save as current hardware\" keeps it for next time.");
        else if (model.Changes.Count == 0) Line("No hardware changes.");
        foreach (var c in model.Changes) Line($"{c.Area}: {c.Text}");

        // ── Saved choices pointing at missing sensors ──
        Heading("Saved settings for sensors that aren't here");
        if (model.Stale.Count == 0) Line("None — every tray icon, alert, logged sensor and name points at a present sensor.");
        var withSuggestion = model.Stale.Where(s => s.Suggestion is not null).ToList();
        if (withSuggestion.Count > 1)
            _body.Children.Add(AlertUi.Button($"Move all {withSuggestion.Count} to the suggested sensors", () =>
            {
                foreach (var s in withSuggestion) _actions.Move(s, s.Suggestion!);
                Refresh();
            }));
        foreach (var stale in model.Stale) _body.Children.Add(StaleRow(stale));

        // ── Coverage ──
        Heading("What OpenHW can't fully read here");
        if (model.Notes.Count == 0) Line("Nothing known to be missing.");
        foreach (var note in model.Notes)
        {
            var text = new TextBlock { Text = $"{note.Area}: {note.Text}", TextWrapping = TextWrapping.Wrap, Foreground = note.Warning ? AlertUi.Alarm : Palette.Text };
            var block = new StackPanel { Spacing = 2, Children = { text } };
            if (note.Fix is { } fix)
                block.Children.Add(new SelectableTextBlock { Text = fix, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Footnote, Margin = new Thickness(14, 0, 0, 0) });
            if (note.Action is { } action)
                block.Children.Add(AlertUi.Row(new Control[] { new Border { Width = 6 },
                    AlertUi.Button(action, action == Coverage.OpenPowerLimits ? _actions.ShowPowerLimits : _actions.ShowGpuLending) }));
            _body.Children.Add(block);
        }

        // ── Done ──
        var save = AlertUi.Button("Save as current hardware", () => { _actions.Accept(); Refresh(); });
        ToolTip.SetTip(save, "Remember this machine's hardware, so the next start reports only new changes.");
        _body.Children.Add(new Border { Height = 8 });
        _body.Children.Add(AlertUi.Row(save, AlertUi.Button("Refresh", Refresh), AlertUi.Button("Close", Close)));
    }

    private Control StaleRow(StaleReference stale)
    {
        var inventory = _actions.Inventory;
        var candidates = inventory.Where(i => stale.Kind is null || i.Kind == stale.Kind)
            .OrderBy(i => i.Id == stale.Suggestion ? 0 : 1)
            .ThenBy(i => SensorPresentation.DisplayName(i), StringComparer.OrdinalIgnoreCase).ToList();
        var pick = new ComboBox { Width = 440, MaxDropDownHeight = 420 };
        foreach (var item in candidates)
            pick.Items.Add(new ComboBoxItem
            {
                Content = (item.Id == stale.Suggestion ? "★ " : "") + $"{SensorPresentation.DisplayName(item)}  —  {GroupOf(item)}",
                Tag = item.Id,
            });
        if (stale.Suggestion is not null) pick.SelectedIndex = 0;
        var move = AlertUi.Button("Move", () =>
        {
            if (pick.SelectedItem is ComboBoxItem { Tag: string id }) { _actions.Move(stale, id); Refresh(); }
        });
        var remove = AlertUi.Button("Remove", () => { _actions.Remove(stale); Refresh(); });
        ToolTip.SetTip(remove, "Drop these saved settings (tray icon, alert rules, logging, name) for the missing sensor.");

        var name = new TextBlock { Text = stale.OldName, FontWeight = FontWeight.SemiBold };
        var uses = new TextBlock { Text = string.Join(" · ", stale.Uses), Foreground = Palette.Footnote };
        var id = new SelectableTextBlock { Text = stale.Id, Foreground = Palette.Footnote, FontSize = 11 };
        return new Border
        {
            BorderBrush = FooterIcons.ButtonEdge, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    AlertUi.Row(name, uses), id,
                    AlertUi.Row(new TextBlock { Text = stale.Suggestion is null ? "Move to:" : "Most likely now:", VerticalAlignment = VerticalAlignment.Center }, pick, move, remove),
                },
            },
        };
    }

    private Dictionary<string, string> _titles = [];

    private string GroupOf(HardwareItem item) => _titles.GetValueOrDefault(item.Id, item.Group);

    private void Heading(string text) =>
        _body.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 14, Margin = new Thickness(0, 10, 0, 2) });

    private void Line(string text, IBrush? brush = null) =>
        _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? Palette.Text });
}
