namespace WPods.Core;

/// <summary>
/// Linux stand-in for upstream's HandsFree. On Windows, "Disable Hands-Free mode" is on by default because WPods brings
/// the AirPods mic over A2DP itself (stereo mic). That mic path is not ported yet, so on Linux Hands-Free stays under
/// WirePlumber's control: it switches the card to the headset profile when an app opens the mic. Use
/// `lpods profile a2dp|headset` to pin a profile by hand.
/// </summary>
internal static class HandsFree
{
    public static void SetEnabled(ulong address, bool enabled, Action? changing = null) =>
        Log.Info("handsfree", $"{Mac.Format(address)}: Hands-Free {(enabled ? "on" : "off")} requested; left to WirePlumber on Linux");
}
