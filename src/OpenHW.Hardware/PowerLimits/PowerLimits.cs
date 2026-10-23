using System.Globalization;

namespace OpenHW.Hardware;

/// <summary>One NVIDIA card's power limit and the range NVML allows. Milliwatts.</summary>
public sealed record GpuPowerLimit(string Uuid, string Name, string BusId, long LimitMW, long DefaultMW, long MinMW, long MaxMW, long? DrawMW);

/// <summary>
/// The CPU socket power limit (PPT), milliwatts. <see cref="MaxMW"/> is what the SMU itself reports it will accept
/// (HSMP GET_SOCKET_POWER_LIMIT_MAX); the floor is OpenHW's, well under any idle draw. Firmware may still clamp a
/// value inside that range, which the readback in <see cref="CpuPowerLimits.Set"/> reports. The active frequency
/// limit and its source (e.g. "OPN Max", "PPT", thermal) say whether the power limit is what holds the cores back.
/// </summary>
public sealed record CpuPowerLimit(long LimitMW, long MaxMW, long? DrawMW, long? BootMW, int? ActiveLimitMHz, string? LimitSource)
{
    public const long MinMW = 50_000;
}

/// <summary>
/// GPU power limits through NVML. Reads work for any user; <see cref="Set"/> needs root (openhw-helper). The limit
/// lasts until the driver reloads or the machine reboots.
/// </summary>
public sealed class GpuPowerLimits : IDisposable
{
    private readonly object _gate = new();
    private bool _initialised;

    public IReadOnlyList<GpuPowerLimit> Read()
    {
        lock (_gate)
        {
            if (!Init()) return [];
            var gpus = new List<GpuPowerLimit>();
            if (NvmlNative.nvmlDeviceGetCount_v2(out var count) != NvmlNative.Success) return gpus;
            for (uint i = 0; i < count; i++)
            {
                if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out var h) != NvmlNative.Success) continue;
                if (NvmlNative.nvmlDeviceGetPowerManagementLimit(h, out var limit) != NvmlNative.Success ||
                    NvmlNative.nvmlDeviceGetPowerManagementLimitConstraints(h, out var min, out var max) != NvmlNative.Success) continue;
                var defaultMW = NvmlNative.nvmlDeviceGetPowerManagementDefaultLimit(h, out var d) == NvmlNative.Success ? d : max;
                var pci = new NvmlNative.PciInfo();
                var bus = NvmlNative.nvmlDeviceGetPciInfo(h, ref pci) == NvmlNative.Success ? pci.busId : "";
                gpus.Add(new GpuPowerLimit(
                    NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetUUID(h, b, n)),
                    NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetName(h, b, n)),
                    bus, limit, defaultMW, min, max,
                    NvmlNative.nvmlDeviceGetPowerUsage(h, out var draw) == NvmlNative.Success ? draw : null));
            }
            return gpus;
        }
    }

    /// <summary>
    /// Root only. Sets the card with this UUID to <paramref name="milliwatts"/> (null = its default limit), which must
    /// lie within NVML's constraints for that card, then reads it back. Throws <see cref="ArgumentException"/> for an
    /// unknown card or out-of-range value and <see cref="IOException"/> when NVML refuses or doesn't keep the value.
    /// </summary>
    public long Set(string uuid, long? milliwatts)
    {
        lock (_gate)
        {
            var gpu = Read().FirstOrDefault(g => g.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException("no such GPU");
            var target = milliwatts ?? gpu.DefaultMW;
            if (target < gpu.MinMW || target > gpu.MaxMW)
                throw new ArgumentException($"{target / 1000.0:0.#} W is outside {gpu.MinMW / 1000.0:0.#} to {gpu.MaxMW / 1000.0:0.#} W for {gpu.Name}");
            var handle = Handle(gpu.Uuid);
            var rc = NvmlNative.nvmlDeviceSetPowerManagementLimit(handle, (uint)target);
            if (rc != NvmlNative.Success)
                throw new IOException(rc == NvmlNative.ErrorNoPermission ? "NVML: permission denied (needs root)" : $"NVML refused the limit (error {rc})");
            if (NvmlNative.nvmlDeviceGetPowerManagementLimit(handle, out var now) != NvmlNative.Success || now != target)
                throw new IOException($"NVML reports {now / 1000.0:0.#} W after setting {target / 1000.0:0.#} W");
            return target;
        }
    }

    private nint Handle(string uuid)
    {
        NvmlNative.nvmlDeviceGetCount_v2(out var count);
        for (uint i = 0; i < count; i++)
            if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out var h) == NvmlNative.Success &&
                NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetUUID(h, b, n)) == uuid) return h;
        throw new ArgumentException("no such GPU");
    }

    private bool Init()
    {
        if (_initialised) return true;
        try { _initialised = NvmlNative.nvmlInit_v2() == NvmlNative.Success; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _initialised = false; }
        return _initialised;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_initialised) NvmlNative.nvmlShutdown();
            _initialised = false;
        }
    }
}

