using System.Net;
using System.Text;

namespace StudyStash.App.Tests;

/// <summary>One request a <see cref="FakeLibrary"/> saw: its method, path (no query), the query string (with the
/// leading "?", or "" for none), the body it was sent (if any), and its Authorization header (if any).</summary>
public sealed record Sent(string Method, string Path, string Query, string? Body, string? Authorization);

/// <summary>
/// Stands in for the library's Canvas API: a route is method+path (the query travels along on every
/// <see cref="Sent"/> for a test to check, but doesn't choose the answer). Answers with a fixture's JSON, an inline
/// JSON string, a status code (with an optional JSON body, e.g. a 400's <c>{"detail": "…"}</c>), or raw bytes.
/// Anything with no route answers 404, same as an older library missing the call. Keeps every request it saw.
/// </summary>
public sealed class FakeLibrary : HttpMessageHandler
{
    public List<Sent> Requests { get; } = [];

    readonly Dictionary<string, Func<HttpResponseMessage>> routes = [];

    public HttpClient Client() => new(this);

    static string Key(HttpMethod method, string path) => $"{method.Method} {path}";

    /// <summary>Answers 200 with a fixture's JSON, or inline JSON when <paramref name="jsonOrFixture"/> already
    /// starts with <c>{</c> or <c>[</c>.</summary>
    public FakeLibrary Json(HttpMethod method, string path, string jsonOrFixture)
    {
        string body = jsonOrFixture.TrimStart() is [ '{' or '[', ..] ? jsonOrFixture : CanvasFixtures.Text(jsonOrFixture);
        routes[Key(method, path)] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return this;
    }

    /// <summary>Answers with a status and, optionally, a JSON body.</summary>
    public FakeLibrary Status(HttpMethod method, string path, HttpStatusCode status, string body = "")
    {
        routes[Key(method, path)] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return this;
    }

    /// <summary>Answers 200 with raw bytes (a file download).</summary>
    public FakeLibrary Bytes(HttpMethod method, string path, byte[] bytes)
    {
        routes[Key(method, path)] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        string path = request.RequestUri!.AbsolutePath;
        var sent = new Sent(request.Method.Method, path, request.RequestUri.Query, body, request.Headers.Authorization?.ToString());
        lock (Requests) Requests.Add(sent);
        return routes.TryGetValue(Key(request.Method, path), out var reply) ? reply() : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
    }
}
