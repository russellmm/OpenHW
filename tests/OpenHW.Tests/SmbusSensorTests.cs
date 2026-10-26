using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>
/// The DDR5 sideband and board VR readers against a fake SMBus and sysfs, with register values captured on the
/// TRX50-SAGE on 2026-10-25 (tools/smbus-probe), which HWiNFO showed as 1.35 V, 12.04 V and so on.
/// </summary>
public sealed class SmbusSensorTests : IDisposable
{
    private readonly string _sys = Directory.CreateTempSubdirectory("openhw-smbus").FullName;
    private readonly FakeBuses _buses = new();

    public SmbusSensorTests()
    {
        Hub(0, 0x50, "AMDI0010:01");
        Hub(1, 0x52, "AMDI0010:02");
        Adapter(4, "SMBus PIIX4 adapter port 0 at 0b00");
        Adapter(6, "SMBus PIIX4 adapter port 1 at 0b20");
        foreach (var (bus, hub) in new[] { (0, 0x50), (1, 0x52) }) Dimm(bus, hub & 7);
        Xdpe(page: 1);
    }

    public void Dispose() => Directory.Delete(_sys, recursive: true);

    // ── fake sysfs: /sys/devices/platform/<controller>/i2c-N/N-00xx with the spd5118 driver bound ────────────────
    private void Hub(int bus, int address, string controller, byte[]? spd = null)
    {
        var client = $"{bus}-00{address:x2}";
        var device = Path.Combine(_sys, "devices", "platform", controller, $"i2c-{bus}", client);
        Directory.CreateDirectory(device);
        File.WriteAllBytes(Path.Combine(device, "eeprom"), spd ?? Spd());
        Link(Path.Combine(_sys, "bus", "i2c", "devices", client), device);
        Link(Path.Combine(_sys, "bus", "i2c", "drivers", "spd5118", client), device);
    }

