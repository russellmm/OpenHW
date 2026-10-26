using OpenHW.App.Sensors;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class LoggingTests
{
    [Fact]
    public void WritesAHeaderAndOneRowPerPoll()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("openhw-log-").FullName, "sub", "log.csv");
        var columns = new[]
        {
            new LogColumn("a", "CPU [#0]: AMD Ryzen: Enhanced: CPU (Tctl/Tdie) [°C]", v => v),
            new LogColumn("b", "Memory \"used\" [MB]", v => v * 1024), // a scaled sensor (GiB shown as MB)
            new LogColumn("c", "Missing", v => v),
        };
        using (var logger = new SensorLogger(path, columns))
        {
            logger.Append(new DateTime(2026, 10, 23, 21, 5, 9), id => id switch { "a" => 43.125, "b" => 1.5, _ => null });
            logger.Append(new DateTime(2026, 10, 23, 21, 5, 11), id => id == "a" ? 44 : double.NaN);
            Assert.Equal(2, logger.Rows);
            // Flushed every row: readable while logging continues.
            Assert.Equal(3, File.ReadAllLines(path).Length);
        }
        var lines = File.ReadAllLines(path);
        Assert.Equal("Date,Time,\"CPU [#0]: AMD Ryzen: Enhanced: CPU (Tctl/Tdie) [°C]\",\"Memory \"\"used\"\" [MB]\",Missing".Replace("\"CPU", "CPU").Replace("[°C]\"", "[°C]"), lines[0]);
        Assert.Equal("2026-10-23,21:05:09,43.125,1536,", lines[1]);
        Assert.Equal("2026-10-23,21:05:11,44,,", lines[2]);
    }

    [Fact]
    public void LoggingIsOptInPerSensor()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-logchoice-").FullName);
        var custom = SensorCustomizations.Current;
        Assert.False(custom.IsLogged("x")); // nothing is logged until chosen
        custom.SetLogged("x", true);
        Assert.True(custom.IsLogged("x"));
        Assert.Contains("x", System.Text.Json.JsonSerializer.Deserialize<SensorCustomizations>(
            File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!, "openhw", "sensors.json")))!.Logged);
        custom.SetLogged("x", false);
        Assert.False(custom.IsLogged("x"));
    }
}

public sealed class LogColumnTests
{
    [Fact]
    public void OnlyChosenSensorsBecomeColumnsInWindowOrder()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-logcols-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        Assert.Empty(SensorLogger.ColumnsFor(hub)); // opt-in: nothing chosen, nothing logged

        var custom = SensorCustomizations.Current;
        var cpu = hub.CpuTemperatureId()!;
        var fclk = hub.Inventory.First(i => i.Id == "hsmp:fclk").Id;
        // A drive row: its group comes after the CPU's (GPU rows need a protocol-8 helper, so they aren't used here).
        var drive = hub.Inventory.First(i => i.Backend == "nvme-activity").Id;
        foreach (var id in new[] { drive, fclk, cpu }) custom.SetLogged(id, true);
        var columns = SensorLogger.ColumnsFor(hub);
        Assert.Equal([cpu, fclk, drive], columns.Select(c => c.Id)); // window order: CPU group before the drives
        Assert.Equal("CPU [#0]: AMD Ryzen Threadripper 9970X: Enhanced: CPU (Tctl/Tdie) [°C]", columns[0].Header);

        custom.SetDisabled(fclk, true);          // monitoring off: left out even though chosen
        custom.Rename(cpu, "Threadripper");
        var after = SensorLogger.ColumnsFor(hub);
        Assert.Equal([cpu, drive], after.Select(c => c.Id));
        Assert.EndsWith(": Threadripper [°C]", after[0].Header);
    }
}
