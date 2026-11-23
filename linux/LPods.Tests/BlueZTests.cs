using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WPods.Core;
using WPods.Nearby;

namespace LPods.Tests;

/// <summary>The Linux platform layer against a mocked BlueZ (python-dbusmock) on a private system bus.</summary>
public sealed class BlueZTests
{
    private const string AirPods = "40:DA:5C:66:8C:B3";
    private const ulong AirPodsAddress = 0x40DA5C668CB3;
    private static readonly TaskCompletionSource<(ulong Address, byte Type, byte[] Data)> Advert = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Paired_AirPods_links_and_adverts_come_from_BlueZ()
    {
        using MockBus? bus = await MockBus.StartAsync();
        Assert.SkipWhen(bus is null, "needs dbus-daemon and python-dbusmock (LPODS_TEST_PYTHON picks the interpreter)");
        using var home = new TempHome();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await bus!.RunAsync("airpods", AirPods, "AirPods Pro");
        using var link = new BluetoothLink();
        link.Start();
        LinkDevice device = Assert.Single(link.Snapshot());
        Assert.Equal(AirPodsAddress, device.Address);
        Assert.Equal("AirPods Pro", device.Name);
        Assert.False(device.Up);
        Assert.Equal((ushort)0x2014, device.ProductId);

        // Never pages: no channel while BlueZ reports the device disconnected.
        Assert.Null(ServicePipes.OpenAap(AirPodsAddress, out int error));
        Assert.Equal(Native.ErrorDeviceNotConnected, error);

        var up = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.LinkChanged += (address, isUp) =>
        {
            if (address == AirPodsAddress)
            {
                up.TrySetResult(isUp);
            }
        };
        await bus.RunAsync("connect", AirPods);
        Assert.True(await up.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.True(link.IsUp(AirPodsAddress));
        Assert.Equal(AirPodsAddress, link.FirstUp());

        // Proximity adverts reach upstream's callback with the address type and the payload after the company id.
        Assert.Equal(0, StartScan());
        const string payload = "071901142055aa38000000112233445566778899aabbccddeeff00";
        await bus.RunAsync("advert", "5A:11:22:33:44:55", payload);
        (ulong advertAddress, byte type, byte[] data) = await Advert.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        NearbyNative.wpn_ble_stop();
        Assert.Equal(0x5A1122334455UL, advertAddress);
        Assert.Equal((byte)1, type);
        Assert.Equal(Convert.FromHexString(payload), data);
    }

    private static unsafe int StartScan() => NearbyNative.wpn_ble_start(&OnAdvert, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnAdvert(nint context, ulong address, byte addressType, short rssi, byte* data, uint length) =>
        Advert.TrySetResult((address, addressType, new ReadOnlySpan<byte>(data, (int)length).ToArray()));
}
