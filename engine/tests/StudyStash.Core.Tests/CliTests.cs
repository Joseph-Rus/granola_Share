using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>The engine's commands: the app runs as the engine only for commands that exist, and the ones that are gone
/// (Granola's sign-in and sync, the laptop's old watcher) open the app or print the usage instead.</summary>
public class CliTests
{
    [Theory]
    [InlineData("run")]
    [InlineData("serve")]
    [InlineData("--home", "x", "run")]
    [InlineData("mcp")]
    [InlineData("ai", "test")]
    [InlineData("doctor")]
    [InlineData("version")]
    public void A_command_that_exists_runs_the_engine(params string[] args) => Assert.True(Cli.IsCommand(args));

    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    [InlineData("sync")]
    [InlineData("tools")]
    [InlineData("client", "run")]
    [InlineData("--home", "x", "client", "open")]
    [InlineData]
    public void A_command_that_is_gone_opens_the_app(params string[] args) => Assert.False(Cli.IsCommand(args));

    [Theory]
    [InlineData("login")]
    [InlineData("sync")]
    [InlineData("tools")]
    public async Task A_command_that_is_gone_prints_the_usage(string command)
    {
        using var dir = new TempDir();
        Assert.Equal(2, await Cli.RunAsync(["--home", dir["home"], command]));
        Assert.Empty(Directory.GetFileSystemEntries(dir["home"]));
    }
}
