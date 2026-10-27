using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.Sensors;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Settings;

/// <summary>What the gear's dialogs change through the app, the same calls the menus, tray and other windows use.</summary>
public sealed record SensorSettingsActions(
    Action Applied,
    Func<string, TrayState> TrayStateOf,
    Action<string> AddToTray,
    Action<string> RemoveFromTray,
    Action ClearTray,
    Action<string, string> SetTrayColour);

/// <summary>
/// The footer gear: HWiNFO's "Sensor Settings" dialog, tab for tab (reference/settings_menu/General_Settings*.jpg).
/// Items OpenHW already has are connected to the same settings the older windows change — File → Settings, Tray
/// Icons, View → Layout Settings, the row menu's Rename / Customize values / Enable Logging, Alerts — and saved at
/// once, as those are; a change made in any of them shows here and the other way round. OK and Cancel close.
/// The other controls are still scaffolding: they toggle but nothing is read or saved. Main Settings opens
/// <see cref="MainSettingsWindow"/>.
/// </summary>
public sealed class SensorSettingsWindow : ClassicDialog
{
    public static readonly string[] Tabs = ["General", "Layout", "Customize", "Tray Icon", "OSD", "OSD (RTSS)", "Alerts", "Logitech LCD", "HWiNFO Gadget"];

    private readonly SensorHub _hub;
    private readonly AppSettings _settings;
    private readonly AlertStore _alerts;
    private readonly SensorSettingsActions _actions;
    private readonly SensorCustomizations _custom = SensorCustomizations.Current;
    private int _showing; // > 0 while the dialog shows saved values: control events then aren't user changes

    public ClassicEntryList LayoutList { get; } = new("Monitoring", 733.6, 120);
    public ClassicEntryList HiddenList { get; } = new("", 733.6, 120, showValueTitle: false);
    public ClassicEntryList CustomizeList { get; } = new("Logged", 745.6, 108);
    public ClassicEntryList TrayList { get; } = new("In Tray", 767.2, 84);
    public ClassicEntryList AlertList { get; } = new("Alert", 757.6, 97.6);

    /// <summary>Opens Main Settings over this window (the General tab's button).</summary>
    public Func<Task> ShowMainSettings { get; set; }

    public SensorSettingsWindow(SensorHub hub, AppSettings settings, AlertStore alerts, SensorSettingsActions actions)
        : base("OpenHW - Sensor Settings", new Size(934, 730), new Rect(8.8, 27.2, 917.6, 664), new Point(643.2, 700.8))
    {
        (_hub, _settings, _alerts, _actions) = (hub, settings, alerts, actions);
        ShowMainSettings = () => new MainSettingsWindow(settings, actions.Applied).ShowDialog(this);

        AddTab("General", General());
        AddTab("Layout", Layout());
        AddTab("Customize", Customize());
        AddTab("Tray Icon", TrayIcon());
        AddTab("OSD", null);
        AddTab("OSD (RTSS)", null);
        AddTab("Alerts", Alert());
        AddTab("Logitech LCD", null);
        AddTab("HWiNFO Gadget", null);
        ShowSaved();

        // Every place that changes these raises one of these events; the dialog shows the new state.
        void OnChange() { if (Dispatcher.UIThread.CheckAccess()) ShowSaved(); else Dispatcher.UIThread.Post(ShowSaved); }
        _custom.Changed += OnChange;
        _alerts.Changed += OnChange;
        _settings.Saved += OnChange;
        _hub.InventoryChanged += OnChange;
        Closed += (_, _) =>
        {
            _custom.Changed -= OnChange;
            _alerts.Changed -= OnChange;
            _settings.Saved -= OnChange;
            _hub.InventoryChanged -= OnChange;
        };
    }

    // For the tests: the connected controls, and their "Set"/"Rename" actions.
    internal TextBox PollField => _poll;
    internal void SetPollNow() => SetPoll();
    internal CheckBox MonitoringBox => _monitoring;
    internal CheckBox ShowBox => _show;
    internal TextBox CurrentLabel => _current;
    internal void RenameLabelNow() => RenameLabel();
    internal TextBox MultiplyField => _multiply;
    internal void SetValuesNow() => SetValues();
    internal CheckBox LoggedBox => _logged;
    internal CheckBox InTrayBox => _inTray;
    internal CheckBox AboveBox => _above;
    internal TextBox AboveField => _aboveValue;
    internal CheckBox EnableAlertingBox => _enable;

