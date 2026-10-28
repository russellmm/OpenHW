using System.Text.Json;

namespace OpenHW.App.Settings;

/// <summary>A sensor shown as its own tray icon: the value drawn on a tile of <see cref="Colour"/>.</summary>
public sealed class TraySensor
{
    /// <summary>A sensor id, or <see cref="AppSettings.CpuTemperature"/> for "this machine's CPU temperature".</summary>
    public string SensorId { get; set; } = AppSettings.CpuTemperature;
    public string Colour { get; set; } = "#C0392B";
}

/// <summary>
/// User preferences, kept in ~/.config/openhw/settings.json. A missing or unreadable file means the defaults, which
/// match OpenHW before settings existed: 2 s polls and one CPU-temperature tray icon on a red tile.
/// </summary>
public sealed class AppSettings
{
    public const string CpuTemperature = "auto:cpu-temperature";
    public static readonly double[] PollChoices = [1, 2, 3, 5, 10];

    /// <summary>Tile colours offered for tray icons: distinct on a dark panel, each readable with white or black text.</summary>
    public static readonly (string Name, string Hex)[] Colours =
    [
        ("Red", "#C0392B"), ("Orange", "#D35400"), ("Amber", "#F0B429"), ("Green", "#27AE60"), ("Teal", "#16A085"),
        ("Blue", "#2E86DE"), ("Purple", "#8E44AD"), ("Grey", "#5D6D7E"), ("White", "#ECF0F1"),
    ];

