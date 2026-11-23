using Tmds.DBus.Protocol;
using WPods.Core;
using MessageWriter = Tmds.DBus.Protocol.MessageWriter;

namespace LPods.Platform;

/// <summary>What the tray shows: a title (the tooltip on KDE), battery lines and the three menu labels.</summary>
internal sealed record TrayModel(string Title, string IconName, IReadOnlyList<string> Lines, string Open, string Startup, bool StartupOn, string Quit);

/// <summary>
/// A tray icon as a StatusNotifierItem (the protocol of KDE, and of GNOME with the AppIndicator extension, Waybar, and
/// others) with its menu over dbusmenu. Events fire on a thread-pool thread. Without a StatusNotifierWatcher on the bus
/// nothing shows; the icon appears when one starts.
/// </summary>
internal sealed class Tray : IDisposable
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string ItemInterface = "org.kde.StatusNotifierItem";
    private const string MenuInterface = "com.canonical.dbusmenu";
    private const string WatcherName = "org.kde.StatusNotifierWatcher";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private const int IdOpen = 1;
    private const int IdStartup = 2;
    private const int IdQuit = 3;
    private const int IdLinesFirst = 10;

    private readonly Lock _lock = new();
    private readonly string _name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
    private DBusConnection? _connection;
    private TrayModel _model = new("LPods", "audio-headphones-symbolic", [], "Open", "Start at login", false, "Quit");
    private uint _revision = 1;
    private bool _registered;

    /// <summary>The icon was clicked (Activate).</summary>
    public event Action? Activated;

    public event Action? OpenClicked;

    public event Action? StartupClicked;

    public event Action? QuitClicked;

    public async Task StartAsync(TrayModel model)
    {
        _model = model;
        try
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync().ConfigureAwait(false);
            connection.AddMethodHandler(new ItemHandler(this));
            connection.AddMethodHandler(new MenuHandler(this));
            await connection.RequestNameAsync(_name, RequestNameOptions.None).ConfigureAwait(false);
            _connection = connection;
            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = WatcherName },
                static (Message m, object? _) =>
                {
                    Reader reader = m.GetBodyReader();
                    reader.ReadString();
                    reader.ReadString();
                    return reader.ReadString();
                },
                (Notification<string> n) =>
                {
                    // A watcher appeared (the shell extension was enabled, the panel restarted): register again.
                    if (n.HasValue && n.Value.Length > 0)
                    {
                        _ = RegisterAsync();
                    }
                },
                emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
            await RegisterAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn("tray", $"tray not started: {e.Message}");
        }
    }

    public void Update(TrayModel model)
    {
        bool title;
        bool icon;
        bool menu;
        lock (_lock)
        {
            title = model.Title != _model.Title;
            icon = model.IconName != _model.IconName;
            menu = !model.Lines.SequenceEqual(_model.Lines) || model.Open != _model.Open || model.Startup != _model.Startup
                || model.StartupOn != _model.StartupOn || model.Quit != _model.Quit;
            _model = model;
            if (menu)
            {
                _revision++;
            }
        }
        if (title)
        {
            Signal(ItemPath, ItemInterface, "NewTitle", null);
        }
        if (icon)
        {
            Signal(ItemPath, ItemInterface, "NewIcon", null);
        }
        if (menu)
        {
            Signal(MenuPath, MenuInterface, "LayoutUpdated", "ui", w =>
            {
                w.WriteUInt32(_revision);
                w.WriteInt32(0);
            });
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private async Task RegisterAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }
        try
        {
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: WatcherName, path: "/StatusNotifierWatcher", @interface: WatcherName, member: "RegisterStatusNotifierItem", signature: "s");
                writer.WriteString(_name);
                message = writer.CreateMessage();
            }
            await connection.CallMethodAsync(message).ConfigureAwait(false);
            if (!_registered)
            {
                _registered = true;
                Log.Info("tray", "tray icon registered");
            }
        }
        catch (DBusErrorReplyException e)
        {
            Log.Info("tray", $"no tray host yet ({e.ErrorName}); GNOME needs the AppIndicator extension. The window opens with `lpods open`.");
        }
    }

    private void Signal(string path, string @interface, string member, string? signature, Action<MessageWriter>? body = null)
    {
        if (_connection is not { } connection)
        {
            return;
        }
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteSignalHeader(destination: null, path: path, @interface: @interface, member: member, signature: signature);
        body?.Invoke(writer);
        connection.TrySendMessage(writer.CreateMessage());
    }

    private TrayModel Model
    {
        get
        {
            lock (_lock)
            {
                return _model;
            }
        }
    }

    private uint Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    private Dictionary<string, VariantValue> ItemProperties()
    {
        TrayModel model = Model;
        return new Dictionary<string, VariantValue>
        {
            ["Category"] = "Hardware",
            ["Id"] = "lpods",
            ["Title"] = model.Title,
            ["Status"] = "Active",
            ["WindowId"] = VariantValue.UInt32(0),
            ["IconName"] = model.IconName,
            ["IconThemePath"] = "",
            ["AttentionIconName"] = "",
            ["OverlayIconName"] = "",
            ["ItemIsMenu"] = false,
            ["Menu"] = VariantValue.ObjectPath(new ObjectPath(MenuPath)),
        };
    }

    private Dictionary<string, VariantValue> MenuProperties() => new()
    {
        ["Version"] = VariantValue.UInt32(3),
        ["TextDirection"] = "ltr",
        ["Status"] = "normal",
        ["IconThemePath"] = VariantValue.Array(Array.Empty<string>()),
    };

    /// <summary>The menu: battery lines (grayed), a separator, then Open, Start at login, Quit.</summary>
    private List<(int Id, Dictionary<string, VariantValue> Properties)> MenuItems()
    {
        TrayModel model = Model;
        var items = new List<(int, Dictionary<string, VariantValue>)>();
        for (int i = 0; i < model.Lines.Count; i++)
        {
            items.Add((IdLinesFirst + i, new Dictionary<string, VariantValue> { ["label"] = model.Lines[i], ["enabled"] = false }));
        }
        if (model.Lines.Count > 0)
        {
            items.Add((IdLinesFirst + model.Lines.Count, new Dictionary<string, VariantValue> { ["type"] = "separator" }));
        }
        items.Add((IdOpen, new Dictionary<string, VariantValue> { ["label"] = model.Open }));
        items.Add((IdStartup, new Dictionary<string, VariantValue>
        {
            ["label"] = model.Startup,
            ["toggle-type"] = "checkmark",
            ["toggle-state"] = VariantValue.Int32(model.StartupOn ? 1 : 0),
        }));
        items.Add((IdQuit, new Dictionary<string, VariantValue> { ["label"] = model.Quit }));
        return items;
    }

    private void OnMenuEvent(int id)
    {
        Action? action = id switch
        {
            IdOpen => OpenClicked,
            IdStartup => StartupClicked,
            IdQuit => QuitClicked,
            _ => null,
        };
        if (action is not null)
        {
            _ = Task.Run(action);
        }
    }

    /// <summary>org.kde.StatusNotifierItem at /StatusNotifierItem.</summary>
    private sealed class ItemHandler(Tray tray) : IPathMethodHandler
    {
        public string Path => ItemPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            Message request = context.Request;
            string member = request.MemberAsString ?? "";
            if (context.IsPropertiesInterfaceRequest)
            {
                Reader reader = request.GetBodyReader();
                reader.ReadString();
                Dictionary<string, VariantValue> properties = tray.ItemProperties();
                switch (member)
                {
                    case "GetAll":
                        using (MessageWriter writer = context.CreateReplyWriter("a{sv}"))
                        {
                            writer.WriteDictionary(properties);
                            context.Reply(writer.CreateMessage());
                        }
                        break;
                    case "Get" when properties.TryGetValue(reader.ReadString(), out VariantValue value):
                        using (MessageWriter writer = context.CreateReplyWriter("v"))
                        {
                            writer.WriteVariant(value);
                            context.Reply(writer.CreateMessage());
                        }
                        break;
                    default:
                        context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "no such property");
                        break;
                }
                return default;
            }
            if (request.InterfaceAsString == ItemInterface)
            {
                switch (member)
                {
                    case "Activate":
                    case "SecondaryActivate":
                        _ = Task.Run(() => tray.Activated?.Invoke());
                        break;
                    case "ContextMenu":
                    case "Scroll":
                        break;
                    default:
                        context.ReplyUnknownMethodError();
                        return default;
                }
                using MessageWriter empty = context.CreateReplyWriter(null);
                context.Reply(empty.CreateMessage());
                return default;
            }
            context.ReplyUnknownMethodError();
            return default;
        }
    }

    /// <summary>com.canonical.dbusmenu at /MenuBar.</summary>
    private sealed class MenuHandler(Tray tray) : IPathMethodHandler
    {
        public string Path => MenuPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            Message request = context.Request;
            string member = request.MemberAsString ?? "";
            if (context.IsPropertiesInterfaceRequest)
            {
                Reader reader = request.GetBodyReader();
                reader.ReadString();
                Dictionary<string, VariantValue> properties = tray.MenuProperties();
                if (member == "GetAll")
                {
                    using MessageWriter writer = context.CreateReplyWriter("a{sv}");
                    writer.WriteDictionary(properties);
                    context.Reply(writer.CreateMessage());
                }
                else if (member == "Get" && properties.TryGetValue(reader.ReadString(), out VariantValue value))
                {
                    using MessageWriter writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(value);
                    context.Reply(writer.CreateMessage());
                }
                else
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "no such property");
                }
                return default;
            }
            if (request.InterfaceAsString != MenuInterface)
            {
                context.ReplyUnknownMethodError();
                return default;
            }
            Reader args = request.GetBodyReader();
            switch (member)
            {
                case "GetLayout":
                {
                    using MessageWriter writer = context.CreateReplyWriter("u(ia{sv}av)");
                    writer.WriteUInt32(tray.Revision);
                    writer.WriteStructureStart();
                    writer.WriteInt32(0);
                    writer.WriteDictionary(new Dictionary<string, VariantValue> { ["children-display"] = "submenu" });
                    ArrayStart children = writer.WriteArrayStart(DBusType.Variant);
                    foreach ((int id, Dictionary<string, VariantValue> properties) in tray.MenuItems())
                    {
                        writer.WriteSignature("(ia{sv}av)");
                        writer.WriteStructureStart();
                        writer.WriteInt32(id);
                        writer.WriteDictionary(properties);
                        ArrayStart none = writer.WriteArrayStart(DBusType.Variant);
                        writer.WriteArrayEnd(none);
                    }
                    writer.WriteArrayEnd(children);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "GetGroupProperties":
                {
                    using MessageWriter writer = context.CreateReplyWriter("a(ia{sv})");
                    ArrayStart items = writer.WriteArrayStart(DBusType.Struct);
                    foreach ((int id, Dictionary<string, VariantValue> properties) in tray.MenuItems())
                    {
                        writer.WriteStructureStart();
                        writer.WriteInt32(id);
                        writer.WriteDictionary(properties);
                    }
                    writer.WriteArrayEnd(items);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "GetProperty":
                {
                    int id = args.ReadInt32();
                    string name = args.ReadString();
                    VariantValue? value = tray.MenuItems().FirstOrDefault(i => i.Id == id).Properties?.GetValueOrDefault(name);
                    if (value is { } found)
                    {
                        using MessageWriter writer = context.CreateReplyWriter("v");
                        writer.WriteVariant(found);
                        context.Reply(writer.CreateMessage());
                    }
                    else
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "no such property");
                    }
                    break;
                }
                case "Event":
                {
                    int id = args.ReadInt32();
                    string eventId = args.ReadString();
                    if (eventId == "clicked")
                    {
                        tray.OnMenuEvent(id);
                    }
                    using MessageWriter writer = context.CreateReplyWriter(null);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "AboutToShow":
                {
                    using MessageWriter writer = context.CreateReplyWriter("b");
                    writer.WriteBool(false);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
            return default;
        }
    }
}
