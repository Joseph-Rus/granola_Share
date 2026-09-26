using System.Net;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Tests;

public class CanvasStatusTests
{
    public static IEnumerable<object[]> States()
    {
        // fixture, Kind, Title, Text, Glyph, Tone, ActionLabel, IsPrimary, IsPlain, CanClose — design 08's table.
        yield return ["state-not-set-up", CanvasStateKind.NotSetUp, "Connect Canvas",
            "Bring in assignments, due dates and course files next to your lectures.", "link_off", CanvasTone.Info, "Connect", true, false, true];
        yield return ["state-no-extension", CanvasStateKind.NoExtension, "Finish setting up the Chrome extension",
            "It takes three clicks in Chrome.", "extension", CanvasTone.Warn, "Show me how", true, false, true];
        yield return ["state-chrome-away", CanvasStateKind.ChromeAway, "Is Chrome open?",
            "Chrome last checked in at 8:12. Canvas syncs only while Chrome is open.", "schedule", CanvasTone.Warn, "Open Chrome", false, false, true];
        yield return ["state-signed-out", CanvasStateKind.SignedOut, "Sign in to Canvas in Chrome",
            "Syncing waits until you do.", "lock", CanvasTone.Warn, "Open Canvas", false, false, true];
        yield return ["state-syncing", CanvasStateKind.Syncing, "Syncing… 3 left",
            "BIO 110, CALC II and HIST 210.", "", CanvasTone.Info, null!, false, false, true];
        yield return ["state-connected", CanvasStateKind.Connected, "Connected",
            "Last sync 10:24. Next at 11:24.", "check_circle", CanvasTone.Ok, "Sync now", false, false, false];
        yield return ["state-updated", CanvasStateKind.Updated, "The Chrome extension updated itself",
            "Now version 1.4. Nothing to do.", "new_releases", CanvasTone.Info, "Dismiss", false, true, false];
        yield return ["state-error", CanvasStateKind.Error, "Canvas didn’t answer",
            "school.instructure.com didn’t respond at 10:24. Study Stash will try again at 11:24.", "error", CanvasTone.Error, "Try now", false, false, true];
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Show_matches_design_08_s_table(string fixture, CanvasStateKind kind, string title, string text, string glyph, CanvasTone tone,
        string? actionLabel, bool isPrimary, bool isPlain, bool canClose)
    {
        var model = new CanvasStatusModel(CanvasFixtures.Context());
        model.Show(CanvasFixtures.Load<CanvasApi.State>(fixture));

        Assert.Equal(kind, model.Kind);
        Assert.Equal(title, model.Title);
        Assert.Equal(text, model.Text);
        Assert.Equal(glyph, model.Glyph);
        Assert.Equal(tone, model.Tone);
        Assert.Equal(actionLabel, model.ActionLabel);
        Assert.Equal(isPrimary, model.IsPrimary);
        Assert.Equal(isPlain, model.IsPlain);
        Assert.Equal(canClose, model.CanClose);
        Assert.True(model.IsVisible);
    }

    [Fact]
    public void Syncing_shows_progress_as_done_over_total()
    {
        var model = new CanvasStatusModel(CanvasFixtures.Context());
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-syncing"));

        Assert.Equal((5.0 - 3.0) / 5.0, model.Progress);
        Assert.True(model.IsSyncing);
    }

    [Fact]
    public void Connected_and_syncing_are_the_compact_states()
    {
        var model = new CanvasStatusModel(CanvasFixtures.Context());
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-connected"));
        Assert.True(model.IsCompact);
        Assert.Equal("Connected · last sync 10:24", model.CompactLine);

        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-error"));
        Assert.False(model.IsCompact);
    }

    [Fact]
    public void Act_on_not_set_up_calls_OnConnect()
    {
        var model = new CanvasStatusModel(CanvasFixtures.Context()) { OnConnect = () => called = true };
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-not-set-up"));
        model.ActCommand.Execute(null);
        Assert.True(called);
    }
    bool called;

    [Fact]
    public void Act_on_no_extension_calls_OnShowMeHow()
    {
        bool shown = false;
        var model = new CanvasStatusModel(CanvasFixtures.Context()) { OnShowMeHow = () => shown = true };
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-no-extension"));
        model.ActCommand.Execute(null);
        Assert.True(shown);
    }

    [Fact]
    public void Act_on_chrome_away_opens_chrome()
    {
        List<(string What, string Arg)> log = [];
        var model = new CanvasStatusModel(CanvasFixtures.Context(log: log));
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-chrome-away"));
        model.ActCommand.Execute(null);
        Assert.Contains(log, e => e.What == "OpenChrome");
    }

    [Fact]
    public void Act_on_signed_out_opens_canvas_in_chrome()
    {
        List<(string What, string Arg)> log = [];
        var model = new CanvasStatusModel(CanvasFixtures.Context(log: log));
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-signed-out"));
        model.ActCommand.Execute(null);
        Assert.Contains(log, e => e.What == "OpenInChrome" && e.Arg == "https://school.instructure.com");
    }

    [Fact]
    public async Task Act_on_connected_posts_sync_true()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas", "canvas");
        var model = new CanvasStatusModel(CanvasFixtures.Context(fake));
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-connected"));
        await model.ActCommand.ExecuteAsync(null);

        var sent = Assert.Single(fake.Requests, r => r.Method == "POST");
        Assert.Contains("\"sync\":true", sent.Body);
    }

    [Fact]
    public async Task Act_on_error_posts_sync_true()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas", "canvas");
        var model = new CanvasStatusModel(CanvasFixtures.Context(fake));
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-error"));
        await model.ActCommand.ExecuteAsync(null);

        var sent = Assert.Single(fake.Requests, r => r.Method == "POST");
        Assert.Contains("\"sync\":true", sent.Body);
    }

    [Fact]
    public async Task Act_on_updated_posts_dismiss_update_true()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas", "canvas");
        var model = new CanvasStatusModel(CanvasFixtures.Context(fake));
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-updated"));
        await model.ActCommand.ExecuteAsync(null);

        var sent = Assert.Single(fake.Requests, r => r.Method == "POST");
        Assert.Contains("\"dismiss_update\":true", sent.Body);
    }

    [Fact]
    public void Close_hides_the_card_for_the_session()
    {
        var model = new CanvasStatusModel(CanvasFixtures.Context());
        model.Show(CanvasFixtures.Load<CanvasApi.State>("state-error"));
        model.CloseCommand.Execute(null);
        Assert.False(model.IsVisible);
    }
}
