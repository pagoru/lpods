using System.Globalization;
using LPods.Platform;
using WPods.Core;
using WPods.Ui;

namespace LPods;

/// <summary>
/// Upstream's low-battery message (TrayApp.CheckLowBattery): once per discharge per part below 10 %, re-armed at
/// 10 % or above or while charging. Shown as a desktop notification.
/// </summary>
internal sealed class LowBattery
{
    private const int LowBatteryPercent = 10;
    private static readonly string[] PartLabels = ["battery.left", "battery.right", "battery.case", "battery.max"];

    private readonly Func<string, string> _text;
    private readonly bool[] _lowNotified = new bool[4];
    private readonly Lock _lock = new();

    public LowBattery(ConnectionManager connections, Func<string, string> text)
    {
        _text = text;
        connections.Attached += session => session.Changed += change =>
        {
            if ((change & DeviceChange.Battery) != 0)
            {
                Check(session.State);
            }
        };
    }

    private void Check(DeviceState state)
    {
        int lowest = -1;
        int lowestPct = 0;
        lock (_lock)
        {
            for (int i = 0; i < _lowNotified.Length; i++)
            {
                Battery battery = i switch
                {
                    0 => state.LeftBattery,
                    1 => state.RightBattery,
                    2 => state.CaseBattery,
                    _ => state.HeadphoneBattery,
                };
                if (battery.Percent is not { } pct)
                {
                    continue;
                }
                if (pct >= LowBatteryPercent || battery.Status == BatteryStatus.Charging)
                {
                    _lowNotified[i] = false;
                }
                else if (!_lowNotified[i])
                {
                    _lowNotified[i] = true;
                    if (lowest < 0 || pct < lowestPct)
                    {
                        lowest = i;
                        lowestPct = pct;
                    }
                }
            }
        }
        if (lowest < 0)
        {
            return;
        }
        string text = _text("battery.low")
            .Replace("{name}", state.Name ?? "AirPods", StringComparison.Ordinal)
            .Replace("{part}", _text(PartLabels[lowest]), StringComparison.Ordinal)
            .Replace("{pct}", lowestPct.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        _ = Notifications.ShowAsync(state.Name ?? "AirPods", text, icon: "battery-caution");
    }
}
