using System.Runtime.CompilerServices;

namespace StudyStash.App.Tests;

/// <summary>Before any test runs: nothing the app's tests do changes this computer (no login item is written or
/// even looked for).</summary>
static class TestSafety
{
#pragma warning disable CA2255 // the tests are the application here: this has to run before any of them
    [ModuleInitializer]
    internal static void TurnOffSystemChanges() => Platform.Desktop.SystemChangesOff = true;
#pragma warning restore CA2255
}

/// <summary>A settings folder of its own for one test, removed afterwards: never the real one.</summary>
public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "studystash-app-tests", Guid.NewGuid().ToString("N")[..12]);

    public TempHome() => Directory.CreateDirectory(Path);

    public string this[string name] => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Tests that change the process's environment variables run alone, so no other test sees them.</summary>
[CollectionDefinition(nameof(EnvironmentTests), DisableParallelization = true)]
public sealed class EnvironmentTests;
