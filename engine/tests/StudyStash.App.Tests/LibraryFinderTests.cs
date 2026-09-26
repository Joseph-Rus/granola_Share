using System.Net;
using StudyStash.App.Services;
using StudyStash.Core;

namespace StudyStash.App.Tests;

/// <summary>A fake network: /api/health answers by IP, made fresh each time it's asked.</summary>
sealed class FakeLibraries(Dictionary<string, Func<HttpResponseMessage>> byHost) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(byHost.TryGetValue(request.RequestUri!.Host, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
}

/// <summary>Looking for a library with no password: this computer's own port, then Tailscale peers, read-only.</summary>
public sealed class LibraryFinderTests
{
    static HttpResponseMessage Health(HttpStatusCode status, string? body = null, bool ours = false)
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body ?? "{}") };
        if (ours) r.Headers.Add("X-Study-Stash", "1");
        return r;
    }

    static ProcResult ThreePeers() => new(0, """
        {
          "Peer": {
            "a": { "HostName": "mac-mini", "DNSName": "mac-mini.tailnet-xyz.ts.net.", "TailscaleIPs": ["100.1.1.1"], "Online": true },
            "b": { "HostName": "phone", "DNSName": "phone.tailnet-xyz.ts.net.", "TailscaleIPs": ["100.1.1.2"], "Online": false },
            "c": { "HostName": "pc", "DNSName": "pc.tailnet-xyz.ts.net.", "TailscaleIPs": ["100.1.1.3"], "Online": true }
          }
        }
        """);

    [Fact]
    public async Task This_computer_answers_first_without_asking_Tailscale()
    {
        var handler = new FakeLibraries(new() { ["127.0.0.1"] = () => Health(HttpStatusCode.OK, """{"pool_name":"Ada's library"}""") });
        bool asked = false;
        var finder = new LibraryFinder { Handler = handler, Run = (_, _, _) => { asked = true; return ThreePeers(); } };

        var found = await finder.FindAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.True(found!.Local);
        Assert.Equal("http://127.0.0.1:8787", found.Url);
        Assert.False(asked);
    }

    [Fact]
    public async Task Finds_the_online_peer_that_answers_as_a_library_and_names_it()
    {
        var handler = new FakeLibraries(new()
        {
            ["127.0.0.1"] = () => Health(HttpStatusCode.ServiceUnavailable),
            ["100.1.1.1"] = () => Health(HttpStatusCode.Unauthorized, """{"detail":"wrong password"}"""),
            ["100.1.1.3"] = () => Health(HttpStatusCode.NotFound),
        });
        var finder = new LibraryFinder { Handler = handler, Run = (_, _, _) => ThreePeers() };

        var found = await finder.FindAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.False(found!.Local);
        Assert.Equal("http://100.1.1.1:8787", found.Url);
        Assert.Equal("mac-mini", found.Name);
    }

    [Fact]
    public async Task The_offline_peer_is_never_asked()
    {
        bool askedPhone = false;
        var handler = new FakeLibraries(new()
        {
            ["127.0.0.1"] = () => Health(HttpStatusCode.ServiceUnavailable),
            ["100.1.1.2"] = () => { askedPhone = true; return Health(HttpStatusCode.OK, """{"pool_name":"x"}"""); },
        });
        var finder = new LibraryFinder { Handler = handler, Run = (_, _, _) => ThreePeers() };

        await finder.FindAsync(TestContext.Current.CancellationToken);

        Assert.False(askedPhone);
    }

    [Fact]
    public async Task Nothing_answering_is_null()
    {
        var handler = new FakeLibraries(new());
        var finder = new LibraryFinder { Handler = handler, Run = (_, _, _) => new ProcResult(0, "{}") };

        Assert.Null(await finder.FindAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_header_alone_is_enough_even_with_no_pool_name()
    {
        var handler = new FakeLibraries(new() { ["127.0.0.1"] = () => Health(HttpStatusCode.OK, "{}", ours: true) });
        var finder = new LibraryFinder { Handler = handler, Run = (_, _, _) => new ProcResult(0, "{}") };

        var found = await finder.FindAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.True(found!.Local);
    }
}
