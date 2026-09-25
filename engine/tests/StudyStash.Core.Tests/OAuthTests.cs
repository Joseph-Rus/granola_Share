using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>
/// Signing in to Granola, checked against the MCP authorization spec (2025-11-25) and the RFCs it builds on: PKCE
/// (RFC 7636), protected resource metadata (RFC 9728), server metadata (RFC 8414 / OpenID), dynamic client
/// registration (RFC 7591) and resource indicators (RFC 8707). The fake sign-in server publishes Granola's real
/// metadata and checks every request the way a real one must.
/// </summary>
public class OAuthTests
{
    const string Mcp = "https://mcp.granola.ai/mcp";

    static JsonObject GranolaMeta() => (JsonObject)Golden.Granola("auth_meta")!.DeepClone();

    /// <summary>Granola's sign-in server, in memory: discovery, registration, codes, and the token endpoint.</summary>
    sealed class FakeAuthServer : HttpMessageHandler
    {
        public JsonObject Meta { get; set; } = GranolaMeta();
        public bool PathMetadata { get; set; } = true; // RFC 9728's metadata under the MCP server's own path
        public bool Rfc8414 { get; set; } = true; // or only OpenID discovery
        public bool RotateRefreshTokens { get; set; } = true;
        public List<(string Method, string Url, string Body)> Requests { get; } = [];
        public int Registrations { get; private set; }

        readonly Dictionary<string, (string Challenge, string ClientId, string Redirect, string Resource)> codes = [];
        readonly HashSet<string> refreshTokens = [];
        int issued;

        /// <summary>What the sign-in page does when a person clicks Allow: a code for this request, or an error.</summary>
        public string Authorize(Dictionary<string, string> q)
        {
            Assert.Equal("code", q["response_type"]);
            Assert.Equal("S256", q["code_challenge_method"]); // OAuth 2.1 and the spec: S256
            Assert.Equal(Mcp, q["resource"]); // RFC 8707: the MCP server, in both requests
            string code = "code-" + ++issued;
            codes[code] = (q["code_challenge"], q["client_id"], q["redirect_uri"], q["resource"]);
            return code;
        }

