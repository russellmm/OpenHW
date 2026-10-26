using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenHW.App.Power;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

public sealed class GpuLendingTests
{
    // What openhw-helper reports on the owner's machine with the RTX 6000 at E1 chosen and the filter off.
    private static CardFilterStatus Status(bool enabled = false) => new(
        enabled, enabled, "0000:e1:00.0",
        [new("0000:01:00.0", "NVIDIA GeForce RTX 5060 Ti"), new("0000:11:00.0", "NVIDIA RTX PRO 6000 Blackwell Workstation Edition"),
         new("0000:e1:00.0", "NVIDIA RTX PRO 6000 Blackwell Workstation Edition")],
        [new("/dev/nvidia0", 195, 0), new("/dev/dri/card0", 226, 0), new("/dev/dri/renderD129", 226, 129)],
        [new("session.slice", "GNOME desktop", true, true), new("user-975.slice", "Login screen", true, true),
         new("app-*openhw*.scope", "OpenHW window", true, true)],
        [new(12040, "gnome-shell", "/user.slice/user-1000.slice/user@1000.service/session.slice/org.gnome.Shell@ubuntu.service", "session.slice", true),
         new(12725, "Xwayland", "/user.slice/user-1000.slice/user@1000.service/session.slice/org.gnome.Shell@ubuntu.service", "session.slice", true),
         new(15004, "chrome", "/user.slice/user-1000.slice/user@1000.service/app.slice/app-com.google.Chrome-14950.scope", "app-com.google.Chrome-*.scope", false),
         new(2786, "nvidia-persiste", "/system.slice/nvidia-persistenced.service", null, false)]);

    private static readonly CardHolder Chrome = new(15004, "chrome", "/user.slice/user-1000.slice/user@1000.service/app.slice/app-com.google.Chrome-14950.scope", "app-com.google.Chrome-*.scope", false);
    private static readonly CardHolder OpenFan = new(4242, "openfan", "/user.slice/user-1000.slice/user@1000.service/app.slice/app-gnome-openfan-4200.scope", "app-gnome-openfan-*.scope", false);

    internal static LendStatus Lend(params CardHolder[] apps) => new("Windows-10-LTSC", "shut off", false, "idle", null, 0, [],
        ["openfan-helper.service"], ["rtxgpu-openfan.service"], apps);

