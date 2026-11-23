using System.Diagnostics;
using WPods.Core;

namespace LPods.Platform;

/// <summary>Desktop integration: opening links and files, and starting the app at login.</summary>
internal static class Desktop
{
    private static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "lpods.desktop");

    /// <summary>Opens a URL or file with the desktop's default handler.</summary>
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false, RedirectStandardError = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("ui", $"open {target} failed: {e.Message}");
        }
    }

    /// <summary>Opens the desktop's Bluetooth settings (GNOME, KDE, or a Bluetooth manager).</summary>
    public static void OpenBluetoothSettings()
    {
        foreach ((string program, string[] args) in new (string, string[])[]
        {
            ("gnome-control-center", ["bluetooth"]),
            ("systemsettings", ["kcm_bluetooth"]),
            ("blueman-manager", []),
            ("blueberry", []),
        })
        {
            try
            {
                var start = new ProcessStartInfo(program) { UseShellExecute = false };
                foreach (string arg in args)
                {
                    start.ArgumentList.Add(arg);
                }
                Process.Start(start)?.Dispose();
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed: try the next one.
            }
        }
        Log.Warn("ui", "no Bluetooth settings program found");
    }

    public static bool AutostartEnabled => File.Exists(AutostartFile);

    /// <summary>Starts hidden in the tray at login (an XDG autostart entry; works on every desktop).</summary>
    public static bool SetAutostart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
                File.WriteAllText(AutostartFile,
                    "[Desktop Entry]\nType=Application\nName=LPods\nComment=AirPods on Linux\nExec=lpods daemon\nIcon=io.github.pagoru.LPods\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
            }
            else
            {
                File.Delete(AutostartFile);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("ui", $"autostart not changed: {e.Message}");
            return false;
        }
    }
}
