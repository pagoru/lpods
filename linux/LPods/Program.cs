namespace LPods;

internal static class Program
{
    /// <summary>
    /// <c>lpods daemon</c> runs in the background (tray only); <c>lpods</c> and <c>lpods open</c> show the window, starting
    /// the app first when it is not running; everything else is a command for the running app.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "open";
        if (command == "daemon")
        {
            return await Daemon.RunAsync(showWindow: false).ConfigureAwait(false);
        }
        if (command == "open" && !await Control.IsRunningAsync().ConfigureAwait(false))
        {
            return await Daemon.RunAsync(showWindow: true).ConfigureAwait(false);
        }
        return await Cli.RunAsync(args.Length > 0 ? args : ["open"]).ConfigureAwait(false);
    }
}