/// <summary>
/// The CPU socket power limit through amd_hsmp_hwmon's <c>power1_cap</c> (µW), which the kernel turns into HSMP
/// SET_SOCKET_POWER_LIMIT. Readable by anyone; <see cref="Set"/> needs root. The live limit lasts until reboot, when
/// firmware (and hsmp-control-apply.service, from <c>/etc/hsmp-control/ppt_mw</c>) sets it again.
/// </summary>
public sealed class CpuPowerLimits(string hwmonRoot = "/sys/class/hwmon", string bootConfig = "/etc/hsmp-control/ppt_mw", Action<string, long>? write = null)
{
    private readonly Action<string, long> _write = write ?? ((path, value) =>
        File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture) + "\n"));

    private string? HwmonDir()
    {
        if (!Directory.Exists(hwmonRoot)) return null;
        return Directory.EnumerateDirectories(hwmonRoot).Order()
            .FirstOrDefault(d => SysFs.ReadText(Path.Combine(d, "name")) == "amd_hsmp_hwmon" && File.Exists(Path.Combine(d, "power1_cap")));
    }

    public CpuPowerLimit? Read()
    {
        if (HwmonDir() is not { } dir || ReadLong(Path.Combine(dir, "power1_cap")) is not { } capUW) return null;
        var device = Path.Combine(dir, "device"); // the HSMP platform device: frequency-limit attributes live here
        var source = SysFs.ReadText(Path.Combine(device, "pwr_current_active_freq_limit_source"));
        return new CpuPowerLimit(capUW / 1000, (ReadLong(Path.Combine(dir, "power1_cap_max")) ?? capUW) / 1000,
            ReadLong(Path.Combine(dir, "power1_input")) / 1000, ReadLong(bootConfig),
            (int?)ReadLong(Path.Combine(device, "pwr_current_active_freq_limit")), source.Length > 0 ? source : null);
    }

    /// <summary>
    /// Root only. Sets the socket limit to <paramref name="milliwatts"/> (null = the boot value from hsmp-control's
    /// config), between <see cref="CpuPowerLimit.MinMW"/> and the SMU's reported maximum, and reads it back.
    /// </summary>
    public long Set(long? milliwatts)
    {
        var now = Read() ?? throw new IOException("amd_hsmp_hwmon is not available");
        var target = milliwatts ?? ReadLong(bootConfig) ?? throw new ArgumentException($"no boot value in {bootConfig}");
        if (target < CpuPowerLimit.MinMW || target > now.MaxMW)
            throw new ArgumentException($"{target / 1000.0:0.#} W is outside {CpuPowerLimit.MinMW / 1000} to {now.MaxMW / 1000} W");
        var path = Path.Combine(HwmonDir()!, "power1_cap");
        _write(path, target * 1000);
        var kept = ReadLong(path);
        if (kept != target * 1000) throw new IOException($"the CPU reports {kept / 1e6:0.#} W after setting {target / 1000.0:0.#} W (firmware clamp?)");
        return target;
    }

    private static long? ReadLong(string path) =>
        long.TryParse(SysFs.ReadText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}
