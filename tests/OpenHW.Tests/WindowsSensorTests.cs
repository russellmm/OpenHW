using OpenHW.Hardware;
using OpenHW.Hardware.Windows;
using Xunit;

namespace OpenHW.Tests;

public sealed class WindowsSensorTests
{
    // k10temp's decoding (drivers/hwmon/k10temp.c), checked against this machine's first live read (2026-11-18).
    [WindowsFact]
    public void K10TempDecodesTctlAndCcdsAsLinuxDoes()
    {
        Assert.Equal(41.625, K10Temp.Tctl(333u << 21));                   // range-select clear
        Assert.Equal(41.625, K10Temp.Tctl((725u << 21) | 0x80000));       // range-select: −49 °C
        Assert.Equal(42.25, K10Temp.Ccd(0x800 | 730));                    // 730 × 0.125 − 49
        Assert.Null(K10Temp.Ccd(730));                                    // valid bit clear
        Assert.Equal((0x1F0u, 16), K10Temp.Layout(0x1a, 0x08));           // Threadripper PRO 9975WX
        Assert.Equal((0x308u, 8), K10Temp.Layout(0x1a, 0x44));            // Zen 5 Ryzen desktop
        Assert.Null(K10Temp.Layout(0x1a, 0x70));
    }

    [WindowsFact]
    public void AHidUpsReadsAsNutVariables()
    {
        (ushort, ushort)[] input = [(0x84, 0x1A), (0x84, 0x04)], output = [(0x84, 0x1C), (0x84, 0x04)], power = [(0x84, 0x24), (0x84, 0x04)];
        var ups = new WindowsHidUps.HidUps("ups", "CPS", "CP2000PFCRM2U", "", [
            new(0x84, 0x30, input, 120), new(0x84, 0x30, output, 121), new(0x84, 0x35, power, 17), new(0x84, 0x44, power, 1200),
            new(0x85, 0x66, power, 100), new(0x85, 0x68, power, 2100), new(0x85, 0xD0, power, 1), new(0x85, 0x44, power, 0),
            new(0x85, 0x45, power, 0)]);
        var vars = WindowsHidUps.Vars(ups);
        Assert.Equal(("120", "121", "17", "1200", "204"), (vars["input.voltage"], vars["output.voltage"], vars["ups.load"], vars["ups.realpower.nominal"], vars["ups.realpower"]));
        Assert.Equal(("100", "2100", "OL"), (vars["battery.charge"], vars["battery.runtime"], vars["ups.status"]));
    }

    [Fact]
    public void HsmpRowsMatchLinuxIds()
    {
        var reply = GpuWire.Encode(new Dictionary<string, double> { ["fclk"] = 1950, ["socket-power"] = 71, ["limit-sources"] = 16 });
        var engine = new WindowsHsmpEngine(() => reply);
        var items = engine.Discover();
        Assert.Contains(items, i => i.Id == "hsmp:fclk" && i.Group == HsmpEngine.Group);
        Assert.Contains(items, i => i.Id == "hsmp:socket-power" && i.Name == "CPU Package Power");
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.Equal(1950, readings["hsmp:fclk"]);
        Assert.Equal(1, readings["hsmp:limit:opn-max"]);   // bit 4
        Assert.Equal(0, readings["hsmp:limit:ppt-limit"]); // bit 3
    }

    // BIOS Setup on Windows: the service's uefivar command and the app's client (read-only).
    [WindowsFact]
    public void UefiVariablesComeThroughTheService()
    {
        var session = new WindowsHelperSession(new HelperSession(new NoSmart()), null, null);
        Assert.StartsWith("err expected", session.HandleLine("uefivar ../x 8be4df61-93ca-11d2-aa0d-00e098032b8c"));
        Assert.StartsWith("err expected", session.HandleLine("uefivar BootOrder not-a-guid"));
        Assert.True(WindowsUefi.ValidName("VARSTORE_OCMR_SETTINGS_NAME"));

        const string global = "8be4df61-93ca-11d2-aa0d-00e098032b8c";
        // Boot0001 "Windows Boot Manager" (EFI_LOAD_OPTION: attributes, path length, UCS-2 description).
        var option = new byte[] { 1, 0, 0, 0, 0, 0 }.Concat(System.Text.Encoding.Unicode.GetBytes("Windows Boot Manager\0")).ToArray();
        var vars = new Dictionary<string, byte[]> { ["BootOrder"] = [1, 0], ["Boot0001"] = option, ["Setup"] = [7, 8, 9] };
        var machine = new HelperFirmwareVariables(line =>
        {
            var parts = line.Split(' ');
            Assert.Equal("uefivar", parts[0]);
            return vars.TryGetValue(parts[1], out var b) ? "ok " + Convert.ToBase64String(b) : "ok none";
        });
        Assert.True(machine.Available);
        Assert.Equal(new byte[] { 7, 8, 9 }, machine.Read("Setup", "ec87d643-eba4-4bb5-a1e5-3f3e36b20da9"));
        Assert.Null(machine.Read("Missing", global));
        Assert.Equal(["Windows Boot Manager"], machine.BootEntries());
        Assert.False(new HelperFirmwareVariables(_ => throw new IOException("no service")).Available);
    }

