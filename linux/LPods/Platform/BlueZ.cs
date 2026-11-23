using System.Globalization;
using Tmds.DBus.Protocol;
using WPods.Core;

namespace LPods.Platform;

/// <summary>One org.bluez.Device1 object.</summary>
internal sealed record BlueZDevice(
    string Path, ulong Address, bool RandomAddress, string Name, bool Paired, bool Connected, ushort Vendor, ushort Product,
    IReadOnlyList<string> Uuids)
{
    /// <summary>Apple's AAP service (L2CAP PSM 0x1001), listed in the SDP records of AirPods.</summary>
    public const string AapUuid = "74ec2172-0bad-4d01-8f77-997b2be0722a";

    public const ushort AppleVendor = 0x004C;

    public bool IsAirPods =>
        Paired && (Uuids.Contains(AapUuid, StringComparer.OrdinalIgnoreCase) || (Vendor == AppleVendor && Models.FromProximityId(Product) is not null));
}

/// <summary>
/// BlueZ over the system bus: device and adapter snapshots, method calls and change notifications. One shared
/// connection; every change (device added, removed or a property changed) raises <see cref="Changed"/> with the
/// object path. Advertisement data is passed to <see cref="AdvertReceived"/>.
/// </summary>
internal static class BlueZ
{
    private const string Service = "org.bluez";
    public const string DeviceInterface = "org.bluez.Device1";
    public const string AdapterInterface = "org.bluez.Adapter1";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DBusConnection? _connection;

    /// <summary>Something under /org/bluez changed (path of the object, or "" when the whole tree may have).</summary>
    public static event Action<string>? Changed;

    /// <summary>Apple manufacturer data (company 0x004C) of an advert: device path, RSSI (or null), payload.</summary>
    public static event Action<string, short?, byte[]>? AdvertReceived;

