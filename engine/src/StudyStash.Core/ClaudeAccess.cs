using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyStash.Core;

/// <summary>An app that registered to sign in to the library (claude.ai, or Claude Code): OAuth dynamic client
/// registration.</summary>
public sealed class ClaudeClient
{
    public required string ClientId { get; init; }
    public string Name { get; init; } = "";
    public List<string> RedirectUris { get; init; } = [];
    public double Created { get; init; }
}

/// <summary>
/// One connection that may read the library: a Claude that signed in (with an access token that lasts an hour and
/// a refresh token that renews it), or a token made in Settings for another MCP client. Only hashes are kept.
/// </summary>
public sealed class ClaudeGrant
{
    public required string Id { get; init; }
    public required string Kind { get; init; } // "signin" | "token"
    public string ClientId { get; init; } = "";
    public string Name { get; set; } = "";
    public string AccessHash { get; set; } = "";
    public double AccessExpires { get; set; }
    public string RefreshHash { get; set; } = "";
    public double Created { get; init; }
    public double LastUsed { get; set; }
}

/// <summary>An authorization code waiting to be exchanged: whose it is, where it goes, and its PKCE challenge.</summary>
public sealed record ClaudeCode(string ClientId, string RedirectUri, string Challenge, string Resource, double Expires);

