using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using StudyStash.Core.Canvas;

namespace StudyStash.Core.Tests;

/// <summary>
/// The Chrome extension's service worker (extension/background.js, the copy the engine carries), run in Jint with
/// Chrome and the network stood in for: what it hands the library for each kind of Canvas answer, and how it talks to
/// the library.
/// </summary>
public class ExtensionScriptTests
{
    const string Canvas = "https://canvas.test";
    const string Library = "http://127.0.0.1:8787";
    const string Unauthorized = """{"status":"unauthorized","errors":[{"message":"user not authorized to perform that action"}]}""";
    const string Unauthenticated = """{"status":"unauthenticated","errors":[{"message":"user authorization required"}]}""";

    /// <summary>One answer from the fake network: what fetch() resolves to, read by the script through a small adapter.</summary>
    public sealed class FakeResponse
    {
        public int Status { get; init; } = 200;
        /// <summary>Where the answer came from after redirects; the address asked for when empty.</summary>
        public string Url { get; set; } = "";
        public Dictionary<string, string> Headers { get; init; } = [];
        public string Body { get; init; } = "";
        /// <summary>fetch() itself fails, the way Chrome's does ("TypeError: Failed to fetch").</summary>
        public bool Throws { get; init; }
        /// <summary>How many times the script read the body as bytes (arrayBuffer()).</summary>
        public int BytesRead { get; private set; }

        public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());
        public string Text() => Body;

