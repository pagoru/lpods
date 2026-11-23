using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace LPods;

/// <summary>Talks to the running lpods over its control socket.</summary>
internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args[0] is "help" or "-h" or "--help")
        {
            Console.WriteLine(Commands.Help);
            return 0;
        }
        bool json = args.Contains("--json");
        args = [.. args.Where(a => a != "--json")];
        bool waybar = args[0] == "waybar";
        string[] request = waybar ? ["watch"] : args;
        try
        {
            await foreach (JsonNode reply in Control.SendAsync(request).ConfigureAwait(false))
            {
                if (request is ["watch"])
                {
                    Console.WriteLine(waybar ? Waybar(reply) : reply.ToJsonString());
                    continue;
                }
                if (reply["ok"]?.GetValue<bool>() != true)
                {
                    Console.Error.WriteLine(reply["error"]?.GetValue<string>() ?? "failed");
                    return 1;
                }
                JsonNode? result = reply["result"];
                Console.WriteLine(json || result is null ? result?.ToJsonString() ?? "" : args[0] switch
                {
                    "status" => Pretty(result),
                    "devices" => Devices(result),
                    _ => "ok",
                });
            }
            return 0;
        }
        catch (SocketException)
        {
            Console.Error.WriteLine("lpods is not running. Start it with `lpods daemon` (or `systemctl --user enable --now lpods`).");
            return 2;
        }
    }

    private static string Pretty(JsonNode status)
    {
        var text = new StringBuilder();
        if (status["session"] is not JsonObject s)
        {
            text.AppendLine("No AirPods connected.");
            text.Append(Devices(status["devices"]!));
            return text.ToString().TrimEnd();
        }
        text.AppendLine($"{s["name"]} ({s["model"] ?? "AirPods"}) {s["address"]}{(s["ready"]?.GetValue<bool>() == true ? "" : " [checking]")}");
        foreach ((string part, JsonNode? b) in s["battery"]!.AsObject())
        {
            text.AppendLine($"  {Title(part),-10} {Battery(b)}{(s["ear"]?[part] is { } ear ? $"  {ear}" : "")}");
        }
        if (s["noiseControl"] is { } noise)
        {
            text.AppendLine($"  {"Noise",-10} {noise}");
        }
        if (s["heartRate"] is JsonObject hr)
        {
            text.AppendLine($"  {"Heart rate",-10} {hr["bpm"]?.ToString() ?? "-"} bpm ({hr["status"]})");
        }
        foreach ((string name, JsonNode? value) in s["settings"]!.AsObject())
        {
            if (value is not null)
            {
                text.AppendLine($"  {name} = {(value.GetValueKind() == System.Text.Json.JsonValueKind.True ? "on" : value.GetValueKind() == System.Text.Json.JsonValueKind.False ? "off" : value)}");
            }
        }
        return text.ToString().TrimEnd();
    }

    private static string Devices(JsonNode devices)
    {
        var text = new StringBuilder();
        foreach (JsonNode? d in devices.AsArray())
        {
            string state = d!["attached"]!.GetValue<bool>() ? "attached" : d["connected"]!.GetValue<bool>() ? "connected" : "not connected";
            text.AppendLine($"{d["address"]}  {d["name"]}  {d["model"] ?? ""}  {state}");
        }
        return text.Length == 0 ? "No paired AirPods. Pair them in your Bluetooth settings first." : text.ToString().TrimEnd();
    }

    private static string Battery(JsonNode? b) => b is null ? "-"
        : b["connected"]?.GetValue<bool>() == false ? "disconnected"
        : $"{b["percent"]}%{(b["charging"]?.GetValue<bool>() == true ? " charging" : "")}";

    private static string Title(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Waybar custom module line: {"text","tooltip","class"}.</summary>
    private static string Waybar(JsonNode status)
    {
        if (status["session"] is not JsonObject s)
        {
            return new JsonObject { ["text"] = "", ["tooltip"] = "No AirPods connected", ["class"] = "disconnected" }.ToJsonString();
        }
        JsonObject battery = s["battery"]!.AsObject();
        int? lowest = battery.Where(p => p.Key != "case")
            .Select(p => p.Value?["percent"]?.GetValue<int?>())
            .Where(p => p is not null)
            .Min();
        return new JsonObject
        {
            ["text"] = lowest is { } l ? $"{l}%" : "",
            ["tooltip"] = Pretty(status),
            ["class"] = lowest is < 20 ? "low" : "connected",
            ["percentage"] = lowest,
        }.ToJsonString();
    }
}
