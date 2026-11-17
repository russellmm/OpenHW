using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.Tests;

/// <summary>BIOS settings and host-interface access over Redfish, against a fake shaped like the WRX90E's AMI service.</summary>
public sealed class BiosSettingsTests
{
    private sealed class FakeBmc : HttpMessageHandler
    {
        public bool Locked; // the host interface requires a login
        public readonly List<string> Log = [];

        private const string Bios = """
            {"AttributeRegistry":"BiosAttributeRegistryA5496.14.5.0","Attributes":{
              "SB01K":"Auto","CbsCmnPPTLimitSHP":0,"PBSAmdCpmPcieRpUnCorrectedErrorSev":122642480,"ACPI002":true,
              "SetUserPasswordHDD":"","CbsCmnMemTargetSpeedDdrSHP":"DDR5_5200","Hide":false,"Mac":"00-00-00-00-00-00\u0000\u7470junk"}}
            """;
        private const string Registry = """
            {"RegistryEntries":{"Attributes":[
              {"AttributeName":"SetUserPasswordHDD","DisplayName":"Set User Password","Type":"Password"},
              {"AttributeName":"SB01K","DisplayName":"Clock Spread Spectrum","HelpText":"Enable/Disable Clock Spread Spectrum","Type":"Enumeration","DefaultValue":"Auto","ResetRequired":true,
               "Value":[{"ValueName":"Auto","ValueDisplayName":"Auto"},{"ValueName":"Enabled","ValueDisplayName":"Enabled"}]},
              {"AttributeName":"CbsCmnMemTargetSpeedDdrSHP","DisplayName":"Memory Target Speed ","Type":"Enumeration","DefaultValue":"Auto",
               "Value":[{"ValueName":"Auto","ValueDisplayName":"Auto"},{"ValueName":"DDR5_5200","ValueDisplayName":"DDR5-5200"}]},
              {"AttributeName":"CbsCmnPPTLimitSHP","DisplayName":"PPT","Type":"Integer","DefaultValue":0},
              {"AttributeName":"PBSAmdCpmPcieRpUnCorrectedErrorSev","DisplayName":"Pcie Root Port UnCorr Error Sev Reg","Type":"HexInteger","DefaultValue":122642480},
              {"AttributeName":"ACPI002","DisplayName":"Enable Hibernation","Type":"Enumeration","DefaultValue":"Disabled",
               "Value":[{"ValueName":"None","ValueDisplayName":"None"},{"ValueName":"Disabled","ValueDisplayName":"Disabled"}]},
              {"AttributeName":"Hide","DisplayName":"MAC Hide flag","Type":"Enumeration","DefaultValue":"None",
               "Value":[{"ValueName":"None","ValueDisplayName":"None"},{"ValueName":"Disabled","ValueDisplayName":"Disabled"}]},
              {"AttributeName":"Mac","DisplayName":"Router MAC address","Type":"String","DefaultValue":"00-00-00-00-00-00"}],
              "Dependencies":[]}}
            """;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Contains("X-Auth-Token");
            Log.Add($"{request.Method} {path}" + (token ? " [token]" : ""));
            if (request.Method == HttpMethod.Post && path == "/redfish/v1/SessionService/Sessions")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
                r.Headers.Add("X-Auth-Token", "tok");
                r.Headers.Location = new Uri("/redfish/v1/SessionService/Sessions/3", UriKind.Relative);
                return r;
            }
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (Locked && !token) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            return path switch
            {
                "/redfish/v1/Systems/Self" => Ok("""{"Id":"Self"}"""),
                "/redfish/v1/Systems/Self/Bios" => Ok(Bios),
                "/redfish/v1/Registries/BiosAttributeRegistryA5496.14.5.0" =>
                    Ok("""{"Location":[{"Language":"en-US","Uri":"/redfish/v1/Registries/BiosAttributeRegistryA5496.en-US.14.5.0.json"}]}"""),
                "/redfish/v1/Registries/BiosAttributeRegistryA5496.en-US.14.5.0.json" => Gzip(Registry),
                "/redfish/v1/Managers/Self/HostInterfaces" => Ok("""{"Members":[{"@odata.id":"/redfish/v1/Managers/Self/HostInterfaces/Self"}]}"""),
                "/redfish/v1/Managers/Self/HostInterfaces/Self" => Ok(Locked
                    ? """{"AuthenticationModes":["BasicAuth","RedfishSessionAuth"],"CredentialBootstrapping":{"Enabled":false,"RoleId":"HostInterfaceAdministrator"}}"""
                    : """{"AuthenticationModes":["AuthNone"],"CredentialBootstrapping":{"Enabled":true,"RoleId":"HostInterfaceAdministrator"}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        // As the WRX90E serves registry files: the stored gzip bytes, with no Content-Encoding.
        private static HttpResponseMessage Gzip(string json)
        {
            var memory = new MemoryStream();
            using (var gz = new GZipStream(memory, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(json));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(memory.ToArray()) };
        }
    }

    private static BmcRedfish Client(FakeBmc fake, BmcLogin? login = null) =>
        new(new Uri("https://bmc.test"), fake, () => login);

    [Fact]
    public void SettingsComeInWordsWithDefaultsAndNoPasswords()
    {
        var fake = new FakeBmc();
        var reply = Client(fake).ReadBiosSettings();
        Assert.Equal("BiosAttributeRegistryA5496.14.5.0", reply.Registry);
        Assert.DoesNotContain(reply.Settings, s => s.Type == "Password");
        var speed = reply.Settings.Single(s => s.Name == "CbsCmnMemTargetSpeedDdrSHP");
        Assert.Equal(("Memory Target Speed", "DDR5-5200", "Auto", false), (speed.Display, speed.Value, speed.Default, speed.IsDefault));
        Assert.Equal("0x74F6030", reply.Settings.Single(s => s.Type == "HexInteger").Value);
        // Checkboxes as AMI publishes them: a true/false value, "None"/"Disabled" in the registry.
        Assert.True(reply.Settings.Single(s => s.Name == "ACPI002") is { Value: "Enabled", Default: "Disabled", IsDefault: false });
        Assert.True(reply.Settings.Single(s => s.Name == "Hide") is { Value: "Disabled", Default: null, IsDefault: true });
        Assert.True(reply.Settings.Single(s => s.Name == "Mac") is { Value: "00-00-00-00-00-00", IsDefault: true });
        Assert.True(reply.Settings.Single(s => s.Name == "SB01K") is { IsDefault: true, ResetRequired: true, Help: "Enable/Disable Clock Spread Spectrum" });
        Assert.Equal("0", reply.Settings.Single(s => s.Name == "CbsCmnPPTLimitSHP").Value);

        // The registry file is read once per version.
        Client(fake).ReadBiosSettings();
        var client = Client(fake);
        client.ReadBiosSettings();
        client.ReadBiosSettings();
        Assert.Equal(3, fake.Log.Count(l => l.EndsWith(".json", StringComparison.Ordinal)));
        Assert.DoesNotContain(fake.Log, l => l.Contains("[token]", StringComparison.Ordinal)); // open: no login used
    }

    [Fact]
    public void OpenHostInterfaceIsReportedAndLockedOneStillReadsWithTheStoredLogin()
    {
        var fake = new FakeBmc();
        var open = Client(fake).ReadAccess();
        Assert.True(open.OpenWithoutLogin);
        Assert.Equal(["AuthNone"], open.AuthModes);
        Assert.Equal((true, "HostInterfaceAdministrator"), (open.CredentialBootstrapping, open.BootstrapRole));

        fake.Locked = true;
        var login = new BmcLogin("openhw", "pw");
        var locked = Client(fake, login).ReadAccess();
        Assert.False(locked.OpenWithoutLogin);
        Assert.Equal(["BasicAuth", "RedfishSessionAuth"], locked.AuthModes);
        Assert.Equal(7, Client(fake, login).ReadBiosSettings().Settings.Count); // read in a session
        Assert.Contains(fake.Log, l => l == "GET /redfish/v1/Systems/Self/Bios [token]");

        // Locked and no login stored: access still known; BIOS settings say what's missing.
        Assert.False(Client(fake).ReadAccess().OpenWithoutLogin);
        Assert.Contains("none is stored", Assert.Throws<IOException>(() => Client(fake).ReadBiosSettings()).Message);
    }
}
