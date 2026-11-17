using OpenHW.Hardware;

namespace OpenHW.App.HardwareCheck;

/// <summary>
/// What the BMC publishes over Redfish through openhw-helper (protocol 18): BIOS Setup's options and whether the
/// host interface lets this PC in without a login. Each read is a helper request that can take a few seconds the
/// first time (the BIOS attribute registry), so callers run it off the UI thread.
/// </summary>
public static class BmcInfo
{
    /// <summary>The BIOS settings reply is ~0.5 MB on the wire (1,300 options with help text).</summary>
    public const int MaxReplyBytes = 8 << 20;

    public static (BiosSettingsReply? Value, string? Error) BiosSettings(Func<string, string>? request = null) =>
        Fetch<BiosSettingsReply>("biossettings", request);

    public static (BmcAccessReply? Value, string? Error) Access(Func<string, string>? request = null) =>
        Fetch<BmcAccessReply>("bmcaccess", request);

    private static (T? Value, string? Error) Fetch<T>(string command, Func<string, string>? request) where T : class
    {
        request ??= line => HelperClient.Request(line, receiveTimeoutMs: 60000, maxReplyBytes: MaxReplyBytes);
        try { return (GpuWire.Decode<T>(request(command)), null); }
        catch (IOException ex) when (ex.Message.Contains("unknown command", StringComparison.Ordinal))
        { return (null, "needs openhw-helper protocol 18: install the current OpenHW .deb"); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException)
        { return (null, ex.Message.Replace("openhw-helper: err ", "")); }
    }
}
