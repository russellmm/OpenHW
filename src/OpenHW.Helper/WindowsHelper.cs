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
        // Each pipe client is served on the thread pool and some requests block (IPMI through WMI, Redfish): with the
        // default minimum, a few slow ones made the pool add threads one by one and quick requests ("bmc") waited
        // seconds behind them. Threads up to this count start at once.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 32));
        switch (args)
        {
            case ["--ipmi-check"]: return IpmiCheck();
            case ["--hsmp-check"]: return HsmpCheck();
            case ["--smbus-scan"]: return SmbusScan();
            case ["--smbus-switch"]: return SmbusSwitch();
            case ["--vr-page-test"]: return VrPageTest();
            case ["--spd-scan", var port] when int.TryParse(port, out var p) && p is >= 0 and <= 4: return SpdScan(p);
            case ["--i2c-read", var c, var a, var r] when int.TryParse(c, out var controller) && TryHex(a, out var address) && TryHex(r, out var register):
                return I2cRead(controller, address, register);
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

        // Every request is a new pipe connection and so a new session: building one must not touch the BMC (it used to
        // ask the fan driver for its status, which reads the zones over IPMI, just to decide whether to offer Redfish).
        public WindowsHelperSession Session() => new(
            new HelperSession(_smart, gpuPower: _gpuPower, bmc: _bmc, fans: _fans,
                redfish: _fans is not null || _oneShot is "fansources" or "biossettings" or "bmcaccess" ? _redfish : null, // one-shot reads as on Linux
                loginPath: LoginPath, gpuFans: _gpuFans, vr: _vr),
            _gpuPower, _savedGpuPower, _cpuPower, _oneShot is null ? _sources : null, _hsmpSensors, _kernel, _rapl, _clocks, _vr);

        private readonly WindowsEffectiveClocks _clocks = new(() => PawnIo.Open("AMDFamily17", out _));

        private readonly WindowsRapl _rapl = new(() => PawnIo.Open("AMDFamily17", out _));

        private readonly WindowsKernelSensors _kernel = new();

        // The chipset SMBus through PawnIO (null without it): the board VR now, the DIMMs next.
        private static readonly WindowsSmbusProvider? Smbus = WindowsSmbusProvider.Open(out _);
        private readonly BoardVrReader? _vr = Smbus is null ? null
            : new BoardVrReader(Smbus, BoardVrs.ForWindows(WindowsMachine.BoardVendor, WindowsMachine.BoardName), adapterNumber: 0,
                // The WRX90E leaves PAGE on 0: set the rail's page, read and restore, holding the shared SMBus mutex
                // for the whole read so no other tool sees the other page (BoardVrReader; tested with the owner 2026-11-18).
                canSwitchPage: BoardVrs.WindowsKnown.Any(v => v.Matches(WindowsMachine.BoardVendor, WindowsMachine.BoardName)),
                exclusive: read => HardwareMutex.Hold(HardwareMutex.Smbus, read));

        private readonly WindowsHsmpSensors _hsmpSensors = new(() => WindowsHsmp.Open(out _));

        /// <summary>The fans' Redfish sources, read once and kept while the service runs (see <see cref="FanSourcesCache"/>).</summary>
        private readonly FanSourcesCache _sources = new();

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
            _hsmpSensors.Dispose();
            _kernel.Dispose();
            _rapl.Dispose();
            _clocks.Dispose();
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

    /// <summary>
    /// Read-only map of the chipset SMBus ports 0 and 1: which addresses answer a receive-byte (as i2cdetect -r), each
    /// DDR5 hub's MR0 (0x51 = SPD5118) at 0x50–0x57, each PMIC's vendor at 0x48–0x4F, and the PMBus IC_DEVICE_ID at
    /// 0x20 (the VR's address on the TRX50-SAGE). Nothing is written.
    /// </summary>
    private static int SmbusScan()
    {
        using var buses = WindowsSmbusProvider.Open(out var reason);
        if (buses is null) { Console.WriteLine(reason); return 1; }
        foreach (var port in new[] { 0, 1 })
        {
            var bus = (WindowsSmbus)buses.Open(port);
            var found = new List<int>();
            for (var a = 0x08; a <= 0x77; a++)
                try { bus.ReceiveByte(a); found.Add(a); } catch (IOException) { }
            Console.WriteLine($"port {port}: {(found.Count == 0 ? "nothing" : string.Join(' ', found.Select(a => a.ToString("x2"))))}");
            foreach (var a in found)
            {
                string Try(Func<string> read) { try { return read(); } catch (IOException ex) { return "(" + ex.Message + ")"; } }
                if (a is >= 0x50 and <= 0x57) Console.WriteLine($"  0x{a:x2} hub MR0 = {Try(() => bus.ReadByte(a, 0x00).ToString("x2"))}");
                if (a is >= 0x48 and <= 0x4F) Console.WriteLine($"  0x{a:x2} PMIC vendor = {Try(() => $"{bus.ReadByte(a, 0x3C):x2}{bus.ReadByte(a, 0x3D):x2}")}");
                if (a == 0x20) Console.WriteLine($"  0x20 IC_DEVICE_ID = {Try(() => Convert.ToHexString(bus.ReadBlock(a, 0xAD)))}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Owner-approved one-shot (2026-11-18, owner present): the WRX90E's XDPE19283B (port 0, 0x20) is left on PAGE 0
    /// (an unused loop, POWER_GOOD# set), so the memory rail on PAGE 1 never shows. Confirms IC_DEVICE_ID, reads PAGE
    /// (stops unless it is 0), prints page 0, writes PAGE = 1 once, prints page 1, and always writes the original PAGE
    /// back and reads it to confirm. One documented register, no other writes.
    /// </summary>
    private static int VrPageTest()
    {
        using var buses = WindowsSmbusProvider.Open(out var reason);
        if (buses is null) { Console.WriteLine(reason); return 1; }
        var vr = BoardVrs.WindowsKnown[0];
        var bus = buses.Open(0);
        const int a = 0x20;
        var id = bus.ReadBlock(a, 0xAD);
        Console.WriteLine($"IC_DEVICE_ID = {Convert.ToHexString(id)}");
        if (!id.SequenceEqual(vr.DeviceId)) { Console.WriteLine("not the expected chip: stopping"); return 1; }
        var original = bus.ReadByte(a, 0x00);
        Console.WriteLine($"PAGE = {original}");
        if (original != 0) { Console.WriteLine("PAGE isn't 0 as seen before: stopping without writing"); return 1; }
        PmbusSample Sample() => new(vr.Slug, bus.ReadByte(a, 0x00), bus.ReadByte(a, 0x20),
            bus.ReadWord(a, 0x88), bus.ReadWord(a, 0x89), bus.ReadWord(a, 0x8B), bus.ReadWord(a, 0x8C),
            bus.ReadWord(a, 0x8D), bus.ReadWord(a, 0x8E), bus.ReadWord(a, 0x96), bus.ReadWord(a, 0x97), bus.ReadWord(a, 0x79));
        void Print(string title, PmbusSample s)
        {
            Console.WriteLine($"{title}: {BoardVrReader.Format(s)}");
            foreach (var (key, value) in BoardVrEngine.Values(s)) Console.WriteLine($"  {key,-10} {value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
        }
        Print("page 0", Sample());
        try
        {
            bus.WriteByte(a, 0x00, 1);
            Print("page 1", Sample());
        }
        finally
        {
            bus.WriteByte(a, 0x00, original);
            Console.WriteLine($"PAGE restored to {bus.ReadByte(a, 0x00)}");
        }
        return 0;
    }

    /// <summary>
    /// Owner-approved (2026-11-18): the device at port 1 address 0x73, probably a PCA954x I2C switch in front of the
    /// other DIMMs. Reads its channel mask, then for each single channel writes that mask, lists what answers in the
    /// DIMM ranges (thermal sensors 0x10–0x17, PMICs 0x48–0x4F, hubs 0x50–0x57) with each hub's MR0–MR7, and finally
    /// writes the original mask back. The port-1 baseline (switch untouched) comes first.
    /// </summary>
    private static int SmbusSwitch()
    {
        using var buses = WindowsSmbusProvider.Open(out var reason);
        if (buses is null) { Console.WriteLine(reason); return 1; }
        var bus = (WindowsSmbus)buses.Open(1);
        string Try(Func<string> read) { try { return read(); } catch (IOException ex) { return "(" + ex.Message + ")"; } }
        void Dimms(string title)
        {
            var found = new List<int>();
            foreach (var a in Enumerable.Range(0x10, 8).Concat(Enumerable.Range(0x48, 16)))
                try { bus.ReceiveByte(a); found.Add(a); } catch (IOException) { }
            Console.WriteLine($"{title}: {(found.Count == 0 ? "nothing" : string.Join(' ', found.Select(a => a.ToString("x2"))))}");
            foreach (var a in found.Where(a => a is >= 0x50 and <= 0x57 or >= 0x10 and <= 0x17))
                Console.WriteLine($"  0x{a:x2} MR0-7 = {Try(() => string.Join(' ', Enumerable.Range(0, 8).Select(r => bus.ReadByte(a, (byte)r).ToString("x2"))))}");
        }
        Dimms("port 1, switch untouched");
        byte original;
        try { original = bus.ReceiveByte(0x73); }
        catch (IOException ex) { Console.WriteLine("0x73 doesn't answer a read: " + ex.Message); return 1; }
        Console.WriteLine($"0x73 channel mask now = 0x{original:x2}");
        try
        {
            for (var channel = 0; channel < 8; channel++)
            {
                var mask = (byte)(1 << channel);
                try { bus.SendByte(0x73, mask); }
                catch (IOException ex) { Console.WriteLine($"channel {channel}: refused ({ex.Message})"); continue; }
                Dimms($"channel {channel} (mask 0x{mask:x2}, reads back 0x{Try(() => bus.ReceiveByte(0x73).ToString("x2"))})");
            }
        }
        finally
        {
            bus.SendByte(0x73, original);
            Console.WriteLine($"0x73 restored to 0x{original:x2}, reads back 0x{Try(() => bus.ReceiveByte(0x73).ToString("x2"))}");
        }
        return 0;
    }

    /// <summary>
    /// Read-only, one FCH SMBus port: a receive-byte at each DDR5 hub address 0x50–0x57, then MR0/MR1 of the hubs
    /// that answer (an SPD5118 reads 51 18). Ports 2–4 are the main controller's other ports (port select, restored
    /// after every transaction); HWiNFO's debug log shows the DIMMs on hosts it numbers 1–3.
    /// </summary>
    private static int SpdScan(int port)
    {
        using var buses = WindowsSmbusProvider.Open(out var reason);
        if (buses is null) { Console.WriteLine(reason); return 1; }
        var bus = (WindowsSmbus)buses.Open(port);
        var found = new List<int>();
        for (var a = 0x50; a <= 0x57; a++)
            try { bus.ReceiveByte(a); found.Add(a); } catch (IOException) { }
        Console.WriteLine($"port {port} hubs: {(found.Count == 0 ? "none" : string.Join(' ', found.Select(a => a.ToString("x2"))))}");
        foreach (var a in found)
        {
            try { Console.WriteLine($"  0x{a:x2} MR0 MR1 = {bus.ReadByte(a, 0x00):x2} {bus.ReadByte(a, 0x01):x2}"); }
            catch (IOException ex) { Console.WriteLine($"  0x{a:x2}: {ex.Message}"); }
        }
        return 0;
    }

    private static bool TryHex(string text, out int value) =>
        int.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, System.Globalization.NumberStyles.HexNumber, null, out value);

    /// <summary>
    /// The gentle first test of AMDI2C (owner's rule, 2026-11-18: no sweeps): one controller, one address, two
    /// consecutive registers read (an SPD5118 hub's MR0/MR1 read 51 18). Nothing is written.
    /// </summary>
    private static int I2cRead(int controller, int address, int register)
    {
        using var i2c = WindowsI2cProvider.Open(out var reason);
        if (i2c is null) { Console.WriteLine(reason); return 1; }
        Console.WriteLine($"AMDI2C on the {(i2c.OnTestDriver ? "test" : "official")} PawnIO driver; controllers 0x{i2c.Controllers:x2}");
        var bus = i2c.Open(controller);
        foreach (var r in new[] { register, register + 1 })
            try { Console.WriteLine($"I2C{controller} 0x{address:x2} reg 0x{r:x2} = 0x{bus.ReadByte(address, (byte)r):x2}"); }
            catch (IOException ex) { Console.WriteLine($"I2C{controller} 0x{address:x2} reg 0x{r:x2}: {ex.Message}"); }
        return 0;
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
        AllowStartStop();
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

    /// <summary>
    /// Lets "OpenHW Users" and the installing account start, stop and query the service (Settings → Driver
    /// Management), without elevation: the service's own access list gets one entry each (SERVICE_START, _STOP,
    /// _QUERY_STATUS/CONFIG, _INTERROGATE, user-defined control, read). Nothing else changes.
    /// </summary>
    private static void AllowStartStop()
    {
        var sids = new List<string>();
        try { sids.Add(((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(HelperPipe.Group).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value); }
        catch (System.Security.Principal.IdentityNotMappedException) { }
        if (System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value is { } self) sids.Add(self);
        var start = new ProcessStartInfo("sc.exe", ["sdshow", ServiceName]) { UseShellExecute = false, RedirectStandardOutput = true };
        using var sdshow = Process.Start(start)!;
        var sddl = sdshow.StandardOutput.ReadToEnd().Trim();
        sdshow.WaitForExit();
        if (!sddl.StartsWith("D:", StringComparison.Ordinal)) { Console.WriteLine("Couldn't read the service's permissions: " + sddl); return; }
        var sacl = sddl.IndexOf("S:", StringComparison.Ordinal);
        var dacl = sacl >= 0 ? sddl[..sacl] : sddl;
        foreach (var sid in sids)
        {
            var ace = $"(A;;CCLCSWRPWPLOCRRC;;;{sid})";
            if (!dacl.Contains(ace, StringComparison.OrdinalIgnoreCase)) dacl += ace;
        }
        Sc("sdset", ServiceName, dacl + (sacl >= 0 ? sddl[sacl..] : ""));
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
