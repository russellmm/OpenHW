namespace OpenHW.Hardware;

/// <summary>
/// Reads a drive's SMART log through openhw-helper (the log needs root). Without the helper installed, or for
/// a user outside the openhw group, drive health rows stay empty and everything else keeps working.
/// </summary>
public sealed class NvmeSmartClient(string? socketPath = null) : INvmeSmartReader
{
    public byte[] Read(string controller, string identity)
    {
        var reply = HelperClient.Request($"nvmesmart {controller} {identity}", socketPath);
        byte[] data;
        try { data = Convert.FromBase64String(reply); } catch (FormatException) { throw new IOException("Malformed NVMe health response"); }
        if (data.Length != 512 || data.All(b => b == 0)) throw new IOException("Invalid NVMe health log");
        return data;
    }
    public void Dispose() { }
}
