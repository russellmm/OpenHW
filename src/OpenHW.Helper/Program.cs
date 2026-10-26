using System.Runtime.InteropServices;
using OpenHW.Hardware;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// openhw-helper — root daemon for the few things Linux reserves for root: NVMe SMART reads, cpufreq ceilings,
// GPU power limits (NVML), the CPU socket power limit (HSMP), and those limits' boot-time values in the owner's
// boot services' config; RAPL energy, the DDR5 DIMM and board VR sensors on the SMBus, and every NVIDIA GPU
// reading for the window (so the desktop app never opens a card). It serves fixed,
// validated commands over /run/openhw/helper.sock (root:openhw 0660); no client input becomes a path.
// `openhw-helper --once <command>` answers one command on stdout and exits (for checking a build with sudo).

if (Native.geteuid() != 0)
{
    Console.Error.WriteLine("openhw-helper must run as root (systemd unit: openhw-helper.service).");
    return 1;
}

// `openhw-helper --selftest-card-filter <pci>`: the card filter's kernel side, in a throwaway cgroup tree (as root).
if (args is ["--selftest-card-filter", var testCard])
    return CardFilterSelfTest.Run(testCard, Console.Out);

using var smart = new CachedSmartReader(new NativeNvmeSmartReader(), TimeSpan.FromSeconds(10));
var socketPath = HelperSocket.Path;
var smbus = new LinuxSmbusProvider();
var ddr5 = new Ddr5Reader(smbus);
var vr = new BoardVrReader(smbus, BoardVrs.ForThisBoard());
using var gpus = new SharedGpuEngine();
using var card = new CardFilter();

if (args is ["--once", .. var command] && command.Length > 0)
{
    using var limits = new GpuPowerLimits();
    // Only the card filter's status: changing it from a process that exits at once would leave the kernel filter
    // without the daemon that classifies new cgroups.
    var cardRead = command is ["cardfilter"] ? card : null;
    Console.WriteLine(new HelperSession(smart, gpuPower: limits, rapl: new RaplReader(), ddr5: ddr5, vr: vr, gpus: gpus, card: cardRead).HandleLine(string.Join(' ', command)));
    return 0;
}

using var gpuPower = new GpuPowerLimits();
// Lending the card to a VM: both NVML users let go of every card for the handoff, and open again afterwards.
using var lending = new CardLending(card, () => { gpus.Suspend(); gpuPower.Suspend(); }, () => { gpus.Resume(); gpuPower.Resume(); });
var server = new HelperServer(smart, socketPath, new CpuFrequencyLimits(), gpuPower, new CpuPowerLimits(), new BootPowerLimits(), new RaplReader(), ddr5, vr, gpus, card, lending);
// The card filter, if the owner enabled it: back in force (pinned program reused) before clients connect.
try { card.Start(); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"openhw-helper: card filter: {ex.Message}"); }
server.Start();
var shared = server.RestrictToGroup(HelperSocket.Group);
var product = typeof(HelperSession).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?";
Console.WriteLine($"openhw-helper {product} (protocol {HelperSession.Version}) listening on {socketPath}" +
                  (shared ? $" (group {HelperSocket.Group})" : $" (root only: group '{HelperSocket.Group}' missing)"));

var stopping = new ManualResetEventSlim();
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stopping.Set(); });
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stopping.Set(); });
stopping.Wait();

await server.DisposeAsync();
return 0;

internal static class Native
{
    [DllImport("libc")]
    public static extern uint geteuid();
}
