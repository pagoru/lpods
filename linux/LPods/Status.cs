using System.Text.Json.Nodes;
using WPods.Core;

namespace LPods;

/// <summary>JSON snapshots of the paired AirPods and the attached session, for the control socket.</summary>
internal static class Status
{
    public static JsonObject Build(ConnectionManager connections)
    {
        AapSession? session = connections.ShownSession;
        var devices = new JsonArray();
        foreach (KnownDevice d in connections.GetDevices())
        {
            devices.Add(new JsonObject
            {
                ["address"] = Format(d.Address),
                ["name"] = d.Name,
                ["model"] = Models.FromModelNumber(d.ModelNumber)?.Name,
                ["modelNumber"] = d.ModelNumber,
                ["connected"] = d.LinkUp,
                ["attached"] = d.Attached,
                ["idle"] = d.Idle == IdleReason.None ? null : Camel(d.Idle),
            });
        }
        return new JsonObject
        {
            ["devices"] = devices,
            ["session"] = session is null ? null : Session(session),
        };
    }

    private static JsonObject Session(AapSession session)
    {
        DeviceState s = session.State;
        bool single = (Models.FromModelNumber(s.ModelNumber) ?? Models.Unknown).SingleBattery;
        var battery = new JsonObject();
        if (single)
        {
            battery["headphone"] = Battery(s.HeadphoneBattery);
        }
        else
        {
            battery["left"] = Battery(s.LeftBattery);
            battery["right"] = Battery(s.RightBattery);
            battery["case"] = Battery(s.CaseBattery);
        }
        return new JsonObject
        {
            ["address"] = Format(session.Address),
            ["status"] = Camel(s.Status),
            ["ready"] = s.InitialReportDone,
            ["name"] = s.Name,
            ["model"] = s.ModelName,
            ["modelNumber"] = s.ModelNumber,
            ["battery"] = battery,
            ["ear"] = single
                ? new JsonObject { ["headphone"] = Ear(s.PrimaryEar) }
                : new JsonObject { ["left"] = Ear(s.LeftEar), ["right"] = Ear(s.RightEar) },
            ["noiseControl"] = s.ListeningMode is { } mode ? NoiseName(mode) : null,
            ["capabilities"] = new JsonArray([.. Enum.GetValues<DeviceCapabilities>()
                .Where(c => c != DeviceCapabilities.None && (s.Capabilities & c) == c && System.Numerics.BitOperations.PopCount((uint)c) == 1)
                .Select(c => (JsonNode)Camel(c))]),
            ["settings"] = new JsonObject
            {
                ["conversation-awareness"] = s.ConversationAwareness,
                ["personalized-volume"] = s.PersonalizedVolume,
                ["ear-detection"] = s.EarDetection,
                ["one-bud-anc"] = s.OneBudAnc,
                ["allow-off"] = s.AllowOff,
                ["mic-side"] = s.MicSide is { } side ? Camel(side) : null,
                ["press-speed"] = s.PressSpeed is { } speed ? Camel(speed) : null,
                ["hold-duration"] = s.HoldDuration is { } hold ? Camel(hold) : null,
                ["tone-volume"] = s.ToneVolume,
                ["stem-modes"] = s.StemModes is { } stem ? StemNames(stem) : null,
                ["hearing-aid"] = s.HearingAidEnrolled == true ? s.HearingAidEnabled : null,
                ["media-assist-calls"] = s.MediaAssistCalls,
                ["media-assist-media"] = s.MediaAssistMedia,
            },
            ["heartRate"] = s.HeartRateSupported
                ? new JsonObject { ["status"] = Camel(s.HeartRate), ["bpm"] = s.HeartRateBpm }
                : null,
        };
    }

    public static string NoiseName(ListeningMode mode) => mode switch
    {
        ListeningMode.Off => "off",
        ListeningMode.NoiseCancellation => "nc",
        ListeningMode.Transparency => "transparency",
        _ => "adaptive",
    };

    public static string StemNames(StemModes modes) => string.Join(',',
        new[] { StemModes.Off, StemModes.NoiseCancellation, StemModes.Transparency, StemModes.Adaptive }
            .Where(m => (modes & m) != 0)
            .Select(m => NoiseName(m switch
            {
                StemModes.Off => ListeningMode.Off,
                StemModes.NoiseCancellation => ListeningMode.NoiseCancellation,
                StemModes.Transparency => ListeningMode.Transparency,
                _ => ListeningMode.Adaptive,
            })));

    private static JsonObject? Battery(Battery b) => b.Percent is null && b.Status != BatteryStatus.Disconnected
        ? null
        : new JsonObject { ["percent"] = b.Percent, ["charging"] = b.Status == BatteryStatus.Charging, ["connected"] = b.Status != BatteryStatus.Disconnected };

    private static string? Ear(EarStatus? ear) => ear is { } e ? Camel(e) : null;

    public static string Format(ulong address)
    {
        string mac = Mac.Format(address);
        return string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
    }

    private static string Camel<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
