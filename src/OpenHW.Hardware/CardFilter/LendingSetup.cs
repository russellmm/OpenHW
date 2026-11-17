namespace OpenHW.Hardware;

/// <summary>Which of GPU Lending's system files are in place (shown on the page).</summary>
public sealed record LendingFiles(bool BootOrder, bool GnomeIgnore, bool Hook, bool AudioIgnore = true);

/// <summary>
/// GPU Lending's footprint outside the helper, put in place or taken out by Install / Uninstall on the page:
/// <list type="bullet">
/// <item>a drop-in that starts openhw-helper before the login screen (so "hide from boot" holds before GNOME starts;
///   the packaged unit itself has no such ordering);</item>
/// <item>a udev rule that keeps GNOME's compositor (mutter) off the lent card;</item>
/// <item>a WirePlumber rule that leaves the card's display-audio function unclaimed (the hook refuses while anything
///   holds it, and the desktop's sound server would otherwise open it at every login);</item>
/// <item>the libvirt handoff hook (<c>qemu.d/50-windows-gpu</c>): a two-line launcher for OpenHW's packaged
///   <c>/usr/lib/openhw/lending/hook.py</c>, which reads the card (UUID + current slot) and the VM from
///   <c>card-filter.json</c> — so it follows the card like everything else. Removed on uninstall.</item>
/// </list>
/// RTXinVM's own udev rule (<c>61-windows-gpu.rules</c>) and hook launcher, which named one fixed slot, are replaced
/// by OpenHW's and kept in <c>/etc/openhw/lending-off/</c> (the launcher as <c>50-windows-gpu.rtxinvm</c>). All of it takes full effect at the next boot (udev, systemd ordering and libvirt read these at start).
/// </summary>
public sealed class LendingSetup(string etcRoot = "/etc", ICommandRunner? runner = null, string systemctl = "/usr/bin/systemctl")
{
    public const string HookName = "50-windows-gpu", LegacyRuleName = "61-windows-gpu.rules", RuleName = "61-openhw-lending.rules";

    private readonly ICommandRunner _run = runner ?? new ProcessRunner();
    private string DropIn => Path.Combine(etcRoot, "systemd", "system", "openhw-helper.service.d", "lending.conf");
    private string Rules => Path.Combine(etcRoot, "udev", "rules.d");
    private string Hooks => Path.Combine(etcRoot, "libvirt", "hooks", "qemu.d");
    private string Stash => Path.Combine(etcRoot, "openhw", "lending-off");
    private string AudioRule => Path.Combine(etcRoot, "wireplumber", "wireplumber.conf.d", AudioRuleName);
    public const string AudioRuleName = "51-openhw-lending.conf", HookScript = "/usr/lib/openhw/lending/hook.py";

    public static string HookText =>
        "#!/bin/sh\n" +
        "# Written by OpenHW (Power → GPU Lending… → Install); removed by Uninstall. The hook reads the card and the VM\n" +
        "# from /etc/openhw/card-filter.json.\n" +
        $"exec /usr/bin/python3 -I {HookScript} \"$@\"\n";

    /// <summary>WirePlumber's name for the card's audio function (function 1): <c>alsa_card.pci-0000_c1_00.1</c>.</summary>
    public static string AudioDeviceName(string card) =>
        "alsa_card.pci-" + System.Text.RegularExpressions.Regex.Replace(card.ToLowerInvariant(), @"\.\d$", ".1").Replace(':', '_');

    public static string AudioRuleText(string card) =>
        "# Written by OpenHW (Power → GPU Lending… → Install); removed by Uninstall.\n" +
        "# Leaves the lent card's display audio unclaimed, so the handoff can take it; other audio is unaffected.\n" +
        "# Takes effect when WirePlumber starts (each login, or: systemctl --user restart wireplumber).\n" +
        "monitor.alsa.rules = [\n" +
        "  {\n" +
        $"    matches = [ {{ device.name = \"{AudioDeviceName(card)}\" }} ]\n" +
        "    actions = { update-props = { device.disabled = true } }\n" +
        "  }\n" +
        "]\n";

