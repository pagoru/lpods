using LPods.Platform;

namespace LPods.Tests;

// MPRIS and the tray share the process-wide session bus address.
[Collection("session bus")]
public sealed class MediaTests
{
    [Fact]
    public void Pactl_volume_is_the_mean_of_the_channels()
    {
        Assert.Equal(0.65f, PipeWire.ParseVolume("Volume: front-left: 42598 /  65% / -11.23 dB,   front-right: 42598 /  65% / -11.23 dB")!.Value, 0.001f);
        Assert.Equal(0.5f, PipeWire.ParseVolume("Volume: mono: 32768 /  50% / -18.06 dB")!.Value, 0.001f);
        Assert.Null(PipeWire.ParseVolume(""));
    }

    [Fact]
    public void Sink_names_give_the_address() =>
        Assert.Equal(0x40DA5C668CB3UL, PipeWire.AddressOfSink("bluez_output.40_DA_5C_66_8C_B3.1"));

    [Fact]
    public async Task Pauses_playing_players_and_resumes_only_those()
    {
        using MockBus? bus = await MockBus.StartSessionAsync();
        Assert.SkipWhen(bus is null, "needs dbus-daemon and python-dbusmock (LPODS_TEST_PYTHON picks the interpreter)");
        await bus!.RunScriptAsync("mpris_player.py", "music", "Playing");
        await bus.RunScriptAsync("mpris_player.py", "video", "Paused");

        Assert.Equal(1, await Task.Run(Mpris.PauseAll, TestContext.Current.CancellationToken));
        // The player the user had paused stays paused; the one LPods paused plays again.
        Assert.Equal(1, await Task.Run(Mpris.ResumePaused, TestContext.Current.CancellationToken));
        Assert.Equal(0, await Task.Run(Mpris.ResumePaused, TestContext.Current.CancellationToken));
    }
}