    private HardwareItem? Item(string? id) => id is null ? null : _hub.Inventory.FirstOrDefault(i => i.Id == id);
    private static string? SensorKey(ClassicEntryList list) => list.Selected is { IsGroup: false } e ? e.Key : null;

    private void Showing(Action show)
    {
        _showing++;
        try { show(); }
        finally { _showing--; }
    }

    private bool UserChange => _showing == 0;

    /// <summary>Redraws every tab from the saved settings (after any change, here or elsewhere).</summary>
    private void ShowSaved() => Showing(() =>
    {
        var cpu = _hub.CpuTemperatureId();
        var tray = _settings.TrayIcons.Select(t => t.SensorId == AppSettings.CpuTemperature ? cpu : t.SensorId).ToHashSet();
        var all = ClassicEntryList.FromHub(_hub, id => _custom.IsDisabled(id) ? "No" : "Yes",
            groupValueOf: g => g.Sensors.Any(s => !_custom.IsHidden(s.Id) && !_custom.IsDisabled(s.Id)) ? "Yes" : "No");
        LayoutList.Fill(Keep(all, id => !_custom.IsHidden(id)));
        HiddenList.Fill(Keep(all, _custom.IsHidden).Select(e => e with { Value = "" }).ToList());
        CustomizeList.Fill(ClassicEntryList.FromHub(_hub, id => _custom.IsLogged(id) ? "Yes" : "No"));
        TrayList.Fill(ClassicEntryList.FromHub(_hub, id => tray.Contains(id) ? "Yes" : "No"));
        AlertList.Fill(ClassicEntryList.FromHub(_hub, id => _alerts.For(id).Any() ? "Yes" : "No"));
        ShowGeneral();
        ShowLayout();
        ShowCustomize();
        ShowTray();
        ShowAlert();
    });

    // Keeps the sensors that pass, and a group heading (plus the blank line before it) only when it has any.
    private static List<ClassicEntryList.Entry> Keep(IReadOnlyList<ClassicEntryList.Entry> all, Func<string, bool> keep)
    {
        var result = new List<ClassicEntryList.Entry>();
        ClassicEntryList.Entry? heading = null;
        foreach (var entry in all)
        {
            if (entry.IsSpacer) continue;
            if (entry.IsGroup) { heading = entry; continue; }
            if (!keep(entry.Key)) continue;
            if (heading is not null)
            {
                if (result.Count > 0) result.Add(new ClassicEntryList.Entry("", false, "", ""));
                result.Add(heading);
                heading = null;
            }
            result.Add(entry);
        }
        return result;
    }

    /// <summary>Runs on Enter and when the field loses focus: HWiNFO's fields apply on OK, OpenHW's at once.</summary>
    private static void OnCommit(TextBox box, Action commit)
    {
        box.LostFocus += (_, _) => commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
    }

    private static string Number(double value) => value.ToString("0.##########", System.Globalization.CultureInfo.CurrentCulture);

    // ── General ───────────────────────────────────────────────────────────────────────────────────────────────

    private TextBox _poll = null!;

