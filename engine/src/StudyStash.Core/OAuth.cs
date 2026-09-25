using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core;

public sealed class OAuthException(string message) : Exception(message);

/// <summary>
/// Signing in to Granola's MCP server, the way the MCP authorization spec lays it out (OAuth 2.1). Find the sign-in
/// server from the MCP server's metadata (RFC 9728, then RFC 8414 or OpenID discovery). Register this app once
/// (dynamic client registration, RFC 7591), kept in oauth_client.json. Then sign in with an authorization code, PKCE
/// (S256, RFC 7636) and a localhost callback. Every request names the MCP server as the `resource` (RFC 8707).
/// Tokens live in tokens.json and refresh themselves. Both files are written exactly as the Python engine writes them.
/// </summary>
public sealed class GranolaOAuth(string mcpUrl, int callbackPort, string prompt, string tokensPath, string clientPath,
    HttpClient? http = null)
{
    const string Scope = "mcp offline_access";
    static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    readonly HttpClient http = http ?? SharedHttp;
    JsonObject? meta;

    public static GranolaOAuth For(Config cfg, HttpClient? http = null) =>
        new(cfg.McpUrl, cfg.OauthCallbackPort, cfg.OauthPrompt, cfg.TokensPath, cfg.ClientPath, http);

    public static GranolaOAuth For(ClientConfig cc, HttpClient? http = null) =>
        new(cc.McpUrl, cc.OauthCallbackPort, cc.OauthPrompt, cc.TokensPath, cc.ClientPath, http);

    public string TokensPath => tokensPath;

    static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The S256 code challenge for a verifier.</summary>
    public static string Challenge(string verifier) => B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A fresh (verifier, challenge) pair: 48 random bytes, as the Python engine makes them.</summary>
    public static (string Verifier, string Challenge) MakePkce()
    {
        string verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Challenge(verifier));
    }

    static void WritePrivate(string path, JsonObject data)
    {
        Directory.CreateDirectory(Py.Parent(path));
        Py.WriteText(path, PyJson.Dumps(data, indent: 2));
        Py.OwnerOnly(path);
    }

    static JsonObject ReadJson(string path) =>
        Py.JsonLoads(Py.ReadText(path)) as JsonObject ?? throw new OAuthException($"{Path.GetFileName(path)} is not a JSON object");

    async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var r = await http.SendAsync(request, ct);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(ct));
    }

    async Task<JsonObject?> GetJsonAsync(string url, CancellationToken ct)
    {
        var (status, body) = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (status != HttpStatusCode.OK) return null;
        return Py.JsonLoads(body) as JsonObject;
    }

    async Task<JsonObject> PostFormAsync(string url, (string, string)[] form, string failure, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Py.UrlEncode(form), Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        var (status, body) = await SendAsync(request, ct);
        if ((int)status >= 400) throw new OAuthException(string.Format(failure, (int)status, body));
        return Py.JsonLoads(body) as JsonObject ?? throw new OAuthException($"{url} sent something other than a JSON object");
    }

    // --- discovery ---------------------------------------------------------------------------------------------

    /// <summary>RFC 9728's two places for the MCP server's metadata: under its own path first, then the root.</summary>
    internal static List<string> ResourceMetadataUrls(string resource)
    {
        var u = new Uri(resource);
        string origin = u.GetLeftPart(UriPartial.Authority), path = u.AbsolutePath.TrimEnd('/');
        var urls = new List<string>();
        if (path.Length > 0) urls.Add($"{origin}/.well-known/oauth-protected-resource{path}");
        urls.Add($"{origin}/.well-known/oauth-protected-resource");
        return urls;
    }

    /// <summary>The spec's order for a sign-in server's metadata: RFC 8414, then OpenID discovery.</summary>
    internal static List<string> ServerMetadataUrls(string issuer)
    {
        var u = new Uri(issuer);
        string origin = u.GetLeftPart(UriPartial.Authority), path = u.AbsolutePath.TrimEnd('/');
        return path.Length == 0
            ? [$"{origin}/.well-known/oauth-authorization-server", $"{origin}/.well-known/openid-configuration"]
            : [$"{origin}/.well-known/oauth-authorization-server{path}", $"{origin}/.well-known/openid-configuration{path}",
                $"{origin}{path}/.well-known/openid-configuration"];
    }

    public async Task<JsonObject> MetadataAsync(CancellationToken ct = default)
    {
        if (meta is not null) return meta;
        JsonObject? resource = null;
        foreach (string url in ResourceMetadataUrls(mcpUrl))
            if ((resource = await GetJsonAsync(url, ct)) is not null) break;
        if (resource is null) throw new OAuthException($"{mcpUrl} publishes no OAuth metadata, so there's nowhere to sign in");
        var servers = resource["authorization_servers"] as JsonArray;
        if (servers is not { Count: > 0 } || Py.AsString(servers[0]) is not string first)
            throw new OAuthException("MCP resource metadata lists no authorization server");
        string issuer = first.TrimEnd('/');
        foreach (string url in ServerMetadataUrls(issuer))
            if ((meta = await GetJsonAsync(url, ct)) is not null) return meta;
        throw new OAuthException($"the sign-in server {issuer} publishes no metadata");
    }

    public string RedirectUri => $"http://localhost:{callbackPort}/callback";

    static string Endpoint(JsonObject meta, string key) =>
        Py.AsString(meta[key]) ?? throw new OAuthException($"Granola's sign-in server lists no {key}");

    // --- client registration -------------------------------------------------------------------------------------

    public async Task<string> ClientIdAsync(CancellationToken ct = default)
    {
        if (File.Exists(clientPath))
        {
            var saved = ReadJson(clientPath);
            if (Py.AsString(saved["redirect_uri"]) == RedirectUri && Py.AsString(saved["client_id"]) is string id) return id;
        }
        var m = await MetadataAsync(ct);
        var body = new JsonObject
        {
            ["client_name"] = "Study Stash",
            ["redirect_uris"] = new JsonArray(RedirectUri, $"http://127.0.0.1:{callbackPort}/callback"),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = Scope,
        };
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(m, "registration_endpoint"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var (status, text) = await SendAsync(request, ct);
        if ((int)status >= 400) throw new OAuthException($"client registration failed: {(int)status} {text}");
        string cid = Py.AsString((Py.JsonLoads(text) as JsonObject)?["client_id"])
            ?? throw new OAuthException("client registration failed: no client_id came back");
        WritePrivate(clientPath, new JsonObject { ["client_id"] = cid, ["redirect_uri"] = RedirectUri });
        return cid;
    }

    // --- tokens -------------------------------------------------------------------------------------------------

    public JsonObject? LoadTokens() => File.Exists(tokensPath) ? ReadJson(tokensPath) : null;

    /// <summary>Saved as sent, plus expires_at (seconds since 1970) worked out from expires_in.</summary>
    public void SaveTokens(JsonObject tok)
    {
        tok = (JsonObject)tok.DeepClone();
        if (tok.ContainsKey("expires_in") && !tok.ContainsKey("expires_at"))
            tok["expires_at"] = Py.Time() + Number(tok["expires_in"]);
        WritePrivate(tokensPath, tok);
    }

    static double Number(JsonNode? v) => v switch
    {
        JsonValue n when n.GetValueKind() == JsonValueKind.Number => Py.NumberValue(n),
        JsonValue s when Py.AsString(s) is string text => double.Parse(Py.Strip(text), System.Globalization.CultureInfo.InvariantCulture),
        _ => 0,
    };

    public void Logout()
    {
        if (File.Exists(tokensPath)) File.Delete(tokensPath);
    }

    public bool IsLoggedIn() => LoadTokens() is not null;

    /// <summary>A usable access token, refreshed first when it has less than a minute left.</summary>
    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        var tok = LoadTokens() ?? throw new OAuthException("not logged in: run `granola-share login`");
        if (Number(tok["expires_at"]) - 60 < Py.Time()) tok = await RefreshAsync(tok, ct);
        return Py.AsString(tok["access_token"]) ?? throw new OAuthException("tokens.json has no access token: run `granola-share login`");
    }

    public async Task<JsonObject> RefreshAsync(JsonObject tok, CancellationToken ct = default)
    {
        if (!Py.Truthy(tok["refresh_token"]))
            throw new OAuthException("access token expired and no refresh token: run `granola-share login`");
        string rt = Py.Str(tok["refresh_token"]);
        var m = await MetadataAsync(ct);
        var fresh = await PostFormAsync(Endpoint(m, "token_endpoint"),
            [("grant_type", "refresh_token"), ("refresh_token", rt), ("client_id", await ClientIdAsync(ct)), ("resource", mcpUrl)],
            "token refresh failed ({0}): {1}. Run `granola-share login`.", ct);
        if (!fresh.ContainsKey("refresh_token")) fresh["refresh_token"] = rt; // a server that doesn't rotate it
        SaveTokens(fresh);
        return LoadTokens() ?? fresh;
    }

    // --- signing in ---------------------------------------------------------------------------------------------

    public async Task<string> BuildAuthorizeUrlAsync(string state, string challenge, CancellationToken ct = default)
    {
        var m = await MetadataAsync(ct);
        // The spec: no advertised S256 means no PKCE, and then a client must not go on.
        if ((m["code_challenge_methods_supported"] as JsonArray)?.Any(x => Py.AsString(x) == "S256") != true)
            throw new OAuthException("Granola's sign-in server doesn't offer PKCE (S256), so Study Stash won't sign in there");
        var query = new List<(string, string)>
        {
            ("response_type", "code"), ("client_id", await ClientIdAsync(ct)), ("redirect_uri", RedirectUri), ("scope", Scope),
            ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("resource", mcpUrl),
        };
        // prompt=login forces the account picker, so a browser already signed in to another Google account can't
        // quietly connect the wrong (empty) Granola account.
        if (prompt.Length > 0) query.Add(("prompt", prompt));
        return Endpoint(m, "authorization_endpoint") + "?" + Py.UrlEncode(query);
    }

    public async Task<JsonObject> ExchangeCodeAsync(string code, string verifier, CancellationToken ct = default)
    {
        var m = await MetadataAsync(ct);
        var tok = await PostFormAsync(Endpoint(m, "token_endpoint"),
            [("grant_type", "authorization_code"), ("code", code), ("redirect_uri", RedirectUri),
                ("client_id", await ClientIdAsync(ct)), ("code_verifier", verifier), ("resource", mcpUrl)],
            "token exchange failed ({0}): {1}", ct);
        SaveTokens(tok);
        return tok;
    }

    /// <summary>
    /// Open the sign-in page and wait for Granola to send the browser back to http://localhost:PORT/callback.
    /// `openUrl` opens the browser (tests pass their own).
    /// </summary>
    public async Task<JsonObject> LoginAsync(bool openBrowser = true, TimeSpan? timeout = null, Action<string>? log = null,
        Func<string, Task>? openUrl = null, CancellationToken ct = default)
    {
        log ??= Console.WriteLine;
        var (verifier, challenge) = MakePkce();
        string state = B64Url(RandomNumberGenerator.GetBytes(16));
        string url = await BuildAuthorizeUrlAsync(state, challenge, ct);
        using var callback = CallbackListener.Start(callbackPort);
        log("Open this URL to sign in to Granola:\n\n  " + url + "\n");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(300));
        var waiting = callback.WaitAsync(cts.Token);
        if (openBrowser)
        {
            try
            {
                await (openUrl ?? OpenInBrowser)(url);
            }
            catch (Exception)
            {
                // the URL is in the log for a person to open
            }
        }
        var received = await waiting;
        if (received.TryGetValue("error", out string? error))
            throw new OAuthException($"login denied: {error} {received.GetValueOrDefault("error_description", "")}");
        if (!received.TryGetValue("code", out string? code)) throw new OAuthException("timed out waiting for the browser callback");
        if (received.GetValueOrDefault("state") != state) throw new OAuthException("state mismatch in OAuth callback");
        var tok = await ExchangeCodeAsync(code, verifier, ct);
        log("Logged in. Tokens saved to " + tokensPath);
        return tok;
    }

    static Task OpenInBrowser(string url)
    {
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
            : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [url]);
        using var _ = Process.Start(psi);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The page the browser lands on: http://localhost:PORT/callback?code=...&amp;state=... A bare socket rather than
    /// HttpListener, so Windows needs no admin rights for it. It answers on 127.0.0.1 and, where there is one, ::1,
    /// since "localhost" may be either.
    /// </summary>
    sealed class CallbackListener : IDisposable
    {
        readonly List<TcpListener> listeners = [];

        public static CallbackListener Start(int port)
        {
            var c = new CallbackListener();
            try
            {
                var v4 = new TcpListener(IPAddress.Loopback, port);
                v4.Start();
                c.listeners.Add(v4);
            }
            catch (SocketException e)
            {
                throw new OAuthException($"can't listen on port {port} for the sign-in callback ({e.SocketErrorCode}): is another sign-in open?");
            }
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
                    v6.Start();
                    c.listeners.Add(v6);
                }
                catch (SocketException)
                {
                    // no IPv6 loopback here: 127.0.0.1 is enough
                }
            }
            return c;
        }

        /// <summary>Answers requests until one brings a code or an error; the query of every /callback counts.</summary>
        public async Task<Dictionary<string, string>> WaitAsync(CancellationToken ct)
        {
            var received = new Dictionary<string, string>();
            var accepts = listeners.Select(l => l.AcceptTcpClientAsync(ct).AsTask()).ToList();
            try
            {
                while (!received.ContainsKey("code") && !received.ContainsKey("error"))
                {
                    var done = await Task.WhenAny(accepts);
                    int which = accepts.IndexOf(done);
                    using (var client = await done)
                        await AnswerAsync(client, received, ct);
                    accepts[which] = listeners[which].AcceptTcpClientAsync(ct).AsTask();
                }
            }
            catch (OperationCanceledException)
            {
                // the time is up: the caller says so
            }
            return received;
        }

        static async Task AnswerAsync(TcpClient client, Dictionary<string, string> received, CancellationToken ct)
        {
            var stream = client.GetStream();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var head = new StringBuilder();
            var buffer = new byte[4096];
            while (!head.ToString().Contains("\r\n\r\n") && head.Length < 16384)
            {
                int n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0) break;
                head.Append(Encoding.Latin1.GetString(buffer, 0, n));
            }
            string[] requestLine = head.ToString().Split("\r\n")[0].Split(' ');
            string target = requestLine.Length >= 2 ? requestLine[1] : "";
            int q = target.IndexOf('?');
            string path = q >= 0 ? target[..q] : target;
            string reply;
            if (path != "/callback")
            {
                reply = "HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\n\r\n";
            }
            else
            {
                foreach (var (k, v) in Py.ParseQs(q >= 0 ? target[(q + 1)..] : "")) received[k] = v;
                const string page = "<h2>granola-share is connected.</h2><p>You can close this tab.</p>";
                reply = $"HTTP/1.0 200 OK\r\nContent-Type: text/html\r\nContent-Length: {page.Length}\r\n\r\n{page}";
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cts.Token);
        }

        public void Dispose()
        {
            foreach (var l in listeners) l.Stop();
        }
    }
}