/// <summary>What /token hands back.</summary>
public sealed record ClaudeTokens(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Who may read the library through its MCP server, kept in claude.json: registered clients, sign-ins and tokens.
/// This is the OAuth 2.1 authorization server Claude needs (authorization code with PKCE, refresh tokens, dynamic
/// client registration); a person allows a sign-in with the library's password.
/// </summary>
public sealed class ClaudeAccess
{
    public const double AccessSeconds = 3600, CodeSeconds = 600;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    sealed class State
    {
        public List<ClaudeClient> Clients { get; set; } = [];
        public List<ClaudeGrant> Grants { get; set; } = [];
        /// <summary>The library's public address for Claude on the web (https://mini.tail1234.ts.net), when it's on.</summary>
        public string PublicUrl { get; set; } = "";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TailnetUrl { get; set; }
    }

    readonly string path;
    readonly Func<DateTimeOffset> clock;
    readonly Lock gate = new();
    readonly ConcurrentDictionary<string, ClaudeCode> codes = new();
    readonly List<double> failures = [];
    State state;
    DateTime loadedAt;

    public ClaudeAccess(string home, Func<DateTimeOffset>? clock = null)
    {
        path = Path.Combine(home, "claude.json");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        state = Load();
    }

    /// <summary>Another copy changed claude.json (the Study Stash app, or a second library process): read it again, so a
    /// disconnect takes effect at once. Called under the lock.</summary>
    void Fresh()
    {
        var at = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (at != loadedAt) state = Load();
    }

    double Now => clock().ToUnixTimeMilliseconds() / 1000.0;

    State Load()
    {
        try
        {
            loadedAt = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new State();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new State();
        }
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, path, overwrite: true);
        Py.OwnerOnly(path);
        loadedAt = File.GetLastWriteTimeUtc(path);
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    static string NewToken(string prefix) => prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string PublicUrl
    {
        get
        {
            lock (gate)
            {
                Fresh();
                return state.PublicUrl;
            }
        }
        set
        {
            lock (gate)
            {
                Fresh();
                state.PublicUrl = value.TrimEnd('/');
                Save();
            }
        }
    }

    public string? TailnetUrl
    {
        get
        {
            lock (gate)
            {
                Fresh();
                return state.TailnetUrl;
            }
        }
        set
        {
            lock (gate)
            {
                Fresh();
                state.TailnetUrl = value?.TrimEnd('/');
                Save();
            }
        }
    }

    // --- clients ------------------------------------------------------------------------------------------------

    /// <summary>A redirect a sign-in may go back to: https anywhere, or http only to this computer (a CLI's
    /// callback).</summary>
    public static bool GoodRedirect(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Fragment.Length == 0
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host == "localhost")));

    public ClaudeClient Register(string name, IEnumerable<string> redirectUris)
    {
        var uris = redirectUris.ToList();
        if (uris.Count == 0 || !uris.All(GoodRedirect)) throw new ArgumentException("redirect_uris must be https, or http on localhost");
        var client = new ClaudeClient { ClientId = "ssc_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)), Name = name.Trim(), RedirectUris = uris, Created = Now };
        lock (gate)
        {
            Fresh();
            state.Clients.Add(client);
            // Registration is open (it has to be), so keep the list from growing without end: drop the oldest
            // clients that never signed in.
            var unused = state.Clients.Where(c => state.Grants.All(g => g.ClientId != c.ClientId)).OrderBy(c => c.Created).ToList();
            foreach (var c in unused.Take(Math.Max(0, unused.Count - 50))) state.Clients.Remove(c);
            Save();
        }
        return client;
    }

    public ClaudeClient? Client(string clientId)
    {
        lock (gate)
        {
            Fresh();
            return state.Clients.FirstOrDefault(c => c.ClientId == clientId);
        }
    }

    // --- signing in -------------------------------------------------------------------------------------------------

    /// <summary>Too many wrong passwords lately: the sign-in page refuses for a while (it's on the internet).</summary>
    public bool LockedOut()
    {
        lock (gate)
        {
            failures.RemoveAll(t => Now - t > 900);
            return failures.Count >= 8;
        }
    }

    public void Failed()
    {
        lock (gate) failures.Add(Now);
    }

    public string NewCode(string clientId, string redirectUri, string challenge, string resource)
    {
        foreach (var (k, c) in codes)
            if (c.Expires < Now) codes.TryRemove(k, out _);
        string code = NewToken("sscode_");
        codes[code] = new ClaudeCode(clientId, redirectUri, challenge, resource, Now + CodeSeconds);
        return code;
    }

    static string S256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A code for tokens (once), if the client, the redirect and the PKCE verifier all match. Else the
    /// OAuth error.</summary>
    public (ClaudeTokens? Tokens, string? Error) Exchange(string code, string verifier, string clientId, string redirectUri)
    {
        if (!codes.TryRemove(code, out var c) || c.Expires < Now) return (null, "invalid_grant");
        if (c.ClientId != clientId || c.RedirectUri != redirectUri) return (null, "invalid_grant");
        if (verifier.Length < 43 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(S256(verifier)), Encoding.ASCII.GetBytes(c.Challenge)))
            return (null, "invalid_grant");
        string access = NewToken("ssa_"), refresh = NewToken("ssr_");
        lock (gate)
        {
            Fresh();
            var client = state.Clients.FirstOrDefault(x => x.ClientId == clientId);
            state.Grants.Add(new ClaudeGrant
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), Kind = "signin", ClientId = clientId,
                Name = client?.Name is { Length: > 0 } n ? n : "Claude", AccessHash = Hash(access), AccessExpires = Now + AccessSeconds,
                RefreshHash = Hash(refresh), Created = Now, LastUsed = Now,
            });
            Save();
        }
        return (new ClaudeTokens(access, refresh, (int)AccessSeconds), null);
    }

    /// <summary>A refresh token for a new pair; the old refresh token stops working (rotation).</summary>
    public (ClaudeTokens? Tokens, string? Error) Refresh(string refreshToken, string clientId)
    {
        string h = Hash(refreshToken);
        lock (gate)
        {
            Fresh();
            var g = state.Grants.FirstOrDefault(x => x.Kind == "signin" && x.RefreshHash == h);
            if (g is null || (clientId.Length > 0 && g.ClientId != clientId)) return (null, "invalid_grant");
            string access = NewToken("ssa_"), refresh = NewToken("ssr_");
            g.AccessHash = Hash(access);
            g.AccessExpires = Now + AccessSeconds;
            g.RefreshHash = Hash(refresh);
            g.LastUsed = Now;
            Save();
            return (new ClaudeTokens(access, refresh, (int)AccessSeconds), null);
        }
    }

    /// <summary>A token made in Settings, for an MCP client that takes one (shown once).</summary>
    public (string Token, ClaudeGrant Grant) CreateToken(string name)
    {
        string token = NewToken("sst_");
        var g = new ClaudeGrant
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)), Kind = "token", Name = name.Trim().Length > 0 ? name.Trim() : "MCP client",
            AccessHash = Hash(token), AccessExpires = double.MaxValue, Created = Now,
        };
        lock (gate)
        {
            Fresh();
            state.Grants.Add(g);
            Save();
        }
        return (token, g);
    }

    /// <summary>The connection a bearer token belongs to, if it may read now.</summary>
    public ClaudeGrant? Check(string bearer)
    {
        if (bearer.Length == 0) return null;
        string h = Hash(bearer);
        lock (gate)
        {
            Fresh();
            var g = state.Grants.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x.AccessHash), Encoding.ASCII.GetBytes(h)));
            if (g is null || g.AccessExpires < Now) return null;
            if (Now - g.LastUsed > 60)
            {
                g.LastUsed = Now;
                Save();
            }
            return g;
        }
    }

    public List<ClaudeGrant> Grants()
    {
        lock (gate)
        {
            Fresh();
            return [.. state.Grants.OrderByDescending(g => g.LastUsed)];
        }
    }

    public bool Revoke(string id)
    {
        lock (gate)
        {
            Fresh();
            int n = state.Grants.RemoveAll(g => g.Id == id);
            if (n > 0) Save();
            return n > 0;
        }
    }
}

