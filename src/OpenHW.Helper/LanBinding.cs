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
/// route. With no such interface (BMC on another subnet), the routing table decides as usual. When several wired ports
/// share the subnet, only one may actually reach the BMC (after the 2026-11-18 reboot it answered on Ethernet 5, not
/// Ethernet 4): each is tried with a short connect timeout, the last one that worked first.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LanBinding
{
    public static void Apply(SocketsHttpHandler handler, Uri bmc)
    {
        if (!IPAddress.TryParse(bmc.Host, out var target) || target.AddressFamily != AddressFamily.InterNetwork) return;
        var candidates = LocalAddressesOnSubnet(target);
        if (candidates.Count == 0) return;
        Console.Error.WriteLine($"openhw-helper: Redfish to {target} from {string.Join(" or ", candidates)} (wired ports on its subnet)");
        IPAddress? good = null;
        handler.ConnectCallback = async (context, token) =>
        {
            Exception? last = null;
            foreach (var local in good is { } g ? candidates.OrderBy(c => c.Equals(g) ? 0 : 1) : candidates)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(local, 0));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(context.DnsEndPoint, timeout.Token);
                    if (!local.Equals(good)) Console.Error.WriteLine($"openhw-helper: Redfish to {target} now from {local}");
                    good = local;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    if (token.IsCancellationRequested) throw;
                    last = ex;
                }
            }
            throw new IOException($"the BMC ({target}) doesn't answer from {string.Join(" or ", candidates)}", last);
        };
    }

    /// <summary>How long one wired port gets to connect before the next is tried.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Every up Ethernet or Wi-Fi IPv4 address (not a tunnel) whose subnet contains <paramref name="target"/>.</summary>
    public static List<IPAddress> LocalAddressesOnSubnet(IPAddress target)
    {
        var found = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211)) continue;
            if (nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || nic.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && unicast.PrefixLength is > 0 and <= 32 &&
                    SameSubnet(unicast.Address, target, unicast.PrefixLength))
                    found.Add(unicast.Address);
        }
        return found;
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
