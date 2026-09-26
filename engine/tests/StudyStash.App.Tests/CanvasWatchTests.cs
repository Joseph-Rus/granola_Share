using StudyStash.App.Services;

namespace StudyStash.App.Tests;

public class CanvasWatchTests
{
    [Fact]
    public async Task RefreshAsync_reads_the_state_and_raises_Changed()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected");
        var watch = new CanvasWatch(CanvasFixtures.Context(fake));
        int changed = 0;
        watch.Changed += () => changed++;

        await watch.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connected", watch.State?.Status);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task RefreshAsync_does_nothing_without_a_library()
    {
        var watch = new CanvasWatch(CanvasFixtures.Context());
        int changed = 0;
        watch.Changed += () => changed++;

        await watch.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Null(watch.State);
        Assert.Equal(0, changed);
    }

    [Theory]
    [InlineData("state-syncing", 5)]
    [InlineData("state-no-extension", 5)]
    [InlineData("state-connected", 60)]
    [InlineData("state-chrome-away", 60)]
    [InlineData("state-error", 60)]
    public async Task NextDelay_is_fast_only_while_syncing_or_waiting_for_the_extension(string fixture, int seconds)
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", fixture);
        var watch = new CanvasWatch(CanvasFixtures.Context(fake));
        await watch.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(seconds), watch.NextDelay);
    }

    [Fact]
    public void NextDelay_defaults_slow_before_the_first_refresh() =>
        Assert.Equal(TimeSpan.FromSeconds(60), new CanvasWatch(CanvasFixtures.Context()).NextDelay);

    [Fact]
    public async Task Start_polls_until_Stop()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected");
        var watch = new CanvasWatch(CanvasFixtures.Context(fake));

        watch.Start();
        await Task.Delay(80, TestContext.Current.CancellationToken); // one immediate refresh, then it waits out NextDelay (60 s)
        int afterStart = fake.Requests.Count;
        Assert.True(afterStart >= 1);

        watch.Stop();
        await Task.Delay(80, TestContext.Current.CancellationToken);
        Assert.Equal(afterStart, fake.Requests.Count); // stopped before the next poll, not after it
    }
}
