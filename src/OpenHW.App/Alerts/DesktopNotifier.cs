using Tmds.DBus.Protocol;

namespace OpenHW.App.Alerts;

/// <summary>
/// Desktop notifications through org.freedesktop.Notifications on the session bus (GNOME's notification bubbles).
/// An alert is sent critical, so GNOME keeps it until dismissed; its "back to normal" replaces the same bubble.
/// Failures (no notification daemon) are logged, never thrown.
/// </summary>
public sealed class DesktopNotifier : IDisposable
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private Connection? _connection;
    private readonly Dictionary<string, uint> _bubbles = []; // rule id → notification id, so updates replace

    public async Task NotifyAsync(string key, string summary, string body, bool critical)
    {
        try
        {
            if (_connection is null)
            {
                var connection = new Connection(Address.Session ?? throw new InvalidOperationException("No session bus"));
                await connection.ConnectAsync();
                _connection = connection;
            }
            _bubbles.TryGetValue(key, out var replaces);
            _bubbles[key] = await _connection.CallMethodAsync(Build(_connection, replaces, summary, body, critical),
                static (Message m, object? _) => m.GetBodyReader().ReadUInt32());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"openhw: notification failed ({ex.Message})");
        }
    }

    // Notify(app_name, replaces_id, app_icon, summary, body, actions, hints, expire_timeout) → id.
    // MessageWriter is a ref struct: built synchronously, sent by the caller.
    private static MessageBuffer Build(Connection connection, uint replaces, string summary, string body, bool critical)
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
            writer.WriteArray(Array.Empty<string>());
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

    public void Dispose() => _connection?.Dispose();
}
