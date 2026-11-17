using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenHW.App.Bios;
using OpenHW.Hardware;
using Xunit;

namespace OpenHW.App.Tests;

/// <summary>
/// The BIOS module (File → BIOS Setup…), ported from the standalone emulator's tools/smoke_test.py, plus what OpenHW
/// adds: this machine's values from its UEFI variables, and the plan instead of an emulated NVRAM.
/// </summary>
public sealed class BiosSetupTests : IDisposable
{
    private static readonly Lazy<BiosData> Db = new(() => BiosData.Load(BiosData.Latest()!));
    private readonly string _state = Directory.CreateTempSubdirectory("openhw-bios-").FullName;
    public void Dispose() => Directory.Delete(_state, recursive: true);

    private static JsonObject Platform() => BiosModel.LoadPlatform(Path.Combine(Path.GetTempPath(), "openhw-no-such-dir"));

    /// <summary>A machine whose variables are the factory defaults with some bytes changed; others missing.</summary>
    private sealed class FakeMachine(BiosData db) : IFirmwareVariables
    {
        public readonly Dictionary<string, byte[]> Vars = db.Varstores.Values
            .Where(v => v.Name is "Setup" && v.Size > 100 || v.Name is "VARSTORE_OCMR_SETTINGS_NAME" or "AmdSetupSHP" or "AodSetupShp" or "AutoDetectData")
            .ToDictionary(v => v.Name, v => (byte[])v.Defaults.Clone());
        public byte[]? Read(string name, string guid) => Vars.TryGetValue(name, out var b) ? (byte[])b.Clone() : null;
        public IReadOnlyList<string> BootEntries() => ["Ubuntu (P2: WD_BLACK SN850X 8000GB)", "UEFI OS"];
    }

    private static BiosItem Find(BiosModel m, string fsid, string prompt) =>
        m.Items.Values.First(x => x.Fsid == fsid && x.Item.Prompt == prompt && x.Item.IsSetting).Item;

    private static void SetMachine(FakeMachine fake, BiosModel m, BiosItem it, long value)
    {
        var name = m.Db.Varstores[it.Vs!].Name;
        var buf = fake.Vars[name];
        for (var i = 0; i < it.Sz; i++) buf[it.O + i] = (byte)(value >> (8 * i));
    }

    [Fact]
    public void ReadsThisMachinesValuesAndListsWhatDiffersFromDefaults()
    {
        var fake = new FakeMachine(Db.Value);
        var probe = new BiosModel(Db.Value, null, _state, Platform());
        var tuner = Find(probe, "setup", "Ai Overclock Tuner");
        SetMachine(fake, probe, tuner, 7); // EXPO
        // A typed field the running firmware filled with the word "Auto": the same as the factory default's empty field.
        var typed = probe.Items.Values.First(x => x.Item.T == "string" && x.Item.Vs?.StartsWith("VARSTORE_OCMR", StringComparison.Ordinal) == true).Item;
        var auto = System.Text.Encoding.Unicode.GetBytes("Auto");
        auto.CopyTo(fake.Vars["VARSTORE_OCMR_SETTINGS_NAME"], typed.O);

        var m = new BiosModel(Db.Value, fake, _state, Platform());
        Assert.Contains(m.FromMachine, k => k.StartsWith("Setup:", StringComparison.Ordinal));
        Assert.Contains("AMD_PBS_SETUP", m.NotFromMachine); // missing: factory defaults
        Assert.Equal("EXPO", m.Display(tuner, BiosSource.Saved));
        var nd = Assert.Single(m.NonDefault());
        Assert.Equal(("Auto", "EXPO"), (nd.From, nd.To));
        Assert.Equal("", m.Get(typed, BiosSource.Saved));
        Assert.Empty(m.PendingChanges());
    }

    [Fact]
    public void PlanSurvivesAReopenAndDropsOffOnceTheMachineHasIt()
    {
        var fake = new FakeMachine(Db.Value);
        var m = new BiosModel(Db.Value, fake, _state, Platform());
        var smt = Find(m, "cbs_shp", "SMT Control");
        Assert.Equal("Auto", m.Display(smt));
        m.Set(smt, m.Options("cbs_shp", smt).First(o => o.Label == "Disable").Value);
        var change = Assert.Single(m.PendingChanges());
        Assert.Equal(("Auto", "Disable"), (change.From, change.To));
        m.SavePlan();
        Assert.Contains("\"planned\":\"Disable\"", File.ReadAllText(Path.Combine(_state, "history.jsonl")));

        var reopened = new BiosModel(Db.Value, fake, _state, Platform());
        Assert.Equal("Disable", reopened.Display(Find(reopened, "cbs_shp", "SMT Control")));
        Assert.Equal("Auto", reopened.Display(Find(reopened, "cbs_shp", "SMT Control"), BiosSource.Saved));
        Assert.Single(reopened.PendingChanges());

        // Entered in the real BIOS and rebooted: the machine now has it, so it isn't a change any more.
        SetMachine(fake, m, smt, (long)m.Get(smt));
        var after = new BiosModel(Db.Value, fake, _state, Platform());
        Assert.Empty(after.PendingChanges());
        Assert.Equal("Disable", after.Display(Find(after, "cbs_shp", "SMT Control"), BiosSource.Saved));

        // Favorites are kept without saving the edits on screen into the plan.
        var tuner = Find(after, "setup", "Ai Overclock Tuner");
        after.Set(tuner, 1L);
        after.Favorites.Add(tuner.Id);
        after.PersistMeta();
        var again = new BiosModel(Db.Value, fake, _state, Platform());
        Assert.Contains(tuner.Id, again.Favorites);
        Assert.Empty(again.PendingChanges());
    }

