using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using OpenHW.Hardware;
using OpenHW.Hardware.Windows;

namespace OpenHW.Helper;

/// <summary>
/// openhw-helper on Windows: the "OpenHW Helper" service (LocalSystem), the twin of the Linux root daemon. It serves the
/// same <see cref="HelperSession"/> commands on the named pipe \\.\pipe\openhw-helper for the things Windows reserves for
/// Administrator: the BMC over IPMI (Microsoft_IPMI through WMI) — sensors, discrete states, SEL, fan zones, curves and
/// alarm thresholds — Redfish with the stored BMC login, NVIDIA fan control and GPU power limits (NVML writes).
/// Its state (fan journal, saved originals, BMC login, certificate pin) is in %ProgramData%\OpenHW, readable by SYSTEM
/// and Administrators only.
/// <list type="bullet">
/// <item><c>openhw-helper</c> — run by the Service Control Manager.</item>
/// <item><c>openhw-helper --console</c> — the same in a console (elevated), until Ctrl+C.</item>
/// <item><c>openhw-helper --once &lt;command&gt;</c> — one command's reply on stdout (elevated), for checks.</item>
/// <item><c>openhw-helper --install</c> / <c>--uninstall</c> — register or remove the service and the "OpenHW Users"
/// group (elevated); --install adds the user who ran it to the group.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHelper
{
    public const string ServiceName = "OpenHWHelper";
    private static string StateDir => OsFiles.HelperStateDir;

    public static int Run(string[] args)
    {
        switch (args)
        {
            case ["--ipmi-check"]: return IpmiCheck();
            case ["--hsmp-check"]: return HsmpCheck();
            case ["--install"]: return Install();
            case ["--uninstall"]: return Uninstall();
            case ["--once", .. var command] when command.Length > 0:
            {
                using var host = new Host(oneShot: string.Join(' ', command));
                Console.WriteLine(host.Session().HandleLine(string.Join(' ', command)));
                return 0;
            }
            case ["--console"]:
            {
                using var host = new Host();
                host.Start();
                Console.WriteLine($"openhw-helper {Product} (protocol {HelperSession.Version}) listening on \\\\.\\pipe\\{HelperPipe.Name}" +
                                  (host.GroupAccess ? $" (group {HelperPipe.Group})" : " (administrators only: group missing)"));
                var stopping = new ManualResetEventSlim();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Set(); };
                stopping.Wait();
                return 0;
            }
            case []:
                ServiceBase.Run(new HelperService());
                return 0;
            default:
                Console.Error.WriteLine("usage: openhw-helper [--console | --once <command> | --install | --uninstall]");
                return 2;
        }
    }

    private static string Product => typeof(HelperSession).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";

    /// <summary>Everything the service owns; disposing it gives every held fan back (BMC curves, NVIDIA automatic).</summary>
    private sealed class Host : IDisposable
    {
        private readonly BmcReader _bmc;
        private readonly FanDriver? _fans;
        private readonly BmcRedfish? _redfish;
        private readonly GpuPowerLimits _gpuPower;
        private readonly LiveNvmlFans? _nvmlFans;
        private readonly GpuFanDriver? _gpuFans;
        private readonly WindowsNvmeSmartReader _smart = new();
        private PipeHelperServer? _server;

        private readonly string? _oneShot;

        public Host(string? oneShot = null)
        {
            _oneShot = oneShot;
            Directory.CreateDirectory(StateDir);
            var identity = WindowsMachine.Uuid;
            _bmc = new BmcReader(() => WmiIpmiTransport.Open(out _), Path.Combine(StateDir, "bmc-thresholds-original.json"), () => identity);
            _gpuPower = new GpuPowerLimits();
            // A one-shot process can't hold a fan (it would give it back as it exits): status only, as on Linux.
            if (oneShot is null || oneShot == "fans")
                _fans = new FanDriver(OpenFans, Path.Combine(StateDir, "fan-restore.json"), identity, startWatchdog: oneShot is null);
            if (oneShot is null)
            {
                _nvmlFans = new LiveNvmlFans();
                _gpuFans = new GpuFanDriver(_nvmlFans, startWatchdog: true);
            }
            if (_fans?.Status(null) is { Available: false, Reason: { } why }) Console.Error.WriteLine($"openhw-helper: fan control off: {why}");
            var redfishBase = RedfishBase();
            var handler = (SocketsHttpHandler)BmcRedfish.PinnedHandler(Path.Combine(StateDir, "bmc-cert.sha256"));
            LanBinding.Apply(handler, redfishBase);
            _redfish = new BmcRedfish(redfishBase, handler, () => BmcLogin.Load(LoginPath));
        }

        private static string LoginPath => Path.Combine(StateDir, "bmc-login");

        // Redfish: Windows has no driver for the BMC's USB host-interface NIC, so 169.254.0.17 is unreachable. The BMC's
        // LAN address instead: %ProgramData%\OpenHW\bmc-url if the owner set one ("https://10.0.0.200"), else the
        // address the BMC reports for itself over IPMI, else the host interface (works if its driver is installed).
        private static Uri RedfishBase()
        {
            var file = Path.Combine(StateDir, "bmc-url");
            try
            {
                if (File.Exists(file) && Uri.TryCreate(File.ReadAllText(file).Trim(), UriKind.Absolute, out var url) && url.Scheme == "https") return url;
            }
            catch (IOException) { }
            using var ipmi = WmiIpmiTransport.Open(out _);
            if (ipmi is not null && BmcLanAddress.Find(ipmi, a => LanBinding.LocalAddressOnSubnet(a) is not null) is { } address)
            {
                Console.Error.WriteLine($"openhw-helper: Redfish on the BMC's LAN address {address} (of {string.Join(", ", BmcLanAddress.All(ipmi))})");
                return new Uri($"https://{address}");
            }
            return BmcRedfish.HostInterface;
        }

        private static (AsusBmcFans? Fans, string? Reason) OpenFans()
        {
            var ipmi = WmiIpmiTransport.Open(out var reason);
            if (ipmi is null) return (null, reason);
            var fans = AsusBmcFans.Open(ipmi, WindowsMachine.BoardVendor, WindowsMachine.BoardName, out reason);
            if (fans is null) ipmi.Dispose();
            return (fans, reason);
        }

        private readonly SavedGpuPowerLimits _savedGpuPower = new();
        private readonly WindowsCpuPowerLimits _cpuPower = new(() => WindowsHsmp.Open(out _));

        public WindowsHelperSession Session() => new(
            new HelperSession(_smart, gpuPower: _gpuPower, bmc: _bmc, fans: _fans,
                redfish: _fans?.Status(null).Available == true || _oneShot is "fansources" or "biossettings" or "bmcaccess" ? _redfish : null, // one-shot reads as on Linux
                loginPath: LoginPath, gpuFans: _gpuFans),
            _gpuPower, _savedGpuPower, _cpuPower);

        public bool GroupAccess => _server?.GroupAccess ?? false;

        public void Start()
        {
            // The saved GPU power limits, as nvidia-power-limit.service does at boot on Linux (OpenFan did this on Windows).
            try { foreach (var line in _savedGpuPower.Apply(_gpuPower)) Console.Error.WriteLine("openhw-helper: GPU power limit at start: " + line); }
            catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException) { Console.Error.WriteLine("openhw-helper: GPU power limits: " + ex.Message); }
            // The kept CPU power limit, as hsmp-control-apply.service does at boot on Linux.
            if (_cpuPower.Saved() is { } cpu)
                try { Console.Error.WriteLine($"openhw-helper: CPU power limit at start: {_cpuPower.Set(cpu)} mW"); }
                catch (Exception ex) when (ex is IOException or ArgumentException) { Console.Error.WriteLine("openhw-helper: CPU power limit: " + ex.Message); }
            _server = new PipeHelperServer(() => Session().HandleLine);
            _server.Start();
        }

        public void Dispose()
        {
            _server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _gpuFans?.Dispose();
            _nvmlFans?.Dispose();
            _fans?.Dispose(); // every zone OpenHW drives goes back to its BMC curve
            _gpuPower.Dispose();
            _cpuPower.Dispose();
            _bmc.Dispose();
        }
    }

    private sealed class HelperService : ServiceBase
    {
        private Host? _host;

        public HelperService()
        {
            ServiceName = WindowsHelper.ServiceName;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            _host = new Host();
            _host.Start();
        }

        protected override void OnStop() => Stop_();
        protected override void OnShutdown() => Stop_();

        private void Stop_()
        {
            _host?.Dispose();
            _host = null;
        }
    }

    /// <summary>--ipmi-check (elevated): Get Device ID through WMI, the raw reply or the full error. Reads only.</summary>
    private static int IpmiCheck()
    {
        using var ipmi = WmiIpmiTransport.Open(out var reason);
        if (ipmi is null)
        {
            Console.WriteLine("no transport: " + reason);
            return 1;
        }
        try
        {
            var reply = ipmi.Send(0x06, 0x01, 0, []);
            Console.WriteLine("Get Device ID: " + Convert.ToHexString(reply));
            foreach (var (label, data, size) in new (string, byte[]?, uint)[] { ("null/0", null, 0), ("empty/0", [], 0), ("[0]/0", [0], 0), ("[0]/1", [0], 1) })
            {
                using var searcher = new System.Management.ManagementObjectSearcher(new System.Management.ManagementScope(@"root\WMI"), new System.Management.ObjectQuery("SELECT * FROM Microsoft_IPMI"));
                using var o = searcher.Get().Cast<System.Management.ManagementObject>().First();
                using var input = o.GetMethodParameters("RequestResponse");
                input["NetworkFunction"] = (byte)6; input["Command"] = (byte)1; input["Lun"] = (byte)0; input["ResponderAddress"] = (byte)0x20;
                input["RequestData"] = data; input["RequestDataSize"] = size;
                try
                {
                    using var output = o.InvokeMethod("RequestResponse", input, null);
                    Console.WriteLine($"{label}: cc={output["CompletionCode"]} size={output["ResponseDataSize"]} data={Convert.ToHexString(output["ResponseData"] as byte[] ?? [])}");
                }
                catch (Exception ex) { Console.WriteLine($"{label}: {ex.GetType().Name} {ex.Message.Trim()}"); }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// --hsmp-check (elevated): HSMP's test message (1: the SMU answers its argument + 1) through AMD's driver, then
    /// read-only queries — interface version (3), SMU firmware (2), socket power (4), power limit (6) and its maximum (7).
    /// Nothing is set.
    /// </summary>
    private static int HsmpCheck()
    {
        using var hsmp = WindowsHsmp.Open(out var reason);
        if (hsmp is null)
        {
            Console.WriteLine("no HSMP: " + reason);
            return 1;
        }
        try
        {
            var reply = hsmp.SendRaw(WindowsHsmp.Request(1, [0x1234], 1));
            Console.WriteLine("test 0x1234 -> reply " + Convert.ToHexString(reply));
            var answer = BitConverter.ToUInt32(reply, 8);
            if (answer != 0x1235)
            {
                Console.WriteLine($"unexpected answer 0x{answer:X}: the message layout isn't the one assumed; stopping.");
                return 1;
            }
            foreach (var (name, id) in new[] { ("interface version", 3u), ("SMU firmware", 2u), ("socket power mW", 4u), ("power limit mW", 6u), ("power limit max mW", 7u) })
                Console.WriteLine($"{name}: {hsmp.Send(id, [], 1)[0]}");
            return 0;
        }
        catch (IOException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    // ---- install / uninstall (elevated) ----

    private static int Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        Directory.CreateDirectory(StateDir);
        RestrictStateDir();
        var ok = Sc("create", ServiceName, "binPath=", $"\"{exe}\"", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "OpenHW Helper") == 0
                 || Sc("config", ServiceName, "binPath=", $"\"{exe}\"", "start=", "auto") == 0;
        if (!ok) return 1;
        Sc("description", ServiceName, "OpenHW's privileged reads and fan control: BMC (IPMI), NVIDIA fans and power limits.");
        // Restart after a crash: fans that OpenHW held go back to their curves when it starts again (journal).
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
        Run("net", "localgroup", HelperPipe.Group, "/add", "/comment:May use the OpenHW Helper service");
        var user = Environment.GetEnvironmentVariable("USERDOMAIN") is { Length: > 0 } domain ? $"{domain}\\{Environment.UserName}" : Environment.UserName;
        Run("net", "localgroup", HelperPipe.Group, user, "/add");
        // The group counts only from the next sign-in: allow this account by SID too, so OpenHW works right away.
        if (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value is { } sid)
        {
            var path = PipeHelperServer.AllowedUsersPath;
            var users = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            if (!users.Contains(sid)) File.WriteAllLines(path, [.. users, sid]);
        }
        Sc("start", ServiceName);
        Console.WriteLine($"Installed. {user} is in \"{HelperPipe.Group}\" (sign out and in again for the membership to apply).");
        return 0;
    }

    private static int Uninstall()
    {
        Sc("stop", ServiceName); // the service gives every held fan back as it stops
        var code = Sc("delete", ServiceName);
        Run("net", "localgroup", HelperPipe.Group, "/delete");
        return code;
    }

    // %ProgramData%\OpenHW: SYSTEM and Administrators only (ProgramData's default lets every user read).
    private static void RestrictStateDir()
    {
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(StateDir).SetAccessControl(security);
    }

    private static int Sc(params string[] args) => Run("sc.exe", args);

    private static int Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
    }
}
