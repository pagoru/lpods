using System.Text.Json.Nodes;
using LPods.Platform;
using WPods.Core;

namespace LPods;

/// <summary>Executes one control command against the running app. Throws <see cref="CommandException"/> on bad input.</summary>
internal sealed class Commands(ConnectionManager connections)
{
    public const string Help = """
        lpods [open]                       show the window (starts LPods if it is not running)
        lpods daemon                       run in the background, in the tray
        lpods status [--json]              battery, ear and settings of the connected AirPods
        lpods devices                      paired AirPods
        lpods watch                        print status as JSON on every change
        lpods waybar                       print Waybar/i3blocks JSON on every change
        lpods connect [ADDRESS]            connect (pages the AirPods if needed)
        lpods disconnect
        lpods noise off|nc|transparency|adaptive|next
        lpods set SETTING VALUE            change an AirPods setting:
            conversation-awareness on|off   personalized-volume on|off   ear-detection on|off
            one-bud-anc on|off              allow-off on|off             hearing-aid on|off
            media-assist-calls on|off       media-assist-media on|off    heart-rate on|off
            mic-side automatic|left|right   press-speed default|slower|slowest
            hold-duration default|shorter|shortest   tone-volume 0-100
            stem-modes MODE,MODE[,...]      (at least two of off, nc, transparency, adaptive)
        lpods profile a2dp|headset         pin the PipeWire profile of the connected AirPods
        """;

    /// <summary>Shows the main window; null when there is no desktop session.</summary>
    public Action? OpenWindow { get; set; }

    public async Task<JsonNode?> RunAsync(string[] args)
    {
        string command = args.Length > 0 ? args[0] : "status";
        switch (command)
        {
            case "open":
                return OpenWindow is { } open ? Open(open) : throw new CommandException("no desktop session: the window needs a display");
            case "status":
                return Status.Build(connections);
            case "devices":
                return Status.Build(connections)["devices"]!.DeepClone();
            case "connect":
                return await ConnectAsync(args.Length > 1 ? args[1] : null).ConfigureAwait(false);
            case "disconnect":
                return await connections.DisconnectAsync().ConfigureAwait(false) ? Ok() : throw new CommandException("not connected");
            case "noise":
                return await NoiseAsync(Arg(args, 1, "off|nc|transparency|adaptive|next")).ConfigureAwait(false);
            case "set":
                return await SetAsync(Arg(args, 1, "SETTING"), Arg(args, 2, "VALUE")).ConfigureAwait(false);
            case "profile":
                return Profile(Arg(args, 1, "a2dp|headset"));
            default:
                throw new CommandException($"unknown command '{command}'\n\n{Help}");
        }
    }

    private static JsonNode Open(Action open)
    {
        open();
        return Ok();
    }

    private async Task<JsonNode?> ConnectAsync(string? text)
    {
        ulong address;
        if (text is null)
        {
            address = connections.LastUsed != 0 ? connections.LastUsed : connections.GetDevices().FirstOrDefault()?.Address ?? 0;
            if (address == 0)
            {
                throw new CommandException("no paired AirPods; pair them in your Bluetooth settings first");
            }
        }
        else if (!Mac.TryParse(text, out address))
        {
            throw new CommandException($"not a Bluetooth address: {text}");
        }
        if (!connections.GetDevices().Any(d => d.Address == address && d.LinkUp))
        {
            // Like upstream's nearby Connect: page the AirPods, then the link-up event attaches them.
            int hr = WPods.Nearby.NearbyNative.wpn_bt_connect_audio(address);
            if (hr < 0)
            {
                throw new CommandException("could not connect; are the AirPods out of the case and near?");
            }
            return Ok();
        }
        return await connections.ConnectAsync(address).ConfigureAwait(false) ? Ok() : throw new CommandException("connected, but the AirPods did not answer yet; retrying in the background");
    }

    private async Task<JsonNode?> NoiseAsync(string value)
    {
        AapSession session = Session();
        ListeningMode mode;
        if (value == "next")
        {
            DeviceState s = session.State;
            StemModes cycle = s.StemModes is { } stem && Aap.IsValidStemModes(stem) ? stem
                : StemModes.NoiseCancellation | StemModes.Transparency
                    | ((s.Capabilities & DeviceCapabilities.Adaptive) != 0 ? StemModes.Adaptive : 0);
            ListeningMode[] order = [.. new[] { ListeningMode.Off, ListeningMode.NoiseCancellation, ListeningMode.Transparency, ListeningMode.Adaptive }
                .Where(m => (cycle & ToStem(m)) != 0)];
            int index = s.ListeningMode is { } current ? Array.IndexOf(order, current) : -1;
            mode = order[(index + 1) % order.Length];
        }
        else
        {
            mode = ParseNoise(value);
        }
        return await session.SetListeningModeAsync(mode).ConfigureAwait(false)
            ? new JsonObject { ["noiseControl"] = Status.NoiseName(mode) }
            : throw new CommandException("not written; the AirPods may have just disconnected");
    }

