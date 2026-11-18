using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace OpenHW.Helper;

/// <summary>
/// Redfish to the BMC goes out the wired port on the BMC's own subnet, never through a VPN. On the owner's PC
/// Tailscale accepts a subnet route for 10.0.0.0/24 with a lower metric than the directly attached X710 ports, so
/// Windows sent BMC traffic through the tunnel (seen 2026-11-17). The connection is bound to the local address of a
/// physical, up interface whose subnet holds the BMC: Windows' strong-host model then uses that interface's on-link
/// route. With no such interface (BMC on another subnet), the routing table decides as usual.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LanBinding
{
    public static void Apply(SocketsHttpHandler handler, Uri bmc)
    {
        if (!IPAddress.TryParse(bmc.Host, out var target) || target.AddressFamily != AddressFamily.InterNetwork) return;
        if (LocalAddressOnSubnet(target) is not { } local) return;
        Console.Error.WriteLine($"openhw-helper: Redfish to {target} from {local} (the wired port on its subnet)");
        handler.ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(context.DnsEndPoint, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    /// <summary>The IPv4 address of an up Ethernet or Wi-Fi interface (not a tunnel) whose subnet contains <paramref name="target"/>.</summary>
    public static IPAddress? LocalAddressOnSubnet(IPAddress target)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)) continue;
            if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || nic.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.PrefixLength is <= 0 or > 32) continue;
                if (SameSubnet(unicast.Address, target, unicast.PrefixLength)) return unicast.Address;
            }
        }
        return null;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        static uint Value(IPAddress ip) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        return (Value(a) & mask) == (Value(b) & mask);
    }
}
