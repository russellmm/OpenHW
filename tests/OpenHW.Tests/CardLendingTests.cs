using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>
/// Lending against a fake machine: sysfs (the card's driver link, its connectors), /proc (who holds the card) and fake
/// virsh / nvidia-smi / systemctl that change that machine as the real ones would. The libvirt hook's part (VFIO)
/// is what "virsh start" simulates.
/// </summary>
public sealed class CardLendingTests : IDisposable
{
    private const string Card = "0000:e1:00.0", Vm = "Windows-10-LTSC";
    private readonly string _root = Directory.CreateTempSubdirectory("openhw-lending").FullName;
    private readonly FakeMachine _machine;
    private readonly CardFilter _filter;
    private int _suspends, _resumes;

    public CardLendingTests()
    {
        _machine = new FakeMachine(_root);
        _filter = new CardFilter(P("etc/card-filter.json"), P("run/now.json"), P("bpf"), P("cgroupfs/user.slice"), P("proc"), P("sys"), P("cgroupfs"));
        _filter.SelectCard(Card);
        _filter.SelectVm(Vm);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string P(string relative) => Path.Combine(_root, relative);

    private CardLending Lending() => new(_filter, () => _suspends++, () => _resumes++, _machine, P("sys"), P("run/handoff.json"),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), statePath: P("run/lend.json"));

    private static LendStatus WaitFor(CardLending lending, int sequence)
    {
        for (var i = 0; i < 200; i++)
        {
            var s = lending.Status();
            if (s.Sequence > sequence) return s;
            Thread.Sleep(50);
        }
        throw new TimeoutException("lend/return didn't finish");
    }

