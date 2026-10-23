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
        Assert.Contains("unconfirmed", soc.Details);
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
