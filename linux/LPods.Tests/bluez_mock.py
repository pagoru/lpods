"""Drives python-dbusmock's bluez5 template for the BlueZ tests: bluez_mock.py COMMAND [ARGS...].

Runs against the bus in DBUS_SYSTEM_BUS_ADDRESS (a private dbus-daemon the test starts).
"""
import sys

import dbus

MOCK = "org.bluez.Mock"
bus = dbus.SystemBus()
root = dbus.Interface(bus.get_object("org.bluez", "/"), MOCK)
command, args = sys.argv[1], sys.argv[2:]


def device(address):
    return bus.get_object("org.bluez", "/org/bluez/hci0/dev_" + address.replace(":", "_"))


if command == "adapter":
    root.AddAdapter("hci0", "lpods-test")
elif command == "airpods":
    address, name = args
    root.AddDevice("hci0", address, name)
    root.PairDevice("hci0", address)
    device(address).UpdateProperties(
        "org.bluez.Device1",
        {
            "UUIDs": dbus.Array(["0000110b-0000-1000-8000-00805f9b34fb", "74ec2172-0bad-4d01-8f77-997b2be0722a"], signature="s"),
            "Modalias": "bluetooth:v004Cp2014d0B01",
        },
        dbus_interface="org.freedesktop.DBus.Mock",
    )
elif command == "connect":
    root.ConnectDevice("hci0", args[0])
elif command == "disconnect":
    root.DisconnectDevice("hci0", args[0])
elif command == "advert":
    address, payload = args
    root.AddDevice("hci0", address, "")
    device(address).UpdateProperties(
        "org.bluez.Device1",
        {
            "AddressType": "random",
            "RSSI": dbus.Int16(-50),
            "ManufacturerData": dbus.Dictionary(
                {dbus.UInt16(0x004C): dbus.Array(bytes.fromhex(payload), signature="y", variant_level=1)}, signature="qv"
            ),
        },
        dbus_interface="org.freedesktop.DBus.Mock",
    )
else:
    sys.exit(f"unknown command {command}")
