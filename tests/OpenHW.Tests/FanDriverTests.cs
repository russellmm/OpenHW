using System.Globalization;
using System.Text.Json;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>
/// The fan driver against a simulated WRX90E BMC that answers with the replies OpenFan captured from the real one
/// (Fixtures/asus-wrx90-bmc.json) and keeps each zone's curve as the firmware would.
/// </summary>
public sealed class FanDriverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openhw-fans").FullName;
    private string Journal => Path.Combine(_dir, "fan-restore.json");
    private string OpenFanJournal => Path.Combine(_dir, "openfan-bmc-restore.json");
    private DateTime _now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);
    private const string A = "0123456789abcdef0123456789abcdef", B = "fedcba9876543210fedcba9876543210";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    internal sealed class FakeAsusBmc : IIpmiTransport
    {
        public readonly Dictionary<int, byte[]> States = [];
        private readonly Dictionary<int, byte[]> _tables = [];
        private readonly byte[] _count, _order;
        public string Firmware = "2.01";
        public bool RefuseWrites = false, ReadbackWrong;
        public int Writes;
        public readonly List<string> Unexpected = [];

        public FakeAsusBmc()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "asus-wrx90-bmc.json")));
            byte[] Hex(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => byte.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
            byte[] count = [], order = [];
            foreach (var c in doc.RootElement.GetProperty("Commands").EnumerateArray())
            {
                var args = c.GetProperty("Arguments").EnumerateArray().Select(a => a.GetString()!).ToArray();
                var output = c.GetProperty("Output").GetString() ?? "";
                if (args is [_, _, "raw", "0x30", "0x13", "0x01", "0x00"]) count = Hex(output);
                else if (args is [_, _, "raw", "0x30", "0x13", "0x03", "0x00"]) order = Hex(output);
                else if (args is [_, _, "raw", "0x30", "0x13", "0x00", var t]) _tables[Convert.ToInt32(t, 16)] = Hex(output);
                else if (args is [_, _, "raw", "0x30", "0x0f", var z]) States[Convert.ToInt32(z, 16)] = Hex(output);
            }
            (_count, _order) = (count, order);
        }

        public void RenameTable(int zone, string name)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(name.PadRight(32, '\0'));
            bytes.CopyTo(_tables[zone], 0);
        }

        public byte[] Send(byte netFn, byte command, byte lun, ReadOnlySpan<byte> request)
        {
            var data = request.ToArray();
            switch (netFn, command)
            {
                case (0x06, 0x01): // Get Device ID as the AST2600 answers it (ASUS 2623, product 0x1193, aux 22 00 00 00)
                    var fw = Firmware.Split('.');
                    return [0, 0x20, 0x01, byte.Parse(fw[0], CultureInfo.InvariantCulture), Convert.ToByte(fw[1], 16), 0x02, 0xbf,
                        0x3f, 0x0a, 0x00, 0x93, 0x11, 0x22, 0, 0, 0];
                case (0x30, 0x13) when data is [0x01, 0x00]: return [0, .. _count];
                case (0x30, 0x13) when data is [0x03, 0x00]: return [0, .. _order];
                case (0x30, 0x13) when data.Length == 2 && data[0] == 0 && _tables.TryGetValue(data[1], out var table): return [0, .. table];
                case (0x30, 0x0f) when data.Length == 1 && States.TryGetValue(data[0], out var state): return [0, .. state];
                case (0x30, 0x0e) when data.Length == 12 && data[0] == 0x04:
                    if (RefuseWrites) return [0xc1];
                    if (data[3] == 0 || data[5] == 0 || data[7] == 0 || data[9] == 0 || data[11] == 0) return [0xd9]; // firmware: no 0 % points
                    Writes++;
                    States[data[1]] = [0x04, .. data[2..]];
                    data.AsSpan(2, 10).CopyTo(_tables[data[1]].AsSpan(34)); // the table's copy of the active curve
                    if (ReadbackWrong) States[data[1]][2]++;
                    return [0];
                default:
                    Unexpected.Add($"{netFn:x2}/{command:x2}");
                    return [0xc1];
            }
        }

        public void Dispose() { }
    }

    private FanDriver Driver(FakeAsusBmc bmc, string board = AsusBmcFans.Board, string identity = "machine-1") =>
        new(() => (AsusBmcFans.Open(bmc, AsusBmcFans.Vendor, board, out var reason), reason), Journal, identity,
            () => _now, () => File.Exists(OpenFanJournal) ? "OpenFan is controlling BMC fans right now." : null);

    [Fact]
    public void OpensOnlyOnTheValidatedBoardBmcAndTables()
    {
        var bmc = new FakeAsusBmc();
        using (var ok = Driver(bmc)) Assert.True(ok.Status(null).Available);
        using (var other = Driver(new FakeAsusBmc(), board: "Pro WS TRX50-SAGE WIFI A"))
            Assert.Contains("validated only", other.Status(null).Reason);
        using (var newer = Driver(new FakeAsusBmc { Firmware = "2.02" }))
            Assert.Contains("Unvalidated BMC", newer.Status(null).Reason);
        var renamed = new FakeAsusBmc();
        renamed.RenameTable(3, "Zone4(SOMETHING)");
        using (var changed = Driver(renamed))
        {
            Assert.Contains("zone 3", changed.Status(null).Reason);
            Assert.Throws<ArgumentException>(() => changed.Set(A, "bmc:1", 70));
        }
        Assert.Empty(bmc.Unexpected); // identity checks send nothing but Get Device ID and the OEM reads
    }

    [Fact]
    public void ATakeoverJournalsTheCurveWritesAFlatDutyAndReleaseRestoresItExactly()
    {
        var bmc = new FakeAsusBmc();
        var original = (byte[])bmc.States[1].Clone(); // CHA_FAN1: 20→20, 45→40, 65→70, 70→100, 100→100
        using var fans = Driver(bmc);
        Assert.Equal("bmc:1 70", fans.Set(A, "bmc:1", 70));
        Assert.Equal(new byte[] { 0x04, 20, 70, 45, 70, 65, 70, 70, 70, 100, 70 }, bmc.States[1]); // temperatures kept
        Assert.True(File.Exists(Journal));
        var zone = fans.Status(A).Zones[1];
        Assert.Equal(("CHA_FAN1", "bmc:CHA_FAN1", true, true, 70, 1), (zone.Name, zone.RpmSensorId, zone.Owned, zone.OwnedByYou, zone.Target, zone.Writes));
        Assert.Equal([new(20, 20), new(45, 40), new(65, 70), new(70, 100), new(100, 100)], zone.Original!);

        Assert.Equal("bmc:1", fans.Release(A, "bmc:1"));
        Assert.Equal(original, bmc.States[1]);
        Assert.False(File.Exists(Journal));
        Assert.Null(fans.Status(null).Zones[1].Target);
    }

    [Fact]
    public void OneClientPerZoneAndNeverWhileOpenFanHoldsBmcFans()
    {
        var bmc = new FakeAsusBmc();
        using var fans = Driver(bmc);
        fans.Set(A, "bmc:2", 60);
        Assert.Contains("another client", Assert.Throws<ArgumentException>(() => fans.Set(B, "bmc:2", 80)).Message);
        Assert.Contains("another client", Assert.Throws<ArgumentException>(() => fans.Release(B, "bmc:2")).Message);
        Assert.Equal("nothing held", fans.Release(B, "all"));

        File.WriteAllText(OpenFanJournal, "{}");
        Assert.Contains("OpenFan", Assert.Throws<ArgumentException>(() => fans.Set(B, "bmc:3", 50)).Message);
        Assert.Equal("bmc:2 70", fans.Set(A, "bmc:2", 70)); // a zone already held keeps working
        Assert.Throws<ArgumentException>(() => fans.Set(A, "bmc:2", 0)); // the firmware refuses 0 %
        Assert.Throws<ArgumentException>(() => fans.Set("not-a-token", "bmc:2", 50));
        Assert.Throws<ArgumentException>(() => fans.Set(A, "bmc:7", 50)); // the eighth slot isn't a fan
    }

    [Fact]
    public void AClientThatStopsRenewingGetsItsZonesRestored()
    {
        var bmc = new FakeAsusBmc();
        var original = (byte[])bmc.States[5].Clone();
        using var fans = Driver(bmc);
        fans.Set(A, "bmc:5", 90);
        _now += TimeSpan.FromSeconds(10);
        Assert.Equal(1, fans.Beat(A));
        _now += TimeSpan.FromSeconds(10);
        fans.Tick();
        Assert.Equal(90, fans.Status(null).Zones[5].Target); // renewed 10 s ago
        _now += TimeSpan.FromSeconds(6);
        fans.Tick();
        Assert.Equal(original, bmc.States[5]);
        Assert.False(fans.Status(null).Zones[5].Owned);
    }

    [Fact]
    public void FasterIsWrittenAtOnceSlowerWaitsForTheFlashLimits()
    {
        var bmc = new FakeAsusBmc();
        using var fans = Driver(bmc);
        fans.Set(A, "bmc:3", 50);                                  // takeover: 1 write
        _now += TimeSpan.FromSeconds(2);
        Assert.Equal("bmc:3 80", fans.Set(A, "bmc:3", 80));        // faster: at once (2)
        _now += TimeSpan.FromSeconds(2);
        Assert.Equal("bmc:3 80 pending 60", fans.Set(A, "bmc:3", 60)); // slower within 30 s: waits
        Assert.Equal(80, bmc.States[3][2]);
        Assert.Equal("bmc:3 80 (within 3 %)", fans.Set(A, "bmc:3", 78)); // too small a step to spend a write on
        fans.Set(A, "bmc:3", 60);
        for (var s = 0; s < 27; s++) { _now += TimeSpan.FromSeconds(1); fans.Beat(A); fans.Tick(); } // a client renews
        Assert.Equal(80, bmc.States[3][2]);                        // 29 s after the last write
        _now += TimeSpan.FromSeconds(2);
        fans.Beat(A);
        fans.Tick();
        Assert.Equal(60, bmc.States[3][2]);                        // the pending duty, once due (3)
        Assert.Equal(3, fans.Status(A).Zones[3].Writes);
        Assert.Equal(3, bmc.Writes);
        _now += TimeSpan.FromSeconds(1);
        fans.Set(A, "bmc:3", 60);                                  // the same duty again: no write
        Assert.Equal(3, bmc.Writes);
    }

    [Fact]
    public void AfterACrashTheNextStartRestoresTheJournaledCurves()
    {
        var bmc = new FakeAsusBmc();
        var original = (byte[])bmc.States[2].Clone(); // CHA_FAN2
        var crashed = Driver(bmc);
        crashed.Set(A, "bmc:2", 90);
        // (no Dispose: the helper died with CHA_FAN2 on OpenHW's setting)
        Assert.NotEqual(original, bmc.States[2]);
        using var next = Driver(bmc);
        Assert.Equal(original, bmc.States[2]);
        Assert.False(File.Exists(Journal));
        Assert.True(next.Status(null).Available);
    }

    [Fact]
    public void AJournalFromAnotherMachineDisablesWrites()
    {
        var bmc = new FakeAsusBmc();
        Driver(bmc, identity: "machine-1").Set(A, "bmc:1", 70);
        using var other = Driver(bmc, identity: "machine-2");
        Assert.False(other.Status(null).Available);
        Assert.Contains("doesn't match this machine", other.Status(null).Reason);
    }

    [Fact]
    public void ARefusedOrWrongWriteLeavesTheFirmwareCurve()
    {
        var bmc = new FakeAsusBmc { ReadbackWrong = true };
        var original = (byte[])bmc.States[1].Clone();
        using var fans = Driver(bmc);
        var error = Assert.Throws<IOException>(() => fans.Set(A, "bmc:1", 70));
        Assert.Contains("didn't read back", error.Message);
        bmc.ReadbackWrong = false;
        fans.Release(A, "all");
        Assert.Equal(original, bmc.States[1]);
        Assert.False(fans.Status(null).Zones[1].Owned);
    }

    [Fact]
    public void DisposingTheDriverGivesEveryZoneBack()
    {
        var bmc = new FakeAsusBmc();
        var originals = bmc.States.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone());
        using (var fans = Driver(bmc))
        {
            fans.Set(A, "bmc:0", 40);
            fans.Set(B, "bmc:4", 50);
        }
        foreach (var (zone, state) in originals) Assert.Equal(state, bmc.States[zone]);
    }

    [Fact]
    public void HelperCommandsDriveTheZones()
    {
        var bmc = new FakeAsusBmc();
        using var fans = Driver(bmc);
        var helper = new HelperSession(new NoSmart(), fans: fans);
        Assert.Equal("ok bmc:1 70", helper.HandleLine($"fanset {A} bmc:1 70"));
        Assert.Equal("ok 1", helper.HandleLine($"fanbeat {A}"));
        var status = GpuWire.Decode<FanDriverStatus>(helper.HandleLine($"fans {A}")[3..])!;
        Assert.True(status.Zones[1].OwnedByYou);
        Assert.Equal("err bmc:1 is controlled by another client", helper.HandleLine($"fanset {B} bmc:1 50"));
        Assert.Equal("ok bmc:1", helper.HandleLine($"fanrelease {A} all"));
        Assert.Equal("err unknown command", helper.HandleLine($"fanset {A} bmc:1"));
        Assert.Equal("err fan control unavailable", new HelperSession(new NoSmart()).HandleLine("fans"));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("none");
        public void Dispose() { }
    }
}
