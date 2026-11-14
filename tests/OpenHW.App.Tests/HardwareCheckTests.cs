using Avalonia.Headless;
using Avalonia.LogicalTree;
using OpenHW.App.HardwareCheck;
using OpenHW.App.Sensors;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>The TRX50-SAGE → WRX90E-SAGE SE swap of 2026-11: what the hardware check makes of it.</summary>
public sealed class HardwareCheckTests
{
    private static HardwareItem Item(string id, string name, HardwareKind kind, string group = "", string backend = "hwmon") =>
        new() { Id = id, Name = name, Kind = kind, Backend = backend, Group = group };

    private static readonly IReadOnlyList<HardwareItem> Wrx90 =
    [
        Item("hwmon:nct6798:0:fan:7", "fan7", HardwareKind.Tach, "nct6798 (hwmon0)"),
        Item("hwmon:nct6798:0:fan:1", "fan1", HardwareKind.Tach, "nct6798 (hwmon0)"),
        Item("hwmon:asusec:11:fan:1", "CPU_Opt", HardwareKind.Tach, "asusec (hwmon11)"),
        Item("bmc:W_PUMP+", "W_PUMP+", HardwareKind.Tach, BmcEngine.GroupKey, "openhw"),
        Item("bmc:CPU_FAN", "CPU_FAN", HardwareKind.Tach, BmcEngine.GroupKey, "openhw"),
        Item("bmc:CHA_FAN2", "CHA_FAN2", HardwareKind.Tach, BmcEngine.GroupKey, "openhw"),
        Item("bmc:DIMMA1_Temp", "DIMMA1_Temp", HardwareKind.Temperature, BmcEngine.GroupKey, "openhw"),
        Item("bmc:DIMMB1_Temp", "DIMMB1_Temp", HardwareKind.Temperature, BmcEngine.GroupKey, "openhw"),
        Item("hwmon:nvme:4:temp:1", "Drive temperature", HardwareKind.Temperature, "nvme (hwmon4)"),
        Item("nvml:GPU-aaaa:temp:core", "temp", HardwareKind.Temperature, "RTX PRO 6000 Blackwell Workstation Edition C1:00.0", "nvml"),
        Item("nvml:GPU-bbbb:temp:core", "temp", HardwareKind.Temperature, "RTX PRO 6000 Blackwell Workstation Edition F1:00.0", "nvml"),
    ];

    private static string Name(HardwareItem i) => SensorPresentation.BuiltInName(i);

    private static HardwareSnapshot Snapshot(string board, string cpu, params SnapshotGpu[] gpus) => new()
    {
        Taken = new DateTime(2026, 10, 28, 12, 0, 0), BoardVendor = "ASUSTeK COMPUTER INC.", Board = board, Cpu = cpu,
        Gpus = gpus.ToList(), Chips = ["k10temp", "nct6799"], MemoryGiB = 246,
    };

    [Fact]
    public void TheOldPumpAlertPointsAtTheBmcsPumpHeader()
    {
        var now = HardwareSnapshot.Take(Wrx90, new("ASUSTeK COMPUTER INC.", "Pro WS WRX90E-SAGE SE", "AMD Ryzen Threadripper PRO 9975WX", []), "AST2600", Name);
        var stale = StaleReferences.Find(
            [new("hwmon:nct6799:0:fan:6", "Alert below 2800"), new("hwmon:nct6799:0:fan:2", "Tray icon"), new("auto:cpu-temperature", "Tray icon"),
             new("bmc:W_PUMP+", "Logged")],
            Wrx90, before: null, now, Name, new Dictionary<string, string>());
        Assert.Equal(2, stale.Count); // the auto icon and the present sensor aren't stale
        var pump = stale.Single(s => s.Id == "hwmon:nct6799:0:fan:6");
        Assert.Equal(("Water Pump+", "bmc:W_PUMP+"), (pump.OldName, pump.Suggestion)); // name from the TRX50 profile
        Assert.Equal(["Alert below 2800"], pump.Uses);
        Assert.Equal("bmc:CPU_FAN", stale.Single(s => s.Id == "hwmon:nct6799:0:fan:2").Suggestion); // "CPU" fan
    }

