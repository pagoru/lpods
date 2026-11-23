using System.Runtime.InteropServices;
using LPods.Platform;
using LPods.Ui;
using WPods.Audio;
using WPods.Core;
using WPods.Nearby;
using WPods.Ui;

namespace LPods;

/// <summary>The background app: upstream's ConnectionManager and NearbyService on the Linux platform layer.</summary>
internal static class Daemon
{
    public static async Task<int> RunAsync(bool showWindow)
    {
        if (await Control.IsRunningAsync().ConfigureAwait(false))
        {
            Console.Error.WriteLine("lpods is already running.");
            return 1;
        }
        LinuxPaths.Secure();
        Log.Info("app", $"start lpods {typeof(Daemon).Assembly.GetName().Version} on {RuntimeInformation.OSDescription} pid={Environment.ProcessId}");
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Log.Error("app", $"unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += static (_, e) => Log.Error("app", "unobserved task exception", e.Exception);

        var settings = new SettingsStore();
        var connections = new ConnectionManager(settings);
        using var nearby = new NearbyService(connections);
        // Opening the case releases a pair the user disconnected while the link is still up.
        nearby.CaseOpened += connections.OnCaseOpened;
        using var defaultOutput = new DefaultOutputWatcher(connections);
        using var ducker = new ConversationDucker(connections);
        _ = new EarPause(connections);
        Localization? locales = Localization.Load(Path.Combine(AppContext.BaseDirectory, "locales"));
        ResolvedLocale? locale = locales is null ? null : LinuxLabels.Resolve(locales, settings.Ui.Language);
        Func<string, string> text = key => locale?.Text(key) ?? key;
        _ = new LowBattery(connections, text);
        using var changes = new Changes(connections);
        var commands = new Commands(connections);
        using var control = new Control(commands, connections, changes);
        var stop = new TaskCompletionSource();

        // The window and the tray need a desktop session; without one the daemon runs headless.
        using var theme = new DesktopTheme();
        UiApp? ui = null;
        if (locales is not null && GtkLoop.Start() is { } loop)
        {
            await theme.StartAsync().ConfigureAwait(false);
            loop.Send(_ => ui = new UiApp(settings, connections, nearby, locales, loop, theme, () => stop.TrySetResult()), null);
            commands.OpenWindow = ui!.Activate;
        }

        connections.Start();
        nearby.Start();
        control.Start();
        Console.Error.WriteLine($"lpods running{(ui is null ? " without a window (no desktop session)" : "")}. Logs: {Path.Combine(LinuxPaths.Data, "logs")}");
        if (showWindow)
        {
            ui?.Activate();
        }

        using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.TrySetResult(); });
        using PosixSignalRegistration interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.TrySetResult(); });
        await stop.Task.ConfigureAwait(false);

        Log.Info("app", "exit");
        ui?.Dispose();
        await connections.DisposeAsync().ConfigureAwait(false);
        Log.Flush();
        return 0;
    }
}

/// <summary>One event for anything a status reader shows: links, the device list, and the attached session's state.</summary>
internal sealed class Changes : IDisposable
{
    private readonly ConnectionManager _connections;

    public Changes(ConnectionManager connections)
    {
        _connections = connections;
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        connections.LinkChanged += OnLinkChanged;
        connections.DevicesChanged += Raise;
    }

    public event Action? Changed;

    public void Dispose()
    {
        _connections.Attached -= OnAttached;
        _connections.Detached -= OnDetached;
        _connections.LinkChanged -= OnLinkChanged;
        _connections.DevicesChanged -= Raise;
    }

    private void OnAttached(AapSession session)
    {
        session.Changed += OnSessionChanged;
        Raise();
    }

    private void OnDetached(AapSession session)
    {
        session.Changed -= OnSessionChanged;
        Raise();
    }

    private void OnLinkChanged(ulong address, bool up) => Raise();

    private void OnSessionChanged(DeviceChange change) => Raise();

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("app", "change handler failed", e);
        }
    }
}