    [Fact]
    public void RulesProfilesSearchAndVariantBehaveAsInTheEmulator()
    {
        var m = new BiosModel(Db.Value, null, _state, Platform());
        Assert.Equal(371, m.Paths.Count);
        Assert.Equal(("cbs_shp", m.Db.Formsets["cbs_shp"].Root), m.ResolveLink(new BiosItem { T = "ref", Link = ("@ext", "AMD CBS") }));
        // Ai Overclock Tuner = Manual reveals BCLK1 Frequency.
        var tuner = Find(m, "setup", "Ai Overclock Tuner");
        var bclk = m.Form("setup", 10004).Items.First(i => i.Prompt == "BCLK1 Frequency");
        Assert.Equal(BiosState.Hide, m.StateOf("setup", bclk));
        m.Set(tuner, 1L);
        Assert.NotEqual(BiosState.Hide, m.StateOf("setup", bclk));
        Assert.Contains(m.VisibilityNotes("setup", 10004, bclk), n => n.Contains("Ai Overclock Tuner", StringComparison.Ordinal));
        m.Discard();
        // Profiles.
        var smt = Find(m, "cbs_shp", "SMT Control");
        m.Set(smt, m.Options("cbs_shp", smt).First(o => o.Label == "Disable").Value);
        m.SaveProfile(3, "Test");
        Assert.Equal("Test", m.ProfileInfo(3)!.Value.Name);
        m.LoadDefaults();
        m.LoadProfile(3);
        Assert.Equal("Disable", m.Display(smt));
        m.Discard();
        // Search.
        Assert.NotEmpty(m.Search("SR-IOV"));
        // Help: allowed values and checking typed input.
        var help = new BiosHelp(m);
        var details = help.Details("cbs_shp", null, smt);
        Assert.Contains(details.Choices, c => c.Label == "Disable");
        var number = m.Items.Values.First(x => x.Item.T == "num" && x.Item.Max > 10 && x.Item.Step <= 1 && !m.InternalIds.Contains(x.Item.Id)).Item;
        Assert.False(help.Validate(number, (number.Max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Ok);
        Assert.True(help.Validate(number, number.Min.ToString(System.Globalization.CultureInfo.InvariantCulture)).Ok);
        // 7000WX variant.
        var stp = Platform();
        stp["variant"] = "STP";
        var m7 = new BiosModel(Db.Value, null, Directory.CreateTempSubdirectory("openhw-bios7-").FullName, stp);
        Assert.Equal("cbs_stp", m7.ResolveLink(new BiosItem { T = "ref", Link = ("@ext", "AMD CBS") })!.Value.Fsid);
    }

    [AvaloniaFact]
    public void EveryPageRendersAndTheScreenNavigates()
    {
        var fake = new FakeMachine(Db.Value);
        var platform = BiosPlatform.Probe(Platform(), fake);
        Assert.Equal("probed", platform["source"]!.GetValue<string>());
        var m = new BiosModel(Db.Value, fake, _state, platform);
        var w = new BiosWindow(m, id => id == "bmc:+12V" ? 11.82 : null) { Width = 1366, Height = 860 };
        w.Show();
        var errors = new List<string>();
        foreach (var ((fsid, fid), crumbs) in m.Paths.ToList())
        {
            try { w.GoTo(fsid, fid, null); Dispatcher.UIThread.RunJobs(); }
            catch (Exception ex) { errors.Add($"{string.Join(">", crumbs)}: {ex.Message}"); }
        }
        Assert.Empty(errors);

        // Advanced > AMD CBS > CPU Common Options > Thread Enablement, change SMT, Esc back.
        w.GoTo("setup", 10005, null);
        void Open(string prompt)
        {
            var row = w.Rows.First(r => r.Item.Prompt == prompt);
            var target = m.ResolveLink(row.Item)!.Value;
            w.OpenPage(target.Fsid, target.Fid);
        }
        Open("AMD CBS");
        Open("CPU Common Options");
        Open("Thread Enablement");
        Assert.Contains(w.Rows, r => r.Item.Prompt == "SMT Control");
        var captures = Environment.GetEnvironmentVariable("OPENHW_CAPTURES") ?? Path.GetTempPath();
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-bios-cbs.png"));
        w.GoBack(); w.GoBack(); w.GoBack();
        Assert.Contains(w.Rows, r => r.Item.Prompt == "AMD CBS");

        w.GoTo("setup", 10003, null);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-bios-main.png"));
        w.GoTo("setup", 10004, null);
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame()?.Save(Path.Combine(captures, "openhw-bios-aitweaker.png"));
        w.GoTo("setup", 10008, null);
        Assert.Contains(w.Rows, r => r.Item.T == "bootopt" && r.Value == "Ubuntu (P2: WD_BLACK SN850X 8000GB)");
    }
}
