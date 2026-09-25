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

    public static string Text(string name) =>
        new UTF8Encoding(false).GetString(File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Golden", name)));

    public static JsonArray Cases(string name) => (JsonArray)cases.Value[name]!;

    public static JsonNode Case(string name) => cases.Value[name]!;

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
