using OpenHW.App.Settings;
using OpenHW.App.Sensors;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class RenumberingTests
{
    private static HardwareItem Item(string id, string group) =>
        new() { Id = id, Name = id, Kind = HardwareKind.Temperature, Backend = "hwmon", Group = group };

    // This boot: asusec registered first and took hwmon9, so HSMP moved from 9 to 10.
    private static readonly HwmonRenumbering Boot = new([
        Item("hwmon:asusec:9:temp:1", "asusec (hwmon9)"),
        Item("hwmon:amd_hsmp_hwmon:10:power:1", "amd_hsmp_hwmon (hwmon10)"),
        Item("hwmon:nvme:2:temp:1", "nvme (hwmon2)"),
        Item("hwmon:nvme:3:temp:1", "nvme (hwmon3)"),
        Item("hwmon:spd5118:device-x:temp:1", "DDR5 DIMMs"),
    ]);

    [Fact]
    public void ARenumberedChipsSensorAndGroupMoveToTheirCurrentNumber()
    {
        Assert.Equal("hwmon:amd_hsmp_hwmon:10:power:1", Boot.Id("hwmon:amd_hsmp_hwmon:9:power:1"));
        Assert.Equal("asusec (hwmon9)", Boot.Group("asusec (hwmon12)"));
    }

    [Fact]
    public void PresentAmbiguousAndForeignIdsAreLeftAlone()
    {
        Assert.Equal("hwmon:asusec:9:temp:1", Boot.Id("hwmon:asusec:9:temp:1"));
        Assert.Equal("hwmon:nvme:7:temp:1", Boot.Id("hwmon:nvme:7:temp:1")); // two drives: no guess
        Assert.Equal("nvme (hwmon7)", Boot.Group("nvme (hwmon7)"));
        Assert.Equal("hwmon:asusec:4:temp:2", Boot.Id("hwmon:asusec:4:temp:2")); // that channel is gone
        Assert.Equal("nvml:GPU-1:temp:core", Boot.Id("nvml:GPU-1:temp:core"));
        Assert.Equal("auto:cpu-temperature", Boot.Id("auto:cpu-temperature"));
    }

    [Fact]
    public void SavedChoicesFollowTheSensor()
    {
        var settings = new AppSettings { TrayIcons = [new() { SensorId = "hwmon:amd_hsmp_hwmon:9:power:1" }, new()] };
        Assert.True(settings.Remap(Boot));
        Assert.Equal(["hwmon:amd_hsmp_hwmon:10:power:1", AppSettings.CpuTemperature], settings.TrayIcons.Select(t => t.SensorId));
        Assert.False(settings.Remap(Boot));

        var collapsed = new HashSet<string> { "asusec (hwmon10)", "asusec (hwmon11)", "asusec (hwmon12)", "edac" };
        Assert.True(HwmonRenumbering.Remap(collapsed, Boot.Group));
        Assert.Equal(["asusec (hwmon9)", "edac"], collapsed.Order());

        // A name already given under the current id wins over one carried from the old id.
        var names = new Dictionary<string, string> { ["hwmon:amd_hsmp_hwmon:9:power:1"] = "old", ["hwmon:amd_hsmp_hwmon:10:power:1"] = "new" };
        Assert.True(HwmonRenumbering.Remap(names, Boot.Id));
        Assert.Equal("new", Assert.Single(names).Value);
    }
}
