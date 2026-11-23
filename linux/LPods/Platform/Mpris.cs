using Tmds.DBus.Protocol;
using WPods.Core;

namespace LPods.Platform;

/// <summary>
/// Media players on the session bus (MPRIS, org.mpris.MediaPlayer2.*): the Linux side of upstream's wpn_media_*
/// helpers. Pauses what is playing, remembers it, and resumes only those players.
/// </summary>
internal static class Mpris
{
    private const string Prefix = "org.mpris.MediaPlayer2.";
    private const string Path = "/org/mpris/MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Paused = new(StringComparer.Ordinal);
    private static DBusConnection? _connection;

    /// <summary>Pauses every playing player; the number paused, or -1 on error.</summary>
    public static int PauseAll()
    {
        try
        {
            return Run(async connection =>
            {
                int count = 0;
                foreach (string player in await PlayersAsync(connection).ConfigureAwait(false))
                {
                    if (await StatusAsync(connection, player).ConfigureAwait(false) == "Playing")
                    {
                        await CallAsync(connection, player, "Pause").ConfigureAwait(false);
                        lock (Paused)
                        {
                            Paused.Add(player);
                        }
                        count++;
                    }
                }
                return count;
            });
        }
        catch (Exception e)
        {
            Log.Warn("mpris", $"pause failed: {e.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Resumes the players this paused that are still paused, unless something else is playing now; the number
    /// resumed, or -1 on error. Forgets them either way.
    /// </summary>
    public static int ResumePaused()
    {
        string[] paused;
        lock (Paused)
        {
            paused = [.. Paused];
            Paused.Clear();
        }
        if (paused.Length == 0)
        {
            return 0;
        }
        try
        {
            return Run(async connection =>
            {
                List<string> players = await PlayersAsync(connection).ConfigureAwait(false);
                foreach (string player in players)
                {
                    if (await StatusAsync(connection, player).ConfigureAwait(false) == "Playing")
                    {
                        return 0;
                    }
                }
                int count = 0;
                foreach (string player in paused.Where(players.Contains))
                {
                    if (await StatusAsync(connection, player).ConfigureAwait(false) == "Paused")
                    {
                        await CallAsync(connection, player, "Play").ConfigureAwait(false);
                        count++;
                    }
                }
                return count;
            });
        }
        catch (Exception e)
        {
            Log.Warn("mpris", $"resume failed: {e.Message}");
            return -1;
        }
    }

    public static void Forget()
    {
        lock (Paused)
        {
            Paused.Clear();
        }
    }

    private static T Run<T>(Func<DBusConnection, Task<T>> action) => Task.Run(async () =>
    {
        DBusConnection connection = await ConnectionAsync().ConfigureAwait(false);
        return await action(connection).ConfigureAwait(false);
    }).GetAwaiter().GetResult();

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

    private static async Task<List<string>> PlayersAsync(DBusConnection connection) =>
        [.. (await connection.ListServicesAsync().ConfigureAwait(false)).Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))];

    private static async Task<string?> StatusAsync(DBusConnection connection, string player)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: player, path: Path, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
            writer.WriteString(PlayerInterface);
            writer.WriteString("PlaybackStatus");
            message = writer.CreateMessage();
        }
        try
        {
            VariantValue value = await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
            while (value.Type == VariantValueType.Variant)
            {
                value = value.GetVariantValue();
            }
            return value.Type == VariantValueType.String ? value.GetString() : null;
        }
        catch (DBusErrorReplyException)
        {
            return null;
        }
    }

    private static async Task CallAsync(DBusConnection connection, string player, string member)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: player, path: Path, @interface: PlayerInterface, member: member);
            message = writer.CreateMessage();
        }
        await connection.CallMethodAsync(message).ConfigureAwait(false);
    }
}
