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
/// Root-only reads: <c>nvmesmart</c>, <c>rapl</c> (the RAPL package and core energy counters), <c>ddr5</c> (each DIMM's
/// thermal sensors and PMIC; the PMIC's ADC select R30 is the one register set, and restored) and <c>vr</c> (this
/// board's known PMBus VR, reads only) and <c>bmc</c> (the BMC's threshold sensors over IPMI, read-only commands; see
/// <see cref="BmcReader"/>) — none takes arguments: buses and addresses come from sysfs and fixed
/// tables — and the NVIDIA GPUs for the window, so the app never opens a card: <c>gpus</c> (the rows),
/// <c>gpuread</c> (their values) and <c>gpulimits</c> (the Power Limits window's list), as <see cref="GpuWire"/>.
/// The card filter (<see cref="CardFilter"/>): <c>cardfilter</c> (status, holders), <c>cardfilter on|off</c>,
/// <c>cardselect &lt;pci|none&gt;</c>, <c>cardhide|cardshow &lt;pattern&gt; &lt;now|boot|both&gt; [name]</c>,
/// <c>cardforget &lt;pattern&gt;</c> — patterns are cgroup unit names with * wildcards, PCI addresses must be listed cards.
/// <c>lendinstall</c> / <c>lenduninstall</c> put GPU Lending's filter and system files in place or take them out
/// (<see cref="LendingSetup"/>; refused while the card is lent or a lend/return is under way).
/// Lending (<see cref="CardLending"/>): <c>lendstatus</c>, <c>cardvms</c>, <c>cardvm &lt;name|none&gt;</c> (a VM with the card
/// assigned), <c>lend</c> and <c>vmreturn</c> (both start in the background; follow them with <c>lendstatus</c>).
/// Fans (protocol 13, <see cref="FanDriver"/>): <c>fans [token]</c> (status), <c>fanset &lt;token&gt; &lt;zone&gt; &lt;1–100&gt;</c>,
/// <c>fanrelease &lt;token&gt; &lt;zone|all&gt;</c>, <c>fanbeat &lt;token&gt;</c>; configured curves <c>fancurve &lt;zone&gt; &lt;t:d,…&gt;</c>,
/// <c>fanoriginal &lt;zone&gt;</c>. Protocol 14: <c>fansources</c>, <c>fansource &lt;zone&gt; &lt;mask&gt;</c> (Redfish),
/// <c>bmclogin [base64 user, base64 password]</c>, <c>bmclogout</c>. Protocol 15, NVIDIA fans (<see cref="GpuFanDriver"/>):
/// <c>gpufans [token]</c>, <c>gpufanset &lt;token&gt; &lt;nvml:uuid:fan:n&gt; &lt;0–100&gt;</c> (0 = the card's automatic control),
/// <c>gpufanrelease &lt;token&gt; &lt;id|all&gt;</c>; <c>fanbeat</c> renews both kinds of lease.
/// Protocol 16, read-only BMC: <c>bmcstates</c> (discrete sensors' asserted states), <c>bmcsel</c> (the System Event Log).
/// Protocol 17, fan alarm thresholds: <c>bmcthresh</c> (list), <c>bmcthresh &lt;sensor&gt; &lt;lnr&gt; &lt;lcr&gt; &lt;lnc&gt;</c> (RPM),
/// <c>bmcthreshoriginal &lt;sensor&gt;</c>. Protocol 18, Redfish reads: <c>biossettings</c>, <c>bmcaccess</c>.
/// Replies are "ok …" or "err …".
/// </summary>
public sealed partial class HelperSession(INvmeSmartReader smart, CpuFrequencyLimits? cpufreq = null, GpuPowerLimits? gpuPower = null, CpuPowerLimits? cpuPower = null, BootPowerLimits? boot = null, RaplReader? rapl = null, Ddr5Reader? ddr5 = null, BoardVrReader? vr = null, SharedGpuEngine? gpus = null, CardFilter? card = null, CardLending? lending = null, BmcReader? bmc = null, FanDriver? fans = null, BmcRedfish? redfish = null, string loginPath = BmcLogin.DefaultPath, GpuFanDriver? gpuFans = null)
{
    public const string Version = "18";

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
                    boot.SetGpu(gpu.Uuid, watts);
                    return "ok " + (boot.GpuBoot(gpuPower.Read()).TryGetValue(gpu.Uuid, out var b) ? (b.MilliWatts / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) : "none");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
            }

            case "rapl" when parts.Length == 1: // read-only: the package and core energy counters
                if (rapl is null) return "err RAPL unavailable";
                var counters = rapl.Read();
                return counters.Count == 0 ? "err no RAPL counters" : "ok " + RaplReader.Format(counters);

            case "ddr5" when parts.Length == 1:
                if (ddr5 is null) return "err DDR5 sideband unavailable";
                try
                {
                    var dimms = ddr5.Read();
                    return dimms.Count == 0 ? "err no DDR5 SPD hubs" : "ok " + Ddr5Reader.Format(dimms);
                }
                catch (IOException ex) { return "err " + ex.Message; }

            case "vr" when parts.Length == 1:
                if (vr?.Read() is not { } sample) return "err no known board VR";
                return "ok " + BoardVrReader.Format(sample);

            case "bmc" when parts.Length == 1: // read-only: the board management controller's threshold sensors (IPMI)
                if (bmc is null) return "err no BMC";
                return bmc.Read(TimeSpan.FromSeconds(20)) is { } reply ? "ok " + GpuWire.Encode(reply) : "err " + (bmc.Error ?? "BMC not ready");

            case "bmcstates" when parts.Length == 1: // read-only (protocol 16): discrete sensors' asserted states
                if (bmc is null) return "err no BMC";
                return bmc.States(TimeSpan.FromSeconds(20)) is { } states ? "ok " + GpuWire.Encode(states) : "err " + (bmc.Error ?? "BMC not ready");

            case "bmcsel" when parts.Length == 1: // read-only (protocol 16): the System Event Log's newest entries
                if (bmc is null) return "err no BMC";
                try { return bmc.Sel(TimeSpan.FromSeconds(20)) is { } sel ? "ok " + GpuWire.Encode(sel) : "err " + (bmc.Error ?? "BMC not ready"); }
                catch (IOException ex) { return "err " + ex.Message; }

            // Fan alarm thresholds (protocol 17): the lower three of an RPM sensor, in RPM; originals kept for restore.
            case "bmcthresh" when parts.Length == 1:
                if (bmc is null) return "err no BMC";
                return bmc.FanThresholds(TimeSpan.FromSeconds(20)) is { } list ? "ok " + GpuWire.Encode(list) : "err " + (bmc.Error ?? "BMC not ready");

            case "bmcthresh" when parts.Length == 5:
            {
                if (bmc is null) return "err no BMC";
                if (!BmcSensorName().IsMatch(parts[1])) return "err invalid sensor name";
                var rpm = new double[3];
                for (var i = 0; i < 3; i++)
                    if (!Rpm().IsMatch(parts[i + 2])) return "err expected RPM values";
                    else rpm[i] = double.Parse(parts[i + 2], System.Globalization.CultureInfo.InvariantCulture);
                try { return "ok " + GpuWire.Encode(bmc.SetFanThresholds(parts[1], rpm[0], rpm[1], rpm[2])); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }
            }

            // Redfish reads (protocol 18): Setup's published options, and whether the BMC lets this PC in without a login.
            case "biossettings" when parts.Length == 1:
                if (redfish is null) return "err Redfish unavailable";
                try { return "ok " + GpuWire.Encode(redfish.ReadBiosSettings()); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return "err " + ex.Message; }

            case "bmcaccess" when parts.Length == 1:
                if (redfish is null) return "err Redfish unavailable";
                try { return "ok " + GpuWire.Encode(redfish.ReadAccess()); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return "err " + ex.Message; }

            case "bmcthreshoriginal" when parts.Length == 2:
                if (bmc is null) return "err no BMC";
                if (!BmcSensorName().IsMatch(parts[1])) return "err invalid sensor name";
                try { return "ok " + GpuWire.Encode(bmc.RestoreFanThresholds(parts[1])); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }

            // Fan control (protocol 13): the one writer of fan settings. A client names itself with a random 32-hex
            // token; its zones go back to their firmware curves when it releases them or stops renewing (15 s).
            case "fans" when parts.Length is 1 or 2:
                if (fans is null) return "err fan control unavailable";
                try { return "ok " + GpuWire.Encode(fans.Status(parts.Length == 2 ? parts[1] : null)); }
                catch (ArgumentException ex) { return "err " + ex.Message; }

            case "fancurve" when parts.Length == 3: // a zone's own curve, kept and run by the BMC: "fancurve bmc:1 20:20,45:40,65:70,70:100,100:100"
                if (fans is null) return "err fan control unavailable";
                try { return "ok " + fans.SetCurve(parts[1], FanDriver.ParseCurve(parts[2])); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { return "err " + ex.Message; }

            case "fanoriginal" when parts.Length == 2:
                if (fans is null) return "err fan control unavailable";
                try { return "ok " + fans.RestoreOriginal(parts[1]); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { return "err " + ex.Message; }

            // Temperature sources (protocol 14): Redfish, as IPMI's source command is refused on this firmware. Reading
            // needs no login over the host interface; writing uses the stored BMC login.
            case "fansources" when parts.Length == 1:
                if (redfish is null) return "err BMC Redfish unavailable";
                try { return "ok " + GpuWire.Encode(redfish.ReadSources()); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return "err " + ex.Message; }

            case "fansource" when parts.Length == 3:
                if (redfish is null) return "err BMC Redfish unavailable";
                if (FanZone().Match(parts[1]) is not { Success: true } zm || !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mask))
                    return "err expected a fan zone and a source bitmask";
                if (fans?.Status(null).Zones.ElementAtOrDefault(zm.Groups[1].Value[0] - '0') is { Owned: true }) return "err " + parts[1] + " is taken over by a client right now";
                try { redfish.SetSource(zm.Groups[1].Value[0] - '0', mask); return "ok " + parts[1] + " " + parts[2]; }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return "err " + ex.Message; }

            // The BMC login for Redfish writes: checked by opening a session, then kept root-only. No reply ever
            // contains the password; "bmclogin" alone says which user is stored.
            case "bmclogin" when parts.Length == 1:
                return "ok " + (BmcLogin.Load(loginPath)?.User is { Length: > 0 } stored ? stored : "none");

            case "bmclogin" when parts.Length == 3:
            {
                if (redfish is null) return "err BMC Redfish unavailable";
                string user, password;
                try { (user, password) = (Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])), Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]))); }
                catch (FormatException) { return "err expected base64 user and password"; }
                if (user.Length is 0 or > 32 || password.Length is < 8 or > 20 || user.Any(char.IsControl) || password.Any(char.IsControl))
                    return "err a BMC user is 1–32 characters and its password 8–20";
                var candidate = new BmcLogin(user, password);
                try { redfish.CheckLogin(candidate); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return "err " + ex.Message; }
                try { candidate.Save(loginPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
                return "ok " + user;
            }

            case "bmclogout" when parts.Length == 1:
                try { File.Delete(loginPath); return "ok none"; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }

            case "fanset" or "fanrelease" or "fanbeat":
                if (fans is null) return "err fan control unavailable";
                try
                {
                    return (parts[0], parts.Length) switch
                    {
                        ("fanset", 4) when int.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pct)
                            => "ok " + fans.Set(parts[1], parts[2], pct),
                        ("fanrelease", 3) => "ok " + fans.Release(parts[1], parts[2]),
                        ("fanbeat", 2) => "ok " + (fans.Beat(parts[1]) + (gpuFans?.Beat(parts[1]) ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => "err unknown command",
                    };
                }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { return "err " + ex.Message; }

            // NVIDIA fans (protocol 15): same client model as the BMC fans; NVML keeps nothing, so no write budget.
            case "gpufans" when parts.Length is 1 or 2:
                if (gpuFans is null) return "err GPU fan control unavailable";
                try { return "ok " + GpuWire.Encode(gpuFans.Status(parts.Length == 2 ? parts[1] : null)); }
                catch (ArgumentException ex) { return "err " + ex.Message; }

            case "gpufanset" when parts.Length == 4:
            case "gpufanrelease" when parts.Length == 3:
                if (gpuFans is null) return "err GPU fan control unavailable";
                try
                {
                    if (parts[0] == "gpufanrelease") return "ok " + gpuFans.Release(parts[1], parts[2]);
                    return int.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var duty)
                        ? "ok " + gpuFans.Set(parts[1], parts[2], duty) : "err expected a duty 0–100";
                }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }

            case "gpus" when parts.Length == 1:
                return gpus is null ? "err GPUs unavailable" : "ok " + GpuWire.Encode(gpus.Discover());

            case "gpuread" when parts.Length == 1:
                return gpus is null ? "err GPUs unavailable" : "ok " + GpuWire.Encode(gpus.Read());

            case "gpulimits" when parts.Length == 1:
                return gpuPower is null ? "err GPU power limits unavailable" : "ok " + GpuWire.Encode(gpuPower.Read());

            case "lendstatus" or "cardvms" or "cardvm" or "lend" or "vmreturn":
                if (lending is null) return "err lending unavailable";
                try
                {
                    return (parts[0], parts.Length) switch
                    {
                        ("lendstatus", 1) => "ok " + GpuWire.Encode(lending.Status()),
                        ("cardvms", 1) => "ok " + GpuWire.Encode(lending.Vms()),
                        ("cardvm", 2) when parts[1] == "none" || CardLending.VmName().IsMatch(parts[1]) =>
                            Done(() => lending.SelectVm(parts[1] == "none" ? null : parts[1]), parts[1]),
                        ("lend", 1) => Done(lending.Lend, "lending"),
                        ("vmreturn", 1) => Done(lending.Return, "returning"),
                        _ => "err unknown command",
                    };
                }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }

            case "lendinstall" or "lenduninstall" when parts.Length == 1:
                if (card is null) return "err card filter unavailable";
                try
                {
                    if (parts[0] == "lenduninstall" && lending?.Status() is { } l && (l.Lent || l.Stage is not ("idle" or "lent" or "failed" or "returned")))
                        return "err return the card first";
                    var changes = card.SetInstalled(parts[0] == "lendinstall");
                    return "ok " + (changes.Count == 0 ? "nothing to change" : string.Join("; ", changes));
                }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }

            case "cardfilter" or "cardselect" or "cardhide" or "cardshow" or "cardforget":
                if (card is null) return "err card filter unavailable";
                try { return CardCommand(card, parts, line); }
                catch (ArgumentException ex) { return "err " + ex.Message; }
                catch (IOException ex) { return "err " + ex.Message; }

            case "cpufreqrestore" when parts.Length == 1:
                if (cpufreq is null) return "err cpufreq unavailable";
                try { return "ok " + cpufreq.RestoreHardwareMaximum(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "err " + ex.Message; }
                catch (ArgumentException ex) { return "err " + ex.Message; }

            default:
                return "err unknown command";
        }
    }

    private static string Done(Action action, string reply)
    {
        action();
        return "ok " + reply;
    }

    private static string CardCommand(CardFilter card, string[] parts, string line)
    {
        switch (parts[0], parts.Length)
        {
            case ("cardfilter", 1):
                return "ok " + GpuWire.Encode(card.Status());
            case ("cardfilter", 2) when parts[1] is "on" or "off":
                card.SetEnabled(parts[1] == "on");
                return "ok " + parts[1];
            case ("cardselect", 2):
                if (parts[1] != "none" && !PciAddress().IsMatch(parts[1])) return "err invalid PCI address";
                return "ok " + card.SelectCard(parts[1] == "none" ? null : parts[1]);
            case ("cardhide" or "cardshow", >= 3) when parts[2] is "now" or "boot" or "both":
            {
                // Optional name: the rest of the line, printable ASCII, at most 60 characters.
                var name = parts.Length > 3 ? string.Join(' ', parts.Skip(3)) : null;
                if (name is not null && (name.Length > 60 || name.Any(c => c < 0x20 || c > 0x7e))) return "err invalid name";
                card.SetHidden(parts[1], parts[2] != "boot", parts[2] != "now", parts[0] == "cardhide", name);
                return "ok " + parts[1];
            }
            case ("cardforget", 2):
                if (!CardPatterns.IsValid(parts[1])) return "err invalid pattern";
                card.Forget(parts[1]);
                return "ok " + parts[1];
        }
        return "err unknown command";
    }

    [GeneratedRegex(@"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$", RegexOptions.CultureInvariant)] private static partial Regex PciAddress();
    [GeneratedRegex(@"^bmc:([0-6])$", RegexOptions.CultureInvariant)] private static partial Regex FanZone();
    [GeneratedRegex(@"^[A-Za-z0-9_.+]{1,32}$", RegexOptions.CultureInvariant)] private static partial Regex BmcSensorName();
    [GeneratedRegex(@"^[0-9]{1,5}$", RegexOptions.CultureInvariant)] private static partial Regex Rpm();
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
public sealed class HelperServer(INvmeSmartReader smart, string socketPath, CpuFrequencyLimits? cpufreq = null, GpuPowerLimits? gpuPower = null, CpuPowerLimits? cpuPower = null, BootPowerLimits? boot = null, RaplReader? rapl = null, Ddr5Reader? ddr5 = null, BoardVrReader? vr = null, SharedGpuEngine? gpus = null, CardFilter? card = null, CardLending? lending = null, BmcReader? bmc = null, FanDriver? fans = null, BmcRedfish? redfish = null, GpuFanDriver? gpuFans = null) : IAsyncDisposable
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
        var session = new HelperSession(smart, cpufreq, gpuPower, cpuPower, boot, rapl, ddr5, vr, gpus, card, lending, bmc, fans, redfish, gpuFans: gpuFans);
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
