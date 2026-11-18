using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace OpenHW.Hardware.Windows;

/// <summary>Where the Windows helper service listens, and who may connect.</summary>
public static class HelperPipe
{
    public const string Name = "openhw-helper";

    /// <summary>The local group whose members' OpenHW may use the helper: the Windows twin of the "openhw" group.</summary>
    public const string Group = "OpenHW Users";
}

/// <summary>
/// The Windows helper service's server: the same line protocol and <see cref="HelperSession"/> as the Unix-socket
/// <see cref="HelperServer"/>, on the named pipe <c>\\.\pipe\openhw-helper</c>. The pipe's access list allows SYSTEM,
/// Administrators and the "OpenHW Users" group — fail closed: without the group, administrators only. The first
/// instance is created with FILE_FLAG_FIRST_PIPE_INSTANCE, so another process can't have squatted the name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeHelperServer(Func<HelperSession> session, string name = HelperPipe.Name) : IAsyncDisposable
{
    private const int MaxLineBytes = 1024; // as HelperServer

    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _sessions = [];
    private readonly object _sessionsLock = new();
    private Task? _acceptTask;

    /// <summary>Whether the group exists, so its members can connect (else administrators only).</summary>
    public bool GroupAccess { get; private set; }

    public void Start()
    {
        var first = Create(first: true); // fails if the name is taken: a second helper, or an impostor
        _acceptTask = AcceptLoopAsync(first);
    }

    private NamedPipeServerStream Create(bool first)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        try
        {
            var group = (SecurityIdentifier)new NTAccount(HelperPipe.Group).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new PipeAccessRule(group, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            GroupAccess = true;
        }
        catch (IdentityNotMappedException) { GroupAccess = false; }
        // Accounts --install allowed by SID (the installing user): group membership only counts from the next sign-in.
        foreach (var user in AllowedUsers()) security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // The server's own account needs FILE_CREATE_PIPE_INSTANCE for the next instances: in the service that is
        // SYSTEM (already allowed); a test server runs as its user.
        if (WindowsIdentity.GetCurrent().User is { } self)
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, options, 4096, 4096, security);
    }

    /// <summary>%ProgramData%\OpenHW\pipe-users: one SID per line, written by <c>openhw-helper --install</c>.</summary>
    public static string AllowedUsersPath => Path.Combine(OsFiles.HelperStateDir, "pipe-users");

    private static IEnumerable<SecurityIdentifier> AllowedUsers()
    {
        string[] lines;
        try { lines = File.ReadAllLines(AllowedUsersPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines)
        {
            SecurityIdentifier? sid = null;
            try { sid = new SecurityIdentifier(line.Trim()); }
            catch (ArgumentException) { }
            if (sid is not null) yield return sid;
        }
    }

    private async Task AcceptLoopAsync(NamedPipeServerStream next)
    {
        try { await AcceptAsync(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw-helper: the pipe stopped accepting clients: {ex.Message}");
        }
    }

    private async Task AcceptAsync(NamedPipeServerStream next)
    {
        while (!_cts.IsCancellationRequested)
        {
            var pipe = next;
            try { await pipe.WaitForConnectionAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                await pipe.DisposeAsync();
                if (_cts.IsCancellationRequested) return;
                next = Create(first: false);
                continue;
            }
            next = Create(first: false); // the next client waits on a fresh instance while this one is served
            var task = ServeAsync(pipe);
            lock (_sessionsLock)
            {
                _sessions.RemoveAll(t => t.IsCompleted);
                _sessions.Add(task);
            }
        }
        await next.DisposeAsync();
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        var helper = session();
        try
        {
            await using (pipe)
            {
                while (!_cts.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(pipe, _cts.Token);
                    if (line is null) break;
                    var reply = Encoding.ASCII.GetBytes(helper.HandleLine(line) + "\n");
                    await pipe.WriteAsync(reply, _cts.Token);
                    await pipe.FlushAsync(_cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    // Bounded: a client cannot make the service buffer an unlimited line.
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
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
        if (_acceptTask is not null) await _acceptTask;
        Task[] sessions;
        lock (_sessionsLock) sessions = _sessions.ToArray();
        await Task.WhenAll(sessions);
    }
}

/// <summary>The Windows side of <see cref="HelperClient"/>: one request over the helper's named pipe.</summary>
[SupportedOSPlatform("windows")]
public static class PipeHelperClient
{
    public static string Request(string line, int receiveTimeoutMs, int maxReplyBytes, string name = HelperPipe.Name)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(1500); }
        catch (TimeoutException) { throw new IOException("openhw-helper: the OpenHW Helper service isn't running"); }
        catch (UnauthorizedAccessException) { throw new IOException($"openhw-helper: access denied (add this user to the \"{HelperPipe.Group}\" group)"); }
        pipe.Write(Encoding.ASCII.GetBytes(line + "\n"));
        pipe.Flush();
        var bytes = new byte[maxReplyBytes];
        var count = 0;
        var end = -1;
        using var timeout = new CancellationTokenSource(receiveTimeoutMs);
        while (count < bytes.Length && end < 0)
        {
            int read;
            try { read = pipe.ReadAsync(bytes.AsMemory(count), timeout.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { throw new IOException("openhw-helper: no reply"); }
            if (read == 0) break;
            end = Array.IndexOf(bytes, (byte)'\n', count, read);
            count += read;
        }
        var reply = Encoding.ASCII.GetString(bytes, 0, end >= 0 ? end : count).TrimEnd('\r');
        if (end < 0 || !reply.StartsWith("ok ", StringComparison.Ordinal)) throw new IOException("openhw-helper: " + reply);
        return reply[3..];
    }
}
