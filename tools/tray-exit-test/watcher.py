# A minimal org.kde.StatusNotifierWatcher for tools/tray-exit-test/run.sh: accepts registrations and reports a host,
# so OpenHW registers its tray icons on the private test bus as it does with GNOME's AppIndicator extension.
from gi.repository import Gio, GLib
XML = """<node><interface name="org.kde.StatusNotifierWatcher">
<method name="RegisterStatusNotifierItem"><arg type="s" direction="in"/></method>
<method name="RegisterStatusNotifierHost"><arg type="s" direction="in"/></method>
<property name="RegisteredStatusNotifierItems" type="as" access="read"/>
<property name="IsStatusNotifierHostRegistered" type="b" access="read"/>
<property name="ProtocolVersion" type="i" access="read"/>
<signal name="StatusNotifierItemRegistered"><arg type="s"/></signal></interface></node>"""
node = Gio.DBusNodeInfo.new_for_xml(XML)
items = []
def call(conn, sender, path, iface, method, params, invocation):
    if method == "RegisterStatusNotifierItem":
        service = params.unpack()[0]
        items.append(service if service.startswith(":") or "/" not in service else sender + service)
        print("item", sender, service, flush=True)
    invocation.return_value(None)
def get(conn, sender, path, iface, name):
    return {"RegisteredStatusNotifierItems": GLib.Variant("as", items),
            "IsStatusNotifierHostRegistered": GLib.Variant("b", True), "ProtocolVersion": GLib.Variant("i", 0)}[name]
def acquired(conn, name):
    conn.register_object("/StatusNotifierWatcher", node.interfaces[0], call, get, None)
Gio.bus_own_name(Gio.BusType.SESSION, "org.kde.StatusNotifierWatcher", 0, acquired, None, None)
print("watcher up", flush=True)
GLib.MainLoop().run()
