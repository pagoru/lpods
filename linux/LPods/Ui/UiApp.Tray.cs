using System.Globalization;
using System.Text;
using LPods.Platform;
using WPods.Core;
using WPods.Nearby;

namespace LPods.Ui;

// Tray icon and the nearby offers (notifications with a Connect button instead of upstream's popup window: Wayland
// gives an app no say over where its windows go).
internal sealed partial class UiApp
{
    private const string TrayIcon = "audio-headphones-symbolic";
    private static readonly string[] PartLabels = ["battery.left", "battery.right", "battery.case", "battery.max"];

    private Tray? _tray;
    // Notification of each offered pair, its last shown content, and the ones the user already acted on.
    private readonly Dictionary<ulong, uint> _offerNotes = [];
    private readonly Dictionary<ulong, string> _offerShown = [];
    private readonly HashSet<uint> _acted = [];
    private bool _offersRunning;
    private bool _offersAgain;

    private void StartTray()
    {
        _tray = new Tray();
        _tray.Activated += () => _loop.Post(static s => ((UiApp)s!).ToggleMain(), this);
        _tray.OpenClicked += Activate;
        _tray.StartupClicked += () => _loop.Post(static s => ((UiApp)s!).ToggleStartupFromTray(), this);
        _tray.QuitClicked += () => _quit();
        Notifications.ActionInvoked += OnNotificationAction;
        Notifications.Dismissed += OnNotificationDismissed;
        _ = _tray.StartAsync(TrayModelNow());
    }

    private void StopTray()
    {
        Notifications.ActionInvoked -= OnNotificationAction;
        Notifications.Dismissed -= OnNotificationDismissed;
        _tray?.Dispose();
        _tray = null;
    }

    private void UpdateTray() => _tray?.Update(TrayModelNow());

    private void ToggleMain()
    {
        if (_mainShown && _main is { IsActive: true })
        {
            HideMain();
        }
        else
        {
            ShowMain();
        }
    }

    private void ToggleStartupFromTray()
    {
        Desktop.SetAutostart(!Desktop.AutostartEnabled);
        SendStartupPref();
        UpdateTray();
    }

    // Title and menu lines: the name, then the reported parts ("Left 80%", "Right 79%", "Case 100% charging").
    private TrayModel TrayModelNow()
    {
        string title;
        var lines = new List<string>();
        if (_session?.State is { } state)
        {
            string name = state.Name ?? DeviceName(_session.Address);
            DeviceState battery = state.BatterySource;
            AppendPart(lines, "battery.left", battery.LeftBattery);
            AppendPart(lines, "battery.right", battery.RightBattery);
            AppendPart(lines, "battery.case", battery.CaseBattery);
            AppendPart(lines, "battery.max", battery.HeadphoneBattery);
            title = lines.Count == 0 ? name : name + " · " + string.Join(" · ", lines);
            lines.Insert(0, name);
        }
        else
        {
            title = Text("app.name") + " · " + Text("status.notConnected");
            lines.Add(Text("status.notConnected"));
        }
        return new TrayModel(title, TrayIcon, lines, Text("tray.open"), Text("tray.startup"), Desktop.AutostartEnabled, Text("tray.quit"));
    }

