using System.Globalization;

namespace OpenHW.App.Settings;

/// <summary>
/// Settings → Window scale. Automatic (0) leaves it to Avalonia, which on X11 follows the desktop's Xft.dpi — on
/// GNOME that is the display scale × the text scaling factor. A fixed percentage replaces it for every OpenHW window.
/// Avalonia reads its scale only when the platform starts, so this is applied in Program.Main, before that.
/// </summary>
public static class WindowScale
{
    public static readonly int[] Choices = [0, 100, 125, 150, 175, 200, 225, 250, 275, 300, 350, 400];

    private const string Global = "AVALONIA_GLOBAL_SCALE_FACTOR";
    private const string Screens = "AVALONIA_SCREEN_SCALE_FACTORS";

    // Avalonia's own per-screen setting, naming no real monitor: every screen then counts as 1×, and the global
    // factor (which otherwise multiplies Xft.dpi) is the whole scale. Its value also marks the variables as OpenHW's.
    private const string NoScreens = "openhw-fixed-scale=1";

    /// <summary>The fixed percentage this process runs at, or null when the desktop's scale is used.</summary>
    public static int? Fixed { get; private set; }

    /// <summary>
    /// Sets (fixed scale) or clears (automatic) the environment Avalonia reads. Clearing only removes variables
    /// OpenHW set — a restart inherits them — so scale variables the user exported themselves keep working.
    /// </summary>
    public static void Apply(int percent)
    {
        if (percent > 0)
        {
            Environment.SetEnvironmentVariable(Screens, NoScreens);
            Environment.SetEnvironmentVariable(Global, (percent / 100.0).ToString(CultureInfo.InvariantCulture));
            Fixed = percent;
            return;
        }
        if (Environment.GetEnvironmentVariable(Screens) == NoScreens)
        {
            Environment.SetEnvironmentVariable(Screens, null);
            Environment.SetEnvironmentVariable(Global, null);
        }
        Fixed = null;
    }

    public static string Label(int percent) => percent > 0 ? $"{percent} %" : "Automatic";
}