    private static (GpuLendingWindow Window, List<string> Sent) Open(bool enabled = false, LendStatus? lend = null, List<(string, string)>? ran = null,
        Settings.AppSettings? settings = null)
    {
        var sent = new List<string>();
        string Request(string line)
        {
            if (line == "cardfilter") return GpuWire.Encode(Status(enabled));
            if (line == "lendstatus") return GpuWire.Encode(lend ?? Lend(Chrome));
            if (line == "cardvms") return GpuWire.Encode(new List<LendVm> { new("Windows-10-LTSC", "shut off", true), new("ubuntu-test", "shut off", false) });
            sent.Add(line);
            return "x";
        }
        var coordinator = new LendCoordinator(Request, (program, args) =>
        {
            ran?.Add((program, string.Join(' ', args)));
            // As systemd reports a systemd-run unit: a FragmentPath under /run/…/transient, and Transient=yes.
            return (0, args.Contains("show") ? "ExecStart={ path=/usr/bin/openfan ; argv[]=/usr/bin/openfan ; ignore_errors=no }\nSlice=rtxgpu.slice\nDescription=RTXinVM app openfan\nFragmentPath=/run/user/1000/systemd/transient/rtxgpu-openfan.service\nTransient=yes" : "");
        }, Path.Combine(Directory.CreateTempSubdirectory("openhw-lend-").FullName, "lend-paused.json"));
        var window = new GpuLendingWindow(Request, rtxinvmGuarding: () => true, lending: coordinator, settings: settings ?? new Settings.AppSettings());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, sent);
    }

    private static Button Button(Window w, string text, int index = 0) =>
        w.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == text).ElementAt(index);

    private static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        for (var i = 0; i < 50 && Dispatcher.UIThread.HasJobsWithPriority(DispatcherPriority.Background); i++) Dispatcher.UIThread.RunJobs();
        Thread.Sleep(100); // the command runs on the thread pool
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ShowsTheCardWhatHoldsItAndTheList()
    {
        var (window, _) = Open();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        var card = window.GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal("NVIDIA RTX PRO 6000 Blackwell Workstation Edition (E1:00.0)", (card.SelectedItem as ComboBoxItem)?.Content);
        Assert.True(card.IsEnabled);
        Assert.Contains(texts, t => t.StartsWith("Off. RTXinVM's guard is protecting this card", StringComparison.Ordinal));
        Assert.Contains("gnome-shell (12040), Xwayland (12725)", texts);             // one row for the whole desktop
        Assert.Contains("Hidden, but the desktop still has the card open: log out once to free it.", texts);
        Assert.Contains("nvidia-persiste (outside the desktop session)", texts);
        Assert.Contains("NVIDIA's persistence daemon: lending turns persistence off for this card, and it lets go.", texts);
        Assert.Contains("Login screen", texts);
        Assert.Contains("app-*openhw*.scope", texts);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-gpu-lending.png"));
    }

    [AvaloniaFact]
    public void ButtonsAndCheckboxesSendTheHelperCommands()
    {
        var (window, sent) = Open();
        Click(Button(window, "Hide from boot"));                    // the Chrome row
        Assert.Equal("cardhide app-com.google.Chrome-*.scope both Chrome", sent.Last());
        Click(Button(window, "Hide now"));
        Assert.Equal("cardhide app-com.google.Chrome-*.scope now Chrome", sent.Last());

        var now = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.Content as string == "Hide now"); // GNOME desktop
        now.IsChecked = false;
        Thread.Sleep(100); Dispatcher.UIThread.RunJobs();
        Assert.Equal("cardshow session.slice now", sent.Last());

        Click(Button(window, "Remove", 2));
        Assert.Equal("cardforget app-*openhw*.scope", sent.Last());

        var filter = window.GetVisualDescendants().OfType<CheckBox>().Single(c => (c.Content as string ?? "").StartsWith("Keep this card", StringComparison.Ordinal));
        filter.IsChecked = true;
        Thread.Sleep(100); Dispatcher.UIThread.RunJobs();
        Assert.Equal("cardfilter on", sent.Last());
    }

    [AvaloniaFact]
    public void TheCardCanOnlyChangeWhileTheFilterIsOff()
    {
        var (window, _) = Open(enabled: true);
        Assert.False(window.GetVisualDescendants().OfType<ComboBox>().First().IsEnabled);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? "").StartsWith("On: hidden programs are refused", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void AnOldHelperIsExplained()
    {
        var window = new GpuLendingWindow(_ => throw new IOException("openhw-helper: err unknown command"), () => false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "openhw-helper is too old for GPU lending: install the current OpenHW .deb.");
    }

    // Run with OPENHW_LIVE_HELPER=1 to render the window against the installed openhw-helper (read-only: it only
    // asks for the status). Skipped otherwise.
    [AvaloniaFact]
    public void LiveHelperRender()
    {
        if (Environment.GetEnvironmentVariable("OPENHW_LIVE_HELPER") != "1") return;
        var window = new GpuLendingWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-gpu-lending-live.png"));
    }

    [AvaloniaFact]
    public void LendIsGreyedWhileSomethingStandsInTheWay()
    {
        var (window, _) = Open();
        var lend = Button(window, "Lend to Windows-10-LTSC");
        Assert.False(lend.IsEnabled);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains("Chrome has the card open: close it, or tick \"Close and reopen when lending\" below.", texts);
        Assert.Contains("Paused for the handoff and started again afterwards: openfan-helper.service, rtxgpu-openfan.service.", texts);
        var vm = window.GetVisualDescendants().OfType<ComboBox>().Last();
        Assert.Equal(["None", "Windows-10-LTSC"], vm.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content!)); // only VMs with the card
    }

    [AvaloniaFact]
    public void LendingPausesTheUsersServiceThenAsksTheHelper()
    {
        var ran = new List<(string, string)>();
        var (window, sent) = Open(lend: Lend(), ran: ran);
        var lend = Button(window, "Lend to Windows-10-LTSC");
        Assert.True(lend.IsEnabled);
        Click(lend);
        Thread.Sleep(300); Dispatcher.UIThread.RunJobs();
        Assert.Contains(("systemctl", "--user stop rtxgpu-openfan.service"), ran);
        Assert.Equal("lend", sent.Last());
        window.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "openhw-gpu-lending-lend.png"));
    }

    [Fact]
    public void ATransientServiceIsRecreatedAfterTheLendAndAfterTheReturn()
    {
        var ran = new List<string>();
        var stage = Lend();
        var coordinator = new LendCoordinator(_ => "lending", (program, args) =>
        {
            ran.Add(program + " " + string.Join(' ', args));
            return (0, args.Contains("show") ? "ExecStart={ path=/usr/bin/openfan ; argv[]=/usr/bin/openfan ; ignore_errors=no }\nSlice=rtxgpu.slice\nDescription=RTXinVM app openfan\nFragmentPath=/run/user/1000/systemd/transient/rtxgpu-openfan.service\nTransient=yes" : "");
        }, Path.Combine(Directory.CreateTempSubdirectory("openhw-lend-").FullName, "lend-paused.json"));
        coordinator.Lend(stage);
        Assert.Contains("systemctl --user stop rtxgpu-openfan.service", ran);
        const string Recreate = "systemd-run --user --quiet --collect --unit=rtxgpu-openfan.service --slice=rtxgpu.slice --description=RTXinVM app openfan -- /usr/bin/openfan";

        ran.Clear();
        coordinator.Follow(stage with { Stage = "lent", Sequence = 1, Lent = true });
        Assert.Equal([Recreate], ran);                 // not "systemctl start": a stopped transient unit is gone

        ran.Clear();
        coordinator.Follow(stage with { Stage = "returned", Sequence = 2 });
        Assert.Equal(["systemctl --user stop rtxgpu-openfan.service", Recreate], ran); // restarted to list the card again
        Assert.Null(coordinator.Current);
    }

    [AvaloniaFact]
    public void ATickedAppIsClosedAndReopenedInsteadOfBlocking()
    {
        var settings = new Settings.AppSettings { LendReopen = ["app-gnome-openfan-*.scope"] };
        var (window, _) = Open(lend: Lend(OpenFan), settings: settings);
        Assert.True(Button(window, "Lend to Windows-10-LTSC").IsEnabled);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains("Paused for the handoff and started again afterwards: openfan-helper.service, rtxgpu-openfan.service, Openfan.", texts);

        var (other, _) = Open(lend: Lend(OpenFan, Chrome), settings: settings); // Chrome isn't ticked: it blocks
        Assert.False(Button(other, "Lend to Windows-10-LTSC").IsEnabled);
    }

    [Fact]
    public void TheCoordinatorClosesAndReopensATickedApp()
    {
        var root = Directory.CreateTempSubdirectory("openhw-reopen-").FullName;
        var scopeDir = Directory.CreateDirectory(Path.Combine(root, "cgroup", "user.slice/user-1000.slice/user@1000.service/app.slice/app-gnome-openfan-4200.scope")).FullName;
        File.WriteAllText(Path.Combine(scopeDir, "cgroup.procs"), "4245\n4242\n");
        var proc = Directory.CreateDirectory(Path.Combine(root, "proc", "4242")).FullName;
        File.WriteAllText(Path.Combine(proc, "cmdline"), "/opt/openfan/app/openfan\0--tray\0");
        File.CreateSymbolicLink(Path.Combine(proc, "cwd"), "/home/russellm");
        var ran = new List<string>();
        var launched = new List<string>();
        var coordinator = new LendCoordinator(_ => "lending", (program, args) => { ran.Add(program + " " + string.Join(' ', args)); return (0, ""); },
            Path.Combine(root, "lend-paused.json"), argv => launched.Add(string.Join(' ', argv)), Path.Combine(root, "proc"), Path.Combine(root, "cgroup"));
        var status = Lend(OpenFan) with { UserServices = [] };

        coordinator.Lend(status, ["app-gnome-openfan-*.scope"]);
        Assert.Equal(["systemctl --user stop app-gnome-openfan-4200.scope"], ran); // closed gracefully, through its scope

        coordinator.Follow(status with { Stage = "lent", Sequence = 1, Lent = true });
        var reopen = Assert.Single(launched);
        Assert.Matches(@"^systemd-run --user --quiet --collect --scope --slice=app.slice --unit=app-gnome-openfan-\d+\.scope --working-directory=/home/russellm -- /opt/openfan/app/openfan --tray$", reopen);
        Assert.Matches(@"^app-gnome-openfan-\d+\.scope$", coordinator.Current!.Apps![0].Scope);
        Assert.True(CardPatterns.Matches("app-gnome-openfan-*.scope", coordinator.Current!.Apps![0].Scope)); // the same entry covers it

        ran.Clear();
        var scope = coordinator.Current.Apps[0].Scope;
        coordinator.Follow(status with { Stage = "returned", Sequence = 2 });
        Assert.Equal([$"systemctl --user stop {scope}"], ran);
        Assert.Equal(2, launched.Count);               // reopened again: it lists the returned card
        Assert.Null(coordinator.Current);

        Assert.Throws<IOException>(() => coordinator.Lend(Lend(Chrome) with { UserServices = [] }, ["app-gnome-openfan-*.scope"])); // not ticked
        Directory.Delete(root, recursive: true);
    }
}

