using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class BoardProfileTests
{
    private const string Vendor = "ASUSTeK COMPUTER INC.";
    private const string Board = "Pro WS TRX50-SAGE WIFI A";

    private static HardwareItem Volt(int n, string chip = "nct6799") => new()
    {
        Id = $"hwmon:{chip}:0:in:{n}", Name = $"in{n}", Kind = HardwareKind.Voltage, Backend = "hwmon",
        Group = $"{chip} (hwmon0)", Unit = "V", Details = "libsensors",
    };

    private static HardwareItem Input(string kind, int n, HardwareKind type, string label) => new()
    {
        Id = $"hwmon:nct6799:0:{kind}:{n}", Name = label, Kind = type, Backend = "hwmon",
        Group = "nct6799 (hwmon0)", Unit = type == HardwareKind.Tach ? "RPM" : "°C", Details = "libsensors",
    };

    [Fact]
    public void NamesTemperaturesAndFansLikeHwinfoAndHidesOpenPins()
    {
        var temps = new[] { (1, "SYSTIN"), (3, "AUXTIN0"), (5, "AUXTIN2"), (9, "Virtual_TEMP"), (12, "PCH_CHIP_TEMP"), (13, "TSI0_TEMP") }
            .Select(t => Input("temp", t.Item1, HardwareKind.Temperature, t.Item2));
        var fans = Enumerable.Range(1, 6).Select(n => Input("fan", n, HardwareKind.Tach, $"fan{n}"));
        var (items, scales) = BoardProfiles.Apply([.. temps, .. fans], Vendor, Board);

        Assert.Equal(["Motherboard", "Temp2", "CPU (Weighted Value)", "TSI0 (CPU)",
                      "Chassis 1", "CPU", "Chassis 2", "Chassis 3", "Chassis 4", "Water Pump+"], items.Select(i => i.Name));
        Assert.Empty(scales); // only voltages are scaled
        var weighted = items.Single(i => i.OriginalName == "Virtual_TEMP");
        Assert.False(weighted.MappingConfirmed);
        Assert.Contains("averaged", weighted.Details);
    }

    [Fact]
    public void AnInputsAlarmRowIsNotRenamed()
    {
        var alarm = new HardwareItem
        {
            Id = "hwmon:nct6799:0:temp:1:alarm", Name = "SYSTIN · alarm", Kind = HardwareKind.Status, Backend = "hwmon", Unit = "flag",
        };
        var (items, _) = BoardProfiles.Apply([alarm], Vendor, Board);
        Assert.Equal("SYSTIN · alarm", Assert.Single(items).Name);
    }

    [Fact]
    public void NamesAndScalesKnownBoardRails()
    {
        var (items, scales) = BoardProfiles.Apply([Volt(1), Volt(4), Volt(5), Volt(2)], Vendor, Board);

        Assert.Equal(["+5V", "+12V", "CPU VDDIO", "AVSB"], items.Select(i => i.Name));
        Assert.Equal(5, scales["hwmon:nct6799:0:in:1"]);
        Assert.Equal(12, scales["hwmon:nct6799:0:in:4"]);
        Assert.Equal(1.11, scales["hwmon:nct6799:0:in:5"]);
        Assert.False(scales.ContainsKey("hwmon:nct6799:0:in:2")); // ×1 rails need no scaling
    }

    [Fact]
    public void ScaledReadingsMatchHwinfo()
    {
        // Raw pin voltages read on Linux vs what HWiNFO showed for the same rails on this machine.
        var (_, scales) = BoardProfiles.Apply([Volt(1), Volt(4), Volt(11), Volt(14)], Vendor, Board);
        Assert.Equal(5.120, 1.024 * scales["hwmon:nct6799:0:in:1"], 3);
        Assert.Equal(11.904, 0.992 * scales["hwmon:nct6799:0:in:4"], 3);
        Assert.Equal(1.350, 1.216 * scales["hwmon:nct6799:0:in:11"], 2);
        Assert.Equal(1.803, 1.624 * scales["hwmon:nct6799:0:in:14"], 2);
    }

    [Fact]
    public void HidesUnusedInputsAndMarksUnconfirmedOnes()
    {
        var (items, _) = BoardProfiles.Apply([Volt(13), Volt(17)], Vendor, Board);
        var soc = Assert.Single(items);
        Assert.Equal("CPU VSOC", soc.Name);
        Assert.Equal("in13", soc.OriginalName);
        Assert.Equal(1.11, soc.Scale);
        Assert.False(soc.MappingConfirmed);
    }

    [Fact]
    public void OtherBoardsAndChipsAreUntouched()
    {
        var input = new[] { Volt(1), Volt(1, "it8689") };
        var (otherBoard, scales) = BoardProfiles.Apply(input, "Gigabyte Technology Co., Ltd.", "TRX50 AERO D");
        Assert.Equal(["in1", "in1"], otherBoard.Select(i => i.Name));
        Assert.Empty(scales);

        var (otherChip, _) = BoardProfiles.Apply([Volt(1, "it8689")], Vendor, Board);
        Assert.Equal("in1", Assert.Single(otherChip).Name);
    }
}
