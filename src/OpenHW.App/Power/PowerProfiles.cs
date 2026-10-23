using Tmds.DBus.Protocol;

namespace OpenHW.App.Power;

/// <summary>
/// power-profiles-daemon over the system bus (what GNOME's power menu and <c>powerprofilesctl</c> use). Switching
/// needs no root: polkit lets the active session change the profile.
/// </summary>
public sealed class PowerProfiles : IDisposable
{
    private const string Service = "org.freedesktop.UPower.PowerProfiles";
    private const string ObjectPath = "/org/freedesktop/UPower/PowerProfiles";
    private const string Interface = "org.freedesktop.UPower.PowerProfiles";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private readonly Connection _connection;
    private IDisposable? _match;

    /// <summary>The active profile changed, from any source. Raised on the D-Bus thread.</summary>
    public event Action<string>? ActiveChanged;

    private PowerProfiles(Connection connection) => _connection = connection;

    public static async Task<PowerProfiles> ConnectAsync()
    {
        var connection = new Connection(Address.System ?? throw new InvalidOperationException("No system bus"));
        await connection.ConnectAsync();
        var profiles = new PowerProfiles(connection);
        profiles._match = await connection.AddMatchAsync(
            new MatchRule
            {
                Type = MessageType.Signal,
                Sender = Service,
                Path = ObjectPath,
                Interface = PropertiesInterface,
                Member = "PropertiesChanged",
            },
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                if (reader.ReadString() != Interface) return null;
                var changed = reader.ReadDictionaryOfStringToVariantValue();
                return changed.TryGetValue("ActiveProfile", out var value) ? value.GetString() : null;
            },
            (Exception? ex, string? active, object? _, object? state) =>
            {
                if (ex is null && active is not null) ((PowerProfiles)state!).ActiveChanged?.Invoke(active);
            },
            ObserverFlags.None,
            readerState: null,
            handlerState: profiles,
            emitOnCapturedContext: false);
        return profiles;
    }

    public async Task<string> GetActiveAsync() => (await GetPropertyAsync("ActiveProfile")).GetString();

    /// <summary>Profile names the daemon offers, in its order (power-saver, balanced, performance).</summary>
    public async Task<IReadOnlyList<string>> GetProfilesAsync()
    {
        var value = await GetPropertyAsync("Profiles");
        var names = new List<string>();
        for (var i = 0; i < value.Count; i++)
            if (value.GetItem(i).GetDictionary<string, VariantValue>().TryGetValue("Profile", out var name)) names.Add(name.GetString());
        return names;
    }

    public Task SetActiveAsync(string profile)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: PropertiesInterface, member: "Set", signature: "ssv");
        writer.WriteString(Interface);
        writer.WriteString("ActiveProfile");
        writer.WriteVariantString(profile);
        return _connection.CallMethodAsync(writer.CreateMessage());
    }

    // MessageWriter is a ref struct, so these build the call synchronously and hand back the pending reply.
    private Task<VariantValue> GetPropertyAsync(string name)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: PropertiesInterface, member: "Get", signature: "ss");
        writer.WriteString(Interface);
        writer.WriteString(name);
        return _connection.CallMethodAsync(writer.CreateMessage(), static (Message message, object? _) => message.GetBodyReader().ReadVariantValue());
    }

    public void Dispose()
    {
        _match?.Dispose();
        _connection.Dispose();
    }
}
