using Avalonia;
using Avalonia.Controls;

namespace OpenHW.App.Settings;

/// <summary>
/// Sensor Settings → Main Settings: HWiNFO's "Settings" dialog (reference/settings_menu/Main_Settings_*.jpg).
/// <b>Scaffolding</b> like <see cref="SensorSettingsWindow"/>: the controls work, nothing is saved, OK and Cancel
/// close. License Management is grayed (OpenHW has no license to manage).
/// </summary>
public sealed class MainSettingsWindow : ClassicDialog
{
    public static readonly string[] Tabs = ["General / User Interface", "Safety", "SMBus / I2C", "Driver Management", "License Management"];

    public MainSettingsWindow()
        : base("OpenHW - Settings", new Size(813, 453), new Rect(4, 29.6, 800.8, 383.2), new Point(522.4, 423.2))
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AddTab("General / User Interface", General());
        AddTab("Safety", Safety());
        AddTab("SMBus / I2C", SmbusI2c());
        AddTab("Driver Management", DriverManagement());
        AddTab("License Management", null);
    }

    // Main_Settings_General.jpg — page frame at (7, 81).
    private static ClassicPage General()
    {
        var p = new ClassicPage(7, 81);
        (string Text, bool On)[] left =
        [
            ("Show System Summary on Startup", false), ("Show Sensors on Startup", true), ("Minimize Main Window on Startup", true),
            ("Minimize Sensors on Startup", true), ("Minimize Sensors instead of closing", true), ("Show Welcome Screen and Progress", false),
            ("Validate Window Positions", true), ("Auto Start", true), ("Automatic Update", true), ("Flush Buffers on Start", true),
            ("Snapshot CPU Polling", false), ("Shared Memory Support", true),
        ];
        for (var i = 0; i < left.Length; i++) p.Check(left[i].Text, 26, 109 + 30 * i, left[i].On);
        p.Label("[12-HOUR LIMIT]", 52, 460, bold: true);
        p.Label("Language:", 24, 489);
        p.Combo(["English"], 0, 24, 507, 329, 534);

        (string Text, bool On)[] right =
        [
            ("Wake disabled GPUs", true), ("Wake disabled GPUs (Extended)", false), ("Poll Sleeping GPUs", false),
            ("Reorder GPUs", true), ("Prefer AMD ADL", false), ("PresentMon Support", true),
        ];
        for (var i = 0; i < right.Length; i++) p.Check(right[i].Text, 491, 109 + 30 * i, right[i].On);
        p.Check("Remember Preferences", 491, 294, true);

        p.Button("Backup User Settings", 490, 326, 649, 397);
        p.Button("Reset Preferences", 490, 404, 649, 475);
        p.Button("Check for Updates", 490, 481, 649, 552);

        p.Group("Color Mode", 719, 322, 961, 441, 729);
        p.Radio("Default (Grey)", "color-mode", 734, 352);
        p.Radio("Default (Black)", "color-mode", 734, 382, isChecked: true);
        p.Radio("Disabled (Light)", "color-mode", 734, 412);
        p.Label("Theme:", 729, 467);
        p.Combo(["None"], 0, 728, 487, 963, 514);
        return p;
    }

    // Main_Settings_Safety.jpg — page frame at (8, 79).
    private static ClassicPage Safety()
    {
        var p = new ClassicPage(8, 79);
        p.Group("IDE/(S)ATA Drive Scan", 25, 107, 335, 326, 35);
        p.Radio("Safe Mode", "drive-scan", 40, 137, isChecked: true);
        p.Radio("Low-level IO Access", "drive-scan", 40, 170);
        p.Radio("Disable Drive Scan", "drive-scan", 40, 202);
        p.Check("CSMI SAS Support", 40, 242, true);
        p.Check("S.M.A.R.T. Support", 40, 272, true);
        p.Check("ATA Statistics Support", 40, 302, true);

        p.Group("CPU Clock Measurement", 25, 347, 335, 501, 35);
        p.Check("Bus Clock-based", 40, 377, true);
        p.Check("Periodic polling", 67, 422, true);
        p.Check("Use HPET", 40, 467, true);

        (string Text, double Y, bool On)[] middle =
        [
            ("Low-level PCI Access", 122, false), ("TPM Check", 152, false), ("EC Support", 182, true),
            ("Evaluate ACPI Methods", 212, false), ("Enable PCH Thermal Device", 242, false),
            ("Safe scan of potentially problematic PCI devices", 272, false),
            ("CorsairLink and Asetek Support", 332, true), ("SW SMI", 362, true), ("Audio Codec Check", 392, true),
            ("ACPI AML Enumeration", 422, false), ("Intel ME Support", 452, true), ("Intel SVID Support", 482, true),
        ];
        foreach (var (text, y, on) in middle) p.Check(text, 351, y, on);

        (string Text, double Y, bool On)[] right =
        [
            ("Enable NVML", 122, true), ("oneAPI Support", 152, true), ("USB Type-C PD Support", 182, true),
            ("Debug Mode", 452, false), ("Debug Write Direct", 482, true),
        ];
        foreach (var (text, y, on) in right) p.Check(text, 675, y, on);
        return p;
    }

    // Main_Settings_SMBus_I2C.jpg — page frame at (12, 85).
    private static ClassicPage SmbusI2c()
    {
        var p = new ClassicPage(12, 85);
        p.Check("SMBus Support", 44, 113, true);
        p.Group("SMBus Device Exclusion", 30, 158, 449, 390, 40);
        // 8 rows (0x–7x) × 16 columns (x0–xF) of 7-bit addresses; HWiNFO excludes 0x00–0x0F by default.
        for (var column = 0; column < 16; column++)
            p.Label($"x{column:X}", 67 + 22.5 * column, 192);
        for (var row = 0; row < 8; row++)
        {
            var cy = 213 + 22.7 * row;
            p.Label($"{row}x", 42, cy);
            for (var column = 0; column < 16; column++) p.Check("", 68 + 22.5 * column, cy, row == 0);
        }

        p.Check("GPU I2C Support", 489, 113, true);
        p.Group("GPU I2C Bus Exclusion", 475, 161, 689, 255, 484);
        for (var bus = 0; bus < 8; bus++)
        {
            p.Label($"{bus}", 494 + 23 * bus, 192);
            p.Check("", 489 + 23 * bus, 213);
        }

        p.Check("GPU I2C via NVAPI", 489, 281, true);
        p.Check("GPU I2C via ADL", 489, 311);
        p.Check("GPU I2C Caching", 489, 368, true);
        p.Button("Reset GPU I2C Cache", 489, 390, 745, 446);
        p.Check("Force GPU I2C Support", 489, 473);
        return p;
    }

    // Main_Settings_Driver_Management.jpg — page frame at (9, 86).
    private static ClassicPage DriverManagement()
    {
        var p = new ClassicPage(9, 86);
        p.Label("Current Status:", 39, 127);
        p.Readout("Installed", 215, 112, 349, 141);
        p.Button("Install", 40, 181, 172, 222);
        p.Button("Remove", 216, 181, 348, 222);
        p.Check("Persistent Driver", 40, 279);
        return p;
    }
}
