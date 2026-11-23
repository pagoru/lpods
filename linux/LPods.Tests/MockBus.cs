using System.Diagnostics;

namespace LPods.Tests;

/// <summary>
/// A private dbus-daemon used as the system bus, with python-dbusmock's BlueZ template on it. Null when dbus-daemon or
/// python-dbusmock is missing (the test is skipped then).
/// </summary>
internal sealed class MockBus : IDisposable
{
    private static readonly string Script = Path.Combine(AppContext.BaseDirectory, "bluez_mock.py");
    private readonly Process _daemon;
    private readonly Process _bluez;
    private readonly string _python;

    private MockBus(Process daemon, Process bluez, string python)
    {
        _daemon = daemon;
        _bluez = bluez;
        _python = python;
    }

    public static async Task<MockBus?> StartAsync()
    {
        string python = Environment.GetEnvironmentVariable("LPODS_TEST_PYTHON") ?? "python3";
        if (!await RunsAsync(python, "-c", "import dbus, dbusmock") || !await RunsAsync("dbus-daemon", "--version"))
        {
            return null;
        }
        var daemon = Process.Start(new ProcessStartInfo("dbus-daemon", "--session --nofork --print-address")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        string address = (await daemon.StandardOutput.ReadLineAsync())!.Trim();
        Environment.SetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS", address);
        var info = new ProcessStartInfo(python, "-m dbusmock --system --template bluez5") { UseShellExecute = false, RedirectStandardOutput = true };
        var bluez = Process.Start(info)!;
        var bus = new MockBus(daemon, bluez, python);
        // The template is up once its mock interface answers.
        for (int i = 0; i < 100 && !await RunsAsync(python, Script, "adapter"); i++)
        {
            await Task.Delay(100);
        }
        return bus;
    }

    public Task RunAsync(params string[] args) => RunsAsync(_python, [Script, .. args]).ContinueWith(
        t => t.Result ? Task.CompletedTask : throw new InvalidOperationException($"bluez_mock {string.Join(' ', args)} failed"),
        TaskScheduler.Default).Unwrap();

    public void Dispose()
    {
        _bluez.Kill();
        _daemon.Kill();
        _bluez.Dispose();
        _daemon.Dispose();
    }

    private static async Task<bool> RunsAsync(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            using Process process = Process.Start(info)!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
