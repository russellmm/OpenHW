using System.Net.Sockets;

namespace OpenHW.Hardware;

/// <summary>
/// Reads a drive's SMART log through openfan-linux's root helper (the log needs CAP_SYS_ADMIN). This is the one
/// runtime link to openfan-linux: without its helper running, drive health rows stay empty.
/// </summary>
public sealed class NvmeSmartClient(string? socketPath = null) : INvmeSmartReader
{
    public static string DefaultSocketPath =>
        Environment.GetEnvironmentVariable("OPENFAN_HELPER_SOCKET") ?? "/run/openfan/helper.sock";

    public byte[] Read(string controller, string identity)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { SendTimeout = 1500, ReceiveTimeout = 2500 };
        socket.Connect(new UnixDomainSocketEndPoint(socketPath ?? DefaultSocketPath));
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        writer.WriteLine($"nvmesmart {controller} {identity}");
        // Bounded ASCII response: never consume an unbounded line from a broken/mismatched helper.
        var bytes = new byte[1024]; var count = 0; var end = -1;
        while (count < bytes.Length && end < 0)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            end = Array.IndexOf(bytes, (byte)'\n', count, read); count += read;
        }
        var line = System.Text.Encoding.ASCII.GetString(bytes, 0, end >= 0 ? end : count).TrimEnd('\r');
        if (end < 0 || !line.StartsWith("ok ", StringComparison.Ordinal)) throw new IOException("NVMe health helper: " + line);
        byte[] data;
        try { data = Convert.FromBase64String(line[3..]); } catch (FormatException) { throw new IOException("Malformed NVMe health response"); }
        if (data.Length != 512 || data.All(b => b == 0)) throw new IOException("Invalid NVMe health log");
        return data;
    }
    public void Dispose() { }
}
