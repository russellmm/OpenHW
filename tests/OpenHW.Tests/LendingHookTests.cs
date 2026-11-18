using System.Diagnostics;
using Xunit;

namespace OpenHW.Tests;

/// <summary>
/// The packaged libvirt hook (packaging/lending/hook.py) picks its card and VM from OpenHW's card-filter.json, never a
/// fixed slot. Run as a normal user with a fake settings file: a VM that passes the chosen card through gets as far as
/// the root check (so the match worked); anything else is left alone. Nothing here touches hardware.
/// </summary>
public sealed class LendingHookTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openhw-hook").FullName;
    private string Settings => Path.Combine(_dir, "card-filter.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string HookPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "packaging", "lending", "hook.py");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("packaging/lending/hook.py");
    }

    private static string Xml(string bus) =>
        $"<domain><name>W</name><devices><hostdev mode='subsystem' type='pci' managed='yes'><source>" +
        $"<address domain='0x0000' bus='{bus}' slot='0x00' function='0x0'/></source></hostdev></devices></domain>";

    private (int Code, string Err) Run(string xml, params string[] args)
    {
        var start = new ProcessStartInfo("/usr/bin/python3") { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(HookPath());
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["OPENHW_HOOK_SETTINGS"] = Settings;
        using var p = Process.Start(start)!;
        p.StandardInput.Write(xml);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, err);
    }

    [LinuxFact]
    public void TheChosenCardIsMatchedWhereverItSits()
    {
        if (Environment.UserName == "root") return; // the root check is what proves the match
        File.WriteAllText(Settings, """{"Card":"0000:c1:00.0","CardUuid":"GPU-6d3e","Vm":"W","Enabled":true}""");

        var lend = Run(Xml("0xc1"), "W", "prepare", "begin");
        Assert.Equal(1, lend.Code);
        Assert.Contains("requires administrator privileges", lend.Err);

        var oldSlot = Run(Xml("0xe1"), "W", "prepare", "begin");  // the VM still names the card's old slot
        Assert.Equal(0, oldSlot.Code);
        Assert.Contains("does not pass 0000:c1:00.0 through", oldSlot.Err);

        Assert.Equal((0, ""), Run(Xml("0xc1"), "Other", "prepare", "begin")); // another VM
        Assert.Equal((0, ""), Run(Xml("0xc1"), "W", "start", "begin"));      // a phase the hook doesn't handle
    }

    [LinuxFact]
    public void WithoutASetUpCardTheHookLeavesEveryVmAlone()
    {
        Assert.Equal((0, ""), Run(Xml("0xc1"), "W", "prepare", "begin"));    // no settings file
        File.WriteAllText(Settings, """{"Card":"0000:c1:00.0","CardUuid":"GPU-6d3e","Vm":"W","Uninstalled":true}""");
        Assert.Equal((0, ""), Run(Xml("0xc1"), "W", "prepare", "begin"));
        File.WriteAllText(Settings, """{"Card":null,"Vm":"W"}""");
        Assert.Equal((0, ""), Run(Xml("0xc1"), "W", "prepare", "begin"));
    }
}
