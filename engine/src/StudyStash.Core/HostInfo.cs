using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>What `tailscale status --json` says. State is Tailscale's own: Running, NeedsLogin, Stopped, or "".</summary>
public sealed record TailscaleInfo(bool Installed = false, bool Running = false, string State = "", string Dns = "",
    List<string>? Ips = null, string Exe = "")
{
    public List<string> Ips { get; init; } = Ips ?? [];
}

/// <summary>Where this computer can be reached (Tailscale), and the one-line install for your laptop.</summary>
public static partial class HostInfo
{
    public const string Raw = "https://raw.githubusercontent.com/Joseph-Rus/study-stash/main";

    public static readonly string[] TailscalePaths =
        ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/opt/homebrew/bin/tailscale", @"C:\Program Files\Tailscale\tailscale.exe"];

    static IEnumerable<string> TailscaleCandidates() =>
        new[] { Machine.Which("tailscale") }.Concat(TailscalePaths).OfType<string>().Where(File.Exists);

    public static string? TailscaleExe() => TailscaleCandidates().FirstOrDefault();

    public static TailscaleInfo Tailscale(Runner? run = null, IEnumerable<string>? candidates = null)
    {
        var info = new TailscaleInfo();
        foreach (string exe in candidates ?? TailscaleCandidates())
        {
            info = info with { Installed = true, Exe = exe };
            JsonObject? data;
            try
            {
                data = Py.JsonLoads((run ?? Machine.Run)(exe, ["status", "--json"], TimeSpan.FromSeconds(10))?.Stdout ?? "") as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (data is null) continue;
            var me = data["Self"] as JsonObject ?? new JsonObject();
            string state = Py.Truthy(data["BackendState"]) ? Py.Str(data["BackendState"]) : "";
            var ips = (me["TailscaleIPs"] as JsonArray ?? new JsonArray()).Select(Py.Str).Where(ip => ip.Contains('.')).ToList();
            return info with
            {
                State = state, Running = state == "Running", Ips = ips,
                Dns = (Py.Truthy(me["DNSName"]) ? Py.Str(me["DNSName"]) : "").TrimEnd('.'),
            };
        }
        return info;
    }

    /// <summary>What's wrong with Tailscale here, in a few words, or "" when it's connected.</summary>
    public static string TailscaleProblem(TailscaleInfo ts)
    {
        if (ts.Running) return "";
        if (!ts.Installed) return "not installed";
        return ts.State switch
        {
            "NeedsLogin" => "installed, but signed out",
            "NeedsMachineAuth" => "waiting for approval in your Tailscale admin page",
            "Stopped" => "installed, but turned off",
            _ => "installed, but not running",
        };
    }

    /// <summary>Addresses your laptop can use, best first: the Tailscale name, its IP, then the local name.</summary>
    public static List<string> ServerUrls(int port, TailscaleInfo ts, string? hostName = null)
    {
        var urls = new List<string>();
        if (ts.Running)
        {
            if (ts.Dns.Length > 0) urls.Add($"http://{ts.Dns}:{port}");
            urls.AddRange(ts.Ips.Take(1).Select(ip => $"http://{ip}:{port}"));
        }
        urls.Add($"http://{hostName ?? Machine.HostName()}:{port}");
        return urls;
    }

    [GeneratedRegex(@"[^\w@%+=:,./-]", RegexOptions.ECMAScript)]
    private static partial Regex ShellUnsafe();

    /// <summary>shlex.quote: as it is when it's safe for a shell, else in single quotes.</summary>
    public static string ShQuote(string s) =>
        s.Length == 0 ? "''" : !ShellUnsafe().IsMatch(s) ? s : "'" + s.Replace("'", "'\"'\"'") + "'";

    static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>One-liners that install the laptop side with this library's address and password filled in.</summary>
    public static (string Mac, string Windows) InviteCommands(string url, string key) => (
        $"curl -fsSL {Raw}/install.sh | GRANOLA_SHARE_SERVER={ShQuote(url)} GRANOLA_SHARE_KEY={ShQuote(key)} sh",
        $"$env:GRANOLA_SHARE_SERVER={PsQuote(url)}; $env:GRANOLA_SHARE_KEY={PsQuote(key)}; irm {Raw}/install.ps1 | iex");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>"free", "ours" (a Study Stash library already answers there), or "busy" (something else has it).</summary>
    public static async Task<string> PortStatusAsync(int port, HttpClient? http = null)
    {
        using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            // POSIX: ignore sockets lingering after a restart, as the web server does. (On Windows this flag would
            // allow sharing a port someone is listening on.)
            if (!OperatingSystem.IsWindows()) s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                s.Bind(new IPEndPoint(IPAddress.Any, port));
                return "free";
            }
            catch (SocketException)
            {
            }
        }
        try
        {
            using var r = await (http ?? Http).GetAsync($"http://127.0.0.1:{port}/api/health");
            if (r.Headers.Contains("x-granola-share")) return "ours";
            // 0.1 didn't send that header, but its health check answers in a recognizable way.
            var body = r.Content.Headers.ContentType?.MediaType == "application/json"
                ? Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject : null;
            if ((r.StatusCode == HttpStatusCode.Unauthorized && Py.AsString(body?["detail"]) == "bad pool password")
                || (r.StatusCode == HttpStatusCode.OK && body is not null && body.ContainsKey("pool_name") && body.ContainsKey("classes")))
                return "ours";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
        return "busy";
    }

    /// <summary>Wait for this computer's library to answer, as this version. What setup does after starting it.</summary>
    public static async Task<bool> WaitForServerAsync(Config cfg, TimeSpan? timeout = null, HttpClient? http = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(25));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{cfg.WebPort}/api/health");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.PoolPassword);
                using var r = await (http ?? Http).SendAsync(request);
                if (r.StatusCode == HttpStatusCode.OK
                    && Py.AsString((Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject)?["version"]) == Engine.Version)
                    return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
            }
            await Task.Delay(1000);
        }
        return false;
    }
}
