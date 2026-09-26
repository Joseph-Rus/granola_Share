using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>A library "Find it" turned up, with no password sent: where it is, and its name if it gave one.</summary>
public sealed record FoundLibrary(string Url, string Name, bool Local);

/// <summary>
/// Looks for a library with no password: this computer's own port first, then every online Tailscale peer, in
/// parallel. Read-only: only ever a GET to /api/health and `tailscale status --json`, never a change to anything.
/// </summary>
public sealed class LibraryFinder
{
    const int Port = 8787;
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>A fake server for tests; real HTTP otherwise.</summary>
    public HttpMessageHandler? Handler { get; init; }
    /// <summary>A fake `tailscale` for tests; the real command otherwise.</summary>
    public Runner? Run { get; init; }
    /// <summary>Other ports on this computer worth trying (a library already configured on one, say).</summary>
    public IReadOnlyList<int> ExtraPorts { get; init; } = [];

    async Task<(bool IsLibrary, string PoolName)> ProbeAsync(HttpClient http, string url)
    {
        try
        {
            using var r = await http.GetAsync(url.TrimEnd('/') + "/api/health");
            JsonObject? body = null;
            try
            {
                body = Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject;
            }
            catch (JsonException)
            {
            }
            string name = Py.AsString(body?["pool_name"]) ?? "";
            if (r.Headers.Contains("X-Granola-Share") || r.Headers.Contains("X-Study-Stash")) return (true, name);
            if (r.StatusCode == HttpStatusCode.Unauthorized)
            {
                string detail = Py.AsString(body?["detail"]) ?? "";
                return (detail is "wrong password" or "bad pool password", name);
            }
            return (r.StatusCode == HttpStatusCode.OK && name.Length > 0, name);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (false, "");
        }
    }

    /// <summary>Every online peer's name and its first Tailscale IPv4 address, from `tailscale status --json`.</summary>
    List<(string Name, string Ip)> Peers()
    {
        var result = new List<(string, string)>();
        // A test's fake Runner doesn't care what "exe" it's handed; only the real path needs a real Tailscale.
        string? exe = Run is not null ? "tailscale" : HostInfo.TailscaleExe();
        if (exe is null) return result;
        JsonObject? data;
        try
        {
            data = Py.JsonLoads((Run ?? Machine.Run)(exe, ["status", "--json"], TimeSpan.FromSeconds(10))?.Stdout ?? "") as JsonObject;
        }
        catch (JsonException)
        {
            return result;
        }
        if (data?["Peer"] is not JsonObject peers) return result;
        foreach (var kv in peers)
        {
            if (kv.Value is not JsonObject p || !Py.Truthy(p["Online"])) continue;
            string ip = (p["TailscaleIPs"] as JsonArray)?.Select(Py.Str).FirstOrDefault(x => x.Contains('.')) ?? "";
            if (ip.Length == 0) continue;
            string dns = (Py.AsString(p["DNSName"]) ?? "").TrimEnd('.');
            result.Add((dns.Length > 0 ? dns.Split('.')[0] : Py.AsString(p["HostName"]) ?? ip, ip));
        }
        return result;
    }

    /// <summary>This computer first, then up to 32 online Tailscale peers in parallel; the first that answers as a
    /// library, or null when nothing did.</summary>
    public async Task<FoundLibrary?> FindAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient(Handler ?? new HttpClientHandler(), disposeHandler: Handler is null) { Timeout = ProbeTimeout };
        foreach (int port in new[] { Port }.Concat(ExtraPorts).Distinct())
        {
            var (ok, name) = await ProbeAsync(http, $"http://127.0.0.1:{port}");
            if (ok) return new FoundLibrary($"http://127.0.0.1:{port}", name, Local: true);
        }
        var peers = Peers();
        FoundLibrary? found = null;
        var gate = new Lock();
        await Parallel.ForEachAsync(peers.Take(32), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 16 }, async (peer, token) =>
        {
            string url = $"http://{peer.Ip}:{Port}";
            var (ok, name) = await ProbeAsync(http, url);
            if (!ok) return;
            lock (gate) found ??= new FoundLibrary(url, name.Length > 0 ? name : peer.Name, Local: false);
        });
        return found;
    }
}