    public static string DropInText =>
        "# Written by OpenHW (Power → GPU Lending… → Install); removed by Uninstall.\n" +
        "# The card filter must be in force before the login screen and the desktop can open the lent card.\n" +
        "[Unit]\nBefore=display-manager.service systemd-user-sessions.service\n";

    public static string RuleText(string card) =>
        "# Written by OpenHW (Power → GPU Lending… → Install); removed by Uninstall.\n" +
        "# Keeps the lent card out of GNOME's compositor; CUDA and other programs can still use it.\n" +
        $"SUBSYSTEM==\"drm\", KERNELS==\"{card}\", TAG+=\"mutter-device-ignore\"\n";

    public LendingFiles Check(string? card) => new(File.Exists(DropIn),
        card is not null && Read(Path.Combine(Rules, RuleName)) == RuleText(card), Read(Path.Combine(Hooks, HookName)) == HookText,
        card is not null && Read(AudioRule) == AudioRuleText(card));

    /// <summary>
    /// Makes the files match: installed with a card chosen → all in place; uninstalled → all taken out. Installed with
    /// no card yet → only the hook comes back (there's nothing to order or ignore). Returns what changed, for the log.
    /// <paramref name="reload"/>: tell systemd about a changed drop-in (not at the helper's own start: only the next
    /// boot depends on it, and systemd reads it then anyway).
    /// </summary>
    public IReadOnlyList<string> Apply(bool installed, string? card, bool reload = true)
    {
        var changes = new List<string>();
        var full = installed && card is not null;

        if (File.Exists(Path.Combine(Rules, LegacyRuleName))) // replaced by OpenHW's own rule, installed or not
        {
            Move(Path.Combine(Rules, LegacyRuleName), Path.Combine(Stash, LegacyRuleName));
            changes.Add($"moved {LegacyRuleName} to {Stash}");
        }
        var rule = Path.Combine(Rules, RuleName);
        if (full && Read(rule) != RuleText(card!)) { Write(rule, RuleText(card!)); changes.Add("wrote " + RuleName); }
        if (!full && File.Exists(rule)) { File.Delete(rule); changes.Add("removed " + RuleName); }

        var audio = Read(AudioRule);
        if (full && audio != AudioRuleText(card!)) { Write(AudioRule, AudioRuleText(card!)); changes.Add("wrote the WirePlumber rule " + AudioRuleName); }
        if (!full && audio is not null) { File.Delete(AudioRule); changes.Add("removed the WirePlumber rule " + AudioRuleName); }

        var hook = Path.Combine(Hooks, HookName);
        var current = Read(hook);
        if (current is not null && current != HookText) // someone else's launcher (RTXinVM's names one fixed slot): keep it aside
        {
            Move(hook, Path.Combine(Stash, HookName + ".rtxinvm"));
            changes.Add($"moved the old libvirt hook {HookName} to {Stash} as {HookName}.rtxinvm");
            current = null;
        }
        if (installed && current is null && Directory.Exists(Hooks))
        {
            Write(hook, HookText);
            File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101); // 0755: libvirt runs only executable hooks
            changes.Add("wrote the libvirt hook " + HookName);
        }
        if (!installed && current is not null)
        {
            File.Delete(hook);
            changes.Add("removed the libvirt hook " + HookName);
        }

        var dropIn = Read(DropIn);
        if (full && dropIn != DropInText) { Write(DropIn, DropInText); changes.Add("wrote the boot-order drop-in"); }
        if (!full && dropIn is not null) { File.Delete(DropIn); changes.Add("removed the boot-order drop-in"); }
        if (reload && changes.Any(c => c.Contains("drop-in", StringComparison.Ordinal)))
            _run.Run(systemctl, ["daemon-reload"], TimeSpan.FromSeconds(30));
        return changes;
    }

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // In the helper's sandbox each writable folder is its own bind mount, so this is often a cross-mount move: .NET
    // then copies (keeping the mode, 0755 for the hook) and deletes.
    private static void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: true);
    }
}