    private async Task<JsonNode?> SetAsync(string setting, string value)
    {
        AapSession session = Session();
        bool written;
        try
        {
            written = setting switch
            {
                "conversation-awareness" => await session.SetConversationAwarenessAsync(OnOff(value)).ConfigureAwait(false),
                "personalized-volume" => await session.SetPersonalizedVolumeAsync(OnOff(value)).ConfigureAwait(false),
                "ear-detection" => await session.SetEarDetectionAsync(OnOff(value)).ConfigureAwait(false),
                "one-bud-anc" => await session.SetOneBudAncAsync(OnOff(value)).ConfigureAwait(false),
                "allow-off" => await session.SetAllowOffAsync(OnOff(value)).ConfigureAwait(false),
                "hearing-aid" => await session.SetHearingAidAsync(OnOff(value)).ConfigureAwait(false),
                "media-assist-calls" => await session.SetMediaAssistCallsAsync(OnOff(value)).ConfigureAwait(false),
                "media-assist-media" => await session.SetMediaAssistMediaAsync(OnOff(value)).ConfigureAwait(false),
                "heart-rate" => await session.SetHeartRateAsync(OnOff(value)).ConfigureAwait(false),
                "mic-side" => await session.SetMicSideAsync(Parse<MicSide>(value)).ConfigureAwait(false),
                "press-speed" => await session.SetPressSpeedAsync(Parse<PressSpeed>(value)).ConfigureAwait(false),
                "hold-duration" => await session.SetHoldDurationAsync(Parse<HoldDuration>(value)).ConfigureAwait(false),
                "tone-volume" => await session.SetToneVolumeAsync(byte.TryParse(value, out byte v) && v <= 100 ? v : throw new CommandException("tone-volume is 0-100")).ConfigureAwait(false),
                "stem-modes" => await session.SetStemModesAsync(ParseStem(value)).ConfigureAwait(false),
                _ => throw new CommandException($"unknown setting '{setting}'\n\n{Help}"),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new CommandException(e.Message);
        }
        return written ? Ok() : throw new CommandException("not written; the AirPods may have just disconnected");
    }

    private JsonNode? Profile(string value)
    {
        AapSession session = Session();
        string card = PipeWire.CardName(session.Address);
        string prefix = value switch
        {
            "a2dp" => "a2dp-sink",
            "headset" => "headset-head-unit",
            _ => throw new CommandException("profile is a2dp or headset"),
        };
        try
        {
            string profile = PipeWire.FindProfile(card, p => p.StartsWith(prefix, StringComparison.Ordinal))
                ?? throw new CommandException($"{card} has no {value} profile");
            PipeWire.Pactl("set-card-profile", card, profile);
            return new JsonObject { ["profile"] = profile };
        }
        catch (InvalidOperationException e)
        {
            throw new CommandException(e.Message);
        }
    }

    private AapSession Session() => connections.Session is { State.Status: SessionStatus.Ready } session
        ? session
        : throw new CommandException("no AirPods connected");

    private static JsonObject Ok() => new() { ["ok"] = true };

    private static string Arg(string[] args, int index, string name) =>
        args.Length > index ? args[index] : throw new CommandException($"missing {name}\n\n{Help}");

    private static bool OnOff(string value) => value switch
    {
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => throw new CommandException($"expected on or off, got '{value}'"),
    };

    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out T result) && Enum.IsDefined(result)
            ? result
            : throw new CommandException($"expected {string.Join('|', Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}, got '{value}'");

    private static ListeningMode ParseNoise(string value) => value switch
    {
        "off" => ListeningMode.Off,
        "nc" or "anc" or "noise-cancellation" => ListeningMode.NoiseCancellation,
        "transparency" => ListeningMode.Transparency,
        "adaptive" => ListeningMode.Adaptive,
        _ => throw new CommandException($"expected off|nc|transparency|adaptive|next, got '{value}'"),
    };

    private static StemModes ToStem(ListeningMode mode) => mode switch
    {
        ListeningMode.Off => StemModes.Off,
        ListeningMode.NoiseCancellation => StemModes.NoiseCancellation,
        ListeningMode.Transparency => StemModes.Transparency,
        _ => StemModes.Adaptive,
    };

    private static StemModes ParseStem(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Aggregate(StemModes.None, (all, name) => all | ToStem(ParseNoise(name)));
}

internal sealed class CommandException(string message) : Exception(message);
