using Avalonia;
using OpenHW.App.Alerts;
using OpenHW.App.Sensors;

namespace OpenHW.App.Settings;

/// <summary>
/// The footer gear: HWiNFO's "Sensor Settings" dialog, tab for tab (reference/settings_menu/General_Settings*.jpg).
/// <b>Scaffolding</b>: the controls toggle and type but nothing is read from or saved to OpenHW's settings yet; OK and
/// Cancel just close. The entry lists show OpenHW's real sensors with their current Monitoring / Logged / In Tray /
/// Alert state, read-only. Main Settings opens <see cref="MainSettingsWindow"/>. Hooking each item up to the real
/// option (or graying it where Linux has no such thing) comes next, item by item with the owner.
/// </summary>
public sealed class SensorSettingsWindow : ClassicDialog
{
    public static readonly string[] Tabs = ["General", "Layout", "Customize", "Tray Icon", "OSD", "OSD (RTSS)", "Alerts", "Logitech LCD", "HWiNFO Gadget"];

    public ClassicEntryList LayoutList { get; }
    public ClassicEntryList CustomizeList { get; }
    public ClassicEntryList TrayList { get; }
    public ClassicEntryList AlertList { get; }

    /// <summary>Opens Main Settings over this window (the General tab's button).</summary>
    public Func<Task> ShowMainSettings { get; set; }

    public SensorSettingsWindow(SensorHub hub, AppSettings settings, AlertStore alerts)
        : base("OpenHW - Sensor Settings", new Size(934, 730), new Rect(8.8, 27.2, 917.6, 664), new Point(643.2, 700.8))
    {
        ShowMainSettings = () => new MainSettingsWindow().ShowDialog(this);
        var custom = SensorCustomizations.Current;
        var cpu = hub.CpuTemperatureId();
        var tray = settings.TrayIcons.Select(t => t.SensorId == AppSettings.CpuTemperature ? cpu : t.SensorId).ToHashSet();

        LayoutList = new ClassicEntryList("Monitoring", 733.6, 120);
        CustomizeList = new ClassicEntryList("Logged", 745.6, 108);
        TrayList = new ClassicEntryList("In Tray", 767.2, 84);
        AlertList = new ClassicEntryList("Alert", 757.6, 97.6);
        var hidden = new ClassicEntryList("", 733.6, 120, showValueTitle: false);

        void Fill()
        {
            var all = ClassicEntryList.FromHub(hub, id => custom.IsDisabled(id) ? "No" : "Yes", groupValue: "Yes");
            LayoutList.Fill(WithoutHidden(all, id => !custom.IsHidden(id)));
            hidden.Fill(WithoutHidden(all, custom.IsHidden).Select(e => e with { Value = "" }).ToList());
            CustomizeList.Fill(ClassicEntryList.FromHub(hub, id => custom.IsLogged(id) ? "Yes" : "No"));
            TrayList.Fill(ClassicEntryList.FromHub(hub, id => tray.Contains(id) ? "Yes" : "No"));
            AlertList.Fill(ClassicEntryList.FromHub(hub, id => alerts.For(id).Any() ? "Yes" : "No"));
        }
        Fill();

        AddTab("General", General());
        AddTab("Layout", Layout(LayoutList, hidden));
        AddTab("Customize", Customize(CustomizeList));
        AddTab("Tray Icon", TrayIcon(TrayList));
        AddTab("OSD", null);
        AddTab("OSD (RTSS)", null);
        AddTab("Alerts", Alert(AlertList));
        AddTab("Logitech LCD", null);
        AddTab("HWiNFO Gadget", null);
    }

    // Keeps the sensors that pass, and a group heading (plus the blank line before it) only when it has any.
    private static List<ClassicEntryList.Entry> WithoutHidden(IReadOnlyList<ClassicEntryList.Entry> all, Func<string, bool> keep)
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

