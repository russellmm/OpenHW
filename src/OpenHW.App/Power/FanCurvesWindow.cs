using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenHW.App.Alerts;
using OpenHW.App.SensorWindow;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Power → Fan Curves…: the user's curves, as in OpenFan — graphs over any temperature, flat duties, and mixes (max /
/// min / average of other curves). Each row says where the curve runs: on the BMC (only the BMC's own sensors: no flash
/// writes once applied) or driven by OpenHW (⚠ flash writes). Fans pick their curve in Fan Control.
/// </summary>
public sealed class FanCurvesWindow : Window
{
    private readonly FanCurveSensors _sensors;
    private readonly StackPanel _body = new() { Spacing = 6, Margin = new Thickness(14, 10) };

    public FanCurvesWindow(FanCurveSensors sensors)
    {
        _sensors = sensors;
        CurveUi.Dialog(this, "Fan Curves", 980);
        Content = _body;
        Store.Changed += OnChanged;
        Closed += (_, _) => Store.Changed -= OnChanged;
        Draw();
    }

    private FanCurveStore Store => _sensors.Runner.Store;

    private void OnChanged() => Dispatcher.UIThread.Post(Draw);

    public void Draw()
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock { Foreground = Palette.Footnote, TextWrapping = TextWrapping.Wrap,
            Text = "Curves give a duty from temperatures. One that reads only the BMC's own sensors (CPU Package, T_Sensor, LAN, " +
                   "the DIMMs, the PCIe slots — which read the GPUs in them) runs on the BMC: no flash writes once applied. One that " +
                   "reads any other sensor makes OpenHW drive the fan, and every duty change is written to the BMC's flash. Pick " +
                   "a fan's curve in Power → Fan Control…" });
        _body.Children.Add(AlertUi.Row(
            AlertUi.Button("New graph", () => New(FanCurveKind.Graph)),
            AlertUi.Button("New flat", () => New(FanCurveKind.Flat)),
            AlertUi.Button("New mix", () => New(FanCurveKind.Mix))));

        if (Store.Curves.Count == 0)
        {
            _body.Children.Add(new TextBlock { Text = "No curves yet.", Foreground = Palette.Footnote, Margin = new Thickness(0, 6) });
            return;
        }
        var choices = _sensors.Choices();
        var library = Store.Library;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*,260,170,70,70"), Margin = new Thickness(0, 6, 0, 0) };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        void Cell(int row, int col, string text, IBrush? brush = null, FontWeight weight = FontWeight.Normal) =>
            Put(new TextBlock { Text = text, Foreground = brush ?? Palette.Text, FontWeight = weight, Margin = new Thickness(0, 3, 10, 3),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, row, col);
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headers = ["Curve", "What", "Runs", "Fans", "", ""];
        for (var c = 0; c < headers.Length; c++) Cell(0, c, headers[c], Palette.Footnote, FontWeight.SemiBold);
        var row = 1;
        foreach (var curve in Store.Curves)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var plan = BmcCurveFit.Plan(curve, library, _sensors.Runner.BmcNumber, 1, Store.FourthAt70);
            Cell(row, 0, curve.Name);
            Cell(row, 1, MixCurveDialog.Describe(curve, choices) +
                         (curve.Kind == FanCurveKind.Mix ? ": " + string.Join(", ", curve.Children.Select(id => Store.Find(id)?.Name ?? "?")) : ""));
            Cell(row, 2, plan.OnBmc ? "On the BMC (no flash writes)" : "⚠ OpenHW-driven (flash writes)", plan.OnBmc ? CurveUi.BmcLine : CurveUi.Warn);
            var fans = Store.Assignments.Where(a => a.Value == curve.Id).Select(a => AsusBmcFans.Sensors[FanCurveRunner.ZoneIndex(a.Key)]);
            Cell(row, 3, string.Join(", ", fans) is { Length: > 0 } f ? f : "–", Palette.Footnote);
            var c0 = curve;
            var edit = AlertUi.Button("Edit…", () => Edit(c0, isNew: false));
            edit.Padding = new Thickness(8, 1);
            Put(edit, row, 4);
            var delete = AlertUi.Button("Delete", () => _ = DeleteAsync(c0));
            delete.Padding = new Thickness(8, 1);
            Put(delete, row, 5);
            row++;
        }
        _body.Children.Add(grid);
    }

    private void New(FanCurveKind kind)
    {
        var id = FanCurveStore.NewId();
        var curve = kind switch
        {
            FanCurveKind.Graph => FanCurve.NewGraph(id, "New graph", _sensors.Choices().FirstOrDefault(c => c.Bmc)?.Id),
            FanCurveKind.Flat => new FanCurve { Id = id, Name = "New flat", Kind = FanCurveKind.Flat, Percent = 40 },
            _ => new FanCurve { Id = id, Name = "New mix", Kind = FanCurveKind.Mix },
        };
        Edit(curve, isNew: true);
    }

    /// <summary>Opens the curve's editor; Ok saves it (a new one is added). Fans running it on the BMC show "not applied".</summary>
    public Window Edit(FanCurve curve, bool isNew)
    {
        void Save(FanCurve edited)
        {
            var at = Store.Curves.FindIndex(c => c.Id == edited.Id);
            if (at >= 0) Store.Curves[at] = edited;
            else if (isNew) Store.Curves.Add(edited);
            Store.Save();
        }
        Window editor = curve.Kind switch
        {
            FanCurveKind.Graph => new GraphEditorWindow(_sensors, curve, Save),
            FanCurveKind.Flat => new FlatCurveDialog(curve, Save),
            _ => new MixCurveDialog(_sensors, curve, Save),
        };
        Settings.WindowPlaces.Track(editor, "fan-curve-" + curve.Kind.ToString().ToLowerInvariant());
        editor.ShowDialog(this);
        return editor;
    }

    private async Task DeleteAsync(FanCurve curve)
    {
        var fans = Store.Assignments.Count(a => a.Value == curve.Id);
        var mixes = Store.Curves.Count(c => c.Children.Contains(curve.Id));
        var text = $"Delete \"{curve.Name}\"?" +
                   (fans > 0 ? $"\n\n{fans} fan(s) run it: they keep the BMC curve they have now (an OpenHW-driven fan goes back to its BMC curve)." : "") +
                   (mixes > 0 ? $"\n\nIt's taken out of {mixes} mix(es)." : "");
        if (!await new ConfirmDialog("Fan Curves", text, "Delete", false).ShowDialog<bool>(this)) return;
        Store.Remove(curve.Id);
        Store.Save();
    }
}
