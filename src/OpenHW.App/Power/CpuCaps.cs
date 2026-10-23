using System.Text.Json;

namespace OpenHW.App.Power;

/// <summary>
/// The CPU frequency caps set through OpenHW (policy id → kHz), kept in ~/.config/openhw/cpufreq-caps.json for the
/// current boot only: the kernel forgets ceilings on reboot, so caps saved under another boot id are dropped. The
/// power-mode fix restores these instead of the hardware maximum, so a deliberate cap survives power-saver.
/// </summary>
public sealed class CpuCaps
{
    public string BootId { get; set; } = "";
    public Dictionary<int, long> Policies { get; set; } = [];

    private static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "openhw", "cpufreq-caps.json");

    private static string CurrentBootId() => Hardware.SysFs.ReadText("/proc/sys/kernel/random/boot_id");

    public static CpuCaps Load()
    {
        var boot = CurrentBootId();
        try
        {
            var caps = JsonSerializer.Deserialize<CpuCaps>(File.ReadAllText(FilePath));
            if (caps is not null && caps.BootId == boot) return caps;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new CpuCaps { BootId = boot };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw: could not save CPU caps ({ex.Message})");
        }
    }
}
