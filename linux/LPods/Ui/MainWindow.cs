using System.Runtime.InteropServices;
using LPods.Platform;

namespace LPods.Ui;

/// <summary>
/// The main window: no frame (the page draws its own title bar), 400 px wide, 480 to 960 px tall, resizable vertically
/// from a thin strip at the top and bottom edge. Close hides it (the app lives in the tray); see <c>CloseRequested</c>.
/// </summary>
internal sealed unsafe class MainWindow
{
    public const int Width = 400;
    public const int MinHeight = 480;
    public const int MaxHeight = 960;
    private const int ResizeStrip = 4;

    private readonly GCHandle _self;
    private readonly nint _window;
    private uint _pressTime;
    private int _pressButton = 1;
    private int _pressX;
    private int _pressY;
    private bool _destroyed;

    public MainWindow(string title, int height, uint background, WebPane pane)
    {
        Pane = pane;
        _self = GCHandle.Alloc(this);
        nint data = GCHandle.ToIntPtr(_self);
        _window = Gtk.gtk_window_new(Gtk.GtkWindowToplevel);
        Gtk.gtk_window_set_title(_window, title);
        Gtk.gtk_window_set_icon_name(_window, GtkLoop.AppId);
        Gtk.gtk_window_set_decorated(_window, 0);
        Gtk.gtk_window_set_position(_window, Gtk.GtkWinPosCenter);
        Gtk.gtk_window_set_default_size(_window, Width, Math.Clamp(height, MinHeight, MaxHeight));
        var hints = new Gtk.GdkGeometry { MinWidth = Width, MaxWidth = Width, MinHeight = MinHeight, MaxHeight = MaxHeight };
        Gtk.gtk_window_set_geometry_hints(_window, 0, &hints, Gtk.GdkHintMinSize | Gtk.GdkHintMaxSize);

        nint overlay = Gtk.gtk_overlay_new();
        Gtk.gtk_container_add(overlay, pane.View);
        Gtk.gtk_overlay_add_overlay(overlay, Strip(Gtk.GtkAlignStart, Gtk.GdkWindowEdgeNorth, data));
        Gtk.gtk_overlay_add_overlay(overlay, Strip(Gtk.GtkAlignEnd, Gtk.GdkWindowEdgeSouth, data));
        Gtk.gtk_container_add(_window, overlay);

        // Remember where the last press was: the page asks for a window drag on mouse down.
        Gtk.gtk_widget_add_events(pane.View, Gtk.GdkButtonPressMask);
        Gtk.g_signal_connect_data(pane.View, "button-press-event", (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnViewPress, data, 0, 0);
        Gtk.g_signal_connect_data(_window, "delete-event", (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnDelete, data, 0, 0);
        Gtk.g_signal_connect_data(_window, "notify::is-active", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnActive, data, 0, 0);
        pane.SetBackground(background);
    }

    public WebPane Pane { get; }

    /// <summary>The window manager's close (Alt+F4, the shell's Close): the app hides the window instead.</summary>
    public event Action? CloseRequested;

    public bool IsVisible => !_destroyed && Gtk.gtk_widget_get_visible(_window) != 0;

    public bool IsActive => !_destroyed && Gtk.gtk_window_is_active(_window) != 0;

    /// <summary>Current height in pixels.</summary>
    public int Height
    {
        get
        {
            Gtk.gtk_window_get_size(_window, out _, out int height);
            return height;
        }
    }

    public void Show()
    {
        Gtk.gtk_widget_show_all(_window);
        Gtk.gtk_window_deiconify(_window);
        Gtk.gtk_window_present_with_time(_window, Gtk.gtk_get_current_event_time());
        Pane.Focus();
    }

    public void Hide() => Gtk.gtk_widget_hide(_window);

    public void Minimize() => Gtk.gtk_window_iconify(_window);

    /// <summary>Sets the height within the limits; not saved as the user's height.</summary>
    public void SetHeight(double height) =>
        Gtk.gtk_window_resize(_window, Width, (int)Math.Clamp(Math.Ceiling(height), MinHeight, MaxHeight));

    /// <summary>Resizes to fit the page's content (without the resize strips).</summary>
    public void FitContentHeight(double content) => SetHeight(content + 2 * ResizeStrip);

    public void BeginDrag() => Gtk.gtk_window_begin_move_drag(_window, _pressButton, _pressX, _pressY, _pressTime);

    public void SetTheme(uint background, bool dark)
    {
        Pane.SetBackground(background);
        Gtk.SetBool(Gtk.gtk_settings_get_default(), "gtk-application-prefer-dark-theme", dark);
    }

    /// <summary>Destroys the window for real (exit, hidden too long).</summary>
    public void Destroy()
    {
        if (_destroyed)
        {
            return;
        }
        _destroyed = true;
        Pane.Dispose();
        Gtk.gtk_widget_destroy(_window);
        GtkLoop.FreeLater(_self);
    }

    private nint Strip(int align, int edge, nint data)
    {
        nint box = Gtk.gtk_event_box_new();
        Gtk.gtk_event_box_set_visible_window(box, 0);
        Gtk.gtk_widget_set_size_request(box, -1, ResizeStrip);
        Gtk.gtk_widget_set_halign(box, Gtk.GtkAlignFill);
        Gtk.gtk_widget_set_valign(box, align);
        Gtk.gtk_widget_add_events(box, Gtk.GdkButtonPressMask);
        nint edgeData = Marshal.AllocHGlobal(sizeof(nint) * 2);
        ((nint*)edgeData)[0] = data;
        ((nint*)edgeData)[1] = edge;
        Gtk.g_signal_connect_data(box, "button-press-event", (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnStripPress, edgeData, 0, 0);
        Gtk.g_signal_connect_data(box, "realize", (nint)(delegate* unmanaged<nint, nint, void>)&OnStripRealize, 0, 0, 0);
        return box;
    }

    private static MainWindow? From(nint data) => data == 0 ? null : (MainWindow?)GCHandle.FromIntPtr(data).Target;

    [UnmanagedCallersOnly]
    private static void OnStripRealize(nint box, nint data)
    {
        nint cursor = Gtk.gdk_cursor_new_from_name(Gtk.gdk_display_get_default(), "ns-resize");
        if (cursor != 0)
        {
            Gtk.gdk_window_set_cursor(Gtk.gtk_widget_get_window(box), cursor);
            Gtk.g_object_unref(cursor);
        }
    }

    [UnmanagedCallersOnly]
    private static int OnStripPress(nint box, nint gdkEvent, nint data)
    {
        MainWindow? window = From(((nint*)data)[0]);
        if (window is null || window._destroyed)
        {
            return 0;
        }
        int edge = (int)((nint*)data)[1];
        Gtk.gdk_event_get_button(gdkEvent, out uint button);
        Gtk.gdk_event_get_root_coords(gdkEvent, out double x, out double y);
        Gtk.gtk_window_begin_resize_drag(window._window, edge, (int)button, (int)x, (int)y, Gtk.gdk_event_get_time(gdkEvent));
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int OnViewPress(nint view, nint gdkEvent, nint data)
    {
        MainWindow? window = From(data);
        if (window is not null && Gtk.gdk_event_get_button(gdkEvent, out uint button) != 0 && Gtk.gdk_event_get_root_coords(gdkEvent, out double x, out double y) != 0)
        {
            window._pressButton = (int)button;
            window._pressX = (int)x;
            window._pressY = (int)y;
            window._pressTime = Gtk.gdk_event_get_time(gdkEvent);
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnDelete(nint window, nint gdkEvent, nint data)
    {
        From(data)?.CloseRequested?.Invoke();
        // TRUE: not destroyed.
        return 1;
    }

    [UnmanagedCallersOnly]
    private static void OnActive(nint window, nint pspec, nint data)
    {
        if (From(data) is { _destroyed: false } w && Gtk.gtk_window_is_active(w._window) != 0)
        {
            w.Pane.Focus();
        }
    }
}
