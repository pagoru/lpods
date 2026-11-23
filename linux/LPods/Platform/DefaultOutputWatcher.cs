using System.Diagnostics;
using LPods.Platform;

namespace WPods.Core;

/// <summary>
/// Linux stand-in for upstream's DefaultOutputWatcher: when the default output (PipeWire sink) becomes a paired
/// AirPods, the session follows it (<see cref="ConnectionManager.FollowDefaultOutputAsync"/>). Follows
/// `pactl subscribe` server events.
/// </summary>
public sealed class DefaultOutputWatcher : IDisposable
{
    private readonly ConnectionManager _connections;
    private readonly CancellationTokenSource _stop = new();
    private string? _lastSink;

    public DefaultOutputWatcher(ConnectionManager connections)
    {
        _connections = connections;
        _ = Task.Run(RunAsync);
    }

    /// <summary>1 = the pair is the default output, 0 = it is not, -1 = unknown (upstream's helper contract).</summary>
    internal static int wpn_audio_is_default_output(ulong btAddress)
    {
        try
        {
            return PipeWire.DefaultSink() is { } sink && PipeWire.AddressOfSink(sink) == btAddress ? 1 : 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var info = new ProcessStartInfo("pactl", "subscribe") { RedirectStandardOutput = true, UseShellExecute = false };
                info.Environment["LC_ALL"] = "C";
                using Process process = Process.Start(info)!;
                using CancellationTokenRegistration kill = _stop.Token.Register(() => process.Kill());
                Check();
                while (await process.StandardOutput.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.Contains("on server", StringComparison.Ordinal))
                    {
                        Check();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warn("output", $"pactl subscribe failed: {e.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
    }

    private void Check()
    {
        string? sink;
        try
        {
            sink = PipeWire.DefaultSink();
        }
        catch (Exception)
        {
            return;
        }
        if (sink == _lastSink)
        {
            return;
        }
        _lastSink = sink;
        ulong address = sink is null ? 0 : PipeWire.AddressOfSink(sink);
        if (address != 0)
        {
            Log.Info("output", $"default output is {Mac.Format(address)}");
            _ = _connections.FollowDefaultOutputAsync(address);
        }
    }
}
