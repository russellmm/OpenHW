using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenHW.Hardware;

namespace OpenHW.App.Bios;

/// <summary>Where a value is read from: the factory defaults, the machine's current settings, or the edits on screen.</summary>
public enum BiosSource { Default, Saved, Work }

/// <summary>"hide", "gray" or "ok" for an item under its show/hide/lock rules.</summary>
public enum BiosState { Ok, Gray, Hide }

/// <summary>A setting whose value differs between two sources, for the change lists.</summary>
public sealed record BiosChange(string Fsid, long Fid, BiosItem Item, string From, string To);

/// <summary>
/// The BIOS settings engine (ported from the BIOS emulator's biosemu/model.py): menus, values, show/hide rules,
/// planned changes, favorites and profiles. No UI code.
/// <para>
/// Values live as byte buffers per UEFI variable ("varstore"), addressed by offset, exactly as the firmware keeps them.
/// <b>Saved</b> is this machine's current settings, read from its UEFI variables (<see cref="IFirmwareVariables"/>);
/// a variable that can't be read falls back to its factory default (and, for runtime detection flags, to values worked
/// out from the platform profile — the "overlay"). <b>Work</b> is Saved plus the edits on screen. OpenHW doesn't write
/// the firmware: Save keeps the edits as a <b>plan</b> (per setting, in ~/.config/openhw/bios/plan.json) to enter in the
/// real BIOS; a planned value the machine has since taken drops off by itself.
/// </para>
/// </summary>
public sealed class BiosModel
{
    /// <summary>The top menu tabs, as on the ASUS Advanced Mode screen: (title, formset, form or null for its root).</summary>
    public static readonly (string Name, string Fsid, long? Fid)[] Tabs =
    [
        ("My Favorites", "fav", null), ("Main", "setup", 10003), ("Ai Tweaker", "setup", 10004), ("Advanced", "setup", 10005),
        ("Monitor", "setup", 10006), ("Boot", "setup", 10008), ("Tool", "setup", 10009), ("Server Mgmt", "servermgmt", null),
        ("Exit", "setup", 10010),
    ];

    /// <summary>Pages AMI adds to the bottom of Advanced at runtime (after "AMD PBS").</summary>
    public static readonly (string Fsid, string Title)[] AdvancedDynamic = [("memstatus", "AMD Mem Configuration Status"), ("@aod", "AMD Overclocking")];

    public static readonly Dictionary<string, string> ThirdParty = new()
    {
        ["raid"] = "RAIDXpert2 Configuration Utility", ["realtek"] = "Realtek Ethernet Controller",
        ["ipv4"] = "IPv4 Network Configuration", ["ipv6"] = "IPv6 Network Configuration", ["httpboot"] = "HTTP Boot Configuration",
    };

    public BiosData Db { get; }
    public JsonObject Platform { get; private set; }
    public string StateDir { get; }
    public bool ShowHidden { get; set; }

    private readonly Dictionary<string, byte[]> _defaults = [];
    private readonly Dictionary<string, string> _byName = new(StringComparer.Ordinal);
    private Dictionary<string, byte[]> _saved = [];
    private Dictionary<string, byte[]> _work = [];
    private Dictionary<string, object> _savedVol = new(StringComparer.Ordinal);
    private Dictionary<string, object> _workVol = new(StringComparer.Ordinal);
    private Dictionary<string, Dictionary<int, (int Size, long Value)>> _overlay = [];

    /// <summary>Varstores read from this machine (the others are factory defaults).</summary>
    public HashSet<string> FromMachine { get; } = new(StringComparer.Ordinal);

    /// <summary>Variables this BIOS's menus use that the machine doesn't expose, or whose size differs (another BIOS version).</summary>
    public List<string> NotFromMachine { get; } = [];

    public List<string> Favorites { get; private set; } = [];
    public Dictionary<string, bool> Passwords { get; } = new() { ["admin"] = false, ["user"] = false };
    public List<int> BootOrder { get; private set; } = [];
    public string? PlannedAt { get; private set; }
    public DateTime ReadAt { get; private set; }

    public BiosModel(BiosData db, IFirmwareVariables? machine, string stateDir, JsonObject? platform = null)
    {
        Db = db;
        StateDir = stateDir;
        Directory.CreateDirectory(Path.Combine(stateDir, "profiles"));
        Platform = platform ?? LoadPlatform(stateDir);
        foreach (var (k, v) in db.Varstores)
        {
            _defaults[k] = v.Defaults.Length == v.Size ? v.Defaults : Resize(v.Defaults, v.Size);
            _byName.TryAdd(v.Name, k);
        }
        Machine = machine;
        LoadState();
        BuildOverlay();
        IndexItems();
    }

    public IFirmwareVariables? Machine { get; }

    private static byte[] Resize(byte[] b, int size) { var r = new byte[size]; b.AsSpan(0, Math.Min(b.Length, size)).CopyTo(r); return r; }

    // ── files ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string DefaultStateDir => Path.Combine(Settings.AppSettings.ConfigDir, "bios");

    private string PlanFile => Path.Combine(StateDir, "plan.json");
    private string PlatformFile => Path.Combine(StateDir, "platform.json");

