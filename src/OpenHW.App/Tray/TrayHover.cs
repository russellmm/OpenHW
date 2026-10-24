using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace OpenHW.App.Tray;

public enum TrayHoverState
{
    On,
    Off,
    /// <summary>Installed, but this GNOME session started before it was: Wayland only finds new extensions at login.</summary>
    NeedsLogin,
    /// <summary>Installed and switched on, but GNOME couldn't run it (see the error text).</summary>
    Failed,
    NotInstalled,
    /// <summary>No GNOME Shell on this session bus (another desktop, or none).</summary>
    NoShell,
}

/// <summary>A state, GNOME's error text for <see cref="TrayHoverState.Failed"/>, and for <see cref="TrayHoverState.NeedsLogin"/>
/// whether it will be on after the login.</summary>
public sealed record TrayHoverStatus(TrayHoverState State, string Error = "", bool OnAtLogin = false);

/// <summary>Turns hover tooltips for OpenHW's tray icons on and off. The UI talks to this, tests to a fake.</summary>
public interface ITrayHover
{
    Task<TrayHoverStatus> GetAsync();
    Task<TrayHoverStatus> SetAsync(bool on);
}

/// <summary>
/// GNOME's tray shows no tooltips, so hover comes from OpenHW's own Shell extension (packaging/gnome-extension, in
/// the .deb). Whether it's on is GNOME's own extension state — the same thing the Extensions app shows — read and set
/// through org.gnome.Shell.Extensions; OpenHW keeps no copy, so a change in the Extensions app is the truth too.
/// </summary>
public sealed class GnomeTrayHover : ITrayHover
{
    public const string Uuid = "openhw-tray-hover@russellmm.github.io";
    private const string Service = "org.gnome.Shell.Extensions";
    private const string ObjectPath = "/org/gnome/Shell/Extensions";

    // Where GNOME looks: the user's data dir, then the system's (the .deb installs there).
    private static string[] InstallDirs =>
    [
        Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "gnome-shell", "extensions", Uuid),
        Path.Combine("/usr/share/gnome-shell/extensions", Uuid),
    ];

    private Connection? _connection;

    public async Task<TrayHoverStatus> GetAsync()
    {
        Dictionary<string, VariantValue> info;
        try { info = await GetInfoAsync(); }
        catch (Exception ex) when (ex is DBusException or ConnectException or InvalidOperationException) { return new(TrayHoverState.NoShell); }

        if (info.Count == 0)
        {
            if (!InstallDirs.Any(Directory.Exists)) return new(TrayHoverState.NotInstalled);
            // Not loaded yet: what GNOME will do at the next login is whatever its enabled list says.
            return new(TrayHoverState.NeedsLogin, OnAtLogin: EnabledAtLogin());
        }
        var enabled = info.TryGetValue("enabled", out var e) && e.GetBool();
        var error = info.TryGetValue("error", out var text) ? text.GetString() : "";
        if (enabled && error.Length > 0) return new(TrayHoverState.Failed, error);
        return new(enabled ? TrayHoverState.On : TrayHoverState.Off);
    }

    public async Task<TrayHoverStatus> SetAsync(bool on)
    {
        var status = await GetAsync();
        switch (status.State)
        {
            case TrayHoverState.NeedsLogin:
                // GNOME refuses to enable an extension it hasn't loaded, so edit the list it reads at login instead
                // (exactly what EnableExtension/DisableExtension write).
                SetEnabledAtLogin(on);
                break;
            case TrayHoverState.On or TrayHoverState.Off or TrayHoverState.Failed:
                await CallAsync(on ? "EnableExtension" : "DisableExtension");
                break;
        }
        return await GetAsync();
    }

    private async Task<Connection> ConnectAsync()
    {
        if (_connection is not null) return _connection;
        var connection = new Connection(Address.Session ?? throw new InvalidOperationException("No session bus"));
        await connection.ConnectAsync();
        return _connection = connection;
    }

    private async Task<Dictionary<string, VariantValue>> GetInfoAsync()
    {
        var connection = await ConnectAsync();
        return await connection.CallMethodAsync(InfoMessage(connection),
            static (Message message, object? _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue());
    }

    // MessageWriter is a ref struct, so the calls are built synchronously here.
    private static MessageBuffer InfoMessage(Connection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Service, member: "GetExtensionInfo", signature: "s");
        writer.WriteString(Uuid);
        return writer.CreateMessage();
    }

    private async Task CallAsync(string member)
    {
        var connection = await ConnectAsync();
        await connection.CallMethodAsync(CallMessage(connection, member));
    }

    private static MessageBuffer CallMessage(Connection connection, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Service, member: member, signature: "s");
        writer.WriteString(Uuid);
        return writer.CreateMessage();
    }

    private static bool EnabledAtLogin() => ReadList("enabled-extensions").Contains(Uuid);

    private static void SetEnabledAtLogin(bool on)
    {
        var enabled = ReadList("enabled-extensions");
        var disabled = ReadList("disabled-extensions");
        enabled.Remove(Uuid);
        disabled.Remove(Uuid);
        (on ? enabled : disabled).Add(Uuid);
        WriteList("enabled-extensions", enabled);
        WriteList("disabled-extensions", disabled);
    }

    // gsettings prints a string list as GVariant text: ['a@b', 'c@d'] or "@as []". Extension uuids hold no quotes.
    private static List<string> ReadList(string key) => ParseList(Gsettings("get", "org.gnome.shell", key));

    public static List<string> ParseList(string gvariant) => gvariant.Split('\'').Where((_, i) => i % 2 == 1).ToList();

    private static void WriteList(string key, List<string> list) =>
        Gsettings("set", "org.gnome.shell", key, "[" + string.Join(", ", list.Select(u => $"'{u}'")) + "]");

    private static string Gsettings(params string[] args)
    {
        var start = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
