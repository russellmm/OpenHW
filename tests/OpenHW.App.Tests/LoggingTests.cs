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
    public void LoggingCanBeSwitchedOffPerSensor()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-logchoice-").FullName);
        var custom = SensorCustomizations.Current;
        Assert.True(custom.IsLogged("x"));
        custom.SetLogged("x", false);
        Assert.False(custom.IsLogged("x"));
        Assert.Contains("x", System.Text.Json.JsonSerializer.Deserialize<SensorCustomizations>(
            File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")!, "openhw", "sensors.json")))!.NotLogged);
    }
}

public sealed class LogColumnTests
{
    [Fact]
    public void ColumnsFollowTheWindowAndLeaveOutWhatsSwitchedOff()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Directory.CreateTempSubdirectory("openhw-logcols-").FullName);
        using var hub = new SensorHub();
        hub.Sample();
        var all = SensorLogger.ColumnsFor(hub);
        Assert.True(all.Count > 100);
        var cpu = all.Single(c => c.Id == hub.CpuTemperatureId());
        Assert.Equal("CPU [#0]: AMD Ryzen Threadripper 9970X: Enhanced: CPU (Tctl/Tdie) [°C]", cpu.Header);

        var custom = SensorCustomizations.Current;
        custom.SetLogged(all[0].Id, false);
        custom.SetDisabled(all[1].Id, true);
        custom.Rename(cpu.Id, "Threadripper");
        var after = SensorLogger.ColumnsFor(hub);
        Assert.Equal(all.Count - 2, after.Count);
        Assert.DoesNotContain(after, c => c.Id == all[0].Id || c.Id == all[1].Id);
        Assert.EndsWith(": Threadripper [°C]", after.Single(c => c.Id == cpu.Id).Header);
    }
}
