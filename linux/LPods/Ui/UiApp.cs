using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using LPods.Platform;
using WPods.Core;
using WPods.Nearby;
using WPods.Ui;

namespace LPods.Ui;

/// <summary>
/// Owns the UI of the daemon: the main window (upstream's web pages in WebKitGTK), the tray icon and the nearby offers.
/// A port of upstream's TrayApp: core events only set dirty flags, and one posted <see cref="Flush"/> per burst sends the
/// changes to the page from the UI thread. Wherever the window shows something upstream's host would send that this one
/// cannot (mic modes, mixer, hotkeys, updates), the message is left out and linux.css hides the control.
/// </summary>
internal sealed partial class UiApp : IDisposable
{
    [Flags]
    private enum Dirty
    {
        None = 0,
        Session = 1 << 0,
        Devices = 1 << 1,
        Battery = 1 << 2,
        Ear = 1 << 3,
        ListeningMode = 1 << 4,
        Settings = 1 << 5,
        HeartRate = 1 << 6,
        Status = 1 << 7,
        Offers = 1 << 9,
        HostSettings = 1 << 10,
        Toast = 1 << 11,
        Caps = 1 << 12,
        Checking = 1 << 13,
    }

    // Main-page messages whose last sent JSON is kept to skip sends that change nothing.
    private enum Sent
    {
        Devices,
        Battery,
        Ear,
        ListeningMode,
        Settings,
        HeartRate,
        Caps,
        Checking,
        Count,
    }

    private sealed record ToastRequest(string Id, ulong Device, string? Code);

    // Match tokens.css bg-window, so the window never flashes another color before the page paints.
    private const uint MainLight = 0xF2F2F4;
    private const uint MainDark = 0x1C1C1F;
    private static readonly TimeSpan DestroyAfter = TimeSpan.FromSeconds(60);

    private readonly SettingsStore _settings;
    private readonly ConnectionManager _connections;
    private readonly NearbyService _nearby;
    private readonly Localization _locales;
    private readonly GtkLoop _loop;
    private readonly DesktopTheme _theme;
    private readonly Action _quit;
    private readonly MessageWriter _writer = new();
    private readonly string?[] _lastSent = new string?[(int)Sent.Count];
    private readonly Lock _hostSettingsLock = new();
    private readonly HashSet<ulong> _hostSettingsDirty = [];
    private readonly Dictionary<ulong, NearbyOffer> _offerByAddress = [];
    private readonly ConcurrentQueue<ToastRequest> _toasts = new();
    private readonly Hotkeys _hotkeys = new();

    private UiPrefs _prefs;
    private ResolvedLocale _locale;
    private int _dirty;

    // UI-thread state, refreshed by Flush.
    private AapSession? _session;
    private List<KnownDevice> _devices = [];
    private IReadOnlyList<NearbyOffer> _offers = [];
    private MainWindow? _main;
    private bool _mainShown;
    private bool _mainInitSent;
    private CancellationTokenSource? _destroyTimer;
    private string _connLogged = "";
    private bool _exiting;

    public UiApp(SettingsStore settings, ConnectionManager connections, NearbyService nearby, Localization locales, GtkLoop loop, DesktopTheme theme, Action quit)
    {
        _settings = settings;
        _connections = connections;
        _nearby = nearby;
        _locales = locales;
        _loop = loop;
        _theme = theme;
        _quit = quit;
        _prefs = settings.Ui;
        _locale = LinuxLabels.Resolve(locales, _prefs.Language);
        Log.Info("ui", $"start: language={_prefs.Language} -> {_locale.Code}, theme={_prefs.Theme}");

        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        connections.DevicesChanged += () => MarkDirty(Dirty.Devices);
        connections.LinkChanged += OnLinkChanged;
        connections.HandsFreeFailed += (address, _) => QueueToast("handsFreeFailed", address);
        settings.HostSettingsChanged += OnHostSettingsChanged;
        nearby.OffersChanged += () => MarkDirty(Dirty.Offers);
        nearby.NearbyChanged += () => MarkDirty(Dirty.Devices);
        theme.Changed += () => _loop.Post(static s => ((UiApp)s!).OnDesktopThemeChanged(), this);

        _devices = connections.GetDevices();
        StartTray();
    }

