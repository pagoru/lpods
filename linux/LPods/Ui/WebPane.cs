using System.Runtime.InteropServices;
using System.Text.Json;
using LPods.Platform;
using WPods.Core;
using WPods.Ui;

namespace LPods.Ui;

/// <summary>
/// One WebKitGTK view showing a page of web/ through lpods://app/. Page messages arrive on the UI thread
/// (window.chrome.webview, see linux.js); <see cref="Post"/> drops messages until the page has sent "ready".
/// </summary>
internal sealed unsafe class WebPane : IDisposable
{
    private const string Scheme = "lpods";
    private const string Host = "app";

    private static readonly Lock SchemeLock = new();
    private static bool _schemeRegistered;
    private static readonly string WebDirectory = Path.Combine(AppContext.BaseDirectory, "web");
    private static string? _script;
    private static string? _style;

    private readonly Action<WebPane, PageMessage> _onMessage;
    private readonly GCHandle _self;
    private bool _disposed;

    public WebPane(Action<WebPane, PageMessage> onMessage)
    {
        _onMessage = onMessage;
        _self = GCHandle.Alloc(this);
        RegisterScheme();
        nint content = Gtk.webkit_user_content_manager_new();
        Gtk.webkit_user_content_manager_register_script_message_handler(content, "host");
        nint script = Gtk.webkit_user_script_new(Script, Gtk.WebKitInjectTopFrame, Gtk.WebKitInjectAtDocumentStart, 0, 0);
        Gtk.webkit_user_content_manager_add_script(content, script);
        Gtk.webkit_user_script_unref(script);
        nint style = Gtk.webkit_user_style_sheet_new(Style, Gtk.WebKitInjectTopFrame, Gtk.WebKitUserStyleLevelUser, 0, 0);
        Gtk.webkit_user_content_manager_add_style_sheet(content, style);
        Gtk.webkit_user_style_sheet_unref(style);
        Gtk.g_signal_connect_data(content, "script-message-received::host", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnScriptMessage, GCHandle.ToIntPtr(_self), 0, 0);
        View = Gtk.webkit_web_view_new_with_user_content_manager(content);
        Gtk.g_object_unref(content);
        Gtk.g_signal_connect_data(View, "decide-policy", (nint)(delegate* unmanaged<nint, nint, int, nint, int>)&OnDecidePolicy, GCHandle.ToIntPtr(_self), 0, 0);
        Gtk.g_signal_connect_data(View, "context-menu", (nint)(delegate* unmanaged<nint, nint, nint, nint, int>)&OnContextMenu, 0, 0, 0);
        nint settings = Gtk.webkit_web_view_get_settings(View);
        Gtk.SetBool(settings, "enable-developer-extras", Environment.GetEnvironmentVariable("LPODS_DEVTOOLS") == "1");
        Gtk.SetBool(settings, "enable-back-forward-navigation-gestures", false);
        Gtk.SetBool(settings, "enable-write-console-messages-to-stdout", Environment.GetEnvironmentVariable("LPODS_DEVTOOLS") == "1");
    }

    /// <summary>The WebKitWebView widget.</summary>
    public nint View { get; }

    /// <summary>The page sent "ready" (it has its listener and waits for init).</summary>
    public bool Ready { get; private set; }

    private static string Script => _script ??= File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ui", "linux.js"));

    private static string Style => _style ??= File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ui", "linux.css"));

    public void Load(string page, uint background)
    {
        SetBackground(background);
        Gtk.webkit_web_view_load_uri(View, $"{Scheme}://{Host}/{page}");
    }

    public void Post(string json)
    {
        if (Ready && !_disposed)
        {
            Gtk.webkit_web_view_evaluate_javascript(View, $"window.__lpods_deliver({json})", -1, 0, 0, 0, 0, 0);
        }
    }

    public void SetBackground(uint rgb)
    {
        Gtk.GdkRgba color = Gtk.Rgba(rgb);
        Gtk.webkit_web_view_set_background_color(View, &color);
    }

