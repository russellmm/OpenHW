using System.Globalization;
using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Power modes and CPU frequency caps that actually take effect. power-profiles-daemon's power-saver turns CPU boost
/// off, and the kernel then holds every cpufreq ceiling at the base clock. Switching back normally restores the
/// previous ceiling, but a ceiling stored while boost was off (the base clock) survives that, and "performance" then
/// silently runs at base clock. Whenever a profile change (from OpenHW, GNOME's menu or powerprofilesctl) turns boost
/// back on, this puts every ceiling back to what it should be: the cap set through OpenHW this boot (see
/// <see cref="CpuCaps"/>), or the hardware maximum. A ceiling lower than that found any other way (another tool, or
/// already there when OpenHW started) is only reported, with <see cref="RestoreCapsAsync"/> to fix it on request.
/// </summary>
public sealed class PowerModeController(CpuFrequencyLimits limits) : IDisposable
{
    private static readonly TimeSpan BoostSettle = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly CpuCaps _caps = CpuCaps.Load();
    private PowerProfiles? _profiles;
    private bool _boostWasOff; // boost has been seen off since the last off→on fix: sticky, so no read can race it away

    public IReadOnlyList<string> Profiles { get; private set; } = [];
    public string? ActiveProfile { get; private set; }
    public CpuFrequencyState Cpu { get; private set; } = new([]);
    public IReadOnlyList<CpuCore> Cores { get; } = limits.ReadCores();

    /// <summary>Why the last switch or cap change failed; null after a success.</summary>
    public string? LastError { get; private set; }

    /// <summary>Any of the above changed. Raised on a background thread.</summary>
    public event Action? Changed;

    public bool Available => _profiles is not null && Profiles.Count > 0;

    /// <summary>The cap set through OpenHW for this policy this boot, if any.</summary>
    public long? CapOf(int policy)
    {
        lock (_gate) return _caps.Policies.TryGetValue(policy, out var khz) ? khz : null;
    }

    /// <summary>
    /// The lowest ceiling that sits below what it should be, while boost is on — the base-clock clamp, or a cap from
    /// another tool. Null when every policy is where OpenHW expects it.
    /// </summary>
    public long? UnexpectedCapKHz
    {
        get
        {
            var cpu = Cpu;
            if (cpu.BoostOff) return null;
            var low = cpu.Policies.Where(p => p.MaxKHz < Desired(p)).ToArray();
            return low.Length == 0 ? null : low.Min(p => p.MaxKHz);
        }
    }

    // OpenHW's cap for the policy, or the hardware maximum; never above what the hardware offers right now.
    private long Desired(CpuFrequencyPolicy policy) => Math.Min(CapOf(policy.Id) ?? policy.HardwareMaxKHz, policy.HardwareMaxKHz);

    /// <summary>Re-apply OpenHW's caps whenever OpenHW starts (so after a reboot too, with Start at login).</summary>
    public bool KeepCapsAfterReboot
    {
        get { lock (_gate) return _caps.KeepAfterReboot; }
        set
        {
            lock (_gate)
            {
                _caps.KeepAfterReboot = value;
                _caps.Save();
            }
        }
    }

    public async Task StartAsync()
    {
        Refresh();
        await ReapplySavedCapsAsync();
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
            _boostWasOff |= cpu.BoostOff;
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

    /// <summary>
    /// Sets caps through openhw-helper (null = remove the cap: hardware maximum) and remembers them for this boot.
    /// Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> SetCapsAsync(IReadOnlyDictionary<int, long?> targets)
    {
        var error = await WriteAsync(targets);
        if (error is null)
        {
            lock (_gate)
            {
                foreach (var (id, khz) in targets)
                {
                    // A cap at the boost maximum is no cap; one stored while boost is off still counts.
                    var policy = Cpu.Get(id);
                    if (khz is { } value && (policy is null || value < policy.HardwareMaxKHz || policy.Boost == false)) _caps.Policies[id] = value;
                    else _caps.Policies.Remove(id);
                }
                _caps.Save();
            }
        }
        LastError = error;
        Refresh();
        Changed?.Invoke();
        return error;
    }

    /// <summary>Puts every ceiling that sits below OpenHW's cap (or the hardware maximum) back there.</summary>
    public async Task RestoreCapsAsync()
    {
        LastError = await WriteAsync(Mismatched(Cpu));
        Refresh();
        Changed?.Invoke();
    }

    private Dictionary<int, long?> Mismatched(CpuFrequencyState cpu) =>
        cpu.Policies.Where(p => p.MaxKHz != Desired(p)).ToDictionary(p => p.Id, p => CapOf(p.Id) is { } cap && cap < p.HardwareMaxKHz ? cap : (long?)null);

    private async Task OnProfileChangedAsync(string active)
    {
        lock (_gate) ActiveProfile = active;
        LastError = null;
        Changed?.Invoke();

        // The daemon can announce the new profile before it flips boost: wait until boost matches the profile
        // (off for power-saver, on otherwise), or for the profile to turn out not to change it.
        var boostOff = active == "power-saver";
        var cpu = limits.Read();
        var waited = TimeSpan.Zero;
        while (cpu.BoostOff != boostOff && waited < BoostSettle)
        {
            await Task.Delay(100);
            waited += TimeSpan.FromMilliseconds(100);
            cpu = limits.Read();
        }
        bool fixNow;
        lock (_gate)
        {
            Cpu = cpu;
            _boostWasOff |= cpu.BoostOff;
            fixNow = _boostWasOff && !cpu.BoostOff;
            if (fixNow) _boostWasOff = false;
        }
        if (fixNow && Mismatched(cpu) is { Count: > 0 } fix)
        {
            LastError = await WriteAsync(fix);
            Refresh();
        }
        Changed?.Invoke();
    }

    // Saved caps carried over from an earlier boot: write back only those, and only the ones the hardware allows right
    // now (a cap above the boost-off base clock waits for boost — the power-mode fix applies it then). Other policies
    // are left alone: a ceiling OpenHW didn't set is only reported.
    private async Task ReapplySavedCapsAsync()
    {
        Dictionary<int, long?> targets;
        lock (_gate)
            targets = _caps.Policies
                .Where(c => Cpu.Get(c.Key) is { } p && c.Value <= p.HardwareMaxKHz && p.MaxKHz != c.Value)
                .ToDictionary(c => c.Key, c => (long?)c.Value);
        if (targets.Count == 0) return;
        LastError = await WriteAsync(targets);
        Refresh();
    }

    // One helper command per distinct value; ids are cpufreq policy numbers.
    private static async Task<string?> WriteAsync(IReadOnlyDictionary<int, long?> targets)
    {
        if (targets.Count == 0) return null;
        try
        {
            await Task.Run(() =>
            {
                foreach (var group in targets.GroupBy(t => t.Value))
                {
                    var ids = string.Join(',', group.Select(t => t.Key.ToString(CultureInfo.InvariantCulture)));
                    var value = group.Key is { } khz ? khz.ToString(CultureInfo.InvariantCulture) : "max";
                    HelperClient.Request($"cpufreqmax {ids} {value}", receiveTimeoutMs: 5000);
                }
            });
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            return ex.Message.Contains("unknown command", StringComparison.Ordinal)
                ? "openhw-helper is too old for CPU frequency caps: run sudo packaging/install-helper.sh"
                : $"CPU frequency cap failed ({ex.Message.Replace("openhw-helper: err ", "")})";
        }
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