    [Fact]
    public void NamesFromBoardsHwinfoAndBmcsMeetInOneSpelling()
    {
        Assert.Equal(StaleReferences.Normalise("Water Pump+", HardwareKind.Tach), StaleReferences.Normalise("W_PUMP+", HardwareKind.Tach));
        Assert.Equal(StaleReferences.Normalise("Chassis 2", HardwareKind.Tach), StaleReferences.Normalise("CHA_FAN2", HardwareKind.Tach));
        Assert.Equal(StaleReferences.Normalise("CPU", HardwareKind.Tach), StaleReferences.Normalise("CPU_FAN", HardwareKind.Tach));
        Assert.NotEqual(StaleReferences.Normalise("CPU", HardwareKind.Temperature), StaleReferences.Normalise("CPU_FAN", HardwareKind.Temperature));
    }

    [Fact]
    public void ADriveTemperatureFollowsTheDriveNotTheNumber()
    {
        var before = Snapshot("x", "y");
        before.Drives = [new("id-solidigm", "SOLIDIGM", "0000:c1:00.0", 3), new("id-wd", "WD", "0000:d1:00.0", 6)];
        var now = Snapshot("x", "y");
        now.Drives = [new("id-solidigm", "SOLIDIGM", "0000:21:00.0", 4)];
        var stale = StaleReferences.Find([new("hwmon:nvme:3:temp:1", "Tray icon"), new("hwmon:nvme:6:temp:1", "Logged")],
            Wrx90, before, now, Name, new Dictionary<string, string>());
        Assert.Equal("hwmon:nvme:4:temp:1", stale.Single(s => s.Id == "hwmon:nvme:3:temp:1").Suggestion);
        Assert.Null(stale.Single(s => s.Id == "hwmon:nvme:6:temp:1").Suggestion); // that drive is gone: no guess
    }

    [Fact]
    public void TheSwapReadsAsBoardCpuAndSlotChanges()
    {
        var before = Snapshot("Pro WS TRX50-SAGE WIFI A", "AMD Ryzen Threadripper 9970X 32-Cores",
            new("GPU-aaaa", "RTX PRO 6000", "11:00.0"), new("GPU-bbbb", "RTX PRO 6000", "E1:00.0"));
        var now = Snapshot("Pro WS WRX90E-SAGE SE", "AMD Ryzen Threadripper PRO 9975WX 32-Cores",
            new("GPU-aaaa", "RTX PRO 6000", "C1:00.0"), new("GPU-bbbb", "RTX PRO 6000", "F1:00.0"));
        now.Chips = ["k10temp", "nct6798"];
        now.Bmc = "AST2600";
        var changes = HardwareDiff.Compare(before, now);
        Assert.Contains(changes, c => c.Area == "Motherboard" && c.Text == "Pro WS TRX50-SAGE WIFI A → Pro WS WRX90E-SAGE SE");
        Assert.Contains(changes, c => c.Area == "CPU");
        Assert.Contains(changes, c => c.Text == "Moved: RTX PRO 6000 E1:00.0 → F1:00.0");
        Assert.Contains(changes, c => c.Text == "New: nct6798");
        Assert.Contains(changes, c => c.Text == "Gone: nct6799");
        Assert.Contains(changes, c => c.Area == "BMC" && c.Text == "New: AST2600");
        Assert.Empty(HardwareDiff.Compare(now, now));
    }

    [Fact]
    public void ALentCardIsNotReportedGone()
    {
        var before = Snapshot("b", "c", new SnapshotGpu("GPU-aaaa", "RTX PRO 6000", "C1:00.0"));
        var now = Snapshot("b", "c");
        Assert.Single(HardwareDiff.Compare(before, now));
        Assert.Empty(HardwareDiff.Compare(before, now, gpuLent: true));
        var stale = StaleReferences.Find([new("nvml:GPU-cccc:temp:core", "Tray icon")], Wrx90, before, now, Name,
            new Dictionary<string, string>(), gpuLent: true);
        Assert.Empty(stale);
    }