    // General_Settings.jpg — page frame at (13, 84).
    private ClassicPage General()
    {
        var p = new ClassicPage(13, 84);
        p.Group("Polling Period", 31, 101, 583, 233, 41);
        p.Label("Global:", 43, 134);
        _poll = p.Field("", 277, 120, 357, 148);
        ToolTip.SetTip(_poll, "OpenHW polls every " + string.Join(", ", AppSettings.PollChoices.Select(s => $"{s * 1000:0}")) +
                              " ms; other values go to the nearest. Same as File → Settings → Poll every.");
        p.Label("ms", 372, 134);
        p.Label("Disk S.M.A.R.T. every", 43, 166);
        p.Field("100", 304, 152, 357, 180);
        p.Label("cycles", 370, 166);
        p.Label("Emb. Controller every", 43, 197);
        p.Field("1", 304, 183, 357, 211);
        p.Label("cycles", 370, 197);
        p.Button("Set", 463, 131, 568, 199).Click += (_, _) => SetPoll();
        OnCommit(_poll, SetPoll);

        p.Group("Averaging Interval", 31, 248, 583, 373, 41);
        p.Label("Average last", 43, 278);
        p.Field("0", 291, 264, 357, 292);
        p.Label("samples", 368, 278);
        p.Button("Set", 463, 254, 568, 306);
        p.Label("0 = Whole runtime until user reset (default)", 47, 327);

        p.Group("Temperature units", 31, 406, 272, 485, 41);
        p.Radio("Celsius", "units", 43, 432, isChecked: true);
        p.Radio("Fahrenheit", "units", 43, 460);
        p.Label("Decimal Separator:", 297, 432);
        p.Field(".", 548, 414, 571, 442, alignRight: false);
        p.Label("Thousands Separator:", 297, 460);
        p.Field(",", 548, 446, 571, 474, alignRight: false);

        (string Text, bool On)[] options =
        [
            ("Minimize Graphs with Sensors Window", true), ("Snap Window Edges", true), ("Text Buttons", false),
            ("Log all values for Report (consumes memory)", false), ("Row Shading", true), ("Graph Shading", true),
            ("Highlight changing values", true), ("Always On Top", false), ("Show ToolTips", true),
            ("Accept negative temperatures (for sub-zero cooling)", false), ("Show all fans (including stopped or not present)", false),
        ];
        double[] rows = [529, 558, 586, 615, 643, 672, 700, 729, 757, 786, 814];
        for (var i = 0; i < options.Length; i++) p.Check(options[i].Text, 34, rows[i], options[i].On);
        p.Link("Check online for HWiNFO Addons and Extensions", 32, 853);

        p.Group("Show values", 597, 101, 866, 373, 608);
        string[] values = ["Temperatures", "Voltages", "Fans", "Currents", "Powers", "Clocks", "Usages", "Others"];
        double[] valueRows = [130, 159, 187, 216, 247, 275, 304, 332];
        for (var i = 0; i < values.Length; i++) p.Check(values[i], 626, valueRows[i], true);

        p.Group("Show columns", 871, 101, 1142, 264, 882);
        string[] columns = ["Current", "Minimum", "Maximum", "Average", "Profiling Time"];
        for (var i = 0; i < columns.Length; i++) p.Check(columns[i], 900, valueRows[i], i < 4);

        p.Check("Enable Hot Key to Reset Values", 638, 392);
        p.Label("Hot Key", 637, 422);
        p.Field("", 900, 408, 1108, 436, alignRight: false);
        p.Check("Enable Hot Key to Pause Monitoring", 638, 453);
        p.Label("Hot Key", 637, 484);
        p.Field("", 900, 470, 1108, 498, alignRight: false);

        p.Group("Sensor Logging", 610, 515, 1136, 818, 621);
        p.Check("Enable Toggling Hot Key", 638, 541);
        p.Label("Hot Key", 637, 577);
        p.Field("", 900, 563, 1108, 591, alignRight: false);
        p.Label("Output File Format", 637, 612);
        p.Combo(["CSV"], 0, 900, 598, 1108, 626);
        p.Label("Date/Time Format", 637, 645);
        p.Combo(["Default"], 0, 900, 632, 1108, 658);
        p.Label("CSV Item Separator:", 637, 681);
        p.Field(",", 901, 664, 925, 694, alignRight: false);
        p.Label("Logging Time Limit", 637, 715);
        p.Field("", 901, 700, 991, 730);
        p.Label("seconds", 1005, 715);
        p.Check("Write Direct To Disk (No Buffering)", 638, 748);
        p.Label("Notes:", 637, 786);
        p.Label("Beginning:", 746, 786);
        p.Button("Set Note", 832, 772, 943, 802);
        p.LabelRight("End:", 995, 786);
        p.Button("Set NOTE", 1000, 772, 1110, 802);

        p.Button("Main Settings", 620, 831, 779, 886).Click += async (_, _) => await ShowMainSettings();
        p.Button("Change Font", 796, 831, 955, 886);
        p.Button("Reset Graphs", 971, 831, 1130, 886);
        return p;
    }

    private void ShowGeneral() => _poll.Text = $"{_settings.PollSeconds * 1000:0}";

    // HWiNFO takes any number of ms; OpenHW offers a few intervals (File → Settings), so this picks the nearest.
    private void SetPoll()
    {
        if (!UserChange) return;
        if (!int.TryParse(_poll.Text?.Trim(), out var ms) || ms <= 0) { ShowGeneral(); return; }
        var seconds = AppSettings.PollChoices.MinBy(s => Math.Abs(s * 1000 - ms));
        if (seconds != _settings.PollSeconds)
        {
            _settings.PollSeconds = seconds;
            _settings.Save();
            _actions.Applied();
        }
        Showing(ShowGeneral); // shows what OpenHW uses, e.g. 2500 → 2000
    }