    private bool Dark => _prefs.Theme == "dark" || (_prefs.Theme == "auto" && _theme.Dark);

    private uint MainBackground => Dark ? MainDark : MainLight;

    private AccentRamp? Accent => _prefs.UseAccent ? _theme.Accent : null;

    private bool MainLive => _main is { Pane.Ready: true } && _mainShown;

    private string Text(string key) => _locale.Text(key);

    // ---- Thread-pool side: record what changed, post one flush per burst ----

    private void MarkDirty(Dirty dirty)
    {
        if (Interlocked.Or(ref _dirty, (int)dirty) == 0)
        {
            _loop.Post(static s => ((UiApp)s!).Flush(), this);
        }
    }

    private void QueueToast(string id, ulong device, string? code = null)
    {
        Log.Info("ui", $"toast queued: {id} device={Mac.Format(device)}{(code is null ? "" : " code=" + code)}");
        _toasts.Enqueue(new ToastRequest(id, device, code));
        MarkDirty(Dirty.Toast);
    }

    private void OnAttached(AapSession session)
    {
        session.Changed += OnSessionChanged;
        lock (_linkLock)
        {
            if (_attachWaits.Remove(session.Address, out TaskCompletionSource? wait))
            {
                Log.Info("ui", $"attached {Mac.Format(session.Address)}: user connect wait completed");
                wait.TrySetResult();
            }
        }
        MarkDirty(Dirty.Session);
    }

    private void OnDetached(AapSession session)
    {
        session.Changed -= OnSessionChanged;
        MarkDirty(Dirty.Session);
    }

    private void OnSessionChanged(DeviceChange change)
    {
        Dirty dirty = Dirty.None;
        if ((change & DeviceChange.Connection) != 0)
        {
            dirty |= Dirty.Status | Dirty.ListeningMode | Dirty.Settings;
        }
        if ((change & DeviceChange.Identity) != 0)
        {
            dirty |= Dirty.Devices;
        }
        if ((change & DeviceChange.Battery) != 0)
        {
            dirty |= Dirty.Battery;
        }
        if ((change & DeviceChange.Ear) != 0)
        {
            dirty |= Dirty.Ear;
        }
        if ((change & DeviceChange.ListeningMode) != 0)
        {
            dirty |= Dirty.ListeningMode;
        }
        if ((change & DeviceChange.Settings) != 0)
        {
            dirty |= Dirty.Settings;
        }
        if ((change & DeviceChange.HeartRate) != 0)
        {
            dirty |= Dirty.HeartRate;
        }
        if ((change & DeviceChange.Capabilities) != 0)
        {
            dirty |= Dirty.Caps;
        }
        if ((change & DeviceChange.InitialReport) != 0)
        {
            dirty |= Dirty.Checking | Dirty.ListeningMode | Dirty.Settings;
        }
        if ((change & DeviceChange.StemModes) != 0)
        {
            dirty |= Dirty.Devices;
        }
        if (dirty != Dirty.None)
        {
            MarkDirty(dirty);
        }
    }

    private void OnLinkChanged(ulong address, bool up)
    {
        if (up)
        {
            lock (_linkLock)
            {
                if (_linkWaits.Remove(address, out TaskCompletionSource? wait))
                {
                    Log.Info("ui", $"link up {Mac.Format(address)}: user connect link wait completed");
                    wait.TrySetResult();
                }
            }
        }
        MarkDirty(Dirty.Devices);
    }

    private void OnHostSettingsChanged(ulong address, HostSettings settings)
    {
        lock (_hostSettingsLock)
        {
            _hostSettingsDirty.Add(address);
        }
        MarkDirty(Dirty.HostSettings);
    }

    // ---- UI thread ----

