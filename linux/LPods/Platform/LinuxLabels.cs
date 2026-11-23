using WPods.Ui;

namespace LPods.Platform;

/// <summary>
/// Adapts upstream's locale files to Linux without editing them: the app is LPods, and the Windows wording becomes
/// Linux wording. English gets hand-written texts; other languages get the brand and OS names swapped in the
/// translated text (both are Latin script in the upstream translations). Legal texts keep naming WPods, whose author
/// holds the rights.
/// </summary>
internal static class LinuxLabels
{
    private static readonly Dictionary<string, string> English = new()
    {
        ["firstRun.body"] = "Control your AirPods from Linux: battery, noise control, microphone and more.",
        ["firstRun.trayTitle"] = "Lives in the tray",
        ["firstRun.micDesc"] = "As the Bluetooth headset. The sound turns mono while an app records.",
        ["empty.body"] = "Pair your AirPods with this PC in your Bluetooth settings, then come back here.",
        ["pair.addDevice"] = "Bluetooth settings",
        ["pair.step3Title"] = "Choose them in your Bluetooth settings",
        ["legal.adapterBody"] = "LPods uses your Bluetooth adapter through BlueZ, the Bluetooth stack of Linux. It doesn't replace any driver.",
        ["legal.licenseBody"] = "LPods is a Linux port of WPods, free software under the GNU General Public License, version 3. Copyright (C) 2026 fpndev (rimjoeh) <rimjoeh@gmail.com>. The WPods code and interface are the author's work.",
        ["windows.title"] = "Settings",
        ["windows.startup"] = "Start at login",
        ["windows.themeDesc"] = "Auto follows your desktop's dark mode.",
        ["windows.accent"] = "Use the desktop accent color",
        ["windows.accentDesc"] = "Matches the accent color of your desktop, where it has one.",
        ["lang.sameAsWindows"] = "Same as the system",
        ["mic.sideDesc"] = "Which AirPod's mic is heard.",
        ["tray.startup"] = "Start at login",
        ["popup.failedBody"] = "Make sure your AirPods are close to your PC and try again.",
    };

    public static ResolvedLocale Resolve(Localization locales, string choice)
    {
        ResolvedLocale locale = locales.Resolve(choice);
        bool english = locale.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(locale.Labels.Count, StringComparer.Ordinal);
        foreach ((string key, string text) in locale.Labels)
        {
            string adapted = text;
            if (english && English.TryGetValue(key, out string? replacement))
            {
                adapted = replacement;
            }
            else
            {
                if (!key.StartsWith("legal.", StringComparison.Ordinal))
                {
                    adapted = adapted.Replace("WPods", "LPods", StringComparison.Ordinal);
                }
                adapted = adapted.Replace("Windows", "Linux", StringComparison.Ordinal);
            }
            labels[key] = adapted;
        }
        labels["key.win"] = "Super";
        return locale with { Labels = labels };
    }
}