    // ── Layout ────────────────────────────────────────────────────────────────────────────────────────────────

    private ClassicPage _layoutPage = null!;
    private CheckBox _monitoring = null!, _show = null!;
    private Button[] _arrows = [];

    // General_Settings_Layout.jpg — page frame at (15, 83).
    private ClassicPage Layout()
    {
        var p = _layoutPage = new ClassicPage(15, 83);
        p.Place(LayoutList, 34, 103, 1138, 482);
        _monitoring = p.Check("Monitoring", 46, 509, needsSelection: true);
        _show = p.Check("Show", 46, 538, needsSelection: true);
        p.Check("Fixed order", 318, 509, isChecked: true);
        p.Button("Restore Original Order", 316, 529, 597, 574).Click += (_, _) => _custom.RestoreOriginalOrder();
        p.Group("Order", 635, 499, 810, 589, 646);
        _arrows =
        [
            p.Arrow("↑", "Move up", 663, 523, 692, 549),
            p.Arrow("⤒", "Move to the top", 699, 523, 728, 549),
            p.Arrow("↓", "Move down", 663, 551, 692, 577),
            p.Arrow("⤓", "Move to the bottom", 699, 551, 728, 577),
        ];
        int[] steps = [-1, int.MinValue, 1, int.MaxValue];
        for (var i = 0; i < _arrows.Length; i++)
        {
            var by = steps[i];
            _arrows[i].Click += (_, _) => MoveSelected(by);
        }
        p.Button("__", 762, 535, 800, 561);
        p.Label("Hidden items:", 45, 604);
        p.Place(HiddenList, 34, 630, 1138, 869);

        // One selection across both lists, as View → Layout Settings.
        LayoutList.SelectionChanged += e => { if (e is not null) HiddenList.Selected = null; Showing(ShowLayout); };
        HiddenList.SelectionChanged += e => { if (e is not null) LayoutList.Selected = null; Showing(ShowLayout); };
        _monitoring.IsCheckedChanged += (_, _) =>
        {
            if (UserChange && LayoutSensor is { } id) _custom.SetDisabled(id, _monitoring.IsChecked != true);
        };
        _show.IsCheckedChanged += (_, _) =>
        {
            if (!UserChange || LayoutSensor is not { } id) return;
            var hide = _show.IsChecked != true;
            _custom.SetHidden(id, hide);
            // The sensor moved to the other list: keep it selected there.
            var list = hide ? HiddenList : LayoutList;
            list.Selected = list.Entries.FirstOrDefault(e => !e.IsGroup && e.Key == id);
        };
        return p;
    }

    private string? LayoutSensor => SensorKey(LayoutList) ?? SensorKey(HiddenList);

    internal void MoveSelected(int by)
    {
        if (LayoutList.Selected is not { IsSpacer: false } e) return;
        var groups = SensorPresentation.Build(_hub.Inventory, _hub.Children, _hub.Machine);
        LayoutWindow.MoveShown(_custom, groups, LayoutList.Shown, e.Key, e.IsGroup, e.GroupKey, by);
    }

    private void ShowLayout()
    {
        var id = LayoutSensor;
        EnableSelectionControls(_layoutPage, id is not null);
        _monitoring.IsChecked = id is not null && !_custom.IsDisabled(id);
        _show.IsChecked = id is not null && !_custom.IsHidden(id);
        foreach (var arrow in _arrows) arrow.IsEnabled = LayoutList.Selected is not null;
    }

    // ── Customize ─────────────────────────────────────────────────────────────────────────────────────────────

    private ClassicPage _customizePage = null!;
    private TextBox _original = null!, _current = null!, _unitOriginal = null!, _unitCurrent = null!, _multiply = null!, _add = null!;
    private Button _renameUnit = null!, _setValues = null!;
    private ComboBox _decimals = null!;
    private CheckBox _logged = null!;

