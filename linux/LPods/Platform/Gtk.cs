using System.Runtime.InteropServices;

namespace LPods.Platform;

/// <summary>
/// The GLib, GTK 3 and WebKitGTK (4.1 API) calls the window uses. Loaded on first use, so the app runs without them
/// when there is no desktop (the daemon and the CLI never touch this class).
/// </summary>
internal static unsafe partial class Gtk
{
    private const string GLibLib = "libglib-2.0.so.0";
    private const string GObjectLib = "libgobject-2.0.so.0";
    private const string GioLib = "libgio-2.0.so.0";
    private const string GtkLib = "libgtk-3.so.0";
    private const string GdkLib = "libgdk-3.so.0";
    private const string WebKitLib = "libwebkit2gtk-4.1.so.0";
    private const string JscLib = "libjavascriptcoregtk-4.1.so.0";

    public const int GTypeBoolean = 5 << 2;
    public const int GtkWindowToplevel = 0;
    public const int GtkWinPosCenter = 1;
    public const int GdkHintMinSize = 1 << 1;
    public const int GdkHintMaxSize = 1 << 2;
    public const int GtkAlignFill = 0;
    public const int GtkAlignStart = 1;
    public const int GtkAlignEnd = 2;
    public const int GdkWindowEdgeNorth = 1;
    public const int GdkWindowEdgeSouth = 6;
    public const int GdkPointerMotionMask = 1 << 2;
    public const int GdkButtonPressMask = 1 << 8;
    public const int GdkLeaveNotifyMask = 1 << 13;
    public const int WebKitInjectTopFrame = 1;
    public const int WebKitInjectAtDocumentStart = 0;
    public const int WebKitUserStyleLevelUser = 1;
    public const int WebKitPolicyNavigationAction = 0;
    public const int WebKitPolicyNewWindowAction = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct GValue
    {
        public nuint GType;
        public ulong Data0;
        public ulong Data1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GdkRgba
    {
        public double Red;
        public double Green;
        public double Blue;
        public double Alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GdkGeometry
    {
        public int MinWidth;
        public int MinHeight;
        public int MaxWidth;
        public int MaxHeight;
        public int BaseWidth;
        public int BaseHeight;
        public int WidthInc;
        public int HeightInc;
        public double MinAspect;
        public double MaxAspect;
        public int WinGravity;
    }

    // ---- GLib / GObject / GIO ----

    [LibraryImport(GLibLib)]
    public static partial uint g_idle_add(delegate* unmanaged<nint, int> function, nint data);

    [LibraryImport(GLibLib)]
    public static partial void g_free(nint memory);

    [LibraryImport(GLibLib)]
    public static partial nint g_bytes_new(void* data, nuint size);

    [LibraryImport(GLibLib)]
    public static partial void g_bytes_unref(nint bytes);

    [LibraryImport(GLibLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void g_set_prgname(string name);

    [LibraryImport(GLibLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void g_set_application_name(string name);

    [LibraryImport(GLibLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint g_quark_from_string(string text);

    [LibraryImport(GLibLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint g_error_new_literal(uint domain, int code, string message);

    [LibraryImport(GLibLib)]
    public static partial void g_error_free(nint error);

    [LibraryImport(GObjectLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial ulong g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroy, int flags);

    [LibraryImport(GObjectLib)]
    public static partial void g_object_unref(nint instance);

    [LibraryImport(GObjectLib)]
    public static partial GValue* g_value_init(GValue* value, nuint type);

    [LibraryImport(GObjectLib)]
    public static partial void g_value_set_boolean(GValue* value, int boolean);

    [LibraryImport(GObjectLib)]
    public static partial void g_value_unset(GValue* value);

    [LibraryImport(GObjectLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void g_object_set_property(nint instance, string name, GValue* value);

    [LibraryImport(GioLib)]
    public static partial nint g_memory_input_stream_new_from_bytes(nint bytes);

    // ---- GTK / GDK ----

    [LibraryImport(GtkLib)]
    public static partial int gtk_init_check(nint argc, nint argv);

    [LibraryImport(GtkLib)]
    public static partial void gtk_main();

    [LibraryImport(GtkLib)]
    public static partial void gtk_main_quit();

    [LibraryImport(GtkLib)]
    public static partial nint gtk_window_new(int type);

    [LibraryImport(GtkLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void gtk_window_set_title(nint window, string title);

    [LibraryImport(GtkLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void gtk_window_set_icon_name(nint window, string name);

    [LibraryImport(GtkLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void gtk_window_set_default_icon_name(string name);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_default_size(nint window, int width, int height);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_resize(nint window, int width, int height);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_get_size(nint window, out int width, out int height);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_decorated(nint window, int decorated);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_position(nint window, int position);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_geometry_hints(nint window, nint geometryWidget, GdkGeometry* geometry, int mask);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_present_with_time(nint window, uint timestamp);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_iconify(nint window);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_deiconify(nint window);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_begin_move_drag(nint window, int button, int rootX, int rootY, uint timestamp);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_begin_resize_drag(nint window, int edge, int button, int rootX, int rootY, uint timestamp);

    [LibraryImport(GtkLib)]
    public static partial void gtk_container_add(nint container, nint widget);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_show_all(nint widget);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_hide(nint widget);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_destroy(nint widget);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_grab_focus(nint widget);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_set_margin_top(nint widget, int margin);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_set_margin_bottom(nint widget, int margin);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_add_events(nint widget, int events);

    [LibraryImport(GtkLib)]
    public static partial nint gtk_widget_get_window(nint widget);

    [LibraryImport(GtkLib)]
    public static partial nint gtk_event_box_new();

    [LibraryImport(GtkLib)]
    public static partial void gtk_event_box_set_visible_window(nint eventBox, int visible);

    [LibraryImport(GtkLib)]
    public static partial nint gtk_overlay_new();

    [LibraryImport(GtkLib)]
    public static partial void gtk_overlay_add_overlay(nint overlay, nint widget);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_set_halign(nint widget, int align);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_set_valign(nint widget, int align);

    [LibraryImport(GtkLib)]
    public static partial void gtk_widget_set_size_request(nint widget, int width, int height);

    [LibraryImport(GtkLib)]
    public static partial int gtk_widget_get_allocated_height(nint widget);

    [LibraryImport(GtkLib)]
    public static partial int gtk_widget_get_visible(nint widget);

    [LibraryImport(GtkLib)]
    public static partial int gtk_window_is_active(nint window);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_skip_taskbar_hint(nint window, int setting);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_keep_above(nint window, int setting);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_type_hint(nint window, int hint);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_set_accept_focus(nint window, int setting);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_move(nint window, int x, int y);

    [LibraryImport(GtkLib)]
    public static partial void gtk_window_get_position(nint window, out int x, out int y);

    [LibraryImport(GtkLib)]
    public static partial nint gtk_settings_get_default();

    [LibraryImport(GtkLib)]
    public static partial uint gtk_get_current_event_time();

    [LibraryImport(GdkLib)]
    public static partial int gdk_event_get_coords(nint gdkEvent, out double x, out double y);

    [LibraryImport(GdkLib)]
    public static partial int gdk_event_get_root_coords(nint gdkEvent, out double x, out double y);

    [LibraryImport(GdkLib)]
    public static partial int gdk_event_get_button(nint gdkEvent, out uint button);

    [LibraryImport(GdkLib)]
    public static partial uint gdk_event_get_time(nint gdkEvent);

    [LibraryImport(GdkLib)]
    public static partial nint gdk_display_get_default();

    [LibraryImport(GdkLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint gdk_cursor_new_from_name(nint display, string name);

    [LibraryImport(GdkLib)]
    public static partial void gdk_window_set_cursor(nint window, nint cursor);

    // ---- WebKitGTK ----

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_web_context_get_default();

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void webkit_web_context_register_uri_scheme(nint context, string scheme, delegate* unmanaged<nint, nint, void> callback, nint data, nint destroy);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_web_context_get_security_manager(nint context);

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void webkit_security_manager_register_uri_scheme_as_secure(nint manager, string scheme);

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void webkit_security_manager_register_uri_scheme_as_cors_enabled(nint manager, string scheme);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_uri_scheme_request_get_path(nint request);

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void webkit_uri_scheme_request_finish(nint request, nint stream, long length, string contentType);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_uri_scheme_request_finish_error(nint request, nint error);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_user_content_manager_new();

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int webkit_user_content_manager_register_script_message_handler(nint manager, string name);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_user_content_manager_add_script(nint manager, nint script);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_user_content_manager_add_style_sheet(nint manager, nint sheet);

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint webkit_user_script_new(string source, int injectedFrames, int injectionTime, nint allowList, nint blockList);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_user_script_unref(nint script);

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint webkit_user_style_sheet_new(string source, int injectedFrames, int level, nint allowList, nint blockList);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_user_style_sheet_unref(nint sheet);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_web_view_new_with_user_content_manager(nint manager);

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void webkit_web_view_load_uri(nint view, string uri);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_web_view_reload(nint view);

    [LibraryImport(WebKitLib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void webkit_web_view_evaluate_javascript(nint view, string script, nint length, nint worldName, nint sourceUri, nint cancellable, nint callback, nint data);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_web_view_set_background_color(nint view, GdkRgba* rgba);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_web_view_get_settings(nint view);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_settings_set_enable_developer_extras(nint settings, int enabled);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_javascript_result_get_js_value(nint result);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_navigation_policy_decision_get_navigation_action(nint decision);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_navigation_action_get_request(nint action);

    [LibraryImport(WebKitLib)]
    public static partial nint webkit_uri_request_get_uri(nint request);

    [LibraryImport(WebKitLib)]
    public static partial void webkit_policy_decision_ignore(nint decision);

    [LibraryImport(JscLib)]
    public static partial nint jsc_value_to_string(nint value);

    /// <summary>Copies a GLib-owned UTF-8 string (not freed).</summary>
    public static string? Utf8(nint text) => Marshal.PtrToStringUTF8(text);

    /// <summary>Copies and frees a newly allocated UTF-8 string.</summary>
    public static string? TakeUtf8(nint text)
    {
        string? value = Marshal.PtrToStringUTF8(text);
        g_free(text);
        return value;
    }

    /// <summary>g_object_set for one boolean property (g_object_set itself is variadic).</summary>
    public static void SetBool(nint instance, string name, bool value)
    {
        GValue v = default;
        g_value_init(&v, GTypeBoolean);
        g_value_set_boolean(&v, value ? 1 : 0);
        g_object_set_property(instance, name, &v);
        g_value_unset(&v);
    }

    public static GdkRgba Rgba(uint rgb) => new()
    {
        Red = ((rgb >> 16) & 0xFF) / 255.0,
        Green = ((rgb >> 8) & 0xFF) / 255.0,
        Blue = (rgb & 0xFF) / 255.0,
        Alpha = 1,
    };
}