    private void Adapter(int bus, string name)
    {
        var dir = Path.Combine(_sys, "devices", "pci0000:00", "0000:00:14.0", $"i2c-{bus}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name"), name + "\n");
        Link(Path.Combine(_sys, "bus", "i2c", "devices", $"i2c-{bus}"), dir);
    }

    private static void Link(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateSymbolicLink(path, target);
    }

    // The Kingston KF556R28-32's SPD: Renesas hub, Richtek PMIC (8A 8C, installed), TS5111 sensors.
    private static byte[] Spd(ushort pmicVendor = 0x8A8C, byte pmicType = 0x80)
    {
        var spd = new byte[1024];
        spd[194] = 0x80; spd[195] = 0xB3; spd[196] = 0x80;
        spd[198] = (byte)(pmicVendor >> 8); spd[199] = (byte)pmicVendor; spd[200] = pmicType;
        spd[210] = 0x80; spd[211] = 0xB3; spd[212] = 0xC0;
        spd[512] = 0x01; spd[513] = 0x98;
        System.Text.Encoding.ASCII.GetBytes("KF556R28-32".PadRight(30)).CopyTo(spd, 521);
        return spd;
    }

    private void Dimm(int bus, int n)
    {
        var b = _buses.Bus(bus);
        b.Set(0x10 | n, 0x00, 0x51); b.SetWord(0x10 | n, 0x31, 0x02F0);   // TS0 47.0 °C
        b.Set(0x30 | n, 0x00, 0x51); b.SetWord(0x30 | n, 0x31, 0x02E0);   // TS1 46.0 °C
        var pmic = 0x48 | n;
        b.Set(pmic, 0x3C, 0x8A); b.Set(pmic, 0x3D, 0x8C);
        foreach (var (r, v) in new (byte, byte)[] { (0x08, 0), (0x09, 0), (0x0A, 0), (0x0B, 0), (0x0C, 0x15), (0x0E, 0x09), (0x0F, 0), (0x1A, 0xC2), (0x1B, 0x45), (0x33, 0), (0x30, 0xC8) })
            b.Set(pmic, r, v);
        // R31 follows R30's ADC_SELECT, as the PMIC's does.
        b.Adc[pmic] = select => select switch { 0 => 0x5A, 2 => 0x5A, 3 => 0x79, 5 => 0xAC, 8 => 0x78, 9 => 0x43, _ => 0 };
    }

    private void Xdpe(byte page, byte[]? deviceId = null)
    {
        var b = _buses.Bus(4);
        b.Blocks[(0x20, 0xAD)] = deviceId ?? [0x01, 0x95];
        b.Set(0x20, 0x00, page); b.Set(0x20, 0x20, 0x18);
        foreach (var (cmd, word) in new (byte, ushort)[] { (0x88, 0xD302), (0x89, 0xB380), (0x8B, 0x015A), (0x8C, 0xCBB0), (0x8D, 0x0026), (0x8E, 0x0025), (0x96, 0xD2D0), (0x97, 0xD278), (0x79, 0x0002) })
            b.SetWord(0x20, cmd, word);
    }

    private Ddr5Reader Reader() => new(_buses, _sys, adcSettle: TimeSpan.Zero, cacheFor: TimeSpan.Zero);

    [Fact]
    public void ReadsEachDimmAndWritesOnlyTheAdcSelectWhichItRestores()
    {
        var samples = Reader().Read();
        Assert.Equal(["0-50", "1-52"], samples.Select(s => s.Client));
        var dimm = samples[0];
        Assert.Equal((ushort)0x02F0, dimm.Ts0);
        Assert.Equal([0x5A, 0x5A, 0x79, 0xAC, 0x78, 0x43], dimm.Pmic!.Adc);
        var writes = _buses.Bus(0).Writes;
        Assert.All(writes, w => Assert.Equal((0x48, (byte)0x30), (w.Address, w.Command))); // nothing but PMIC R30
        Assert.Equal([0x80, 0x90, 0x98, 0xA8, 0xC0, 0xC8, 0xC8], writes.Select(w => (int)w.Value)); // six rails, then back to 0xC8
        Assert.Equal(0xC8, _buses.Bus(0).Get(0x48, 0x30));
        Assert.Equal(samples, Ddr5Reader.Parse(Ddr5Reader.Format(samples)), new SampleComparer());
    }

    [Fact]
    public void APmicTheSpdDoesNotDeclareIsNeverWritten()
    {
        _buses.Bus(0).Set(0x48, 0x3C, 0x80); // a different vendor than the SPD's 8A 8C
        Assert.Null(Reader().Read()[0].Pmic);
        Assert.Empty(_buses.Bus(0).Writes);
    }

    [Fact]
    public void AModuleWithoutAPmicIsLeftAlone()
    {
        File.WriteAllBytes(Path.Combine(_sys, "bus", "i2c", "devices", "0-0050", "eeprom"), Spd(pmicType: 0x00));
        var dimm = Reader().Read()[0];
        Assert.Null(dimm.Pmic);
        Assert.NotNull(dimm.Ts0);
        Assert.Empty(_buses.Bus(0).Writes);
    }

    [Fact]
    public void DecodesTheRailsAsHwinfoShowedThem()
    {
        Assert.Equal(47.0, Ddr5Decode.Temperature(0x02F0));
        Assert.Equal(-1.25, Ddr5Decode.Temperature(0x1FEC)); // 13-bit two's complement, 0.25 °C steps
        Assert.Equal(1.350, Ddr5Decode.Rail(0x5A)!.Value, 3);
        Assert.Equal(1.815, Ddr5Decode.Rail(0x79)!.Value, 3);
        Assert.Equal(12.04, Ddr5Decode.Input(0xAC)!.Value, 2);
        Assert.Null(Ddr5Decode.Rail(0)); // "undefined"
        Assert.Equal(2.625, Ddr5Decode.Meter(0x15));
    }

    [Fact]
    public void TheDimmEngineGroupsEachModuleWithItsHubTemperature()
    {
        var reply = Ddr5Reader.Format(Reader().Read());
        var engine = new DimmEngine(() => reply, _sys);
        var items = engine.Discover();
        var hub = DramSensorIdentity.Locate("0-0050", _sys)!.Value;
        var first = items.Where(i => i.Group == DimmEngine.GroupKey(hub.Identity)).ToList();
        Assert.Equal(["TS0 Temperature", "TS1 Temperature", "VDD (SWA) Voltage", "VDDQ (SWB) Voltage", "VPP (SWC) Voltage",
                      "1.8V VOUT Voltage", "1.0V VOUT Voltage", "VIN Voltage", "Total Power",
                      "PMIC High Temperature", "PMIC Over Voltage", "PMIC Under Voltage"], first.Select(i => i.Name));
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        double? Value(string name) => readings[first.Single(i => i.Name == name).Id];
        Assert.Equal(1.35, Value("VDD (SWA) Voltage")!.Value, 3);
        Assert.Equal(2.625, Value("Total Power"));
        Assert.Equal(0, Value("PMIC Over Voltage"));
        Assert.Equal("DDR5 DIMM [#0]: Kingston KF556R28-32 (AMDI0010:01 / 0x50)", DimmEngine.Title(DimmEngine.GroupKey(hub.Identity), _sys));
    }

    [Fact]
    public void PerRailMetersWhenThePmicIsNotSetToTotalPower()
    {
        foreach (var bus in new[] { 0, 1 }) _buses.Bus(bus).Set(0x48 | (bus == 0 ? 0 : 2), 0x1B, 0x05); // current mode
        var reply = Ddr5Reader.Format(Reader().Read());
        var names = new DimmEngine(() => reply, _sys).Discover().Select(i => i.Name).ToList();
        Assert.Contains("SWA Current", names);
        Assert.DoesNotContain("Total Power", names);
    }

    [Fact]
    public void TheVrIsConfirmedByItsDeviceIdAndDecodesLikeHwinfo()
    {
        var vr = BoardVrs.For("ASUSTeK COMPUTER INC.", "Pro WS TRX50-SAGE WIFI A");
        var sample = new BoardVrReader(_buses, vr, _sys).Read();
        Assert.NotNull(sample);
        Assert.Equal("xdpe19283b 01 18 d302 b380 015a cbb0 0026 0025 d2d0 d278 0002", BoardVrReader.Format(sample));
        Assert.Empty(_buses.Bus(4).Writes);
        var values = BoardVrEngine.Values(BoardVrReader.Parse(BoardVrReader.Format(sample))).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal(1.3516, values["vout"]!.Value, 4);
        Assert.Equal(12.03, values["vin"]!.Value, 2);
        Assert.Equal(7.375, values["iout"]);
        Assert.Equal(38, values["loop1"]);
        Assert.Equal(100, values["efficiency"]); // POUT 11.25 W over PIN 9.875 W at light load: capped
    }

    [Fact]
    public void ADifferentChipAtTheVrAddressIsNotRead()
    {
        Xdpe(page: 1, deviceId: [0x02, 0x22]);
        var vr = BoardVrs.For("ASUSTeK COMPUTER INC.", "Pro WS TRX50-SAGE WIFI A");
        Assert.Null(new BoardVrReader(_buses, vr, _sys).Read());
        Assert.Equal([(0x20, (byte)0xAD)], _buses.Bus(4).Reads); // only the identity check
    }

    [Fact]
    public void VrReadingsAreBlankWhileAnotherPageIsSelected()
    {
        Xdpe(page: 0);
        var vr = BoardVrs.For("ASUSTeK COMPUTER INC.", "Pro WS TRX50-SAGE WIFI A")!;
        var reader = new BoardVrReader(_buses, vr, _sys);
        var engine = new BoardVrEngine(() => BoardVrReader.Format(reader.Read()!), vr);
        Assert.Equal(9, engine.Discover().Count);
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.All(readings.Values, Assert.Null);
    }

    [Fact]
    public void PmbusFormats()
    {
        Assert.Equal(-0.5, Pmbus.Linear11(0xFFFF)); // exponent -1, mantissa -1
        Assert.Equal(-0.25, Pmbus.Linear11(0xF7FF)); // exponent -2, mantissa -1
        Assert.Null(Pmbus.Linear16(0x015A, 0x40)); // direct mode: not decoded
    }

    [Fact]
    public void HelperServesBothCommandsWithoutArguments()
    {
        var vr = BoardVrs.For("ASUSTeK COMPUTER INC.", "Pro WS TRX50-SAGE WIFI A");
        var session = new HelperSession(new NoSmart(), ddr5: Reader(), vr: new BoardVrReader(_buses, vr, _sys));
        Assert.StartsWith("ok 0-50 02f0 02e0 8a8c 00,00,00,00,15,09,00,c2,45,00 5a,5a,79,ac,78,43;1-52 ", session.HandleLine("ddr5"));
        Assert.StartsWith("ok xdpe19283b 01 18", session.HandleLine("vr"));
        Assert.Equal("err unknown command", session.HandleLine("ddr5 0x48"));
        Assert.Equal("err no known board VR", new HelperSession(new NoSmart(), vr: new BoardVrReader(_buses, null, _sys)).HandleLine("vr"));
    }

    private sealed class SampleComparer : IEqualityComparer<Ddr5Sample>
    {
        public bool Equals(Ddr5Sample? a, Ddr5Sample? b) => a!.Client == b!.Client && a.Ts0 == b.Ts0 && a.Ts1 == b.Ts1 &&
            a.Pmic?.Vendor == b.Pmic?.Vendor && (a.Pmic?.Registers ?? []).SequenceEqual(b.Pmic?.Registers ?? []) &&
            (a.Pmic?.Adc ?? []).SequenceEqual(b.Pmic?.Adc ?? []);
        public int GetHashCode(Ddr5Sample s) => s.Client.GetHashCode();
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("no drives");
        public void Dispose() { }
    }

    private sealed class FakeBuses : ISmbusProvider
    {
        private readonly Dictionary<int, FakeBus> _buses = [];
        public FakeBus Bus(int n) => _buses.TryGetValue(n, out var b) ? b : _buses[n] = new FakeBus();
        public ISmbus Open(int adapter) => Bus(adapter);
    }

    private sealed class FakeBus : ISmbus
    {
        private readonly Dictionary<(int, byte), ushort> _registers = [];
        public readonly Dictionary<(int, byte), byte[]> Blocks = [];
        public readonly Dictionary<int, Func<int, byte>> Adc = [];
        public readonly List<(int Address, byte Command, byte Value)> Writes = [];
        public readonly List<(int Address, byte Command)> Reads = [];

        public void Set(int address, byte command, byte value) => _registers[(address, command)] = value;
        public void SetWord(int address, byte command, ushort value) => _registers[(address, command)] = value;
        public byte Get(int address, byte command) => (byte)_registers[(address, command)];

        private ushort Value(int address, byte command)
        {
            Reads.Add((address, command));
            if (command == 0x31 && Adc.TryGetValue(address, out var adc)) return adc(Get(address, 0x30) >> 3 & 0x0F);
            return _registers.TryGetValue((address, command), out var v) ? v : throw new IOException($"NACK 0x{address:x2}");
        }

        public byte ReadByte(int address, byte command) => (byte)Value(address, command);
        public ushort ReadWord(int address, byte command) => Value(address, command);

        public byte[] ReadBlock(int address, byte command)
        {
            Reads.Add((address, command));
            return Blocks.TryGetValue((address, command), out var b) ? b : throw new IOException("NACK");
        }

        public void WriteByte(int address, byte command, byte value)
        {
            Writes.Add((address, command, value));
            _registers[(address, command)] = value;
        }

        public void Dispose() { }
    }
}