    // General_Settings_Customize.jpg — page frame at (11, 81).
    private ClassicPage Customize()
    {
        var p = _customizePage = new ClassicPage(11, 81);
        p.Place(CustomizeList, 30, 101, 1135, 480);
        p.Group("Label", 29, 503, 716, 597, 39);
        p.Label("Original", 41, 533);
        _original = p.Field("", 151, 517, 512, 546, alignRight: false);
        _original.IsReadOnly = true;
        p.Label("Current", 41, 571);
        _current = p.Field("", 151, 556, 512, 586, alignRight: false);
        p.Button("Rename", 536, 556, 696, 588).Click += (_, _) => RenameLabel();
        OnCommit(_current, RenameLabel);

        p.Group("Unit", 743, 503, 1026, 597, 753);
        _unitOriginal = p.Field("", 757, 520, 837, 549, alignRight: false);
        _unitOriginal.IsReadOnly = true;
        _unitCurrent = p.Field("", 757, 557, 837, 587, alignRight: false);
        _renameUnit = p.Button("Rename", 850, 556, 1011, 588);
        _renameUnit.Click += (_, _) => RenameUnit();

        p.Group("Font", 29, 617, 405, 706, 39);
        p.Label("Color:", 41, 664);
        p.Swatch(150, 643, 192, 684, null);
        p.Check("Bold", 231, 643, needsSelection: true);
        p.Check("Italic", 231, 678, needsSelection: true);

        p.Group("Red color (critical) if:", 435, 617, 716, 706, 445);
        p.Check("value <", 446, 643, needsSelection: true);
        p.Field("", 608, 628, 705, 658, needsSelection: true);
        p.Check("value >", 446, 678, needsSelection: true);
        p.Field("", 608, 664, 705, 694, needsSelection: true);

        p.Group("Invalid if:", 743, 617, 1026, 706, 753);
        p.Check("value <", 758, 643, needsSelection: true);
        p.Field("", 919, 628, 1015, 658, needsSelection: true);
        p.Check("value >", 758, 678, needsSelection: true);
        p.Field("", 919, 664, 1015, 694, needsSelection: true);

        p.Group("Customize values", 29, 726, 405, 827, 39);
        p.Label("Multiply", 41, 764);
        _multiply = p.Field("1", 214, 746, 299, 776, needsSelection: true);
        p.Label("Add (+)", 41, 799);
        _add = p.Field("0", 214, 784, 299, 814, needsSelection: true);
        _setValues = p.Button("Set", 308, 757, 397, 804);
        _setValues.Click += (_, _) => SetValues();
        ToolTip.SetTip(_setValues, "Shown value = reading × Multiply + Add, everywhere the sensor appears (as the row's Customize values…).");

        p.Label("Thousands Separator:", 446, 735);
        p.Field("", 685, 720, 710, 749, alignRight: false);
        p.Label("Decimal Digits:", 446, 785);
        _decimals = p.Combo(["Auto", .. Enumerable.Range(0, ValueCustomization.MaxDecimals + 1).Select(d => d.ToString())], -1, 651, 769, 707, 795);
        _decimals.SelectionChanged += (_, _) =>
        {
            if (UserChange && SensorKey(CustomizeList) is { } id && _decimals.SelectedIndex >= 0)
                ChangeValues(id, v => v with { Decimals = _decimals.SelectedIndex == 0 ? null : _decimals.SelectedIndex - 1 });
        };
        _logged = p.Check("Logged", 758, 757, needsSelection: true);
        _logged.IsCheckedChanged += (_, _) =>
        {
            if (UserChange && SensorKey(CustomizeList) is { } id) _custom.SetLogged(id, _logged.IsChecked == true);
        };
        CustomizeList.SelectionChanged += _ => Showing(ShowCustomize);
        return p;
    }

    private void ShowCustomize()
    {
        var item = Item(SensorKey(CustomizeList));
        EnableSelectionControls(_customizePage, item is not null);
        var values = item is null ? new ValueCustomization() : _custom.ValuesOf(item.Id) ?? new ValueCustomization();
        var custom = item is not null && SensorPresentation.CanCustomize(item);
        _original.Text = item is null ? "" : SensorPresentation.BuiltInName(item);
        _current.Text = item is null ? "" : SensorPresentation.DisplayName(item);
        _unitOriginal.Text = item is null ? "" : SensorPresentation.BuiltInUnit(item);
        _unitCurrent.Text = item is null ? "" : values.Unit ?? SensorPresentation.BuiltInUnit(item);
        _multiply.Text = Number(values.Multiply);
        _add.Text = Number(values.Offset);
        _multiply.ClearValue(BorderBrushProperty);
        _add.ClearValue(BorderBrushProperty);
        _multiply.IsEnabled = _add.IsEnabled = _setValues.IsEnabled = _unitCurrent.IsEnabled = _renameUnit.IsEnabled = _decimals.IsEnabled = item is null || custom;
        _decimals.SelectedIndex = item is null ? -1 : (values.Decimals ?? -1) + 1;
        _logged.IsChecked = item is not null && _custom.IsLogged(item.Id);
    }

