using Tmds.DBus.Protocol;
using WPods.Core;
using MessageWriter = Tmds.DBus.Protocol.MessageWriter;

namespace LPods.Platform;

/// <summary>Desktop notifications (org.freedesktop.Notifications on the session bus). Failures are logged only.</summary>
internal static class Notifications
{
    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DBusConnection? _connection;

    /// <summary>The user clicked a notification or one of its buttons: id, action key ("default" for the notification itself).</summary>
    public static event Action<uint, string>? ActionInvoked;

    /// <summary>The user dismissed a notification (not closed by the app, not expired): id.</summary>
    public static event Action<uint>? Dismissed;

    /// <summary>
    /// Shows a notification; <paramref name="replaces"/> is the id of one to update (0 = new). <paramref name="actions"/>
    /// are (key, label) buttons; the key "default" is the click on the notification itself. Returns its id.
    /// </summary>
    public static async Task<uint> ShowAsync(string summary, string body, uint replaces = 0, string icon = "audio-headphones",
        IReadOnlyList<(string Key, string Label)>? actions = null, bool resident = false)
    {
        try
        {
            DBusConnection connection = await ConnectionAsync().ConfigureAwait(false);
            var flat = new List<string>();
            foreach ((string key, string label) in actions ?? [])
            {
                flat.Add(key);
                flat.Add(label);
            }
            var hints = new Dictionary<string, VariantValue> { ["desktop-entry"] = "io.github.pagoru.LPods" };
            if (resident)
            {
                // Stays in the notification list until dismissed (GNOME), instead of being dropped when it times out.
                hints["resident"] = true;
            }
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: Service, path: ObjectPath, @interface: Service, member: "Notify", signature: "susssasa{sv}i");
                writer.WriteString("LPods");
                writer.WriteUInt32(replaces);
                writer.WriteString(icon);
                writer.WriteString(summary);
                writer.WriteString(body);
                writer.WriteArray(flat.ToArray());
                writer.WriteDictionary(hints);
                writer.WriteInt32(-1);
                message = writer.CreateMessage();
            }
            return await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadUInt32(), null).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn("notify", $"notification not shown: {e.Message}");
            return 0;
        }
    }

    public static async Task CloseAsync(uint id)
    {
        try
        {
            DBusConnection connection = await ConnectionAsync().ConfigureAwait(false);
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Service, member: "CloseNotification", signature: "u");
                writer.WriteUInt32(id);
                message = writer.CreateMessage();
            }
            await connection.CallMethodAsync(message).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn("notify", $"notification not closed: {e.Message}");
        }
    }

    private static async Task<DBusConnection> ConnectionAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                var connection = new DBusConnection(DBusAddress.Session!);
                await connection.ConnectAsync().ConfigureAwait(false);
                await connection.AddMatchAsync(
                    new MatchRule { Type = MessageType.Signal, Interface = Service, Member = "ActionInvoked", Path = ObjectPath },
                    static (Message m, object? _) =>
                    {
                        Reader reader = m.GetBodyReader();
                        return (reader.ReadUInt32(), reader.ReadString());
                    },
                    static (Notification<(uint Id, string Key)> n) =>
                    {
                        if (n.HasValue)
                        {
                            ActionInvoked?.Invoke(n.Value.Id, n.Value.Key);
                        }
                    },
                    emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
                await connection.AddMatchAsync(
                    new MatchRule { Type = MessageType.Signal, Interface = Service, Member = "NotificationClosed", Path = ObjectPath },
                    static (Message m, object? _) =>
                    {
                        Reader reader = m.GetBodyReader();
                        return (reader.ReadUInt32(), reader.ReadUInt32());
                    },
                    static (Notification<(uint Id, uint Reason)> n) =>
                    {
                        // Reason 2: dismissed by the user.
                        if (n.HasValue && n.Value.Reason == 2)
                        {
                            Dismissed?.Invoke(n.Value.Id);
                        }
                    },
                    emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
                _connection = connection;
            }
            return _connection;
        }
        finally
        {
            Gate.Release();
        }
    }
}
