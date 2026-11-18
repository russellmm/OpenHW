using System.Runtime.Versioning;

namespace OpenHW.Hardware.Windows;

/// <summary>
/// The ASUS embedded controller's temperatures on the Pro WS WRX90E-SAGE SE, through PawnIO's signed LpcACPIEC module
/// (ports 0x62/0x66 only), reported as Linux's <c>asusec</c> hwmon chip so the group reads "ASUS EC: ASUS" as in
/// HWiNFO. Only three bank-0 registers, identified on this board by OpenFan against HWiNFO: VRM1 0x34, VRM2 0x33, the
/// USB thermistor 0x42. Nothing else in the EC is read and nothing is written — only the ACPI EC "read" command
/// (0x80). Windows' own EC driver uses the same ports, so each read holds the shared Global\Access_EC mutex, each value
/// is the median of three reads and must be plausible (5–110 °C), and the controller is read every 5 s at most.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AsusEc : IKernelChip
{
    private const byte CommandPort = 0x66, DataPort = 0x62, ReadCommand = 0x80;
    private const byte InputBufferFull = 0x02, OutputBufferFull = 0x01;

    public static readonly (string Feature, string Label, byte Register)[] Temperatures =
    [
        ("temp1", "VRM1", 0x34),
        ("temp2", "VRM2", 0x33),
        ("temp3", "USB Thermistor", 0x42),
    ];

    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepGood = TimeSpan.FromSeconds(30);

    private readonly PawnIo _pawn;
    private readonly Dictionary<string, (double? Value, DateTime GoodAt)> _last = [];
    private DateTime _readAt = DateTime.MinValue;

    private AsusEc(PawnIo pawn) => _pawn = pawn;

    /// <summary>Only on the board the register map belongs to.</summary>
    public static AsusEc? Open(string vendor, string board) =>
        vendor.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase) && board.Equals("Pro WS WRX90E-SAGE SE", StringComparison.OrdinalIgnoreCase) &&
        PawnIo.Open("LpcACPIEC", out _) is { } pawn ? new AsusEc(pawn) : null;

    public IReadOnlyList<KernelChannel> Read()
    {
        var now = DateTime.UtcNow;
        if (now - _readAt >= ReadEvery)
        {
            _readAt = now;
            foreach (var (feature, _, register) in Temperatures)
            {
                double? value = Median(register) is { } v && v is >= 5 and <= 110 ? v : null;
                if (value is not null) _last[feature] = (value, now);
                else if (!_last.TryGetValue(feature, out var previous) || now - previous.GoodAt > KeepGood) _last[feature] = (null, previous.GoodAt);
            }
        }
        return Temperatures.Select(t => new KernelChannel("asusec", 1, t.Feature, t.Label, 2, _last.GetValueOrDefault(t.Feature).Value)).ToList();
    }

    private double? Median(byte register)
    {
        var reads = new List<byte>(3);
        for (var i = 0; i < 3; i++)
            if (ReadByte(register) is { } b) reads.Add(b);
        if (reads.Count == 0) return null;
        reads.Sort();
        return reads[reads.Count / 2];
    }

    private byte? ReadByte(byte register)
    {
        try
        {
            return HardwareMutex.Hold<byte?>(HardwareMutex.EmbeddedController, () =>
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    if (!WaitInputClear()) continue;
                    Write(CommandPort, ReadCommand);
                    if (!WaitInputClear()) continue;
                    Write(DataPort, register);
                    if (WaitInputClear() && WaitOutputFull()) return (byte)Port(DataPort);
                }
                return null;
            });
        }
        catch (IOException) { return null; }
    }

    private bool WaitInputClear()
    {
        for (var i = 0; i < 50; i++)
        {
            if ((Port(CommandPort) & InputBufferFull) == 0) return true;
            Thread.Sleep(1);
        }
        return false;
    }

    // Some ASUS controllers clear IBF instead of raising OBF when the data is ready (as OpenFan found).
    private bool WaitOutputFull()
    {
        for (var i = 0; i < 5; i++)
        {
            if ((Port(CommandPort) & OutputBufferFull) != 0) return true;
            Thread.Sleep(1);
        }
        return WaitInputClear();
    }

    private ulong Port(byte port) => _pawn.Execute("ioctl_pio_read", [port], 1)[0];

    private void Write(byte port, byte value) => _pawn.Execute("ioctl_pio_write", [port, value], 0);

    public void Dispose() => _pawn.Dispose();
}
