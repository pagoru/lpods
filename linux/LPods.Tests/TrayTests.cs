using System.Text.Json;
using LPods.Platform;

namespace LPods.Tests;

/// <summary>The tray as a StatusNotifierItem, against a host that reads and clicks it like a shell extension would.</summary>
[Collection("session bus")]
public sealed class TrayTests
{
    [Fact]
    public async Task Tray_registers_with_a_host_that_starts_later_and_answers_its_clicks()
    {
        using MockBus? bus = await MockBus.StartSessionAsync();
        Assert.SkipWhen(bus is null, "needs dbus-daemon and python-dbusmock (LPODS_TEST_PYTHON picks the interpreter)");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string output = Path.Combine(Path.GetTempPath(), $"lpods-sni-{Environment.ProcessId}.json");
        File.Delete(output);

        using var tray = new Tray();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tray.Activated += () => activated.TrySetResult();
        tray.OpenClicked += () => opened.TrySetResult();
        tray.QuitClicked += () => quit.TrySetResult();
        // No StatusNotifierWatcher yet: the tray waits for one to appear.
        await tray.StartAsync(new TrayModel("AirPods Pro · Left 80%", "audio-headphones-symbolic", ["AirPods Pro", "Left 80%"], "Open LPods", "Start at login", true, "Quit LPods"));

        Task host = bus!.RunScriptAsync("sni_host.py", output);
        Task finished = await Task.WhenAny(host, Task.Delay(TimeSpan.FromSeconds(20), ct));
        Assert.Same(host, finished);
        await host;

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(output));
        Assert.False(json.RootElement.TryGetProperty("error", out JsonElement error), error.ToString());
        JsonElement properties = json.RootElement.GetProperty("properties");
        Assert.Equal("lpods", properties.GetProperty("Id").GetString());
        Assert.Equal("AirPods Pro · Left 80%", properties.GetProperty("Title").GetString());
        Assert.Equal("audio-headphones-symbolic", properties.GetProperty("IconName").GetString());
        Assert.Equal("/MenuBar", properties.GetProperty("Menu").GetString());
        string[] labels = [.. json.RootElement.GetProperty("clicked").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(["AirPods Pro", "Left 80%", "Open LPods", "Quit LPods", "Start at login"], labels);

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await quit.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        File.Delete(output);
    }
}
