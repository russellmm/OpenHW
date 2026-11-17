using System.Text.Json;
using OpenHW.Hardware;

namespace OpenHW.App.HardwareCheck;

/// <summary>Something about this machine OpenHW reads only partly, or not at all, and what would fix it.</summary>
/// <param name="Warning">True when something is broken or stale (shown first, in the alarm color); false for plain information.</param>
/// <param name="Action">A button's label for the fix OpenHW can do itself, or null.</param>
public sealed record CoverageNote(string Area, string Text, bool Warning = false, string? Fix = null, string? Action = null);

/// <summary>
/// What OpenHW can't fully read on this machine. Linux loads the kernel drivers by itself (udev, at boot); what's
/// missing is either a driver that isn't loaded, OpenHW's own knowledge of the board, or a stale saved setting.
/// </summary>
public static class Coverage
{
    private static string ShortBus(string bus) => bus.Length >= 7 ? bus[^7..].ToUpperInvariant() : bus;

    public const string OpenGpuLending = "Open GPU Lending…";
    public const string OpenPowerLimits = "Open Power Limits…";

    public sealed record Probe(Func<string, bool> Exists, Func<string, string> ReadText, Func<string?> HelperProtocol, string Etc = "/etc");

    public static Probe Live => new(p => File.Exists(p) || Directory.Exists(p), SysFs.ReadText, () =>
    {
        try { return HelperClient.Request("ping").Trim(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException) { return null; }
    });

    public static List<CoverageNote> Check(HardwareSnapshot now, IReadOnlyList<HardwareItem> inventory, bool bmcAnswered, Probe probe,
        HardwareSnapshot? before = null)
    {
        var notes = new List<CoverageNote>();

        // ── The board management controller (IPMI) ──
        var ipmiInAcpi = probe.Exists("/sys/bus/acpi/devices/IPI0001:00");
        var ipmiDevice = probe.Exists("/dev/ipmi0");
        if (ipmiInAcpi && !ipmiDevice)
            notes.Add(new("BMC", "The board has a BMC (ACPI IPI0001) but no /dev/ipmi0: the kernel's IPMI device driver isn't loaded.",
                Warning: true, Fix: "sudo modprobe ipmi_devintf ipmi_si; to load it at every boot: echo ipmi_devintf | sudo tee /etc/modules-load.d/openhw-ipmi.conf"));
        else if (ipmiDevice && !bmcAnswered)
        {
            var protocol = probe.HelperProtocol();
            notes.Add(protocol is not null && int.TryParse(protocol, out var p) && p < 12
                ? new("BMC", $"The BMC is there (/dev/ipmi0), but the installed openhw-helper (protocol {p}) predates BMC support.",
                    Warning: true, Fix: "Install this version's .deb (sudo apt install ./dist/openhw_<version>_amd64.deb) — the helper reads the BMC.")
                : new("BMC", "The BMC is there (/dev/ipmi0) but hasn't answered yet; its sensor list takes several seconds to read at first.",
                    Fix: protocol is null ? "openhw-helper isn't running: sudo systemctl start openhw-helper" : null));
        }

        // ── DIMMs ──
        if (now.DimmsBmc > now.DimmsReadable)
            notes.Add(new("Memory", $"{now.DimmsBmc} DIMMs installed (BMC), {now.DimmsReadable} readable over the SPD sideband. " +
                "The others' voltages and PMIC power aren't shown; their temperatures come from the BMC group.",
                Fix: "Linux bound SPD hubs only on some of the board's memory buses. Attaching the spd5118 driver to the " +
                     "others needs a root scan of those buses, shared with the BMC — not done automatically."));
        else if (now.DimmsReadable == 0 && now.MemoryGiB > 0)
            notes.Add(new("Memory", "No DIMM sensors: the kernel's spd5118 driver hasn't bound any SPD hub.",
                Fix: "sudo modprobe spd5118 (DDR5) or jc42 (DDR4); some boards need i2c_piix4 or i2c_designware loaded first."));

        // ── Super I/O names ──
        var superIo = now.Chips.FirstOrDefault(c => c.StartsWith("nct", StringComparison.OrdinalIgnoreCase) || c.StartsWith("it8", StringComparison.OrdinalIgnoreCase));
        if (superIo is not null && !BoardProfiles.Known.Any(p => p.Matches(now.BoardVendor, now.Board, superIo)))
            notes.Add(new("Motherboard", $"No board profile for {now.Board} ({superIo}): its voltages show as in0…inN at the pin " +
                "(no divider, so +12 V reads about 1 V) and fans and temperatures under the chip's own names.",
                Fix: now.Bmc.Length > 0
                    ? "The BMC group reads this board's rails and fans under their real names. A profile still needs a session " +
                      "comparing readings with HWiNFO on this board (docs/board-profiles.md)."
                    : "A profile needs a session comparing readings with HWiNFO on this board (docs/board-profiles.md)."));
        else if (superIo is null && !now.Board.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            notes.Add(new("Motherboard", "No Super I/O sensor chip found (no nct67xx/it87 hwmon).",
                Fix: "sudo modprobe nct6775 (Nuvoton) or it87 (ITE); some boards need acpi_enforce_resources=lax on the kernel command line."));

        // ── Board VR ──
        if (BoardVrs.For(now.BoardVendor, now.Board) is null)
            notes.Add(new("Motherboard", "No known PMBus voltage regulator for this board, so no VR group (only the TRX50-SAGE's is mapped)."));

        // ── GPU Lending's card: chosen by UUID since 0.21.0, and followed to its current slot when the helper starts ──
        var filter = probe.ReadText(Path.Combine(probe.Etc, "openhw", "card-filter.json"));
        if (filter.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(filter);
                var pci = doc.RootElement.TryGetProperty("Card", out var c) ? c.GetString() : null;
                var uuid = doc.RootElement.TryGetProperty("CardUuid", out var u) ? u.GetString() : null;
                var card = uuid is null ? null : now.Gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
                const string vmHint = " If the card moved, also check the VM's PCI device (virt-manager → the VM → its PCI host device): libvirt names it by slot.";
                if (uuid is not null && card is null)
                    notes.Add(new("GPU Lending", $"The card chosen for lending ({uuid}) isn't in this machine.", Warning: true,
                        Fix: "Choose a card in Power → GPU Lending.", Action: OpenGpuLending));
                else if (uuid is null && pci is { Length: > 0 } && !probe.Exists("/sys/bus/pci/devices/" + pci))
                    notes.Add(new("GPU Lending", $"Set up for card {pci}, which isn't in this machine any more " +
                        $"(GPUs now: {string.Join(", ", now.Gpus.Select(g => g.Pci))}). The card filter hides nothing and Lend won't work. " +
                        "(Settings from before 0.21.0 name the slot, not the card; from now on the card is followed to any slot.)",
                        Warning: true, Fix: "Choose the card again in Power → GPU Lending." + vmHint, Action: OpenGpuLending));
                else if (card is not null && pci is not null && !BootPowerLimits.SameBus(pci, card.Pci))
                    notes.Add(new("GPU Lending", $"The lending card moved from {pci} to {card.Pci}; openhw-helper follows it at its next start.",
                        Fix: "sudo systemctl restart openhw-helper (or reboot)." + vmHint));
            }
            catch (JsonException) { }
        }

        // ── Boot GPU power limits: lines naming a PCI slot don't follow a card that moves ──
        var boot = new BootPowerLimits(probe.Etc);
        var slotNotes = new List<string>();
        var cardLines = boot.CardLines();
        var failAlone = boot.LinesFailAlone();
        foreach (var (bus, watts, openhw) in boot.SlotLines())
        {
            var whose = openhw ? "OpenHW (from before 0.21.0)" : BootPowerLimits.GpuUnit;
            var here = now.Gpus.FirstOrDefault(g => BootPowerLimits.SameBus(g.Pci, bus));
            var was = before?.Gpus.FirstOrDefault(g => BootPowerLimits.SameBus(g.Pci, bus));
            // With OpenHW's drop-in in place the unit's slot lines can't do harm: an empty slot's line fails on its own,
            // and a card with its own "at boot" value gets OpenHW's line after the slot's.
            if (!openhw && failAlone && (here is null || cardLines.ContainsKey(here.Uuid))) continue;
            if (here is null) slotNotes.Add($"{whose} sets {watts} W for slot {ShortBus(bus)}, which has no GPU now");
            else if (was is not null && was.Uuid != here.Uuid) slotNotes.Add($"{whose} sets {watts} W for slot {ShortBus(bus)}, which now holds {here.Name} instead of {was.Name}");
        }
        foreach (var (uuid, watts) in cardLines.Where(c => now.Gpus.All(g => !string.Equals(g.Uuid, c.Key, StringComparison.OrdinalIgnoreCase))))
            slotNotes.Add($"OpenHW keeps {watts} W at boot for card {uuid}, which isn't in this machine");
        if (slotNotes.Count > 0)
            notes.Add(new("Power limits", "Boot GPU power limits: " + string.Join("; ", slotNotes) + ". " +
                $"GPUs now: {string.Join(", ", now.Gpus.Select(g => $"{g.Pci} {g.Name}"))}.",
                Warning: true,
                Fix: "Set each card's \"at boot\" value in Power → Power Limits: OpenHW then names the card itself (by UUID), so it " +
                     $"follows the card to any slot and wins over {BootPowerLimits.GpuUnit}'s per-slot lines. OpenHW doesn't edit that unit; " +
                     "its lines for empty slots just fail harmlessly once OpenHW's drop-in is in place.",
                Action: OpenPowerLimits));

        return notes.OrderByDescending(n => n.Warning).ToList();
    }
}
