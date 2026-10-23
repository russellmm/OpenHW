using System.Net.Sockets;
using System.Text;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

public sealed class HelperTests
{
    private const string Controller = "nvme0";
    private static readonly string Identity = new('a', 64);

    private sealed class FakeSmart(byte[]? data = null) : INvmeSmartReader
    {
        public int Reads { get; private set; }
        public (string Controller, string Identity)? Last { get; private set; }

        public byte[] Read(string controller, string identity)
        {
            Reads++;
            Last = (controller, identity);
            return data ?? throw new IOException("Cannot open NVMe controller (13)");
        }

        public void Dispose() { }
    }

    private static byte[] Log() => Enumerable.Range(0, 512).Select(i => (byte)(i % 251 + 1)).ToArray();

    [Fact]
    public void PingReportsVersion() =>
        Assert.Equal("ok " + HelperSession.Version, new HelperSession(new FakeSmart()).HandleLine("ping"));

    [Theory]
    [InlineData("nvmesmart /dev/sda aaaa")]
    [InlineData("nvmesmart nvme0 ../../etc/shadow")]
    [InlineData("nvmesmart nvme0; " + "rm")]
    public void RejectsAnythingButAnEnumeratedNvmeIdentity(string line)
    {
        var smart = new FakeSmart(Log());
        var reply = new HelperSession(smart).HandleLine(line);
        Assert.StartsWith("err", reply);
        Assert.Equal(0, smart.Reads);
    }

    [Theory]
    [InlineData("set nvml:GPU-1:fan:0 100")]
    [InlineData("power GPU-123456 600")]
    [InlineData("")]
    public void HasNoWriteCommands(string line) =>
        Assert.StartsWith("err", new HelperSession(new FakeSmart(Log())).HandleLine(line));

    [Fact]
    public void ReturnsSmartLogAsBase64()
    {
        var log = Log();
        var reply = new HelperSession(new FakeSmart(log)).HandleLine($"nvmesmart {Controller} {Identity}");
        Assert.Equal("ok " + Convert.ToBase64String(log), reply);
    }

    [Fact]
    public void ReportsReaderFailureAsError() =>
        Assert.Equal("err Cannot open NVMe controller (13)",
            new HelperSession(new FakeSmart()).HandleLine($"nvmesmart {Controller} {Identity}"));

    [Fact]
    public void CacheLimitsDriveReads()
    {
        var inner = new FakeSmart(Log());
        var cached = new CachedSmartReader(inner, TimeSpan.FromMinutes(1));
        for (var i = 0; i < 5; i++) cached.Read(Controller, Identity);
        Assert.Equal(1, inner.Reads);
    }

    [Fact]
    public async Task ClientReadsThroughServerSocket()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"openhw-test-{Guid.NewGuid():N}.sock");
        var log = Log();
        var smart = new FakeSmart(log);
        await using (var server = new HelperServer(smart, socket))
        {
            server.Start();
            var data = await Task.Run(() => new NvmeSmartClient(socket).Read(Controller, Identity));
            Assert.Equal(log, data);
            Assert.Equal((Controller, Identity), smart.Last);
        }
        Assert.False(File.Exists(socket)); // removed on shutdown
    }

    [Fact]
    public async Task OverlongLineDropsConnectionWithoutReply()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"openhw-test-{Guid.NewGuid():N}.sock");
        await using var server = new HelperServer(new FakeSmart(Log()), socket);
        server.Start();
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 3000 };
        client.Connect(new UnixDomainSocketEndPoint(socket));
        client.Send(Encoding.ASCII.GetBytes(new string('x', 4096) + "\n"));
        // Closed without a reply: EOF, or a reset because the rest of our line was never read.
        var buffer = new byte[64];
        try { Assert.Equal(0, client.Receive(buffer)); }
        catch (SocketException ex) { Assert.Equal(SocketError.ConnectionReset, ex.SocketErrorCode); }
    }
}
