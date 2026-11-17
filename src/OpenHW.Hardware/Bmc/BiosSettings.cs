using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenHW.Hardware;

/// <summary>One BIOS Setup option as the BMC publishes it (UEFI HII → Redfish): its current and default value, in words.</summary>
public sealed record BiosSetting(string Name, string Display, string? Help, string Type, string? Value, string? Default, bool ResetRequired)
{
    public bool IsDefault => Default is null || Value == Default;
}

/// <summary>The helper's <c>biossettings</c> reply: every published Setup option, in the registry's (Setup's) order.</summary>
public sealed record BiosSettingsReply(string Registry, List<BiosSetting> Settings);

/// <summary>
/// The helper's <c>bmcaccess</c> reply: whether the BMC's Redfish service answers this PC without a login (the host
/// interface's "AuthNone" mode), and how the host interface is set up.
/// </summary>
public sealed record BmcAccessReply(bool OpenWithoutLogin, List<string> AuthModes, bool? CredentialBootstrapping, string? BootstrapRole);

/// <summary>
/// Builds <see cref="BiosSettingsReply"/> from the BMC's <c>Systems/Self/Bios</c> page (current values) and its
/// attribute registry (names, help, allowed values, defaults). The BIOS publishes both when Setup's "Publish HII
/// Resources" is on. Passwords are never included.
/// </summary>
public static class BiosSettingsParser
{
    public static BiosSettingsReply Build(JsonNode bios, JsonNode registry)
    {
        var values = bios["Attributes"] as JsonObject ?? throw new IOException("the BMC's BIOS page has no Attributes");
        var entries = registry["RegistryEntries"]?["Attributes"] as JsonArray ?? throw new IOException("the BIOS attribute registry has no attributes");
        var settings = new List<BiosSetting>();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var name = entry["AttributeName"]?.GetValue<string>();
            var type = entry["Type"]?.GetValue<string>() ?? "";
            if (name is null || type == "Password") continue;
            var choices = (entry["Value"] as JsonArray)?.OfType<JsonObject>()
                .Where(v => v["ValueName"] is not null)
                .ToDictionary(v => Scalar(v["ValueName"]) ?? "", v => v["ValueDisplayName"]?.GetValue<string>() ?? Scalar(v["ValueName"]) ?? "", StringComparer.Ordinal);
            // Setup's checkboxes come through as a true/false value with a registry entry claiming the choices
            // "None"/"Disabled" (AMI, 14.5.0): shown as Enabled/Disabled; a "None" default means no known default.
            var current = values.TryGetPropertyValue(name, out var c) ? c : null;
            var checkbox = current is JsonValue cv && cv.TryGetValue<bool>(out _);
            string? Words(JsonNode? node)
            {
                if (node is JsonValue bv && bv.TryGetValue<bool>(out var flag)) return flag ? "Enabled" : "Disabled";
                var raw = Scalar(node);
                if (raw is null) return null;
                if (checkbox) return raw is "Enabled" or "Disabled" ? raw : null;
                if (choices is not null && choices.TryGetValue(raw, out var shown)) return shown;
                if (type == "HexInteger" && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hex))
                    return "0x" + hex.ToString("X", CultureInfo.InvariantCulture);
                return raw;
            }
            settings.Add(new BiosSetting(name, entry["DisplayName"]?.GetValue<string>()?.Trim() is { Length: > 0 } d ? d : name,
                entry["HelpText"]?.GetValue<string>()?.Trim(), type,
                Words(current),
                Words(entry["DefaultValue"]), entry["ResetRequired"]?.GetValue<bool>() ?? false));
        }
        return new BiosSettingsReply(bios["AttributeRegistry"]?.GetValue<string>() ?? "", settings);
    }

    /// <summary>Up to the first character that isn't printable ASCII (some Setup strings carry leftover bytes after it).</summary>
    private static string Printable(string s)
    {
        var end = 0;
        while (end < s.Length && s[end] is >= ' ' and <= '~') end++;
        return s[..end].TrimEnd();
    }

    private static string? Scalar(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => Printable(s),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };
}