        static HttpResponseMessage Json(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        JsonObject NewTokens()
        {
            string refresh = "rt-" + ++issued;
            refreshTokens.Add(refresh);
            return new JsonObject
            {
                ["access_token"] = "at-" + issued, ["token_type"] = "Bearer", ["expires_in"] = 3600,
                ["refresh_token"] = refresh, ["scope"] = "mcp offline_access",
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, url, body));
            var resource = new JsonObject
            {
                ["resource"] = Mcp, ["authorization_servers"] = new JsonArray("https://mcp-auth.granola.ai"),
                ["bearer_methods_supported"] = new JsonArray("header"), ["scopes_supported"] = new JsonArray("mcp"),
            };
            switch (url)
            {
                case "https://mcp.granola.ai/.well-known/oauth-protected-resource/mcp" when PathMetadata:
                case "https://mcp.granola.ai/.well-known/oauth-protected-resource":
                    return Json(resource);
                case "https://mcp-auth.granola.ai/.well-known/oauth-authorization-server" when Rfc8414:
                case "https://mcp-auth.granola.ai/.well-known/openid-configuration":
                    return Json(Meta.DeepClone());
                case "https://mcp-auth.granola.ai/oauth2/register":
                {
                    var reg = (JsonObject)JsonNode.Parse(body)!;
                    Assert.Equal("none", reg["token_endpoint_auth_method"].S()); // a public client: no secret
                    Assert.Equal(["authorization_code", "refresh_token"], reg["grant_types"]!.AsArray().Select(g => g.S()));
                    Registrations++;
                    return Json(new JsonObject { ["client_id"] = "client-" + Registrations, ["redirect_uris"] = reg["redirect_uris"]!.DeepClone() });
                }
                case "https://mcp-auth.granola.ai/oauth2/token":
                {
                    Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
                    var form = Py.ParseQs(body);
                    Assert.Equal(Mcp, form["resource"]);
                    if (form["grant_type"] == "authorization_code")
                    {
                        if (!codes.Remove(form["code"], out var issuedFor))
                            return Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
                        // PKCE: only whoever made the challenge knows the verifier behind it.
                        if (GranolaOAuth.Challenge(form["code_verifier"]) != issuedFor.Challenge || form["client_id"] != issuedFor.ClientId
                            || form["redirect_uri"] != issuedFor.Redirect)
                            return Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
                        return Json(NewTokens());
                    }
                    if (form["grant_type"] == "refresh_token")
                    {
                        if (!refreshTokens.Contains(form["refresh_token"]))
                            return Json(new JsonObject { ["error"] = "invalid_grant" }, HttpStatusCode.BadRequest);
                        var tok = NewTokens();
                        if (RotateRefreshTokens) refreshTokens.Remove(form["refresh_token"]); // OAuth 2.1: one use each
                        else
                        {
                            tok.Remove("refresh_token");
                            refreshTokens.Remove("rt-" + issued);
                        }
                        return Json(tok);
                    }
                    return Json(new JsonObject { ["error"] = "unsupported_grant_type" }, HttpStatusCode.BadRequest);
                }
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static (GranolaOAuth OAuth, FakeAuthServer Server, int Port) Setup(TempDir dir, string prompt = "login")
    {
        var server = new FakeAuthServer();
        int port = FreePort();
        return (new GranolaOAuth(Mcp, port, prompt, dir["tokens.json"], dir["oauth_client.json"], new HttpClient(server)), server, port);
    }

    static Dictionary<string, string> Query(string url) => Py.ParseQs(new Uri(url).Query.TrimStart('?'));

    /// <summary>The browser after a person clicks Allow: Granola sends it back to our callback with a code.</summary>
    static Func<string, Task> Browser(FakeAuthServer server, Func<Dictionary<string, string>, string>? answer = null) => async url =>
    {
        var q = Query(url);
        string back = answer?.Invoke(q) ?? $"code={server.Authorize(q)}&state={Uri.EscapeDataString(q["state"])}";
        using var browser = new HttpClient();
        var favicon = await browser.GetAsync(q["redirect_uri"].Replace("/callback", "/favicon.ico")); // browsers ask for one
        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        string page = await browser.GetStringAsync(q["redirect_uri"] + "?" + back);
        Assert.Contains("connected", page);
    };

    [Fact]
    public void Pkce_matches_the_rfc_7636_example()
    {
        // RFC 7636, Appendix B: this verifier has this S256 challenge.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GranolaOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var (verifier, challenge) = GranolaOAuth.MakePkce();
        Assert.InRange(verifier.Length, 43, 128); // RFC 7636 §4.1
        Assert.Matches("^[A-Za-z0-9._~-]+$", verifier);
        Assert.Equal(GranolaOAuth.Challenge(verifier), challenge);
        Assert.NotEqual(verifier, GranolaOAuth.MakePkce().Verifier);
    }

    [Fact]
    public void Discovery_tries_the_places_the_spec_lists_in_order()
    {
        Assert.Equal(["https://mcp.granola.ai/.well-known/oauth-protected-resource/mcp", "https://mcp.granola.ai/.well-known/oauth-protected-resource"],
            GranolaOAuth.ResourceMetadataUrls(Mcp));
        Assert.Equal(["https://mcp.example.com/.well-known/oauth-protected-resource"], GranolaOAuth.ResourceMetadataUrls("https://mcp.example.com"));
        Assert.Equal(["https://mcp-auth.granola.ai/.well-known/oauth-authorization-server", "https://mcp-auth.granola.ai/.well-known/openid-configuration"],
            GranolaOAuth.ServerMetadataUrls("https://mcp-auth.granola.ai"));
        Assert.Equal(["https://auth.example.com/.well-known/oauth-authorization-server/tenant1",
                "https://auth.example.com/.well-known/openid-configuration/tenant1", "https://auth.example.com/tenant1/.well-known/openid-configuration"],
            GranolaOAuth.ServerMetadataUrls("https://auth.example.com/tenant1/"));
    }

    [Fact]
    public async Task Discovery_falls_back_to_the_root_and_to_openid()
    {
        using var dir = new TempDir();
        var (oauth, server, _) = Setup(dir);
        server.PathMetadata = false;
        server.Rfc8414 = false;
        var meta = await oauth.MetadataAsync();
        Assert.Equal("https://mcp-auth.granola.ai/oauth2/token", meta["token_endpoint"].S());
        Assert.Equal(["https://mcp.granola.ai/.well-known/oauth-protected-resource/mcp", "https://mcp.granola.ai/.well-known/oauth-protected-resource",
            "https://mcp-auth.granola.ai/.well-known/oauth-authorization-server", "https://mcp-auth.granola.ai/.well-known/openid-configuration"],
            server.Requests.Select(r => r.Url));
        await oauth.MetadataAsync();
        Assert.Equal(4, server.Requests.Count); // found once, then remembered
    }

    [Fact]
    public async Task Signing_in_from_start_to_finish()
    {
        using var dir = new TempDir();
        var (oauth, server, port) = Setup(dir);
        var logs = new List<string>();
        string? opened = null;
        var tok = await oauth.LoginAsync(log: logs.Add, openUrl: async url =>
        {
            opened = url;
            await Browser(server)(url);
        });
        var q = Query(opened!);
        Assert.StartsWith("https://mcp-auth.granola.ai/oauth2/authorize?", opened);
        Assert.Equal(($"http://localhost:{port}/callback", "mcp offline_access", "login", "client-1"),
            (q["redirect_uri"], q["scope"], q["prompt"], q["client_id"]));
        Assert.Equal(43, q["code_challenge"].Length);
        Assert.Contains(opened!, logs[0]); // someone without a browser can copy it
        Assert.StartsWith("Logged in.", logs[^1]);

        // The registration asked for both spellings of the loopback address; the tokens were saved with a deadline.
        var registration = (JsonObject)JsonNode.Parse(server.Requests.Single(r => r.Url.EndsWith("/register")).Body)!;
        Assert.Equal([$"http://localhost:{port}/callback", $"http://127.0.0.1:{port}/callback"], registration["redirect_uris"]!.AsArray().Select(u => u.S()));
        var saved = oauth.LoadTokens()!;
        Assert.Equal(("at-2", "rt-2"), (saved["access_token"].S(), saved["refresh_token"].S()));
        Assert.InRange(saved["expires_at"]!.GetValue<double>() - Py.Time(), 3590, 3601);
        Assert.Equal(tok["access_token"].S(), await oauth.AccessTokenAsync());
        Assert.True(oauth.IsLoggedIn());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dir["tokens.json"]));

        // Signing in again reuses the registration; logging out forgets the tokens only.
        await oauth.LoginAsync(log: _ => { }, openUrl: Browser(server));
        Assert.Equal(1, server.Registrations);
        oauth.Logout();
        Assert.False(oauth.IsLoggedIn());
        Assert.True(File.Exists(dir["oauth_client.json"]));
    }

