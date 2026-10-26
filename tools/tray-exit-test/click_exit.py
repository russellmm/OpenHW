# Clicks "Exit" in OpenHW's first tray icon menu, as GNOME's panel does (com.canonical.dbusmenu Event "clicked").
import sys, time
from gi.repository import Gio, GLib
bus = Gio.bus_get_sync(Gio.BusType.SESSION)
def prop(dest, path, iface, name):
    return bus.call_sync(dest, path, "org.freedesktop.DBus.Properties", "Get", GLib.Variant("(ss)", (iface, name)),
                         None, 0, 2000, None).unpack()[0]
deadline = time.monotonic() + 40
while time.monotonic() < deadline:
    items = prop("org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher", "org.kde.StatusNotifierWatcher", "RegisteredStatusNotifierItems")
    found = [s for s in items if prop(s.split("/")[0], "/" + s.split("/", 1)[1] if "/" in s else "/StatusNotifierItem",
                                       "org.kde.StatusNotifierItem", "Id") == "openhw-tray-0"]
    if found: break
    time.sleep(0.5)
else:
    sys.exit("no OpenHW tray icon registered")
service = found[0].split("/")[0]
item_path = "/" + found[0].split("/", 1)[1] if "/" in found[0] else "/StatusNotifierItem"
menu = prop(service, item_path, "org.kde.StatusNotifierItem", "Menu")
layout = bus.call_sync(service, menu, "com.canonical.dbusmenu", "GetLayout", GLib.Variant("(iias)", (0, -1, [])),
                       None, 0, 2000, None).unpack()[1]
def find(node):
    ident, props, children = node
    if "Exit" in str(props.get("label", "")): return ident
    for child in children:
        hit = find(child)
        if hit is not None: return hit
exit_id = find(layout)
print("clicking Exit, item", exit_id, flush=True)
bus.call_sync(service, menu, "com.canonical.dbusmenu", "Event",
              GLib.Variant("(isvu)", (exit_id, "clicked", GLib.Variant("s", ""), 0)), None, 0, 2000, None)