public sealed class CardHolderWatchTests
{
    private const string App = "/user.slice/user-1000.slice/user@1000.service/app.slice/";

    private sealed class FakeNotify : CardHolderWatch.INotify
    {
        public readonly List<(string Summary, Action<string> Answer)> Asked = [];
        public int Saves, Pages;
        public void Ask(string key, string summary, string body, IReadOnlyList<(string Key, string Label)> actions, Action<string> answer)
        {
            Assert.Equal(["default", "hide", "reopen", "leave"], actions.Select(a => a.Key));
            Asked.Add((summary, answer));
        }
        public void Save() => Saves++;
        public void OpenPage() => Pages++;
    }

    private static CardFilterStatus Status(bool enabled, params CardHolder[] holders) => new(enabled, enabled, "0000:e1:00.0",
        [new("0000:e1:00.0", "NVIDIA RTX PRO 6000 Blackwell Workstation Edition")], [],
        [new("session.slice", "GNOME desktop", true, true)], holders);

    [Fact]
    public void AsksOnceAboutEachUndecidedAppAndActsOnTheAnswer()
    {
        var settings = new OpenHW.App.Settings.AppSettings { LendReopen = ["app-gnome-openfan-*.scope"], LendKnownApps = ["app-com.anthropic.Claude-*.scope"] };
        var status = Status(true,
            new(1, "chrome", App + "app-com.google.Chrome-1.scope", "app-com.google.Chrome-*.scope", false),
            new(2, "chrome", App + "app-com.google.Chrome-1.scope", "app-com.google.Chrome-*.scope", false),
            new(3, "openfan", App + "app-gnome-openfan-3.scope", "app-gnome-openfan-*.scope", false),          // ticked
            new(4, "claude", App + "app-com.anthropic.Claude-4.scope", "app-com.anthropic.Claude-*.scope", false), // "Leave it"
            new(5, "openfan-helper", "/system.slice/openfan-helper.service", null, false),                       // a service
            new(6, "gnome-shell", "/user.slice/user-1000.slice/user@1000.service/session.slice/org.gnome.Shell@ubuntu.service", "session.slice", true));
        var sent = new List<string>();
        var notify = new FakeNotify();
        var watch = new CardHolderWatch(line => { if (line == "cardfilter") return GpuWire.Encode(status); sent.Add(line); return "ok"; }, settings, notify);

        watch.Check();
        var (summary, answer) = Assert.Single(notify.Asked);       // Chrome, once for its two processes
        Assert.Equal("Chrome opened the RTX PRO 6000 Blackwell Workstation Edition", summary);
        watch.Check();
        Assert.Single(notify.Asked);                               // not again this run

        answer("hide");
        Assert.Equal(["cardhide app-com.google.Chrome-*.scope both Chrome"], sent);
        answer("reopen");
        Assert.Contains("app-com.google.Chrome-*.scope", settings.LendReopen);
        answer("leave");
        Assert.Contains("app-com.google.Chrome-*.scope", settings.LendKnownApps);
        Assert.Equal(2, notify.Saves);
        answer("default");
        Assert.Equal(1, notify.Pages);
    }

