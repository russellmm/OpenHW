namespace OpenHW.Hardware;

/// <summary>Which hwmon chips the hwmon engine skips (e.g. "it87", "nvme").</summary>
public sealed class SourceSettings
{
    public bool Hwmon { get; set; } = true;

    /// <summary>hwmon chip names excluded from the inventory.</summary>
    public List<string> DisabledHwmonChips { get; set; } = [];

    public bool Nvml { get; set; } = true;
}
