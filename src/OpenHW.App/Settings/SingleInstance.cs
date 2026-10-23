using System.Net.Sockets;
using System.Text;

namespace OpenHW.App.Settings;

/// <summary>
/// One OpenHW per user session. The first instance listens on a socket in $XDG_RUNTIME_DIR (private to the user); a
/// second launch connects, asks it to show its window, and exits. A socket left by a crashed instance refuses the
/// connection, so it is replaced.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Another launch asked to see the window. Raised on a background thread.</summary>
    public event Action? ShowRequested;

    private SingleInstance(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
        _ = AcceptAsync();
    }

    private static string SocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : Path.GetTempPath();
        return Path.Combine(runtime, "openhw-app.sock");
    }

    /// <summary>Becomes the instance, or returns null after asking the running one to show itself.</summary>
    public static SingleInstance? Claim()
    {
        var path = SocketPath();
        if (TellRunning(path)) return null;
        try { File.Delete(path); } catch (IOException) { }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(4);
        return new SingleInstance(listener, path);
    }

    private static bool TellRunning(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { SendTimeout = 1000 };
            socket.Connect(new UnixDomainSocketEndPoint(path));
            socket.Send(Encoding.ASCII.GetBytes("show\n"));
            return true;
        }
        catch (SocketException) { return false; } // stale socket from a crashed instance
    }

    private async Task AcceptAsync()
    {
        var buffer = new byte[16];
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptAsync(_cts.Token);
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                if (Encoding.ASCII.GetString(buffer, 0, read).Trim() == "show") ShowRequested?.Invoke();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
