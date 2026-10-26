using Tmds.DBus.Protocol;

namespace OpenHW.App.Alerts;

/// <summary>
/// Desktop notifications through org.freedesktop.Notifications on the session bus (GNOME's notification bubbles).
/// An alert is sent critical, so GNOME keeps it until dismissed; its "back to normal" replaces the same bubble.
/// Failures (no notification daemon) are logged, never thrown. A notification can carry buttons (actions): a click
/// comes back through the ActionInvoked signal and runs the handler given with it ("default" = the bubble itself).
/// </summary>
public sealed class DesktopNotifier : IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private Connection? _connection;
    private IDisposable? _actionMatch;
    private readonly Dictionary<string, uint> _bubbles = []; // rule id → notification id, so updates replace
    private readonly Dictionary<uint, Action<string>> _handlers = []; // notification id → its buttons' handler
    private readonly object _gate = new();

    /// <param name="actions">Buttons as (key, label); "default" is a click on the bubble. Their handler gets the key.</param>
    public async Task NotifyAsync(string key, string summary, string body, bool critical,
        IReadOnlyList<(string Key, string Label)>? actions = null, Action<string>? onAction = null)
    {
        try
        {
            if (_connection is null)
            {
                var connection = new Connection(Address.Session ?? throw new InvalidOperationException("No session bus"));
                await connection.ConnectAsync();
                _actionMatch = await connection.AddMatchAsync(
                    new MatchRule { Type = MessageType.Signal, Interface = Service, Member = "ActionInvoked", Path = ObjectPath },
                    static (Message message, object? _) =>
                    {
                        var reader = message.GetBodyReader();
                        return (reader.ReadUInt32(), reader.ReadString());
                    },
                    (Exception? ex, (uint Id, string Action) invoked, object? _, object? state) =>
                    {
                        if (ex is not null) return;
                        var self = (DesktopNotifier)state!;
                        Action<string>? handler;
                        lock (self._gate) self._handlers.Remove(invoked.Id, out handler);
                        handler?.Invoke(invoked.Action);
                    },
                    ObserverFlags.None, readerState: null, handlerState: this, emitOnCapturedContext: false);
                _connection = connection;
            }
            _bubbles.TryGetValue(key, out var replaces);
            var id = await _connection.CallMethodAsync(Build(_connection, replaces, summary, body, critical, actions ?? []),
                static (Message m, object? _) => m.GetBodyReader().ReadUInt32());
            _bubbles[key] = id;
            if (onAction is not null) lock (_gate) _handlers[id] = onAction;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"openhw: notification failed ({ex.Message})");
        }
    }

    // Notify(app_name, replaces_id, app_icon, summary, body, actions, hints, expire_timeout) → id.
    // MessageWriter is a ref struct: built synchronously, sent by the caller.
    private static MessageBuffer Build(Connection connection, uint replaces, string summary, string body, bool critical,
        IReadOnlyList<(string Key, string Label)> actions)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Service, member: "Notify", signature: "susssasa{sv}i");
            writer.WriteString("OpenHW");
            writer.WriteUInt32(replaces);
            writer.WriteString("openhw");
            writer.WriteString(summary);
            writer.WriteString(body);
            writer.WriteArray(actions.SelectMany(a => new[] { a.Key, a.Label }).ToArray()); // [key, label, key, label, …]
            var hints = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("urgency");
            writer.WriteVariantByte(critical ? (byte)2 : (byte)1);
            writer.WriteDictionaryEnd(hints);
            writer.WriteInt32(critical ? 0 : -1); // 0 = never expires
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    public void Dispose()
    {
        _actionMatch?.Dispose();
        _connection?.Dispose();
    }
}
