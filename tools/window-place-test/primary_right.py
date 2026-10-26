# Makes the second virtual monitor (Meta-1, at x=1920) the primary one in the private test shell, through mutter's
# DisplayConfig — the layout of the owner's desktop, where the primary monitor is right of the other.
from gi.repository import Gio, GLib
bus = Gio.bus_get_sync(Gio.BusType.SESSION)
proxy = Gio.DBusProxy.new_sync(bus, 0, None, "org.gnome.Mutter.DisplayConfig", "/org/gnome/Mutter/DisplayConfig",
                               "org.gnome.Mutter.DisplayConfig")
serial, monitors, logical, props = proxy.call_sync("GetCurrentState", None, 0, -1, None).unpack()
modes = {spec[0]: next(m[0] for m in modes if m[6].get("is-current")) for spec, modes, _ in monitors}
config = [(0, 0, 1.0, 0, False, [("Meta-0", modes["Meta-0"], {})]),
          (1920, 0, 1.0, 0, True, [("Meta-1", modes["Meta-1"], {})])]
proxy.call_sync("ApplyMonitorsConfig", GLib.Variant("(uua(iiduba(ssa{sv}))a{sv})", (serial, 1, config, {})), 0, -1, None)
print("primary: Meta-1 (right)")