    // Same rule as the row's Rename: OpenHW's own name (or nothing) goes back to it.
    private void RenameLabel()
    {
        if (!UserChange || Item(SensorKey(CustomizeList)) is not { } item) return;
        var name = _current.Text?.Trim();
        var next = string.IsNullOrEmpty(name) || name == SensorPresentation.BuiltInName(item) ? null : name;
        if (next != _custom.NameOf(item.Id)) _custom.Rename(item.Id, next);
    }

    private void RenameUnit()
    {
        if (!UserChange || Item(SensorKey(CustomizeList)) is not { } item || !SensorPresentation.CanCustomize(item)) return;
        var unit = _unitCurrent.Text?.Trim() ?? "";
        ChangeValues(item.Id, v => v with { Unit = unit == SensorPresentation.BuiltInUnit(item) ? null : unit });
    }

    private void SetValues()
    {
        if (!UserChange || Item(SensorKey(CustomizeList)) is not { } item || !SensorPresentation.CanCustomize(item)) return;
        var multiply = CustomizeDialog.ParseNumber(_multiply.Text);
        var offset = CustomizeDialog.ParseNumber(_add.Text);
        if (multiply is null or 0) _multiply.BorderBrush = Brushes.IndianRed;
        if (offset is null) _add.BorderBrush = Brushes.IndianRed;
        if (multiply is null or 0 || offset is null) return;
        ChangeValues(item.Id, v => v with { Multiply = multiply.Value, Offset = offset.Value });
    }

    private void ChangeValues(string id, Func<ValueCustomization, ValueCustomization> change)
    {
        var values = change(_custom.ValuesOf(id) ?? new ValueCustomization()).Normalised();
        if (values == (_custom.ValuesOf(id) ?? new ValueCustomization())) return;
        _custom.Customize(id, values.IsDefault ? null : values);
    }

    // ── Tray Icon ─────────────────────────────────────────────────────────────────────────────────────────────

    private ClassicPage _trayPage = null!;
    private CheckBox _inTray = null!;
    private Button _background = null!;

    // General_Settings_Tray_Icon.jpg — page frame at (12, 81).
    private ClassicPage TrayIcon()
    {
        var p = _trayPage = new ClassicPage(12, 81);
        p.Place(TrayList, 31, 101, 1135, 480);
        _inTray = p.Check("Show in Tray", 43, 507, needsSelection: true);
        ToolTip.SetShowOnDisabled(_inTray, true);
        _inTray.IsCheckedChanged += (_, _) =>
        {
            if (!UserChange || SensorKey(TrayList) is not { } id) return;
            if (_inTray.IsChecked == true) _actions.AddToTray(id);
            else _actions.RemoveFromTray(id);
        };
        p.Group("Color", 43, 536, 595, 653, 53);
        p.Label("Background:", 56, 564);
        _background = p.Swatch(200, 543, 243, 586, ClassicColors.Swatch, needsSelection: true);
        ToolTip.SetTip(_background, "The tile's color, as in File → Tray Icons… and the icon's Color menu.");
        _background.Click += async (_, _) =>
        {
            if (SensorKey(TrayList) is not { } id || TrayColour(id) is not { } current) return;
            if (await ColorDialog.PickAsync(this, current) is { } hex) _actions.SetTrayColour(id, hex);
        };
        p.Check("Transparent", 256, 552, needsSelection: true);
        p.Check("Depends on alert threshold", 256, 576, needsSelection: true);
        p.Label("Text:", 56, 619);
        p.Swatch(200, 598, 243, 640, ClassicColors.Swatch, needsSelection: true);
        p.Check("Transparent", 256, 617, needsSelection: true);

        p.Label("Font:", 622, 531);
        p.Button("", 691, 514, 918, 543);
        p.Check("Font Auto-Scale", 692, 579, needsSelection: true);
        p.Check("Anti-aliased", 692, 607, needsSelection: true);

        p.Label("Divide value by:", 42, 693);
        p.Field("1", 301, 675, 395, 704, needsSelection: true);
        p.Button("Set", 410, 675, 553, 704);
        p.Label("Truncate to:", 42, 740);
        p.Combo(["1", "2", "3", "4"], -1, 301, 726, 408, 751);
        p.Label("digits", 423, 740);
        p.Label("Show value type:", 42, 785);
        p.Combo(["Current", "Minimum", "Maximum", "Average"], -1, 301, 769, 462, 795);

        var addAll = p.Button("Add All Items To Tray", 691, 669, 949, 723);
        ToolTip.SetTip(addAll, $"Adds sensors in list order until the tray has {AppSettings.MaxTrayIcons} icons, the most OpenHW shows.");
        addAll.Click += (_, _) =>
        {
            foreach (var entry in TrayList.Entries.Where(e => !e.IsGroup && !e.IsSpacer))
                if (_actions.TrayStateOf(entry.Key).CanAdd) _actions.AddToTray(entry.Key);
        };
        p.Button("Remove All Items From Tray", 691, 741, 949, 795).Click += (_, _) => _actions.ClearTray();
        TrayList.SelectionChanged += _ => Showing(ShowTray);
        return p;
    }

