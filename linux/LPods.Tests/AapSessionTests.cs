using System.Text;
using WPods.Core;

namespace LPods.Tests;

/// <summary>
/// Upstream's AapSession, unchanged, over the Linux bridge (ServicePipes.Bridged) to a fake AirPods: proves the shared
/// protocol code runs on Linux and the bridge keeps the pipe's message framing.
/// </summary>
public sealed class AapSessionTests
{
    private const ulong Address = 0x40DA5C668CB3;

    [Fact]
    public async Task Handshake_reports_and_settings_flow_through_the_bridge()
    {
        using var home = new TempHome();
        (FakeAirPods airPods, int channel) = FakeAirPods.Create();
        using (airPods)
        {
            var stream = Platform.Bridged(channel);
            await using var session = new AapSession(Address, stream, new SettingsStore(), productId: 0x2014);
            session.Start();

            // Host opens with the handshake; the AirPods answer with anything.
            await airPods.ExpectAsync(Aap.Handshake);
            airPods.Send(0x01, 0x00, 0x04, 0x00, 0x00, 0x00);
            await airPods.ExpectAsync(Aap.FeatureFlags);
            await airPods.ExpectAsync(p => p.Length == 10 && p[4] == 0x0F);

            airPods.Send(DeviceInfo("Pablo's AirPods Pro", "A2931"));
            airPods.Send(Setting(Aap.ListeningModeId, (byte)ListeningMode.NoiseCancellation));
            airPods.Send(Setting(Aap.ConversationAwarenessId, 0x01));
            airPods.Send(Battery((Aap.RightComponent, 80, 0x02), (Aap.LeftComponent, 75, 0x02), (0x08, 40, 0x01)));
            airPods.Send(0x04, 0x00, 0x04, 0x00, 0x06, 0x00, 0x00, 0x01);

            await WaitAsync(() => session.State.InitialReportDone, TimeSpan.FromSeconds(10));
            DeviceState s = session.State;
            Assert.Equal(SessionStatus.Ready, s.Status);
            Assert.Equal("Pablo's AirPods Pro", s.Name);
            Assert.Equal("AirPods Pro 2", s.ModelName);
            Assert.Equal(ListeningMode.NoiseCancellation, s.ListeningMode);
            Assert.True(s.ConversationAwareness);
            Assert.Equal((byte)75, s.LeftBattery.Percent);
            Assert.Equal((byte)80, s.RightBattery.Percent);
            Assert.Equal((byte)40, s.CaseBattery.Percent);
            Assert.Equal(BatteryStatus.Charging, s.CaseBattery.Status);
            // Right is the primary bud (listed first): primary in ear, secondary out.
            Assert.Equal(EarStatus.InEar, s.RightEar);
            Assert.Equal(EarStatus.OutOfEar, s.LeftEar);

            // Host -> AirPods: a setting arrives as the exact AAP packet.
            Assert.True(await session.SetListeningModeAsync(ListeningMode.Transparency, TestContext.Current.CancellationToken));
            await airPods.ExpectAsync(Aap.Setting(Aap.ListeningModeId, (byte)ListeningMode.Transparency));

            // The channel drops: the session ends.
            airPods.Close();
            Task<SessionEnd> end = session.Completion;
            Assert.Same(end, await Task.WhenAny(end, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        }
    }

    private static byte[] Setting(byte id, byte value) => Aap.Setting(id, value);

    private static byte[] DeviceInfo(string name, string model)
    {
        var packet = new List<byte> { 0x04, 0x00, 0x04, 0x00, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        packet.AddRange(Encoding.UTF8.GetBytes(name));
        packet.Add(0);
        packet.AddRange(Encoding.UTF8.GetBytes(model));
        packet.Add(0);
        packet.AddRange(Encoding.UTF8.GetBytes("Apple Inc."));
        packet.Add(0);
        return [.. packet];
    }

    private static byte[] Battery(params (byte Component, byte Level, byte Status)[] parts)
    {
        var packet = new List<byte> { 0x04, 0x00, 0x04, 0x00, 0x04, 0x00, (byte)parts.Length };
        foreach ((byte component, byte level, byte status) in parts)
        {
            packet.AddRange([component, 0x01, level, status, 0x01]);
        }
        return [.. packet];
    }

    internal static async Task WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(50);
        }
    }
}

internal static class Platform
{
    public static System.IO.Pipes.NamedPipeClientStream Bridged(int channel) =>
        ServicePipes.Bridged(0x40DA5C668CB3, channel, out int error) ?? throw new IOException($"bridge failed: {error}");
}