    private void Flush()
    {
        var dirty = (Dirty)Interlocked.Exchange(ref _dirty, 0);
        if (_exiting)
        {
            return;
        }
        AapSession? session = _connections.ShownSession;
        if (session != _session)
        {
            Log.Info("ui", $"session {(_session is null ? "none" : Mac.Format(_session.Address))} -> {(session is null ? "none" : Mac.Format(session.Address))}");
            _session = session;
            dirty |= Dirty.Session | Dirty.Devices | Dirty.Status;
        }
        if ((dirty & Dirty.Offers) != 0)
        {
            _offers = _nearby.Offers;
            _offerByAddress.Clear();
            foreach (NearbyOffer offer in _offers)
            {
                _offerByAddress[offer.Address] = offer;
            }
            dirty |= Dirty.Devices;
        }
        if ((dirty & Dirty.Devices) != 0)
        {
            _devices = _connections.GetDevices();
        }
        DeviceState? state = session?.State;
        if (state is not null && (dirty & Dirty.Status) != 0 && state.Status == SessionStatus.NoResponse)
        {
            SendToast("noResponse", session!.Address);
        }
        UpdateTray();
        if (MainLive)
        {
            SendMainDeltas(dirty, state);
        }
        if ((dirty & Dirty.Offers) != 0)
        {
            UpdateOffers();
        }
        if ((dirty & Dirty.Toast) != 0)
        {
            while (_toasts.TryDequeue(out ToastRequest? toast))
            {
                SendToast(toast.Id, toast.Device, toast.Code);
            }
        }
        if ((dirty & (Dirty.Devices | Dirty.Offers | Dirty.Session | Dirty.Status)) != 0)
        {
            LogConn();
        }
    }

    private void LogConn()
    {
        string summary = string.Join(' ', _devices.Select(d => $"{Mac.Format(d.Address)}={Conn(d, out _)}"))
            + $" attached={(_session is null ? "none" : Mac.Format(_session.Address))} offers={_offers.Count}";
        if (summary != _connLogged)
        {
            _connLogged = summary;
            Log.Info("ui", "conn: " + summary);
        }
    }

    private void SendMainDeltas(Dirty dirty, DeviceState? state)
    {
        if ((dirty & Dirty.Devices) != 0)
        {
            SendDevices();
        }
        if ((dirty & Dirty.Session) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("state");
            MessageWriter.WriteState(w, state);
            PostMain(_writer.End());
            Array.Clear(_lastSent, (int)Sent.Battery, (int)Sent.Count - (int)Sent.Battery);
        }
        else if (state is not null)
        {
            SendStateDeltas(dirty, state, force: false);
        }
        if ((dirty & Dirty.HostSettings) != 0)
        {
            ulong[] addresses;
            lock (_hostSettingsLock)
            {
                addresses = [.. _hostSettingsDirty];
                _hostSettingsDirty.Clear();
            }
            foreach (ulong address in addresses)
            {
                SendHostSettings(address);
            }
        }
    }

    private void SendDevices(bool force = false)
    {
        Utf8JsonWriter w = _writer.Begin("devices");
        WriteDevices(w);
        SendMain(Sent.Devices, _writer.End(), force);
    }

    /// <summary>Resends devices and state even if unchanged, so the page drops an optimistic view.</summary>
    private void SendRealState()
    {
        _lastSent[(int)Sent.Devices] = null;
        MarkDirty(Dirty.Session | Dirty.Devices);
    }

