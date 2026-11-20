using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenHW.Hardware;

/// <summary>Where a boot-time power limit comes from.</summary>
public enum BootLimitSource { None, BootService, OpenHW }

/// <summary>A limit applied at every boot, in milliwatts, and who set it.</summary>
public sealed record BootLimit(long MilliWatts, BootLimitSource Source);

/// <summary>
/// Boot-time power limits:
/// <list type="bullet">
///   <item>CPU: <c>/etc/openhw/cpu-ppt-mw</c>, which openhw-helper applies once per boot as it starts
///   (<see cref="ApplyCpuOncePerBoot"/>), between <see cref="CpuPowerLimit.MinMW"/> and the SMU's maximum. Until 1.0.1
///   this was <c>/etc/hsmp-control/ppt_mw</c> for hsmp-control-apply.service, a Threadripper 9970X-only tool that
///   refused every start after the board swap; the package moves the value over and retires that service.</item>
///   <item>GPUs: nvidia-power-limit.service runs <c>nvidia-smi -i &lt;bus&gt; -pl &lt;W&gt;</c> per slot. OpenHW never
///   edits that unit; it writes its own drop-in (<c>openhw.conf</c>) that repeats the unit's lines and adds the cards
///   the owner set, by GPU UUID. Deleting the drop-in returns every card to the unit's own values.</item>
/// </list>
/// Reads need no root; writes are helper-only. systemd reads units fresh at boot, so no daemon-reload is needed.
/// </summary>
public sealed partial class BootPowerLimits(string etc = "/etc")
{
    public const string GpuUnit = "nvidia-power-limit.service";

    private string PptFile => Path.Combine(etc, "openhw", "cpu-ppt-mw");
    private string UnitFile => Path.Combine(etc, "systemd", "system", GpuUnit);
    private string DropIn => Path.Combine(etc, "systemd", "system", GpuUnit + ".d", "openhw.conf");

    public bool GpuServiceInstalled => File.Exists(UnitFile);

    /// <summary>The CPU limit openhw-helper applies at boot, or null (firmware default).</summary>
    public long? CpuBootMW() =>
        long.TryParse(SysFs.ReadText(PptFile), NumberStyles.None, CultureInfo.InvariantCulture, out var mw) ? mw : null;

    /// <summary>
    /// What each present card gets at boot, by its UUID: OpenHW's own lines name the card (<c>-i GPU-…</c>), so they
    /// follow it to any slot; the unit's lines name a PCI slot, so they apply to whichever card sits there now.
    /// The drop-in replaces the unit's list, so when it exists only its lines count; the last line for a card wins.
    /// </summary>
    public IReadOnlyDictionary<string, BootLimit> GpuBoot(IReadOnlyList<GpuPowerLimit> present)
    {
        var limits = new Dictionary<string, BootLimit>(StringComparer.OrdinalIgnoreCase);
        var unit = PowerLines(UnitFile).ToList();
        foreach (var (target, watts) in File.Exists(DropIn) ? PowerLines(DropIn) : unit)
        {
            if (IsUuid(target))
            {
                limits[target] = new BootLimit(watts * 1000, BootLimitSource.OpenHW);
                continue;
            }
            if (present.FirstOrDefault(g => SameBus(g.BusId, target)) is not { } card) continue; // nothing in that slot now
            limits[card.Uuid] = new BootLimit(watts * 1000,
                unit.Any(u => SameBus(u.Target, target) && u.Watts == watts) ? BootLimitSource.BootService : BootLimitSource.OpenHW);
        }
        return limits;
    }

    /// <summary>Lines naming a PCI slot (the unit's own, or an OpenHW drop-in from before 0.21.0) — they don't follow a card that moves.</summary>
    public IReadOnlyList<(string Bus, long Watts, bool OpenHW)> SlotLines()
    {
        var unit = PowerLines(UnitFile).Where(l => !IsUuid(l.Target)).ToList();
        // The drop-in repeats the unit's lines; only the ones that differ were OpenHW's (written before 0.21.0).
        return [.. unit.Select(l => (l.Target, l.Watts, false)),
                .. PowerLines(DropIn).Where(l => !IsUuid(l.Target) && !unit.Contains(l)).Select(l => (l.Target, l.Watts, true))];
    }