    /// <summary>A "#RRGGBB" tile color: one of <see cref="Colours"/> or a custom one picked in Settings.</summary>
    public static bool IsColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit);

    /// <summary>The preset's name, or null for a custom color.</summary>
    public static string? PresetName(string hex) =>
        Colours.FirstOrDefault(c => c.Hex.Equals(hex, StringComparison.OrdinalIgnoreCase)).Name;

    public const int MaxCustomColours = 16;

    /// <summary>Colors the user added (picked or typed), "#RRGGBB", offered after the presets everywhere a color is chosen.</summary>
    public List<string> CustomColours { get; set; } = [];

    /// <summary>Every color to offer: the presets by name, then the custom colors labelled with their hex code.</summary>
    public IReadOnlyList<(string Label, string Hex)> AllColours() =>
        [.. Colours, .. CustomColours.Where(h => PresetName(h) is null).Select(h => (h, h))];

    /// <summary>Adds a color to the custom list (normalised to upper case) unless it's a preset or already there.</summary>
    public void AddCustomColour(string hex)
    {
        var color = hex.ToUpperInvariant();
        if (!IsColor(color) || PresetName(color) is not null || CustomColours.Contains(color)) return;
        CustomColours.Add(color);
        if (CustomColours.Count > MaxCustomColours) CustomColours.RemoveAt(0); // oldest goes first
    }

    public double PollSeconds { get; set; } = 2;
    public bool StartAtLogin { get; set; }
    public bool StartInTray { get; set; } = true;

    /// <summary>
    /// While the window is hidden or minimized, read only the pinned sensors (tray icons, alerts, logging, open
    /// graphs) — as if every group were collapsed. Off by default: HWiNFO keeps all statistics running.
    /// </summary>
    public bool PauseWhenHidden { get; set; }

    /// <summary>Draw windows without OpenGL (applies at the next start). Saves the GPU driver's memory, costs some CPU.</summary>
    public bool SoftwareRendering { get; set; }

    /// <summary>
    /// GPU Lending: list patterns of apps ("app-gnome-openfan-*.scope") that are closed before the card is lent and
    /// reopened afterwards (and again when it comes back), instead of blocking the lend until closed by hand.
    /// </summary>
    public List<string> LendReopen { get; set; } = [];

    /// <summary>GPU Lending: apps the owner answered "Leave it" for (they may use the lent card; no more questions).</summary>
    public List<string> LendKnownApps { get; set; } = [];

    /// <summary>Size of every OpenHW window in percent; 0 = Automatic (the desktop's scale). Applies at the next start.</summary>
    public int WindowScale { get; set; }
    public List<TraySensor> TrayIcons { get; set; } = [new()];

    /// <summary>Tray tiles show a short unit after the digits ("32°", "55W", "87%"); off = digits only, as before 0.8.0.</summary>
    public bool TrayUnits { get; set; } = true;

    // Sensor Settings → General (HWiNFO's names). Defaults are what OpenHW did before these were switches.

    /// <summary>Row Shading: alternate black/gray sensor rows under each group heading.</summary>
    public bool RowShading { get; set; } = true;

    /// <summary>Highlight changing values: a Current cell turns blue while it differs from the last sample.</summary>
    public bool HighlightChanges { get; set; } = true;

    /// <summary>Show ToolTips: a sensor row explains its reading on hover.</summary>
    public bool ShowToolTips { get; set; } = true;

    /// <summary>Always On Top: the sensor window stays above other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>Averaging Interval: Average = the last N samples; 0 = the whole run until reset.</summary>
    public int AverageSamples { get; set; }
    public const int MaxAverageSamples = 100_000;

    /// <summary>Show columns: Current, Minimum, Maximum, Average (the Sensor column is always shown).</summary>
    public bool ShowCurrent { get; set; } = true;
    public bool ShowMinimum { get; set; } = true;
    public bool ShowMaximum { get; set; } = true;
    public bool ShowAverage { get; set; } = true;

    /// <summary>Main Settings → Validate Window Positions: a window saved on a monitor that's gone opens on the primary.</summary>
    public bool ValidateWindowPositions { get; set; } = true;

    // Main Settings → Safety / SMBus: which of OpenHW's readers run (HWiNFO's Enable NVML, SMBus Support,
    // S.M.A.R.T. Support). Off = those sensors leave the list until turned back on.

    /// <summary>GPUs (NVML, through openhw-helper).</summary>
    public bool ReadNvidia { get; set; } = true;

    /// <summary>DIMM and board VR sensors on the SMBus (through openhw-helper).</summary>
    public bool ReadSmbus { get; set; } = true;

    /// <summary>NVMe S.M.A.R.T. health readings (through openhw-helper); drive temperatures from hwmon stay.</summary>
    public bool ReadSmart { get; set; } = true;

    private static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
        ? config
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>~/.config/openhw: settings and OpenHW's other per-user files.</summary>
    public static string ConfigDir => Path.Combine(ConfigHome, "openhw");

    private static string FilePath => Path.Combine(ConfigDir, "settings.json");

    /// <summary>Freedesktop autostart entry; GNOME starts it at login.</summary>
    public static string AutostartPath => Path.Combine(ConfigHome, "autostart", "openhw.desktop");

    public static AppSettings Load()
    {
        AppSettings settings;
        try { settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { settings = new AppSettings(); }
        settings.Normalise();
        return settings;
    }

    // A hand-edited or older file can hold anything: clamp to what the UI could have produced.
    private void Normalise()
    {
        if (!PollChoices.Contains(PollSeconds)) PollSeconds = 2;
        if (!Settings.WindowScale.Choices.Contains(WindowScale)) WindowScale = 0;
        AverageSamples = Math.Clamp(AverageSamples, 0, MaxAverageSamples);
        // One icon per sensor: a file from before duplicates were blocked keeps the first of each.
        TrayIcons = TrayIcons.Where(t => !string.IsNullOrWhiteSpace(t.SensorId)).DistinctBy(t => t.SensorId).ToList();
        foreach (var icon in TrayIcons)
            if (!IsColor(icon.Colour)) icon.Colour = Colours[0].Hex;
        CustomColours = CustomColours.Where(IsColor).Select(h => h.ToUpperInvariant()).Where(h => PresetName(h) is null)
            .Distinct().TakeLast(MaxCustomColours).ToList();
        StartAtLogin = AutostartEnabled(); // the file is the truth: the user may have removed or disabled it elsewhere
    }

    /// <summary>Points tray icons for a renumbered hwmon sensor at its current id; true (unsaved) if any moved.</summary>
    public bool Remap(Sensors.HwmonRenumbering renumbering)
    {
        var changed = false;
        foreach (var icon in TrayIcons)
        {
            var id = renumbering.Id(icon.SensorId);
            if (id == icon.SensorId) continue;
            icon.SensorId = id;
            changed = true;
        }
        if (changed) TrayIcons = TrayIcons.DistinctBy(t => t.SensorId).ToList();
        return changed;
    }

    /// <summary>
    /// Raised after every <see cref="Save"/> (UI thread), so each window showing a setting — File → Settings, Tray
    /// Icons, the gear's Sensor Settings — redraws when another one changes it.
    /// </summary>
    public event Action? Saved;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            WriteAutostart();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"openhw: could not save settings ({ex.Message})");
        }
        Saved?.Invoke();
    }

    /// <summary>
    /// The command of a login entry another tool wrote in OpenHW's place — e.g. RTXinVM's
    /// <c>rtxgpu --app openhw</c>, which starts OpenHW in its own slice — or null when the entry is OpenHW's own (a
    /// quoted launcher path, optionally <c>--tray</c>) or there is none. OpenHW leaves such an entry as it is.
    /// </summary>
    public static string? ForeignAutostart()
    {
        string[] lines;
        try { lines = File.ReadAllLines(AutostartPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var exec = lines.FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal))?[5..].Trim();
        if (exec is null) return null;
        return System.Text.RegularExpressions.Regex.IsMatch(exec, @"^""[^""]+""( --tray)?$") ? null : exec;
    }

    // GNOME skips an entry with Hidden=true or X-GNOME-Autostart-enabled=false.
    private static bool AutostartEnabled()
    {
        try
        {
            var lines = File.ReadAllLines(AutostartPath);
            return !lines.Contains("Hidden=true") && !lines.Contains("X-GNOME-Autostart-enabled=false");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void WriteAutostart()
    {
        // Another launcher's entry (e.g. RTXinVM's rtxgpu, which starts OpenHW in its GPU slice) is kept: turning
        // login start off disables it in place and turning it on re-enables it, so its command is never lost.
        if (ForeignAutostart() is not null)
        {
            if (AutostartEnabled() == StartAtLogin) return; // already as asked: leave the other tool's file untouched
            var lines = File.ReadAllLines(AutostartPath)
                .Where(l => !l.StartsWith("Hidden=", StringComparison.Ordinal) && !l.StartsWith("X-GNOME-Autostart-enabled=", StringComparison.Ordinal))
                .Append($"X-GNOME-Autostart-enabled={(StartAtLogin ? "true" : "false")}");
            if (!StartAtLogin) lines = lines.Append("Hidden=true");
            File.WriteAllLines(AutostartPath, lines);
            return;
        }
        if (!StartAtLogin)
        {
            if (File.Exists(AutostartPath)) File.Delete(AutostartPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartPath)!);
        var exe = LauncherPath();
        File.WriteAllText(AutostartPath, $"""
            [Desktop Entry]
            Type=Application
            Name=OpenHW
            Comment=Hardware monitor
            Exec="{exe}"{(StartInTray ? " --tray" : "")}
            Icon=openhw
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """);
    }

    // The installed launcher when there is one (the per-user install's ~/.local/bin/openhw, else the package's
    // /usr/bin/openhw), so autostart survives reinstalls; else this process.
    private static string LauncherPath()
    {
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "openhw");
        if (File.Exists(user)) return user;
        return File.Exists("/usr/bin/openhw") ? "/usr/bin/openhw" : Environment.ProcessPath ?? "openhw";
    }
}
