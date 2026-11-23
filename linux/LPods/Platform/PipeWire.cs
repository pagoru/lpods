using System.Diagnostics;
using WPods.Core;

namespace LPods.Platform;

/// <summary>
/// Audio through pactl, which talks to PipeWire's PulseAudio server (Fedora's default; package pulseaudio-utils).
/// Bluetooth objects are named after the address: bluez_card.40_DA_5C_66_8C_B3, bluez_output.40_DA_5C_66_8C_B3.1.
/// </summary>
internal static class PipeWire
{
    public static string AddressPart(ulong address)
    {
        string mac = Mac.Format(address);
        return string.Join('_', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2)));
    }

    public static string CardName(ulong address) => "bluez_card." + AddressPart(address);

    /// <summary>The address of a bluez sink name, or 0.</summary>
    public static ulong AddressOfSink(string sink)
    {
        const string prefix = "bluez_output.";
        if (!sink.StartsWith(prefix, StringComparison.Ordinal) || sink.Length < prefix.Length + 17)
        {
            return 0;
        }
        return Mac.TryParse(sink.AsSpan(prefix.Length, 17).ToString().Replace('_', ':'), out ulong address) ? address : 0;
    }

    /// <summary>Volume of the default sink as a fraction (1.0 = 100 %), the mean of its channels; null when unreadable.</summary>
    public static float? DefaultSinkVolume()
    {
        return ParseVolume(Pactl("get-sink-volume", "@DEFAULT_SINK@"));
    }

    /// <summary>"Volume: front-left: 42598 /  65% / -11.23 dB,   front-right: 42598 /  65% / -11.23 dB" -> 0.65.</summary>
    internal static float? ParseVolume(string text)
    {
        var raw = new List<int>();
        foreach (string part in text.Split(','))
        {
            int colon = part.LastIndexOf(':');
            int slash = part.IndexOf('/', StringComparison.Ordinal);
            if (colon >= 0 && slash > colon && int.TryParse(part.AsSpan(colon + 1, slash - colon - 1).Trim(), out int value))
            {
                raw.Add(value);
            }
        }
        return raw.Count == 0 ? null : (float)raw.Average() / NormalVolume;
    }

    public static void SetDefaultSinkVolume(float fraction) =>
        Pactl("set-sink-volume", "@DEFAULT_SINK@", ((int)MathF.Round(Math.Clamp(fraction, 0f, 1.5f) * NormalVolume)).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private const int NormalVolume = 65536;

    public static string? DefaultSink() => Pactl("get-default-sink").Trim() is { Length: > 0 } sink ? sink : null;

    public static string? ActiveProfile(string card) => CardBlock(card)
        .Select(l => l.Trim())
        .FirstOrDefault(l => l.StartsWith("Active Profile:", StringComparison.Ordinal))?["Active Profile:".Length..].Trim();

    public static string? FindProfile(string card, Func<string, bool> match)
    {
        bool inProfiles = false;
        foreach (string line in CardBlock(card))
        {
            string trimmed = line.Trim();
            if (trimmed == "Profiles:")
            {
                inProfiles = true;
                continue;
            }
            if (inProfiles)
            {
                if (!line.StartsWith("\t\t", StringComparison.Ordinal))
                {
                    break;
                }
                int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && match(trimmed[..colon]) && !trimmed.Contains("available: no", StringComparison.Ordinal))
                {
                    return trimmed[..colon];
                }
            }
        }
        return null;
    }

    // The "Card #N" block of `pactl list cards` whose Name is the card.
    private static IEnumerable<string> CardBlock(string card)
    {
        List<string> block = [];
        bool match = false;
        foreach (string line in Pactl("list", "cards").Split('\n'))
        {
            if (line.StartsWith("Card #", StringComparison.Ordinal))
            {
                if (match)
                {
                    return block;
                }
                block.Clear();
            }
            block.Add(line);
            if (line.Trim() == "Name: " + card)
            {
                match = true;
            }
        }
        return match ? block : [];
    }

    /// <summary>Runs pactl; returns stdout, throws with stderr on failure.</summary>
    public static string Pactl(params string[] args)
    {
        var info = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["LC_ALL"] = "C";
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using Process process = Process.Start(info) ?? throw new InvalidOperationException("pactl did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"pactl {string.Join(' ', args)}: {stderr.Trim()}");
        }
        return stdout.GetAwaiter().GetResult();
    }
}
