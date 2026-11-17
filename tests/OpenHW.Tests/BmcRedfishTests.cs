using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>Fan temperature sources over Redfish, against a fake AMI Redfish service shaped like the WRX90E's.</summary>
public sealed class BmcRedfishTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openhw-redfish").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeRedfish : HttpMessageHandler
    {
        public readonly long[] Sources = [1, 8, 1, 1, 1, 1, 1, 16777215];
        public readonly List<string> Log = [];
        public bool IgnorePatch;
        public string Password = "correct-horse";
        private int _etag = 100;
        private readonly HashSet<string> _tokens = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var token = request.Headers.TryGetValues("X-Auth-Token", out var t) ? t.First() : null;
            Log.Add($"{request.Method} {path}" + (token is null ? "" : " [token]") +
                    (request.Headers.TryGetValues("If-Match", out var m) ? $" If-Match {m.First()}" : ""));
            switch (request.Method.Method, path)
            {
                case ("POST", "/redfish/v1/SessionService/Sessions"):
                    var login = JsonNode.Parse(body!)!;
                    if ((string?)login["UserName"] != "openhw" || (string?)login["Password"] != Password)
                        return Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Login failed"}}""");
                    var reply = Json(HttpStatusCode.Created, "{}");
                    reply.Headers.Add("X-Auth-Token", "tok-" + _tokens.Count);
                    _tokens.Add("tok-" + _tokens.Count);
                    reply.Headers.Location = new Uri("/redfish/v1/SessionService/Sessions/7", UriKind.Relative);
                    return reply;
                case ("DELETE", "/redfish/v1/SessionService/Sessions/7"):
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case ("GET", BmcRedfish.ThermalPath):
                    var page = Json(HttpStatusCode.OK, Thermal());
                    page.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{_etag}\"");
                    return page;
                case ("PATCH", BmcRedfish.ThermalPath):
                    if (token is null || !_tokens.Contains(token)) return Json(HttpStatusCode.Unauthorized, "{}");
                    if (!request.Headers.TryGetValues("If-Match", out var match) || match.First() != $"\"{_etag}\"")
                        return Json(HttpStatusCode.PreconditionFailed, """{"error":{"message":"ETag mismatch"}}""");
                    var oem = JsonNode.Parse(body!)!["Oem"]!;
                    if (oem["Mode"] is null) // as the WRX90E's BMC answers
                        return Json(HttpStatusCode.BadRequest, """{"error":{"@Message.ExtendedInfo":[{"Message":"The property Oem/Mode is a required property and must be included in the request."}]}}""");
                    if ((string?)oem["Mode"] != "Manual") return Json(HttpStatusCode.BadRequest, """{"error":{"message":"mode changed"}}""");
                    var pwm = oem["PWM"]!.AsArray();
                    // As the WRX90E's BMC answers: every entry complete; the unused 8th slot's values refused.
                    foreach (var entry in pwm)
                    {
                        foreach (var field in new[] { "Index", "Source", "Temperature1", "FanDuty1", "Temperature5", "FanDuty5" })
                            if (entry![field] is null)
                                return Json(HttpStatusCode.BadRequest, new JsonObject { ["error"] = new JsonObject { ["message"] =
                                    $"The property Oem/PWM/{(int)entry["Index"]!}/{field} is a required property and must be included in the request." } }.ToJsonString());
                        if ((int)entry["Index"]! == 7)
                            return Json(HttpStatusCode.BadRequest, """{"error":{"@Message.ExtendedInfo":[{"Message":"The value 16777215 for Source is not valid."}]}}""");
                    }
                    if (!IgnorePatch) foreach (var p in pwm) Sources[(int)p!["Index"]!] = (long)p["Source"]!;
                    _etag++;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Json(HttpStatusCode.NotFound, "{}");
        }

        private string Thermal() => new JsonObject
        {
            ["Oem"] = new JsonObject
            {
                ["Mode"] = "Manual",
                ["PWM"] = new JsonArray(Sources.Select((s, i) => (JsonNode)new JsonObject
                {
                    ["Index"] = i, ["Source"] = s, ["Temperature1"] = 20, ["FanDuty1"] = 20, ["Temperature2"] = 45, ["FanDuty2"] = 20,
                    ["Temperature3"] = 65, ["FanDuty3"] = 20, ["Temperature4"] = 70, ["FanDuty4"] = 100, ["Temperature5"] = 100, ["FanDuty5"] = 100,
                }).ToArray()),
            },
        }.ToJsonString();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        private static HttpResponseMessage Json(HttpStatusCode code, string text) =>
            new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private static BmcRedfish Client(FakeRedfish fake, BmcLogin? login = null) =>
        new(new Uri("https://169.254.0.17"), fake, () => login);

    private static readonly BmcLogin Login = new("openhw", "correct-horse");

    [Fact]
    public void SourcesAreReadWithoutALogin()
    {
        var fake = new FakeRedfish();
        Assert.Equal(new Dictionary<int, long> { [0] = 1, [1] = 8, [2] = 1, [3] = 1, [4] = 1, [5] = 1, [6] = 1, [7] = 16777215 },
            Client(fake).ReadSources());
        Assert.Equal(["GET /redfish/v1/Chassis/Self/Thermal"], fake.Log);
    }

    [Fact]
    public void ASourceChangeLogsInSendsTheETagReadsBackAndLogsOut()
    {
        var fake = new FakeRedfish();
        Client(fake, Login).SetSource(1, 24); // DIMMA1 + DIMMB1
        Assert.Equal(24, fake.Sources[1]);
        Assert.Equal(1, fake.Sources[0]);
        Assert.Equal([
            "POST /redfish/v1/SessionService/Sessions",
            "GET /redfish/v1/Chassis/Self/Thermal [token]",
            "PATCH /redfish/v1/Chassis/Self/Thermal [token] If-Match \"100\"",
            "GET /redfish/v1/Chassis/Self/Thermal [token]",
            "DELETE /redfish/v1/SessionService/Sessions/7 [token]",
        ], fake.Log);
    }

    [Fact]
    public void OnlyTheOneZonesCompleteEntryIsSentSoOtherFansAndTheSpareSlotAreUntouched()
    {
        var fake = new FakeRedfish();
        Client(fake, Login).SetSource(2, 1L << 11); // PCIE01
        Assert.Equal([1, 8, 2048, 1, 1, 1, 1, 16777215], fake.Sources);
        Assert.Single(fake.Log, l => l.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangeThatDoesntReadBackIsReported()
    {
        var fake = new FakeRedfish { IgnorePatch = true };
        Assert.Contains("reads back source 1, not 4", Assert.Throws<IOException>(() => Client(fake, Login).SetSource(3, 4)).Message);
        Assert.EndsWith("DELETE /redfish/v1/SessionService/Sessions/7 [token]", fake.Log[^1]); // the session is closed anyway
    }

    [Fact]
    public void BadMasksMissingOrWrongLoginsAreRefused()
    {
        var fake = new FakeRedfish();
        Assert.Throws<ArgumentException>(() => Client(fake, Login).SetSource(1, 0));
        Assert.Throws<ArgumentException>(() => Client(fake, Login).SetSource(1, 1L << 18));
        Assert.Contains("no BMC login", Assert.Throws<ArgumentException>(() => Client(fake).SetSource(1, 8)).Message);
        Assert.Contains("refused that login", Assert.Throws<ArgumentException>(() => Client(fake).CheckLogin(Login with { Password = "wrong-pass" })).Message);
        Client(fake).CheckLogin(Login);
        Assert.Empty(fake.Log.Where(l => l.StartsWith("PATCH", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheHelperChecksThenKeepsTheLoginRootOnlyAndNeverEchoesThePassword()
    {
        var fake = new FakeRedfish();
        var path = Path.Combine(_dir, "bmc-login");
        var helper = new HelperSession(new NoSmart(), redfish: Client(fake, null), loginPath: path);
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        Assert.Equal("ok none", helper.HandleLine("bmclogin"));
        Assert.Equal("err the BMC refused that login (401)", helper.HandleLine($"bmclogin {B64("openhw")} {B64("wrong-pass")}"));
        Assert.False(File.Exists(path));
        var stored = helper.HandleLine($"bmclogin {B64("openhw")} {B64("correct-horse")}");
        Assert.Equal("ok openhw", stored);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(Login, BmcLogin.Load(path));
        Assert.DoesNotContain("correct-horse", helper.HandleLine("bmclogin"));
        Assert.Equal("err a BMC user is 1–32 characters and its password 8–20", helper.HandleLine($"bmclogin {B64("openhw")} {B64("short")}"));
        Assert.Equal("ok none", helper.HandleLine("bmclogout"));
        Assert.Equal("err expected a fan zone and a source bitmask", helper.HandleLine("fansource bmc:9 8"));
    }

    private sealed class NoSmart : INvmeSmartReader
    {
        public byte[] Read(string controller, string identity) => throw new IOException("none");
        public void Dispose() { }
    }
}

/// <summary>Against the real BMC, read-only, no login: run with OPENHW_LIVE_BMC=1.</summary>
public sealed class BmcRedfishLiveTests
{
    [Fact]
    public void ReadsTheRealBmcsSources()
    {
        if (Environment.GetEnvironmentVariable("OPENHW_LIVE_BMC") != "1") return;
        var pin = Path.Combine(Path.GetTempPath(), $"openhw-pin-{Guid.NewGuid():N}");
        try
        {
            var sources = new BmcRedfish(BmcRedfish.HostInterface, BmcRedfish.PinnedHandler(pin), () => null).ReadSources();
            Console.WriteLine(string.Join(", ", sources.Select(p => $"{p.Key}={p.Value}")));
            Assert.Equal(8, sources.Count);
            Assert.True(File.Exists(pin)); // pinned on first use
        }
        finally { File.Delete(pin); }
    }
}