    [Fact]
    public void CoverageNamesTheStaleLendingCardTheMissingProfileAndTheDimmGap()
    {
        var now = HardwareSnapshot.Take(Wrx90, new("ASUSTeK COMPUTER INC.", "Pro WS WRX90E-SAGE SE", "TR PRO", []), "AST2600", Name);
        now.DimmsReadable = 4;
        now.DimmsBmc = 8;
        var files = new Dictionary<string, string>
        {
            ["/etc/openhw/card-filter.json"] = """{"Card":"0000:e1:00.0","Enabled":true}""",
            ["/dev/ipmi0"] = "", ["/sys/bus/acpi/devices/IPI0001:00"] = "",
            [Coverage.BootGpuLimitsFile] = "ExecStart=/usr/bin/nvidia-smi -i 00000000:01:00.0 -pl 150\nExecStart=/usr/bin/nvidia-smi -i 00000000:C1:00.0 -pl 300\n",
        };
        var notes = Coverage.Check(now, Wrx90, bmcAnswered: true,
            new Coverage.Probe(files.ContainsKey, p => files.GetValueOrDefault(p, ""), () => "12"));
        var lending = notes.First();
        Assert.True(lending.Warning);
        Assert.Equal(("GPU Lending", Coverage.OpenGpuLending), (lending.Area, lending.Action));
        Assert.Contains("0000:e1:00.0", lending.Text);
        Assert.Contains(notes, n => n.Area == "Memory" && n.Text.StartsWith("8 DIMMs installed (BMC), 4 readable"));
        Assert.Contains(notes, n => n.Area == "Motherboard" && n.Text.StartsWith("No board profile for Pro WS WRX90E-SAGE SE (nct6798)"));
        Assert.DoesNotContain(notes, n => n.Area == "BMC");
        var boot = notes.Single(n => n.Area == "Power limits");
        Assert.True(boot.Warning);
        Assert.Contains("but 01:00.0 has no GPU now", boot.Text); // C1:00.0 still has a card
        Assert.Equal(Coverage.OpenPowerLimits, boot.Action);
    }

    [Fact]
    public void AnOldHelperOrAnUnloadedDriverIsCalledOut()
    {
        var now = new HardwareSnapshot { Board = "b", Chips = ["nct6798"] };
        var oldHelper = Coverage.Check(now, [], bmcAnswered: false,
            new Coverage.Probe(p => p is "/dev/ipmi0" or "/sys/bus/acpi/devices/IPI0001:00", _ => "", () => "11"));
        Assert.Contains(oldHelper, n => n.Area == "BMC" && n.Warning && n.Text.Contains("protocol 11"));
        var noDriver = Coverage.Check(now, [], bmcAnswered: false,
            new Coverage.Probe(p => p == "/sys/bus/acpi/devices/IPI0001:00", _ => "", () => "12"));
        Assert.Contains(noDriver, n => n.Area == "BMC" && n.Fix!.Contains("modprobe ipmi_devintf"));
    }

    [Fact]
    public void SnapshotsRoundTripAndCountBmcDimms()
    {
        var now = HardwareSnapshot.Take(Wrx90, new("ASUSTeK", "Pro WS WRX90E-SAGE SE", "TR PRO", []), "AST2600", Name);
        Assert.Equal(2, now.DimmsBmc);
        Assert.Equal(["RTX PRO 6000 Blackwell Workstation Edition"], now.Gpus.Select(g => g.Name).Distinct());
        Assert.Equal(["C1:00.0", "F1:00.0"], now.Gpus.Select(g => g.Pci));
        var path = Path.Combine(Path.GetTempPath(), $"openhw-hw-{Guid.NewGuid():N}.json");
        try
        {
            now.Save(path);
            var back = HardwareSnapshot.Load(path)!;
            Assert.Equal((now.Board, now.Bmc, now.Gpus.Count, now.SensorNames["bmc:W_PUMP+"]), (back.Board, back.Bmc, back.Gpus.Count, back.SensorNames["bmc:W_PUMP+"]));
            Assert.Empty(HardwareDiff.Compare(back, now));
        }
        finally { File.Delete(path); }
    }
}