    /// <summary>The saved platform profile, else the shipped one (stand-in hardware the menus' detection rules need).</summary>
    public static JsonObject LoadPlatform(string stateDir)
    {
        foreach (var p in new[] { Path.Combine(stateDir, "platform.json"), Path.Combine(BiosData.DataDir, "platform", "wrx90e-9975wx.json") })
            if (File.Exists(p)) return JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        return new JsonObject();
    }

    public void SavePlatform(JsonObject platform)
    {
        Platform = platform;
        File.WriteAllText(PlatformFile, platform.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        BuildOverlay();
        IndexItems();
    }

    /// <summary>Reads the machine's variables again (Saved), keeping the plan on top.</summary>
    public void Reload()
    {
        var plan = PendingIds().ToDictionary(id => id, id => Get(Items[id].Item, BiosSource.Work));
        LoadState(plan);
        IndexItems();
    }

    private void LoadState(Dictionary<string, object>? keepPlan = null)
    {
        _saved = _defaults.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
        FromMachine.Clear();
        NotFromMachine.Clear();
        if (Machine is not null)
            foreach (var (k, v) in Db.Varstores)
            {
                var bytes = Machine.Read(v.Name, v.Guid);
                if (bytes is not null && bytes.Length == v.Size) { _saved[k] = bytes; FromMachine.Add(k); }
                else NotFromMachine.Add(bytes is null ? v.Name : $"{v.Name} ({bytes.Length} bytes, expected {v.Size})");
            }
        ReadAt = DateTime.Now;
        _savedVol = new(StringComparer.Ordinal);
        var bootCount = BootNames().Count;
        if (BootOrder.Count != bootCount) BootOrder = Enumerable.Range(0, bootCount).ToList();
        Dictionary<string, object> plan = keepPlan ?? [];
        if (keepPlan is null && File.Exists(PlanFile))
        {
            Favorites = [];
            try
            {
                var st = JsonNode.Parse(File.ReadAllText(PlanFile))!.AsObject();
                foreach (var f in st["favorites"]?.AsArray() ?? []) if (f?.GetValue<string>() is { } id) Favorites.Add(id);
                foreach (var key in new[] { "admin", "user" }) if (st["passwords"]?[key] is JsonValue pv) Passwords[key] = pv.GetValue<bool>();
                if (st["boot_order"] is JsonArray bo && bo.Count == bootCount)
                {
                    var order = bo.Select(n => n!.GetValue<int>()).ToList();
                    if (order.Order().SequenceEqual(BootOrder)) BootOrder = order;
                }
                PlannedAt = st["planned_at"]?.GetValue<string>();
                foreach (var (id, node) in st["plan"]?.AsObject() ?? [])
                    if (node is JsonValue v) plan[id] = v.TryGetValue<long>(out var l) ? l : v.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or IOException) { }
        }
        _work = _saved.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
        _workVol = new(_savedVol, StringComparer.Ordinal);
        _pendingPlan = plan;
    }

    // The plan is applied once the items are indexed (it names items by id).
    private Dictionary<string, object> _pendingPlan = [];

    private void ApplyPlan()
    {
        foreach (var (id, value) in _pendingPlan)
            if (Items.TryGetValue(id, out var at) && at.Item.IsSetting) Set(at.Item, value);
        _pendingPlan = [];
    }

    /// <param name="edits">True: the plan becomes the edits on screen. False: keep the plan as last saved (meta only).</param>
    private void WritePlan(bool edits = true)
    {
        var plan = new JsonObject();
        if (edits)
            foreach (var id in PendingIds())
            {
                var v = Get(Items[id].Item, BiosSource.Work);
                plan[id] = v is long l ? JsonValue.Create(l) : JsonValue.Create((string)v);
            }
        else
            try { if (File.Exists(PlanFile) && JsonNode.Parse(File.ReadAllText(PlanFile))?["plan"] is JsonObject old) plan = (JsonObject)old.DeepClone(); }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        var st = new JsonObject
        {
            ["bios_version"] = Db.BiosVersion, ["planned_at"] = PlannedAt, ["plan"] = plan,
            ["favorites"] = new JsonArray(Favorites.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["passwords"] = new JsonObject { ["admin"] = Passwords["admin"], ["user"] = Passwords["user"] },
            ["boot_order"] = new JsonArray(BootOrder.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
        };
        var tmp = PlanFile + ".tmp";
        File.WriteAllText(tmp, st.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, PlanFile, overwrite: true);
    }

    // ── platform ────────────────────────────────────────────────────────────────────────────────────────────────────

    public string Variant => Platform["variant"]?.GetValue<string>() ?? "SHP";

    private JsonObject Obj(string key) => Platform[key] as JsonObject ?? new JsonObject();

    /// <summary>
    /// Runtime-only values the real BIOS fills in at boot (hardware detection) — used only for variables the machine
    /// doesn't expose; where it does (AutoDetectData, SetupAiFeatures, AsusHwmDeviceStatus on the WRX90E), its own bytes count.
    /// </summary>
    private void BuildOverlay()
    {
        var cpu = Obj("cpu");
        var cores = cpu["cores"]?.GetValue<int>() ?? 96;
        var ccds = cpu["ccds"]?.GetValue<int>() ?? 12;
        var dimms = (Obj("memory")["dimms_populated"] as JsonArray)?.Select(n => n!.GetValue<int>()).ToList() ?? Enumerable.Repeat(1, 8).ToList();
        var gen = Variant == "SHP" ? 2 : 1;
        var ov = new Dictionary<string, Dictionary<int, (int, long)>>(StringComparer.Ordinal);
        void Put(string name, int off, int size, long val)
        {
            if (_byName.TryGetValue(name, out var k) && !FromMachine.Contains(k))
                (ov.TryGetValue(k, out var d) ? d : ov[k] = [])[off] = (size, val);
        }
        Put("AutoDetectData", 0, 1, gen);
        Put("AmiAmdSbSuppressIfSetupData", 0, 1, gen);
        for (var n = 0; n < 16; n++) Put("AutoDetectData", 2 + n, 1, n < ccds ? 1 : 0);
        Put("AutoDetectData", 19, 1, cores);
        for (var n = 0; n < 8; n++) Put("AutoDetectData", 20 + n, 1, n < dimms.Count && dimms[n] != 0 ? 1 : 0);
        Put("AutoDetectData", 28, 1, 0);
        Put("SetupAiFeatures", 0, 1, ccds);
        Put("SetupAiFeatures", 1, 1, cores);
        Put("SystemAccess", 0, 1, 0);
        var mem = Obj("memory");
        var oc = mem["oc_profiles"]?.GetValue<string>() ?? "none";
        var spd = mem["spd"] as JsonObject;
        var nXmp = spd is null ? 1 : (spd["xmp"] as JsonArray)?.Count ?? 0;
        var nExpo = spd is null ? 1 : (spd["expo"] as JsonArray)?.Count ?? 0;
        Put("VARSTORE_OCMR_SETTINGS_NAME", 98, 1, oc is "DOCP" or "both" ? (1L << nXmp) - 1 : 0);
        Put("VARSTORE_OCMR_SETTINGS_NAME", 101, 1, oc is "EXPO" or "both" ? (1L << nExpo) - 1 : 0);
        for (var off = 0; off < 30; off++) Put("AsusHwmDeviceStatus", off, 1, 1);
        var cooler = Platform["cooler"]?.GetValue<string>() ?? "none";
        Put("AsusHwmDeviceStatus", 4, 1, cooler == "ROG RYUJIN II" ? 1 : 0);
        Put("AsusHwmDeviceStatus", 5, 1, cooler == "ROG RYUJIN II EVA" ? 1 : 0);
        Put("DynamicPageCount", 0, 2, 0);
        Put("IDESecDev", 0, 2, 0);
        foreach (var off in new[] { 3, 4, 5 }) Put("PcieSataSetupVar", off, 1, 1);
        foreach (var var in new[] { "AodSetupShp", "AodSetupStp" })
            foreach (var off in new[] { 3, 4, 7, 8, 9, 10 }) Put(var, off, 1, 1);
        foreach (var o in (Platform["runtime_overrides"] as JsonArray)?.OfType<JsonObject>() ?? [])
            Put(o["var"]!.GetValue<string>(), o["off"]!.GetValue<int>(), o["size"]!.GetValue<int>(), o["value"]!.GetValue<long>());
        _overlay = ov;
    }

    // ── memory (Ai Tweaker) ─────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> MemItems = new() { ["tuner"] = "Ai Overclock Tuner", ["docp"] = "DOCP", ["expo"] = "EXPO", ["freq"] = "Memory Frequency" };
    private readonly Dictionary<string, BiosItem?> _memCache = [];

    private BiosItem? MemItem(string key)
    {
        if (!_memCache.TryGetValue(key, out var it))
            _memCache[key] = it = Form("setup", 10004).Items.FirstOrDefault(i => i.Prompt == MemItems[key] && i.Vs is not null);
        return it;
    }

    public JsonObject Spd()
    {
        var mem = Obj("memory");
        if (mem["spd"] is JsonObject spd) return spd;
        var f = (mem["frequency"]?.ToString() ?? "4800").Split(' ')[0];
        return new JsonObject
        {
            ["jedec"] = new JsonObject { ["name"] = "Default (JEDEC)", ["speed"] = int.TryParse(f, out var s) ? s : 4800 },
            ["expo"] = new JsonArray(), ["xmp"] = new JsonArray(),
        };
    }

    /// <summary>Memory speed/timings the board runs with, from Ai Overclock Tuner + profile + frequency.</summary>
    public JsonObject MemoryConfig(BiosSource src = BiosSource.Saved)
    {
        var spd = Spd();
        var prof = spd["jedec"]!.DeepClone().AsObject();
        long G(string k) => MemItem(k) is { } i && Get(i, src) is long l ? l : 0;
        var tuner = G("tuner");
        if (tuner is 5 or 6 or 11 or 12 && spd["xmp"] is JsonArray { Count: > 0 } xmp)
        {
            var i = G("docp");
            if (i < xmp.Count) prof = xmp[(int)i]!.DeepClone().AsObject();
        }
        else if (tuner is 7 or 8 && spd["expo"] is JsonArray { Count: > 0 } expo)
        {
            var i = G("expo");
            if (i < expo.Count) prof = expo[(int)i]!.DeepClone().AsObject();
        }
        var forced = G("freq");
        if (forced != 0 && forced != (prof["speed"]?.GetValue<long>() ?? 0))
            prof = new JsonObject { ["name"] = (prof["name"]?.GetValue<string>() ?? "") + " @ forced frequency", ["speed"] = forced, ["volt"] = prof["volt"]?.DeepClone() };
        return prof;
    }

    public static string ProfileLabel(JsonObject p)
    {
        var t = string.Join('-', new[] { "cl", "trcd", "trp" }.Where(p.ContainsKey).Select(k => p[k]!.ToString()));
        return p["volt"] is JsonValue v
            ? string.Create(CultureInfo.InvariantCulture, $"DDR5-{p["speed"]} {t} {v.GetValue<double>():0.00}V")
            : $"DDR5-{p["speed"]}";
    }

    public Dictionary<string, string> TextVars()
    {
        var cpu = Obj("cpu");
        var mem = Obj("memory");
        var dimms = (mem["dimms_populated"] as JsonArray)?.Count(n => n!.GetValue<int>() != 0) ?? 0;
        var total = mem["total_mb"]?.GetValue<long>() ?? dimms * (mem["dimm_size_gb"]?.GetValue<long>() ?? 0) * 1024;
        var run = MemoryConfig(BiosSource.Saved);
        var tgt = MemoryConfig(BiosSource.Work);
        string S(JsonNode? n) => n?.ToString() ?? "";
        return new()
        {
            ["bios_version"] = Db.BiosVersion, ["build_date"] = Db.BuildDate.Length > 0 ? Db.BuildDate : "N/A",
            ["agesa"] = Db.Agesa.Length > 0 ? Db.Agesa : "N/A", ["cpu_brand"] = S(cpu["brand"]) is { Length: > 0 } b ? b : "N/A",
            ["cpu_speed"] = S(cpu["speed_mhz"]) is { Length: > 0 } c ? c : "N/A", ["total_mb"] = total.ToString(CultureInfo.InvariantCulture),
            ["mem_freq"] = S(run["speed"]), ["target_mem_freq"] = S(tgt["speed"]),
            ["tcl"] = S(run["cl"]), ["trcd"] = S(run["trcd"]), ["trp"] = S(run["trp"]), ["tras"] = S(run["tras"]),
        };
    }

    /// <summary>Text the firmware fills in at runtime ("Main|BIOS Version"), from the platform profile's templates.</summary>
    public string? RuntimeText(string formTitle, string prompt)
    {
        var key = $"{formTitle}|{prompt.Trim().TrimEnd(':').Trim()}";
        if (Obj("text")[key]?.GetValue<string>() is not { } t) return null;
        foreach (var (k, v) in TextVars()) t = t.Replace("{" + k + "}", v, StringComparison.Ordinal);
        return t;
    }

    public IReadOnlyList<string> BootNames() =>
        (Platform["boot_options"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToList() ?? [];

    // ── values ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static long Rd(byte[] buf, int off, int size)
    {
        long v = 0;
        for (var i = size - 1; i >= 0; i--) v = v << 8 | (off + i < buf.Length ? buf[off + i] : (byte)0);
        return v;
    }

    private static void Wr(byte[] buf, int off, int size, long val)
    {
        for (var i = 0; i < size && off + i < buf.Length; i++) buf[off + i] = (byte)(val >> (8 * i));
    }

    public long QValue(string fsid, long qid)
    {
        if (!Db.Formsets[fsid].Qmap.TryGetValue(qid, out var loc)) return 0;
        if (loc.Key == "@") return _workVol.TryGetValue(loc.Volatile!, out var v) && v is long l ? l : 0;
        if (_overlay.TryGetValue(loc.Key, out var ov) && ov.TryGetValue(loc.Offset, out var x)) return x.Value;
        return _work.TryGetValue(loc.Key, out var buf) ? Rd(buf, loc.Offset, loc.Size) : 0;
    }

    private static string VolId(BiosItem it) => $"{it.Id.Split(':')[0]}:{it.Q}";

    /// <summary>A setting's value: long, or string for typed (string) fields.</summary>
    public object Get(BiosItem it, BiosSource src = BiosSource.Work)
    {
        if (it.Vs is { } key && _defaults.ContainsKey(key))
        {
            byte[] buf;
            if (src == BiosSource.Default) buf = _defaults[key];
            else
            {
                if (_overlay.TryGetValue(key, out var ov) && ov.TryGetValue(it.O, out var x) && it.T != "string") return x.Value;
                buf = src == BiosSource.Work ? _work[key] : _saved[key];
            }
            if (it.T == "string")
            {
                var end = Math.Min(it.O + it.Sz, buf.Length);
                var text = System.Text.Encoding.Unicode.GetString(buf, it.O, Math.Max(0, end - it.O) & ~1);
                var nul = text.IndexOf('\0');
                text = nul >= 0 ? text[..nul] : text;
                // The factory defaults leave a typed field empty; the running firmware writes the word "Auto". Same value.
                return text.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "" : text;
            }
            return Rd(buf, it.O, it.Sz);
        }
        if (src == BiosSource.Default) return VolDefault(it);
        var d = src == BiosSource.Work ? _workVol : _savedVol;
        return d.TryGetValue(VolId(it), out var val) ? val : VolDefault(it);
    }

    private static object VolDefault(BiosItem it)
    {
        if (it.T == "string") return "";
        return it.IfrDef ?? it.Def ?? (it.Opts.Count > 0 ? it.Opts[0].Value : it.Min);
    }

    public void Set(BiosItem it, object val)
    {
        if (it.Vs is { } key && _work.TryGetValue(key, out var buf))
        {
            if (it.T == "string")
            {
                var raw = System.Text.Encoding.Unicode.GetBytes(val.ToString() ?? "");
                if (raw.Length > it.Sz - 2) raw = raw[..Math.Max(0, (it.Sz - 2) & ~1)];
                for (var i = 0; i < it.Sz && it.O + i < buf.Length; i++) buf[it.O + i] = i < raw.Length ? raw[i] : (byte)0;
            }
            else Wr(buf, it.O, it.Sz, Convert.ToInt64(val, CultureInfo.InvariantCulture));
        }
        else _workVol[VolId(it)] = val;
    }

    public string Display(BiosItem it, BiosSource src = BiosSource.Work)
    {
        var v = Get(it, src);
        switch (it.T)
        {
            case "oneof":
                foreach (var o in OptionsNamed(it)) if (o.Value == (long)v) return o.Label.Length > 0 ? o.Label : o.Value.ToString(CultureInfo.InvariantCulture);
                return $"0x{(long)v:X}";
            case "check": return (long)v != 0 ? "Enabled" : "Disabled";
            case "num": return it.Disp == 2 ? $"0x{(long)v:X}" : ((long)v).ToString(CultureInfo.InvariantCulture);
            case "string": return v is string s && s.Length > 0 ? s : "Auto";
            default: return "";
        }
    }

    private static readonly Dictionary<string, string[]> RuntimeOptionNames = new() { ["System Language"] = ["English", "Français"] };

    /// <summary>Options, with names filled in for lists the firmware builds at runtime (DOCP/EXPO profiles, languages).</summary>
    public IReadOnlyList<BiosOption> OptionsNamed(BiosItem it)
    {
        var opts = it.Opts;
        if (it.Vs is not null && it.Prompt is "DOCP" or "EXPO" && ReferenceEquals(it, MemItem(it.Prompt.ToLowerInvariant())))
        {
            var profs = Spd()[it.Prompt == "DOCP" ? "xmp" : "expo"] as JsonArray ?? [];
            var named = opts.Select((o, i) => (o, i)).Where(x => x.i < profs.Count)
                .Select(x => x.o with { Label = x.o.Label + "  " + ProfileLabel(profs[x.i]!.AsObject()) }).ToList();
            return named.Count > 0 ? named : opts;
        }
        if (RuntimeOptionNames.TryGetValue(it.Prompt, out var names) && opts.All(o => o.Label.Length == 0))
            return opts.Select((o, i) => o with { Label = i < names.Length ? names[i] : o.Value.ToString(CultureInfo.InvariantCulture) }).ToList();
        return opts;
    }

    /// <summary>The choices of a one-of setting that are allowed right now.</summary>
    public IReadOnlyList<BiosOption> Options(string fsid, BiosItem it) =>
        OptionsNamed(it).Where(o => !o.Rules.Any(r => r.Kind == "hide" && EvalRpn(fsid, r.Rpn))).ToList();

    // ── conditions ──────────────────────────────────────────────────────────────────────────────────────────────────

    public bool EvalRpn(string fsid, JsonArray rpn)
    {
        var st = new Stack<long>();
        static long B(bool b) => b ? 1 : 0;
        try
        {
            foreach (var node in rpn)
            {
                var t = node!.AsArray();
                var op = t[0]!.GetValue<string>();
                long Arg(int i) => t[i]!.GetValue<long>();
                switch (op)
                {
                    case "eqv": st.Push(B(QValue(fsid, Arg(1)) == Arg(2))); break;
                    case "eqid": st.Push(B(QValue(fsid, Arg(1)) == QValue(fsid, Arg(2)))); break;
                    case "eqlist": { var q = QValue(fsid, Arg(1)); st.Push(B(t[2]!.AsArray().Any(x => x!.GetValue<long>() == q))); break; }
                    case "qref": st.Push(QValue(fsid, Arg(1))); break;
                    case "const": st.Push(t[1] is JsonValue cv && cv.TryGetValue<bool>(out var cb) ? B(cb) : Arg(1)); break;
                    case "true": case "one": st.Push(1); break;
                    case "false": case "zero": st.Push(0); break;
                    case "ones": st.Push(-1); break;
                    case "not": st.Push(B(st.Pop() == 0)); break;
                    case "bnot": st.Push(~st.Pop()); break;
                    case "tobool": st.Push(B(st.Pop() != 0)); break;
                    case "touint": break;
                    case "dup": st.Push(st.Peek()); break;
                    case "cond": { var c = st.Pop(); var b = st.Pop(); var a = st.Pop(); st.Push(a != 0 ? b : c); break; }
                    default:
                    {
                        var b = st.Pop();
                        var a = st.Pop();
                        st.Push(op switch
                        {
                            "and" => B(a != 0 && b != 0), "or" => B(a != 0 || b != 0), "eq" => B(a == b), "ne" => B(a != b),
                            "gt" => B(a > b), "ge" => B(a >= b), "lt" => B(a < b), "le" => B(a <= b), "band" => a & b, "bor" => a | b,
                            "add" => a + b, "sub" => a - b, "mul" => a * b, "div" => b != 0 ? a / b : 0, "mod" => b != 0 ? a % b : 0,
                            "shl" => a << (int)b, "shr" => a >> (int)b,
                            _ => throw new InvalidOperationException(op),
                        });
                        break;
                    }
                }
            }
            return st.Count > 0 && st.Peek() != 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException or ArgumentOutOfRangeException) { return false; }
    }

    public BiosState StateOf(string fsid, BiosItem it)
    {
        if (BoardHidden.ContainsKey(it.Id)) return BiosState.Hide;
        var gray = false;
        foreach (var r in it.Rules)
            if (EvalRpn(fsid, r.Rpn))
            {
                if (r.Kind == "hide") return BiosState.Hide;
                gray = true;
            }
        if ((it.Qf & 0x01) != 0) gray = true;
        return gray ? BiosState.Gray : BiosState.Ok;
    }

    // ── navigation ──────────────────────────────────────────────────────────────────────────────────────────────────

    public (string Fsid, long Fid)? FormRoot(string fsid)
    {
        if (fsid == "@aod") fsid = Db.Variants[Variant].Aod;
        return Db.Formsets.TryGetValue(fsid, out var f) ? (fsid, f.Root) : null;
    }

    public (string Fsid, long Fid)? ResolveLink(BiosItem it)
    {
        if (it.Link is not { } link) return null;
        if (link.A == "@ext")
        {
            var name = (link.B ?? "").ToLowerInvariant();
            if (name == "amd cbs") return FormRoot(Db.Variants[Variant].Cbs);
            if (name == "amd pbs") return FormRoot("pbs");
            if (name.StartsWith("nvme", StringComparison.Ordinal)) return FormRoot("nvme");
            return null;
        }
        if (link.B is null) return FormRoot(link.A);
        return long.TryParse(link.B, CultureInfo.InvariantCulture, out var fid) && Db.Formsets.TryGetValue(link.A, out var fs) && fs.Forms.ContainsKey(fid)
            ? (link.A, fid) : null;
    }

    public BiosForm Form(string fsid, long fid) => Db.Formsets[fsid].Forms[fid];

    /// <summary>A page's items, with what the firmware adds at runtime (dynamic Advanced pages, boot option rows).</summary>
    public List<BiosItem> PageItems(string fsid, long fid)
    {
        var items = Form(fsid, fid).Items.ToList();
        if (fsid == "setup" && fid == 10005)
        {
            var extra = AdvancedDynamic.Select(d => new BiosItem { T = "ref", P = d.Title, Id = "dyn:" + d.Fsid, Link = (d.Fsid, null) }).ToList();
            foreach (var key in (Platform["third_party_pages"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? [])
                if (Db.Formsets.TryGetValue(key, out var f))
                    extra.Add(new BiosItem { T = "ref", P = ThirdParty.GetValueOrDefault(key, f.Title), Id = "dyn:" + key, Link = (key, null) });
            var pos = items.FindIndex(x => x.P == "AMD PBS");
            items.InsertRange(pos < 0 ? items.Count : pos + 1, extra);
        }
        if (fsid == "setup" && fid == 10008)
        {
            var output = new List<BiosItem>();
            foreach (var x in items)
            {
                if (x.P == "Boot Option #%d")
                    for (var n = 0; n < BootOrder.Count; n++)
                        output.Add(new BiosItem { T = "bootopt", P = $"Boot Option #{n + 1}", H = "Sets the system boot order", Id = $"boot:{n}", Slot = n });
                else if (x.P == "Boot Override")
                {
                    output.Add(x);
                    var names = BootNames();
                    for (var n = 0; n < names.Count; n++)
                        output.Add(new BiosItem { T = "bootnow", P = names[n], H = "Boot from this device now.", Id = $"bootnow:{n}" });
                }
                else output.Add(x);
            }
            items = output;
        }
        return items;
    }

    public Dictionary<(string, long), List<string>> Paths { get; } = [];
    public Dictionary<string, (string Fsid, long Fid, BiosItem Item)> Items { get; } = new(StringComparer.Ordinal);
    public HashSet<(string, long)> HiddenPages { get; } = [];
    public HashSet<string> InternalIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BoardHidden { get; } = new(StringComparer.Ordinal);

    /// <summary>A breadcrumb for every reachable page and an index of every item (search, favorites, change lists).</summary>
    private void IndexItems()
    {
        Paths.Clear(); Items.Clear(); HiddenPages.Clear(); InternalIds.Clear(); BoardHidden.Clear(); _qidx.Clear();
        var seen = new HashSet<(string, long)>();
        var queue = new List<((string, long) Page, List<string> Crumbs, bool Hidden)>();
        foreach (var (name, fsid, fid) in Tabs)
        {
            if (fsid == "fav") continue;
            var root = fid is { } f ? (fsid, f) : FormRoot(fsid);
            if (root is { } r) queue.Add((r, [name], false));
        }
        // Visible links first, so a page reachable both ways counts as visible.
        while (queue.Count > 0)
        {
            var i = queue.FindIndex(q => !q.Hidden);
            if (i < 0) i = 0;
            var (page, crumbs, hid) = queue[i];
            queue.RemoveAt(i);
            if (!seen.Add(page)) continue;
            Paths[page] = crumbs;
            if (hid) HiddenPages.Add(page);
            foreach (var it in PageItems(page.Item1, page.Item2))
            {
                Items.TryAdd(it.Id, (page.Item1, page.Item2, it));
                if (it.T == "ref" && ResolveLink(it) is { } tgt && !seen.Contains(tgt))
                {
                    var title = it.Prompt.Length > 0 ? it.Prompt : Form(tgt.Fsid, tgt.Fid).Title;
                    queue.Add((tgt, [.. crumbs, title], hid || StateOf(page.Item1, it) == BiosState.Hide));
                }
            }
        }
        // ASUS typed fields are stored twice: the text field plus an always-hidden raw number twin. Never show the twins.
        foreach (var fs in Db.Formsets.Values)
            foreach (var f in fs.Forms.Values)
                for (var i = 1; i < f.Items.Count; i++)
                    if (f.Items[i].T == "num" && f.Items[i - 1].T == "string" && f.Items[i - 1].Prompt == f.Items[i].Prompt)
                        InternalIds.Add(f.Items[i].Id);
        // Settings for hardware this board doesn't have (platform "hide_items").
        var rules = (Platform["hide_items"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        foreach (var (id, (fsid, fid, it)) in Items)
        {
            var title = Form(fsid, fid).Title;
            foreach (var r in rules)
                if ((r["page"]?.GetValue<string>() ?? title) == title && it.Prompt == r["prompt"]?.GetValue<string>())
                    BoardHidden[id] = r["reason"]?.GetValue<string>() ?? "Not used on this board";
        }
        if (_pendingPlan.Count > 0) ApplyPlan();
    }

    private readonly Dictionary<string, Dictionary<long, BiosItem>> _qidx = [];

    /// <summary>The setting a rule refers to (by question id), or null for internal values.</summary>
    private BiosItem? QItem(string fsid, long q)
    {
        if (!_qidx.TryGetValue(fsid, out var d))
        {
            _qidx[fsid] = d = [];
            foreach (var f in Db.Formsets[fsid].Forms.Values)
                foreach (var it in f.Items)
                    if (it.Q is { } qq && it.Prompt.Length > 0 && it.IsSetting) d.TryAdd(qq, it);
        }
        if (!d.TryGetValue(q, out var item)) return null;
        // An internal value the BIOS always hides.
        if (item.Rules.Any(r => r.Rpn.Count == 1 && r.Rpn[0]![0]!.GetValue<string>() == "true")) return null;
        return item;
    }

    /// <summary>Plain-language reasons an item is hidden or locked, or what controls it.</summary>
    public List<string> VisibilityNotes(string fsid, long? fid, BiosItem it)
    {
        var notes = new List<string>();
        if (BoardHidden.TryGetValue(it.Id, out var why)) notes.Add("Not used on this board: " + why);
        if (fid is { } f && HiddenPages.Contains((fsid, f))) notes.Add("This whole page is hidden by the BIOS on this system.");
        foreach (var r in it.Rules)
        {
            var active = EvalRpn(fsid, r.Rpn);
            var ops = r.Rpn.Select(t => t![0]!.GetValue<string>()).ToList();
            if (ops.All(o => o is "true" or "const" or "eq" or "false" or "not"))
            {
                if (active)
                    notes.Add(r.Kind == "hide" ? "ASUS has permanently turned this item off in this BIOS version." : "Always locked (grayed out) in this BIOS version.");
                continue;
            }
            var deps = new List<string>();
            var internalDep = false;
            foreach (var t in r.Rpn.Select(n => n!.AsArray()))
                if (t[0]!.GetValue<string>() is "eqv" or "eqlist" or "qref" or "eqid")
                {
                    var q = QItem(fsid, t[1]!.GetValue<long>());
                    if (q is null) internalDep = true;
                    else if ($"{q.Prompt} (now: {Display(q)})" is var d && !deps.Contains(d)) deps.Add(d);
                }
            var what = new List<string>();
            if (deps.Count > 0) what.Add("the setting " + string.Join(", ", deps));
            if (internalDep) what.Add("what the BIOS detects about your hardware");
            if (what.Count == 0 || (!active && deps.Count == 0)) continue;
            var src = string.Join(" and ", what);
            notes.Add(r.Kind == "hide"
                ? (active ? "Hidden right now because of " : "Only shown depending on ") + src + "."
                : (active ? "Locked right now because of " : "Can be locked depending on ") + src + ".");
        }
        return notes;
    }

    public bool ItemHidden(string fsid, long fid, BiosItem it) => HiddenPages.Contains((fsid, fid)) || StateOf(fsid, it) == BiosState.Hide;

    public List<string> PathOf(string fsid, long fid) => Paths.TryGetValue((fsid, fid), out var p) ? p : [Form(fsid, fid).Title];

    public List<(string Fsid, long Fid, BiosItem Item)> Search(string text, int limit = 300)
    {
        text = text.Trim();
        var res = new List<(string, long, BiosItem)>();
        if (text.Length == 0) return res;
        foreach (var (id, (fsid, fid, it)) in Items)
        {
            if (it.T == "sub" || it.Prompt.Length == 0) continue;
            if (!it.P.Contains(text, StringComparison.OrdinalIgnoreCase) && !it.H.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            if ((ItemHidden(fsid, fid, it) && !ShowHidden) || InternalIds.Contains(id)) continue;
            res.Add((fsid, fid, it));
            if (res.Count >= limit) break;
        }
        return res;
    }

    // ── changes / plan ──────────────────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<string> PendingIds() =>
        Items.Where(x => x.Value.Item.IsSetting && !Equals(Get(x.Value.Item, BiosSource.Work), Get(x.Value.Item, BiosSource.Saved))).Select(x => x.Key);

    /// <summary>Settings whose value on screen differs from the machine's (the plan).</summary>
    public List<BiosChange> PendingChanges() =>
        Items.Values.Where(x => x.Item.IsSetting && !Equals(Get(x.Item, BiosSource.Work), Get(x.Item, BiosSource.Saved)) && !ItemHidden(x.Fsid, x.Fid, x.Item))
            .Select(x => new BiosChange(x.Fsid, x.Fid, x.Item, Display(x.Item, BiosSource.Saved), Display(x.Item, BiosSource.Work))).ToList();

    /// <summary>Settings whose value differs from the factory default.</summary>
    public List<BiosChange> NonDefault(BiosSource src = BiosSource.Saved) =>
        Items.Values.Where(x => x.Item.IsSetting && !Equals(Get(x.Item, src), Get(x.Item, BiosSource.Default)) && !ItemHidden(x.Fsid, x.Fid, x.Item))
            .Select(x => new BiosChange(x.Fsid, x.Fid, x.Item, Display(x.Item, BiosSource.Default), Display(x.Item, src))).ToList();

    public bool HasPending() => _work.Any(kv => !kv.Value.AsSpan().SequenceEqual(_saved[kv.Key])) ||
        _workVol.Count != _savedVol.Count || _workVol.Any(kv => !_savedVol.TryGetValue(kv.Key, out var v) || !Equals(v, kv.Value));

    /// <summary>Keeps the edits on screen as the plan (and in the plan's history). Nothing is written to the firmware.</summary>
    public List<BiosChange> SavePlan()
    {
        var changes = PendingChanges();
        PlannedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        WritePlan();
        var line = new JsonObject
        {
            ["planned_at"] = PlannedAt,
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode)new JsonObject
            {
                ["path"] = string.Join(" > ", PathOf(c.Fsid, c.Fid)), ["item"] = c.Item.Prompt, ["machine"] = c.From, ["planned"] = c.To,
            }).ToArray()),
        };
        File.AppendAllText(Path.Combine(StateDir, "history.jsonl"), line.ToJsonString() + "\n");
        return changes;
    }

    /// <summary>Back to the machine's values (the plan is emptied when saved next, or now with <paramref name="persist"/>).</summary>
    public void Discard(bool persist = false)
    {
        _work = _saved.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
        _workVol = new(_savedVol, StringComparer.Ordinal);
        if (persist) WritePlan();
    }

    public void LoadDefaults()
    {
        _work = _defaults.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
        _workVol = new(StringComparer.Ordinal);
    }

    /// <summary>Favorites, boot order and passwords, without saving the edits on screen.</summary>
    public void PersistMeta() => WritePlan(edits: false);

    // ── profiles (ASUS User Profile, slots 1–8) ─────────────────────────────────────────────────────────────────────

    private string ProfilePath(int n) => Path.Combine(StateDir, "profiles", $"profile{n}.json");

    public (string Name, string? SavedAt)? ProfileInfo(int n)
    {
        var p = ProfilePath(n);
        if (!File.Exists(p)) return null;
        try
        {
            var d = JsonNode.Parse(File.ReadAllText(p))!;
            return (d["name"]?.GetValue<string>() ?? "", d["saved_at"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return null; }
    }

    public void SaveProfile(int n, string name)
    {
        var stores = new JsonObject();
        foreach (var (k, v) in _work) stores[k] = Convert.ToHexString(v).ToLowerInvariant();
        var d = new JsonObject
        {
            ["name"] = name, ["saved_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["bios_version"] = Db.BiosVersion, ["stores"] = stores,
        };
        File.WriteAllText(ProfilePath(n), d.ToJsonString());
    }

    public string LoadProfile(int n)
    {
        var d = JsonNode.Parse(File.ReadAllText(ProfilePath(n)))!;
        foreach (var (k, node) in d["stores"]!.AsObject())
            if (_work.TryGetValue(k, out var cur) && node?.GetValue<string>() is { } hex && hex.Length == 2 * cur.Length)
                _work[k] = Convert.FromHexString(hex);
        return d["name"]?.GetValue<string>() ?? "";
    }

    /// <summary>A readable report of every setting that differs from factory defaults.</summary>
    public int ExportReport(string path)
    {
        var rows = NonDefault(BiosSource.Saved);
        using var w = new StreamWriter(path);
        w.WriteLine($"{Db.Board} BIOS {Db.BiosVersion} - settings changed from factory defaults");
        w.WriteLine($"Read from this machine: {ReadAt:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine();
        foreach (var c in rows)
            w.WriteLine($"{string.Join(" > ", PathOf(c.Fsid, c.Fid))} > {c.Item.Prompt}\n    {c.To}  (default: {c.From})");
        if (rows.Count == 0) w.WriteLine("All settings are at factory defaults.");
        return rows.Count;
    }
}