    [Fact]
    public void NothingIsAskedWhileTheFilterIsOff()
    {
        var notify = new FakeNotify();
        var status = Status(false, new CardHolder(1, "chrome", App + "app-com.google.Chrome-1.scope", "app-com.google.Chrome-*.scope", false));
        new CardHolderWatch(_ => GpuWire.Encode(status), new OpenHW.App.Settings.AppSettings(), notify).Check();
        Assert.Empty(notify.Asked);
    }

    [Fact]
    public void AnAppSplitAcrossTwoScopesIsOneQuestionAndOneAnswer()
    {
        using var tree = new ChromeTree();
        var settings = new OpenHW.App.Settings.AppSettings();
        var status = Status(true,
            new(37042, "chrome", ChromeTree.Gnome, @"app-gnome-google\x2dchrome-*.scope", false),
            new(37026, "chrome", ChromeTree.Own, "app-com.google.Chrome-*.scope", false));
        var notify = new FakeNotify();
        var watch = new CardHolderWatch(_ => GpuWire.Encode(status), settings, notify, tree.Launches);
        watch.Check();
        var (_, answer) = Assert.Single(notify.Asked);              // was two notifications, one per scope
        answer("reopen");
        Assert.Equal(["app-com.google.Chrome-*.scope", @"app-gnome-google\x2dchrome-*.scope"], settings.LendReopen.Order(StringComparer.Ordinal));

        // The owner's answer from before this fix (only Chrome's own scope ticked) covers the launch: not asked again.
        var earlier = new FakeNotify();
        new CardHolderWatch(_ => GpuWire.Encode(status), new OpenHW.App.Settings.AppSettings { LendReopen = ["app-com.google.Chrome-*.scope"] }, earlier, tree.Launches).Check();
        Assert.Empty(earlier.Asked);
    }