    [Fact]
    public void SaysWhatStandsInTheWayAndWhatWouldBePaused()
    {
        _machine.Hold(300, "chrome", "/user.slice/user-1000.slice/user@1000.service/app.slice/app-com.google.Chrome-1.scope");
        var s = Lending().Status();
        Assert.Equal(["openfan-helper.service"], s.SystemServices);
        Assert.Equal(["rtxgpu-openfan.service"], s.UserServices);
        Assert.Contains(s.Problems, p => p.StartsWith("chrome (300) has the card open: close it, or hide it", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Problems, p => p.Contains("openhw-helper")); // the helper releases itself
        Assert.Throws<ArgumentException>(() => Lending().Lend());

        _machine.Connected = true;
        Assert.Contains(Lending().Status().Problems, p => p.StartsWith("A monitor is plugged into the card", StringComparison.Ordinal));
    }

    [Fact]
    public void LendsInTheRightOrderAndReturns()
    {
        _machine.Release(200); // OpenHW's window has stopped the user's service already
        using var lending = Lending();
        Assert.Empty(lending.Status().Problems);
        lending.Lend();
        var lent = WaitFor(lending, 0);
        Assert.Equal(("lent", null), (lent.Stage, lent.Error));
        Assert.True(lent.Lent);
        Assert.Equal(1, _suspends); // NVML let go of the card before anything else was checked
        Assert.Equal(
        [
            "systemctl stop openfan-helper.service",
            "nvidia-smi -i 0000:e1:00.0 --query-gpu=power.limit --format=csv,noheader,nounits",
            "nvidia-smi -i 0000:e1:00.0 --query-gpu=persistence_mode --format=csv,noheader",
            "nvidia-smi -i 0000:e1:00.0 -pm 0",
            "nvidia-smi -i 0000:e1:00.0 --query-gpu=display_active --format=csv,noheader",
            "nvidia-smi -i 0000:e1:00.0 -pl 300.00",         // the hook records the real values…
            "nvidia-smi -i 0000:e1:00.0 -pm 1",
            "virsh -c qemu:///system start Windows-10-LTSC",  // …and does the VFIO handoff
            "systemctl start openfan-helper.service",
        ], _machine.Actions);
        Assert.Equal(1, _resumes);

        _machine.Actions.Clear();
        lending.Return();
        var back = WaitFor(lending, lent.Sequence);
        Assert.Equal(("returned", null), (back.Stage, back.Error));
        Assert.Equal(["virsh -c qemu:///system shutdown Windows-10-LTSC --mode agent", "systemctl restart openfan-helper.service"], _machine.Actions);
        Assert.Equal(2, _resumes); // a fresh NVML session sees the returned card
    }

    [Fact]
    public void AFailedStartPutsEverythingBack()
    {
        _machine.Release(200);
        _machine.StartFails = true;
        using var lending = Lending();
        lending.Lend();
        var s = WaitFor(lending, 0);
        Assert.Equal("failed", s.Stage);
        Assert.Contains("starting Windows-10-LTSC", s.Error);
        Assert.Contains("nvidia-smi -i 0000:e1:00.0 -pl 300.00", _machine.Actions.Skip(_machine.Actions.IndexOf("virsh -c qemu:///system start Windows-10-LTSC")));
        Assert.Equal("systemctl start openfan-helper.service", _machine.Actions[^1]);
        Assert.Equal(1, _resumes);
        Assert.False(s.Lent);
    }

    [Fact]
    public void AShutdownFromInsideTheVmIsNoticed()
    {
        _machine.Release(200);
        using var lending = Lending();
        lending.Lend();
        var lent = WaitFor(lending, 0);
        _machine.ShutDownInside(); // Windows' own Start menu → shut down
        var back = WaitFor(lending, lent.Sequence);
        Assert.Equal("returned", back.Stage);
        Assert.Contains("systemctl restart openfan-helper.service", _machine.Actions);
    }

    [Fact]
    public void OnlyVmsWithTheCardCanBeChosen()
    {
        var lending = Lending();
        Assert.Equal([new LendVm("Windows-10-LTSC", "shut off", true), new LendVm("ubuntu-test", "shut off", false)], lending.Vms());
        Assert.Throws<ArgumentException>(() => lending.SelectVm("ubuntu-test"));
        Assert.Throws<ArgumentException>(() => lending.SelectVm("x; rm -rf /"));
        var session = new HelperSession(new NoSmart(), lending: lending);
        Assert.Equal("err unknown command", session.HandleLine("cardvm $(reboot)"));
        Assert.Equal("ok Windows-10-LTSC", session.HandleLine("cardvm Windows-10-LTSC"));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }

    /// <summary>The machine as the lending code sees it, and the commands that change it.</summary>
    private sealed class FakeMachine : ICommandRunner
    {
        private readonly string _root;
        private string _state = "shut off";
        private bool _persistence = true;
        public bool StartFails;
        public bool Connected { set => File.WriteAllText(Path.Combine(Device, "drm", "card0", "card0-DP-1", "status"), value ? "connected" : "disconnected"); }
        public readonly List<string> Actions = [];
        private string Device => Path.Combine(_root, "sys", "bus", "pci", "devices", Card);

        public FakeMachine(string root)
        {
            _root = root;
            var info = Directory.CreateDirectory(Path.Combine(root, "proc", "driver", "nvidia", "gpus", Card)).FullName;
            File.WriteAllText(Path.Combine(info, "information"), "Model: \t NVIDIA RTX PRO 6000\nDevice Minor: \t 0\n");
            Directory.CreateDirectory(Path.Combine(Device, "drm", "card0", "card0-DP-1"));
            Connected = false;
            foreach (var d in new[] { "nvidia", "vfio-pci" }) Directory.CreateDirectory(Path.Combine(root, "sys", "bus", "pci", "drivers", d));
            Bind("nvidia");
            Hold(100, "openfan-helper", "/system.slice/openfan-helper.service");
            Hold(150, "openhw-helper", "/system.slice/openhw-helper.service");
            Hold(160, "nvidia-persiste", "/system.slice/nvidia-persistenced.service");
            Hold(200, "openfan", "/user.slice/user-1000.slice/user@1000.service/app.slice/rtxgpu-openfan.service");
        }

        private void Bind(string driver)
        {
            var link = Path.Combine(Device, "driver");
            if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
            File.CreateSymbolicLink(link, Path.Combine(_root, "sys", "bus", "pci", "drivers", driver));
        }

        public void Hold(int pid, string comm, string cgroup)
        {
            var proc = Directory.CreateDirectory(Path.Combine(_root, "proc", pid.ToString())).FullName;
            Directory.CreateDirectory(Path.Combine(proc, "fd"));
            File.CreateSymbolicLink(Path.Combine(proc, "fd", "3"), "/dev/nvidia0");
            File.WriteAllText(Path.Combine(proc, "comm"), comm + "\n");
            File.WriteAllText(Path.Combine(proc, "cgroup"), "0::" + cgroup + "\n");
        }

        public void Release(int pid) => Directory.Delete(Path.Combine(_root, "proc", pid.ToString()), recursive: true);

        public void ShutDownInside() { _state = "shut off"; Bind("nvidia"); }

        public (int Code, string Output) Run(string program, IReadOnlyList<string> args, TimeSpan timeout)
        {
            var name = Path.GetFileName(program);
            var line = name + " " + string.Join(' ', args);
            switch (name, args.Count > 2 ? args[2] : args.Count > 0 ? args[0] : "")
            {
                case ("virsh", "list"): return (0, "Windows-10-LTSC\nubuntu-test\n");
                case ("virsh", "domstate"): return (0, args[3] == Vm ? _state : "shut off");
                case ("virsh", "dumpxml"):
                    return (0, args[3] == Vm
                        ? "<domain><devices><hostdev mode='subsystem' type='pci'><source><address domain='0x0000' bus='0xe1' slot='0x00' function='0x0'/></source></hostdev></devices></domain>"
                        : "<domain><devices/></domain>");
                case ("virsh", "start"):
                    Actions.Add(line);
                    if (StartFails) return (1, "error: hook refused");
                    (_state, _) = ("running", 0);
                    Bind("vfio-pci");
                    return (0, "Domain started");
                case ("virsh", "shutdown"):
                    Actions.Add(line);
                    ShutDownInside();
                    return (0, "");
                case ("systemctl", _):
                    Actions.Add(line);
                    if (args[0] == "stop" && args[1] == "openfan-helper.service") Release(100);
                    return (0, "");
                case ("nvidia-smi", _) when args.Count > 2:
                    Actions.Add(line);
                    switch (args[2])
                    {
                        case "--query-gpu=power.limit": return (0, "300.00");
                        case "--query-gpu=persistence_mode": return (0, _persistence ? "Enabled" : "Disabled");
                        case "--query-gpu=display_active": return (0, "Disabled");
                        case "-pm":
                            _persistence = args[3] == "1";
                            if (!_persistence && Directory.Exists(Path.Combine(_root, "proc", "160"))) Release(160); // persistenced lets go
                            if (!_persistence && Directory.Exists(Path.Combine(_root, "proc", "150"))) Release(150); // stands in for NVML shutdown
                            return (0, "");
                        default: return (0, "");
                    }
            }
            return (1, "unexpected: " + line);
        }
    }
}
