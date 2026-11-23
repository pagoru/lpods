namespace LPods.Tests;

/// <summary>Points XDG_DATA_HOME at a fresh folder, so SettingsStore and Log write there.</summary>
internal sealed class TempHome : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

    public TempHome()
    {
        Path = Directory.CreateTempSubdirectory("lpods-test-").FullName;
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _previous);
        if (Environment.GetEnvironmentVariable("LPODS_TEST_KEEP") is not null)
        {
            Console.Error.WriteLine($"kept {Path}");
            return;
        }
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
