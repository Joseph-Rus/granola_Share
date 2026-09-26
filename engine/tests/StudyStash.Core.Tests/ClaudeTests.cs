using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>The app's API (/api/v2), search passages, Ask, and Claude's door: its sign-in and MCP server.</summary>
public class ClaudeTests
{
    const string Notes = """
        ## Summary
        A recursive function solves a problem by calling itself on a smaller version of it. Each call gets its own frame.

        ## Key points
        - Every recursive function needs a base case that returns without calling itself.
        - Without a base case the stack keeps growing until it overflows.

        ## Definitions
        - **Call stack**: the memory a program uses to track calls that haven't finished yet.

        ## Announcements
        - The midterm is on October 14 and covers recursion traces.
        """;

    const string Transcript = "[00:05] Okay, let's start.\n[18:05] Okay, the midterm. Recursion traces will be on it, the stack diagrams from last week.\n"
        + "[19:30] Think of each call as a plate on a stack.\n[24:12] And when we hit the base case, the frames come off one by one.";

    static (Config Cfg, Store Store) Library(TempDir dir)
    {
        var cfg = new Config(dir["home"], dir["pool"])
        {
            PoolName = "Sam's library", PoolPassword = "pw", OllamaEnabled = false,
            Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("BIO 110", [])],
        };
        Directory.CreateDirectory(cfg.Home);
        var store = new Store(cfg.DbPath, cfg.PoolDir);
        store.Save(new Meeting("rec-1")
        {
            Title = "CS 101 lecture, Tue 23 Sep", Date = "2026-09-23T10:02:12-07:00", Owner = "Sam", Folder = "CS 101", Transcript = Transcript,
            Raw = new JsonObject { ["source"] = "recorder", ["seconds"] = 4320.0 },
        }, new Classification("CS 101", 0.95, "folder", "Recursion and the call stack", ["recursion"]), summaryMd: Notes, summaryModel: "qwen3:8b");
        store.Save(new Meeting("rec-2")
        {
            Title = "BIO lecture", Date = "2026-09-22T09:00:00-07:00", Owner = "Sam", Transcript = "[00:01] Membranes let water through by osmosis.",
            Raw = new JsonObject { ["seconds"] = 3000.0 },
        }, new Classification("BIO 110", 0.9, "folder", "Membranes and osmosis", ["osmosis"]), summaryMd: "## Summary\nWater crosses membranes by osmosis.");
        return (cfg, store);
    }

    static Task<TestSite> Site(Config cfg, Store store, LibraryWebOptions? options = null) =>
        TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store, new Pipeline(cfg, store, log: _ => { }), options ?? new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(), Latest = _ => Task.FromResult<Release?>(null),
        }));

    static HttpRequestMessage Req(HttpMethod m, string path, object? body = null, string key = "pw")
    {
        var r = new HttpRequestMessage(m, path);
        r.Headers.Authorization = new("Bearer", key);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    static async Task<JsonNode> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    }

    // --- passages ------------------------------------------------------------------------------------------------

    [Fact]
    public void Notes_and_transcripts_become_passages()
    {
        var notes = Passages.FromNotes("n", Notes);
        Assert.Equal("Summary", notes[0].Section);
        Assert.StartsWith("A recursive function", notes[0].Text);
        Assert.Contains(notes, p => p.Section == "Key points" && p.Text.StartsWith("Every recursive function"));
        Assert.Contains(notes, p => p.Section == "Definitions" && p.Text.StartsWith("**Call stack**"));
        var timed = Passages.FromTranscript("n", Transcript);
        Assert.Equal(5, timed[0].Start);
        Assert.All(timed, p => Assert.NotNull(p.Start));
        var plain = Passages.FromTranscript("n", string.Join(" ", Enumerable.Repeat("This is a sentence about osmosis.", 40)));
        Assert.True(plain.Count > 1);
        Assert.All(plain, p => Assert.Null(p.Start));
        Assert.Equal("\"call\" \"sta\"*", Passages.AllWords("call sta"));
        Assert.Equal("\"midterm\"", Passages.AnyWords("What's on the midterm?"));
        Assert.Null(Passages.AllWords("  ?! "));
    }

    [Fact]
    public void Lectures_are_indexed_when_filed_and_forgotten_when_deleted()
    {
        using var dir = new TempDir();
        var (_, store) = Library(dir);
        using var _s = store;
        var hits = store.SearchPassages(Passages.AllWords("base case")!);
        Assert.Contains(hits, h => h.Note.Id == "rec-1" && h.Passage.Section == "Key points");
        Assert.Contains(hits, h => h.Note.Id == "rec-1" && h.Passage.Start is not null);
        Assert.Empty(store.SearchPassages(Passages.AllWords("base case")!, className: "BIO 110"));
        Assert.Equal(0, store.IndexMissing());
        store.Delete("rec-1");
        Assert.Empty(store.SearchPassages(Passages.AllWords("base case")!));
    }

    // --- the app's API -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_app_reads_the_library()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        await using var site = await Site(cfg, store);
        var c = site.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(HttpMethod.Get, "/api/v2/library", key: "wrong"))).StatusCode);

        var lib = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/v2/library")));
        Assert.Equal("Sam's library", lib["name"]!.GetValue<string>());
        Assert.Equal("[{\"name\":\"CS 101\",\"lectures\":1,\"color\":0},{\"name\":\"BIO 110\",\"lectures\":1,\"color\":1}]", lib["classes"]!.ToJsonString());

        var list = (JsonArray)await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/v2/lectures?class=CS%20101")));
        var one = list.Single()!;
        Assert.Equal("Recursion and the call stack", one["title"]!.GetValue<string>());
        Assert.Equal(4320, one["seconds"]!.GetValue<double>());
        Assert.Equal("A recursive function solves a problem by calling itself on a smaller version of it.", one["summary"]!.GetValue<string>());

        var full = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/v2/lectures/rec-1")));
        Assert.StartsWith("## Summary", full["notes"]!.GetValue<string>());
        Assert.StartsWith("[00:05]", full["transcript"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Req(HttpMethod.Get, "/api/v2/lectures/nope"))).StatusCode);

        var found = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/v2/search?q=call%20sta")));
        Assert.Equal("rec-1", found["lectures"]![0]!["id"]!.GetValue<string>());
        var passages = found["passages"]!.AsArray();
        Assert.Contains(passages, p => p!["section"]!.GetValue<string>() == "Definitions");
        Assert.Contains(passages, p => p!["at"] is JsonValue v && v.GetValue<double>() >= 1085);
        var cls = await Json(await c.SendAsync(Req(HttpMethod.Get, "/api/v2/search?q=bio")));
        Assert.Equal("BIO 110", cls["classes"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_app_moves_deletes_and_adds_classes()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        await using var site = await Site(cfg, store);
        var c = site.Client;
        var moved = await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/v2/lectures/rec-2/class", new { @class = "CS 101" })));
        Assert.Equal("CS 101", moved["class"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(Req(HttpMethod.Post, "/api/v2/lectures/rec-2/class", new { @class = "Nope" }))).StatusCode);
        var added = await Json(await c.SendAsync(Req(HttpMethod.Post, "/api/v2/classes", new { name = "HIST 210" })));
        Assert.Equal("HIST 210", added["classes"]![2]!["name"]!.GetValue<string>());
        Assert.Contains("HIST 210", File.ReadAllText(cfg.ConfigPath));
        await Json(await c.SendAsync(Req(HttpMethod.Delete, "/api/v2/lectures/rec-2")));
        Assert.Null(store.Get("rec-2"));
    }

    [Fact]
    public async Task Ask_answers_from_the_notes_with_the_moment_it_was_said()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        string? prompt = null;
        await using var site = await Site(cfg, store, new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(), Latest = _ => Task.FromResult<Release?>(null),
            AskChat = (p, schema) =>
            {
                prompt = p;
                int n = p.Split('\n').First(l => l.Contains("at 18:05"))[1] - '0';
                return Task.FromResult($"{{\"answer\":\"Recursion traces and stack diagrams.\",\"sources\":[{n},99]}}");
            },
        });
        var a = await Json(await site.Client.SendAsync(Req(HttpMethod.Post, "/api/v2/ask", new { question = "What's on the midterm?" })));
        Assert.Equal("Recursion traces and stack diagrams.", a["answer"]!.GetValue<string>());
        var src = a["sources"]!.AsArray().Single()!;
        Assert.Equal("rec-1", src["id"]!.GetValue<string>());
        Assert.Equal(1085, src["at"]!.GetValue<double>());
        Assert.Contains("Question: What's on the midterm?", prompt);

        // While recording: what's been said so far, as "This lecture".
        var live = await Json(await site.Client.SendAsync(Req(HttpMethod.Post, "/api/v2/ask", new
        {
            question = "What did she say about the midterm?", live = "[18:05] The midterm has recursion traces.", live_title = "CS 101, now",
        })));
        Assert.Contains("CS 101, now at 18:05", prompt);
        Assert.Null(live["sources"]![0]!["id"]);

        // No AI model on the library: Ask says so.
        await using var plain = await Site(cfg, store);
        var r = await plain.Client.SendAsync(Req(HttpMethod.Post, "/api/v2/ask", new { question = "midterm?" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
    }

    // --- Claude's tools ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claudes_tools_read_the_library_over_its_api()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        await using var site = await Site(cfg, store);
        var lib = new RemoteLibrary("http://localhost", "pw", site.Client);
        Assert.Equal("Library: Sam's library\n- CS 101 (1 lecture)\n- BIO 110 (1 lecture)", await ClaudeTools.ListClassesAsync(lib));
        string lectures = await ClaudeTools.ListLecturesAsync(lib, null, 20, null);
        Assert.StartsWith("- [rec-1] Recursion and the call stack — CS 101, Wed 23 Sep 2026, 1 h 12 min. A recursive function", lectures);
        string search = await ClaudeTools.SearchAsync(lib, "midterm", null, 10);
        Assert.Contains("at 18:05: Okay, the midterm.", search);
        Assert.Contains("Announcements: The midterm is on October 14", search);
        string lecture = await ClaudeTools.GetLectureAsync(lib, "rec-1");
        Assert.Contains("# Recursion and the call stack", lecture);
        Assert.Contains("## Key points", lecture);
        Assert.Equal("[19:30] Think of each call as a plate on a stack.\n[24:12] And when we hit the base case, the frames come off one by one.",
            await ClaudeTools.GetTranscriptAsync(lib, "rec-1", "19:00", null));
        Assert.Contains("There's no lecture", await ClaudeTools.GetLectureAsync(lib, "nope"));
        await Assert.ThrowsAsync<LibraryRefusedException>(() => new RemoteLibrary("http://localhost", "wrong", site.Client).OverviewAsync());
    }

    // --- Claude's door: sign-in and MCP ------------------------------------------------------------------------------

    static async Task<(TestSite Site, ClaudeAccess Access)> Door(Config cfg, Store store, ClaudeAccess? shared = null)
    {
        var access = shared ?? new ClaudeAccess(cfg.Home);
        var site = await TestSite.StartAsync(b => ClaudeWeb.Build(b, cfg, new LibraryReader(cfg, store), access));
        return (site, access);
    }

    static string Verifier() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(40)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Challenge(string v) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(v))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static FormUrlEncodedContent Form(params (string, string)[] f) => new(f.Select(x => KeyValuePair.Create(x.Item1, x.Item2)));

    [Fact]
    public async Task Claude_signs_in_with_the_library_password_and_reads()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        var (site, access) = await Door(cfg, store);
        await using var _site = site;
        var c = site.Client;

        // Not signed in: the MCP server says where to sign in.
        var none = await c.PostAsync("/mcp", JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"", none.Headers.WwwAuthenticate.ToString());
        var meta = await Json(await c.GetAsync("/.well-known/oauth-protected-resource/mcp"));
        Assert.Equal("http://localhost/mcp", meta["resource"]!.GetValue<string>());
        var server = await Json(await c.GetAsync("/.well-known/oauth-authorization-server"));
        Assert.Equal("http://localhost/token", server["token_endpoint"]!.GetValue<string>());

        // Registration takes https redirects, and http only to this computer.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/register", JsonContent.Create(new { redirect_uris = new[] { "http://evil.example/cb" } }))).StatusCode);
        var reg = await Json(await c.PostAsync("/register", JsonContent.Create(new { client_name = "Claude", redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" } })));
        string clientId = reg["client_id"]!.GetValue<string>();

        string verifier = Verifier();
        var fields = new[] { ("response_type", "code"), ("client_id", clientId), ("redirect_uri", "https://claude.ai/api/mcp/auth_callback"),
            ("state", "xyz"), ("code_challenge", Challenge(verifier)), ("code_challenge_method", "S256"), ("resource", "http://localhost/mcp") };
        string query = string.Join("&", fields.Select(f => $"{f.Item1}={Uri.EscapeDataString(f.Item2)}"));
        var page = await c.GetAsync("/authorize?" + query);
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Let Claude read your lectures?", html);
        Assert.Contains("form-action 'self' https://claude.ai", page.Headers.GetValues("Content-Security-Policy").Single());

        var wrong = await c.PostAsync("/authorize", Form([.. fields, ("password", "nope"), ("decision", "allow")]));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var denied = await c.PostAsync("/authorize", Form([.. fields, ("decision", "deny")]));
        Assert.StartsWith("https://claude.ai/api/mcp/auth_callback?error=access_denied&state=xyz", denied.Headers.Location!.ToString());
        var allowed = await c.PostAsync("/authorize", Form([.. fields, ("password", "pw"), ("decision", "allow")]));
        Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
        var back = allowed.Headers.Location!;
        Assert.Equal("claude.ai", back.Host);
        var q = System.Web.HttpUtility.ParseQueryString(back.Query);
        Assert.Equal("xyz", q["state"]);
        string code = q["code"]!;

        // The code, with the verifier, for tokens; a wrong verifier or a second use gets nothing.
        var bad = await c.PostAsync("/token", Form(("grant_type", "authorization_code"), ("code", code), ("code_verifier", Verifier()),
            ("client_id", clientId), ("redirect_uri", "https://claude.ai/api/mcp/auth_callback")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var allowed2 = await c.PostAsync("/authorize", Form([.. fields, ("password", "pw"), ("decision", "allow")]));
        code = System.Web.HttpUtility.ParseQueryString(allowed2.Headers.Location!.Query)["code"]!;
        var tokens = await Json(await c.PostAsync("/token", Form(("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier),
            ("client_id", clientId), ("redirect_uri", "https://claude.ai/api/mcp/auth_callback"))));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/token", Form(("grant_type", "authorization_code"), ("code", code), ("code_verifier", verifier),
            ("client_id", clientId), ("redirect_uri", "https://claude.ai/api/mcp/auth_callback")))).StatusCode);
        string accessToken = tokens["access_token"]!.GetValue<string>(), refresh = tokens["refresh_token"]!.GetValue<string>();
        Assert.Equal(3600, tokens["expires_in"]!.GetValue<int>());
        Assert.DoesNotContain(accessToken, File.ReadAllText(Path.Combine(cfg.Home, "claude.json")));

        // With the token, the MCP server answers: its tools, and a search.
        await using (var mcp = await Connect(site, accessToken))
        {
            var tools = await mcp.ListToolsAsync();
            Assert.Equal(["get_lecture", "get_transcript", "list_classes", "list_lectures", "search_notes"], tools.Select(t => t.Name).Order());
            Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
            var result = await mcp.CallToolAsync("search_notes", new Dictionary<string, object?> { ["query"] = "osmosis" });
            Assert.Contains("Membranes and osmosis", ((TextContentBlock)result.Content[0]).Text);
            Assert.Equal(["quiz_me", "study_guide"], (await mcp.ListPromptsAsync()).Select(p => p.Name).Order());
            Assert.Contains("Study Stash", mcp.ServerInstructions);
        }

        // Refresh gives a new pair and retires the old refresh token; disconnecting ends it.
        var renewed = await Json(await c.PostAsync("/token", Form(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId))));
        Assert.NotEqual(accessToken, renewed["access_token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/token", Form(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId)))).StatusCode);
        Assert.Null(access.Check(accessToken));
        var grant = access.Grants().Single();
        Assert.Equal("Claude", grant.Name);
        Assert.True(access.Revoke(grant.Id));
        Assert.Null(access.Check(renewed["access_token"]!.GetValue<string>()));
    }

    static async Task<McpClient> Connect(TestSite site, string token) =>
        await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }, new HttpClient(site.App.GetTestServer().CreateHandler()) { BaseAddress = new Uri("http://localhost") }, ownsHttpClient: true));

    /// <summary>The MCP SDK's own OAuth client (what an MCP host does): it finds the sign-in, registers, and gets a
    /// code from the sign-in page, where a person types the password.</summary>
    [Fact]
    public async Task An_mcp_client_finds_the_sign_in_by_itself()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        var (site, _) = await Door(cfg, store);
        await using var _site = site;
        var browser = new HttpClient(site.App.GetTestServer().CreateHandler()) { BaseAddress = new Uri("http://localhost") };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri("http://localhost:33418/callback"),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Claude Code" },
                AuthorizationCallbackHandler = async (context, ct) =>
                {
                    var authorize = context.AuthorizationUri;
                    var page = await browser.GetAsync(authorize.PathAndQuery, ct);
                    Assert.Contains("Let Claude Code read your lectures?", await page.Content.ReadAsStringAsync(ct));
                    var form = System.Web.HttpUtility.ParseQueryString(authorize.Query);
                    var fields = form.AllKeys.Select(k => (k!, form[k]!)).Append(("password", "pw")).Append(("decision", "allow")).ToArray();
                    var answer = await browser.PostAsync("/authorize", Form(fields), ct);
                    var back = System.Web.HttpUtility.ParseQueryString(answer.Headers.Location!.Query);
                    return new AuthorizationResult { Code = back["code"]!, State = back["state"], Iss = back["iss"] };
                },
            },
        }, new HttpClient(site.App.GetTestServer().CreateHandler()), ownsHttpClient: true);
        await using var mcp = await McpClient.CreateAsync(transport);
        var result = await mcp.CallToolAsync("list_classes", new Dictionary<string, object?>());
        Assert.StartsWith("Library: Sam's library", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task Guessing_the_password_gets_locked_out()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        var (site, access) = await Door(cfg, store);
        await using var _site = site;
        var client = access.Register("Claude", ["https://claude.ai/cb"]);
        var fields = new[] { ("response_type", "code"), ("client_id", client.ClientId), ("redirect_uri", "https://claude.ai/cb"),
            ("code_challenge", Challenge(Verifier())), ("code_challenge_method", "S256") };
        for (int i = 0; i < 8; i++) await site.Client.PostAsync("/authorize", Form([.. fields, ("password", "guess" + i), ("decision", "allow")]));
        var right = await site.Client.PostAsync("/authorize", Form([.. fields, ("password", "pw"), ("decision", "allow")]));
        Assert.Equal((HttpStatusCode)429, right.StatusCode);
        Assert.Contains("Too many wrong passwords", await right.Content.ReadAsStringAsync());

        // An unknown client, or a redirect it didn't register, never gets redirected to.
        var unknown = await site.Client.GetAsync("/authorize?response_type=code&client_id=nope&redirect_uri=https%3A%2F%2Fevil.example%2F");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var elsewhere = await site.Client.GetAsync($"/authorize?response_type=code&client_id={client.ClientId}&redirect_uri=https%3A%2F%2Fevil.example%2F");
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Null(elsewhere.Headers.Location);
    }

    [Fact]
    public async Task A_token_from_settings_reads_until_its_revoked()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        await using var lib = await Site(cfg, store, new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(), Latest = _ => Task.FromResult<Release?>(null),
            Claude = new ClaudeAccess(cfg.Home),
        });
        var made = await Json(await lib.Client.SendAsync(Req(HttpMethod.Post, "/api/v2/claude/tokens", new { name = "Cursor" })));
        string token = made["token"]!.GetValue<string>();
        Assert.StartsWith("sst_", token);
        Assert.Equal($"http://127.0.0.1:{cfg.WebPort + 1}/mcp", made["local_url"]!.GetValue<string>());
        Assert.Equal("Cursor", made["connections"]![0]!["name"]!.GetValue<string>());

        var (door, _) = await Door(cfg, store);
        await using var _door = door;
        await using (var mcp = await Connect(door, token))
            Assert.Equal(5, (await mcp.ListToolsAsync()).Count);
        await Json(await lib.Client.SendAsync(Req(HttpMethod.Delete, $"/api/v2/claude/connections/{made["token_id"]!.GetValue<string>()}")));
        // The door is its own copy (as a second process would be): it sees the disconnect in claude.json at once.
        var after = await door.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Headers = { Authorization = new("Bearer", token), Accept = { new("application/json"), new("text/event-stream") } },
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Tailscale_puts_the_door_on_the_tailnet_or_the_internet()
    {
        var ran = new List<string>();
        ProcResult? ok = new(0, "");
        var reach = new ClaudeReach
        {
            Tailscale = () => new TailscaleInfo(true, true, "Running", "mini.tail1234.ts.net.", ["100.64.0.9"], "/usr/local/bin/tailscale"),
            Run = (exe, args, _) =>
            {
                ran.Add(string.Join(" ", args));
                return ok;
            },
        };
        Assert.Equal(("https://mini.tail1234.ts.net", (string?)null), reach.Set(8001, internet: true, on: true));
        Assert.Equal("funnel --bg --https=443 http://127.0.0.1:8001", ran[^1]);
        reach.Set(8001, internet: false, on: false);
        Assert.Equal("serve --https=443 off", ran[^1]);
        ok = new(1, "Funnel is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/funnel?node=abc\n");
        Assert.Equal("Tailscale needs permission first: open https://login.tailscale.com/f/funnel?node=abc, allow it, then try again.",
            reach.Set(8001, true, true).Problem);
        Assert.Contains("isn't running", new ClaudeReach { Tailscale = () => new TailscaleInfo(true, false, Exe: "ts") }.Set(8001, true, true).Problem);

        // Through the API: the address it's on is kept, and the sign-in names it.
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        ok = new(0, "");
        var access = new ClaudeAccess(cfg.Home);
        await using var lib = await Site(cfg, store, new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(), Latest = _ => Task.FromResult<Release?>(null),
            Claude = access, Reach = reach,
        });
        var on = await Json(await lib.Client.SendAsync(Req(HttpMethod.Post, "/api/v2/claude/reach", new { internet = true, on = true })));
        Assert.Equal("https://mini.tail1234.ts.net/mcp", on["public_url"]!.GetValue<string>());
        Assert.Equal("https://mini.tail1234.ts.net", new ClaudeAccess(cfg.Home).PublicUrl);
    }

    // --- AI tool access --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Turning_tool_access_off_refuses_the_whole_door_at_once()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        var (site, access) = await Door(cfg, store);
        await using var _site = site;
        var (token, _) = access.CreateToken("Cursor");
        access.ToolsOn = false;

        var refused = await site.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Headers = { Authorization = new("Bearer", token), Accept = { new("application/json"), new("text/event-stream") } },
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("AI tool access is off", await refused.Content.ReadAsStringAsync());

        access.ToolsOn = true;
        await using var mcp = await Connect(site, token);
        Assert.Equal(5, (await mcp.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task A_reading_toggle_off_refuses_only_the_tools_that_need_it()
    {
        using var dir = new TempDir();
        var (cfg, store) = Library(dir);
        using var _s = store;
        var (site, access) = await Door(cfg, store);
        await using var _site = site;
        var (token, _) = access.CreateToken("Cursor");
        access.Reading = access.Reading with { Notes = false };

        await using var mcp = await Connect(site, token);
        var refused = await mcp.CallToolAsync("get_lecture", new Dictionary<string, object?> { ["lecture_id"] = "rec-1" });
        Assert.Equal(true, refused.IsError);
        Assert.Contains("don't let AI tools read that", ((TextContentBlock)refused.Content[0]).Text);

        var ok = await mcp.CallToolAsync("search_notes", new Dictionary<string, object?> { ["query"] = "osmosis" });
        Assert.True(ok.IsError is null or false);
        var tools = await mcp.ListToolsAsync(); // still listed: it's refused at call time, not hidden
        Assert.Contains("get_lecture", tools.Select(t => t.Name));
    }
}
