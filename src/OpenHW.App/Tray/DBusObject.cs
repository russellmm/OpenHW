using System.Text;
using Tmds.DBus.Protocol;

namespace OpenHW.App.Tray;

/// <summary>
/// One exported D-Bus object with a single interface: answers Properties.Get/GetAll and Introspect, and hands
/// every other call on its interface to the subclass. Tmds.DBus.Protocol 0.20 is message-level only, so this is
/// the small amount of plumbing the higher-level Tmds.DBus would otherwise generate.
/// </summary>
public abstract class DBusObject : IMethodHandler
{
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    public abstract string Path { get; }
    protected abstract string Interface { get; }
    protected abstract string IntrospectXml { get; }
    protected abstract IReadOnlyList<string> PropertyNames { get; }

    /// <summary>Writes the property as a variant (signature + value).</summary>
    protected abstract void WriteProperty(ref MessageWriter writer, string name);

    /// <summary>MessageWriter is a ref struct: helpers must take it by ref or their writes land in a copy.</summary>
    protected delegate void BodyWriter(ref MessageWriter writer);

    /// <summary>Handles a call on <see cref="Interface"/>; false if the member is unknown.</summary>
    protected abstract bool HandleMethod(MethodContext context, string member);

    // Every handler only reads cached state, so running on the receive loop avoids a thread hop per call.
    public bool RunMethodHandlerSynchronously(Message message) => true;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([Encoding.UTF8.GetBytes(IntrospectXml)]);
            return default;
        }

        var request = context.Request;
        var iface = request.InterfaceAsString;
        var member = request.MemberAsString ?? "";
        var handled = iface switch
        {
            PropertiesInterface => HandleProperties(context, member),
            _ when iface == Interface => HandleMethod(context, member),
            _ => false,
        };
        if (!handled && !context.ReplySent)
            context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", $"{iface}.{member} is not supported");
        return default;
    }

    private bool HandleProperties(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        var iface = reader.ReadString();
        if (iface != Interface)
        {
            context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface", iface);
            return true;
        }

        switch (member)
        {
            case "Get":
            {
                var name = reader.ReadString();
                if (!PropertyNames.Contains(name))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                    return true;
                }
                Reply(context, "v", (ref MessageWriter writer) => WriteProperty(ref writer, name));
                return true;
            }
            case "GetAll":
            {
                Reply(context, "a{sv}", (ref MessageWriter writer) =>
                {
                    var dict = writer.WriteDictionaryStart();
                    foreach (var name in PropertyNames)
                    {
                        writer.WriteDictionaryEntryStart();
                        writer.WriteString(name);
                        WriteProperty(ref writer, name);
                    }
                    writer.WriteDictionaryEnd(dict);
                });
                return true;
            }
            case "Set":
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "All properties are read-only");
                return true;
            default:
                return false;
        }
    }

    protected static void Reply(MethodContext context, string signature, BodyWriter body)
    {
        var writer = context.CreateReplyWriter(signature);
        try
        {
            body(ref writer);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    protected static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using var writer = context.CreateReplyWriter(null!);
        context.Reply(writer.CreateMessage());
    }

    protected static void EmitSignal(Connection connection, string path, string iface, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(destination: null, path: path, @interface: iface, member: member, signature: null);
        connection.TrySendMessage(writer.CreateMessage());
    }
}
