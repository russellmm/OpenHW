using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenHW.Hardware;

/// <summary>The BMC login OpenHW's helper uses for Redfish writes: root-only (0600) in /etc/openhw/bmc-login.</summary>
public sealed record BmcLogin(string User, string Password)
{
    public const string DefaultPath = "/etc/openhw/bmc-login";

    public static BmcLogin? Load(string path = DefaultPath)
    {
        try { return JsonSerializer.Deserialize<BmcLogin>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string path = DefaultPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = OsFiles.CreatePrivate(temp))
        {
            JsonSerializer.Serialize(stream, this);
        }
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// The BMC's Redfish service for what IPMI can't do on this firmware: each fan zone's temperature source. On the
/// WRX90E the source is <c>Chassis/Self/Thermal → Oem.PWM[n].Source</c>, a bitmask over the BMC's own temperature
/// sensors: bit = sensor number − 1 (CPU Package Temp = 1 → bit 0, DIMMA1_Temp = 4 → bit 3, PCIE07 = 18 → bit 17),
/// several bits = several sensors. Decoded with the owner on 2026-11-16 (BMC web console + Redfish read-back).
/// Reads go without a login while the host interface allows it, else in a session (<see cref="Get"/>); writes open a session with <see cref="BmcLogin"/>, send the
/// change with the page's ETag, read it back, and close the session. The BMC's certificate is pinned on first use.
/// </summary>
public sealed class BmcRedfish(Uri baseUrl, HttpMessageHandler handler, Func<BmcLogin?> login)
{
    public static readonly Uri HostInterface = new("https://169.254.0.17");
    public const string ThermalPath = "/redfish/v1/Chassis/Self/Thermal";
    public const string CertPinPath = "/etc/openhw/bmc-cert.sha256";
    public const int SourceBits = 18; // the BMC's temperature sensors 1–18 (CPU Package … PCIE07)

    private readonly HttpClient _http = new(handler) { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>The live client: the host interface, the stored login, the certificate pinned in /etc/openhw.</summary>
    public static BmcRedfish Live() => new(HostInterface, PinnedHandler(CertPinPath), () => BmcLogin.Load());

    /// <summary>Accepts the BMC's self-signed certificate the first time and only that certificate afterwards.</summary>
    public static HttpMessageHandler PinnedHandler(string pinPath) => new SocketsHttpHandler
    {
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                var fingerprint = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
                try
                {
                    if (File.Exists(pinPath)) return File.ReadAllText(pinPath).Trim() == fingerprint;
                    Directory.CreateDirectory(Path.GetDirectoryName(pinPath)!);
                    File.WriteAllText(pinPath, fingerprint + "\n");
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            },
        },
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Each fan zone's source bitmask (zone index → mask).</summary>
    public IReadOnlyDictionary<int, long> ReadSources()
    {
        var thermal = Get(ThermalPath);
        return Pwm(thermal).ToDictionary(p => (int)p["Index"]!, p => (long)p["Source"]!);
    }

    /// <summary>
    /// A read. Tried without a login first (the host interface answers that way while it's in "AuthNone" mode); when the
    /// BMC wants one (401/403) and a login is stored, read again in a session. So nothing breaks when the owner makes the
    /// host interface require a login.
    /// </summary>
    public JsonNode Get(string path)
    {
        using (var open = Send(HttpMethod.Get, path, null, null))
            if (open.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return Parse(open);
        var credentials = login() ?? throw new IOException("the BMC wants a login for Redfish reads and none is stored (Fan Control → Give OpenHW the BMC login…)");
        var session = Open(credentials);
        try
        {
            using var read = Send(HttpMethod.Get, path, session.Token, null);
            return Parse(read);
        }
        finally { Close(session); }
    }

    /// <summary>True when the BMC answers a Redfish read from this PC without any login.</summary>
    public bool OpenWithoutLogin()
    {
        using var response = Send(HttpMethod.Get, "/redfish/v1/Systems/Self", null, null);
        return response.IsSuccessStatusCode;
    }

    /// <summary>How the host interface is set up, and whether it lets this PC in without a login.</summary>
    public BmcAccessReply ReadAccess()
    {
        var open = OpenWithoutLogin();
        var modes = new List<string>();
        bool? bootstrap = null;
        string? role = null;
        try
        {
            var list = Get("/redfish/v1/Managers/Self/HostInterfaces");
            foreach (var member in (list["Members"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (member["@odata.id"]?.GetValue<string>() is not { } id) continue;
                var hi = Get(id);
                modes.AddRange((hi["AuthenticationModes"] as JsonArray ?? []).Select(m => m?.GetValue<string>()).OfType<string>());
                bootstrap ??= hi["CredentialBootstrapping"]?["Enabled"]?.GetValue<bool>();
                role ??= hi["CredentialBootstrapping"]?["RoleId"]?.GetValue<string>();
            }
        }
        catch (IOException) when (!open) { } // locked down and no login stored: what matters is known
        return new BmcAccessReply(open, modes.Distinct(StringComparer.Ordinal).ToList(), bootstrap, role);
    }

    private (string Uri, JsonNode Registry)? _registry;

    /// <summary>
    /// Every BIOS Setup option the BIOS published (Setup → "Publish HII Resources"), with current and default values.
    /// The attribute registry (~0.6 MB, gzip-served) is read once per registry version.
    /// </summary>
    public BiosSettingsReply ReadBiosSettings()
    {
        var bios = Get("/redfish/v1/Systems/Self/Bios");
        var name = bios["AttributeRegistry"]?.GetValue<string>() ?? throw new IOException("the BIOS published no attribute registry (Setup → Publish HII Resources)");
        var file = Get("/redfish/v1/Registries/" + name);
        var uri = (file["Location"] as JsonArray)?.OfType<JsonObject>()
            .Select(l => l["Uri"]?.GetValue<string>() ?? l["PublicationUri"]?.GetValue<string>()).FirstOrDefault(u => u is not null)
            ?? throw new IOException($"the registry {name} has no file");
        if (_registry is not { } cached || cached.Uri != uri) _registry = cached = (uri, Get(uri));
        return BiosSettingsParser.Build(bios, cached.Registry);
    }

    /// <summary>Checks a login by opening a session (and closing it again). Throws with the BMC's answer if refused.</summary>
    public void CheckLogin(BmcLogin candidate)
    {
        var session = Open(candidate);
        Close(session);
    }

    /// <summary>Sets one zone's source bitmask (bits 0–17, at least one) and reads it back.</summary>
    public void SetSource(int zone, long mask)
    {
        if (mask <= 0 || mask >= 1L << SourceBits) throw new ArgumentException($"a source is 1–{SourceBits} of the BMC's temperature sensors");
        var credentials = login() ?? throw new ArgumentException("no BMC login stored — give OpenHW the BMC login first");
        var session = Open(credentials);
        try
        {
            using var read = Send(HttpMethod.Get, ThermalPath, session.Token, null);
            var etag = read.Headers.ETag?.Tag;
            var thermal = Parse(read);
            var pwm = Pwm(thermal).ToList();
            var entry = pwm.FirstOrDefault(p => (int)p["Index"]! == zone) ?? throw new ArgumentException($"no PWM {zone} on the BMC");
            if ((long)entry["Source"]! == mask) return;
            // What the WRX90E's BMC accepts (learned on the real one, 0.24.0–0.24.2): Oem.Mode, and for each PWM entry
            // sent, the complete entry (Index, Source, Temperature1–5, FanDuty1–5). The whole list is refused over the
            // unused 8th slot's values (though the BMC applies the rest), so only the one zone's entry is sent, as read,
            // with its Source changed.
            var mode = thermal["Oem"]?["Mode"]?.GetValue<string>() ?? throw new IOException("the BMC's thermal page has no Oem.Mode");
            var changed = (JsonObject)entry.DeepClone();
            changed["Source"] = mask;
            var patch = new JsonObject { ["Oem"] = new JsonObject { ["Mode"] = mode, ["PWM"] = new JsonArray(changed) } };
            using (var write = Send(HttpMethod.Patch, ThermalPath, session.Token, patch, etag)) Parse(write, allowEmpty: true);
            using var check = Send(HttpMethod.Get, ThermalPath, session.Token, null);
            var after = Pwm(Parse(check)).FirstOrDefault(p => (int)p["Index"]! == zone);
            if (after is null || (long)after["Source"]! != mask)
                throw new IOException($"the BMC accepted the change but PWM {zone} reads back source {after?["Source"]}, not {mask}");
        }
        finally { Close(session); }
    }

    private sealed record Session(string Token, string? Location);

    private Session Open(BmcLogin credentials)
    {
        var body = new JsonObject { ["UserName"] = credentials.User, ["Password"] = credentials.Password };
        using var response = Send(HttpMethod.Post, "/redfish/v1/SessionService/Sessions", null, body);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new ArgumentException("the BMC refused that login (401)");
        Parse(response, allowEmpty: true);
        var token = response.Headers.TryGetValues("X-Auth-Token", out var values) ? values.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(token)) throw new IOException("the BMC opened no session (no X-Auth-Token)");
        return new Session(token, response.Headers.Location?.ToString());
    }

    private void Close(Session session)
    {
        if (session.Location is null) return;
        try { using var _ = Send(HttpMethod.Delete, session.Location, session.Token, null); }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { } // the BMC times idle sessions out anyway
    }

    private HttpResponseMessage Send(HttpMethod method, string path, string? token, JsonNode? body, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Add("X-Auth-Token", token);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        try { return _http.Send(request); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new IOException("can't reach the BMC's Redfish service: " + ex.Message, ex);
        }
    }

    private static JsonNode Parse(HttpResponseMessage response, bool allowEmpty = false)
    {
        var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        // Registry files come gzip-compressed as they're stored, whatever the request asked for.
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gz.CopyTo(plain);
            bytes = plain.ToArray();
        }
        var text = Encoding.UTF8.GetString(bytes);
        if (!response.IsSuccessStatusCode)
        {
            var message = text;
            try { message = JsonNode.Parse(text)?["error"]?["@Message.ExtendedInfo"]?[0]?["Message"]?.GetValue<string>() ?? JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text; }
            catch (JsonException) { }
            throw new IOException($"Redfish {(int)response.StatusCode}: {message}");
        }
        if (string.IsNullOrWhiteSpace(text)) return allowEmpty ? new JsonObject() : throw new IOException("empty Redfish reply");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private static IEnumerable<JsonObject> Pwm(JsonNode thermal) =>
        (thermal["Oem"]?["PWM"] as JsonArray ?? throw new IOException("the BMC's thermal page has no Oem.PWM list")).OfType<JsonObject>();
}
