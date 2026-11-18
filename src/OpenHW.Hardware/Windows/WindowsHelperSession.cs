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
/// drop-in (<c>gpupowerboot</c>), which Windows doesn't have.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHelperSession(HelperSession shared, GpuPowerLimits? gpuPower, SavedGpuPowerLimits? saved)
{
    public string HandleLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts)
        {
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
}
