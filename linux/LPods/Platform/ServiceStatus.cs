namespace WPods.Core;

/// <summary>Linux stand-in for upstream's ServiceStatus. There is no background service on Linux.</summary>
internal static class ServiceStatus
{
    /// <summary>HD voice needs upstream's HCI proxy, which has no Linux counterpart yet.</summary>
    public static bool HdVoiceAllowed(string? modelNumber) => false;
}
