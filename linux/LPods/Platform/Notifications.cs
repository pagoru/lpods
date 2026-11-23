using Tmds.DBus.Protocol;
using WPods.Core;

namespace LPods.Platform;

/// <summary>Desktop notifications (org.freedesktop.Notifications on the session bus). Failures are logged only.</summary>
internal static class Notifications
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DBusConnection? _connection;

    /// <summary>Shows a notification; <paramref name="replaces"/> is the id of one to update (0 = new). Returns its id.</summary>
    public static async Task<uint> ShowAsync(string summary, string body, uint replaces = 0, string icon = "audio-headphones")
    {
        try
        {
            DBusConnection connection = await ConnectionAsync().ConfigureAwait(false);
            MessageBuffer message;
            using (MessageWriter writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    destination: "org.freedesktop.Notifications", path: "/org/freedesktop/Notifications",
                    @interface: "org.freedesktop.Notifications", member: "Notify", signature: "susssasa{sv}i");
                writer.WriteString("LPods");
                writer.WriteUInt32(replaces);
                writer.WriteString(icon);
                writer.WriteString(summary);
                writer.WriteString(body);
                writer.WriteArray(Array.Empty<string>());
                writer.WriteDictionary(new Dictionary<string, VariantValue> { ["desktop-entry"] = "lpods" });
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

    private static async Task<DBusConnection> ConnectionAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is null)
            {
                var connection = new DBusConnection(DBusAddress.Session!);
                await connection.ConnectAsync().ConfigureAwait(false);
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
