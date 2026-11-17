using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using OpenHW.App.HardwareCheck;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class BiosSettingsWindowTests
{
    private static readonly BiosSettingsReply Reply = new("BiosAttributeRegistryA5496.14.5.0",
    [
        new("ACPI002", "Enable Hibernation", "Enables or Disables System ability to Hibernate (OS/S4 Sleep State).", "Enumeration", "Enabled", "Disabled", false),
        new("ACPI001", "ACPI Sleep State", "Select the highest ACPI sleep state the system will enter when the SUSPEND button is pressed.", "Enumeration", "Suspend Disabled", "S3 (Suspend to RAM)", true),
        new("CbsCmnMemDramEccEnDdrSHP", "DRAM ECC Enable", "Use this option to enable / disable DRAM ECC.", "Enumeration", "Auto", "Auto", true),
        new("CbsCmnPPTLimitSHP", "PPT", null, "Integer", "0", "0", true),
        new("CbsCmnMemt2xRefreshTemperatureThresholdSHP", "DRAM 2x Refresh Temperature Threshold", null, "Enumeration", "85' - 90'", "85' - 90'", true),
    ]);

    [Fact]
    public void SearchMatchesEveryWordInAnyField()
    {
        Assert.Equal(["ACPI002", "ACPI001"], BiosSettingsWindow.Matching(Reply.Settings, null, changedOnly: true).Select(s => s.Name));
        Assert.Equal(["CbsCmnMemDramEccEnDdrSHP"], BiosSettingsWindow.Matching(Reply.Settings, "dram ecc", false).Select(s => s.Name));
        Assert.Equal(["ACPI001"], BiosSettingsWindow.Matching(Reply.Settings, "suspend", true).Select(s => s.Name)); // value
        Assert.Single(BiosSettingsWindow.Matching(Reply.Settings, "pptlimit", false)); // internal name
        Assert.Equal(2, BiosSettingsWindow.Matching(Reply.Settings, "sleep", false).Count); // help text too
    }

    [AvaloniaFact]
    public void ShowsTheSettingsAndCountsTheChangedOnes()
    {
        var window = new BiosSettingsWindow(() => (Reply, null));
        window.Show();
        for (var i = 0; i < 50 && window.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.StartsWith("Reading", StringComparison.Ordinal) == true); i++)
        { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.StartsWith("5 options · 2 changed from default · showing 5", StringComparison.Ordinal));
        window.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, window.GetLogicalDescendants().OfType<ListBox>().Single().ItemCount);
        window.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        var captures = Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath();
        window.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-bios-settings.png"));

        var failing = new BiosSettingsWindow(() => (null, "needs openhw-helper protocol 18: install the current OpenHW .deb"));
        failing.Show();
        for (var i = 0; i < 50 && !failing.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("protocol 18", StringComparison.Ordinal) == true); i++)
        { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
        Assert.Contains(failing.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("protocol 18", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void HardwareCheckListsChangedBiosSettingsAndNotesOpenRedfish()
    {
        BiosSettingsReply With(string hibernation) => Reply with
        {
            Settings = Reply.Settings.Select(s => s.Name == "ACPI002" ? s with { Value = hibernation } : s).ToList(),
        };
        var machine = new OpenHW.App.Sensors.SensorPresentation.MachineInfo("ASUSTeK COMPUTER INC.", "Pro WS WRX90E-SAGE SE", "TR PRO", []);
        var before = HardwareSnapshot.Take([], machine, "AST2600", _ => "", With("Disabled"));
        var now = HardwareSnapshot.Take([], machine, "AST2600", _ => "", With("Enabled"));
        Assert.Equal([new HardwareChange("BIOS setting", "Enable Hibernation: Disabled → Enabled")],
            HardwareDiff.Compare(before, now).Where(c => c.Area == "BIOS setting"));
        // No BIOS settings on one side (BMC silent, or a snapshot from before 0.36.0): nothing compared.
        Assert.DoesNotContain(HardwareDiff.Compare(new HardwareSnapshot { Board = now.Board, BoardVendor = now.BoardVendor, Cpu = now.Cpu, Bmc = now.Bmc }, now),
            c => c.Area == "BIOS setting");
        // Many changes (a BIOS update resets them): the first 12, then a count.
        var many = new BiosSettingsReply("r", Enumerable.Range(0, 30).Select(i => new BiosSetting($"S{i}", $"Option {i:00}", null, "Integer", "1", "0", false)).ToList());
        var manyBefore = HardwareSnapshot.Take([], machine, "AST2600", _ => "", many with { Settings = many.Settings.Select(s => s with { Value = "0" }).ToList() });
        var manyNow = HardwareSnapshot.Take([], machine, "AST2600", _ => "", many);
        var lines = HardwareDiff.Compare(manyBefore, manyNow).Where(c => c.Area == "BIOS setting").ToList();
        Assert.Equal(13, lines.Count);
        Assert.StartsWith("…and 18 more", lines[^1].Text, StringComparison.Ordinal);

        var probe = new Coverage.Probe(_ => false, _ => "", () => "18", Directory.CreateTempSubdirectory("openhw-etc").FullName);
        var open = Coverage.Check(now, [], true, probe, access: new BmcAccessReply(true, ["AuthNone"], true, "HostInterfaceAdministrator"));
        var note = Assert.Single(open, n => n.Area == "BMC access");
        Assert.False(note.Warning); // the owner keeps it open: information, not a warning
        Assert.Contains("AuthNone", note.Text, StringComparison.Ordinal);
        Assert.Contains("HostInterfaceAdministrator", note.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Coverage.Check(now, [], true, probe, access: new BmcAccessReply(false, ["BasicAuth"], false, null)), n => n.Area == "BMC access");
    }
}
