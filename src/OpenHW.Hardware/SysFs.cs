namespace OpenHW.Hardware;

/// <summary>Small sysfs/procfs helpers shared by the engines.</summary>
public static class SysFs
{
    /// <summary>Trimmed file contents, or "" when the node is missing or unreadable.</summary>
    public static string ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
