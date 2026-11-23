"""A minimal StatusNotifier host for the tray test: sni_host.py OUTPUT_JSON.

Owns org.kde.StatusNotifierWatcher on the session bus (DBUS_SESSION_BUS_ADDRESS). When an item registers it reads its
properties and its dbusmenu layout like a shell extension would, writes them to OUTPUT_JSON, then clicks the icon and the
menu entries "Open" and "Quit" and exits.
"""
import json
import sys
import threading

import dbus
import dbus.mainloop.glib
import dbus.service
from gi.repository import GLib

dbus.mainloop.glib.DBusGMainLoop(set_as_default=True)
bus = dbus.SessionBus()
loop = GLib.MainLoop()
output = sys.argv[1]


def plain(value):
    if isinstance(value, dict):
        return {str(k): plain(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [plain(v) for v in value]
    if isinstance(value, (dbus.Boolean,)):
        return bool(value)
    if isinstance(value, (dbus.Int16, dbus.Int32, dbus.Int64, dbus.UInt16, dbus.UInt32, dbus.UInt64, dbus.Byte)):
        return int(value)
    return str(value) if isinstance(value, (dbus.String, dbus.ObjectPath)) else value


def inspect(service):
    try:
        item = bus.get_object(service, "/StatusNotifierItem")
        props = dbus.Interface(item, "org.freedesktop.DBus.Properties").GetAll("org.kde.StatusNotifierItem")
        menu_object = bus.get_object(service, "/MenuBar")
        menu = dbus.Interface(menu_object, "com.canonical.dbusmenu")
        revision, layout = menu.GetLayout(0, -1, dbus.Array([], signature="s"))
        result = {"service": service, "properties": plain(props), "revision": int(revision), "layout": plain(layout)}
        children = layout[2]
        labels = {str(c[1].get("label", "")): int(c[0]) for c in children if "label" in c[1]}
        # A shell extension activates the icon, then picks menu entries.
        dbus.Interface(item, "org.kde.StatusNotifierItem").Activate(0, 0)
        for label in ("Open", "Quit"):
            for name, ident in labels.items():
                if name.startswith(label) or name.endswith(label):
                    menu.Event(ident, "clicked", dbus.String("", variant_level=1), dbus.UInt32(0))
        result["clicked"] = sorted(labels)
        json.dump(result, open(output, "w"))
    except Exception as e:
        json.dump({"error": repr(e)}, open(output, "w"))
    finally:
        loop.quit()


class Watcher(dbus.service.Object):
    @dbus.service.method("org.kde.StatusNotifierWatcher", in_signature="s")
    def RegisterStatusNotifierItem(self, service):
        GLib.timeout_add(100, lambda: threading.Thread(target=inspect, args=(service,)).start() and False)


name = dbus.service.BusName("org.kde.StatusNotifierWatcher", bus)
Watcher(bus, "/StatusNotifierWatcher")
print("ready", flush=True)
loop.run()
