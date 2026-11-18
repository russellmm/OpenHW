using System.Runtime.Versioning;
using System.Text.Json;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The CPU socket power limit (PPT) on Windows: HSMP SET/GET_SOCKET_POWER_LIMIT (5/6), the maximum (7) and the socket's
/// power (4) through AMD's driver (<see cref="WindowsHsmp"/>) — the same messages Linux's amd_hsmp_hwmon sends for
/// <c>power1_cap</c>, with the same rules: <see cref="CpuPowerLimit.MinMW"/> up to the SMU's maximum, read back after
/// setting. The live limit lasts until reboot; the kept value (%ProgramData%\OpenHW\cpu-power-limit.json, the Windows
/// twin of hsmp-control's ppt_mw) is applied by the OpenHW Helper service when it starts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCpuPowerLimits(Func<WindowsHsmp?> open, string? savedPath = null) : IDisposable
{
    private const uint SetLimit = 5, GetLimit = 6, GetLimitMax = 7, GetPower = 4;
    private readonly string _savedPath = savedPath ?? Path.Combine(OsFiles.HelperStateDir, "cpu-power-limit.json");
    private readonly object _gate = new();
    private WindowsHsmp? _hsmp;
    private bool _opened;

    /// <summary>Why it is unavailable, when <see cref="Read"/> returns null.</summary>
    public string? Error { get; private set; }

    private WindowsHsmp? Hsmp()
    {
        if (!_opened)
        {
            _opened = true;
            _hsmp = open();
            if (_hsmp is null) Error ??= "AMD's HSMP driver isn't available.";
        }
        return _hsmp;
    }

    public CpuPowerLimit? Read()
    {
        lock (_gate)
        {
            if (Hsmp() is not { } hsmp) return null;
            try
            {
                var limit = hsmp.Send(GetLimit, [], 1)[0];
                var max = hsmp.Send(GetLimitMax, [], 1)[0];
                var power = hsmp.Send(GetPower, [], 1)[0];
                return new CpuPowerLimit(limit, max, power, Saved(), null, null);
            }
            catch (IOException ex)
            {
                Error = ex.Message;
                return null;
            }
        }
    }

    /// <summary>Sets the live limit (null = the kept value) and reads it back; returns the limit in mW.</summary>
    public long Set(long? milliwatts)
    {
        lock (_gate)
        {
            var now = Read() ?? throw new IOException(Error ?? "HSMP unavailable");
            var target = milliwatts ?? Saved() ?? throw new ArgumentException("no kept CPU power limit");
            if (target < CpuPowerLimit.MinMW || target > now.MaxMW)
                throw new ArgumentException($"{target / 1000.0:0.#} W is outside {CpuPowerLimit.MinMW / 1000} to {now.MaxMW / 1000} W");
            _hsmp!.Send(SetLimit, [(uint)target], 0);
            var kept = _hsmp.Send(GetLimit, [], 1)[0];
            if (kept != target) throw new IOException($"the CPU reports {kept / 1000.0:0.#} W after setting {target / 1000.0:0.#} W (firmware clamp?)");
            return target;
        }
    }

    /// <summary>The kept limit in mW, or null.</summary>
    public long? Saved()
    {
        try { return JsonSerializer.Deserialize<Kept>(File.ReadAllText(_savedPath))?.MilliWatts; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Keeps <paramref name="milliwatts"/> for every service start (validated like <see cref="Set"/>), or forgets it.</summary>
    public void Keep(long? milliwatts)
    {
        if (milliwatts is { } mw)
        {
            var now = Read() ?? throw new IOException(Error ?? "HSMP unavailable");
            if (mw < CpuPowerLimit.MinMW || mw > now.MaxMW)
                throw new ArgumentException($"{mw / 1000.0:0.#} W is outside {CpuPowerLimit.MinMW / 1000} to {now.MaxMW / 1000} W");
            Directory.CreateDirectory(Path.GetDirectoryName(_savedPath)!);
            var temp = _savedPath + ".tmp";
            using (var stream = OsFiles.CreatePrivate(temp)) JsonSerializer.Serialize(stream, new Kept(mw));
            File.Move(temp, _savedPath, overwrite: true);
        }
        else if (File.Exists(_savedPath)) File.Delete(_savedPath);
    }

    private sealed record Kept(long MilliWatts);

    public void Dispose() => _hsmp?.Dispose();
}
