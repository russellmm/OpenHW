using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenHW.App.Bios;

/// <summary>A choice of a one-of setting with what it means.</summary>
public sealed record BiosChoice(string Label, long Value, string Meaning, bool Current, bool Default);

/// <summary>Everything the details panel shows for one setting.</summary>
public sealed class BiosDetails
{
    public string Title { get; init; } = "";
    public string What { get; init; } = "";
    public string Tip { get; init; } = "";
    public string BiosHelp { get; init; } = "";
    public bool FromManual { get; init; }
    public string Allowed { get; init; } = "";
    public List<BiosChoice> Choices { get; } = [];
    public List<(string Term, string Meaning)> Terms { get; } = [];
    public List<string> Visibility { get; init; } = [];
    public string? Default { get; init; }
    public string? Current { get; init; }
    public string? Machine { get; init; }
}

/// <summary>
/// The details for a setting (ported from the BIOS emulator's biosemu/helpinfo.py): plain-English explanation and
/// meaning of each choice, exactly which values are allowed (and checking typed input against them), glossary
/// terms, and why it's hidden or locked. Sources, best first: Bios/Data/explanations.json (hand-written),
/// manual_notes.json (parsed from the ASUS manual), the BIOS's own help text.
/// </summary>
public sealed partial class BiosHelp
{
    private readonly BiosModel _m;
    private readonly Dictionary<string, string> _generic = [];
    private readonly Dictionary<string, string> _glossary = [];
    private readonly Dictionary<string, JsonObject> _settings = [];
    private readonly Dictionary<string, JsonObject> _rules = [];
    private readonly Dictionary<string, JsonObject> _manual = [];
    private readonly List<(string Term, Regex Rx)> _glossRx = [];

