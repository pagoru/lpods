"""python-dbusmock's notification daemon on the session bus: notify_mock.py start | list (the Notify calls) | action ID KEY | closed ID REASON."""
import json
import subprocess
import sys
import time

import dbus

command, args = sys.argv[1], sys.argv[2:]
if command == "start":
    subprocess.Popen([sys.executable, "-m", "dbusmock", "--session", "--template", "notification_daemon"], stdout=subprocess.DEVNULL)
    bus = dbus.SessionBus()
    for _ in range(100):
        try:
            bus.get_object("org.freedesktop.Notifications", "/org/freedesktop/Notifications")
            sys.exit(0)
        except dbus.exceptions.DBusException:
            time.sleep(0.1)
    sys.exit("notification daemon did not start")

bus = dbus.SessionBus()
obj = bus.get_object("org.freedesktop.Notifications", "/org/freedesktop/Notifications")
mock = dbus.Interface(obj, "org.freedesktop.DBus.Mock")


def plain(v):
    if isinstance(v, dict):
        return {str(k): plain(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [plain(x) for x in v]
    if isinstance(v, dbus.Boolean):
        return bool(v)
    if isinstance(v, (dbus.Int16, dbus.Int32, dbus.Int64, dbus.UInt16, dbus.UInt32, dbus.UInt64, dbus.Byte)):
        return int(v)
    return str(v)


if command == "list":
    print(json.dumps([plain(args) for _, method, args in mock.GetCalls() if method == "Notify"]))
elif command == "action":
    mock.EmitSignal("org.freedesktop.Notifications", "ActionInvoked", "us", [dbus.UInt32(args[0]), args[1]])
elif command == "closed":
    mock.EmitSignal("org.freedesktop.Notifications", "NotificationClosed", "uu", [dbus.UInt32(args[0]), dbus.UInt32(args[1])])
else:
    sys.exit(f"unknown command {command}")
