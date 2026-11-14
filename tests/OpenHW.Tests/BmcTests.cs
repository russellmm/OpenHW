using System.Text;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class BmcTests
{
    /// <summary>A Full Sensor Record (type 01h) as an AST2600 stores it, with the conversion factors given.</summary>
    internal static byte[] FullRecord(ushort recordId, byte number, string name, byte baseUnit, int m, int b, int rexp, int bexp,
        byte entity = 7, int format = 0, byte readable = 0x3f)
    {
        var id = Encoding.ASCII.GetBytes(name);
        var r = new byte[48 + id.Length];
        r[0] = (byte)recordId; r[1] = (byte)(recordId >> 8); r[2] = 0x51; r[3] = 0x01; r[4] = (byte)(r.Length - 5);
        r[5] = 0x20; r[6] = 0; r[7] = number; r[8] = entity; r[9] = 0;
        r[12] = baseUnit switch { 1 => 0x01, 4 => 0x02, 18 => 0x04, _ => 0x00 };
        r[13] = 0x01; // threshold
        r[18] = readable;
        r[20] = (byte)(format << 6); r[21] = baseUnit;
        r[23] = 0; // linear
        r[24] = (byte)(m & 0xff); r[25] = (byte)((m >> 2) & 0xc0);
        r[26] = (byte)(b & 0xff); r[27] = (byte)((b >> 2) & 0xc0);
        r[29] = (byte)(((rexp & 0x0f) << 4) | (bexp & 0x0f));
        r[47] = (byte)(0xc0 | id.Length);
        id.CopyTo(r, 48);
        return r;
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
            ];
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
                case (0x04, 0x2d):
                    return Raw.TryGetValue(data[0], out var raw) && raw is { } v ? [0, v, 0xc0, 0] : [0, 0, 0x20, 0];
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
        Assert.Equal(["+12V", "+VCORE_0", "CPU Package Temp", "W_PUMP+", "PSU1 Power In", "PCIE01 Temp"], sensors.Select(s => s.Name));
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
        Assert.Equal(["bmc:+12V", "bmc:+VCORE_0", "bmc:CPU Package Temp", "bmc:W_PUMP+", "bmc:PCIE01 Temp"], items.Select(i => i.Id));
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
}