/// <summary>
/// Putting the Claude port where Claude can reach it, with Tailscale: Serve (HTTPS on your tailnet only: Claude Code
/// on your other computers) or Funnel (HTTPS on the internet: claude.ai and the Claude apps). Off unless
/// <see cref="ThisComputer"/> is used, so a test can't publish anything.
/// </summary>
public sealed class ClaudeReach
{
    public Func<TailscaleInfo> Tailscale { get; init; } = () => new TailscaleInfo();
    public Runner Run { get; init; } = (_, _, _) => throw new InvalidOperationException("Changing Tailscale is off here.");

    public static ClaudeReach ThisComputer() => new() { Tailscale = () => HostInfo.Tailscale(), Run = Machine.Run };

    /// <summary>Serve (tailnet) or Funnel (internet) the Claude port on https://&lt;this computer&gt;.ts.net, or turn it off.
    /// The address, or why not.</summary>
    public (string? Url, string? Problem) Set(int port, bool internet, bool on)
    {
        var ts = Tailscale();
        if (!ts.Installed || ts.Exe.Length == 0) return (null, "Tailscale isn't installed on the library's computer.");
        if (!ts.Running) return (null, "Tailscale isn't running on the library's computer. Open it and sign in.");
        if (ts.Dns.Length == 0) return (null, "Tailscale hasn't given this computer a name yet. Turn on MagicDNS in the Tailscale admin console.");
        string verb = internet ? "funnel" : "serve";
        string[] args = on ? [verb, "--bg", "--https=443", $"http://127.0.0.1:{port}"] : [verb, "--https=443", "off"];
        var p = Run(ts.Exe, args, TimeSpan.FromSeconds(60));
        if (p is not { ExitCode: 0 })
        {
            string said = Py.Strip(p?.Stdout ?? "");
            // Funnel's first use needs a tailnet admin to allow it: Tailscale prints the page for that.
            var link = System.Text.RegularExpressions.Regex.Match(said, @"https://login\.tailscale\.com/\S+");
            return (null, link.Success ? $"Tailscale needs permission first: open {link.Value}, allow it, then try again."
                : $"Tailscale said: {Py.Head(said, 300)}");
        }
        return (on ? $"https://{ts.Dns.TrimEnd('.')}" : null, null);
    }
}
