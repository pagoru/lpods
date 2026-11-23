using System.Runtime.InteropServices;
using WPods.Core;

namespace LPods.Platform;

/// <summary>
/// The GTK main loop on its own thread, as a <see cref="SynchronizationContext"/>: posted work and the continuations of
/// awaits started on it run on that thread, like upstream's WinForms UI thread.
/// </summary>
internal sealed unsafe class GtkLoop : SynchronizationContext
{
    /// <summary>Desktop file id and Wayland app id; also the icon name.</summary>
    public const string AppId = "io.github.pagoru.LPods";

    private readonly Thread _thread;

    private GtkLoop(Thread thread)
    {
        _thread = thread;
    }

    public bool OnThread => Thread.CurrentThread == _thread;

    /// <summary>Starts GTK on a new thread; null when there is no display (or GTK or WebKitGTK is missing).</summary>
    public static GtkLoop? Start()
    {
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null && Environment.GetEnvironmentVariable("DISPLAY") is null)
        {
            Log.Info("ui", "no display: running without a window or tray");
            return null;
        }
        var started = new TaskCompletionSource<GtkLoop?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(started)) { Name = "gtk", IsBackground = true };
        thread.Start();
        return started.Task.GetAwaiter().GetResult();
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        GCHandle handle = GCHandle.Alloc(new Work(d, state));
        Gtk.g_idle_add(&Invoke, GCHandle.ToIntPtr(handle));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (OnThread)
        {
            d(state);
            return;
        }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
        if (error is not null)
        {
            throw new InvalidOperationException("UI call failed", error);
        }
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Frees a handle given to native callbacks a little later: events queued before a widget was destroyed may still
    /// call back, and a callback with a freed handle would crash the process.
    /// </summary>
    public static void FreeLater(GCHandle handle) =>
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => handle.Free(), TaskScheduler.Default);

    /// <summary>Ends the main loop and waits for its thread (WebKit must be shut down from it, not by process exit).</summary>
    public void Quit()
    {
        Post(static _ => Gtk.gtk_main_quit(), null);
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private static void Run(TaskCompletionSource<GtkLoop?> started)
    {
        try
        {
            Gtk.g_set_prgname(AppId);
            Gtk.g_set_application_name("LPods");
            if (Gtk.gtk_init_check(0, 0) == 0)
            {
                Log.Warn("ui", "GTK could not open the display: running without a window or tray");
                started.SetResult(null);
                return;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("ui", $"GTK 3 is not installed ({e.Message}): running without a window or tray");
            started.SetResult(null);
            return;
        }
        var loop = new GtkLoop(Thread.CurrentThread);
        SetSynchronizationContext(loop);
        Gtk.gtk_window_set_default_icon_name(AppId);
        started.SetResult(loop);
        Gtk.gtk_main();
        Log.Info("ui", "GTK main loop ended");
    }

    [UnmanagedCallersOnly]
    private static int Invoke(nint data)
    {
        GCHandle handle = GCHandle.FromIntPtr(data);
        var work = (Work)handle.Target!;
        handle.Free();
        try
        {
            work.Callback(work.State);
        }
        catch (Exception e)
        {
            Log.Error("ui", "UI work failed", e);
        }
        // G_SOURCE_REMOVE: run once.
        return 0;
    }

    private sealed record Work(SendOrPostCallback Callback, object? State);
}