    [Fact]
    public void TheCoordinatorClosesEveryScopeOfALaunchAndOpensItOnce()
    {
        using var tree = new ChromeTree();
        var ran = new List<string>();
        var launched = new List<string>();
        var coordinator = new LendCoordinator(_ => "lending", (program, args) => { ran.Add(program + " " + string.Join(' ', args)); return (0, ""); },
            Path.Combine(tree.Root, "lend-paused.json"), argv => launched.Add(string.Join(' ', argv)), tree.Proc, tree.Cgroup);
        var zygote = new CardHolder(37042, "chrome", ChromeTree.Gnome, @"app-gnome-google\x2dchrome-*.scope", false);
        var status = GpuLendingTests.Lend(zygote) with { UserServices = [] };

        Assert.Empty(coordinator.Blocking(status.Apps, ["app-com.google.Chrome-*.scope"]));   // ticked through its other scope
        Assert.Single(coordinator.Blocking(status.Apps, ["app-gnome-openfan-*.scope"]));
        coordinator.Lend(status, ["app-com.google.Chrome-*.scope"]);
        Assert.Equal(["systemctl --user stop app-com.google.Chrome-37026.scope", @"systemctl --user stop app-gnome-google\x2dchrome-37026.scope"], ran);

        coordinator.Follow(status with { Stage = "lent", Sequence = 1, Lent = true });
        var reopen = Assert.Single(launched);                        // Chrome itself, not its crashpad handler
        Assert.Matches(@"--unit=app-com\.google\.Chrome-\d+\.scope --working-directory=/home/russellm -- /opt/google/chrome/chrome$", reopen);
    }

