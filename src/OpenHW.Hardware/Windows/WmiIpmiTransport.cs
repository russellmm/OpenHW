using System.Management;
using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// IPMI to the BMC through Windows' in-box driver ("Microsoft Generic IPMI Compliant Device", ipmidrv.sys): the WMI
/// method <c>root\WMI Microsoft_IPMI.RequestResponse</c>. Administrator / LocalSystem only — so it runs in the helper
/// service, like /dev/ipmi0 in the root helper on Linux. Every request goes to the BMC itself (responder address 0x20);
/// nothing is bridged. Replies use <see cref="IIpmiTransport"/>'s format, completion code first, so
/// <see cref="IpmiCommands"/>, <see cref="BmcReader"/> and the fan driver work unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiIpmiTransport : IIpmiTransport
{
    private const byte BmcAddress = 0x20;
    private readonly ManagementObject _ipmi;
    // One request at a time across the whole process: the sensor reader, fan control and the Redfish address lookup
    // each open their own transport, and two requests on the BMC's KCS interface at once fail like HWiNFO's did.
    private static readonly object Gate = new();

    private WmiIpmiTransport(ManagementObject ipmi) => _ipmi = ipmi;

    /// <summary>The driver's WMI object, or null with the reason (no device, not elevated, driver not started).</summary>
    public static WmiIpmiTransport? Open(out string? reason)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"), new ObjectQuery("SELECT * FROM Microsoft_IPMI"));
            var ipmi = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            reason = ipmi is null ? "No IPMI device (Microsoft Generic IPMI Compliant Device not present)." : null;
            return ipmi is null ? null : new WmiIpmiTransport(ipmi);
        }
        catch (ManagementException ex)
        {
            reason = $"IPMI through WMI: {ex.Message.Trim()}";
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "IPMI through WMI needs Administrator (the OpenHW Helper service).";
            return null;
        }
    }

    /// <summary>
    /// Another program driving the BMC's KCS interface at the same time (HWiNFO does, through its own driver) makes
    /// the in-box driver's transfer fail: replies come back with completion codes IPMI doesn't define (0x02, 0x2D seen
    /// 2026-11-17 with HWiNFO running), or none at all. Those are retried a few times; real answers, errors
    /// included, are returned as they are.
    /// </summary>
    private const int Attempts = 5;

    public byte[] Send(byte netFn, byte command, byte lun, ReadOnlySpan<byte> data)
    {
        var payload = data.ToArray();
        for (var attempt = 1; ; attempt++)
        {
            byte[] reply;
            try { reply = SendOnce(netFn, command, lun, payload); }
            catch (IOException) when (attempt < Attempts)
            {
                Thread.Sleep(20 * attempt);
                continue;
            }
            if (reply.Length > 0 && !Defined(reply[0]) && attempt < Attempts)
            {
                Thread.Sleep(20 * attempt);
                continue;
            }
            return reply;
        }
    }

    /// <summary>Completion codes IPMI defines: success, command-specific 80h–BEh, generic C0h–D5h, unspecified FFh.</summary>
    public static bool Defined(byte code) => code is 0x00 or (>= 0x80 and <= 0xBE) or (>= 0xC0 and <= 0xD5) or 0xFF;

    private byte[] SendOnce(byte netFn, byte command, byte lun, byte[] data)
    {
        lock (Gate)
        {
            try
            {
                using var input = _ipmi.GetMethodParameters("RequestResponse");
                input["NetworkFunction"] = netFn;
                input["Command"] = command;
                input["Lun"] = lun;
                input["ResponderAddress"] = BmcAddress;
                // The method wants an array even for a request without data; its size says how much of it counts.
                input["RequestData"] = data.Length > 0 ? data : null; // no data: null with size 0 (checked: same reply as [0] with size 0)
                input["RequestDataSize"] = (uint)data.Length;
                using var output = _ipmi.InvokeMethod("RequestResponse", input, null);
                var code = Convert.ToByte(output["CompletionCode"]);
                var response = output["ResponseData"] as byte[] ?? [];
                var size = Convert.ToUInt32(output["ResponseDataSize"]);
                return Reply(code, response, size);
            }
            catch (ManagementException ex)
            {
                throw new IOException($"IPMI 0x{netFn:x2}/0x{command:x2} through WMI: {ex.Message.Trim()}", ex);
            }
        }
    }

    /// <summary>
    /// The reply as the Linux transport gives it: completion code, then the data. The driver's ResponseData already
    /// starts with the completion code (ResponseDataSize counts it); should a driver ever return the data alone, the
    /// code is put in front.
    /// </summary>
    public static byte[] Reply(byte completionCode, byte[] response, uint size)
    {
        var length = (int)Math.Min(size, (uint)response.Length);
        if (length > 0 && response[0] == completionCode) return response[..length];
        return [completionCode, .. response.AsSpan(0, length)];
    }

    public void Dispose() => _ipmi.Dispose();
}
