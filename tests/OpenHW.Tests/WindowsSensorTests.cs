using OpenHW.Hardware;
using OpenHW.Hardware.Windows;
using Xunit;

namespace OpenHW.Tests;

public sealed class WindowsSensorTests
{
    // k10temp's decoding (drivers/hwmon/k10temp.c), checked against this machine's first live read (2026-11-18).
    [WindowsFact]
    public void K10TempDecodesTctlAndCcdsAsLinuxDoes()
    {
        Assert.Equal(41.625, K10Temp.Tctl(333u << 21));                   // range-select clear
        Assert.Equal(41.625, K10Temp.Tctl((725u << 21) | 0x80000));       // range-select: −49 °C
        Assert.Equal(42.25, K10Temp.Ccd(0x800 | 730));                    // 730 × 0.125 − 49
        Assert.Null(K10Temp.Ccd(730));                                    // valid bit clear
        Assert.Equal((0x1F0u, 16), K10Temp.Layout(0x1a, 0x08));           // Threadripper PRO 9975WX
        Assert.Equal((0x308u, 8), K10Temp.Layout(0x1a, 0x44));            // Zen 5 Ryzen desktop
        Assert.Null(K10Temp.Layout(0x1a, 0x70));
    }

    [WindowsFact]
    public void AHidUpsReadsAsNutVariables()
    {
        (ushort, ushort)[] input = [(0x84, 0x1A), (0x84, 0x04)], output = [(0x84, 0x1C), (0x84, 0x04)], power = [(0x84, 0x24), (0x84, 0x04)];
        var ups = new WindowsHidUps.HidUps("ups", "CPS", "CP2000PFCRM2U", "", [
            new(0x84, 0x30, input, 120), new(0x84, 0x30, output, 121), new(0x84, 0x35, power, 17), new(0x84, 0x44, power, 1200),
            new(0x85, 0x66, power, 100), new(0x85, 0x68, power, 2100), new(0x85, 0xD0, power, 1), new(0x85, 0x44, power, 0),
            new(0x85, 0x45, power, 0)]);
        var vars = WindowsHidUps.Vars(ups);
        Assert.Equal(("120", "121", "17", "1200", "204"), (vars["input.voltage"], vars["output.voltage"], vars["ups.load"], vars["ups.realpower.nominal"], vars["ups.realpower"]));
        Assert.Equal(("100", "2100", "OL"), (vars["battery.charge"], vars["battery.runtime"], vars["ups.status"]));
    }

    [Fact]
    public void HsmpRowsMatchLinuxIds()
    {
        var reply = GpuWire.Encode(new Dictionary<string, double> { ["fclk"] = 1950, ["socket-power"] = 71, ["limit-sources"] = 16 });
        var engine = new WindowsHsmpEngine(() => reply);
        var items = engine.Discover();
        Assert.Contains(items, i => i.Id == "hsmp:fclk" && i.Group == HsmpEngine.Group);
        Assert.Contains(items, i => i.Id == "hsmp:socket-power" && i.Name == "CPU Package Power");
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.Equal(1950, readings["hsmp:fclk"]);
        Assert.Equal(1, readings["hsmp:limit:opn-max"]);   // bit 4
        Assert.Equal(0, readings["hsmp:limit:ppt-limit"]); // bit 3
    }
}