    public static async Task<DBusConnection> ConnectionAsync()
    {
        if (Volatile.Read(ref _connection) is { } existing)
        {
            return existing;
        }
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }
            // Signals are matched without a sender: the bus delivers them from BlueZ's unique name, which a rule on
            // "org.bluez" would not match on the client side. The paths (/org/bluez/...) keep other services out.
            var connection = new DBusConnection(DBusAddress.System!);
            await connection.ConnectAsync().ConfigureAwait(false);
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = PropertiesInterface, Member = "PropertiesChanged", PathNamespace = "/org/bluez" },
                static (Message m, object? _) => ReadPropertiesChanged(m),
                static (Notification<(string Path, string Interface, Dictionary<string, VariantValue> Changed)> n) =>
                {
                    if (n.HasValue)
                    {
                        OnProperties(n.Value.Path, n.Value.Interface, n.Value.Changed);
                    }
                },
                emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = ObjectManagerInterface, Member = "InterfacesAdded", Arg0Path = "/org/bluez/" },
                static (Message m, object? _) => ReadInterfacesAdded(m),
                static (Notification<(string Path, Dictionary<string, Dictionary<string, VariantValue>> Interfaces)> n) =>
                {
                    if (n.HasValue)
                    {
                        foreach ((string name, Dictionary<string, VariantValue> properties) in n.Value.Interfaces)
                        {
                            OnProperties(n.Value.Path, name, properties);
                        }
                    }
                },
                emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = ObjectManagerInterface, Member = "InterfacesRemoved", Arg0Path = "/org/bluez/" },
                static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(),
                static (Notification<string> n) =>
                {
                    if (n.HasValue)
                    {
                        Raise(n.Value);
                    }
                },
                emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
            Volatile.Write(ref _connection, connection);
            return connection;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Every BlueZ object: path -> interface -> properties.</summary>
    public static async Task<Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>> GetObjectsAsync()
    {
        DBusConnection connection = await ConnectionAsync().ConfigureAwait(false);
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Service, path: "/", @interface: ObjectManagerInterface, member: "GetManagedObjects");
            message = writer.CreateMessage();
        }
        return await connection.CallMethodAsync(message, static (Message m, object? _) => ReadObjects(m), null).ConfigureAwait(false);
    }

    public static async Task<List<BlueZDevice>> GetDevicesAsync()
    {
        var devices = new List<BlueZDevice>();
        foreach ((string path, Dictionary<string, Dictionary<string, VariantValue>> interfaces) in await GetObjectsAsync().ConfigureAwait(false))
        {
            if (interfaces.TryGetValue(DeviceInterface, out Dictionary<string, VariantValue>? p) && ToDevice(path, p) is { } device)
            {
                devices.Add(device);
            }
        }
        return devices;
    }

    public static async Task<BlueZDevice?> FindDeviceAsync(ulong address) =>
        (await GetDevicesAsync().ConfigureAwait(false)).FirstOrDefault(d => d.Address == address && !d.RandomAddress);

    /// <summary>Object paths of the adapters that are powered.</summary>
    public static async Task<List<string>> GetPoweredAdaptersAsync()
    {
        var adapters = new List<string>();
        foreach ((string path, Dictionary<string, Dictionary<string, VariantValue>> interfaces) in await GetObjectsAsync().ConfigureAwait(false))
        {
            if (interfaces.TryGetValue(AdapterInterface, out Dictionary<string, VariantValue>? p) && Bool(p, "Powered"))
            {
                adapters.Add(path);
            }
        }
        return adapters;
    }

    /// <summary>Calls a method without arguments (Device1.Connect / Disconnect, Adapter1.StartDiscovery / StopDiscovery).</summary>
    public static async Task CallAsync(string path, string @interface, string member)
    {
        DBusConnection connection = await ConnectionAsync().ConfigureAwait(false);
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Service, path: path, @interface: @interface, member: member);
            message = writer.CreateMessage();
        }
        await connection.CallMethodAsync(message).ConfigureAwait(false);
    }

    /// <summary>Adapter1.SetDiscoveryFilter for LE adverts, reporting every advert (not only changes).</summary>
    public static async Task SetLeDiscoveryFilterAsync(string adapter)
    {
        DBusConnection connection = await ConnectionAsync().ConfigureAwait(false);
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Service, path: adapter, @interface: AdapterInterface, member: "SetDiscoveryFilter", signature: "a{sv}");
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["Transport"] = "le",
                ["DuplicateData"] = true,
            });
            message = writer.CreateMessage();
        }
        await connection.CallMethodAsync(message).ConfigureAwait(false);
    }

    public static BlueZDevice? ToDevice(string path, Dictionary<string, VariantValue> p)
    {
        if (!p.TryGetValue("Address", out VariantValue addressValue) || !Mac.TryParse(Unwrap(addressValue).GetString(), out ulong address))
        {
            return null;
        }
        ushort vendor = 0;
        ushort product = 0;
        // "bluetooth:v004Cp2014d0B01" (Device ID record: vendor source, vendor, product, version).
        string modalias = String(p, "Modalias") ?? "";
        int v = modalias.IndexOf(":v", StringComparison.Ordinal);
        if (v >= 0 && modalias.Length >= v + 12 && modalias[v + 6] == 'p')
        {
            _ = ushort.TryParse(modalias.AsSpan(v + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vendor);
            _ = ushort.TryParse(modalias.AsSpan(v + 7, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out product);
        }
        IReadOnlyList<string> uuids = p.TryGetValue("UUIDs", out VariantValue u) ? Unwrap(u).GetArray<string>() : [];
        return new BlueZDevice(
            path, address, String(p, "AddressType") == "random", String(p, "Alias") ?? String(p, "Name") ?? Mac.Format(address),
            Bool(p, "Paired"), Bool(p, "Connected"), vendor, product, uuids);
    }

    private static void OnProperties(string path, string @interface, Dictionary<string, VariantValue> properties)
    {
        if (@interface == DeviceInterface && properties.TryGetValue("ManufacturerData", out VariantValue data))
        {
            try
            {
                Dictionary<ushort, VariantValue> byCompany = Unwrap(data).GetDictionary<ushort, VariantValue>();
                if (byCompany.TryGetValue(BlueZDevice.AppleVendor, out VariantValue apple))
                {
                    short? rssi = properties.TryGetValue("RSSI", out VariantValue r) ? Unwrap(r).GetInt16() : null;
                                byte[] payload = Unwrap(apple).GetArray<byte>();
                    Dispatch(() => AdvertReceived?.Invoke(path, rssi, payload));
                }
            }
            catch (Exception e)
            {
                Log.Warn("bluez", $"unreadable manufacturer data on {path}: {e.Message}");
            }
        }
        // Adverts and RSSI alone change nothing a snapshot shows.
        if (properties.Keys.Any(k => k is not ("ManufacturerData" or "RSSI" or "TxPower" or "ServiceData")))
        {
            Raise(path);
        }
    }

    private static void Raise(string path) => Dispatch(() => Changed?.Invoke(path));

    // Signal handlers run on the connection's receive loop: a handler that waits for a method reply would block it.
    private static void Dispatch(Action action) => ThreadPool.QueueUserWorkItem(static a =>
    {
        try
        {
            a();
        }
        catch (Exception e)
        {
            Log.Error("bluez", "handler failed", e);
        }
    }, action, preferLocal: false);

    private static VariantValue Unwrap(VariantValue value) => value.Type == VariantValueType.Variant ? Unwrap(value.GetVariantValue()) : value;

    private static bool Bool(Dictionary<string, VariantValue> p, string name) =>
        p.TryGetValue(name, out VariantValue value) && Unwrap(value) is { Type: VariantValueType.Bool } b && b.GetBool();

    private static string? String(Dictionary<string, VariantValue> p, string name) =>
        p.TryGetValue(name, out VariantValue value) && Unwrap(value) is { Type: VariantValueType.String } s ? s.GetString() : null;

    private static Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> ReadObjects(Message message)
    {
        Reader reader = message.GetBodyReader();
        var objects = new Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>(StringComparer.Ordinal);
        ArrayEnd end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            string path = reader.ReadObjectPathAsString();
            objects[path] = ReadInterfaces(ref reader);
        }
        return objects;
    }

    private static (string, Dictionary<string, Dictionary<string, VariantValue>>) ReadInterfacesAdded(Message message)
    {
        Reader reader = message.GetBodyReader();
        string path = reader.ReadObjectPathAsString();
        return (path, ReadInterfaces(ref reader));
    }

    private static Dictionary<string, Dictionary<string, VariantValue>> ReadInterfaces(ref Reader reader)
    {
        var interfaces = new Dictionary<string, Dictionary<string, VariantValue>>(StringComparer.Ordinal);
        ArrayEnd end = reader.ReadDictionaryStart();
        while (reader.HasNext(end))
        {
            string name = reader.ReadString();
            interfaces[name] = reader.ReadDictionaryOfStringToVariantValue();
        }
        return interfaces;
    }

    private static (string, string, Dictionary<string, VariantValue>) ReadPropertiesChanged(Message message)
    {
        string path = message.PathAsString ?? "";
        Reader reader = message.GetBodyReader();
        string @interface = reader.ReadString();
        Dictionary<string, VariantValue> changed = reader.ReadDictionaryOfStringToVariantValue();
        return (path, @interface, changed);
    }
}
