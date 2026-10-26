using OpenHW.Hardware;

namespace OpenHW.App.Power;

/// <summary>
/// Tells the owner when a new app opens the lent card (owner's request, 2026-10-26): with the card filter on, every
/// app that isn't hidden can open the card just by drawing with the GPU, and while it holds it the card can't be lent.
/// The first time an app that hasn't been decided on — not on the list, not ticked "Close and reopen when lending",
/// not answered "Leave it" before — holds the card, one notification offers: Hide it (now and from boot), Close and
/// reopen when lending, or Leave it (it may use the card; stop asking). A click on the bubble opens GPU Lending.
/// Each app is asked about at most once per OpenHW run, and "Leave it" is remembered (AppSettings.LendKnownApps).
/// An app whose launch spans several scopes (Chrome: see AppLaunches) is one question, and the answer covers them all;
/// it counts as decided once any of them is ticked or left.
/// </summary>
public sealed class CardHolderWatch(Func<string, string> request, Settings.AppSettings settings, CardHolderWatch.INotify notify,
    AppLaunches? launches = null)
{
    private readonly AppLaunches _launches = launches ?? new AppLaunches();

    /// <summary>The notification and what the owner's answer does; implemented by the App over DesktopNotifier.</summary>
    public interface INotify
    {
        void Ask(string key, string summary, string body, IReadOnlyList<(string Key, string Label)> actions, Action<string> answer);
        void Save();              // settings changed (UI thread)
        void OpenPage();          // GPU Lending
    }

    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    /// <summary>One check (any thread): asks about apps holding the card that nobody has decided on yet.</summary>
    public void Check()
    {
        if (!Monitor.TryEnter(_asked)) return; // the last check is still waiting on the helper
        try { CheckNow(); }
        finally { Monitor.Exit(_asked); }
    }

    private void CheckNow()
    {
        CardFilterStatus? status;
        try { status = GpuWire.Decode<CardFilterStatus>(request("cardfilter")); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or FormatException) { return; }
        if (status is not { Installed: true, Enabled: true, Card: { } card }) return;
        var listed = status.Entries.Select(e => e.Pattern).ToHashSet(StringComparer.Ordinal);
        var cardName = status.Cards.FirstOrDefault(c => c.Pci == card)?.Name.Replace("NVIDIA ", "") ?? "the lent card";
        foreach (var app in status.Holders
                     .Where(h => h.Pattern is not null && !h.Hidden && IsUsersApp(h.Cgroup))
                     .GroupBy(h => h.Pattern!))
        {
            var pattern = app.Key;
            if (Decided(pattern, listed) || _asked.Contains(pattern)) continue;
            var patterns = AppLaunches.Patterns(_launches.Of(app.First().Cgroup)).Append(pattern).Distinct(StringComparer.Ordinal).ToList();
            _asked.UnionWith(patterns);
            if (patterns.Any(p => settings.LendReopen.Contains(p) || settings.LendKnownApps.Contains(p))) continue;
            var undecided = patterns.Where(p => !Decided(p, listed)).ToList();
            var name = Name(app.First().Program);
            notify.Ask("card-holder:" + pattern, $"{name} opened the {cardName}",
                $"While it has the card open, the card can't be lent to a VM. {name} draws on your other GPUs either way.",
                [("default", "GPU Lending…"), ("hide", "Hide it"), ("reopen", "Close & reopen when lending"), ("leave", "Leave it")],
                answer => Answer(answer, undecided, name));
        }
    }

    private bool Decided(string pattern, HashSet<string> listed) =>
        listed.Contains(pattern) || settings.LendReopen.Contains(pattern) || settings.LendKnownApps.Contains(pattern);

    private void Answer(string answer, IReadOnlyList<string> patterns, string name)
    {
        switch (answer)
        {
            case "hide":
                foreach (var pattern in patterns)
                {
                    try { request($"cardhide {pattern} both {name}"); }
                    catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException) { lock (_asked) _asked.Remove(pattern); }
                }
                break;
            case "reopen":
                foreach (var pattern in patterns.Where(p => !settings.LendReopen.Contains(p))) settings.LendReopen.Add(pattern);
                notify.Save();
                break;
            case "leave":
                foreach (var pattern in patterns.Where(p => !settings.LendKnownApps.Contains(p))) settings.LendKnownApps.Add(pattern);
                notify.Save();
                break;
            default:
                notify.OpenPage();
                break;
        }
    }

    // The desktop user's app scopes (not services, not the desktop itself).
    private static bool IsUsersApp(string cgroup) =>
        cgroup.Contains("/user@", StringComparison.Ordinal) && cgroup.Contains("/app.slice/", StringComparison.Ordinal)
        && cgroup.EndsWith(".scope", StringComparison.Ordinal);

    // "OpenOutlook.Des" (a 15-character comm) → "OpenOutlook.Des"; printable ASCII only, as the helper accepts.
    private static string Name(string program)
    {
        var clean = new string(program.Where(c => c >= 0x20 && c <= 0x7e).ToArray()).Trim();
        if (clean.Length == 0) clean = "An app";
        return (char.ToUpperInvariant(clean[0]) + clean[1..])[..Math.Min(clean.Length, 60)];
    }
}
