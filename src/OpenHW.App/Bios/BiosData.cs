using System.Text.Json.Nodes;

namespace OpenHW.App.Bios;

/// <summary>A show/hide/lock rule: an RPN expression over question values (IFR suppressif / grayoutif / disableif).</summary>
public sealed record BiosRule(string Kind, JsonArray Rpn);

/// <summary>One choice of a one-of setting; it can carry its own hide rules.</summary>
public sealed record BiosOption(string Label, long Value, IReadOnlyList<BiosRule> Rules);

/// <summary>
/// One menu item, decoded from the BIOS capsule's IFR (tools/bios-data/build_bios_data.py). Kinds: sub (subtitle),
/// text, oneof, num, check, string, password, ref (link to a page), act/action, date, time; plus the runtime-built
/// bootopt / bootnow rows.
/// </summary>
public sealed class BiosItem
{
    public string T { get; init; } = "";
    public string P { get; init; } = "";
    public string H { get; init; } = "";
    public string Id { get; init; } = "";
    public long? Q { get; init; }
    /// <summary>Varstore key ("Setup:ec87d643"), byte offset and size; null for questions without storage.</summary>
    public string? Vs { get; init; }
    public int O { get; init; }
    public int Sz { get; init; }
    public IReadOnlyList<BiosRule> Rules { get; init; } = [];
    public bool HasRules { get; init; }
    public int Qf { get; init; }
    public bool HasQf { get; init; }
    public long Min { get; init; }
    public long Max { get; init; }
    public long Step { get; init; }
    public long? IfrDef { get; init; }
    public long? Def { get; init; }
    public IReadOnlyList<BiosOption> Opts { get; init; } = [];
    public int Disp { get; init; }
    public string? V { get; init; }
    /// <summary>A ref's target: (formset or "@ext"/"@aod", form id or null for the formset's root / a name).</summary>
    public (string A, string? B)? Link { get; init; }
    public int Minl { get; init; }
    public int Maxl { get; init; }
    /// <summary>Boot option rows: their slot.</summary>
    public int Slot { get; init; }

    public string Prompt => P.Trim();
    public bool IsSetting => T is "oneof" or "num" or "check" or "string";

    public static BiosItem From(JsonObject o)
    {
        static long? L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l
            : n is JsonValue b && b.TryGetValue<bool>(out var x) ? (x ? 1 : 0) : null;
        static IReadOnlyList<BiosRule> Rules(JsonNode? n) =>
            (n as JsonArray)?.OfType<JsonArray>().Where(r => r.Count == 2 && r[1] is JsonArray)
                .Select(r => new BiosRule(r[0]!.GetValue<string>(), (JsonArray)r[1]!)).ToList() ?? [];
        (string, string?)? link = null;
        if (o["link"] is JsonArray l && l.Count == 2)
            link = (l[0]!.GetValue<string>(), l[1] is JsonValue lv ? (lv.TryGetValue<long>(out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : lv.GetValue<string>()) : null);
        return new BiosItem
        {
            T = o["t"]?.GetValue<string>() ?? "", P = o["p"]?.GetValue<string>() ?? "", H = o["h"]?.GetValue<string>() ?? "",
            Id = o["id"]?.GetValue<string>() ?? "", Q = L(o["q"]), Vs = o["vs"]?.GetValue<string>(),
            O = (int)(L(o["o"]) ?? 0), Sz = (int)(L(o["sz"]) ?? 0),
            Rules = Rules(o["c"]), HasRules = o.ContainsKey("c"), Qf = (int)(L(o["qf"]) ?? 0), HasQf = o.ContainsKey("qf"),
            Min = L(o["min"]) ?? 0, Max = L(o["max"]) ?? 0, Step = L(o["step"]) ?? 0,
            IfrDef = L(o["ifrdef"]), Def = L(o["def"]),
            Opts = (o["opts"] as JsonArray)?.OfType<JsonArray>().Select(a => new BiosOption(
                a[0]?.GetValue<string>() ?? "", L(a[1]) ?? 0, a.Count > 2 ? Rules(a[2]) : [])).ToList() ?? [],
            Disp = (int)(L(o["disp"]) ?? 0), V = o["v"]?.GetValue<string>(), Link = link,
            Minl = (int)(L(o["minl"]) ?? 0), Maxl = (int)(L(o["maxl"]) ?? 0),
        };
    }
}

public sealed record BiosForm(string Title, IReadOnlyList<BiosItem> Items);

/// <summary>One formset: its root form, forms by id, and question id → where its value is stored.</summary>
public sealed class BiosFormset
{
    public string Title { get; init; } = "";
    public long Root { get; init; }
    public Dictionary<long, BiosForm> Forms { get; init; } = [];
    /// <summary>Question id → (varstore key, offset, size), or ("@", volatile id, 0).</summary>
    public Dictionary<long, (string Key, string? Volatile, int Offset, int Size)> Qmap { get; init; } = [];
}

public sealed record BiosVarstore(string Key, string Name, string Guid, int Size, byte[] Defaults);

public sealed record BiosVariant(string Label, string Cbs, string Aod);

/// <summary>The menu database for one BIOS version (data/bios-XXXX.json), decoded from the capsule.</summary>
public sealed class BiosData
{
    public string Board { get; init; } = "";
    public string BiosVersion { get; init; } = "";
    public string Agesa { get; init; } = "";
    public string BuildDate { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public Dictionary<string, BiosVarstore> Varstores { get; init; } = [];
    public Dictionary<string, BiosFormset> Formsets { get; init; } = [];
    public Dictionary<string, BiosVariant> Variants { get; init; } = [];

    /// <summary>Where the app ships its BIOS data: next to the app, Bios/Data.</summary>
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "Bios", "Data");