    // General_Settings.jpg — page frame at (13, 84).
    private ClassicPage General()
    {
        var p = new ClassicPage(13, 84);
        p.Group("Polling Period", 31, 101, 583, 233, 41);
        p.Label("Global:", 43, 134);
        p.Field("2000", 277, 120, 357, 148);
        p.Label("ms", 372, 134);
        p.Label("Disk S.M.A.R.T. every", 43, 166);
        p.Field("100", 304, 152, 357, 180);
        p.Label("cycles", 370, 166);
        p.Label("Emb. Controller every", 43, 197);
        p.Field("1", 304, 183, 357, 211);
        p.Label("cycles", 370, 197);
        p.Button("Set", 463, 131, 568, 199);

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

    // General_Settings_Layout.jpg — page frame at (15, 83).
    private static ClassicPage Layout(ClassicEntryList list, ClassicEntryList hidden)
    {
        var p = new ClassicPage(15, 83);
        p.Place(list, 34, 103, 1138, 482);
        p.Check("Monitoring", 46, 509, needsSelection: true);
        p.Check("Show", 46, 538, needsSelection: true);
        p.Check("Fixed order", 318, 509, isChecked: true);
        p.Button("Restore Original Order", 316, 529, 597, 574);
        p.Group("Order", 635, 499, 810, 589, 646);
        p.Arrow("↑", "Move up", 663, 523, 692, 549);
        p.Arrow("⤒", "Move to the top", 699, 523, 728, 549);
        p.Arrow("↓", "Move down", 663, 551, 692, 577);
        p.Arrow("⤓", "Move to the bottom", 699, 551, 728, 577);
        p.Button("__", 762, 535, 800, 561);
        p.Label("Hidden items:", 45, 604);
        p.Place(hidden, 34, 630, 1138, 869);
        EnableOnSelection(p, list);
        return p;
    }

    // General_Settings_Customize.jpg — page frame at (11, 81).
    private static ClassicPage Customize(ClassicEntryList list)
    {
        var p = new ClassicPage(11, 81);
        p.Place(list, 30, 101, 1135, 480);
        p.Group("Label", 29, 503, 716, 597, 39);
        p.Label("Original", 41, 533);
        p.Field("", 151, 517, 512, 546, alignRight: false);
        p.Label("Current", 41, 571);
        p.Field("", 151, 556, 512, 586, alignRight: false);
        p.Button("Rename", 536, 556, 696, 588);

        p.Group("Unit", 743, 503, 1026, 597, 753);
        p.Field("", 757, 520, 837, 549, alignRight: false);
        p.Field("", 757, 557, 837, 587, alignRight: false);
        p.Button("Rename", 850, 556, 1011, 588);

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
        p.Field("1", 214, 746, 299, 776, needsSelection: true);
        p.Label("Add (+)", 41, 799);
        p.Field("0", 214, 784, 299, 814, needsSelection: true);
        p.Button("Set", 308, 757, 397, 804);

        p.Label("Thousands Separator:", 446, 735);
        p.Field("", 685, 720, 710, 749, alignRight: false);
        p.Label("Decimal Digits:", 446, 785);
        p.Combo(["0", "1", "2", "3", "4"], -1, 651, 769, 707, 795);
        p.Check("Logged", 758, 757, needsSelection: true);
        EnableOnSelection(p, list);
        return p;
    }

    // General_Settings_Tray_Icon.jpg — page frame at (12, 81).
    private static ClassicPage TrayIcon(ClassicEntryList list)
    {
        var p = new ClassicPage(12, 81);
        p.Place(list, 31, 101, 1135, 480);
        p.Check("Show in Tray", 43, 507, needsSelection: true);
        p.Group("Color", 43, 536, 595, 653, 53);
        p.Label("Background:", 56, 564);
        p.Swatch(200, 543, 243, 586, ClassicColors.Swatch, needsSelection: true);
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

        p.Button("Add All Items To Tray", 691, 669, 949, 723);
        p.Button("Remove All Items From Tray", 691, 741, 949, 795);
        EnableOnSelection(p, list);
        return p;
    }

    // General_Settings_Alerts.jpg — page frame at (9, 78).
    private static ClassicPage Alert(ClassicEntryList list)
    {
        var p = new ClassicPage(9, 78);
        p.Place(list, 28, 98, 1133, 477);
        p.Check("Enable Alerting", 40, 509, needsSelection: true);
        p.Check("If value  <", 352, 509, needsSelection: true);
        p.Field("0", 566, 494, 644, 524, needsSelection: true);
        p.Check("If value >=", 352, 545, needsSelection: true);
        p.Field("0", 566, 530, 644, 560, needsSelection: true);

        p.Check("Play Sound", 40, 597, needsSelection: true);
        p.Button("", 348, 582, 552, 609, needsSelection: true);
        p.Check("Run a Program", 40, 628, needsSelection: true);
        p.Button("", 348, 613, 552, 640, needsSelection: true);
        p.Label("Arguments:", 568, 628, needsSelection: true);
        p.Field("", 662, 612, 1131, 641, alignRight: false, needsSelection: true);
        p.Check("Display Warning Window", 40, 661, needsSelection: true);
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
        p.Field("1", 338, 775, 427, 805, needsSelection: true);
        EnableOnSelection(p, list);
        return p;
    }
}