    private string? TrayColour(string id)
    {
        var cpu = _hub.CpuTemperatureId();
        return _settings.TrayIcons.FirstOrDefault(t => (t.SensorId == AppSettings.CpuTemperature ? cpu : t.SensorId) == id)?.Colour;
    }

    private void ShowTray()
    {
        var id = SensorKey(TrayList);
        EnableSelectionControls(_trayPage, id is not null);
        var state = id is null ? null : _actions.TrayStateOf(id);
        _inTray.IsChecked = state?.InTray == true;
        _inTray.IsEnabled = state is { InTray: true } or { CanAdd: true };
        ToolTip.SetTip(_inTray, state?.WhyNot);
        var colour = id is null ? null : TrayColour(id);
        _background.IsEnabled = colour is not null;
        ((Border)_background.Content!).Background = colour is not null ? new SolidColorBrush(Color.Parse(colour)) : ClassicColors.Swatch;
    }

    // ── Alerts ────────────────────────────────────────────────────────────────────────────────────────────────

    private ClassicPage _alertPage = null!;
    private CheckBox _enable = null!, _below = null!, _above = null!, _warning = null!;
    private TextBox _belowValue = null!, _aboveValue = null!, _samples = null!;

    // General_Settings_Alerts.jpg — page frame at (9, 78).
    private ClassicPage Alert()
    {
        var p = _alertPage = new ClassicPage(9, 78);
        p.Place(AlertList, 28, 98, 1133, 477);
        _enable = p.Check("Enable Alerting", 40, 509, needsSelection: true);
        _below = p.Check("If value  <", 352, 509, needsSelection: true);
        _belowValue = p.Field("0", 566, 494, 644, 524, needsSelection: true);
        _above = p.Check("If value >=", 352, 545, needsSelection: true);
        ToolTip.SetTip(_above, "OpenHW's \"above\" alert: it fires when the value is more than the threshold.");
        _aboveValue = p.Field("0", 566, 530, 644, 560, needsSelection: true);

        p.Check("Play Sound", 40, 597, needsSelection: true);
        p.Button("", 348, 582, 552, 609, needsSelection: true);
        p.Check("Run a Program", 40, 628, needsSelection: true);
        p.Button("", 348, 613, 552, 640, needsSelection: true);
        p.Label("Arguments:", 568, 628, needsSelection: true);
        p.Field("", 662, 612, 1131, 641, alignRight: false, needsSelection: true);
        // OpenHW always tells you with a desktop notification; there's nothing to turn off yet.
        _warning = p.Check("Display Warning Window", 40, 661, isChecked: true);
        _warning.IsEnabled = false;
        ToolTip.SetShowOnDisabled(_warning, true);
        ToolTip.SetTip(_warning, "OpenHW always shows a desktop notification when an alert fires.");
        p.Label("Note:", 566, 661);
        p.Label("%v program argument = actual sensor value", 566, 680);
        p.Label("%s program argument = actual sensor name", 566, 699);
        p.Check("Change Item Color", 40, 690, needsSelection: true);
        p.Check("Log alerts into:", 40, 718, needsSelection: true);
        p.Label("\"HWiNFO_AlertLog.txt\"", 341, 718);

        p.Label("Notification distance:", 39, 758, needsSelection: true);
        p.Field("0", 338, 742, 427, 772, needsSelection: true);
        p.Label("seconds", 445, 758, needsSelection: true);
        p.Check("Trigger only once", 567, 758, needsSelection: true);
        p.Label("Number of samples to trigger alarm:", 39, 790, needsSelection: true);
        _samples = p.Field("1", 338, 775, 427, 805, needsSelection: true);
        ToolTip.SetTip(_samples, "OpenHW waits samples × the poll interval before an alert fires (the rule's \"for\" time).");

        _enable.IsCheckedChanged += (_, _) =>
        {
            if (!UserChange || SensorKey(AlertList) is not { } id) return;
            var on = _enable.IsChecked == true;
            var rules = _alerts.For(id).ToList();
            if (rules.Count > 0) _alerts.Update(() => rules.ForEach(r => r.Enabled = on));
            else Showing(ShowAlert); // nothing to turn on until a condition is ticked
        };
        WireCondition(_below, _belowValue, AlertCondition.Below);
        WireCondition(_above, _aboveValue, AlertCondition.Above);
        OnCommit(_samples, () =>
        {
            if (!UserChange || SensorKey(AlertList) is not { } id) return;
            var rules = _alerts.For(id).ToList();
            if (!int.TryParse(_samples.Text?.Trim(), out var n) || n < 1) { Showing(ShowAlert); return; }
            var seconds = ForSeconds(n);
            if (rules.Count > 0 && rules.Any(r => r.ForSeconds != seconds)) _alerts.Update(() => rules.ForEach(r => r.ForSeconds = seconds));
        });
        AlertList.SelectionChanged += _ => Showing(ShowAlert);
        return p;
    }