    private void SendStateDeltas(Dirty dirty, DeviceState state, bool force)
    {
        if ((dirty & Dirty.Battery) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("battery");
            MessageWriter.WriteBatteries(w, state);
            SendMain(Sent.Battery, _writer.End(), force);
        }
        if ((dirty & Dirty.Ear) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("ear");
            MessageWriter.WriteEar(w, state);
            SendMain(Sent.Ear, _writer.End(), force);
        }
        if ((dirty & Dirty.ListeningMode) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("listeningMode");
            MessageWriter.WriteListeningMode(w, state);
            SendMain(Sent.ListeningMode, _writer.End(), force);
        }
        if ((dirty & Dirty.Settings) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("settings");
            w.WriteStartObject("settings");
            MessageWriter.WriteSettingFields(w, state);
            w.WriteEndObject();
            SendMain(Sent.Settings, _writer.End(), force);
        }
        if ((dirty & Dirty.HeartRate) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("heartRate");
            MessageWriter.WriteHeartRate(w, state);
            SendMain(Sent.HeartRate, _writer.End(), force);
        }
        if ((dirty & Dirty.Caps) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("caps");
            MessageWriter.WriteCaps(w, state);
            SendMain(Sent.Caps, _writer.End(), force);
        }
        if ((dirty & (Dirty.Status | Dirty.Checking)) != 0)
        {
            Utf8JsonWriter w = _writer.Begin("checking");
            MessageWriter.WriteChecking(w, state);
            SendMain(Sent.Checking, _writer.End(), force);
        }
    }

    private void SendMain(Sent kind, string json, bool force)
    {
        if (!force && _lastSent[(int)kind] == json)
        {
            return;
        }
        _lastSent[(int)kind] = json;
        PostMain(json);
    }

    private void PostMain(string json)
    {
        if (MainLive)
        {
            _main!.Pane.Post(json);
        }
    }

    private void SendHostSettings(ulong address)
    {
        Utf8JsonWriter w = _writer.Begin("hostSettings");
        w.WriteString("id", Mac.Format(address));
        MessageWriter.WriteHost(w, _settings.Get(address));
        w.WriteBoolean("handsFreeApplying", false);
        PostMain(_writer.End());
    }

    private void SendToast(string id, ulong device, string? code = null)
    {
        if (!MainLive)
        {
            Log.Info("ui", $"toast {id} device={Mac.Format(device)} dropped (main window not showing)");
            return;
        }
        Log.Info("ui", $"toast {id} device={Mac.Format(device)}{(code is null ? "" : " code=" + code)} shown");
        Utf8JsonWriter w = _writer.Begin("toast");
        w.WriteString("toast", id);
        if (device != 0)
        {
            w.WriteString("device", Mac.Format(device));
        }
        else
        {
            w.WriteNull("device");
        }
        if (code is not null)
        {
            w.WriteString("code", code);
        }
        PostMain(_writer.End());
    }

    private void WriteDevices(Utf8JsonWriter w)
    {
        w.WriteStartArray("devices");
        foreach (KnownDevice device in _devices)
        {
            string conn = Conn(device, out NearbyOffer? offer);
            MessageWriter.WriteDevice(w, device, conn, conn == "elsewhere" ? offer?.Batteries : null, _settings.Get(device.Address),
                _settings.GetPodSettings(device.Address), _settings.GetEarDetection(device.Address),
                _settings.GetAllowOff(device.Address), _settings.GetMicSide(device.Address));
        }
        w.WriteEndArray();
        if (_session is { } session)
        {
            w.WriteString("attached", Mac.Format(session.Address));
        }
        else
        {
            w.WriteNull("attached");
        }
    }

    private string Conn(KnownDevice device, out NearbyOffer? offer)
    {
        _offerByAddress.TryGetValue(device.Address, out offer);
        if (device.Attached)
        {
            return "connected";
        }
        if (_connecting.Contains(device.Address) || offer is { State: OfferState.Connecting })
        {
            return "connecting";
        }
        switch (device.Idle)
        {
            case IdleReason.UserDisconnected:
                return "notConnected";
            case IdleReason.AttachGaveUp:
                return "attachFailed";
        }
        if (device.LinkUp)
        {
            return _connections.IsRetrying(device.Address) ? "reconnecting" : "connecting";
        }
        // Advertising nearby while its link is down: connected to another device.
        return _nearby.IsNearby(device.Address) ? "elsewhere" : "notConnected";
    }

    // ---- Window ----

