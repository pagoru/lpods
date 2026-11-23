using System.Diagnostics;
using System.Text.Json;
using LPods.Platform;

namespace LPods.Tests;

[Collection("session bus")]
public sealed class NotificationTests
{
    [Fact]
    public async Task Offer_notifications_carry_buttons_and_report_clicks_and_dismissals()
    {
        using MockBus? bus = await MockBus.StartSessionAsync();
        Assert.SkipWhen(bus is null, "needs dbus-daemon and python-dbusmock (LPODS_TEST_PYTHON picks the interpreter)");
        CancellationToken ct = TestContext.Current.CancellationToken;
        await bus!.RunScriptAsync("notify_mock.py", "start");

        var action = new TaskCompletionSource<(uint Id, string Key)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dismissed = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        Notifications.ActionInvoked += (id, key) => action.TrySetResult((id, key));
        Notifications.Dismissed += id => dismissed.TrySetResult(id);

        uint id = await Notifications.ShowAsync("AirPods Pro", "L 80% · R 79%", icon: "audio-headphones",
            actions: [("default", "Connect"), ("connect", "Connect"), ("dismiss", "Not now")], resident: true);
        Assert.NotEqual(0u, id);

        using JsonDocument shown = JsonDocument.Parse(await ReadAsync(bus, ct));
        JsonElement note = shown.RootElement.EnumerateArray().Single();
        // Notify(app_name, replaces_id, icon, summary, body, actions, hints, timeout)
        Assert.Equal("AirPods Pro", note[3].GetString());
        Assert.Equal(["default", "Connect", "connect", "Connect", "dismiss", "Not now"], note[5].EnumerateArray().Select(e => e.GetString()));
        Assert.True(note[6].GetProperty("resident").GetBoolean());
        Assert.Equal("io.github.pagoru.LPods", note[6].GetProperty("desktop-entry").GetString());

        await bus.RunScriptAsync("notify_mock.py", "action", id.ToString(), "connect");
        Assert.Equal((id, "connect"), await action.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await bus.RunScriptAsync("notify_mock.py", "closed", id.ToString(), "2");
        Assert.Equal(id, await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
    }

    private static async Task<string> ReadAsync(MockBus bus, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("LPODS_TEST_PYTHON") ?? "python3") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "notify_mock.py"));
        info.ArgumentList.Add("list");
        using Process process = Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }
}
