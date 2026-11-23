namespace LPods;

internal static class Program
{
    private static async Task<int> Main(string[] args) => args.Length == 0 || args[0] == "daemon"
        ? await Daemon.RunAsync().ConfigureAwait(false)
        : await Cli.RunAsync(args).ConfigureAwait(false);
}