/// <summary>The window itself, headless: its sections, the Move action, and a render for checking by eye.</summary>
public sealed class HardwareCheckWindowTests
{
    private sealed class FakeActions : IHardwareCheckActions
    {
        public List<string> Moves { get; } = [];
        public int Accepted { get; private set; }

        public IReadOnlyList<HardwareItem> Inventory { get; } =
        [
            new() { Id = "bmc:W_PUMP+", Name = "W_PUMP+", Kind = HardwareKind.Tach, Backend = "openhw", Group = BmcEngine.GroupKey, Source = "AST2600 BMC (IPMI, openhw-helper)" },
            new() { Id = "hwmon:nct6798:0:fan:7", Name = "fan7", Kind = HardwareKind.Tach, Backend = "hwmon", Group = "nct6798 (hwmon0)" },
        ];

        public SensorPresentation.MachineInfo Machine { get; } = new("ASUSTeK COMPUTER INC.", "Pro WS WRX90E-SAGE SE", "AMD Ryzen Threadripper PRO 9975WX 32-Cores", []);

        public HardwareCheckModel Build()
        {
            var now = new HardwareSnapshot { BoardVendor = Machine.BoardVendor, Board = Machine.BoardName, Bios = "1405", Cpu = Machine.CpuModel, MemoryGiB = 246, Bmc = "AST2600" };
            var before = new HardwareSnapshot { Taken = new DateTime(2026, 10, 28, 9, 0, 0), Board = "Pro WS TRX50-SAGE WIFI A", Cpu = "AMD Ryzen Threadripper 9970X 32-Cores" };
            var stale = Moves.Count > 0 ? [] : new List<StaleReference>
            {
                new("hwmon:nct6799:0:fan:6", "Water Pump+", HardwareKind.Tach, ["Alert below 2800"], "bmc:W_PUMP+"),
            };
            return new(before, now, HardwareDiff.Compare(before, now), stale,
            [
                new("GPU Lending", "Set up for card 0000:e1:00.0, which isn't in this machine any more.", true, "Choose the card again in Power → GPU Lending.", Coverage.OpenGpuLending),
                new("Memory", "8 DIMMs installed (BMC), 4 readable over the SPD sideband."),
            ]);
        }

        public void Move(StaleReference stale, string newId) => Moves.Add($"{stale.Id}→{newId}");
        public void Remove(StaleReference stale) { }
        public void Accept() => Accepted++;
        public void ShowGpuLending() { }
        public void ShowPowerLimits() { }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void ShowsChangesStaleSettingsAndCoverageAndMovesToTheSuggestion()
    {
        var actions = new FakeActions();
        var window = new HardwareCheckWindow(actions);
        window.Show();
        var texts = window.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Changes since 2026-10-28 09:00", texts);
        Assert.Contains("Motherboard: Pro WS TRX50-SAGE WIFI A → Pro WS WRX90E-SAGE SE", texts);
        Assert.Contains("Water Pump+", texts);
        Assert.Contains(texts, t => t?.StartsWith("GPU Lending: Set up for card 0000:e1:00.0", StringComparison.Ordinal) == true);
        window.CaptureRenderedFrame()?.Save(Path.Combine(
            Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath(), "openhw-hardware-check.png"));

        var pick = window.GetLogicalDescendants().OfType<Avalonia.Controls.ComboBox>().Single();
        Assert.StartsWith("★ W_PUMP+", (string)((Avalonia.Controls.ComboBoxItem)pick.SelectedItem!).Content!);
        var move = window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().Single(b => (string?)b.Content == "Move");
        move.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(["hwmon:nct6799:0:fan:6→bmc:W_PUMP+"], actions.Moves);
        Assert.Contains("None — every tray icon, alert, logged sensor and name points at a present sensor.",
            window.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text));
        window.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().Single(b => (string?)b.Content == "Save as current hardware")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(1, actions.Accepted);
        window.Close();
    }
}
