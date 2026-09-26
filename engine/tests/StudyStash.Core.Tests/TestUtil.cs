using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>A folder of its own for one test, removed afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "studystash-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string name] => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>What the Python engine wrote (engine/tests/golden.py), to compare against.</summary>
public static class Golden
{
    static readonly Lazy<JsonObject> cases = new(() => (JsonObject)JsonNode.Parse(Text("cases.json"))!);
    static readonly Lazy<JsonObject> library = new(() => (JsonObject)JsonNode.Parse(Text("library.json"))!);
    static readonly Lazy<JsonObject> pages = new(() => (JsonObject)JsonNode.Parse(Text("pages.json"))!);
    static readonly Lazy<JsonObject> platform = new(() => (JsonObject)JsonNode.Parse(Text("platform.json"))!);

    public static string Text(string name) =>
        new UTF8Encoding(false).GetString(File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Golden", name)));

    public static JsonArray Cases(string name) => (JsonArray)cases.Value[name]!;

    public static JsonNode Case(string name) => cases.Value[name]!;

    /// <summary>Stage 3's cases: the pieces the library's pages are made of.</summary>
    public static JsonNode? Library(string name) => library.Value[name];

    /// <summary>Whole pages the Python engine served: {"library": {path: {status, html, csp}}, "setup": {state: {draft, html}}}.</summary>
    public static JsonObject PageCases() => pages.Value;

    /// <summary>Stage 5: service files, the firewall rule, doctor's scenarios.</summary>
    public static JsonObject Platform() => platform.Value;

    /// <summary>Data compared as json.dumps writes it, so key order counts too.</summary>
    public static string Dump(JsonNode? node) => PyJson.Dumps(node);

    public static string S(this JsonNode? node) => node!.GetValue<string>();
}

/// <summary>Stands in for Ollama: answers every request with this function, and keeps what was sent.</summary>
public sealed class FakeOllama(Func<string, JsonObject, object> answer) : HttpMessageHandler
{
    public List<(string Path, JsonObject Body)> Sent { get; } = [];

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
        var body = (JsonObject)JsonNode.Parse(text)!;
        Sent.Add((request.RequestUri!.AbsolutePath, body));
        object reply = answer(request.RequestUri.AbsolutePath, body);
        var status = reply is (HttpStatusCode code, string _) ? code : HttpStatusCode.OK;
        string content = reply switch
        {
            (HttpStatusCode, string s) => s,
            string s => s,
            JsonNode n => n.ToJsonString(),
            _ => throw new ArgumentException("answer with a string or JSON"),
        };
        return new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Stands in for a download server: bytes for each URL, a 404 for anything else. Keeps what was asked for.</summary>
public sealed class FakeDownloads(Dictionary<string, byte[]> files) : HttpMessageHandler
{
    public List<string> Asked { get; } = [];

    public HttpClient Client() => new(this);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string url = request.RequestUri!.ToString();
        Asked.Add(url);
        return Task.FromResult(files.TryGetValue(url, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
    }
}

/// <summary>A command runner that answers from a function and keeps every command it was given.</summary>
public sealed class FakeRunner(Func<string, IReadOnlyList<string>, ProcResult?>? answer = null)
{
    public List<List<string>> Calls { get; } = [];

    public ProcResult? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        lock (Calls) Calls.Add([exe, .. args]);
        return answer is null ? new ProcResult(0, "") : answer(exe, args);
    }
}
