"""A fake MPRIS player on the session bus (DBUS_SESSION_BUS_ADDRESS): mpris_player.py NAME STATUS."""
import subprocess
import sys

name, status = sys.argv[1], sys.argv[2]
bus_name = "org.mpris.MediaPlayer2." + name
subprocess.Popen(
    [sys.executable, "-m", "dbusmock", "--session", bus_name, "/org/mpris/MediaPlayer2", "org.mpris.MediaPlayer2.Player"],
    stdout=subprocess.DEVNULL,
)

import time  # noqa: E402

import dbus  # noqa: E402

bus = dbus.SessionBus()
for _ in range(100):
    try:
        mock = dbus.Interface(bus.get_object(bus_name, "/org/mpris/MediaPlayer2"), "org.freedesktop.DBus.Mock")
        mock.AddProperty("org.mpris.MediaPlayer2.Player", "PlaybackStatus", status)
        break
    except dbus.exceptions.DBusException:
        time.sleep(0.1)
mock.AddMethod("org.mpris.MediaPlayer2.Player", "Pause", "", "", "self.Set('org.mpris.MediaPlayer2.Player', 'PlaybackStatus', 'Paused')")
mock.AddMethod("org.mpris.MediaPlayer2.Player", "Play", "", "", "self.Set('org.mpris.MediaPlayer2.Player', 'PlaybackStatus', 'Playing')")
