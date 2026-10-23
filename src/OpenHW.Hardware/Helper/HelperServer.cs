using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware;

/// <summary>Where the root helper listens; OPENHW_HELPER_SOCKET moves both ends (tests use a private socket).</summary>
public static class HelperSocket
{
    public const string DefaultPath = "/run/openhw/helper.sock";
    public const string Group = "openhw";

    public static string Path => Environment.GetEnvironmentVariable("OPENHW_HELPER_SOCKET") ?? DefaultPath;
}

/// <summary>
/// One line-based request → one reply. Read-only by construction: the only commands are fixed reads, every
/// argument is validated against what the system enumerates, and nothing a client sends becomes a path,
/// device or opcode. Replies are "ok …" or "err …".
/// </summary>
public sealed partial class HelperSession(INvmeSmartReader smart)
{
    public const string Version = "1";

    public string HandleLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "err empty";
        switch (parts[0])
        {
            case "ping":
                return "ok " + Version;

            case "nvmesmart" when parts.Length == 3:
                if (!NvmeController().IsMatch(parts[1]) || !NvmeIdentity().IsMatch(parts[2])) return "err invalid NVMe identity";
                try
                {
                    var data = smart.Read(parts[1], parts[2]);
                    return data.Length == 512 ? "ok " + Convert.ToBase64String(data) : "err invalid NVMe log length";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }

            default:
                return "err unknown command";
        }
    }

    [GeneratedRegex(@"^nvme[0-9]{1,8}$", RegexOptions.CultureInvariant)] private static partial Regex NvmeController();
    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex NvmeIdentity();
}

/// <summary>
/// SMART reads are slow admin commands; any number of clients asking at any rate reach the drive at most once
/// per <paramref name="minInterval"/> per drive.
/// </summary>
public sealed class CachedSmartReader(INvmeSmartReader inner, TimeSpan minInterval) : INvmeSmartReader
{
    private readonly Dictionary<(string, string), (byte[] Data, DateTime At)> _cache = [];
    private readonly object _gate = new();

    public byte[] Read(string controller, string identity)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((controller, identity), out var hit) && DateTime.UtcNow - hit.At < minInterval) return hit.Data;
            var data = inner.Read(controller, identity);
            _cache[(controller, identity)] = (data, DateTime.UtcNow);
            return data;
        }
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Unix-socket accept loop for <see cref="HelperSession"/>; the openhw-helper daemon is a thin root wrapper.</summary>
public sealed class HelperServer(INvmeSmartReader smart, string socketPath) : IAsyncDisposable
{
    private const int MaxLineBytes = 256; // longest valid request is ~80 bytes

    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _sessions = [];
    private readonly object _sessionsLock = new();
    private Task? _acceptTask;

    public void Start()
    {
        if (File.Exists(socketPath)) File.Delete(socketPath); // stale socket from a crashed run
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(8);
        _acceptTask = AcceptLoopAsync();
    }

    /// <summary>
    /// 0660 root:<paramref name="group"/>. Fails closed: if the group is missing the socket stays root-only (0600).
    /// Returns whether group access was granted.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public bool RestrictToGroup(string group)
    {
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var gid = GroupId(group);
        if (gid is null || Native.chown(socketPath, 0, gid.Value) != 0) return false;
        File.SetUnixFileMode(socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        return true;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }

            var task = ServeAsync(client);
            lock (_sessionsLock)
            {
                _sessions.RemoveAll(t => t.IsCompleted);
                _sessions.Add(task);
            }
        }
    }

    private async Task ServeAsync(Socket client)
    {
        var session = new HelperSession(smart);
        try
        {
            using (client)
            await using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                while (!_cts.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(stream, _cts.Token);
                    if (line is null) break; // EOF, or an over-long line (connection dropped)
                    var reply = Encoding.ASCII.GetBytes(session.HandleLine(line) + "\n");
                    await stream.WriteAsync(reply, _cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
    }

    // Bounded: a client cannot make the root process buffer an unlimited line.
    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxLineBytes];
        var count = 0;
        var one = new byte[1];
        while (count < buffer.Length)
        {
            if (await stream.ReadAsync(one, token) == 0) return null;
            if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\r');
            buffer[count++] = one[0];
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Dispose(); } catch (ObjectDisposedException) { }
        try
        {
            if (_acceptTask is not null) await _acceptTask;
            Task[] sessions;
            lock (_sessionsLock) sessions = _sessions.ToArray();
            await Task.WhenAll(sessions);
        }
        finally
        {
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    private static uint? GroupId(string group)
    {
        var entry = Native.getgrnam(group);
        // struct group { char *gr_name; char *gr_passwd; gid_t gr_gid; char **gr_mem; }
        return entry == 0 ? null : (uint)Marshal.ReadInt32(entry, 2 * IntPtr.Size);
    }

    private static class Native
    {
        [DllImport("libc", SetLastError = true)] public static extern int chown(string path, uint owner, uint group);
        [DllImport("libc")] public static extern nint getgrnam(string name);
    }
}