    [Fact]
    public async Task A_code_the_verifier_does_not_match_is_refused()
    {
        using var dir = new TempDir();
        var (oauth, server, _) = Setup(dir);
        // Someone else's code, from a sign-in they started: this app's verifier can't redeem it.
        var e = await Assert.ThrowsAsync<OAuthException>(() => oauth.LoginAsync(log: _ => { }, openUrl: Browser(server, q =>
        {
            var theirs = new Dictionary<string, string>(q) { ["code_challenge"] = GranolaOAuth.MakePkce().Challenge };
            return $"code={server.Authorize(theirs)}&state={Uri.EscapeDataString(q["state"])}";
        })));
        Assert.StartsWith("token exchange failed (400):", e.Message);
        Assert.False(oauth.IsLoggedIn());
    }

    [Fact]
    public async Task A_callback_from_another_sign_in_is_refused()
    {
        using var dir = new TempDir();
        var (oauth, server, _) = Setup(dir);
        var e = await Assert.ThrowsAsync<OAuthException>(() => oauth.LoginAsync(log: _ => { },
            openUrl: Browser(server, q => $"code={server.Authorize(q)}&state=forged")));
        Assert.Equal("state mismatch in OAuth callback", e.Message);
        Assert.DoesNotContain(server.Requests, r => r.Url.EndsWith("/token")); // the code was never even tried
    }

