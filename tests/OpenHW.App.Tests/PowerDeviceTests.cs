using OpenHW.App.Sensors;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>The WireView Pro II and UPS groups: titles and a specific explanation for every row.</summary>
public sealed class PowerDeviceTests
{
    private static readonly Dictionary<string, string> UpsVars = new()
    {
        ["input.voltage"] = "120.0", ["output.voltage"] = "120.0", ["ups.load"] = "19", ["ups.realpower"] = "234",
        ["ups.power"] = "234", ["ups.realpower.nominal"] = "1200", ["battery.charge"] = "100", ["battery.voltage"] = "26.3",
        ["battery.runtime"] = "1900", ["ups.status"] = "OL", ["device.mfr"] = "CPS", ["device.model"] = "CP2000PFCRM2U",
        ["driver.name"] = "usbhid-ups", ["ups.test.result"] = "Done and passed",
    };

    private static NutEngine Nut(string status = "OL") => new(cmd => cmd switch
    {
        "LIST UPS" => ["UPS ups \"CyberPower CP2000PFCRM2U\""],
        "LIST VAR ups" => UpsVars.Select(kv => $"VAR ups {kv.Key} \"{(kv.Key == "ups.status" ? status : kv.Value)}\"").ToList(),
        _ => throw new IOException("unexpected " + cmd),
    });

    [Fact]
    public void UpsRowsReadNutAndExplainThemselves()
    {
        using var nut = Nut("OB LB");
        var items = nut.Discover();
        Assert.Equal(OperatingSystem.IsWindows() ? 15 : 13, items.Count); // no input/output frequency or temperature on this UPS; Windows adds Charging / Discharging
        var r = new Dictionary<string, double?>();
        nut.ReadInto(r, null);
        Assert.Equal((234, 1900 / 60.0, 1, 1, 0), (r["nut:ups:ups.realpower"]!.Value, r["nut:ups:battery.runtime"]!.Value,
            r["nut:ups:status:OB"]!.Value, r["nut:ups:status:LB"]!.Value, r["nut:ups:status:RB"]!.Value));
        var group = Assert.Single(SensorPresentation.Build(items, new Dictionary<string, IReadOnlyList<string>>(),
            new SensorPresentation.MachineInfo("ASUSTeK", "Board", "CPU", [])));
        Assert.Equal("UPS: CyberPower CP2000PFCRM2U (NUT)", group.Title);
        // Whole numbers (owner, 2026-11-17): 234 W, 26 V, 32 min, 100 %.
        string Shown(string key, double v) => SensorPresentation.Format(items.Single(i => i.Id == "nut:ups:" + key), v);
        Assert.Equal(("234 W", "26 V", "32 min", "100 %", "1,200 W"),
            (Shown("ups.realpower", 234), Shown("battery.voltage", 26.3), Shown("battery.runtime", 1900 / 60.0), Shown("battery.charge", 100), Shown("ups.realpower.nominal", 1200)));
        AssertExplained(items);
        Assert.Throws<ArgumentException>(() => NutEngine.Query("INSTCMD ups test.battery.start")); // read-only
    }

    [Fact]
    public void WireViewRowsExplainThemselves()
    {
        var reply = GpuWire.Encode(new List<WireViewDevice>
        {
            new("208E31635445", "WireView Pro II", "fw", "/dev/ttyACM0",
                new WireViewSample([26.7, 27.4, 30.1, null], 3.401, 0, [12.025, 12.026, 12.025, 12.029, 12.025, 12.019],
                    [0.312, 0.227, 0.263, 0.397, 0.338, 0.375], [3.75, 2.73, 3.16, 4.78, 4.06, 4.51], 22.99, 1.912, 12.024, 600, 0, 0), null),
        });
        using var engine = new WireViewEngine(_ => reply);
        var items = engine.Discover();
        Assert.Contains(items, i => i.Name == "External Temperature 1");
        Assert.DoesNotContain(items, i => i.Name == "External Temperature 2");
        var group = Assert.Single(SensorPresentation.Build(items, new Dictionary<string, IReadOnlyList<string>>(),
            new SensorPresentation.MachineInfo("ASUSTeK", "Board", "CPU", [])));
        Assert.Equal("Thermal Grizzly WireView Pro II (12V-2x6)", group.Title);
        AssertExplained(items);
        // As the real device names itself: no doubled brand.
        var named = GpuWire.Encode(new List<WireViewDevice> { new("S", "Thermal Grizzly WireView Pro II", "fw", "/dev/ttyACM0",
            new WireViewSample([27.0, 27.0, null, null], 3.4, 0, [12, 12, 12, 12, 12, 12], [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], 0, 0, 12, 600, 0, 0), null) });
        using var engine2 = new WireViewEngine(_ => named);
        Assert.Equal("Thermal Grizzly WireView Pro II (12V-2x6)", SensorPresentation.Build(engine2.Discover(),
            new Dictionary<string, IReadOnlyList<string>>(), new SensorPresentation.MachineInfo("A", "B", "C", [])).Single().Title);
    }

    private static void AssertExplained(IReadOnlyList<HardwareItem> items)
    {
        string[] generic = ["A temperature sensor.", "A voltage measured by the board.", "A power reading.", "A clock frequency.", "A utilisation reading.", ""];
        foreach (var i in items)
            Assert.True(!generic.Contains(SensorExplanations.Explain(i, i.Name)), $"no specific explanation for {i.Name}");
    }
}