    /// <summary>Shows the main window (creating it), from any thread.</summary>
    public void Activate() => _loop.Post(static s => ((UiApp)s!).ShowMain(), this);

    private void ShowMain()
    {
        if (_exiting)
        {
            return;
        }
        _destroyTimer?.Cancel();
        if (_main is not { } window)
        {
            Log.Info("ui", "main window: create");
            CreateMain();
            return;
        }
        if (!_mainShown)
        {
            Log.Info("ui", $"main window: show (page ready={window.Pane.Ready})");
            _mainShown = true;
            window.Show();
            if (window.Pane.Ready)
            {
                if (_mainInitSent)
                {
                    SendMainRefresh();
                }
                else
                {
                    SendMainInit();
                }
            }
            return;
        }
        window.Show();
    }

    private void CreateMain()
    {
        var pane = new WebPane(OnMainMessage);
        var window = new MainWindow(Text("app.name"), (int)_prefs.WindowHeight, MainBackground, pane);
        window.CloseRequested += HideMain;
        window.SetTheme(MainBackground, Dark);
        _main = window;
        _mainShown = true;
        pane.Load("index.html", MainBackground);
        window.Show();
    }

    private void HideMain()
    {
        if (_main is not { } window || !_mainShown)
        {
            return;
        }
        Log.Info("ui", "main window: hide");
        double height = window.Height;
        if (height >= MainWindow.MinHeight && Math.Round(height) != _prefs.WindowHeight && _prefs.FirstRunDone)
        {
            SavePrefs(_prefs with { WindowHeight = Math.Round(height) });
        }
        PostVisibility(false);
        _mainShown = false;
        window.Hide();
        // The hidden page is destroyed after a while: a WebKit process costs memory the tray app should not hold.
        var timer = new CancellationTokenSource();
        _destroyTimer = timer;
        _ = Task.Delay(DestroyAfter, timer.Token).ContinueWith(
            static (t, s) =>
            {
                if (!t.IsCanceled)
                {
                    var app = (UiApp)s!;
                    app._loop.Post(static a => ((UiApp)a!).DestroyIfHidden(), app);
                }
            },
            this, TaskScheduler.Default);
    }

    private void DestroyIfHidden()
    {
        if (!_mainShown)
        {
            DestroyMain();
        }
    }

    private void DestroyMain()
    {
        if (_main is not { } window)
        {
            return;
        }
        Log.Info("ui", "main window: destroy");
        _main = null;
        _mainShown = false;
        _mainInitSent = false;
        window.Destroy();
    }

    private void PostVisibility(bool visible)
    {
        Utf8JsonWriter w = _writer.Begin("visibility");
        w.WriteBoolean("visible", visible);
        _main?.Pane.Post(_writer.End());
    }

