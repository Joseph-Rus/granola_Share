using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core;

/// <summary>Ollama's HTTP API: chat, model details, the installed models, and downloads.</summary>
public static class Ollama
{
    // Best first. MoE "a3b" models are fast (3B active) with big-model judgment; 64 GB Macs run the 35B fine.
    public static readonly string[] ModelPreference = ["qwen3.6:35b", "qwen3.6", "qwen3.8", "qwen3:30b", "gemma4", "qwen3", "gemma3", "llama3"];
    static readonly string[] NotForText = ["embed", "whisper", "clip", "rerank"];

    /// <summary>One connection pool for every call. Each call sets its own time limit.</summary>
    public static HttpClient Http { get; } = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    static string Url(string host, string path) => host.TrimEnd('/') + path;

    /// <summary>POST JSON, read JSON back. A slow answer fails with a sentence a person can act on.</summary>
    public static async Task<JsonNode?> PostAsync(string host, string path, JsonNode body, TimeSpan timeout,
        HttpClient? http = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var r = await (http ?? Http).PostAsync(Url(host, path), content, cts.Token);
            string text = await r.Content.ReadAsStringAsync(cts.Token);
            if (!r.IsSuccessStatusCode) throw new HttpRequestException(Refusal(r.StatusCode, text), null, r.StatusCode);
            return JsonNode.Parse(text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Ollama did not answer within {Seconds(timeout)}");
        }
    }

    static string Seconds(TimeSpan t) => t.TotalSeconds >= 120 ? $"{t.TotalMinutes:0} minutes" : $"{t.TotalSeconds:0} seconds";

    /// <summary>"Ollama answered 404: model 'x' not found", using Ollama's own words when it sent some.</summary>
    static string Refusal(HttpStatusCode status, string body)
    {
        string why = "";
        try
        {
            why = Py.AsString(JsonNode.Parse(body)?["error"]) ?? "";
        }
        catch (JsonException)
        {
        }
        if (why.Length == 0) why = Py.Head(Py.Strip(body), 300);
        return $"Ollama answered {(int)status}" + (why.Length > 0 ? ": " + why : "");
    }

    /// <summary>Installed chat models, biggest first, or null when Ollama is not reachable.</summary>
    public static async Task<List<(string Name, double SizeGb)>?> ListModelsAsync(string host, HttpClient? http = null)
    {
        JsonNode? data;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var r = await (http ?? Http).GetAsync(Url(host, "/api/tags"), cts.Token);
            r.EnsureSuccessStatusCode();
            data = JsonNode.Parse(await r.Content.ReadAsStringAsync(cts.Token));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
        var models = new List<(string, double)>();
        foreach (var m in data?["models"] as JsonArray ?? new JsonArray())
        {
            string name = m is JsonObject o && o.TryGetPropertyValue("name", out var n) && Py.Truthy(n) ? Py.Str(n) : "";
            if (name.Length == 0 || NotForText.Any(bad => name.Contains(bad, StringComparison.OrdinalIgnoreCase))) continue;
            double size = m?["size"] is JsonValue s && s.GetValueKind() == JsonValueKind.Number ? s.GetValue<double>() : 0;
            models.Add((name, Math.Round(size / 1e9, 1, MidpointRounding.ToEven)));
        }
        return models.OrderByDescending(m => m.Item2).ToList();
    }

    /// <summary>"23.1 GB", or a note that an Ollama cloud model sends the text to Ollama's servers.</summary>
    public static string SizeLabel(double sizeGb) => sizeGb != 0 ? $"{Py.FloatRepr(sizeGb)} GB" : "cloud, runs off this computer";

    /// <summary>Ollama lists "llama3.2:latest" for a model pulled as "llama3.2".</summary>
    public static bool HasModel(IReadOnlyCollection<string> installed, string name) =>
        installed.Contains(name) || (!name.Contains(':') && installed.Contains(name + ":latest"));

    public static string PickDefaultModel(IEnumerable<string> installed, string fallback)
    {
        var list = installed.ToList();
        foreach (string pref in ModelPreference)
            foreach (string m in list)
                if (m.ToLowerInvariant().StartsWith(pref, StringComparison.Ordinal)) return m;
        return fallback;
    }

    /// <summary>A model this computer can run, for when nothing suitable is installed yet.</summary>
    public static string RecommendedModel(double? ramGb) => ramGb switch
    {
        null or >= 40 => "qwen3.6:35b-a3b", // ~24 GB
        >= 14 => "gemma4:e4b", // ~10 GB
        _ => "qwen3:1.7b", // ~1.4 GB
    };

    /// <summary>
    /// Download a model through Ollama's own API, so no `ollama` command is needed. progress(done, total) is
    /// called as it goes. Returns (ok, why it failed).
    /// </summary>
    public static async Task<(bool Ok, string Why)> PullAsync(string model, string host = "http://localhost:11434",
        Action<long, long>? progress = null, HttpClient? http = null, CancellationToken ct = default)
    {
        var layers = new Dictionary<string, (long Done, long Total)>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(host, "/api/pull"))
            {
                Content = new StringContent(new JsonObject { ["model"] = model }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var r = await (http ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(cts.Token));
            while (true)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(60)); // a minute with no news means the download is stuck
                string? line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (Py.Strip(line).Length == 0) continue;
                JsonObject? ev;
                try
                {
                    ev = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    continue;
                }
                if (ev is null) continue;
                if (Py.Truthy(ev["error"])) return (false, Py.Str(ev["error"]));
                if (Py.Truthy(ev["digest"]) && Py.Truthy(ev["total"]) && Number(ev["total"]) is long total)
                {
                    layers[Py.Str(ev["digest"])] = (Number(ev["completed"]) ?? 0, total);
                    progress?.Invoke(layers.Values.Sum(l => l.Done), layers.Values.Sum(l => l.Total));
                }
                if (Py.AsString(ev["status"]) == "success") return (true, "");
            }
            if (r.StatusCode != HttpStatusCode.OK) return (false, $"Ollama answered {(int)r.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "the download stalled for a minute");
        }
        catch (HttpRequestException e)
        {
            return (false, e.Message);
        }
        return (false, "the download stopped before it finished");
    }

    static long? Number(JsonNode? v) =>
        v is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? (long)n.GetValue<double>() : null;

    /// <summary>Load the model and have it write a few words, the way a lecture will: (seconds, "") or (null, why).</summary>
    public static async Task<(double? Seconds, string Why)> TryModelAsync(string host, string model, double timeoutSeconds = 600,
        HttpClient? http = null)
    {
        var watch = Stopwatch.StartNew();
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with the single word: ready" }),
            ["stream"] = false,
            ["think"] = false,
            ["options"] = new JsonObject { ["num_predict"] = 16 },
        };
        try
        {
            await PostAsync(host, "/api/chat", body, TimeSpan.FromSeconds(timeoutSeconds), http);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or JsonException)
        {
            return (null, e.Message);
        }
        return (watch.Elapsed.TotalSeconds, "");
    }
}
