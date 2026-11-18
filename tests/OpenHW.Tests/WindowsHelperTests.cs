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
        await using var server = new PipeHelperServer(() => new HelperSession(new NoSmart(), bmc: reader).HandleLine, name);
        server.Start();

        Assert.Equal(HelperSession.Version, PipeHelperClient.Request("ping", 2000, 64, name));
        Assert.StartsWith("eyJ", PipeHelperClient.Request("bmc", 8000, 1 << 20, name)); // base64 of a JSON object
        var refused = Assert.Throws<IOException>(() => PipeHelperClient.Request("bmc set", 2000, 64, name));
        Assert.Contains("err unknown command", refused.Message);
        // A second server can't take the name over (FILE_FLAG_FIRST_PIPE_INSTANCE).
        await using var impostor = new PipeHelperServer(() => new HelperSession(new NoSmart()).HandleLine, name);
        Assert.ThrowsAny<Exception>(impostor.Start);
    }

    [WindowsFact]
    public void SavedGpuLimitsAreKeptByUuidAndTheCommandsValidate()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("openhw-gpulimits-").FullName, "gpu-power-limits.json");
        var saved = new SavedGpuPowerLimits(path);
        Assert.Empty(saved.Read());
        saved.Set("GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0", 450);
        saved.Set("GPU-d0f36b3b-abad-1a42-0e65-e78da2139da3", 300);
        saved.Set("GPU-d0f36b3b-abad-1a42-0e65-e78da2139da3", null);
        Assert.Equal(new Dictionary<string, long> { ["GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0"] = 450 }, saved.Read());

        var session = new WindowsHelperSession(new HelperSession(new NoSmart()), gpuPower: null, saved);
        Assert.Equal(saved.Read(), GpuWire.Decode<Dictionary<string, long>>(session.HandleLine("gpupowersaved")[3..]));
        Assert.Equal("err GPU power limits unavailable", session.HandleLine("gpupowersave GPU-6d3eab54-984e-f1bc-68eb-ee4948e6c3b0 400"));
        Assert.Equal("ok " + HelperSession.Version, session.HandleLine("ping")); // the shared commands still answer
    }

    [Fact]
    public void HsmpMessagesUseLinuxsStructLayoutIn48Bytes()
    {
        // Set the socket limit to 300 W: msg_id 5, one argument, no response, socket 0. The layout was confirmed on
        // 2026-11-17 with HSMP's test message (0x1234 → 0x1235) through AMD's Windows driver.
        var request = WindowsHsmp.Request(5, [300_000], 0);
        Assert.Equal(48, request.Length);
        Assert.Equal("05000000" + "0100" + "0000" + "E0930400" + new string('0', 56) + "0000" + "000000000000", Convert.ToHexString(request));
        Assert.Throws<ArgumentException>(() => WindowsHsmp.Request(1, new uint[9], 0));
        Assert.Equal("err CPU power limit unavailable",
            new WindowsHelperSession(new HelperSession(new NoSmart()), null, null).HandleLine("cpuppt 300000"));
    }

    [Fact]
    public void TheBmcsLanAddressComesFromGetLanConfiguration()
    {
        Assert.Equal("10.0.0.200", BmcLanAddress.Parse([0x00, 0x11, 10, 0, 0, 200])?.ToString());
        Assert.Null(BmcLanAddress.Parse([0x00, 0x11, 0, 0, 0, 0]));        // not configured
        Assert.Null(BmcLanAddress.Parse([0x00, 0x11, 169, 254, 0, 17]));   // the host interface's link-local address
        Assert.Null(BmcLanAddress.Parse([0xCC, 0x11, 10, 0, 0, 200]));      // an error reply
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