    private void SendMainInit()
    {
        Array.Clear(_lastSent);
        Utf8JsonWriter w = _writer.Begin("init");
        MessageWriter.WriteLocale(w, _locale);
        w.WriteStartArray("languages");
        foreach ((string code, LocaleFile file) in _locales.Files)
        {
            w.WriteStartObject();
            w.WriteString("code", code);
            w.WriteString("originalName", file.OriginalName);
            w.WriteString("engName", file.EngName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("language", _prefs.Language == "system" ? "" : _prefs.Language);
        w.WriteString("windowsLanguageName", NativeUi.NativeDisplayName(NativeUi.UserUiLanguage()));
        WritePrefs(w);
        w.WriteBoolean("startup", Desktop.AutostartEnabled);
        w.WriteBoolean("firstRun", !_prefs.FirstRunDone);
        w.WriteBoolean("adapterUnsupported", false);
        w.WriteBoolean("restartPending", false);
        WriteUpdate(w);
        WriteDevices(w);
        if (Selected() is { } selected)
        {
            w.WriteString("selected", Mac.Format(selected));
        }
        else
        {
            w.WriteNull("selected");
        }
        MessageWriter.WriteState(w, _session?.State);
        w.WriteString("mic", "notActive");
        MessageWriter.WriteMicCatalog(w);
        MessageWriter.WriteMicConfig(w, "micConfig", _settings.Mic);
        w.WriteStartObject("mixer");
        w.WriteStartArray("devices");
        w.WriteEndArray();
        w.WriteBoolean("enabled", false);
        w.WriteBoolean("hideDisconnected", false);
        w.WriteBoolean("hideDisabled", false);
        w.WriteEndObject();
        MessageWriter.WriteHotkeys(w, _hotkeys, false);
        w.WriteBoolean("visible", _mainShown);
        PostMain(_writer.End());
        _mainInitSent = true;
    }

    // Re-show of an initialized page: resends what may have changed while hidden, since hidden sends are dropped.
    private void SendMainRefresh()
    {
        Array.Clear(_lastSent);
        PostVisibility(true);
        SendMainDeltas(Dirty.Devices | Dirty.Session, _session?.State);
        Utf8JsonWriter w = _writer.Begin("prefs");
        w.WriteBoolean("startup", Desktop.AutostartEnabled);
        PostMain(_writer.End());
        w = _writer.Begin("accent");
        MessageWriter.WriteAccent(w, Accent);
        PostMain(_writer.End());
    }

    private ulong? Selected()
    {
        if (_session is { } session)
        {
            return session.Address;
        }
        ulong last = _connections.LastUsed;
        if (_devices.Exists(d => d.Address == last))
        {
            return last;
        }
        return _devices.Count > 0 ? _devices[0].Address : null;
    }

    private void WritePrefs(Utf8JsonWriter w)
    {
        w.WriteString("theme", _prefs.Theme);
        w.WriteBoolean("useAccent", _prefs.UseAccent);
        MessageWriter.WriteAccent(w, _theme.Accent);
    }

    private void WriteUpdate(Utf8JsonWriter w)
    {
        Version app = typeof(UiApp).Assembly.GetName().Version ?? new Version(0, 0, 0);
        w.WriteStartObject("update");
        w.WriteString("version", app.ToString(3));
        w.WriteNull("available");
        w.WriteString("status", "idle");
        w.WriteBoolean("checked", true);
        w.WriteBoolean("auto", false);
        w.WriteString("dismissed", _prefs.DismissedUpdate);
        w.WriteEndObject();
    }

    private void SavePrefs(UiPrefs prefs)
    {
        _prefs = prefs;
        try
        {
            _settings.SetUi(prefs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("ui", $"prefs not saved: {e.Message}");
        }
    }

    private void OnDesktopThemeChanged()
    {
        if (_exiting)
        {
            return;
        }
        Log.Info("ui", $"desktop theme changed: dark={_theme.Dark} accent={_theme.Accent?.Accent ?? "none"}");
        _main?.SetTheme(MainBackground, Dark);
        Utf8JsonWriter w = _writer.Begin("accent");
        MessageWriter.WriteAccent(w, _theme.Accent);
        PostMain(_writer.End());
    }

    // "" = same as the system. The page relabels in place; the tray reads labels on use.
    private void SetLanguage(string code)
    {
        string choice = code.Length == 0 ? "system" : code;
        if (choice != "system" && !_locales.Files.ContainsKey(choice))
        {
            return;
        }
        SavePrefs(_prefs with { Language = choice });
        _locale = LinuxLabels.Resolve(_locales, choice);
        Log.Info("ui", $"language {choice} -> {_locale.Code}");
        UpdateTray();
        Utf8JsonWriter w = _writer.Begin("labels");
        MessageWriter.WriteLocale(w, _locale);
        PostMain(_writer.End());
    }

    /// <summary>Closes the window and the tray (the daemon is exiting).</summary>
    public void Dispose()
    {
        _exiting = true;
        _loop.Send(static s =>
        {
            var app = (UiApp)s!;
            app._destroyTimer?.Cancel();
            app.DestroyMain();
            app.StopTray();
        }, this);
        _loop.Quit();
    }
}
