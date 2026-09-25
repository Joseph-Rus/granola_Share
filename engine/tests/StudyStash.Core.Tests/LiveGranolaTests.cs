using Xunit.Abstractions;

namespace StudyStash.Core.Tests;

/// <summary>
/// Against the real Granola, only when asked: STUDYSTASH_LIVE_GRANOLA=1 dotnet test. Read-only. It never registers
/// an app or refreshes a token: Granola rotates refresh tokens, so a refresh here would sign this computer's own
/// Study Stash out. It prints counts, never names, emails or titles.
/// </summary>
public class LiveGranolaTests(ITestOutputHelper output)
{
    static readonly bool Live = Environment.GetEnvironmentVariable("STUDYSTASH_LIVE_GRANOLA") == "1";

    [Fact]
    public async Task Granolas_sign_in_server_publishes_what_signing_in_needs()
    {
        if (!Live)
        {
            output.WriteLine("skipped: set STUDYSTASH_LIVE_GRANOLA=1");
            return;
        }
        using var dir = new TempDir();
        var oauth = new GranolaOAuth(Configs.McpUrl, 3334, "login", dir["tokens.json"], dir["oauth_client.json"]);
        var meta = await oauth.MetadataAsync();
        Assert.Equal("https://mcp-auth.granola.ai", meta["issuer"].S());
        Assert.Contains("S256", meta["code_challenge_methods_supported"]!.AsArray().Select(m => m.S()));
        foreach (string key in new[] { "authorization_endpoint", "token_endpoint", "registration_endpoint" })
            Assert.StartsWith("https://mcp-auth.granola.ai/", meta[key].S());
        Assert.False(File.Exists(dir["oauth_client.json"])); // nothing was registered
    }

    [Fact]
    public async Task Reading_lectures_with_this_computers_sign_in()
    {
        string tokensPath = Path.Combine(Configs.DefaultHome, "tokens.json");
        if (!Live || !File.Exists(tokensPath))
        {
            output.WriteLine("skipped: set STUDYSTASH_LIVE_GRANOLA=1 on a computer that is signed in to Granola");
            return;
        }
        var tokens = (System.Text.Json.Nodes.JsonObject)Py.JsonLoads(Py.ReadText(tokensPath))!;
        double left = (tokens["expires_at"]?.GetValue<double>() ?? 0) - Py.Time();
        if (left < 300)
        {
            output.WriteLine($"skipped: the access token has {left:0} s left, and refreshing it here would sign the laptop out");
            return;
        }
        await using var session = await McpGranolaSession.ConnectAsync(Configs.McpUrl, tokens["access_token"].S());
        var client = new GranolaClient(_ => Task.FromResult<IGranolaSession>(session));
        var tools = await client.ToolsAsync(session);
        output.WriteLine("tools: " + string.Join(", ", tools.Keys.Order()));
        Assert.Contains("list_meetings", tools.Keys);
        var account = await client.GetAccountInfoAsync(session);
        Assert.False(string.IsNullOrEmpty(account?.Email), "get_account_info named nobody");
        output.WriteLine($"account: an email, {(account!.Workspace.Length > 0 ? "a workspace" : "no workspace")}, scopes {string.Join(" ", account.Scopes)}");
        var meetings = await client.ListMeetingsAsync(session, DateOnly.FromDateTime(DateTime.Today).AddDays(-30));
        output.WriteLine($"lectures in the last 30 days: {meetings.Count}");
        Assert.All(meetings, m => Assert.False(string.IsNullOrEmpty(m.Id)));
        if (meetings.Count == 0) return;
        var full = await client.GetMeetingsAsync(session, [meetings[0].Id]);
        Assert.Equal(meetings[0].Id, full.Single().Id);
        output.WriteLine($"one lecture in full: {full[0].NotesMarkdown.Length} characters of notes, dated {(full[0].Date.Length > 0 ? "yes" : "no")}");
        string transcript = await client.GetTranscriptAsync(session, meetings[0].Id);
        output.WriteLine($"its transcript: {transcript.Length} characters (a free plan gets none)");
    }
}