    // HWiNFO counts samples (1 = at once); OpenHW's rules count seconds.
    private int ForSeconds(int samples) => (int)Math.Round((samples - 1) * _settings.PollSeconds);
    private int Samples(int seconds) => (int)Math.Round(seconds / _settings.PollSeconds) + 1;

    private AlertRule? RuleOf(string id, AlertCondition condition) => _alerts.For(id).FirstOrDefault(r => r.Condition == condition);

    // A ticked condition is an OpenHW alert rule for this sensor; unticking removes it.
    private void WireCondition(CheckBox check, TextBox value, AlertCondition condition)
    {
        check.IsCheckedChanged += (_, _) =>
        {
            if (!UserChange || SensorKey(AlertList) is not { } id) return;
            var rule = RuleOf(id, condition);
            if (check.IsChecked == true && rule is null)
            {
                var others = _alerts.For(id).FirstOrDefault();
                _alerts.Add(new AlertRule
                {
                    SensorId = id, Condition = condition, Threshold = CustomizeDialog.ParseNumber(value.Text) ?? 0,
                    ForSeconds = others?.ForSeconds ?? ForSeconds(int.TryParse(_samples.Text, out var n) && n > 0 ? n : 1),
                });
            }
            else if (check.IsChecked != true && rule is not null) _alerts.Remove(rule);
        };
        OnCommit(value, () =>
        {
            if (!UserChange || SensorKey(AlertList) is not { } id || RuleOf(id, condition) is not { } rule) return;
            if (CustomizeDialog.ParseNumber(value.Text) is not { } threshold) { Showing(ShowAlert); return; }
            if (threshold != rule.Threshold) _alerts.Update(() => rule.Threshold = threshold);
        });
    }

    private void ShowAlert()
    {
        var id = SensorKey(AlertList);
        EnableSelectionControls(_alertPage, id is not null);
        var rules = id is null ? [] : _alerts.For(id).ToList();
        var below = rules.FirstOrDefault(r => r.Condition == AlertCondition.Below);
        var above = rules.FirstOrDefault(r => r.Condition == AlertCondition.Above);
        _enable.IsChecked = rules.Any(r => r.Enabled);
        _below.IsChecked = below is not null;
        _above.IsChecked = above is not null;
        _belowValue.Text = below is null ? "0" : AlertMonitor.ThresholdText(below.Threshold);
        _aboveValue.Text = above is null ? "0" : AlertMonitor.ThresholdText(above.Threshold);
        _samples.Text = $"{(rules.Count > 0 ? Samples(rules[0].ForSeconds) : 1)}";
    }
}
