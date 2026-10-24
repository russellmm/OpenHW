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
    public const int MaxTrayIcons = 8;
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
    public List<TraySensor> TrayIcons { get; set; } = [new()];

    /// <summary>Tray tiles show a short unit after the digits ("32°", "55W", "87%"); off = digits only, as before 0.8.0.</summary>
    public bool TrayUnits { get; set; } = true;

    private static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
        ? config
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    private static string FilePath => Path.Combine(ConfigHome, "openhw", "settings.json");

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
        // One icon per sensor: a file from before duplicates were blocked keeps the first of each.
        TrayIcons = TrayIcons.Where(t => !string.IsNullOrWhiteSpace(t.SensorId)).DistinctBy(t => t.SensorId).Take(MaxTrayIcons).ToList();
        foreach (var icon in TrayIcons)
            if (!IsColor(icon.Colour)) icon.Colour = Colours[0].Hex;
        CustomColours = CustomColours.Where(IsColor).Select(h => h.ToUpperInvariant()).Where(h => PresetName(h) is null)
            .Distinct().TakeLast(MaxCustomColours).ToList();
        StartAtLogin = File.Exists(AutostartPath); // the file is the truth: the user may have removed it elsewhere
    }

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
    }

    private void WriteAutostart()
    {
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
