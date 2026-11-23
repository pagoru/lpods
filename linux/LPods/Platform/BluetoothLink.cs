using LPods.Platform;

namespace WPods.Core;

/// <summary>A paired AirPods device; ProductId comes from its Device ID record (0 when absent), known before connecting.</summary>
internal readonly record struct LinkDevice(ulong Address, string Name, bool Up, ushort ProductId);

/// <summary>
/// Linux stand-in for upstream's BluetoothLink: paired AirPods and their connection state, from BlueZ. Every BlueZ
/// change re-reads the device list and raises <see cref="LinkChanged"/> / <see cref="DevicesChanged"/> for what
/// differs. Events fire on thread-pool threads.
/// </summary>
internal sealed class BluetoothLink : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Dictionary<ulong, BlueZDevice> _devices = [];
    private bool _disposed;

    public event Action<ulong, bool>? LinkChanged;

    public event Action? DevicesChanged;

    /// <summary>Reads the initial state (no events for it), then follows BlueZ.</summary>
    public void Start()
    {
        try
        {
            RefreshAsync(raiseEvents: false).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Error("link", "BlueZ not reachable; is bluetooth.service running?", e);
        }
        BlueZ.Changed += OnBlueZChanged;
        Log.Info("link", $"start: {_devices.Count} paired AirPods");
    }

    public bool IsUp(ulong address)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(address, out BlueZDevice? device) && device.Connected;
        }
    }

    public bool IsListed(ulong address)
    {
        lock (_lock)
        {
            return _devices.ContainsKey(address);
        }
    }

    public ulong FirstUp()
    {
        lock (_lock)
        {
            return _devices.Values.FirstOrDefault(d => d.Connected)?.Address ?? 0;
        }
    }

    public void Recheck(ulong address) => _ = Task.Run(() => RefreshAsync(raiseEvents: true));

    public ushort ProductId(ulong address)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(address, out BlueZDevice? device) && device.Vendor == BlueZDevice.AppleVendor ? device.Product : (ushort)0;
        }
    }

    public List<LinkDevice> Snapshot()
    {
        lock (_lock)
        {
            return [.. _devices.Values.Select(d => new LinkDevice(d.Address, d.Name, d.Connected, d.Vendor == BlueZDevice.AppleVendor ? d.Product : (ushort)0))];
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
        BlueZ.Changed -= OnBlueZChanged;
    }

    private void OnBlueZChanged(string path)
    {
        // Device objects are /org/bluez/hciN/dev_XX_..; adapters matter too (powering off drops every link).
        if (path.Length == 0 || path.StartsWith("/org/bluez/", StringComparison.Ordinal))
        {
            _ = Task.Run(() => RefreshAsync(raiseEvents: true));
        }
    }

    private async Task RefreshAsync(bool raiseEvents)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<BlueZDevice> all;
            try
            {
                all = await BlueZ.GetDevicesAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Warn("link", $"BlueZ device list failed: {e.Message}");
                return;
            }
            // A device on two adapters: prefer the connected entry.
            Dictionary<ulong, BlueZDevice> next = all.Where(d => d.IsAirPods && !d.RandomAddress)
                .GroupBy(d => d.Address)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.Connected).First());
            List<(ulong Address, bool Up)> links = [];
            bool listChanged;
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
                foreach ((ulong address, BlueZDevice device) in next)
                {
                    bool wasUp = _devices.TryGetValue(address, out BlueZDevice? old) && old.Connected;
                    if (device.Connected != wasUp)
                    {
                        links.Add((address, device.Connected));
                    }
                }
                foreach ((ulong address, BlueZDevice old) in _devices)
                {
                    if (!next.ContainsKey(address) && old.Connected)
                    {
                        links.Add((address, false));
                    }
                }
                listChanged = !next.Keys.ToHashSet().SetEquals(_devices.Keys)
                    || next.Any(d => _devices.TryGetValue(d.Key, out BlueZDevice? old) && old.Name != d.Value.Name);
                _devices = next;
            }
            if (!raiseEvents)
            {
                return;
            }
            if (listChanged)
            {
                Log.Info("link", $"paired AirPods changed: {next.Count}");
                DevicesChanged?.Invoke();
            }
            foreach ((ulong address, bool up) in links)
            {
                Log.Info("link", $"{Mac.Format(address)} link {(up ? "up" : "down")}");
                LinkChanged?.Invoke(address, up);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
