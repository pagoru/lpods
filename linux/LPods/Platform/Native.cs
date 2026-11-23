using LPods.Platform;

namespace WPods.Core;

/// <summary>
/// Linux stand-in for upstream's Native (Win32 config manager, DPAPI, wpods_native.dll): the members the shared code
/// uses, with the same names and meanings. Error codes keep their Win32 values because the shared code compares them.
/// </summary>
internal static class Native
{
    public const int ErrorSuccess = 0;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorMoreData = 234;
    public const int ErrorServiceDoesNotExist = 1060;
    public const int ErrorDeviceNotConnected = 1167;
    public const int ErrorNotFound = 1168;

    public const int CmNotifyActionInterfaceArrival = 0;

    /// <summary>Upstream's Bluetooth radio interface class; on Linux any BlueZ adapter that powers on.</summary>
    public static readonly Guid RadioInterface = new("0850302A-B344-4FDA-9BE9-90576B8D46F0");

    private const int EFail = unchecked((int)0x80004005);

    private static readonly Lock RadioLock = new();
    private static readonly Dictionary<nint, (nint Context, nint Callback)> RadioWatches = [];
    private static readonly HashSet<string> Powered = new(StringComparer.Ordinal);
    private static nint _nextWatch;
    private static bool _watchingRadios;

    /// <summary>
    /// Calls <paramref name="callback"/> with <see cref="CmNotifyActionInterfaceArrival"/> whenever a BlueZ adapter
    /// powers on (switched on, plugged in, resumed). Returns a handle for <see cref="CM_Unregister_Notification"/>.
    /// </summary>
    public static unsafe nint RegisterInterfaceNotification(in Guid interfaceClass, nint context, delegate* unmanaged[Stdcall]<nint, nint, int, byte*, uint, uint> callback)
    {
        lock (RadioLock)
        {
            nint handle = ++_nextWatch;
            RadioWatches[handle] = (context, (nint)callback);
            if (!_watchingRadios)
            {
                _watchingRadios = true;
                BlueZ.Changed += OnBlueZChanged;
                _ = Task.Run(RefreshRadiosAsync);
            }
            return handle;
        }
    }

    public static uint CM_Unregister_Notification(nint notify)
    {
        lock (RadioLock)
        {
            RadioWatches.Remove(notify);
        }
        return 0;
    }

    /// <summary>Upstream reads Windows' pairing date to spot re-pairing; BlueZ has none, so the stored keys stay.</summary>
    public static DateTime? GetDeviceInstallDate(ulong address) => null;

    public static bool HasDeviceEntryDated(ulong address, DateTime date) => true;

    /// <summary>
    /// Upstream encrypts the proximity keys with DPAPI. Linux has no per-user equivalent without a keyring daemon, so the
    /// bytes pass through and the data folder is made private to the user instead (<see cref="LinuxPaths.Secure"/>).
    /// </summary>
    public static byte[]? Dpapi(ReadOnlySpan<byte> data, bool protect) => data.ToArray();

    /// <summary>Disconnects the pair (Device1.Disconnect); HRESULT-style result like the upstream helper.</summary>
    public static int wpn_bt_disconnect_audio(ulong btAddress)
    {
        try
        {
            if (BlueZ.FindDeviceAsync(btAddress).GetAwaiter().GetResult() is not { } device)
            {
                return EFail;
            }
            BlueZ.CallAsync(device.Path, BlueZ.DeviceInterface, "Disconnect").GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Log.Warn("bluez", $"disconnect {Mac.Format(btAddress)} failed: {e.Message}");
            return EFail;
        }
    }

    private static void OnBlueZChanged(string path)
    {
        if (path.Length == 0 || path.Count(c => c == '/') == 3)
        {
            // An adapter object (/org/bluez/hciN) or the whole tree.
            _ = Task.Run(RefreshRadiosAsync);
        }
    }

    private static async Task RefreshRadiosAsync()
    {
        List<string> powered;
        try
        {
            powered = await BlueZ.GetPoweredAdaptersAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn("bluez", $"adapter list failed: {e.Message}");
            return;
        }
        List<(nint Handle, nint Context, nint Callback)> notify = [];
        lock (RadioLock)
        {
            bool arrived = powered.Any(p => !Powered.Contains(p));
            Powered.Clear();
            Powered.UnionWith(powered);
            if (arrived)
            {
                notify.AddRange(RadioWatches.Select(w => (w.Key, w.Value.Context, w.Value.Callback)));
            }
        }
        foreach ((nint handle, nint context, nint callback) in notify)
        {
            Notify(handle, context, callback);
        }
    }

    private static unsafe void Notify(nint handle, nint context, nint callback) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, int, byte*, uint, uint>)callback)(handle, context, CmNotifyActionInterfaceArrival, null, 0);
}