    public void Focus() => Gtk.gtk_widget_grab_focus(View);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Ready = false;
        // The widget is destroyed with its window; the handle must outlive the callbacks still queued for it.
        GtkLoop.FreeLater(_self);
    }

    private static void RegisterScheme()
    {
        lock (SchemeLock)
        {
            if (_schemeRegistered)
            {
                return;
            }
            _schemeRegistered = true;
            nint context = Gtk.webkit_web_context_get_default();
            Gtk.webkit_web_context_register_uri_scheme(context, Scheme, &OnSchemeRequest, 0, 0);
            nint security = Gtk.webkit_web_context_get_security_manager(context);
            Gtk.webkit_security_manager_register_uri_scheme_as_secure(security, Scheme);
            Gtk.webkit_security_manager_register_uri_scheme_as_cors_enabled(security, Scheme);
        }
    }

    /// <summary>Serves web/ (read-only, no path outside it).</summary>
    [UnmanagedCallersOnly]
    private static void OnSchemeRequest(nint request, nint data)
    {
        try
        {
            string path = Gtk.Utf8(Gtk.webkit_uri_scheme_request_get_path(request)) ?? "/";
            string relative = path.TrimStart('/');
            if (relative.Length == 0)
            {
                relative = "index.html";
            }
            string full = Path.GetFullPath(Path.Combine(WebDirectory, relative));
            if (!full.StartsWith(WebDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            {
                Fail(request, 404, $"not found: {path}");
                return;
            }
            byte[] bytes = File.ReadAllBytes(full);
            nint gbytes;
            fixed (byte* p = bytes)
            {
                gbytes = Gtk.g_bytes_new(p, (nuint)bytes.Length);
            }
            nint stream = Gtk.g_memory_input_stream_new_from_bytes(gbytes);
            Gtk.g_bytes_unref(gbytes);
            Gtk.webkit_uri_scheme_request_finish(request, stream, bytes.Length, ContentType(full));
            Gtk.g_object_unref(stream);
        }
        catch (Exception e)
        {
            Log.Warn("ui", $"page file not served: {e.Message}");
            Fail(request, 500, e.Message);
        }
    }

    private static void Fail(nint request, int code, string message)
    {
        nint error = Gtk.g_error_new_literal(Gtk.g_quark_from_string("lpods-scheme"), code, message);
        Gtk.webkit_uri_scheme_request_finish_error(request, error);
        Gtk.g_error_free(error);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    // script-message-received: (manager, WebKitJavascriptResult*, user data)
    [UnmanagedCallersOnly]
    private static void OnScriptMessage(nint manager, nint value, nint data)
    {
        var pane = (WebPane?)GCHandle.FromIntPtr(data).Target;
        if (pane is null || pane._disposed)
        {
            return;
        }
        // WebKitGTK 4.1 passes a WebKitJavascriptResult (the JSCValue comes from it).
        string? json = Gtk.TakeUtf8(Gtk.jsc_value_to_string(Gtk.webkit_javascript_result_get_js_value(value)));
        if (json is null)
        {
            return;
        }
        PageMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, PageJson.Default.PageMessage);
        }
        catch (JsonException e)
        {
            Log.Warn("ui", $"page message not parsed: {e.Message}");
            return;
        }
        if (message is null)
        {
            return;
        }
        if (message.Type == "ready")
        {
            pane.Ready = true;
        }
        try
        {
            pane._onMessage(pane, message);
        }
        catch (Exception e)
        {
            Log.Error("ui", $"handling page message {message.Type} failed", e);
        }
    }

    // decide-policy: only the app's own pages load; any link leaves to the desktop's handler (the app has none).
    [UnmanagedCallersOnly]
    private static int OnDecidePolicy(nint view, nint decision, int type, nint data)
    {
        if (type is Gtk.WebKitPolicyNewWindowAction)
        {
            Gtk.webkit_policy_decision_ignore(decision);
            return 1;
        }
        if (type is Gtk.WebKitPolicyNavigationAction)
        {
            nint action = Gtk.webkit_navigation_policy_decision_get_navigation_action(decision);
            string? uri = Gtk.Utf8(Gtk.webkit_uri_request_get_uri(Gtk.webkit_navigation_action_get_request(action)));
            if (uri is not null && !uri.StartsWith($"{Scheme}://{Host}/", StringComparison.Ordinal))
            {
                Gtk.webkit_policy_decision_ignore(decision);
                return 1;
            }
        }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnContextMenu(nint view, nint menu, nint hit, nint data) => 1;
}
