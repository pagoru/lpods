using System.Globalization;
using Tmds.DBus.Protocol;
using MessageWriter = Tmds.DBus.Protocol.MessageWriter;
using WPods.Core;
using WPods.Ui;

namespace LPods.Platform;

/// <summary>
/// The desktop's appearance from the XDG settings portal (org.freedesktop.appearance): dark mode ("color-scheme") and
/// the accent color (GNOME 47 and newer, KDE). Without a portal the app is light with no accent; the pages' own
/// prefers-color-scheme query still follows the desktop.
/// </summary>
internal sealed class DesktopTheme : IDisposable
{
    private const string Service = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string Interface = "org.freedesktop.portal.Settings";
    private const string Namespace = "org.freedesktop.appearance";

    private DBusConnection? _connection;
    private IDisposable? _subscription;

    /// <summary>The desktop prefers a dark appearance.</summary>
    public bool Dark { get; private set; }

    /// <summary>The desktop's accent color as a ramp like Windows' (null when it has none).</summary>
    public AccentRamp? Accent { get; private set; }

    /// <summary>Dark mode or accent changed (any thread).</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync().ConfigureAwait(false);
            _connection = connection;
            _subscription = await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Interface = Interface, Member = "SettingChanged" },
                static (Message m, object? _) =>
                {
                    Reader reader = m.GetBodyReader();
                    string ns = reader.ReadString();
                    string key = reader.ReadString();
                    return (ns, key, reader.ReadVariantValue());
                },
                (Notification<(string Namespace, string Key, VariantValue Value)> n) =>
                {
                    if (n.HasValue && n.Value.Namespace == Namespace)
                    {
                        Apply(n.Value.Key, n.Value.Value);
                        Changed?.Invoke();
                    }
                },
                emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
            if (await ReadAsync(connection, "color-scheme").ConfigureAwait(false) is { } scheme)
            {
                Apply("color-scheme", scheme);
            }
            if (await ReadAsync(connection, "accent-color").ConfigureAwait(false) is { } accent)
            {
                Apply("accent-color", accent);
            }
            Log.Info("ui", $"desktop theme: dark={Dark} accent={Accent?.Accent ?? "none"}");
        }
        catch (Exception e)
        {
            Log.Info("ui", $"no appearance portal ({e.Message}); light theme, no accent");
        }
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _connection?.Dispose();
    }

    private void Apply(string key, VariantValue value)
    {
        value = Unwrap(value);
        switch (key)
        {
            case "color-scheme" when value.Type == VariantValueType.UInt32:
                // 0 no preference, 1 prefer dark, 2 prefer light.
                Dark = value.GetUInt32() == 1;
                break;
            case "accent-color" when value.Type == VariantValueType.Struct && value.Count == 3:
                Accent = FromRgb(value.GetItem(0).GetDouble(), value.GetItem(1).GetDouble(), value.GetItem(2).GetDouble());
                break;
        }
    }

    private static VariantValue Unwrap(VariantValue value)
    {
        while (value.Type == VariantValueType.Variant)
        {
            value = value.GetVariantValue();
        }
        return value;
    }

    private static async Task<VariantValue?> ReadAsync(DBusConnection connection, string key)
    {
        MessageBuffer message;
        using (MessageWriter writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(destination: Service, path: ObjectPath, @interface: Interface, member: "Read", signature: "ss");
            writer.WriteString(Namespace);
            writer.WriteString(key);
            message = writer.CreateMessage();
        }
        try
        {
            return await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            // The key does not exist on this desktop.
            return null;
        }
    }

    /// <summary>Components 0 to 1; outside that range the desktop has no accent.</summary>
    internal static AccentRamp? FromRgb(double r, double g, double b)
    {
        if (r is < 0 or > 1 || g is < 0 or > 1 || b is < 0 or > 1)
        {
            return null;
        }
        return new AccentRamp(
            Hex(r, g, b),
            Mix(r, g, b, 0, 0.15), Mix(r, g, b, 0, 0.3), Mix(r, g, b, 0, 0.45),
            Mix(r, g, b, 1, 0.15), Mix(r, g, b, 1, 0.3), Mix(r, g, b, 1, 0.45));
    }

    /// <summary>Moves the color toward black (0) or white (1) by <paramref name="amount"/>.</summary>
    private static string Mix(double r, double g, double b, double toward, double amount) =>
        Hex(r + ((toward - r) * amount), g + ((toward - g) * amount), b + ((toward - b) * amount));

    private static string Hex(double r, double g, double b) =>
        string.Create(CultureInfo.InvariantCulture, $"#{Channel(r):x2}{Channel(g):x2}{Channel(b):x2}");

    private static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