    // Windows, WRX90E: the firmware leaves PAGE 0 (an unused loop), so the reader sets the rail's page, reads it and
    // puts PAGE 0 back, inside one hold of the shared SMBus mutex. Without switchPage nothing is ever written.
    [Fact]
    public void TheWindowsVrReaderSwitchesToTheRailPageAndRestoresIt()
    {
        var chip = new Xdpe();
        var vr = BoardVrs.WindowsKnown[0];
        Assert.Equal(0, new BoardVrReader(chip, vr, adapterNumber: 0).Read()!.Page);
        Assert.Empty(chip.Writes);

        var held = 0;
        var reader = new BoardVrReader(chip, vr, adapterNumber: 0, canSwitchPage: true, exclusive: read => { held++; return read(); });
        Assert.Equal(0, reader.Read()!.Page); // a plain read (the "vr" command, the Safety option off) never writes
        Assert.Empty(chip.Writes);
        held = 0;
        var sample = reader.Read(switchPage: true)!;
        Assert.Equal(1, sample.Page);
        Assert.Equal(0x0140, sample.Vout); // page 1's VOUT (1.25 V), not page 0's
        Assert.Equal([(byte)1, (byte)0], chip.Writes);
        Assert.Equal(0, chip.Page);
        Assert.Equal(1, held);
        var engine = new BoardVrEngine(() => BoardVrReader.Format(reader.Read(switchPage: true)!), vr);
        Assert.Equal(9, engine.Discover().Count);
        var readings = new Dictionary<string, double?>();
        engine.ReadInto(readings, null);
        Assert.Equal(1.25, readings["vr:xdpe19283b:vout"]);

        // A page other than 0 or the rail's: read as it is, nothing written.
        chip.Writes.Clear();
        chip.Page = 3;
        Assert.Equal(3, reader.Read(switchPage: true)!.Page);
        Assert.Empty(chip.Writes);

        // A board without canSwitchPage ignores the request.
        chip.Page = 0;
        Assert.Equal(0, new BoardVrReader(chip, vr, adapterNumber: 0).Read(switchPage: true)!.Page);
        Assert.Empty(chip.Writes);
    }

    /// <summary>The XDPE19283B as read live on 2026-11-18: page 0 an unused loop, page 1 the 1.25 V memory rail.</summary>
    private sealed class Xdpe : ISmbusProvider, ISmbus
    {
        public byte Page;
        public readonly List<byte> Writes = [];
        public ISmbus Open(int adapter) => this;
        public byte ReadByte(int address, byte command) => command switch { 0x00 => Page, 0x20 => 0x18, _ => throw new IOException("NACK") };
        public ushort ReadWord(int address, byte command) => (Page == 1, command) switch
        {
            (_, 0x88) => 0xD301, (true, 0x8B) => 0x0140, (false, 0x8B) => 0x0000, (true, 0x8C) => 0xD208, (false, 0x8C) => 0x9800,
            (true, 0x96) => 0xD280, (true, 0x97) => 0xD240, _ => 0xA000,
        };
        public byte[] ReadBlock(int address, byte command) => command == 0xAD ? [0x01, 0x95] : throw new IOException("NACK");
        public void WriteByte(int address, byte command, byte value) { Assert.Equal(0x00, command); Writes.Add(value); Page = value; }
        public void Dispose() { }
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("none");
        public void Dispose() { }
    }
}
