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

    private static LendStatus Lend(params string[] problems) => new("Windows-10-LTSC", "shut off", false, "idle", null, 0, problems,
        ["openfan-helper.service"], ["rtxgpu-openfan.service"]);

    private static (GpuLendingWindow Window, List<string> Sent) Open(bool enabled = false, LendStatus? lend = null, List<(string, string)>? ran = null)
    {
        var sent = new List<string>();
        string Request(string line)
        {
            if (line == "cardfilter") return GpuWire.Encode(Status(enabled));
            if (line == "lendstatus") return GpuWire.Encode(lend ?? Lend("chrome (15004) has the card open: close it, or hide it and restart it."));
            if (line == "cardvms") return GpuWire.Encode(new List<LendVm> { new("Windows-10-LTSC", "shut off", true), new("ubuntu-test", "shut off", false) });
            sent.Add(line);
            return "x";
        }
        var coordinator = new LendCoordinator(Request, (program, args) =>
        {
            ran?.Add((program, string.Join(' ', args)));
            return (0, args.Contains("show") ? "ExecStart={ path=/usr/bin/openfan ; argv[]=/usr/bin/openfan ; ignore_errors=no }\nSlice=rtxgpu.slice\nDescription=RTXinVM app openfan\nFragmentPath=" : "");
        }, Path.Combine(Directory.CreateTempSubdirectory("openhw-lend-").FullName, "lend-paused.json"));
        var window = new GpuLendingWindow(Request, rtxinvmGuarding: () => true, lending: coordinator);
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
        Assert.Contains("chrome (15004) has the card open: close it, or hide it and restart it.", texts);
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
}