    public BiosHelp(BiosModel model, string? dataDir = null)
    {
        _m = model;
        dataDir ??= BiosData.DataDir;
        var ex = Load(Path.Combine(dataDir, "explanations.json"));
        foreach (var (k, v) in ex["generic_options"] as JsonObject ?? []) _generic[k] = v?.GetValue<string>() ?? "";
        foreach (var (k, v) in ex["glossary"] as JsonObject ?? []) _glossary[k] = v?.GetValue<string>() ?? "";
        foreach (var (k, v) in ex["settings"] as JsonObject ?? []) if (v is JsonObject o) _settings[Norm(k)] = o;
        foreach (var (k, v) in ex["value_rules"] as JsonObject ?? []) if (v is JsonObject o) _rules[Norm(k)] = o;
        foreach (var (k, v) in Load(Path.Combine(dataDir, "manual_notes.json"))) if (v is JsonObject o) _manual[Norm(k)] = o;
        foreach (var term in _glossary.Keys.OrderByDescending(t => t.Length))
            _glossRx.Add((term, new Regex($"(?<![A-Za-z0-9]){Regex.Escape(term)}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)));
    }

    private static JsonObject Load(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static string Norm(string s) => NonKey().Replace(s.ToLowerInvariant(), "");

    /// <summary>Lookup keys for a prompt: exact, then with numbers generalised to '#'.</summary>
    private static List<string> Keys(string prompt)
    {
        var p = prompt.Trim().TrimStart('-').Trim();
        var p2 = PmicPrefix().Replace(p, "");
        var output = new List<string>();
        foreach (var x in new[] { p, p2 })
            foreach (var k in new[] { Norm(x), Norm(Digits().Replace(x, "#")) })
                if (k.Length > 0 && !output.Contains(k)) output.Add(k);
        return output;
    }

    private static JsonObject? Find(Dictionary<string, JsonObject> table, string prompt)
    {
        foreach (var k in Keys(prompt)) if (table.TryGetValue(k, out var v)) return v;
        return null;
    }

    private static string? S(JsonNode? n) => n is JsonValue v ? v.ToString() : null;

    // ── allowed values ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Numeric / format rule for a typed field or number: min, max, step, unit, format, note, decimals.</summary>
    public Dictionary<string, string> ValueRule(BiosItem it)
    {
        var rule = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Find(_manual, it.P)?["range"] is JsonObject range)
            foreach (var (k, v) in range) if (S(v) is { } s) rule[k] = s;
        var mm = MinMax().Match(it.H);
        if (mm.Success)
        {
            rule["min"] = mm.Groups[1].Value;
            rule["max"] = mm.Groups[3].Value;
            if (mm.Groups[2].Value.Length > 0) rule["unit"] = mm.Groups[2].Value;
        }
        if (Find(_rules, it.P) is { } cur)
            foreach (var (k, v) in cur) if (S(v) is { } s) rule[k] = s;
        if (it.T == "num" && !rule.ContainsKey("format"))
        {
            rule.TryAdd("min", it.Min.ToString(CultureInfo.InvariantCulture));
            rule.TryAdd("max", it.Max.ToString(CultureInfo.InvariantCulture));
            if (it.Step > 1) rule.TryAdd("step", it.Step.ToString(CultureInfo.InvariantCulture));
        }
        if (rule.ContainsKey("min"))
        {
            var dec = new[] { "min", "max", "step" }.Max(k => Decimals(rule.GetValueOrDefault(k)));
            rule["decimals"] = dec.ToString(CultureInfo.InvariantCulture);
            if (!rule.ContainsKey("unit") || rule["unit"].Length == 0)
            {
                var u = UnitTag().Match(it.P + " " + it.H);
                if (u.Success) rule["unit"] = u.Groups[1].Value;
                else if (VoltageName().IsMatch(it.P) && dec >= 3) rule["unit"] = "V";
            }
        }
        return rule;
    }

    public string AllowedText(string? fsid, BiosItem it)
    {
        switch (it.T)
        {
            case "oneof": return $"Pick one of the {(fsid is null ? it.Opts.Count : _m.Options(fsid, it).Count)} choices listed.";
            case "check": return "Enabled or Disabled.";
            case "password": return $"{it.Minl} to {(it.Maxl > 0 ? it.Maxl : 20)} characters. Leave empty to remove the password.";
            case not ("num" or "string"): return "";
        }
        var r = ValueRule(it);
        var fmt = r.GetValueOrDefault("format");
        if (fmt == "ipv4") return "An IPv4 address, four numbers 0-255 separated by dots (e.g. 192.168.1.50).";
        if (fmt == "ipv6") return "An IPv6 address (e.g. fd00::50).";
        if (fmt == "mac") return "A MAC address: six pairs of hex digits (e.g. 10-7C-61-00-00-01).";
        if (fmt == "text" || (it.T == "string" && !r.ContainsKey("min")))
            return (it.T == "string" && fmt != "text" ? "Type \"Auto\", or a value. " : "") + $"Up to {(it.Maxl > 0 ? it.Maxl : 16)} characters.";
        var unit = r.GetValueOrDefault("unit") is { Length: > 0 } u ? (u == "x" ? "x" : " " + u) : "";
        var whole = r.GetValueOrDefault("decimals") == "0";
        var s = $"{(whole ? "A whole number from" : "A number from")} {r["min"]} to {r["max"]}{unit}";
        if (r.GetValueOrDefault("step") is { Length: > 0 } step) s += $", in steps of {step}";
        s += ".";
        if (it.T == "string") s = "Type \"Auto\" (let the BIOS decide), or " + char.ToLowerInvariant(s[0]) + s[1..];
        if (it.Disp == 2) s += " Hex values like 0x1F are accepted.";
        if (r.GetValueOrDefault("note") is { Length: > 0 } note) s += " " + note;
        return s;
    }

    /// <summary>Checks a typed value: (ok, value to store — long for numbers, string for typed fields, message).</summary>
    public (bool Ok, object? Value, string Message) Validate(BiosItem it, string text)
    {
        text = text.Trim();
        var r = ValueRule(it);
        if (it.T == "string" && (text.Length == 0 || text.Equals("auto", StringComparison.OrdinalIgnoreCase))) return (true, "", "");
        var fmt = r.GetValueOrDefault("format");
        if (fmt == "ipv4")
        {
            var m = Ipv4().Match(text);
            if (!m.Success || m.Groups.Values.Skip(1).Any(g => int.Parse(g.Value, CultureInfo.InvariantCulture) > 255))
                return (false, null, $"\"{text}\" is not a valid IPv4 address (example: 192.168.1.50).");
            return (true, text, "");
        }
        if (fmt == "mac")
            return Mac().IsMatch(text) ? (true, text.ToUpperInvariant().Replace(':', '-'), "")
                : (false, null, $"\"{text}\" is not a valid MAC address (example: 10-7C-61-00-00-01).");
        if (fmt is "ipv6" or "text" || !r.ContainsKey("min"))
        {
            var maxl = it.Maxl > 0 ? it.Maxl : 64;
            return text.Length > maxl ? (false, null, $"Too long - at most {maxl} characters.") : (true, text, "");
        }
        decimal v;
        try
        {
            v = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return (false, null, $"\"{text}\" is not a number. {AllowedText(null, it)}");
        }
        var lo = decimal.Parse(r["min"], CultureInfo.InvariantCulture);
        var hi = decimal.Parse(r["max"], CultureInfo.InvariantCulture);
        var unitText = r.GetValueOrDefault("unit") is { Length: > 0 } u2 ? " " + u2 : "";
        if (v < lo || v > hi) return (false, null, $"{text} is outside the allowed range {r["min"]} to {r["max"]}{unitText}.");
        var dec = int.Parse(r.GetValueOrDefault("decimals") ?? "0", CultureInfo.InvariantCulture);
        var msg = "";
        if (r.GetValueOrDefault("step") is { Length: > 0 } stepText)
        {
            var st = decimal.Parse(stepText, CultureInfo.InvariantCulture);
            var snapped = lo + Math.Round((v - lo) / st, MidpointRounding.AwayFromZero) * st;
            snapped = Math.Min(Math.Max(snapped, lo), hi);
            if (snapped != v)
            {
                msg = $"{text} isn't a multiple of the {stepText} step, so it was rounded to {Fmt(snapped, dec)}.";
                v = snapped;
            }
        }
        if (it.T == "num")
            return v != decimal.Truncate(v) ? (false, null, "Only whole numbers are allowed here.") : (true, (long)v, msg);
        return (true, Fmt(v, dec), msg);
    }

    // ── explanation ─────────────────────────────────────────────────────────────────────────────────────────────────

    public BiosDetails Details(string fsid, long? fid, BiosItem it)
    {
        var cur = Find(_settings, it.P);
        var man = Find(_manual, it.P);
        var bios = it.H.Replace("\r", "").Trim();
        if (bios.Equals("no help string", StringComparison.OrdinalIgnoreCase) || bios.Equals(it.Prompt, StringComparison.OrdinalIgnoreCase)) bios = "";
        var mdesc = S(man?["desc"]) ?? "";
        mdesc = AdjustKeys().Replace(mdesc, "");
        mdesc = RangeSentence().Replace(mdesc, "");
        mdesc = Spaces().Replace(mdesc, " ").Trim();
        var what = S(cur?["what"]) is { Length: > 0 } w ? w : mdesc.Length > 0 ? mdesc : bios;
        var d = new BiosDetails
        {
            Title = it.Prompt, What = what, Tip = S(cur?["tip"]) ?? "",
            BiosHelp = bios.Length > 0 && bios != what ? bios : "",
            FromManual = S(cur?["what"]) is not { Length: > 0 } && mdesc.Length > 0,
            Allowed = AllowedText(fsid, it),
            Visibility = fid is not null ? _m.VisibilityNotes(fsid, fid, it) : [],
            Default = it.IsSetting ? _m.Display(it, BiosSource.Default) : null,
            Current = it.IsSetting ? _m.Display(it) : null,
            Machine = it.IsSetting ? _m.Display(it, BiosSource.Saved) : null,
        };
        if (it.T == "oneof")
        {
            var curv = _m.Get(it);
            var defv = _m.Get(it, BiosSource.Default);
            var mopts = man?["option_help"] as JsonObject;
            var copts = cur?["options"] as JsonObject;
            foreach (var o in _m.Options(fsid, it))
            {
                var label = o.Label;
                var baseLabel = TwoSpaces().Split(label)[0];
                var meaning = S(copts?[baseLabel]) ?? S(mopts?[baseLabel]) ?? _generic.GetValueOrDefault(baseLabel, "");
                d.Choices.Add(new BiosChoice(label, o.Value, meaning, Equals(curv, o.Value), Equals(defv, o.Value)));
            }
        }
        var hay = string.Join(' ', it.P, what, bios, d.Tip);
        var seen = new HashSet<string>();
        foreach (var (term, rx) in _glossRx)
        {
            if (term.Equals(it.Prompt, StringComparison.OrdinalIgnoreCase) || seen.Contains(_glossary[term])) continue;
            if (rx.IsMatch(hay)) { d.Terms.Add((term, _glossary[term])); seen.Add(_glossary[term]); }
            if (d.Terms.Count >= 5) break;
        }
        return d;
    }

    private static int Decimals(string? s) => s is null || !s.Contains('.') ? 0 : s.Split('.')[1].Length;

    private static string Fmt(decimal v, int dec) =>
        dec > 0 ? v.ToString("F" + dec, CultureInfo.InvariantCulture) : ((long)v).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("[^a-z0-9#]")] private static partial Regex NonKey();
    [GeneratedRegex(@"^PMIC\d+\s+")] private static partial Regex PmicPrefix();
    [GeneratedRegex(@"\d+")] private static partial Regex Digits();
    [GeneratedRegex(@"Min\.?\s*:\s*(-?[\d.]+)\s*([A-Za-z%]*)\s*\|\s*Max\.?\s*:\s*(-?[\d.]+)")] private static partial Regex MinMax();
    [GeneratedRegex(@"\[(W|A|mA|mV|V|MHz|°C)\]")] private static partial Regex UnitTag();
    [GeneratedRegex("voltage|vdd|vid|vpp", RegexOptions.IgnoreCase)] private static partial Regex VoltageName();
    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$")] private static partial Regex Ipv4();
    [GeneratedRegex("^([0-9A-Fa-f]{2}[-:]){5}[0-9A-Fa-f]{2}$")] private static partial Regex Mac();
    [GeneratedRegex(@"Use the <\+> (?:or|and) <-> (?:keys )?to adjust the value\.?")] private static partial Regex AdjustKeys();
    [GeneratedRegex(@"The values? range from .*?(?:interval|increment|step) of [-\d.]+\s*\w*\.?")] private static partial Regex RangeSentence();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex TwoSpaces();
}