    [Fact]
    public async Task Saying_no_and_walking_away_both_end_the_sign_in()
    {
        using var dir = new TempDir();
        var (oauth, server, _) = Setup(dir);
        var denied = await Assert.ThrowsAsync<OAuthException>(() => oauth.LoginAsync(log: _ => { },
            openUrl: Browser(server, _ => "error=access_denied&error_description=The+user+said+no")));
        Assert.Equal("login denied: access_denied The user said no", denied.Message);
        var timeout = await Assert.ThrowsAsync<OAuthException>(() => oauth.LoginAsync(timeout: TimeSpan.FromMilliseconds(300), log: _ => { },
            openUrl: _ => Task.CompletedTask));
        Assert.Equal("timed out waiting for the browser callback", timeout.Message);
    }

    [Fact]
    public async Task A_server_without_pkce_is_refused_before_the_browser_opens()
    {
        using var dir = new TempDir();
        var (oauth, server, _) = Setup(dir);
        server.Meta.Remove("code_challenge_methods_supported");
        bool opened = false;
        var e = await Assert.ThrowsAsync<OAuthException>(() => oauth.LoginAsync(log: _ => { }, openUrl: _ =>
        {
            opened = true;
            return Task.CompletedTask;
        }));
        Assert.Contains("PKCE", e.Message);
        Assert.False(opened);
    }

    [Fact]
    public async Task A_busy_callback_port_says_so()
    {
        using var dir = new TempDir();
        var (oauth, _, port) = Setup(dir);
        var squatter = new TcpListener(IPAddress.Loopback, port);
        squatter.Start();
        try
        {
            var e = await Assert.ThrowsAsync<OAuthException>(() => oauth.LoginAsync(log: _ => { }, openUrl: _ => Task.CompletedTask));
            Assert.Contains($"port {port}", e.Message);
        }
        finally
        {
            squatter.Stop();
        }
    }

    [Fact]
    public async Task An_expired_token_refreshes_itself()
    {
        using var dir = new TempDir();
        var (oauth, server, _) = Setup(dir);
        await oauth.LoginAsync(log: _ => { }, openUrl: Browser(server));
        var tok = oauth.LoadTokens()!;
        tok["expires_at"] = Py.Time() + 30; // under a minute left counts as expired
        oauth.SaveTokens(tok);
        Assert.Equal("at-3", await oauth.AccessTokenAsync());
        var refresh = Py.ParseQs(server.Requests[^1].Body);
        Assert.Equal(("refresh_token", "rt-2", "client-1", Mcp), (refresh["grant_type"], refresh["refresh_token"], refresh["client_id"], refresh["resource"]));
        Assert.Equal("rt-3", oauth.LoadTokens()!["refresh_token"].S()); // the rotated one is kept

        // A server that doesn't rotate: the old refresh token stays.
        server.RotateRefreshTokens = false;
        tok = oauth.LoadTokens()!;
        tok["expires_at"] = 0;
        oauth.SaveTokens(tok);
        await oauth.AccessTokenAsync();
        Assert.Equal("rt-3", oauth.LoadTokens()!["refresh_token"].S());

        // A refresh the server refuses asks for a new sign-in.
        tok = oauth.LoadTokens()!;
        tok["expires_at"] = 0;
        tok["refresh_token"] = "revoked";
        oauth.SaveTokens(tok);
        var e = await Assert.ThrowsAsync<OAuthException>(() => oauth.AccessTokenAsync());
        Assert.StartsWith("token refresh failed (400):", e.Message);
        Assert.EndsWith("Run `granola-share login`.", e.Message);
    }