    private void AppendPart(List<string> lines, string label, Battery battery)
    {
        if (battery.Percent is not { } pct)
        {
            return;
        }
        var text = new StringBuilder(Text(label)).Append(' ')
            .Append(Text("battery.pct").Replace("{pct}", pct.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        if (battery.Status == BatteryStatus.Charging)
        {
            text.Append(' ').Append(Text("battery.charging").ToLowerInvariant());
        }
        lines.Add(text.ToString());
    }

    private string DeviceName(ulong address) => _devices.Find(d => d.Address == address)?.Name ?? Text("app.name");

    // ---- Nearby offers as notifications ----

    private async void UpdateOffers()
    {
        if (_offersRunning)
        {
            _offersAgain = true;
            return;
        }
        _offersRunning = true;
        try
        {
            do
            {
                _offersAgain = false;
                await SyncOfferNotificationsAsync();
            }
            while (_offersAgain);
        }
        finally
        {
            _offersRunning = false;
        }
    }

    private async Task SyncOfferNotificationsAsync()
    {
        NearbyOffer[] offers = [.. _offers];
        foreach (ulong address in _offerNotes.Keys.Where(a => !offers.Any(o => o.Address == a)).ToList())
        {
            uint id = _offerNotes[address];
            _offerNotes.Remove(address);
            _offerShown.Remove(address);
            _acted.Remove(id);
            await Notifications.CloseAsync(id);
        }
        foreach (NearbyOffer offer in offers)
        {
            (string summary, string body, IReadOnlyList<(string, string)> actions) = OfferContent(offer);
            string shown = $"{summary}\n{body}\n{string.Join(',', actions.Select(a => a.Item1))}";
            if (_offerShown.GetValueOrDefault(offer.Address) == shown)
            {
                continue;
            }
            _offerShown[offer.Address] = shown;
            uint id = await Notifications.ShowAsync(summary, body, _offerNotes.GetValueOrDefault(offer.Address), "audio-headphones", actions, resident: true);
            if (id != 0)
            {
                _offerNotes[offer.Address] = id;
            }
        }
    }

    private (string Summary, string Body, IReadOnlyList<(string, string)> Actions) OfferContent(NearbyOffer offer)
    {
        (string, string) connect = ("connect", Text("popup.connect"));
        (string, string) notNow = ("dismiss", Text("popup.notNow"));
        switch (offer.State)
        {
            case OfferState.Connecting:
                return (offer.Name, Text("popup.connecting"), [("cancel", Text("popup.cancel"))]);
            case OfferState.Connected:
                return (offer.Name, Text("popup.connected"), []);
            case OfferState.Failed:
                return (Text("popup.failedTitle"), Text("popup.failedBody"), [("connect", Text("popup.tryAgain")), notNow]);
            default:
                return (offer.Name, OfferBatteries(offer), [("default", connect.Item2), connect, notNow]);
        }
    }

    // "L 80% · R 79% · Case 100%" from the popup's own labels.
    private string OfferBatteries(NearbyOffer offer)
    {
        PodBatteries b = offer.Batteries;
        var parts = new List<string>();
        void Add(string key, Battery battery)
        {
            if (battery.Percent is { } pct)
            {
                parts.Add(Text(key).Replace("{pct}", pct.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
        }
        Add("popup.metaLeft", b.Left);
        Add("popup.metaRight", b.Right);
        Add("popup.metaCase", b.Case);
        if (b.Headphone.Percent is { } headphone)
        {
            parts.Add(Text("battery.pct").Replace("{pct}", headphone.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
        return string.Join(" · ", parts);
    }

    // Notification callbacks arrive on a thread-pool thread.
    private void OnNotificationAction(uint id, string key) =>
        _loop.Post(_ =>
        {
            if (_offerNotes.FirstOrDefault(p => p.Value == id) is not { Value: not 0 } entry)
            {
                return;
            }
            // Some shells report the click as a dismissal afterwards: not a "Not now".
            _acted.Add(id);
            Log.Info("ui", $"offer {Mac.Format(entry.Key)}: notification action {key}");
            switch (key)
            {
                case "default" or "connect":
                    _ = _nearby.ConnectAsync(entry.Key);
                    break;
                case "cancel":
                    _nearby.CancelConnect();
                    break;
                case "dismiss":
                    _nearby.Dismiss(entry.Key);
                    break;
            }
        }, null);

    private void OnNotificationDismissed(uint id) =>
        _loop.Post(_ =>
        {
            if (_acted.Remove(id))
            {
                return;
            }
            if (_offerNotes.FirstOrDefault(p => p.Value == id) is { Value: not 0 } entry)
            {
                Log.Info("ui", $"offer {Mac.Format(entry.Key)}: notification dismissed");
                _nearby.Dismiss(entry.Key);
            }
        }, null);
}
