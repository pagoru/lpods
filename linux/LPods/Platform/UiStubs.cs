using WPods.Core;

namespace WPods.Audio
{
    // Upstream's mixer types (src/WPods/Audio/Mixer.cs), for the shared protocol writer. The mixer is not ported.
    public enum MixerFlow
    {
        Output = 0,
        Input = 1,
    }

    [Flags]
    public enum MixerState
    {
        Active = 1,
        Disabled = 2,
        NotPresent = 4,
        Unplugged = 8,
    }

    public sealed record MixerDevice(string Id, string Name, MixerFlow Flow, MixerState State, ulong Pair, bool OwnMic);
}

namespace WPods.Ui
{
    internal enum HotkeyStatus
    {
        Ok,
        Toggle,
        Conflict,
        Taken,
        Reserved,
    }

    internal sealed record AccentRamp(string Accent, string Dark1, string Dark2, string Dark3, string Light1, string Light2, string Light3);

    /// <summary>
    /// Upstream's global hotkeys (RegisterHotKey) have no Wayland equivalent: none are bound. Linux users bind
    /// `lpods` commands in their desktop's keyboard settings.
    /// </summary>
    internal sealed class Hotkeys
    {
        public IReadOnlyDictionary<string, Shortcut> Bindings { get; } = new Dictionary<string, Shortcut>();

        public HotkeyStatus StatusOf(string action) => default;

        public IEnumerable<string> SharedWith(string action) => [];
    }
}
