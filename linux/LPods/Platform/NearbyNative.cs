using System.Runtime.InteropServices;
using LPods.Platform;
using WPods.Core;

namespace WPods.Nearby;

/// <summary>
/// Linux stand-in for upstream's NearbyNative (wpods_native.dll): a BLE scan through BlueZ discovery that reports
/// Apple manufacturer data to the same callback, and the one-shot audio connect (Device1.Connect).
/// </summary>
internal static unsafe class NearbyNative
{
    private const int EFail = unchecked((int)0x80004005);

    private static readonly Lock ScanLock = new();
    private static readonly Dictionary<string, (ulong Address, byte Type)> AddressByPath = new(StringComparer.Ordinal);
    private static nint _callback;
    private static nint _context;
    private static List<string> _scanning = [];

    /// <summary>Callback: (ctx, address, addressType 0 public / 1 random, rssi, payload after the company ID, length).</summary>
    public static int wpn_ble_start(delegate* unmanaged[Stdcall]<nint, ulong, byte, short, byte*, uint, void> callback, nint context)
    {
        wpn_ble_stop();
        lock (ScanLock)
        {
            _callback = (nint)callback;
            _context = context;
        }
        BlueZ.AdvertReceived += OnAdvert;
        try
        {
            List<string> adapters = BlueZ.GetPoweredAdaptersAsync().GetAwaiter().GetResult();
            foreach (string adapter in adapters)
            {
                BlueZ.SetLeDiscoveryFilterAsync(adapter).GetAwaiter().GetResult();
                BlueZ.CallAsync(adapter, BlueZ.AdapterInterface, "StartDiscovery").GetAwaiter().GetResult();
            }
            lock (ScanLock)
            {
                _scanning = adapters;
            }
            return adapters.Count > 0 ? 0 : EFail;
        }
        catch (Exception e)
        {
            Log.Warn("nearby", $"BLE scan not started: {e.Message}");
            return EFail;
        }
    }

    public static void wpn_ble_stop()
    {
        BlueZ.AdvertReceived -= OnAdvert;
        List<string> adapters;
        lock (ScanLock)
        {
            _callback = 0;
            adapters = _scanning;
            _scanning = [];
        }
        foreach (string adapter in adapters)
        {
            try
            {
                BlueZ.CallAsync(adapter, BlueZ.AdapterInterface, "StopDiscovery").GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Info("nearby", $"stop discovery on {adapter}: {e.Message}");
            }
        }
    }

    /// <summary>Connects the pair's audio (pages it); HRESULT-style result like the upstream helper.</summary>
    public static int wpn_bt_connect_audio(ulong btAddress)
    {
        try
        {
            if (BlueZ.FindDeviceAsync(btAddress).GetAwaiter().GetResult() is not { } device)
            {
                return EFail;
            }
            BlueZ.CallAsync(device.Path, BlueZ.DeviceInterface, "Connect").GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Log.Warn("nearby", $"connect {Mac.Format(btAddress)} failed: {e.Message}");
            return EFail;
        }
    }

    private static void OnAdvert(string path, short? rssi, byte[] data)
    {
        nint callback;
        nint context;
        lock (ScanLock)
        {
            callback = _callback;
            context = _context;
        }
        if (callback == 0 || Address(path) is not { } address)
        {
            return;
        }
        fixed (byte* p = data)
        {
            ((delegate* unmanaged[Stdcall]<nint, ulong, byte, short, byte*, uint, void>)callback)(
                context, address.Address, address.Type, rssi ?? (short)-100, p, (uint)data.Length);
        }
    }

    // Address and type of a device object, cached (resolvable private addresses rotate, so paths come and go).
    private static (ulong Address, byte Type)? Address(string path)
    {
        lock (ScanLock)
        {
            if (AddressByPath.TryGetValue(path, out var known))
            {
                return known;
            }
        }
        try
        {
            var objects = BlueZ.GetObjectsAsync().GetAwaiter().GetResult();
            lock (ScanLock)
            {
                AddressByPath.Clear();
                foreach ((string p, var interfaces) in objects)
                {
                    if (interfaces.TryGetValue(BlueZ.DeviceInterface, out var properties) && BlueZ.ToDevice(p, properties) is { } device)
                    {
                        AddressByPath[p] = (device.Address, device.RandomAddress ? (byte)1 : (byte)0);
                    }
                }
                return AddressByPath.TryGetValue(path, out var found) ? found : null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