    [Fact]
    public async Task Without_tokens_it_asks_you_to_sign_in()
    {
        using var dir = new TempDir();
        var (oauth, _, _) = Setup(dir);
        Assert.Equal("not logged in: run `granola-share login`", (await Assert.ThrowsAsync<OAuthException>(() => oauth.AccessTokenAsync())).Message);
        File.WriteAllText(oauth.TokensPath, "{}"); // an empty file is no sign-in either, as in Python
        Assert.Equal("not logged in: run `granola-share login`", (await Assert.ThrowsAsync<OAuthException>(() => oauth.AccessTokenAsync())).Message);
        oauth.SaveTokens(new JsonObject { ["access_token"] = "x", ["expires_in"] = -120 });
        Assert.Equal("access token expired and no refresh token: run `granola-share login`",
            (await Assert.ThrowsAsync<OAuthException>(() => oauth.AccessTokenAsync())).Message);
    }

    [Fact]
    public async Task A_new_callback_port_means_a_new_registration()
    {
        using var dir = new TempDir();
        var server = new FakeAuthServer();
        var first = new GranolaOAuth(Mcp, 4001, "login", dir["tokens.json"], dir["oauth_client.json"], new HttpClient(server));
        Assert.Equal("client-1", await first.ClientIdAsync());
        Assert.Equal("client-1", await first.ClientIdAsync());
        var moved = new GranolaOAuth(Mcp, 4002, "login", dir["tokens.json"], dir["oauth_client.json"], new HttpClient(server));
        Assert.Equal("client-2", await moved.ClientIdAsync()); // Granola only sends people back to a registered address
    }

    [Fact]
    public async Task The_sign_in_files_and_address_match_python_byte_for_byte()
    {
        using var dir = new TempDir();
        var server = new FakeAuthServer();
        var oauth = new GranolaOAuth(Mcp, 3334, "login", dir["tokens.json"], dir["oauth_client.json"], new HttpClient(server));
        File.WriteAllText(dir["oauth_client.json"], Golden.Granola("client_file").S());
        Assert.Equal(Golden.Granola("authorize_url").S(), await oauth.BuildAuthorizeUrlAsync("st4te_-x", "ch4llenge~"));
        var bare = new GranolaOAuth(Mcp, 3334, "", dir["tokens.json"], dir["oauth_client.json"], new HttpClient(server));
        Assert.Equal(Golden.Granola("authorize_url_bare").S(), await bare.BuildAuthorizeUrlAsync("s", "c"));

        oauth.SaveTokens((JsonObject)Golden.Granola("tokens")!.DeepClone());
        Assert.Equal(Golden.Granola("tokens_file").S(), Py.ReadText(dir["tokens.json"]));

        // What this engine registers and saves is what the Python engine would have saved.
        File.Delete(dir["oauth_client.json"]);
        var registering = new GranolaOAuth(Mcp, 3334, "login", dir["t2.json"], dir["oauth_client.json"], new HttpClient(new RenamingServer(server)));
        await registering.ClientIdAsync();
        Assert.Equal(Golden.Granola("client_file").S(), Py.ReadText(dir["oauth_client.json"]));
    }

    /// <summary>Hands out the client id the Python golden file was made with.</summary>
    sealed class RenamingServer(FakeAuthServer inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = await base.SendAsync(request, ct);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/register")) return r;
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"client_id": "client-abc/123 x+y"}""") };
        }
    }

    [Fact]
    public void Url_encoding_matches_python()
    {
        Assert.Equal(Golden.Granola("urlencode").S(), Py.UrlEncode([("a b", "c/d:e?f=g&h"), ("é", "~_.-!*()'"), ("plus", "1+1")]));
        foreach (var c in (JsonArray)Golden.Granola("parse_qs")!)
            Assert.Equal(Golden.Dump(c![1]), Golden.Dump(new JsonObject(Py.ParseQs(c[0].S()).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)))));
    }
}
