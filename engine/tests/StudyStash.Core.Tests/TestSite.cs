using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace StudyStash.Core.Tests;

/// <summary>A web app on an in-memory server, and a browser for it: it keeps cookies and doesn't follow redirects.</summary>
public sealed class TestSite : IAsyncDisposable
{
    static readonly Uri Base = new("http://localhost");

    readonly CookieContainer jar = new();

    TestSite(WebApplication app)
    {
        App = app;
        Client = new HttpClient(new Browser(jar) { InnerHandler = app.GetTestServer().CreateHandler() }) { BaseAddress = Base };
    }

    public WebApplication App { get; }
    public HttpClient Client { get; }

    public static async Task<TestSite> StartAsync(Func<WebApplicationBuilder, WebApplication> build)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = build(builder);
        await app.StartAsync();
        return new TestSite(app);
    }

    /// <summary>Another browser on the same site: no cookies.</summary>
    public HttpClient Stranger() => new(App.GetTestServer().CreateHandler()) { BaseAddress = Base };

    public void SetCookie(string name, string value) => jar.Add(Base, new Cookie(name, value));

    public void ClearCookies()
    {
        foreach (Cookie c in jar.GetAllCookies()) c.Expired = true;
    }

    public string? Cookie(string name) => jar.GetCookies(Base)[name]?.Value;

    public Task<HttpResponseMessage> Get(string path) => Client.GetAsync(path);

    public async Task<string> Text(string path) => await (await Client.GetAsync(path)).Content.ReadAsStringAsync();

    public Task<HttpResponseMessage> PostForm(string path, params (string Key, string Value)[] form) =>
        Client.PostAsync(path, new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Key, f.Value))));

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }

    sealed class Browser(CookieContainer cookies) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string header = cookies.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", header);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (string c in set) cookies.SetCookies(request.RequestUri!, c);
            return response;
        }
    }
}