    [Fact]
    public void AppsStartedByAnotherAppArentPartOfItsLaunch()
    {
        using var tree = new ChromeTree();
        Assert.Equal([ChromeTree.Own, ChromeTree.Gnome], tree.Launches.Of(ChromeTree.Gnome));
        Assert.Equal([ChromeTree.Own, ChromeTree.Gnome], tree.Launches.Of(ChromeTree.Own));
        Assert.Equal([ChromeTree.Reopened], tree.Launches.Of(ChromeTree.Reopened)); // OpenFan, reopened by OpenHW
        Assert.Equal([ChromeTree.OpenHW], tree.Launches.Of(ChromeTree.OpenHW));
        Assert.Equal(37026, tree.Launches.MainPid(ChromeTree.Own));
    }

    // Chrome as found on the owner's machine (2026-10-26), plus OpenHW (autostarted, so its scope has its PID) and
    // an OpenFan it reopened, whose first process OpenHW started.
    private sealed class ChromeTree : IDisposable
    {
        private const string Slice = "/user.slice/user-1000.slice/user@1000.service/app.slice/";
        public const string Gnome = Slice + @"app-gnome-google\x2dchrome-37026.scope";
        public const string Own = Slice + "app-com.google.Chrome-37026.scope";
        public const string OpenHW = Slice + "app-gnome-openhw-6000.scope";
        public const string Reopened = Slice + "app-gnome-openfan-1793034000000.scope";
        public readonly string Root = Directory.CreateTempSubdirectory("openhw-launch-").FullName;
        public string Proc => Path.Combine(Root, "proc");
        public string Cgroup => Path.Combine(Root, "cgroup");
        public AppLaunches Launches => new(Proc, Cgroup);

        public ChromeTree()
        {
            Scope(Gnome, (37034, 4841, "chrome_crashpad_handler"), (37042, 37026, "chrome"), (37043, 37026, "chrome"));
            Scope(Own, (37026, 4841, "chrome"), (37784, 37026, "chrome"));
            Scope(OpenHW, (6000, 4841, "openhw"));
            Scope(Reopened, (7000, 6000, "openfan"), (7001, 7000, "openfan"));
            var chrome = Path.Combine(Proc, "37026");
            File.WriteAllText(Path.Combine(chrome, "cmdline"), "/opt/google/chrome/chrome\0");
            File.CreateSymbolicLink(Path.Combine(chrome, "cwd"), "/home/russellm");
            var crashpad = Path.Combine(Proc, "37034");
            File.WriteAllText(Path.Combine(crashpad, "cmdline"), "/opt/google/chrome/chrome_crashpad_handler\0--monitor-self\0");
            File.CreateSymbolicLink(Path.Combine(crashpad, "cwd"), "/home/russellm");
        }

        private void Scope(string cgroup, params (int Pid, int Parent, string Comm)[] procs)
        {
            var dir = Directory.CreateDirectory(Path.Combine(Cgroup, cgroup.TrimStart('/'))).FullName;
            File.WriteAllLines(Path.Combine(dir, "cgroup.procs"), procs.Select(p => p.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach (var (pid, parent, comm) in procs)
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName, "stat"),
                    $"{pid} ({comm}) S {parent} {pid} {pid} 0 -1");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
