using System.Net;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The BMC's own LAN address, asked of the BMC over IPMI (Get LAN Configuration Parameters, netFn 0Ch cmd 02h,
/// parameter 3 "IP address" — a read). Windows can't use Redfish's host-interface address (169.254.0.17) without a
/// driver for the BMC's USB NIC, so the helper service reaches Redfish on the LAN address instead: the same BMC, the
/// same "openhw" login. LAN channels are usually 1–2 (the WRX90E: 1 = the dedicated management port).
/// </summary>
public static class BmcLanAddress
{
    /// <summary>
    /// The address to use: of every LAN channel's address (the WRX90E has several ports — on 2026-11-17 channel 1
    /// reported 10.10.10.10 while the owner's network reaches the BMC at 10.0.0.200), the first one that
    /// <paramref name="reachable"/> accepts (a subnet this PC is wired to), else the first configured one.
    /// </summary>
    public static IPAddress? Find(IIpmiTransport ipmi, Func<IPAddress, bool>? reachable = null)
    {
        var all = All(ipmi);
        return all.FirstOrDefault(a => reachable?.Invoke(a) == true) ?? all.FirstOrDefault();
    }

    /// <summary>Each LAN channel's configured IPv4 address (channels 1–8), in channel order.</summary>
    public static IReadOnlyList<IPAddress> All(IIpmiTransport ipmi)
    {
        var addresses = new List<IPAddress>();
        for (byte channel = 1; channel <= 8; channel++)
        {
            byte[] reply;
            try { reply = ipmi.Send(0x0C, 0x02, 0, [channel, 0x03, 0x00, 0x00]); }
            catch (IOException) { continue; }
            if (Parse(reply) is { } address && !addresses.Contains(address)) addresses.Add(address);
        }
        return addresses;
    }

    /// <summary>Reply: completion code, parameter revision, then the 4 address bytes. 0.0.0.0 = not configured.</summary>
    public static IPAddress? Parse(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 6 || reply[0] != 0) return null;
        var bytes = reply.Slice(2, 4);
        if (bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254) return null; // unset, or a link-local (host interface) address
        return new IPAddress(bytes);
    }
}
