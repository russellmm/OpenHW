using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>The card filter's rules and commands, without root (the kernel part has a root self-test in the helper).</summary>
public sealed class CardFilterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("openhw-cardfilter").FullName;

    public CardFilterTests()
    {
        // A fake /proc with the three NVIDIA cards as this machine lists them.
        foreach (var (pci, minor, model, uuid) in new[] { ("0000:01:00.0", 1, "NVIDIA GeForce RTX 5060 Ti", Ti), ("0000:11:00.0", 2, "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", Pro1), ("0000:e1:00.0", 0, "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", Pro2) })
            AddCard(pci, minor, model, uuid);
    }

    private const string Ti = "GPU-551a52fc-c5ce-c335-e0d7-81fc00948a65", Pro1 = "GPU-d0f36b3b-abad-1a42-0e65-e78da2139da3", Pro2 = "GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0";

    private void AddCard(string pci, int minor, string model, string uuid)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "proc", "driver", "nvidia", "gpus", pci)).FullName;
        File.WriteAllText(Path.Combine(dir, "information"), $"Model: \t\t {model}\nDevice Minor: \t {minor}\nGPU UUID: \t {uuid}\n");
    }

    // A new motherboard: every card on another bus address.
    private void SwapBoard()
    {
        Directory.Delete(Path.Combine(_root, "proc", "driver", "nvidia", "gpus"), recursive: true);
        AddCard("0000:11:00.0", 0, "NVIDIA GeForce RTX 5060 Ti", Ti);
        AddCard("0000:c1:00.0", 1, "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", Pro2);
        AddCard("0000:f1:00.0", 2, "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", Pro1);
    }

    private string SettingsJson => File.ReadAllText(Path.Combine(_root, "etc", "card-filter.json"));

    [LinuxFact]
    public void TheChosenCardIsFollowedByIdentityToItsNewSlot()
    {
        using (var filter = Filter()) filter.SelectCard("0000:e1:00.0");
        Assert.Contains(Pro2, SettingsJson);
        SwapBoard();
        using var after = Filter();
        after.Start();
        Assert.Equal("0000:c1:00.0", after.Status().Card); // the same card, not whatever sits at e1 (nothing) or 11 (the 5060 Ti)
        Assert.Contains(Pro2, after.Status().Cards.Single(c => c.Pci == "0000:c1:00.0").Uuid);
    }

    [LinuxFact]
    public void AMissingCardsFilterDoesntBlockChoosingAgain()
    {
        Directory.CreateDirectory(Path.Combine(_root, "etc"));
        // The owner's settings after the board swap: filter on, for a slot that's empty now (no UUID recorded yet).
        File.WriteAllText(Path.Combine(_root, "etc", "card-filter.json"), """{"Card":"0000:e1:00.0","Enabled":true}""");
        SwapBoard();
        using var filter = Filter();
        Assert.Equal("0000:c1:00.0", filter.SelectCard("0000:c1:00.0"));
        var status = filter.Status();
        Assert.Equal(("0000:c1:00.0", false), (status.Card, status.Enabled)); // off until the owner ticks it again
        Assert.Contains(Pro2, SettingsJson);
    }

    [LinuxFact]
    public void APresentCardsFilterMustBeTurnedOffFirst()
    {
        Directory.CreateDirectory(Path.Combine(_root, "etc"));
        File.WriteAllText(Path.Combine(_root, "etc", "card-filter.json"), """{"Card":"0000:11:00.0","Enabled":true}""");
        using var filter = Filter();
        Assert.Throws<ArgumentException>(() => filter.SelectCard("0000:e1:00.0"));
    }

    [LinuxFact]
    public void OldSettingsAdoptTheCardsIdentityAndNeverFilterAnotherCardInItsSlot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "etc"));
        File.WriteAllText(Path.Combine(_root, "etc", "card-filter.json"), """{"Card":"0000:11:00.0","Enabled":false}""");
        using (var filter = Filter()) filter.Start();
        Assert.Contains(Pro1, SettingsJson); // adopted from the card at 11:00.0 (pre-0.21.0 settings had only the address)
        SwapBoard(); // 11:00.0 is now the 5060 Ti; the chosen PRO 6000 went to f1
        using (var filter = Filter()) { filter.Start(); Assert.Equal("0000:f1:00.0", filter.Status().Card); }
        Directory.Delete(Path.Combine(_root, "proc", "driver", "nvidia", "gpus", "0000:f1:00.0"), recursive: true);
        AddCard("0000:f1:00.0", 2, "NVIDIA GeForce RTX 5090", "GPU-00000000-0000-0000-0000-000000000000"); // card swapped out
        using (var filter = Filter()) { filter.Start(); Assert.Null(filter.Status().Card); }
        Assert.Contains(Pro1, SettingsJson); // still remembered, in case it comes back
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CardFilter Filter() => new(Path.Combine(_root, "etc", "card-filter.json"), Path.Combine(_root, "run", "now.json"),
        Path.Combine(_root, "bpf"), Path.Combine(_root, "cgroup"), Path.Combine(_root, "proc"), Path.Combine(_root, "sys"));

    [LinuxTheory]
    [InlineData("app.slice/app-com.google.Chrome-14950.scope", "app-com.google.Chrome-*.scope")]
    [InlineData("app.slice/app-gnome-openoutlook-19910.scope", "app-gnome-openoutlook-*.scope")]
    [InlineData("app.slice/vte-spawn-0c2e8f2a-5f0e-4f3b-9a64-3f2a1c9b7d10.scope", "vte-spawn-*.scope")]
    [InlineData("session.slice/org.gnome.Shell@ubuntu.service", "session.slice")]           // the whole desktop
    [InlineData("session.slice/org.gnome.SettingsDaemon.XSettings.service", "session.slice")] // ibus-x11 lives here
    [InlineData("app.slice/app-com.anthropic.Claude-23722.scope", "app-com.anthropic.Claude-*.scope")]
    [InlineData("app.slice/rtxgpu-openfan.service", "rtxgpu-openfan.service")]
    public void SuggestsTheEntryThatCoversAProcess(string cgroup, string expected) =>
        Assert.Equal(expected, CardPatterns.Suggest("user-1000.slice/user@1000.service/" + cgroup, gdmUid: 975));

    [LinuxFact]
    public void TheLoginScreenIsTheGdmUsersSlice()
    {
        Assert.Equal("user-975.slice", CardPatterns.Suggest("user-975.slice/session-c1.scope", 975));
        Assert.Null(CardPatterns.Suggest("user-1000.slice", 975)); // nothing to list
    }

    [LinuxFact]
    public void PatternsAreUnitNamesWithWildcards()
    {
        Assert.True(CardPatterns.IsValid("app-com.google.Chrome-*.scope"));
        Assert.True(CardPatterns.IsValid("org.gnome.Shell@*.service"));
        Assert.False(CardPatterns.IsValid("*.scope"));              // would hide every app at once
        Assert.False(CardPatterns.IsValid("../etc/passwd.slice"));
        Assert.False(CardPatterns.IsValid("app 1.scope"));
        Assert.True(CardPatterns.IsValid(@"app-gnome-weather\x2dwidget-*.scope"));  // systemd's escape for "-"
        Assert.True(CardPatterns.Matches(@"app-gnome-weather\x2dwidget-*.scope", @"app-gnome-weather\x2dwidget-13422.scope"));
        Assert.Equal(@"app-gnome-weather\x2dwidget-*.scope", CardPatterns.Suggest(@"user-1000.slice/user@1000.service/app.slice/app-gnome-weather\x2dwidget-13422.scope", 975));
        Assert.True(CardPatterns.Matches("app-*openhw*.scope", "app-gnome-openhw-12244.scope"));
        Assert.True(CardPatterns.Matches("app-*openhw*.scope", "app-openhw-1792988066.scope"));
        Assert.False(CardPatterns.Matches("app-*openhw*.scope", "app-gnome-openfan-1.scope"));
    }

    [LinuxFact]
    public void OnlyLeafUnitsAreAllowedSoNewCgroupsAreRefusedUntilSeen()
    {
        string[] hidden = ["session.slice", "app-com.google.Chrome-*.scope"];
        uint? V(string path, bool leaf) => CardPatterns.Verdict(path.Split('/'), leaf, hidden);
        Assert.Equal(Bpf.Allowed, V("user-1000.slice/user@1000.service/app.slice/vte-spawn-1.scope", leaf: true));
        Assert.Equal(Bpf.Hidden, V("user-1000.slice/user@1000.service/session.slice", leaf: false));   // hidden at any level
        Assert.Equal(Bpf.Hidden, V("user-1000.slice/user@1000.service/session.slice/org.gnome.Shell@ubuntu.service", true));
        Assert.Equal(Bpf.Hidden, V("user-1000.slice/user@1000.service/app.slice/app-com.google.Chrome-7.scope", true));
        Assert.Null(V("user-1000.slice/user@1000.service/app.slice", leaf: false)); // containers: no verdict
        Assert.Null(V("user-1000.slice/user@1000.service/app.slice", leaf: true));  // an empty slice still has none
        Assert.Null(V("user-1000.slice/user@1000.service", leaf: true));            // the user manager, even empty
    }

    [LinuxFact]
    public void ChoosingACardStartsTheListWithTheDesktopLoginScreenAndOpenHw()
    {
        var filter = Filter();
        filter.SelectCard("0000:e1:00.0");
        var status = filter.Status();
        Assert.Equal("0000:e1:00.0", status.Card);
        Assert.Equal(3, status.Cards.Count);
        Assert.Contains(status.Entries, e => e is { Pattern: "session.slice", Name: "GNOME desktop", HideAtBoot: true, HiddenNow: true });
        Assert.Contains(status.Entries, e => e is { Pattern: "app-*openhw*.scope", HideAtBoot: true });
        Assert.False(status.Enabled); // nothing is enforced until the owner turns it on
        Assert.Throws<ArgumentException>(() => filter.SelectCard("0000:aa:00.0"));
    }

    [LinuxFact]
    public void HideNowAndHideFromBootAreSeparate()
    {
        var filter = Filter();
        filter.SelectCard("0000:e1:00.0");
        filter.SetHidden("app-com.google.Chrome-*.scope", now: true, boot: false, hide: true, name: "Google Chrome");
        filter.SetHidden("session.slice", now: true, boot: false, hide: false, name: null); // GNOME sees it now…
        var entries = filter.Status().Entries.ToDictionary(e => e.Pattern);
        Assert.Equal((false, true), (entries["app-com.google.Chrome-*.scope"].HideAtBoot, entries["app-com.google.Chrome-*.scope"].HiddenNow));
        Assert.Equal((true, false), (entries["session.slice"].HideAtBoot, entries["session.slice"].HiddenNow)); // …but not after a reboot

        // A new boot: /run is empty again, so "hide now" starts from the list's "hide from boot" entries.
        File.Delete(Path.Combine(_root, "run", "now.json"));
        var rebooted = Filter().Status().Entries.ToDictionary(e => e.Pattern);
        Assert.True(rebooted["session.slice"].HiddenNow);
        Assert.False(rebooted["app-com.google.Chrome-*.scope"].HiddenNow);
    }

    [LinuxFact]
    public void HelperCommandsValidateEverything()
    {
        var session = new HelperSession(new NoSmart(), card: Filter());
        Assert.Equal("ok 0000:e1:00.0", session.HandleLine("cardselect 0000:e1:00.0"));
        Assert.Equal("err invalid PCI address", session.HandleLine("cardselect ../../x"));
        Assert.Equal("ok app-com.google.Chrome-*.scope", session.HandleLine("cardhide app-com.google.Chrome-*.scope both Google Chrome"));
        Assert.Equal("err invalid pattern", session.HandleLine("cardhide *.scope now"));
        Assert.Equal("err unknown command", session.HandleLine("cardhide x.scope later"));
        Assert.Equal("err invalid name", session.HandleLine("cardhide a.scope now " + new string('x', 61)));
        var status = GpuWire.Decode<CardFilterStatus>(session.HandleLine("cardfilter")[3..])!;
        Assert.Contains(status.Entries, e => e.Name == "Google Chrome" && e.HideAtBoot && e.HiddenNow);
        Assert.Equal("ok app-com.google.Chrome-*.scope", session.HandleLine("cardforget app-com.google.Chrome-*.scope"));
        Assert.Equal("err unknown command", session.HandleLine("cardfilter maybe"));
    }

    [LinuxFact]
    public void TheProgramAssemblesWithEveryJumpResolved()
    {
        var code = Bpf.Program(7, 8);
        Assert.Equal(0, code.Length % 8);
        Assert.InRange(code.Length / 8, 100, 4096);
        Assert.Equal(code, Bpf.Program(7, 8));
        Assert.Equal(0x95, code[^8]); // ends in exit
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }

    [LinuxFact]
    public void AtBootTheFilterWaitsForTheCardsNodesButNotForALentOrMissingCard()
    {
        // The card is on the bus with the NVIDIA driver bound, but nvidia_drm hasn't made card/renderD yet.
        var device = Directory.CreateDirectory(Path.Combine(_root, "sys", "bus", "pci", "devices", "0000:e1:00.0")).FullName;
        foreach (var d in new[] { "nvidia", "vfio-pci" }) Directory.CreateDirectory(Path.Combine(_root, "sys", "bus", "pci", "drivers", d));
        File.CreateSymbolicLink(Path.Combine(device, "driver"), Path.Combine(_root, "sys", "bus", "pci", "drivers", "nvidia"));
        var filter = Filter();
        filter.SelectCard("0000:e1:00.0");
        File.WriteAllText(Path.Combine(_root, "etc", "card-filter.json"), File.ReadAllText(Path.Combine(_root, "etc", "card-filter.json")).Replace("\"Enabled\": false", "\"Enabled\": true"));
        double Start()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { Filter().Start(TimeSpan.FromMilliseconds(600)); } catch (IOException) { } // attaching needs root; the wait comes first
            return sw.Elapsed.TotalMilliseconds;
        }
        Assert.InRange(Start(), 550, 3000);          // waited for the nodes

        File.Delete(Path.Combine(device, "driver"));
        File.CreateSymbolicLink(Path.Combine(device, "driver"), Path.Combine(_root, "sys", "bus", "pci", "drivers", "vfio-pci"));
        Assert.InRange(Start(), 0, 400);             // lent to the VM at boot: nothing to wait for

        Directory.Delete(device, recursive: true);
        Assert.InRange(Start(), 0, 400);             // not in this machine
    }
}
