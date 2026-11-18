using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// GPU power limits kept across reboots on Windows, by card UUID (so a limit follows the card to any slot, as on Linux):
/// %ProgramData%\OpenHW\gpu-power-limits.json, readable by SYSTEM and Administrators only. Windows has no
/// nvidia-power-limit.service: the OpenHW Helper service applies these itself when it starts — what OpenFan did.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SavedGpuPowerLimits(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(OsFiles.HelperStateDir, "gpu-power-limits.json");
    private readonly object _gate = new();

    /// <summary>UUID → whole watts.</summary>
    public IReadOnlyDictionary<string, long> Read()
    {
        lock (_gate)
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(_path)) ?? []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Dictionary<string, long>(); }
        }
    }

    public void Set(string uuid, long? watts)
    {
        lock (_gate)
        {
            var limits = new Dictionary<string, long>(Read(), StringComparer.OrdinalIgnoreCase);
            if (watts is { } w) limits[uuid] = w;
            else limits.Remove(uuid);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            using (var stream = OsFiles.CreatePrivate(temp)) JsonSerializer.Serialize(stream, limits);
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>Applies every saved limit to its card, if present; returns one line per card for the log.</summary>
    public IEnumerable<string> Apply(GpuPowerLimits gpus)
    {
        var present = gpus.Read();
        foreach (var (uuid, watts) in Read())
        {
            var card = present.FirstOrDefault(g => g.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
            if (card is null) { yield return $"{uuid}: not present, saved {watts} W kept"; continue; }
            string result;
            try { result = $"{card.Name} ({uuid}): {gpus.Set(card.Uuid, watts * 1000)} mW"; }
            catch (Exception ex) when (ex is ArgumentException or IOException) { result = $"{card.Name} ({uuid}): {ex.Message}"; }
            yield return result;
        }
    }
}

/// <summary>
/// The Windows service's commands: the shared <see cref="HelperSession"/> (same protocol as Linux) plus the ones only
/// Windows has — <c>gpupowersaved</c> (the saved limits, as GpuWire JSON) and <c>gpupowersave &lt;uuid&gt; &lt;W|none&gt;</c>
/// (an enumerated card, within NVML's range for it). Linux keeps its boot limits in nvidia-power-limit.service's
/// drop-in (<c>gpupowerboot</c>), which Windows doesn't have. The CPU socket limit through AMD's HSMP driver
/// (<see cref="WindowsCpuPowerLimits"/>): <c>cpupptread</c> (GpuWire JSON of <see cref="CpuPowerLimit"/>, BootMW = the
/// kept value), <c>cpuppt &lt;mW|boot&gt;</c> (as on Linux; "boot" = the kept value) and <c>cpupptsave &lt;W|none&gt;</c>.
/// </summary>
[SupportedOSPlatform("windows")]
/// <summary>
/// The fans' BMC sensor sources, kept for <see cref="Fresh"/> — in practice read once per service run. Only Redfish
/// (HTTPS) can read or set which sensors a fan follows: IPMI's 30 93 offers just CPU / T-sensor / TR1–TR3 per PWM (ASUS
/// E26328 appendix A) and this firmware refuses to read it (0xC7). On Windows Redfish goes over the LAN with a login per
/// read (three HTTPS requests, about a second), and Fan Control asked every 2 s: that load slowed every other request
/// to the service and the BMC itself, and the app dropped the BMC sensors (2026-11-17). Sources change only through
/// OpenHW (cleared on every write, restore and login change) or the BMC's own web console (Fan Control shows the
/// change after the service restarts or OpenHW writes a source).
/// </summary>
public sealed class FanSourcesCache(Func<DateTime>? now = null)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(12);
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private string? _reply;
    private DateTime _at;

    /// <summary>
    /// How long a failed read is kept: Fan Control asks every 2 s and waits for the answer, so a BMC that doesn't
    /// answer (each try ~10 s) froze it (2026-11-18). The next try comes after this.
    /// </summary>
    public static readonly TimeSpan FailureHold = TimeSpan.FromSeconds(60);

    private string? _failure;
    private DateTime _failedAt;

    public string Get(Func<string> read)
    {
        lock (_gate) // one read at a time; the others wait for it and share its answer
        {
            if (_reply is not null && _now() - _at < Fresh) return _reply;
            if (_failure is not null && _now() - _failedAt < FailureHold) return _failure;
            var reply = read();
            if (reply.StartsWith("ok ", StringComparison.Ordinal)) (_reply, _at, _failure) = (reply, _now(), null);
            else (_failure, _failedAt) = (reply, _now());
            return reply;
        }
    }

    public void Clear() { lock (_gate) (_reply, _failure) = (null, null); }
}

public sealed partial class WindowsHelperSession(HelperSession shared, GpuPowerLimits? gpuPower, SavedGpuPowerLimits? saved,
    WindowsCpuPowerLimits? cpuPower = null, FanSourcesCache? sources = null, WindowsHsmpSensors? hsmp = null, WindowsKernelSensors? kernel = null, WindowsRapl? rapl = null, WindowsEffectiveClocks? clocks = null, BoardVrReader? vr = null)
{
    public string HandleLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
            case ["effclock"] when clocks is not null: // read-only: effective clocks from APERF (WindowsEffectiveClocks)
                return clocks.Read() is { } mhz ? "ok " + GpuWire.Encode(mhz) : "err " + (clocks.Error ?? "effective clocks unavailable");
            case ["rapl"] when rapl is not null: // read-only: RAPL energy, Linux's reply format (WindowsRapl)
                return rapl.Read() is { } counters ? "ok " + RaplReader.Format(counters) : "err " + (rapl.Error ?? "RAPL unavailable");
            case ["vr", "page"]: // the board VR on its rail's page: a PAGE write and restore, on boards that allow it (BoardVrReader)
                if (vr?.Read(switchPage: true) is not { } sample) return "err no known board VR";
                return "ok " + BoardVrReader.Format(sample);
            case ["uefivar", var name, var guid]: // read-only: one UEFI variable for BIOS Setup (WindowsUefi), base64
                if (!WindowsUefi.ValidName(name) || !Guid.TryParse(guid, out var vendor)) return "err expected uefivar <name> <guid>";
                try { return WindowsUefi.Read(name, vendor) is { } bytes ? "ok " + Convert.ToBase64String(bytes) : "ok none"; }
                catch (IOException ex) { return "err " + ex.Message; }
            case ["kernel"]: // read-only: PawnIO readings as hwmon-style chips (WindowsKernelSensors)
                if (kernel is null) return "err kernel access unavailable";
                return "ok " + GpuWire.Encode(kernel.Read());
            case ["hsmpread"]: // read-only: the CPU's HSMP readings (WindowsHsmpSensors)
                if (hsmp is null) return "err HSMP unavailable";
                return hsmp.Read() is { } values ? "ok " + GpuWire.Encode(values) : "err " + (hsmp.Error ?? "HSMP unavailable");
            case ["fansources"] when sources is not null:
                return sources.Get(() => shared.HandleLine(line));
            case ["fansource", _, _] or ["fanoriginal", _] or ["bmclogin", _, _] or ["bmclogout"] when sources is not null:
                sources.Clear(); // a source written, an original curve restored or another login: read again next time
                try { return shared.HandleLine(line); }
                finally { sources.Clear(); }
            case ["cpupptread"]:
                if (cpuPower is null) return "err CPU power limit unavailable";
                return cpuPower.Read() is { } now ? "ok " + GpuWire.Encode(now) : "err " + (cpuPower.Error ?? "CPU power limit unavailable");
            case ["cpuppt", var value]:
                if (cpuPower is null) return "err CPU power limit unavailable";
                if (value != "boot" && !MilliWatts().IsMatch(value)) return "err expected mW or boot";
                try { return "ok " + cpuPower.Set(value == "boot" ? null : long.Parse(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }
            case ["cpupptsave", var value]:
                if (cpuPower is null) return "err CPU power limit unavailable";
                if (value != "none" && !Watts().IsMatch(value)) return "err expected W or none";
                try
                {
                    cpuPower.Keep(value == "none" ? null : long.Parse(value, CultureInfo.InvariantCulture) * 1000);
                    return "ok " + (cpuPower.Saved() is { } kept ? (kept / 1000).ToString(CultureInfo.InvariantCulture) : "none");
                }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
            case ["gpupowersaved"]:
                return saved is null ? "err saved limits unavailable" : "ok " + GpuWire.Encode(saved.Read());
            case ["gpupowersave", var uuid, var value]:
            {
                if (saved is null || gpuPower is null) return "err GPU power limits unavailable";
                if (!Uuid().IsMatch(uuid)) return "err invalid GPU";
                if (value != "none" && !Watts().IsMatch(value)) return "err expected W or none";
                var card = gpuPower.Read().FirstOrDefault(g => g.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
                if (card is null) return "err no such GPU";
                long? watts = value == "none" ? null : long.Parse(value, CultureInfo.InvariantCulture);
                if (watts is { } w && (w * 1000 < card.MinMW || w * 1000 > card.MaxMW))
                    return $"err {w} W is outside {card.MinMW / 1000} to {card.MaxMW / 1000} W for {card.Name}";
                try { saved.Set(card.Uuid, watts); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
                return "ok " + (saved.Read().TryGetValue(card.Uuid, out var kept) ? kept.ToString(CultureInfo.InvariantCulture) : "none");
            }
            default:
                return shared.HandleLine(line);
        }
    }

    [GeneratedRegex(@"^GPU-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex("^[0-9]{1,4}$")]
    private static partial Regex Watts();

    [GeneratedRegex("^[0-9]{1,7}$")]
    private static partial Regex MilliWatts();
}
