using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Power modes that actually take effect. power-profiles-daemon's power-saver turns CPU boost off, and the kernel
/// then holds every cpufreq ceiling at the base clock. Switching back normally restores the previous ceiling, but
/// when the stored ceiling is itself the base clock (seen after a session that started in power-saver),
/// "performance" silently runs at base clock. Whenever a profile change (from OpenHW, GNOME's menu or
/// powerprofilesctl) turns boost back on and the CPU is still capped, this asks openhw-helper to put the ceilings
/// back to the hardware maximum. A cap found any other way (another tool, or one already there when OpenHW
/// started) is only reported, with <see cref="RemoveCapAsync"/> to lift it on request.
/// </summary>
public sealed class PowerModeController(CpuFrequencyLimits limits) : IDisposable
{
    private static readonly TimeSpan BoostSettle = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private PowerProfiles? _profiles;
    private bool _lastBoostOff;

    public IReadOnlyList<string> Profiles { get; private set; } = [];
    public string? ActiveProfile { get; private set; }
    public CpuFrequencyState Cpu { get; private set; } = new([]);

    /// <summary>Why the last switch or cap removal failed; null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>Any of the above changed. Raised on a background thread.</summary>
    public event Action? Changed;

    public bool Available => _profiles is not null && Profiles.Count > 0;

    public async Task StartAsync()
    {
        Refresh();
        try
        {
            var profiles = await PowerProfiles.ConnectAsync();
            Profiles = await profiles.GetProfilesAsync();
            ActiveProfile = await profiles.GetActiveAsync();
            profiles.ActiveChanged += active => _ = OnProfileChangedAsync(active);
            _profiles = profiles;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"openhw: power profiles unavailable ({ex.Message})");
        }
        Changed?.Invoke();
    }

    /// <summary>Re-reads the cpufreq ceilings (cheap sysfs reads; no root).</summary>
    public void Refresh()
    {
        var cpu = limits.Read();
        lock (_gate)
        {
            Cpu = cpu;
            _lastBoostOff = cpu.BoostOff;
        }
    }

    public async Task SetProfileAsync(string profile)
    {
        if (_profiles is null || profile == ActiveProfile) return;
        try
        {
            await _profiles.SetActiveAsync(profile); // the daemon's change signal does the rest
        }
        catch (Exception ex)
        {
            LastError = $"Could not switch to {Label(profile)}: {ex.Message}";
            Changed?.Invoke();
        }
    }

    public async Task RemoveCapAsync()
    {
        await RestoreAsync();
        Changed?.Invoke();
    }

    private async Task OnProfileChangedAsync(string active)
    {
        bool wasBoostOff;
        lock (_gate)
        {
            ActiveProfile = active;
            wasBoostOff = _lastBoostOff;
        }
        LastError = null;
        Changed?.Invoke();

        // The daemon flips boost while it applies the profile; give it a moment to land before deciding.
        var cpu = limits.Read();
        var waited = TimeSpan.Zero;
        while (wasBoostOff && cpu.BoostOff && waited < BoostSettle)
        {
            await Task.Delay(100);
            waited += TimeSpan.FromMilliseconds(100);
            cpu = limits.Read();
        }
        lock (_gate)
        {
            Cpu = cpu;
            _lastBoostOff = cpu.BoostOff;
        }
        if (wasBoostOff && !cpu.BoostOff && cpu.Capped) await RestoreAsync();
        Changed?.Invoke();
    }

    private async Task RestoreAsync()
    {
        try
        {
            await Task.Run(() => HelperClient.Request("cpufreqrestore", receiveTimeoutMs: 5000));
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            LastError = ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old to lift the CPU frequency cap: run sudo packaging/install-helper.sh"
                : $"Could not lift the CPU frequency cap ({ex.Message})";
        }
        Refresh();
    }

    public static string Label(string profile) => profile switch
    {
        "power-saver" => "Power saver",
        "balanced" => "Balanced",
        "performance" => "Performance",
        _ => profile,
    };

    public static string Ghz(long khz) => $"{khz / 1e6:0.00} GHz";

    public void Dispose() => _profiles?.Dispose();
}