    /// <summary>The newest bios-*.json in <paramref name="dir"/>.</summary>
    public static string? Latest(string? dir = null)
    {
        dir ??= DataDir;
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "bios-*.json").Order(StringComparer.Ordinal).LastOrDefault()
            : null;
    }

    public static BiosData Load(string path)
    {
        using var stream = File.OpenRead(path);
        var root = JsonNode.Parse(stream, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 256 })!.AsObject();
        var data = new BiosData
        {
            Board = root["board"]?.GetValue<string>() ?? "", BiosVersion = root["bios_version"]?.GetValue<string>() ?? "",
            Agesa = root["agesa"]?.GetValue<string>() ?? "", BuildDate = root["build_date"]?.GetValue<string>() ?? "",
            SourceFile = root["source_file"]?.GetValue<string>() ?? "",
        };
        foreach (var (key, node) in root["varstores"]!.AsObject())
        {
            var v = node!.AsObject();
            data.Varstores[key] = new BiosVarstore(key, v["name"]!.GetValue<string>(), v["guid"]!.GetValue<string>(),
                v["size"]!.GetValue<int>(), Convert.FromHexString(v["defaults"]?.GetValue<string>() ?? ""));
        }
        foreach (var (key, node) in root["variants"]!.AsObject())
            data.Variants[key] = new BiosVariant(node!["label"]!.GetValue<string>(), node["cbs"]!.GetValue<string>(), node["aod"]!.GetValue<string>());
        foreach (var (fsid, node) in root["formsets"]!.AsObject())
        {
            var f = node!.AsObject();
            var formset = new BiosFormset { Title = f["title"]?.GetValue<string>() ?? fsid, Root = f["root"]?.GetValue<long>() ?? 0 };
            foreach (var (fid, form) in f["forms"]!.AsObject())
                formset.Forms[long.Parse(fid, System.Globalization.CultureInfo.InvariantCulture)] = new BiosForm(
                    form!["title"]?.GetValue<string>() ?? "", form["items"]!.AsArray().OfType<JsonObject>().Select(BiosItem.From).ToList());
            foreach (var (qid, loc) in f["qmap"]?.AsObject() ?? [])
            {
                var a = loc!.AsArray();
                var q = long.Parse(qid, System.Globalization.CultureInfo.InvariantCulture);
                var first = a[0]!.GetValue<string>();
                formset.Qmap[q] = first == "@"
                    ? ("@", a[1]!.ToString(), 0, 0)
                    : (first, null, a[1]!.GetValue<int>(), a[2]!.GetValue<int>());
            }
            data.Formsets[fsid] = formset;
        }
        return data;
    }
}
