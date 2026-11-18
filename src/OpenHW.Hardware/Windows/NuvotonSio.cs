using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The motherboard's Nuvoton NCT6798D Super I/O (WRX90E-SAGE SE), read as Linux's nct6798 driver reports it — chip
/// "nct6798", in0–in14 in volts with the driver's internal ×2 on in2, in3 and in7–in11, fan1–fan7 in RPM, temp1 =
/// SYSTIN — so <see cref="BoardProfiles"/> names and scales it exactly as on Linux (+VCORE_0, CHA_FAN1, W_PUMP+…).
///
/// Through PawnIO's signed LpcIO module, holding the shared ISA-bus mutex. Opening runs the standard Nuvoton sequence
/// once per service start (owner-approved 2026-11-18, after a one-shot dump checked every value against HWiNFO):
/// enter config mode (0x87 0x87 to 0x2E), read the chip ID, select the hardware monitor (LDN 0x0B) and read its base,
/// let LpcIO allow its ports (ioctl_find_bars, which selects each logical device to read its base registers), leave
/// config mode (0xAA). Each read then goes through the hardware monitor's index/data ports (base+5/+6), saving the bank
/// register 0x4E first and putting it back last. Only documented registers: voltages bank 4 0x80–0x8E, fans bank 4
/// 0xC0–0xCB and 0xCE–0xCF (nct6775's NCT6779_REG_FAN), SYSTIN bank 0 0x27. No other writes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NuvotonSio : IKernelChip
{
    public const string Chip = "nct6798";
    private const int HwmonIndex = 2; // k10temp is 0, the ASUS EC 1

    /// <summary>The Linux driver's ×2 inputs (internally halved on the chip): 3VCC, AVSB, 3VSB, VBAT, VTT and in10/in11.</summary>
    private static readonly int[] Doubled = [2, 3, 7, 8, 9, 10, 11];

    /// <summary>NCT6779_REG_FAN (bank 4): fan1–fan7, each a 16-bit RPM count (high byte first); 0x4CC isn't a fan.</summary>
    private static readonly int[] FanRegisters = [0xC0, 0xC2, 0xC4, 0xC6, 0xC8, 0xCA, 0xCE];

    private readonly PawnIo _pawn;
    private readonly ulong _index, _data;

    private NuvotonSio(PawnIo pawn, ulong hwmBase) => (_pawn, _index, _data) = (pawn, hwmBase + 5, hwmBase + 6);

    /// <summary>The NCT6798D (chip ID D428–D42F) on the first Super I/O slot, or null (no LpcIO, another chip).</summary>
    public static NuvotonSio? Open()
    {
        if (PawnIo.Open("LpcIO", out _) is not { } pawn) return null;
        try
        {
            var hwmBase = HardwareMutex.Hold(HardwareMutex.IsaBus, () =>
            {
                pawn.Execute("ioctl_select_slot", [0], 0); // 0x2E/0x2F
                Out(pawn, 0x2E, 0x87);
                Out(pawn, 0x2E, 0x87);
                try
                {
                    var id = SioIn(pawn, 0x20) << 8 | SioIn(pawn, 0x21);
                    if ((id & 0xFFF8) != 0xD428) return 0UL; // NCT6798D only: its registers are the ones checked
                    pawn.Execute("ioctl_superio_outb", [0x07, 0x0B], 0);
                    var address = (SioIn(pawn, 0x60) << 8 | SioIn(pawn, 0x61)) & 0xFFF8;
                    if (address is 0 or 0xFFF8) return 0UL;
                    pawn.Execute("ioctl_find_bars", [], 0);
                    return address;
                }
                finally { Out(pawn, 0x2E, 0xAA); }
            });
            if (hwmBase == 0) { pawn.Dispose(); return null; }
            var chip = new NuvotonSio(pawn, hwmBase);
            if (chip.Bank(0, 0x4F) != 0xA3) { chip.Dispose(); return null; } // Nuvoton's vendor ID (low byte) in the monitor
            return chip;
        }
        catch (IOException) { pawn.Dispose(); return null; }
    }

    private static void Out(PawnIo pawn, ulong port, ulong value) => pawn.Execute("ioctl_pio_outb", [port, value], 0);
    private static ulong SioIn(PawnIo pawn, ulong register) => pawn.Execute("ioctl_superio_inb", [register], 1)[0];

    /// <summary>One register, its bank selected, the previous bank put back (holding the ISA-bus mutex).</summary>
    private byte Bank(int bank, int register) => Banked(read => read(bank, register))[0];

    private byte[] Banked(Func<Func<int, int, byte>, byte> one) => Banked(read => [one(read)]);

    private byte[] Banked(Func<Func<int, int, byte>, byte[]> reads) => HardwareMutex.Hold(HardwareMutex.IsaBus, () =>
    {
        Out(_pawn, _index, 0x4E);
        var saved = _pawn.Execute("ioctl_pio_inb", [_data], 1)[0];
        try
        {
            return reads((bank, register) =>
            {
                Out(_pawn, _index, 0x4E);
                Out(_pawn, _data, (ulong)bank);
                Out(_pawn, _index, (ulong)register);
                return (byte)_pawn.Execute("ioctl_pio_inb", [_data], 1)[0];
            });
        }
        finally
        {
            Out(_pawn, _index, 0x4E);
            Out(_pawn, _data, saved);
        }
    });

    public IReadOnlyList<KernelChannel> Read()
    {
        // One mutex hold for the whole sample: 15 voltages, 7 fans (2 bytes each), SYSTIN.
        var raw = Banked(read =>
        {
            var bytes = new List<byte>();
            for (var i = 0; i < 15; i++) bytes.Add(read(4, 0x80 + i));
            foreach (var fan in FanRegisters) { bytes.Add(read(4, fan)); bytes.Add(read(4, fan + 1)); }
            bytes.Add(read(0, 0x27));
            return bytes.ToArray();
        });
        return Decode(raw);
    }

    /// <summary>The raw sample (15 voltage bytes, 7 fan words high-byte-first, SYSTIN) as nct6798 hwmon channels.</summary>
    public static IReadOnlyList<KernelChannel> Decode(byte[] raw)
    {
        var channels = new List<KernelChannel>();
        for (var i = 0; i < 15; i++)
            channels.Add(new(Chip, HwmonIndex, $"in{i}", $"in{i}", 0, raw[i] * 0.008 * (Doubled.Contains(i) ? 2 : 1)));
        for (var f = 0; f < FanRegisters.Length; f++)
        {
            var rpm = raw[15 + 2 * f] << 8 | raw[16 + 2 * f];
            channels.Add(new(Chip, HwmonIndex, $"fan{f + 1}", $"fan{f + 1}", 1, rpm is 0xFFFF ? null : rpm & 0x1FFF));
        }
        channels.Add(new(Chip, HwmonIndex, "temp1", "SYSTIN", 2, (sbyte)raw[29]));
        return channels;
    }

    public void Dispose() => _pawn.Dispose();
}
