using System.Text;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class BmcTests
{
    /// <summary>A Full Sensor Record (type 01h) as an AST2600 stores it, with the conversion factors given.</summary>
    internal static byte[] FullRecord(ushort recordId, byte number, string name, byte baseUnit, int m, int b, int rexp, int bexp,
        byte entity = 7, int format = 0, byte readable = 0x3f, byte settable = 0)
    {
        var id = Encoding.ASCII.GetBytes(name);
        var r = new byte[48 + id.Length];
        r[0] = (byte)recordId; r[1] = (byte)(recordId >> 8); r[2] = 0x51; r[3] = 0x01; r[4] = (byte)(r.Length - 5);
        r[5] = 0x20; r[6] = 0; r[7] = number; r[8] = entity; r[9] = 0;
        r[12] = baseUnit switch { 1 => 0x01, 4 => 0x02, 18 => 0x04, _ => 0x00 };
        r[13] = 0x01; // threshold
        r[18] = readable; r[19] = settable;
        r[20] = (byte)(format << 6); r[21] = baseUnit;
        r[23] = 0; // linear
        r[24] = (byte)(m & 0xff); r[25] = (byte)((m >> 2) & 0xc0);
        r[26] = (byte)(b & 0xff); r[27] = (byte)((b >> 2) & 0xc0);
        r[29] = (byte)(((rexp & 0x0f) << 4) | (bexp & 0x0f));
        r[47] = (byte)(0xc0 | id.Length);
        id.CopyTo(r, 48);
        return r;
    }

    /// <summary>A Compact Sensor Record (02h) for a discrete sensor, as the AST2600 stores CPU_ECC or PSU1 Detect.</summary>
    internal static byte[] CompactRecord(ushort recordId, byte number, string name, byte sensorType, byte eventType = 0x6f, byte entity = 8)
    {
        var id = Encoding.ASCII.GetBytes(name);
        var r = new byte[32 + id.Length];
        r[0] = (byte)recordId; r[1] = (byte)(recordId >> 8); r[2] = 0x51; r[3] = 0x02; r[4] = (byte)(r.Length - 5);
        r[5] = 0x20; r[7] = number; r[8] = entity; r[12] = sensorType; r[13] = eventType;
        r[31] = (byte)(0xc0 | id.Length);
        id.CopyTo(r, 32);
        return r;
    }

    /// <summary>A 16-byte system event record (type 02h).</summary>
    internal static byte[] SelRecord(ushort id, uint time, byte sensorType, byte sensor, byte dirType, byte data1, byte data2 = 0xff, byte data3 = 0xff)
    {
        var t = BitConverter.GetBytes(time);
        return [(byte)id, (byte)(id >> 8), 0x02, t[0], t[1], t[2], t[3], 0x20, 0x00, 0x04, sensorType, sensor, dirType, data1, data2, data3];
    }

    /// <summary>A management controller locator (12h) naming the chip, as the AST2600 reports itself.</summary>
    private static byte[] ControllerRecord(ushort recordId, string name)
    {
        var id = Encoding.ASCII.GetBytes(name);
        var r = new byte[16 + id.Length];
        r[0] = (byte)recordId; r[3] = 0x12; r[4] = (byte)(r.Length - 5); r[5] = 0x20; r[15] = (byte)(0xc0 | id.Length);
        id.CopyTo(r, 16);
        return r;
    }

    /// <summary>
    /// A BMC on the far side of /dev/ipmi0: answers Get Device ID, the SDR repository (16-byte partial reads, one
    /// reservation cancelled mid-walk) and per-sensor readings and thresholds. Records any command it isn't meant to get.
    /// </summary>
    internal sealed class FakeBmc : IIpmiTransport
    {
        public readonly List<byte[]> Records;
        public readonly Dictionary<byte, byte?> Raw = []; // null = reading unavailable
        public readonly List<(byte NetFn, byte Cmd)> Unexpected = [];
        public readonly Dictionary<byte, int> States = [];
        public readonly Dictionary<byte, byte[]> Thresholds = []; // lnc, lcr, lnr raw
        public int ThresholdWrites;
        public bool RefuseThresholds;
        public readonly List<byte[]> Sel = [];
        public uint SelLastAdd = 0x6739a000;
        public int SelEntryReads;
        private ushort _reservation = 1;
        private bool _cancelledOnce;

        public FakeBmc()
        {
            Records =
            [
                ControllerRecord(0, "AST2600"),
                FullRecord(1, 0x24, "+12V", 4, m: 6, b: 0, rexp: -2, bexp: 0),          // 197 × 0.06 = 11.82 V
                FullRecord(2, 0x1e, "+VCORE_0", 4, m: 8, b: 0, rexp: -3, bexp: 0),      // 109 × 0.008 = 0.872 V
                FullRecord(3, 0x01, "CPU Package Temp", 1, m: 1, b: 0, rexp: 0, bexp: 0, entity: 3),
                FullRecord(4, 0x39, "W_PUMP+", 18, m: 60, b: 0, rexp: 0, bexp: 0, entity: 29),
                FullRecord(5, 0xdf, "PSU1 Power In", 6, m: 2, b: 0, rexp: 0, bexp: 0, entity: 10),
                FullRecord(6, 0x0c, "PCIE01 Temp", 1, m: 1, b: -10, rexp: 0, bexp: 0, entity: 11, format: 2),
                FullRecord(10, 0x35, "CHA_FAN2", 18, m: 120, b: 0, rexp: 0, bexp: 0, entity: 29, readable: 0x07, settable: 0x07),
                CompactRecord(7, 0xf9, "CPU_ECC", 0x0c),
                CompactRecord(8, 0xe3, "PSU1 Detect", 0x08, entity: 10),
                CompactRecord(9, 0xfc, "Memory_Train_ERR", 0xc5),
            ];
            Raw[0x35] = 3; Thresholds[0x35] = [3, 3, 0]; // 360 RPM; lnc 360, lcr 360, lnr 0 as on the WRX90E
            States[0xf9] = 1 << 6; States[0xe3] = 1 << 1; States[0xfc] = 0;
            Raw[0x24] = 197; Raw[0x1e] = 109; Raw[0x01] = 40; Raw[0x39] = 64; Raw[0xdf] = null; Raw[0x0c] = 43;
        }

        public byte[] Send(byte netFn, byte command, byte lun, ReadOnlySpan<byte> data)
        {
            switch (netFn, command)
            {
                case (0x06, 0x01): // Get Device ID: AST2600 firmware 2.01, ASUS (2623), product 0x1193
                    return [0, 0x20, 0x01, 0x02, 0x01, 0x02, 0xbf, 0x3f, 0x0a, 0x00, 0x93, 0x11];
                case (0x0a, 0x22):
                    _reservation++;
                    return [0, (byte)_reservation, (byte)(_reservation >> 8)];
                case (0x0a, 0x23):
                {
                    var reservation = (ushort)(data[0] | data[1] << 8);
                    var id = (ushort)(data[2] | data[3] << 8);
                    int offset = data[4], count = data[5];
                    if (offset > 0 && reservation != _reservation) return [0xc5];
                    if (id == 3 && offset == 16 && !_cancelledOnce) { _cancelledOnce = true; _reservation++; return [0xc5]; }
                    if (count > 16 && count != 0xff) return [0xca];
                    var index = Records.FindIndex(r => (r[0] | r[1] << 8) == id);
                    if (index < 0) return [0xcb];
                    var record = Records[index];
                    var next = index + 1 < Records.Count ? (ushort)(Records[index + 1][0] | Records[index + 1][1] << 8) : (ushort)0xffff;
                    var take = Math.Min(count, record.Length - offset);
                    return [0, (byte)next, (byte)(next >> 8), .. record.AsSpan(offset, take)];
                }
                case (0x04, 0x2d) when States.TryGetValue(data[0], out var bits):
                    return [0, 0, 0xc0, (byte)bits, (byte)(bits >> 8)];
                case (0x0a, 0x40): // Get SEL Info
                {
                    var add = BitConverter.GetBytes(SelLastAdd);
                    return [0, 0x51, (byte)Sel.Count, (byte)(Sel.Count >> 8), 0x00, 0x40, .. add, 0, 0, 0, 0, 0x02];
                }
                case (0x0a, 0x43): // Get SEL Entry (whole records, no reservation)
                {
                    SelEntryReads++;
                    var id = (ushort)(data[2] | data[3] << 8);
                    var index = id == 0 ? 0 : Sel.FindIndex(r => (r[0] | r[1] << 8) == id);
                    if (index < 0 || index >= Sel.Count) return [0xcb];
                    var next = index + 1 < Sel.Count ? (ushort)(Sel[index + 1][0] | Sel[index + 1][1] << 8) : (ushort)0xffff;
                    return [0, (byte)next, (byte)(next >> 8), .. Sel[index]];
                }
                case (0x04, 0x2d):
                    return Raw.TryGetValue(data[0], out var raw) && raw is { } v ? [0, v, 0xc0, 0] : [0, 0, 0x20, 0];
                case (0x04, 0x27) when Thresholds.TryGetValue(data[0], out var t):
                    return [0, 0x07, t[0], t[1], t[2], 0, 0, 0];
                case (0x04, 0x26) when Thresholds.ContainsKey(data[0]):
                    if (RefuseThresholds) return [0xcc];
                    if (data[1] != 0x07) { Unexpected.Add((netFn, command)); return [0xcc]; }
                    ThresholdWrites++;
                    Thresholds[data[0]] = [data[2], data[3], data[4]];
                    return [0];
                case (0x04, 0x27):
                    return data[0] == 0x24 ? [0, 0x3f, 180, 170, 160, 220, 230, 240] /* LNC LC LNR UNC UC UNR, as ipmitool lists +12V */ : [0, 0x00, 0, 0, 0, 0, 0, 0];
                default:
                    Unexpected.Add((netFn, command));
                    return [0xc1];
            }
        }

        public void Dispose() { }
    }

    [Fact]
    public void FullRecordsConvertWithTheirOwnFactors()
    {
        var bmc = new FakeBmc();
        var sensors = IpmiCommands.ReadSdr(bmc).Select(r => BmcSensor.Parse(r)).OfType<BmcSensor>().ToList();
        Assert.Equal(["+12V", "+VCORE_0", "CPU Package Temp", "W_PUMP+", "PSU1 Power In", "PCIE01 Temp", "CHA_FAN2"], sensors.Select(s => s.Name));
        Assert.Equal(11.82, sensors[0].Convert(197));
        Assert.Equal(0.872, sensors[1].Convert(109));
        Assert.Equal(3840, sensors[3].Convert(64));
        Assert.Equal(-15, sensors[5].Convert(0xfb)); // two's complement −5, B = −10
        Assert.Equal((HardwareKind.Tach, "RPM", "Fan"), (sensors[3].Kind, sensors[3].Unit, sensors[3].Entity));
        Assert.Empty(bmc.Unexpected);
    }

    [Fact]
    public void ReaderSendsOnlyReadCommandsAndLeavesUnavailableSensorsEmpty()
    {
        var bmc = new FakeBmc();
        using var reader = new BmcReader(() => bmc);
        var reply = reader.Read(TimeSpan.FromSeconds(10));
        Assert.NotNull(reply);
        Assert.Equal(("AST2600", "2.01", 2623, 0x1193), (reply.Controller, reply.Firmware, reply.ManufacturerId, reply.ProductId));
        Assert.Equal(11.82, reply.Sensors.Single(s => s.Name == "+12V").Value);
        Assert.Null(reply.Sensors.Single(s => s.Name == "PSU1 Power In").Value);
        var limits = reply.Sensors.Single(s => s.Name == "+12V").Limits!;
        Assert.Equal((10.8, 13.2, 14.4), (limits.LowerNonCritical!.Value, limits.UpperNonCritical!.Value, limits.UpperNonRecoverable!.Value));
        Assert.Empty(bmc.Unexpected); // nothing but Get Device ID, SDR reads, readings and thresholds
    }

    [Fact]
    public void HelperAnswersBmcAndTheEngineShowsOnlySensorsThatRead()
    {
        using var reader = new BmcReader(() => new FakeBmc());
        var session = new HelperSession(new NoSmart(), bmc: reader);
        var line = session.HandleLine("bmc");
        Assert.StartsWith("ok ", line);
        Assert.Equal("err unknown command", session.HandleLine("bmc set"));

        using var engine = new BmcEngine(_ => line[3..]);
        Assert.Empty(engine.Discover()); // the first answer comes off the sampling thread
        for (var i = 0; i < 50 && engine.Discover().Count == 0; i++) Thread.Sleep(20);
        var items = engine.Discover();
        Assert.Equal(["bmc:+12V", "bmc:+VCORE_0", "bmc:CPU Package Temp", "bmc:W_PUMP+", "bmc:PCIE01 Temp", "bmc:CHA_FAN2"], items.Select(i => i.Id));
        Assert.All(items, i => Assert.Equal(BmcEngine.GroupKey, i.Group));
        Assert.Contains("BMC limits (V): lower critical 10.2, lower warning 10.8, upper warning 13.2, upper critical 13.8", items[0].Details);
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.Equal(3840, readings["bmc:W_PUMP+"]);
        Assert.Null(readings["bmc:PSU1 Power In"]);
        Assert.Equal(("AST2600", "2.01"), engine.Controller);
    }

    [Fact]
    public void WithoutAnIpmiDeviceTheHelperSaysSo()
    {
        using var reader = new BmcReader(() => null);
        Assert.Equal("err no IPMI device (/dev/ipmi0)", new HelperSession(new NoSmart(), bmc: reader).HandleLine("bmc"));
        Assert.Equal("err no BMC", new HelperSession(new NoSmart()).HandleLine("bmc"));
    }

    [Fact]
    public void RepeatedNamesGetDistinctIds()
    {
        var reply = new BmcWireReply("AST2600", "2.01", 0, 0,
        [
            new("FAN", 1, "Fan", HardwareKind.Tach, "RPM", null, 1), new("FAN", 2, "Fan", HardwareKind.Tach, "RPM", null, 2),
        ]);
        Assert.Equal(["bmc:FAN", "bmc:FAN#2"], BmcEngine.SensorIds(reply));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("none");
        public void Dispose() { }
    }

    [Fact]
    public void DiscreteSensorsReadAsStatesInWords()
    {
        var bmc = new FakeBmc();
        using var reader = new BmcReader(() => bmc);
        var states = reader.States(TimeSpan.FromSeconds(10))!;
        Assert.Equal(["CPU_ECC", "PSU1 Detect", "Memory_Train_ERR"], states.Select(x => x.Name));
        Assert.Equal(["presence detected"], states[0].States);
        Assert.Equal(("Memory", "Memory module"), (states[0].SensorType, states[0].Entity));
        Assert.Equal(["failure detected"], states[1].States);
        Assert.Empty(states[2].States);
        Assert.Equal("OEM C5h", states[2].SensorType);
        // Threshold sensors stay where they were; discrete ones aren't among them.
        Assert.DoesNotContain(reader.Read(TimeSpan.FromSeconds(1))!.Sensors, x => x.Name == "CPU_ECC");
        Assert.Empty(bmc.Unexpected);
    }

    [Fact]
    public void SelIsDecodedAndReadAgainOnlyWhenItChanges()
    {
        var bmc = new FakeBmc();
        bmc.Sel.Add(SelRecord(1, 0x6739a000, 0x0c, 0xf9, 0x6f, 0x00));          // correctable ECC on CPU_ECC
        bmc.Sel.Add(SelRecord(2, 0x6739a010, 0x0c, 0xf9, 0xef, 0x00));          // … deasserted
        bmc.Sel.Add(SelRecord(3, 0x6739a020, 0x01, 0x01, 0x01, 0x59, 0x5a, 0x55)); // CPU Package upper critical going high
        bmc.Sel.Add(SelRecord(4, 100, 0x10, 0x00, 0x6f, 0x02));                 // log cleared, before the clock was set
        using var reader = new BmcReader(() => bmc);
        var sel = reader.Sel(TimeSpan.FromSeconds(10))!;
        Assert.Equal(4, sel.Entries);
        Assert.Equal(("CPU_ECC", "Memory", "correctable ECC"), (sel.Newest[0].Sensor, sel.Newest[0].SensorType, sel.Newest[0].Event));
        Assert.True(sel.Newest[1].Deasserted);
        Assert.Equal("correctable ECC — deasserted", sel.Newest[1].Event);
        Assert.Equal("CPU Package Temp", sel.Newest[2].Sensor);
        Assert.Equal("upper critical going high (reading 0x5a)", sel.Newest[2].Event);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0x6739a020).UtcDateTime, sel.Newest[2].TimeUtc);
        Assert.Equal(("+100 s after BMC start", "log area reset/cleared"), (sel.Newest[3].Time, sel.Newest[3].Event));
        var reads = bmc.SelEntryReads;
        reader.Sel(TimeSpan.FromSeconds(1));
        Assert.Equal(reads, bmc.SelEntryReads); // unchanged SEL: served from the last walk
        bmc.Sel.Add(SelRecord(5, 0x6739b000, 0x23, 0xfe, 0x6f, 0x01));
        bmc.SelLastAdd = 0x6739b000;
        Assert.Equal("hard reset", reader.Sel(TimeSpan.FromSeconds(1))!.Newest[^1].Event);
        Assert.Empty(bmc.Unexpected);
    }

    [Fact]
    public void HelperAnswersBmcStatesAndSel()
    {
        using var reader = new BmcReader(() => new FakeBmc());
        var session = new HelperSession(new NoSmart(), bmc: reader);
        Assert.Equal("ok 18", session.HandleLine("ping"));
        Assert.Equal(3, GpuWire.Decode<List<BmcStateWire>>(session.HandleLine("bmcstates")[3..])!.Count);
        Assert.Equal(0, GpuWire.Decode<BmcSelReply>(session.HandleLine("bmcsel")[3..])!.Entries);
        Assert.Equal("err no BMC", new HelperSession(new NoSmart()).HandleLine("bmcsel"));
    }

    [Fact]
    public void FanThresholdsAreSetInStepsWithTheOriginalsKept()
    {
        var dir = Directory.CreateTempSubdirectory("openhw-thresh-").FullName;
        var path = Path.Combine(dir, "bmc-thresholds-original.json");
        var bmc = new FakeBmc();
        var logged = new List<string>();
        using var reader = new BmcReader(() => bmc, path, () => "machine-1") { Log = logged.Add };
        var fan = reader.FanThresholds(TimeSpan.FromSeconds(10))!.Single(f => f.Name == "CHA_FAN2");
        Assert.Equal((360.0, 360.0, 0.0, 120.0, true), (fan.NonCritical!.Value, fan.Critical!.Value, fan.NonRecoverable!.Value, fan.Step, fan.Settable));
        Assert.Null(fan.Original);
        Assert.DoesNotContain(reader.FanThresholds(TimeSpan.Zero)!, f => f.Name == "+12V"); // fans only
        Assert.False(reader.FanThresholds(TimeSpan.Zero)!.Single(f => f.Name == "W_PUMP+").Settable);

        Assert.Throws<ArgumentException>(() => reader.SetFanThresholds("CHA_FAN2", 0, 100, 240));   // not a 120 RPM step
        Assert.Throws<ArgumentException>(() => reader.SetFanThresholds("CHA_FAN2", 0, 240, 120));   // out of order
        Assert.Throws<ArgumentException>(() => reader.SetFanThresholds("W_PUMP+", 0, 120, 240));    // not settable
        Assert.Throws<ArgumentException>(() => reader.SetFanThresholds("+12V", 0, 120, 240));       // not a fan
        Assert.Equal(0, bmc.ThresholdWrites);

        var after = reader.SetFanThresholds("CHA_FAN2", 0, 120, 240);
        Assert.Equal((240.0, 120.0, 0.0), (after.LowerNonCritical!.Value, after.LowerCritical!.Value, after.LowerNonRecoverable!.Value));
        Assert.Equal(new byte[] { 2, 1, 0 }, bmc.Thresholds[0x35]);
        Assert.Equal(["CHA_FAN2 lower thresholds 0/360/360 → 0/120/240 RPM"], logged);
        Assert.Equal([0.0, 360, 360], reader.FanThresholds(TimeSpan.Zero)!.Single(f => f.Name == "CHA_FAN2").Original);

        reader.SetFanThresholds("CHA_FAN2", 0, 120, 240); // same values: no write
        Assert.Equal(1, bmc.ThresholdWrites);
        reader.SetFanThresholds("CHA_FAN2", 0, 0, 120);   // a second change keeps the first original
        reader.RestoreFanThresholds("CHA_FAN2");
        Assert.Equal(new byte[] { 3, 3, 0 }, bmc.Thresholds[0x35]);

        bmc.RefuseThresholds = true;
        Assert.Contains("refused", Assert.Throws<IOException>(() => reader.SetFanThresholds("CHA_FAN2", 0, 120, 240)).Message);
        Assert.Empty(bmc.Unexpected);

        // Another machine's originals file is ignored.
        using var other = new BmcReader(() => new FakeBmc(), path, () => "machine-2");
        Assert.Null(other.FanThresholds(TimeSpan.FromSeconds(10))!.Single(f => f.Name == "CHA_FAN2").Original);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void HelperValidatesThresholdCommands()
    {
        var dir = Directory.CreateTempSubdirectory("openhw-thresh-").FullName;
        using var reader = new BmcReader(() => new FakeBmc(), Path.Combine(dir, "o.json"), () => "m") { Log = _ => { } };
        var session = new HelperSession(new NoSmart(), bmc: reader);
        Assert.Equal("ok 18", session.HandleLine("ping"));
        Assert.Contains(GpuWire.Decode<List<BmcFanThresholds>>(session.HandleLine("bmcthresh")[3..])!, f => f.Name == "CHA_FAN2");
        Assert.StartsWith("ok ", session.HandleLine("bmcthresh CHA_FAN2 0 120 240"));
        Assert.Equal("err invalid sensor name", session.HandleLine("bmcthresh ../x 0 120 240"));
        Assert.Equal("err expected RPM values", session.HandleLine("bmcthresh CHA_FAN2 0 -1 240"));
        Assert.StartsWith("err ", session.HandleLine("bmcthresh CHA_FAN2 0 130 240"));
        Assert.StartsWith("ok ", session.HandleLine("bmcthreshoriginal CHA_FAN2"));
        Assert.StartsWith("err OpenHW hasn't changed", session.HandleLine("bmcthreshoriginal W_PUMP+"));
        Directory.Delete(dir, true);
    }
}
