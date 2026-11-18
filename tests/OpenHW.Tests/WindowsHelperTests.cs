using OpenHW.Hardware;
using OpenHW.Hardware.Windows;
using Xunit;

namespace OpenHW.Tests;

/// <summary>The Windows helper's pieces that run without Administrator: the IPMI reply format and the named pipe.</summary>
public sealed class WindowsHelperTests
{
    [Fact]
    public void WmiRepliesBecomeCompletionCodeThenData()
    {
        // The driver's ResponseData starts with the completion code and its size counts it.
        Assert.Equal([0x00, 0x20, 0x01], WmiIpmiTransport.Reply(0x00, [0x00, 0x20, 0x01, 0xFF], 3));
        Assert.Equal([0xC1], WmiIpmiTransport.Reply(0xC1, [0xC1], 1));
        // Data alone (a driver that leaves the code out): the code goes in front.
        Assert.Equal([0x00, 0x20, 0x01], WmiIpmiTransport.Reply(0x00, [0x20, 0x01], 2));
        Assert.Equal([0xD5], WmiIpmiTransport.Reply(0xD5, [], 0));
    }

    [WindowsFact]
    public async Task ThePipeServesHelperSessionsLikeTheSocket()
    {
        var name = "openhw-helper-test-" + Guid.NewGuid().ToString("N");
        using var reader = new BmcReader(() => new BmcTests.FakeBmc());
        await using var server = new PipeHelperServer(() => new HelperSession(new NoSmart(), bmc: reader), name);
        server.Start();

        Assert.Equal(HelperSession.Version, PipeHelperClient.Request("ping", 2000, 64, name));
        Assert.StartsWith("eyJ", PipeHelperClient.Request("bmc", 8000, 1 << 20, name)); // base64 of a JSON object
        var refused = Assert.Throws<IOException>(() => PipeHelperClient.Request("bmc set", 2000, 64, name));
        Assert.Contains("err unknown command", refused.Message);
        // A second server can't take the name over (FILE_FLAG_FIRST_PIPE_INSTANCE).
        await using var impostor = new PipeHelperServer(() => new HelperSession(new NoSmart()), name);
        Assert.ThrowsAny<Exception>(impostor.Start);
    }

    [WindowsFact]
    public void WithoutTheServiceTheClientSaysSo()
    {
        var error = Assert.Throws<IOException>(() => PipeHelperClient.Request("ping", 500, 64, "openhw-helper-absent-" + Guid.NewGuid().ToString("N")));
        Assert.Contains("isn't running", error.Message);
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("none");
        public void Dispose() { }
    }
}
