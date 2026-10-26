using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>GPU Lending's Install / Uninstall: its system files under a fake /etc, and the card filter's state.</summary>
public sealed class LendingSetupTests : IDisposable
{
    private const string Card = "0000:e1:00.0";
    private readonly string _root = Directory.CreateTempSubdirectory("openhw-lending-setup").FullName;
    private readonly List<string> _ran = [];
    private string Etc => Path.Combine(_root, "etc");
    private string Hook => Path.Combine(Etc, "libvirt", "hooks", "qemu.d", LendingSetup.HookName);
    private string Stashed(string name) => Path.Combine(Etc, "openhw", "lending-off", name);
    private string Rule => Path.Combine(Etc, "udev", "rules.d", LendingSetup.RuleName);
    private string Legacy => Path.Combine(Etc, "udev", "rules.d", LendingSetup.LegacyRuleName);
    private string DropIn => Path.Combine(Etc, "systemd", "system", "openhw-helper.service.d", "lending.conf");

    public LendingSetupTests()
    {
        // This machine after RTXinVM's uninstall: its udev rule and the libvirt hook are still there.
        Directory.CreateDirectory(Path.GetDirectoryName(Hook)!);
        File.WriteAllText(Hook, "#!/bin/sh\nexec /usr/bin/python3 -I /usr/local/libexec/windows-gpu-handoff/hook.py \"$@\"\n");
        File.SetUnixFileMode(Hook, (UnixFileMode)0b111_101_101);
        Directory.CreateDirectory(Path.GetDirectoryName(Legacy)!);
        File.WriteAllText(Legacy, $"SUBSYSTEM==\"drm\", KERNELS==\"{Card}\", TAG+=\"mutter-device-ignore\"\n");
        var proc = Directory.CreateDirectory(Path.Combine(_root, "proc", "driver", "nvidia", "gpus", Card)).FullName;
        File.WriteAllText(Path.Combine(proc, "information"), "Model: \t\t NVIDIA RTX PRO 6000 Blackwell Workstation Edition\nDevice Minor: \t 1\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LendingSetup Setup() => new(Etc, new Runner(_ran));

    private sealed class Runner(List<string> ran) : ICommandRunner
    {
        public (int Code, string Output) Run(string program, IReadOnlyList<string> args, TimeSpan timeout)
        {
            ran.Add(Path.GetFileName(program) + " " + string.Join(' ', args));
            return (0, "");
        }
    }

    [Fact]
    public void InstallingPutsEverythingInPlaceAndReplacesRtxinvmsRule()
    {
        var changes = Setup().Apply(installed: true, Card);
        Assert.Equal(LendingSetup.RuleText(Card), File.ReadAllText(Rule));
        Assert.Contains("Before=display-manager.service systemd-user-sessions.service", File.ReadAllText(DropIn));
        Assert.False(File.Exists(Legacy));
        Assert.True(File.Exists(Stashed(LendingSetup.LegacyRuleName)));
        Assert.True(File.Exists(Hook));                         // left where it was
        Assert.Equal(new LendingFiles(true, true, true), Setup().Check(Card));
        Assert.Equal(["systemctl daemon-reload"], _ran);
        Assert.Equal(3, changes.Count);

        _ran.Clear();
        Assert.Empty(Setup().Apply(installed: true, Card));      // already in place: nothing, not even a reload
        Assert.Empty(_ran);
    }

    [Fact]
    public void UninstallingTakesItAllOutAndInstallingBringsTheHookBack()
    {
        Setup().Apply(installed: true, Card);
        Setup().Apply(installed: false, Card);
        Assert.False(File.Exists(Rule));
        Assert.False(File.Exists(DropIn));
        Assert.False(File.Exists(Hook));
        Assert.True(File.Exists(Stashed(LendingSetup.HookName)));
        Assert.Equal(new LendingFiles(false, false, false), Setup().Check(Card));

        Setup().Apply(installed: true, Card);
        Assert.True(File.Exists(Hook));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(Hook) & (UnixFileMode)0b111_111_111);
        Assert.Contains("windows-gpu-handoff/hook.py", File.ReadAllText(Hook));
        Assert.Equal(new LendingFiles(true, true, true), Setup().Check(Card));
    }

    [Fact]
    public void AnotherCardRewritesTheRuleAndNoCardLeavesNothingToIgnore()
    {
        Setup().Apply(true, Card);
        Setup().Apply(true, "0000:11:00.0");
        Assert.Contains("KERNELS==\"0000:11:00.0\"", File.ReadAllText(Rule));
        Setup().Apply(true, null);
        Assert.False(File.Exists(Rule));
        Assert.False(File.Exists(DropIn));
        Assert.True(File.Exists(Hook));
    }

    private CardFilter Filter() => new(Path.Combine(Etc, "openhw", "card-filter.json"), Path.Combine(_root, "run", "now.json"),
        Path.Combine(_root, "bpf"), Path.Combine(_root, "cgroup"), Path.Combine(_root, "proc"), Path.Combine(_root, "sys"), setup: Setup());

    [Fact]
    public void UninstallKeepsTheChoicesAndRefusesTheFilterUntilInstalledAgain()
    {
        using (var filter = Filter())
        {
            filter.SelectCard(Card);
            filter.Start();                                      // the boot: installed
            Assert.False(filter.Status().RebootNeeded);
            filter.SetInstalled(false);
            var s = filter.Status();
            Assert.False(s.Installed);
            Assert.True(s.RebootNeeded);
            Assert.Equal(Card, s.Card);
            Assert.Equal(3, s.Entries.Count);
            Assert.Equal(new LendingFiles(false, false, false), s.Files);
            Assert.Throws<ArgumentException>(() => filter.SetEnabled(true));
        }
        using (var filter = Filter())                            // a helper restart keeps "uninstalled" and this boot's start
        {
            Assert.False(filter.Installed);
            Assert.True(filter.Status().RebootNeeded);
            Assert.Equal("ok nothing to change", new HelperSession(new FakeSmart(), card: filter).HandleLine("lenduninstall"));
            Assert.StartsWith("ok ", new HelperSession(new FakeSmart(), card: filter).HandleLine("lendinstall"));
            var s = filter.Status();
            Assert.True(s.Installed);
            Assert.False(s.RebootNeeded);                         // back to how this boot started
            Assert.Equal(new LendingFiles(true, true, true), s.Files);
        }
    }

    private sealed class FakeSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => new byte[512];
        public void Dispose() { }
    }
}