    /// <summary>
    /// True when OpenHW's drop-in (0.21.0 and later) runs every power line with <c>ExecStart=-</c>: then a slot line
    /// for an empty slot, or a value a card refuses, fails on its own without stopping the lines after it.
    /// </summary>
    public bool LinesFailAlone()
    {
        string text;
        try { text = File.ReadAllText(DropIn); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ExecStart=", StringComparison.Ordinal) && l.Contains("nvidia-smi", StringComparison.Ordinal)).ToList();
        return lines.Count > 0 && lines.All(l => l.StartsWith("ExecStart=-", StringComparison.Ordinal));
    }

    /// <summary>OpenHW's own boot limits, by card UUID.</summary>
    public IReadOnlyDictionary<string, long> CardLines() =>
        PowerLines(DropIn).Where(l => IsUuid(l.Target)).GroupBy(l => l.Target, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Watts, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Root only. Sets (or with null removes) the CPU boot limit, between <see cref="CpuPowerLimit.MinMW"/> and
    /// <paramref name="maxMW"/> (the SMU's maximum); openhw-helper applies it from the next boot.
    /// </summary>
    public void SetCpu(long? milliwatts, long maxMW)
    {
        if (milliwatts is null)
        {
            File.Delete(PptFile);
            return;
        }
        if (milliwatts < CpuPowerLimit.MinMW || milliwatts > maxMW)
            throw new ArgumentException($"{milliwatts / 1000.0:0.#} W is outside {CpuPowerLimit.MinMW / 1000} to {maxMW / 1000} W");
        WriteAtomically(PptFile, milliwatts.Value.ToString(CultureInfo.InvariantCulture) + "\n",
            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (CpuBootMW() != milliwatts) throw new IOException("the boot limit did not read back");
    }

    /// <summary>
    /// Root only. Sets the boot limit for the card <paramref name="uuid"/> to whole <paramref name="watts"/>, or with
    /// null drops OpenHW's value for it (the unit's line for its slot, if any, applies again). The drop-in names cards
    /// by UUID — <c>nvidia-smi -i GPU-…</c> — so a card keeps its limit in any slot. It repeats the unit's own lines first
    /// (they name slots; OpenHW never edits the unit) and OpenHW's after them, so OpenHW's win. Every line may fail on
    /// its own (<c>ExecStart=-</c>): an empty slot or a value a card refuses doesn't stop the others. An OpenHW line by
    /// slot from before 0.21.0 is dropped at the next write — which card it meant can't be known after a move.
    /// </summary>
    public void SetGpu(string uuid, long? watts)
    {
        if (!GpuServiceInstalled) throw new IOException($"{GpuUnit} isn't installed, so a boot limit would never be applied");
        if (!IsUuid(uuid)) throw new ArgumentException("expected a GPU UUID");
        var cards = CardLines().ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        if (watts is { } value) cards[uuid] = value;
        else cards.Remove(uuid);

        if (cards.Count == 0)
        {
            if (File.Exists(DropIn)) File.Delete(DropIn); // the unit's own values again
            return;
        }
        var text = new StringBuilder()
            .AppendLine("# Written by OpenHW (Power Limits → at boot). It replaces the unit's ExecStart lines: the unit's own")
            .AppendLine("# per-slot limits first, then OpenHW's per-card ones (by GPU UUID, so they follow a card to any slot).")
            .AppendLine("# A leading '-' lets one line fail (empty slot, value out of range) without stopping the rest.")
            .AppendLine($"# Delete this file to return to {GpuUnit}'s own values.")
            .AppendLine("[Service]")
            .AppendLine("ExecStart=");
        if (UnitText().Contains("-pm 1", StringComparison.Ordinal)) text.AppendLine("ExecStart=-/usr/bin/nvidia-smi -pm 1");
        foreach (var (bus, w) in PowerLines(UnitFile).Where(l => !IsUuid(l.Target)))
            text.AppendLine(CultureInfo.InvariantCulture, $"ExecStart=-/usr/bin/nvidia-smi -i {bus} -pl {w}");
        foreach (var (card, w) in cards.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            text.AppendLine(CultureInfo.InvariantCulture, $"ExecStart=-/usr/bin/nvidia-smi -i {card} -pl {w}");
        WriteAtomically(DropIn, text.ToString(),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (watts is { } check && CardLines().GetValueOrDefault(uuid) != check) throw new IOException("the boot limit did not read back");
    }

    /// <summary>
    /// Root only (openhw-helper as it starts). Sets the CPU's boot limit once per boot: <paramref name="marker"/> (in
    /// /run, gone at reboot) records that it was done, so a helper restart later in the session (a package upgrade,
    /// RTXinVM's switch) never undoes a live limit the owner set since. Returns what happened, for the journal;
    /// throws when the limit can't be set yet (no HSMP driver) so the caller can retry.
    /// </summary>
    public string ApplyCpuOncePerBoot(CpuPowerLimits cpu, string marker)
    {
        if (File.Exists(marker)) return "CPU boot limit already applied this boot";
        if (CpuBootMW() is not { } mw) return "no CPU boot limit set (firmware default)";
        var set = cpu.Set(mw);
        File.WriteAllText(marker, set.ToString(CultureInfo.InvariantCulture) + "\n");
        return $"CPU boot limit applied: {set / 1000.0:0.#} W";
    }

    public static bool IsUuid(string target) => target.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase);

    /// <summary>"00000000:11:00.0" and "0000:11:00.0" are the same slot.</summary>
    public static bool SameBus(string a, string b) =>
        string.Equals(BusTail(a), BusTail(b), StringComparison.OrdinalIgnoreCase);

    private static string BusTail(string bus) => bus.Length >= 7 ? bus[^7..] : bus;

    private string UnitText() => File.Exists(UnitFile) ? File.ReadAllText(UnitFile) : "";

    private static IEnumerable<(string Target, long Watts)> PowerLines(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (Match m in PowerLine().Matches(File.ReadAllText(path)))
            yield return (m.Groups[1].Value, long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    // Temp file + rename in the same directory: a boot service never reads a half-written file.
    private static void WriteAtomically(string path, string text, UnixFileMode mode)
    {
        var temp = path + ".openhw-tmp";
        File.WriteAllText(temp, text);
        File.SetUnixFileMode(temp, mode);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"^ExecStart=-?\S*nvidia-smi\s+-i\s+(GPU-[0-9A-Fa-f-]+|[0-9A-Fa-f:.]+)\s+-pl\s+([0-9]+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PowerLine();


}
