using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace OpenHW.App.Settings;

/// <summary>
/// One OpenHW per user session. The first instance listens — on Linux on a socket in $XDG_RUNTIME_DIR (private to the
/// user), on Windows on a named pipe only this user can open — and a second launch connects, asks it to show its
/// window, and exits. A socket left by a crashed instance refuses the connection, so it is replaced; a pipe goes away
/// with its process.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Socket? _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();
    private NamedPipeServerStream? _pipe;

    /// <summary>Another launch asked to see the window. Raised on a background thread.</summary>
    public event Action? ShowRequested;

    private SingleInstance(Socket? listener, string path, NamedPipeServerStream? pipe)
    {
        _listener = listener;
        _path = path;
        _pipe = pipe;
        _ = listener is not null ? AcceptAsync() : AcceptPipeAsync();
    }

    private static string SocketPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : Path.GetTempPath();
        return Path.Combine(runtime, "openhw-app.sock");
    }

    // Pipe names are machine-wide: the user's SID keeps two users' OpenHWs apart, and a test's private
    // XDG_RUNTIME_DIR (hashed) keeps it apart from a real OpenHW running meanwhile.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string PipeName()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var suffix = string.IsNullOrEmpty(runtime) ? ""
            : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(runtime)))[..12];
        return $"openhw-app-{sid}{suffix}";
    }

    /// <summary>Becomes the instance, or returns null after asking the running one to show itself.</summary>
    public static SingleInstance? Claim()
    {
        if (OperatingSystem.IsWindows()) return ClaimPipe();
        var path = SocketPath();
        if (TellRunning(path)) return null;
        try { File.Delete(path); } catch (IOException) { }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        OpenHW.Hardware.OsFiles.OwnerOnly(path);
        listener.Listen(4);
        return new SingleInstance(listener, path, null);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SingleInstance? ClaimPipe()
    {
        var name = PipeName();
        try
        {
            // One server instance: a second launch can't create it, which is how it knows OpenHW is running.
            var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            return new SingleInstance(null, name, pipe);
        }
        catch (IOException)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(2000);
                client.Write(Encoding.ASCII.GetBytes("show\n"));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { }
            return null;
        }
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
                using var client = await _listener!.AcceptAsync(_cts.Token);
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                if (Encoding.ASCII.GetString(buffer, 0, read).Trim() == "show") ShowRequested?.Invoke();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    // The pipe serves one client at a time: read its line, then disconnect and wait for the next.
    private async Task AcceptPipeAsync()
    {
        var buffer = new byte[16];
        while (!_cts.IsCancellationRequested && _pipe is { } pipe)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_cts.Token);
                var read = await pipe.ReadAsync(buffer, _cts.Token);
                if (Encoding.ASCII.GetString(buffer, 0, read).Trim() == "show") ShowRequested?.Invoke();
                pipe.Disconnect();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (IOException)
            {
                try { pipe.Disconnect(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Dispose();
        _pipe?.Dispose();
        _pipe = null;
        if (_listener is not null) try { File.Delete(_path); } catch (IOException) { }
    }
}
