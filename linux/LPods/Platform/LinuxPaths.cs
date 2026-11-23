using WPods.Core;

namespace LPods.Platform;

internal static class LinuxPaths
{
    /// <summary>Upstream's data folder (LocalApplicationData/WPods = $XDG_DATA_HOME/WPods), shared settings format.</summary>
    public static string Data => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WPods");

    /// <summary>The control socket of the running lpods.</summary>
    public static string ControlSocket =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : Path.GetTempPath(), "lpods.sock");

    /// <summary>
    /// The data folder holds the proximity keys of each pair (upstream encrypts them with DPAPI): readable by this
    /// user only.
    /// </summary>
    public static void Secure()
    {
        try
        {
            // GetFolderPath returns "" for a missing $XDG_DATA_HOME (~/.local/share), and upstream's Log and
            // SettingsStore would then write relative to the working directory.
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            Directory.CreateDirectory(dataHome);
            Directory.CreateDirectory(Data);
            File.SetUnixFileMode(Data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("app", $"data folder not made private: {e.Message}");
        }
    }
}
