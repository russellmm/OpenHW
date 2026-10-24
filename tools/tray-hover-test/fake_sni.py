# Two StatusNotifierItems (stand-ins for tools/tray-hover-test/run.sh) on the private test bus: one with OpenHW's id, one not. The OpenHW one changes its
# tooltip every second and signals NewToolTip, as OpenHW does after each poll.
import sys
from gi.repository import Gio, GLib
XML = """<node><interface name="org.kde.StatusNotifierItem">
<property name="Id" type="s" access="read"/><property name="Category" type="s" access="read"/>
<property name="Status" type="s" access="read"/><property name="Title" type="s" access="read"/>
<property name="IconName" type="s" access="read"/><property name="Menu" type="o" access="read"/>
<property name="ToolTip" type="(sa(iiay)ss)" access="read"/><signal name="NewToolTip"/></interface></node>"""
node = Gio.DBusNodeInfo.new_for_xml(XML)
items = []
def make(item_id, title_fn, body):
    conn = Gio.DBusConnection.new_for_address_sync(Gio.dbus_address_get_for_bus_sync(Gio.BusType.SESSION), Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT | Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION)
    def get(c, sender, path, iface, name):
        return {"Id": GLib.Variant("s", item_id), "Category": GLib.Variant("s", "Hardware"),
                "Status": GLib.Variant("s", "Active"), "Title": GLib.Variant("s", item_id),
                "IconName": GLib.Variant("s", "dialog-information"), "Menu": GLib.Variant("o", "/NO_DBUSMENU"),
                "ToolTip": GLib.Variant("(sa(iiay)ss)", ("", [], title_fn(), body))}[name]
    conn.register_object("/StatusNotifierItem", node.interfaces[0], None, get, None)
    items.append(conn)
    def register():
        try:
            conn.call_sync("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.kde.StatusNotifierWatcher",
                           "RegisterStatusNotifierItem", GLib.Variant("(s)", ("/StatusNotifierItem",)), None, 0, 2000, None)
            print("registered", item_id, conn.get_unique_name(), flush=True); return False
        except GLib.Error as e:
            print("waiting", e.message, flush=True); return True
    GLib.timeout_add(500, register)
    return conn
n = [0]
openhw = make("openhw-tray-0", lambda: f"OpenHW · GPU Temperature: {32 + n[0]}.0 °C",
              "Min 32.0 °C  ·  Max 40.0 °C  ·  Avg 33.1 °C\nGPU [#2]: NVIDIA RTX PRO 6000 Blackwell Workstation Edition E1:00.0\n"
              "GPU core temperature. NVIDIA cards slow down near 83–90 °C and shut down well above that; a card that sits "
              "above 80 °C under steady load has too little airflow or a fan curve that is too relaxed.")
make("other-app", lambda: "Other app", "should never show")
def tick():
    n[0] += 1
    openhw.emit_signal(None, "/StatusNotifierItem", "org.kde.StatusNotifierItem", "NewToolTip", None)
    return True
GLib.timeout_add(1000, tick)
GLib.MainLoop().run()
