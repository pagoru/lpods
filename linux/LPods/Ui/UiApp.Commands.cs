using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LPods.Platform;
using WPods.Core;
using WPods.Nearby;
using WPods.Ui;

namespace LPods.Ui;

// Commands from the main page, and the connect and disconnect flows.
internal sealed partial class UiApp
{
    private static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PageRetryAfter = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    private const string SupportUrl = "https://github.com/pagoru/lpods#readme";

    // User-initiated connects in progress, their cancel sources and cancel requests.
    private readonly HashSet<ulong> _connecting = [];
    private readonly Dictionary<ulong, CancellationTokenSource> _connectCancels = [];
    private readonly HashSet<ulong> _cancelled = [];
    // Link-up and attach waits of user connects, completed from core threads.
    private readonly Lock _linkLock = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _linkWaits = [];
    private readonly Dictionary<ulong, TaskCompletionSource> _attachWaits = [];

    private void OnMainMessage(WebPane pane, PageMessage m)
    {
        if (_main is not { } main || main.Pane != pane)
        {
            return;
        }
        Log.Info("ui", "main command " + Describe(m));
        ulong id = Mac.TryParse(m.Id, out ulong address) ? address : 0;
        switch (m.Type)
        {
            case "ready":
                if (_mainShown)
                {
                    SendMainInit();
                }
                break;
            case "connect" when id != 0:
                Connect(id);
                break;
            case "cancel" when id != 0:
                Cancel(id);
                break;
            case "disconnect" when id != 0:
                Disconnect(id, reconnect: false);
                break;
            case "reconnect" when id != 0:
                Disconnect(id, reconnect: true);
                break;
            case "setListeningMode" when MessageWriter.TryParse(m.Mode, out ListeningMode mode):
                RunCommand((s, ct) => s.SetListeningModeAsync(mode, ct), Dirty.ListeningMode);
                break;
            case "setToggle" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                Toggle(m.Name, m.Value.GetBoolean());
                break;
            case "setHeartRate" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                RunCommand((s, _) => new ValueTask<bool>(s.SetHeartRateAsync(on)), Dirty.HeartRate);
                break;
            }
            case "setHearingAid" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                RunCommand((s, ct) => new ValueTask<bool>(s.SetHearingAidAsync(on, ct)), Dirty.Settings);
                break;
            }
            case "setMediaAssist" when m.Name is "calls" or "media" && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                bool calls = m.Name == "calls";
                RunCommand((s, ct) => new ValueTask<bool>(calls ? s.SetMediaAssistCallsAsync(on, ct) : s.SetMediaAssistMediaAsync(on, ct)), Dirty.Settings);
                break;
            }
            case "setMicSide" when MessageWriter.TryParse(m.Side, out MicSide side):
                RunCommand((s, ct) => s.SetMicSideAsync(side, ct), Dirty.Settings);
                break;
            case "setPressSpeed" when m.Value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(m.Value.GetString(), out PressSpeed speed):
                RunCommand((s, ct) => s.SetPressSpeedAsync(speed, ct), Dirty.Settings);
                break;
            case "setHoldDuration" when m.Value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(m.Value.GetString(), out HoldDuration hold):
                RunCommand((s, ct) => s.SetHoldDurationAsync(hold, ct), Dirty.Settings);
                break;
            case "setToneVolume" when m.Value.ValueKind == JsonValueKind.Number:
            {
                // The AirPods reject tone volumes below 15%.
                byte volume = (byte)Math.Clamp(Math.Round(m.Value.GetDouble()), 15, 100);
                RunCommand((s, ct) => s.SetToneVolumeAsync(volume, ct), Dirty.Settings);
                break;
            }
            case "setHostSetting" when id != 0:
                SetHostSetting(id, m.Name, m.Value);
                break;
            case "setStemModes" when id != 0 && m.Modes is not null:
                SetStemModes(id, m.Modes);
                break;
            case "setTheme" when m.Theme is "light" or "dark" or "auto":
                SavePrefs(_prefs with { Theme = m.Theme });
                main.SetTheme(MainBackground, Dark);
                break;
            case "setAccent" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SavePrefs(_prefs with { UseAccent = m.Value.GetBoolean() });
                break;
            case "setStartup" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                if (!Desktop.SetAutostart(m.Value.GetBoolean()))
                {
                    SendStartupPref();
                }
                break;
            case "setLanguage" when m.Code is not null:
                SetLanguage(m.Code);
                break;
            case "openBluetoothSettings" or "openAddDevice":
                Desktop.OpenBluetoothSettings();
                break;
            case "openSupport":
                Desktop.Open(SupportUrl);
                break;
            case "openLegal" when m.Name is "license" or "notices":
                OpenLegal(m.Name == "license" ? "LICENSE.txt" : "THIRD-PARTY-NOTICES.txt");
                break;
            case "getStarted":
                SavePrefs(_prefs with { FirstRunDone = true });
                // The welcome page fitted the window to itself; the app goes back to the user's height.
                main.SetHeight(_prefs.WindowHeight);
                break;
            case "minimize":
                main.Minimize();
                break;
            case "close":
                HideMain();
                break;
            case "beginDrag":
                main.BeginDrag();
                break;
            case "fitHeight" when !_prefs.FirstRunDone && m.Height > 0:
                main.FitContentHeight(m.Height);
                break;
        }
    }

    // Command type plus its set fields, for the log (none are secret).
    private static string Describe(PageMessage m)
    {
        var text = new StringBuilder(m.Type);
        AppendField(text, "id", m.Id);
        AppendField(text, "mode", m.Mode);
        AppendField(text, "name", m.Name);
        AppendField(text, "side", m.Side);
        AppendField(text, "theme", m.Theme);
        AppendField(text, "code", m.Code);
        if (m.Value.ValueKind != JsonValueKind.Undefined)
        {
            AppendField(text, "value", m.Value.GetRawText());
        }
        if (m.Ids is not null)
        {
            AppendField(text, "ids", string.Join(',', m.Ids));
        }
        if (m.Height != 0)
        {
            AppendField(text, "height", m.Height.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private static void AppendField(StringBuilder text, string name, string? value)
    {
        if (value is not null)
        {
            text.Append(' ').Append(name).Append('=').Append(value);
        }
    }

    // ---- Connect / disconnect (only user clicks may page the AirPods) ----

    private async void Connect(ulong address)
    {
        if (await ConnectCoreAsync(address) is { } failure)
        {
            SendToast(failure, address);
        }
    }

    // The toast id of the failure, or null (connected, cancelled or already connecting).
    private async Task<string?> ConnectCoreAsync(ulong address)
    {
        if (!_connecting.Add(address))
        {
            Log.Info("ui", $"connect {Mac.Format(address)}: already connecting");
            return null;
        }
        _cancelled.Remove(address);
        SendDevices();
        LogConn();
        string? failure = null;
        try
        {
            // Right after a Disconnect, the link may still be dropping; joining it would end disconnected.
            Task pending = _connections.DisconnectFinished(address);
            if (!pending.IsCompleted)
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: waiting for the disconnect still under way");
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch (TimeoutException)
                {
                    Log.Info("ui", $"connect {Mac.Format(address)}: the disconnect did not end within 8 s; connecting anyway");
                }
            }
            if (_offerByAddress.TryGetValue(address, out NearbyOffer? offer) && offer.State is OfferState.Offer or OfferState.Failed)
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: through the nearby offer ({offer.State})");
                await _nearby.ConnectAsync(address);
                failure = _connections.Session?.Address == address ? null : "connectFailed";
            }
            else if (Find(address) is not { LinkUp: true } && (await BlueZ.GetPoweredAdaptersAsync()).Count == 0)
            {
                failure = "bluetoothOff";
            }
            else if (!await ConnectAudioAsync(address))
            {
                failure = "connectFailed";
            }
        }
        finally
        {
            _connecting.Remove(address);
            SendDevices();
        }
        bool cancelled = _cancelled.Remove(address);
        Log.Info("ui", $"connect {Mac.Format(address)}: done, failure={failure ?? "none"}, cancelled={cancelled}");
        LogConn();
        return cancelled ? null : failure;
    }

    /// <summary>
    /// Brings the link up if needed, then attaches, within <see cref="LinkWait"/>. An attach by the connection manager
    /// itself also counts; false on timeout, attach failure or Cancel.
    /// </summary>
    private async Task<bool> ConnectAudioAsync(ulong address)
    {
        using var cancel = new CancellationTokenSource(LinkWait);
        var link = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectCancels[address] = cancel;
        lock (_linkLock)
        {
            _linkWaits[address] = link;
            _attachWaits[address] = attached;
        }
        try
        {
            if (Find(address) is not { LinkUp: true })
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: link down, asking BlueZ to connect");
                int hr = await Task.Run(() => NearbyNative.wpn_bt_connect_audio(address), cancel.Token);
                Log.Info("ui", $"connect {Mac.Format(address)}: connect hr=0x{hr:X8}, waiting for link");
                try
                {
                    await link.Task.WaitAsync(PageRetryAfter, cancel.Token);
                }
                catch (TimeoutException)
                {
                    cancel.CancelAfter(LinkWait);
                    hr = await Task.Run(() => NearbyNative.wpn_bt_connect_audio(address), cancel.Token);
                    Log.Info("ui", $"connect {Mac.Format(address)}: no link after {PageRetryAfter.TotalSeconds:0} s, connect again hr=0x{hr:X8}");
                }
                await link.Task.WaitAsync(cancel.Token);
                Log.Info("ui", $"connect {Mac.Format(address)}: link up");
            }
            // Off the UI thread: the attach waits up to 10 s for the AirPods' answer.
            if (_connections.Session?.Address == address || await Task.Run(() => _connections.ConnectAsync(address)))
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: attached");
                return true;
            }
            Log.Info("ui", $"connect {Mac.Format(address)}: ConnectAsync false, waiting for an attach until the deadline");
            await attached.Task.WaitAsync(cancel.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            bool attachedNow = _connections.Session?.Address == address;
            Log.Info("ui", $"connect {Mac.Format(address)}: timed out or cancelled, attached={attachedNow}");
            return attachedNow;
        }
        finally
        {
            _connectCancels.Remove(address);
            lock (_linkLock)
            {
                _linkWaits.Remove(address);
                _attachWaits.Remove(address);
            }
        }
    }

    private void Cancel(ulong address)
    {
        if (!_connecting.Contains(address))
        {
            return;
        }
        Log.Info("ui", $"connect {Mac.Format(address)}: cancel requested");
        _cancelled.Add(address);
        if (_connectCancels.TryGetValue(address, out CancellationTokenSource? cancel))
        {
            cancel.Cancel();
        }
        else
        {
            _nearby.CancelConnect();
        }
    }

    // The core detaches at once, so "notConnected" shows immediately. A failed disconnect means the pair is attached
    // again: toast and resend the real devices and state.
    private async void Disconnect(ulong address, bool reconnect)
    {
        if (_connections.Session?.Address == address)
        {
            bool ok = await _connections.DisconnectAsync();
            Log.Info("ui", $"disconnect {Mac.Format(address)}: {ok}, reconnect={reconnect}");
            if (!ok)
            {
                SendToast("disconnectFailed", address);
                SendRealState();
            }
        }
        if (reconnect)
        {
            Connect(address);
        }
    }

    private KnownDevice? Find(ulong address) => _connections.GetDevices().Find(d => d.Address == address);

    // ---- Device commands: success shows through the core's Changed event; failure sends the current value back ----

    private async void SetStemModes(ulong address, string[] names)
    {
        if (_connections.Session is { } session && session.Address == address && MessageWriter.TryParseStemModes(names, out StemModes modes))
        {
            bool ok = false;
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                ok = await session.SetStemModesAsync(modes, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // No reply in time.
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Log.Warn("ui", $"setStemModes {Mac.Format(address)} [{string.Join(',', names)}] rejected: {e.Message}");
                ok = true;
            }
            if (!ok)
            {
                SendToast("noResponse", address);
            }
        }
        _devices = _connections.GetDevices();
        SendDevices(force: true);
    }

    private void Toggle(string? name, bool on)
    {
        Func<AapSession, CancellationToken, ValueTask<bool>>? command = name switch
        {
            "earDetection" => (s, ct) => s.SetEarDetectionAsync(on, ct),
            "conversationAwareness" => (s, ct) => s.SetConversationAwarenessAsync(on, ct),
            "personalizedVolume" => (s, ct) => s.SetPersonalizedVolumeAsync(on, ct),
            "oneBudAnc" => (s, ct) => s.SetOneBudAncAsync(on, ct),
            "allowOff" => (s, ct) => s.SetAllowOffAsync(on, ct),
            _ => null,
        };
        if (command is not null)
        {
            RunCommand(command, Dirty.Settings);
        }
    }

    private async void RunCommand(Func<AapSession, CancellationToken, ValueTask<bool>> command, Dirty field)
    {
        AapSession? session = _connections.Session;
        bool ok = false;
        if (session is not null)
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                ok = await command(session, timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
            {
                // No reply in time, or unsupported by the device (not enrolled, no heart-rate sensor).
                Log.Warn("ui", $"device command failed: {e.GetType().Name} {e.Message}");
            }
        }
        if (ok)
        {
            return;
        }
        Log.Warn("ui", $"device command not applied (session={(session is null ? "none" : Mac.Format(session.Address))})");
        if (_connections.Session is { } current && MainLive)
        {
            SendStateDeltas(field, current.State, force: true);
        }
        SendToast("noResponse", session?.Address ?? 0);
    }

    // ---- Host settings and app prefs ----

    // Only the conversation awareness volume (duckPercent) has an effect here; the other Windows audio settings are hidden.
    private async void SetHostSetting(ulong address, string? name, JsonElement value)
    {
        if (name == "duckPercent" && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int duck) && duck is >= 10 and <= 90
            && _connections.Session?.Address == address)
        {
            try
            {
                await _connections.SetHostSettingsAsync(address, _settings.Get(address) with { DuckPercent = duck });
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                Log.Warn("ui", $"host setting change failed: {e.Message}");
            }
        }
        SendHostSettings(address);
    }

    private void SendStartupPref()
    {
        Utf8JsonWriter w = _writer.Begin("prefs");
        w.WriteBoolean("startup", Desktop.AutostartEnabled);
        PostMain(_writer.End());
    }

    private static void OpenLegal(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path))
        {
            Log.Warn("ui", $"{path} missing");
            return;
        }
        Desktop.Open(path);
    }
}
