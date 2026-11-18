using System.Buffers.Binary;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>WireView Pro II: frame decoding (built from the owner's device's own readings, 2026-11-17), the reader, the engine.</summary>
public sealed class WireViewTests
{
    /// <summary>A sensor frame as the device sends it (firmware TG-WV-PRO2-FW_20260430_1838).</summary>
    internal static byte[] Frame(short[]? temps = null, int fan = 0, (short mv, uint ma, uint mw)[]? pins = null, uint totalMw = 22990,
        uint totalMa = 1912, ushort avgMv = 12024, byte cap = 0, ushort status = 0, ushort log = 0)
    {
        var f = new byte[WireViewSample.Size];
        temps ??= [267, 274, -1000, -1000];
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(2 * i), temps[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(8), 3401);
        f[10] = (byte)fan;
        pins ??= [(12025, 312, 3750), (12026, 227, 2730), (12025, 263, 3160), (12029, 397, 4780), (12025, 338, 4060), (12019, 375, 4510)];
        for (var i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(f.AsSpan(12 + 12 * i), pins[i].mv);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16 + 12 * i), pins[i].ma);
            BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(20 + 12 * i), pins[i].mw);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(84), totalMw);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(88), totalMa);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(92), avgMv);
        f[94] = cap;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(96), status);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(98), log);
        return f;
    }

    [Fact]
    public void FrameDecodesAsTheProbeShowedIt()
    {
        var s = WireViewSample.Parse(Frame())!;
        Assert.Equal([26.7, 27.4, null, null], s.Temperatures);
        Assert.Equal((3.401, 0), (s.SupplyVolts, s.FanPercent));
        Assert.Equal([12.025, 12.026, 12.025, 12.029, 12.025, 12.019], s.PinVolts);
        Assert.Equal(0.397, s.PinAmps[3]);
        Assert.Equal(4.78, s.PinWatts[3]);
        Assert.Equal((22.99, 1.912, 12.024, 600), (s.TotalWatts, s.TotalAmps, s.AverageVolts, s.PsuCapabilityWatts!.Value));
        Assert.Empty(WireViewSample.Faults(s.FaultStatus));
        Assert.Equal(["over-current", "current imbalance"], WireViewSample.Faults(0b100100));
        // Out of step: fan duty over 100, or a padding byte set.
        var bad = Frame(); bad[10] = 150;
        Assert.Null(WireViewSample.Parse(bad));
        bad = Frame(); bad[95] = 1;
        Assert.Null(WireViewSample.Parse(bad));
        Assert.Null(WireViewSample.Parse(Frame().AsSpan(0, 99)));
    }

    private sealed class FakeLink(byte[] vendor, Queue<byte[]>? frames = null) : IWireViewLink
    {
        public readonly List<byte> Sent = [];
        public byte[] Command(byte command, int replySize)
        {
            Sent.Add(command);
            return command switch
            {
                WireViewReader.ReadVendorData => vendor,
                WireViewReader.ReadBuildInfo => [.. vendor, .. Pad("Thermal Grizzly WireView Pro II"), .. Pad("TG-WV-PRO2-FW_20260430_1838"), 31],
                WireViewReader.ReadSensorValues => frames is { Count: > 0 } ? frames.Dequeue() : Frame(),
                _ => throw new InvalidOperationException($"command 0x{command:x2} must never be sent"),
            };
        }
        private static byte[] Pad(string s) { var b = new byte[32]; System.Text.Encoding.ASCII.GetBytes(s).CopyTo(b, 0); return b; }
        public void Dispose() { }
    }

    [Fact]
    public void ReaderIdentifiesWireViewsSkipsOthersAndSendsOnlyReads()
    {
        var wv = new FakeLink([0xEF, 0x05, 0x05], new Queue<byte[]>([Frame(fan: 200), Frame()]));
        var other = new FakeLink([0x12, 0x34, 0x01]);
        using var reader = new WireViewReader(() => [("208E31635445", "/dev/ttyACM0"), ("OTHER", "/dev/ttyACM1")],
            port => port.EndsWith('0') ? wv : other);
        reader.Read(TimeSpan.FromSeconds(5));
        List<WireViewDevice> devices;
        var until = DateTime.UtcNow.AddSeconds(5);
        do { devices = reader.Read(TimeSpan.FromSeconds(1)); Thread.Sleep(100); }
        while (devices.FirstOrDefault(d => d.Serial == "208E31635445")?.Sample is null && DateTime.UtcNow < until);
        var d = devices.Single(x => x.Serial == "208E31635445");
        Assert.Equal(("Thermal Grizzly WireView Pro II", "TG-WV-PRO2-FW_20260430_1838"), (d.Product, d.Firmware));
        Assert.Equal(22.99, d.Sample!.TotalWatts); // the out-of-step frame was dropped, the next one kept
        Assert.StartsWith("not a WireView", devices.Single(x => x.Serial == "OTHER").Problem);
        Assert.All(wv.Sent.Concat(other.Sent), c => Assert.Contains(c, new byte[] { 0x01, 0x04, 0x0D }));
        Assert.DoesNotContain((byte)0x04, other.Sent);
    }

    [Fact]
    public void EngineMakesOneGroupPerDeviceWithEveryReading()
    {
        var reply = GpuWire.Encode(new List<WireViewDevice>
        {
            new("208E31635445", "WireView Pro II", "TG-WV-PRO2-FW_20260430_1838", "/dev/ttyACM0", WireViewSample.Parse(Frame()), null),
        });
        using var engine = new WireViewEngine(_ => reply, hwinfoLayout: false); // Linux's flat layout
        var items = engine.Discover();
        Assert.All(items, i => Assert.Equal("wireview:208E31635445", i.Group));
        Assert.Equal(2 + 18 + 4 + 3 + 2, items.Count); // 2 connector temps (no probes), 6 × V/A/W, 3 totals + imbalance, PSU + 2 fault counts, vdd + fan
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.Equal(22.99, readings["wireview:208E31635445:total:w"]);
        Assert.Equal(4.06, readings["wireview:208E31635445:pin5:w"]);
        Assert.Equal(600, readings["wireview:208E31635445:psu:limit"]);
        Assert.Equal(0, readings["wireview:208E31635445:faults:now"]);
        Assert.Null(readings["wireview:208E31635445:imbalance"]); // idle (~0.3 A a wire): too little current to judge
        Assert.Equal(47.2, WireViewEngine.Imbalance([8, 8, 8, 8, 8, 13])); // one wire at 13 A among 8 A ones
        Assert.Null(WireViewEngine.Imbalance([0.01, 0.02, 0, 0, 0, 0]));
        // An old helper (no `wireview`): no group, no exception.
        using var old = new WireViewEngine(_ => throw new IOException("openhw-helper: err unknown command"));
        Assert.Empty(old.Discover());
    }

    [Fact]
    public void WindowsUsesHwinfosLayout()
    {
        var reply = GpuWire.Encode(new List<WireViewDevice>
        {
            new("208E31635445", "WireView Pro II", "TG-WV-PRO2-FW_20260430_1838", "/dev/ttyACM0", WireViewSample.Parse(Frame()), null),
        });
        using var engine = new WireViewEngine(_ => reply, hwinfoLayout: true);
        var items = engine.Discover();
        const string p = "wireview:208E31635445:";
        // HWiNFO's order for the top-level rows (the pins sit under their connector row); the ids are Linux's.
        var top = items.Where(i => !engine.Children.Values.SelectMany(c => c).Contains(i.Id)).Select(i => i.Name).ToList();
        Assert.Equal(["Temperature In", "Temperature Out", "GPU 12VHPWR Connector Voltages", "Average Pin Voltage",
            "GPU 12VHPWR Connector Currents", "Total Current", "GPU 12VHPWR Connector Powers", "Total Power", "Fan Duty",
            "Faults (Status)", "Faults (Log)", "Current Imbalance", "PSU Power Limit (Sense Pins)", "WireView Supply Voltage"], top);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"{p}pin{i}:a").ToList(), engine.Children[p + "pins:a"]);
        Assert.Equal("Pin 5 Power", items.Single(i => i.Id == p + "pin5:w").Name);
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.Equal(22.99, readings[p + "total:w"]);
        Assert.Equal(Enumerable.Range(1, 6).Average(i => readings[$"{p}pin{i}:w"]!.Value), readings[p + "pins:w"]!.Value, 6);
    }

    [Fact]
    public void HelperAnswersWireview()
    {
        using var reader = new WireViewReader(() => [], _ => throw new IOException("none"));
        var session = new HelperSession(new NoSmart(), wireView: reader);
        Assert.Equal("ok 19", session.HandleLine("ping"));
        Assert.Empty(GpuWire.Decode<List<WireViewDevice>>(session.HandleLine("wireview")[3..])!);
        Assert.Equal("err WireView reader unavailable", new HelperSession(new NoSmart()).HandleLine("wireview"));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("none");
        public void Dispose() { }
    }
}
