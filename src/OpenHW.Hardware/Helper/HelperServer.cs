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
/// One line-based request → one reply. Every command is fixed and every argument is validated against what the
/// system enumerates: nothing a client sends becomes a path, device or opcode. The writes:
/// <c>cpufreqmax &lt;all|id,id,…&gt; &lt;kHz|max&gt;</c> (cpufreq ceilings; every id an existing policy, every value
/// within its minimum and hardware maximum), <c>cpufreqrestore</c> (every capped policy to its hardware maximum),
/// <c>gpupower &lt;uuid&gt; &lt;mW|default&gt;</c> (an enumerated card, within NVML's constraints for it) and
/// <c>cpuppt &lt;mW|boot&gt;</c> (socket power limit, 50 W up to the SMU's reported maximum, or hsmp-control's boot value),
/// and the boot-time limits: <c>cpupptboot &lt;mW|none&gt;</c> (hsmp-control's config, 100–300 W as it accepts) and
/// <c>gpupowerboot &lt;uuid&gt; &lt;W|default&gt;</c> (OpenHW's drop-in for nvidia-power-limit.service). Each is read back.
/// Replies are "ok …" or "err …".
/// </summary>
public sealed partial class HelperSession(INvmeSmartReader smart, CpuFrequencyLimits? cpufreq = null, GpuPowerLimits? gpuPower = null, CpuPowerLimits? cpuPower = null, BootPowerLimits? boot = null)
{
    public const string Version = "5";

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

            case "cpufreqmax" when parts.Length == 3:
            {
                if (cpufreq is null) return "err cpufreq unavailable";
                if (parts[1] != "all" && !PolicyList().IsMatch(parts[1])) return "err expected all or policy ids";
                if (parts[2] != "max" && !KiloHertz().IsMatch(parts[2])) return "err expected kHz or max";
                long? khz = parts[2] == "max" ? null : long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                var ids = parts[1] == "all"
                    ? cpufreq.Read().Policies.Select(p => p.Id)
                    : parts[1].Split(',').Select(t => int.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).Distinct();
                try { return "ok " + cpufreq.SetMaximum(ids.ToDictionary(id => id, _ => khz)); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
            }

            case "gpupower" when parts.Length == 3:
                if (gpuPower is null) return "err GPU power limits unavailable";
                if (!GpuUuid().IsMatch(parts[1])) return "err invalid GPU";
                if (parts[2] != "default" && !MilliWatts().IsMatch(parts[2])) return "err expected mW or default";
                try { return "ok " + gpuPower.Set(parts[1], parts[2] == "default" ? null : long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }

            case "cpuppt" when parts.Length == 2:
                if (cpuPower is null) return "err CPU power limit unavailable";
                if (parts[1] != "boot" && !MilliWatts().IsMatch(parts[1])) return "err expected mW or boot";
                try { return "ok " + cpuPower.Set(parts[1] == "boot" ? null : long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }

            case "cpupptboot" when parts.Length == 2:
                if (boot is null) return "err boot limits unavailable";
                if (parts[1] != "none" && !MilliWatts().IsMatch(parts[1])) return "err expected mW or none";
                try
                {
                    boot.SetCpu(parts[1] == "none" ? null : long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                    return "ok " + (boot.CpuBootMW()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
                }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }

            case "gpupowerboot" when parts.Length == 3:
            {
                if (boot is null || gpuPower is null) return "err boot limits unavailable";
                if (!GpuUuid().IsMatch(parts[1])) return "err invalid GPU";
                if (parts[2] != "default" && !Watts().IsMatch(parts[2])) return "err expected W or default";
                var gpu = gpuPower.Read().FirstOrDefault(g => g.Uuid.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                if (gpu is null) return "err no such GPU";
                long? watts = parts[2] == "default" ? null : long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                if (watts is { } w && (w * 1000 < gpu.MinMW || w * 1000 > gpu.MaxMW))
                    return $"err {w} W is outside {gpu.MinMW / 1000} to {gpu.MaxMW / 1000} W for {gpu.Name}";
                try
                {
                    boot.SetGpu(gpu.BusId, watts);
                    return "ok " + (boot.GpuBoot().TryGetValue(gpu.BusId, out var b) ? (b.MilliWatts / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
            }

            case "cpufreqrestore" when parts.Length == 1:
                if (cpufreq is null) return "err cpufreq unavailable";
                try { return "ok " + cpufreq.RestoreHardwareMaximum(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
                catch (ArgumentException ex) { return "err " + ex.Message; }

            default:
                return "err unknown command";
        }
    }

    [GeneratedRegex(@"^nvme[0-9]{1,8}$", RegexOptions.CultureInvariant)] private static partial Regex NvmeController();
    [GeneratedRegex(@"^[0-9]{1,4}(,[0-9]{1,4}){0,255}$", RegexOptions.CultureInvariant)] private static partial Regex PolicyList();
    [GeneratedRegex(@"^[1-9][0-9]{3,7}$", RegexOptions.CultureInvariant)] private static partial Regex KiloHertz();
    [GeneratedRegex(@"^[1-9][0-9]{3,6}$", RegexOptions.CultureInvariant)] private static partial Regex MilliWatts();
    [GeneratedRegex(@"^[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant)] private static partial Regex Watts();
    [GeneratedRegex(@"^GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)] private static partial Regex GpuUuid();
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
public sealed class HelperServer(INvmeSmartReader smart, string socketPath, CpuFrequencyLimits? cpufreq = null, GpuPowerLimits? gpuPower = null, CpuPowerLimits? cpuPower = null, BootPowerLimits? boot = null) : IAsyncDisposable
{
    private const int MaxLineBytes = 1024; // longest valid request: cpufreqmax with every policy id listed

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
        var session = new HelperSession(smart, cpufreq, gpuPower, cpuPower, boot);
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
