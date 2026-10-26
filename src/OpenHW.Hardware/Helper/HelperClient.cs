using System.Net.Sockets;
using System.Text;

namespace OpenHW.Hardware;

/// <summary>One request to openhw-helper. Throws <see cref="IOException"/> or <see cref="SocketException"/> when the
/// helper is missing, the user is outside the openhw group, or the reply is "err …".</summary>
public static class HelperClient
{
    /// <summary>Sends <paramref name="line"/> and returns the reply after "ok ".</summary>
    public static string Request(string line, string? socketPath = null, int receiveTimeoutMs = 2500, int maxReplyBytes = 1024)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { SendTimeout = 1500, ReceiveTimeout = receiveTimeoutMs };
        socket.Connect(new UnixDomainSocketEndPoint(socketPath ?? HelperSocket.Path));
        using var stream = new NetworkStream(socket, ownsSocket: false);
        stream.Write(Encoding.ASCII.GetBytes(line + "\n"));
        // Bounded ASCII response: never consume an unbounded line from a broken/mismatched helper.
        var bytes = new byte[maxReplyBytes]; var count = 0; var end = -1;
        while (count < bytes.Length && end < 0)
        {
            var read = stream.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            end = Array.IndexOf(bytes, (byte)'\n', count, read); count += read;
        }
        var reply = Encoding.ASCII.GetString(bytes, 0, end >= 0 ? end : count).TrimEnd('\r');
        if (end < 0 || !reply.StartsWith("ok ", StringComparison.Ordinal)) throw new IOException("openhw-helper: " + reply);
        return reply[3..];
    }
}