        /// <summary>The body as one char per byte, for the adapter's arrayBuffer().</summary>
        public string Latin1()
        {
            BytesRead++;
            return Body;
        }
    }

    /// <summary>background.js in a Jint engine, with importScripts, chrome.*, fetch, btoa, URL and setTimeout stood in for.</summary>
    public sealed class Worker
    {
        readonly Jint.Engine js;
        readonly Dictionary<string, Func<string, FakeResponse>> routes = [];

        /// <summary>Every fetch the script made: address, method and body.</summary>
        public List<(string Url, string Method, string Body)> Fetched { get; } = [];

        /// <param name="running">The version Chrome is running.</param>
        /// <param name="onDisk">The version in the extension's folder (what chrome.runtime.getURL('manifest.json') reads).</param>
        /// <param name="config">config.js's STUDY_STASH; Prepare's by default.</param>
        public Worker(string running = "1.3", string? onDisk = null, string? config = null)
        {
            config ??= $$"""{"app":"{{Library}}","key":"k3y","canvas":"{{Canvas}}","files":["*.inscloudgate.net"],"protocol":2}""";
            Route("chrome-extension://study-stash/manifest.json", _ => new FakeResponse { Body = $$"""{"version":"{{onDisk ?? running}}"}""" });
            js = new Jint.Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(20)));
            js.SetValue("__net", this);
            js.SetValue("btoa", new Func<string, string>(s => Convert.ToBase64String(Encoding.Latin1.GetBytes(s))));
            js.Execute($$$"""
                var __config = {{{config}}};
                var __reloads = 0;
                function importScripts(name) { if (name === 'config.js') globalThis.STUDY_STASH = __config; }
                const listeners = {addListener() {}};
                var chrome = {
                  runtime: {
                    getManifest: () => ({version: '{{{running}}}'}),
                    getURL: p => 'chrome-extension://study-stash/' + p,
                    reload: () => { __reloads++; },
                    onInstalled: listeners, onStartup: listeners, onMessage: listeners,
                  },
                  alarms: {create() {}, onAlarm: listeners},
                };
                function setTimeout(f) { f(); return 0; }
                function URL(u) {
                  const m = /^([a-z][a-z0-9+.-]*:)\/\/(?:([^@\/?#]*)@)?([^\/?#:]*)(?::(\d*))?([^?#]*)/i.exec(u);
                  if (!m) throw new TypeError('Invalid URL: ' + u);
                  const who = (m[2] || '').split(':');
                  this.protocol = m[1].toLowerCase(); this.username = m[2] ? who[0] : ''; this.password = who[1] || '';
                  this.hostname = m[3].toLowerCase(); this.port = m[4] || ''; this.pathname = m[5] || '/';
                }
                async function fetch(url, opts) {
                  const r = __net.Fetch(url, (opts && opts.method) || 'GET', (opts && opts.body) || '');
                  if (r.Throws) throw new TypeError('Failed to fetch');
                  return {
                    status: r.Status, ok: r.Status >= 200 && r.Status < 300, url: r.Url,
                    headers: {get: n => r.Header(n)},
                    text: async () => r.Text(),
                    json: async () => JSON.parse(r.Text()),
                    arrayBuffer: async () => {
                      const s = r.Latin1(), b = new Uint8Array(s.length);
                      for (let i = 0; i < s.length; i++) b[i] = s.charCodeAt(i);
                      return b.buffer;
                    },
                  };
                }
                """);
            using var script = typeof(Extension).Assembly.GetManifestResourceStream("extension/background.js")!;
            js.Execute(new StreamReader(script).ReadToEnd());
        }

        /// <summary>Answer an address (exactly, or without its query) like this.</summary>
        public Worker Route(string url, Func<string, FakeResponse> answer)
        {
            routes[url] = answer;
            return this;
        }

        public Worker Route(string url, FakeResponse answer) => Route(url, _ => answer);

        /// <summary>Called by the script's fetch().</summary>
        public FakeResponse Fetch(string url, string method, string body)
        {
            Fetched.Add((url, method, body));
            var answer = routes.TryGetValue(url, out var f) || routes.TryGetValue(url.Split('?')[0], out f)
                ? f(body) : new FakeResponse { Status = 404, Body = """{"errors":[{"message":"The specified resource does not exist."}]}""" };
            if (answer.Url.Length == 0) answer.Url = url;
            return answer;
        }

        public JsValue Eval(string code) => js.Evaluate(code).UnwrapIfPromise(TimeSpan.FromSeconds(20));

        /// <summary>What run() hands the library for this job, as JSON.</summary>
        public JsonObject Run(string url, string kind = "json")
        {
            js.SetValue("__out", Eval($"run({{id: '7', url: {JsonSerializer.Serialize(url)}, kind: '{kind}'}})"));
            return JsonNode.Parse(js.Evaluate("JSON.stringify(__out)").AsString())!.AsObject();
        }

        public bool Allowed(string url) => Eval($"allowed({JsonSerializer.Serialize(url)})").AsBoolean();

        public int Reloads => (int)js.Evaluate("__reloads").AsNumber();

        /// <summary>The library's side of what the script sent: the work it asked for and the results it posted.</summary>
        public List<string> Asks => Fetched.Where(f => f.Url.StartsWith(Library + "/api/v2/canvas/work", StringComparison.Ordinal)).Select(f => f.Url).ToList();

        public List<JsonArray> Posts => Fetched.Where(f => f.Url == Library + "/api/v2/canvas/results")
            .Select(f => JsonNode.Parse(f.Body)!["results"]!.AsArray()).ToList();
    }

    static FakeResponse Json(string body, int status = 200, params (string Name, string Value)[] headers) => new()
    {
        Status = status, Body = body,
        Headers = new[] { ("content-type", "application/json; charset=utf-8") }.Concat(headers).ToDictionary(h => h.Item1, h => h.Item2),
    };

    static string S(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    [Fact]
    public void Refuses_anything_but_canvas_and_its_file_store()
    {
        var w = new Worker();
        foreach (string url in new[] { "https://evil.test/api/v1/courses", "https://canvas.test.evil.test/api/v1/courses", "http://files.inscloudgate.net/1",
                     "https://files.inscloudgate.net.evil.test/1", "https://files.inscloudgate.net@evil.test/1", "https://files.inscloudgate.net:8443/1" })
            Assert.Equal("refused: not a Canvas URL", S(w.Run(url, "bytes"), "error"));
        Assert.Empty(w.Fetched);
        Assert.True(w.Allowed(Canvas + "/api/v1/courses"));
        Assert.True(w.Allowed("https://cluster1.inscloudgate.net/files/9?sig=abc"));

        // A config.js from before 1.3 has no list of file hosts: the old rule still lets the file store through.
        var old = new Worker(config: $$"""{"app":"{{Library}}","key":"k3y","canvas":"{{Canvas}}"}""");
        Assert.True(old.Allowed("https://cluster1.inscloudgate.net/files/9"));
        Assert.False(old.Allowed("https://evil.test/files/9"));
    }

    [Fact]
    public void Canvas_s_json_guard_is_stripped_and_paging_and_type_passed_on()
    {
        const string next = "<https://canvas.test/api/v1/courses/4201/assignments?page=2&per_page=100>; rel=\"next\"";
        var w = new Worker().Route(Canvas + "/api/v1/courses/4201/assignments", Json("while(1);[{\"id\":9001}]", 200, ("link", next)));
        var r = w.Run(Canvas + "/api/v1/courses/4201/assignments?per_page=100");
        Assert.Equal("[{\"id\":9001}]", S(r, "text"));
        Assert.Equal((200, next, "application/json; charset=utf-8"), (r["status"]!.GetValue<int>(), S(r, "link"), S(r, "type")));
        Assert.Null(r["signed_out"]);
        Assert.Equal(("GET", ""), (w.Fetched[0].Method, w.Fetched[0].Body));
    }

    [Fact]
    public void A_bounce_to_canvas_s_sign_in_page_says_signed_out()
    {
        var w = new Worker().Route(Canvas + "/api/v1/courses/4201/modules",
            new FakeResponse { Url = Canvas + "/login/canvas", Body = "<!DOCTYPE html><title>Log In to Canvas</title>", Headers = { ["content-type"] = "text/html" } });
        var r = w.Run(Canvas + "/api/v1/courses/4201/modules");
        Assert.True(r["signed_out"]!.GetValue<bool>());
        Assert.Equal(401, r["status"]!.GetValue<int>()); // what a library from before signed_out understands
        Assert.Equal(CanvasAnswer.SignedOut, Crawl.Classify(CanvasResult.From(r)));
    }

    [Fact]
    public void A_web_page_for_a_json_job_says_signed_out()
    {
        // The school's single sign-on page, on its own host: no /login in the address, but a page where JSON should be.
        var w = new Worker().Route(Canvas + "/api/v1/courses/4201/assignments",
            new FakeResponse { Url = "https://sso.school.test/idp/profile", Body = "\n  <html><body>Sign in with your school account</body></html>", Headers = { ["content-type"] = "text/html" } });
        var r = w.Run(Canvas + "/api/v1/courses/4201/assignments");
        Assert.True(r["signed_out"]!.GetValue<bool>());
        Assert.Equal(401, r["status"]!.GetValue<int>());
    }

    [Fact]
    public void A_401_for_a_tab_the_student_can_t_see_is_not_a_sign_out()
    {
        var w = new Worker()
            .Route(Canvas + "/api/v1/courses/4201/files", Json(Unauthorized, 401))
            .Route(Canvas + "/api/v1/courses/4201/pages", Json(Unauthenticated, 401));
        var hidden = w.Run(Canvas + "/api/v1/courses/4201/files");
        Assert.Equal(401, hidden["status"]!.GetValue<int>());
        Assert.Null(hidden["signed_out"]);
        Assert.Equal(Unauthorized, S(hidden, "text"));
        Assert.Equal(CanvasAnswer.Hidden, Crawl.Classify(CanvasResult.From(hidden)));

        var signedOut = w.Run(Canvas + "/api/v1/courses/4201/pages");
        Assert.True(signedOut["signed_out"]!.GetValue<bool>());
        Assert.Equal(CanvasAnswer.SignedOut, Crawl.Classify(CanvasResult.From(signedOut)));
    }

    [Fact]
    public void Canvas_s_rate_limit_is_passed_on()
    {
        var w = new Worker()
            .Route(Canvas + "/api/v1/courses/4201/modules", Json("[]", 200, ("x-rate-limit-remaining", "12.5")))
            .Route(Canvas + "/api/v1/courses/4201/assignments", new FakeResponse
            {
                Status = 403, Body = "403 Forbidden (Rate Limit Exceeded)\n",
                Headers = { ["content-type"] = "text/plain", ["x-rate-limit-remaining"] = "0.0", ["retry-after"] = "30" },
            });
        var calm = w.Run(Canvas + "/api/v1/courses/4201/modules");
        Assert.Equal("12.5", S(calm, "rate"));
        Assert.Null(calm["retry_after"]);
        Assert.Equal(12.5, CanvasResult.From(calm).Rate);

        var limited = CanvasResult.From(w.Run(Canvas + "/api/v1/courses/4201/assignments"));
        Assert.Equal((0.0, 30.0), (limited.Rate, limited.RetryAfter));
        Assert.Equal(CanvasAnswer.RateLimited, Crawl.Classify(limited));
    }

    [Fact]
    public void A_file_canvas_refused_is_never_handed_over_as_a_file()
    {
        var missing = new FakeResponse { Status = 404, Body = """{"errors":[{"message":"The specified resource does not exist."}]}""", Headers = { ["content-type"] = "application/json" } };
        var locked = new FakeResponse { Status = 401, Body = Unauthorized, Headers = { ["content-type"] = "application/json" } };
        var w = new Worker().Route(Canvas + "/files/8801/download", missing).Route(Canvas + "/files/8802/download", locked);

        var r = w.Run(Canvas + "/files/8801/download", "bytes");
        Assert.Null(r["b64"]);
        Assert.Equal(404, r["status"]!.GetValue<int>());
        Assert.Contains("does not exist", S(r, "text"));
        Assert.Equal(0, missing.BytesRead);
        Assert.Equal(CanvasAnswer.Hidden, Crawl.Classify(CanvasResult.From(r)));

        // Canvas's words for a file it hides say "unauthorized": not a sign-out, not a file.
        r = w.Run(Canvas + "/files/8802/download", "bytes");
        Assert.Null(r["b64"]);
        Assert.Null(r["signed_out"]);
        Assert.Equal(CanvasAnswer.Hidden, Crawl.Classify(CanvasResult.From(r)));
    }

    [Fact]
    public void A_file_over_40_MB_is_never_read()
    {
        var big = new FakeResponse { Body = "%PDF-1.4 lecture recording slides", Headers = { ["content-type"] = "application/pdf", ["content-length"] = "52428800" } };
        var small = new FakeResponse { Body = "%PDF-1.4 recursion slides", Headers = { ["content-type"] = "application/pdf", ["content-length"] = "25" } };
        var w = new Worker().Route(Canvas + "/files/555/download", big).Route(Canvas + "/files/556/download", small);

        var r = w.Run(Canvas + "/files/555/download?download_frd=1", "bytes");
        Assert.Equal("too big", S(r, "error"));
        Assert.Null(r["b64"]);
        Assert.Equal(0, big.BytesRead);

        r = w.Run(Canvas + "/files/556/download?download_frd=1", "bytes");
        Assert.Equal("%PDF-1.4 recursion slides", Encoding.Latin1.GetString(Convert.FromBase64String(S(r, "b64"))));
        Assert.Equal(1, small.BytesRead);
    }

    [Fact]
    public void A_download_the_service_worker_can_t_follow_uses_canvas_s_signed_link()
    {
        const string signed = "https://cluster1.inscloudgate.net/files/9/notes.pdf?sig=abc";
        var w = new Worker()
            .Route(Canvas + "/files/9/download", new FakeResponse { Throws = true })
            .Route(Canvas + "/api/v1/files/9/public_url", Json($$"""while(1);{"public_url":"{{signed}}"}"""))
            .Route(signed, new FakeResponse { Body = "%PDF-1.4 week 4 notes", Headers = { ["content-type"] = "application/pdf" } })
            .Route(Canvas + "/files/10/download", new FakeResponse { Throws = true })
            .Route(Canvas + "/api/v1/files/10/public_url", Json("""{"public_url":"https://evil.test/10"}"""));

        var r = w.Run(Canvas + "/files/9/download?download_frd=1", "bytes");
        Assert.Equal("%PDF-1.4 week 4 notes", Encoding.Latin1.GetString(Convert.FromBase64String(S(r, "b64"))));
        Assert.Equal((200, signed), (r["status"]!.GetValue<int>(), S(r, "final")));
        Assert.Equal([Canvas + "/files/9/download?download_frd=1", Canvas + "/api/v1/files/9/public_url", signed], w.Fetched.Select(f => f.Url));

        // A signed link somewhere else is never followed.
        r = w.Run(Canvas + "/files/10/download", "bytes");
        Assert.Contains("no public link", S(r, "error"));
        Assert.DoesNotContain(w.Fetched, f => f.Url.StartsWith("https://evil.test", StringComparison.Ordinal));
        // Only files get the fallback: a JSON read that fails is an error for the library to retry.
        Assert.Equal("TypeError: Failed to fetch", S(new Worker().Route(Canvas + "/api/v1/courses", new FakeResponse { Throws = true }).Run(Canvas + "/api/v1/courses"), "error"));
    }

    /// <summary>A library that hands out one batch of jobs, then nothing; results answer with <paramref name="results"/>.</summary>
    static Worker WithLibrary(Worker w, JsonArray jobs, int results = 200)
    {
        int asked = 0;
        return w.Route(Library + "/api/v2/canvas/work", _ => Json(new JsonObject
        {
            ["jobs"] = asked++ == 0 ? jobs.DeepClone() : new JsonArray(), ["hot"] = false, ["ext"] = Extension.Version(), ["p"] = Extension.Protocol,
        }.ToJsonString()))
            .Route(Library + "/api/v2/canvas/results", _ => Json("""{"ok":true}""", results));
    }

    static JsonObject Job(string id, string path, string kind) => new() { ["id"] = id, ["url"] = Canvas + path, ["kind"] = kind };

    [Fact]
    public void It_says_its_version_and_protocol_and_posts_each_file_on_its_own()
    {
        var w = WithLibrary(new Worker(), [Job("1", "/api/v1/courses/4201/modules", "json"), Job("2", "/files/555/download", "bytes"),
            Job("3", "/api/v1/courses/4201/discussion_topics", "json"), Job("4", "/files/8801/download", "bytes")])
            .Route(Canvas + "/api/v1/courses/4201/modules", Json("[]"))
            .Route(Canvas + "/api/v1/courses/4201/discussion_topics", Json("[]"))
            .Route(Canvas + "/files/555/download", new FakeResponse { Body = "%PDF-1.4 recursion slides" })
            .Route(Canvas + "/files/8801/download", new FakeResponse { Body = "%PDF-1.4 ps4 answers" });
        w.Eval("pump(true)");

        Assert.Equal([$"{Library}/api/v2/canvas/work?v=1.3&p={Extension.Protocol}&force=1", $"{Library}/api/v2/canvas/work?v=1.3&p={Extension.Protocol}"], w.Asks);
        var posts = w.Posts.Select(p => string.Join(",", p.Select(r => S(r!.AsObject(), "id")))).ToList();
        Assert.Equal(["1,3", "2", "4"], posts); // the answers together, then one file per post
        Assert.All(w.Fetched.Where(f => f.Url.StartsWith(Library, StringComparison.Ordinal)), f => Assert.DoesNotContain("k3y", f.Url));
        Assert.Equal(0, w.Reloads);
        Assert.Equal(Extension.Protocol, (int)w.Eval("PROTOCOL").AsNumber());
    }

    [Fact]
    public void Before_taking_work_it_reloads_into_a_newer_folder()
    {
        var w = WithLibrary(new Worker(running: "1.2", onDisk: "1.3"), [Job("1", "/api/v1/courses/4201/modules", "json")]);
        w.Eval("pump(false)");
        Assert.Equal(1, w.Reloads);
        Assert.Empty(w.Asks); // nothing taken that the reload would drop
        Assert.Equal(["chrome-extension://study-stash/manifest.json"], w.Fetched.Select(f => f.Url));
    }

    [Fact]
    public void A_post_the_library_refuses_stops_the_pump()
    {
        var w = WithLibrary(new Worker(), [Job("1", "/files/555/download", "bytes"), Job("2", "/files/556/download", "bytes")], results: 413)
            .Route(Canvas + "/files/555/download", new FakeResponse { Body = "%PDF-1.4 recursion slides" })
            .Route(Canvas + "/files/556/download", new FakeResponse { Body = "%PDF-1.4 more slides" });
        w.Eval("pump(false)"); // settles: nothing thrown out of the service worker
        Assert.Single(w.Posts); // the first file was refused: the second isn't sent, and no more work is asked for
        Assert.Single(w.Asks);
        Assert.False(w.Eval("running").AsBoolean()); // the next alarm can pump again
    }
}
