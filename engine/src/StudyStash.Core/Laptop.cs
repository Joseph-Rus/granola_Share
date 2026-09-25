using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace StudyStash.Core;

/// <summary>What one look at Granola did.</summary>
public sealed class ClientReport
{
    public int Listed { get; set; }
    public int Considered { get; set; }
    public List<(string Title, string Class)> Shared { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<string> Pending { get; } = [];
    /// <summary>Sent again once a copied transcript arrived.</summary>
    public List<string> Reshared { get; } = [];
    /// <summary>What the library finished filing.</summary>
    public List<(string Title, string Class)> Filed { get; } = [];
    public List<string> Errors { get; } = [];
}

/// <summary>The library answered with an error: its status, in httpx's words.</summary>
public sealed class LibraryRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The laptop's side of the library's API: send a lecture, ask whether it's filed.</summary>
public static class LibraryHttp
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    /// <summary>httpx's raise_for_status message, so what's saved and shown reads as it did.</summary>
    static LibraryRefusedException Refused(HttpResponseMessage r, string url)
    {
        int code = (int)r.StatusCode;
        string kind = (code / 100) switch { 1 => "Informational response", 3 => "Redirect response", 4 => "Client error", _ => "Server error" };
        return new LibraryRefusedException(code, $"{kind} '{code} {r.ReasonPhrase}' for url '{url}'\n"
            + $"For more information check: https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/{code}");
    }

    public static async Task<JsonObject> PostAsync(string url, string json, string poolKey, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + poolKey);
        using var r = await (http ?? Http).SendAsync(request);
        if (!r.IsSuccessStatusCode) throw Refused(r, url);
        return Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject ?? [];
    }

    /// <summary>Null when the library doesn't know the note (or is too old to answer).</summary>
    public static async Task<JsonObject?> GetAsync(string url, string poolKey, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + poolKey);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var r = await (http ?? Http).SendAsync(request, cts.Token);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw Refused(r, url);
        return Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject ?? [];
    }
}

/// <summary>
/// Everything the laptop's watcher does to this computer, so tests can answer instead: popups, notifications, and
/// what transcript copying can do. Popups are off unless <see cref="ThisComputer"/> turns them on, so a test that
/// forgets one fails instead of showing a real dialog and waiting on it.
/// </summary>
public sealed class LaptopHost
{
    static Exception Off(string what) => new InvalidOperationException($"{what} is off for this laptop.");

    public string System { get; init; } = Machine.Platform;
    /// <summary>(title, text, yes, no, timeout) → true, false, or null when nobody answered.</summary>
    public Func<string, string, string, string, int, bool?> Ask { get; init; } = (_, _, _, _, _) => throw Off("Asking with a popup");
    /// <summary>(title, text, buttons, preferred, timeout) → the button, or null when nobody answered.</summary>
    public Func<string, string, IReadOnlyList<string>, string, int, string?> Choose { get; init; } = (_, _, _, _, _) => throw Off("Asking with a popup");
    public Action<string, string> Notify { get; init; } = (_, _) => throw Off("Showing a notification");
    public Action<string> OpenUrl { get; init; } = _ => throw Off("Opening a browser");
    /// <summary>(url, JSON body, library password) → the library's answer.</summary>
    public Func<string, string, string, Task<JsonObject>> Post { get; init; } = (url, body, key) => LibraryHttp.PostAsync(url, body, key);
    /// <summary>(url, library password) → the answer, or null when the library doesn't know it.</summary>
    public Func<string, string, Task<JsonObject?>> Get { get; init; } = (url, key) => LibraryHttp.GetAsync(url, key);
    public Func<string, string, Task<JsonObject>> CheckServer { get; init; } = (url, key) => LibraryApi.CheckServerAsync(url, key);
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
    public Runner Run { get; init; } = (_, _, _) => throw Off("Running commands");
    // What the laptop's page looks at, and does: only looking is on by default.
    public Func<LaptopInfo> Readiness { get; init; } = () => Ready.LaptopChecks();
    public Func<string?> TailscaleExe { get; init; } = HostInfo.TailscaleExe;
    public Func<Action<string>, Task<bool>> InstallTailscale { get; init; } = _ => throw Off("Installing Tailscale");
    public Func<bool> OpenTailscale { get; init; } = () => throw Off("Opening Tailscale");
    /// <summary>Sign in to Granola in the browser, saving the sign-in in the laptop's folder.</summary>
    public Func<ClientConfig, Action<string>, Task> SignIn { get; init; } = (_, _) => throw Off("Signing in to Granola");
    /// <summary>Reads your Granola account (a sign-in can be refreshed doing so).</summary>
    public Func<ClientConfig, ILectureSource> Granola { get; init; } = _ => throw Off("Reading Granola");
    /// <summary>Takes the Study Stash icon and the background service off this computer.</summary>
    public Action Remove { get; init; } = () => throw Off("Removing Study Stash");
    /// <summary>Starts the auto-update checks (off: nothing installs itself in a test).</summary>
    public Action<string, CancellationToken, Action<string>> StartUpdates { get; init; } = (_, _, _) => { };
    public Func<string> UserName { get; init; } = () => Environment.UserName;

    public static LaptopHost ThisComputer(Action? restart = null) => new()
    {
        Ask = (title, text, yes, no, timeout) => Dialogs.AskYesNo(title, text, yes, no, timeout),
        Choose = (title, text, buttons, preferred, timeout) => Dialogs.AskChoice(title, text, buttons, preferred, timeout),
        Notify = (title, text) => Dialogs.Notify(title, text),
        OpenUrl = url => Dialogs.OpenUrl(url),
        Run = Machine.Run,
        InstallTailscale = log => Ready.InstallTailscaleAsync(log, null, Machine.Platform, Machine.Run, Ready.Download, remote: false),
        OpenTailscale = () => Ready.OpenTailscale(Machine.Platform, Machine.Run),
        SignIn = (cc, log) => GranolaOAuth.For(cc).LoginAsync(openBrowser: true, log: log),
        Granola = cc => GranolaClient.For(cc.McpUrl, GranolaOAuth.For(cc)),
        Remove = () =>
        {
            Launcher.Uninstall(Machine.Platform, AppPlaces.Default);
            Autostart.Uninstall("client", ServicePlaces.Default, Machine.Run); // this stops this very process
        },
        StartUpdates = (home, stop, log) => Updates.StartAutoUpdate(home, () => Configs.LoadClient(home).AutoUpdate,
            _ => restart?.Invoke(), log, stop),
    };
}

/// <summary>
/// The laptop side (client.py): watch your Granola account and send finished lectures to your library. For each
/// finished note it either sends it automatically or asks "Send it?", then POSTs it to the library's /api/ingest, and
/// says so once the library has filed it. Its record of what it did (client_state.json) is the Python engine's, so a
/// laptop can switch engines without sending anything twice.
/// </summary>
public sealed class ShareClient
{
    public const int OverlapDays = 2;
    public const int FiledCheckHours = 24;
    /// <summary>While waiting on the library to file something.</summary>
    public const int QuickPollSeconds = 30;
    public const string CopyOff = "off", CopyNeedsPermission = "needs permission", CopyOn = "on";

    readonly ILectureSource granola;
    readonly LaptopHost host;
    readonly SemaphoreSlim wake = new(0, 1);

    public ClientConfig Cc { get; set; }
    /// <summary>Transcripts copied from the Granola app, when copying is on.</summary>
    public TranscriptStore? Copies { get; set; }
    public Action<string> Log { get; set; }
    /// <summary>Whether transcripts can be copied from the Granola app right now.</summary>
    public Func<string> CopyState { get; set; } = () => CopyOff;
    public Action AllowCopying { get; set; }
    /// <summary>Bring Granola forward, copy that lecture's transcript, come back.</summary>
    public Func<string, string> CaptureFor { get; set; } = _ => "";
    /// <summary>What happened when the recording that started then ended: "copied", "asking", "saved", "not now", or
    /// null when the laptop didn't see it end.</summary>
    public Func<DateTime?, string?> HandledSince { get; set; } = _ => null;
    /// <summary>The latest check's failure, shown on the Study Stash page.</summary>
    public string? LastError { get; set; }
    /// <summary>Why the library didn't take the last lecture, and its kind: "password", "unreachable", "other".</summary>
    public string? SendProblem { get; set; }
    public string? SendProblemKind { get; set; }
    public JsonObject State { get; private set; }

    public ShareClient(ClientConfig cc, ILectureSource granola, LaptopHost host, TranscriptStore? transcripts = null, Action<string>? log = null)
    {
        Cc = cc;
        this.granola = granola;
        this.host = host;
        Copies = transcripts;
        Log = log ?? Console.WriteLine;
        AllowCopying = () => host.OpenUrl(Dialogs.AccessibilitySettings);
        State = LoadState();
    }

    JsonObject Seen => State["seen"] as JsonObject ?? (JsonObject)(State["seen"] = new JsonObject());

    static string Iso(DateTimeOffset t, bool seconds)
    {
        string text = t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        long micro = t.Ticks / 10 % 1_000_000;
        if (!seconds && micro != 0) text += "." + micro.ToString("000000", CultureInfo.InvariantCulture);
        return text + t.ToString("zzz", CultureInfo.InvariantCulture);
    }

    static DateTimeOffset? When(JsonNode? iso) =>
        Py.AsString(iso) is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    // --- state ---------------------------------------------------------------------------------------------------

    JsonObject LoadState()
    {
        try
        {
            if (File.Exists(Cc.StatePath) && Py.JsonLoads(Py.ReadText(Cc.StatePath)) is JsonObject state) return state;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new JsonObject { ["seen"] = new JsonObject(), ["last_poll"] = null };
    }

    void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Cc.StatePath)!);
        Py.WriteText(Cc.StatePath, PyJson.Dumps(State, indent: 2));
    }

    void Mark(Meeting m, string decision, JsonObject? extra = null)
    {
        var entry = new JsonObject
        {
            ["decision"] = decision, ["title"] = m.Title, ["date"] = Py.Head(m.Date, 10), ["start"] = Py.Head(m.Date, 19),
            ["transcript_chars"] = Py.Strip(m.Transcript).Length, ["at"] = Iso(host.Clock(), seconds: true),
        };
        foreach (var (k, v) in extra ?? []) entry[k] = v?.DeepClone();
        Seen[m.Id] = entry;
        SaveState();
    }

    /// <summary>Check again soon (a transcript was just copied, or settings changed).</summary>
    public void Wake()
    {
        try
        {
            if (wake.CurrentCount == 0) wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    string CopiedTranscript(string title, string? day) => Copies?.Find(title, day) ?? "";

    DateOnly Since()
    {
        if (When(State["last_poll"]) is DateTimeOffset last) return DateOnly.FromDateTime(last.DateTime).AddDays(-OverlapDays);
        return DateOnly.FromDateTime(DateTime.Now).AddDays(-Cc.ShareLookbackDays);
    }

    public string Library => Cc.PoolName.Length > 0 ? Cc.PoolName : "your library";

    // --- decisions -------------------------------------------------------------------------------------------------

    bool? Decide(Meeting m)
    {
        if (Cc.Mode == "auto") return true;
        string text = $"Granola finished notes for:\n\n“{m.Title}”\n{Py.Head(m.Date, 10)}\n\nSend it to {Library}?";
        return host.Ask("Study Stash", text, "Send", "Skip", Cc.DialogTimeoutSeconds);
    }

    /// <summary>A finished lecture without its transcript: "save", "without", "skip", or null (no answer).</summary>
    string? AskSave(Meeting m, string copyState)
    {
        var lines = new List<string> { $"“{m.Title}” is finished.", "" };
        string go;
        if (copyState == CopyNeedsPermission)
        {
            lines.Add($"Save it to {Library}? To include its transcript, allow granola-share to read the "
                + "Granola window: click Allow, then turn on python3.12. The transcript follows once it's on.");
            go = "Allow";
        }
        else
        {
            lines.Add($"Save it to {Library} with its transcript? granola-share opens Granola, copies the "
                + "transcript, and brings you back.");
            go = "Save";
        }
        List<string> buttons = [.. Cc.Mode == "ask" ? ["Skip"] : Array.Empty<string>(), "Without transcript", go];
        string? answer = host.Choose("Study Stash", string.Join("\n", lines), buttons, go, Cc.DialogTimeoutSeconds);
        if (string.IsNullOrEmpty(answer)) return null;
        return answer == go ? "save" : answer == "Without transcript" ? "without" : answer == "Skip" ? "skip" : null;
    }

    Task<JsonObject> Push(Meeting m)
    {
        var sent = m.WithNotes(m.NotesMarkdown);
        sent.Owner = Cc.DisplayName.Length > 0 ? Cc.DisplayName : m.Owner;
        if (!Cc.IncludeTranscripts) sent.Transcript = "";
        return host.Post(Cc.ServerUrl.TrimEnd('/') + "/api/ingest", Granola.MeetingJson(sent), Cc.PoolKey);
    }

    async Task Share(Meeting m, ClientReport rep, string? message = null)
    {
        JsonObject res;
        try
        {
            res = await Push(m);
        }
        catch (Exception e) when (e is not OperationCanceledException || e is TaskCanceledException)
        {
            rep.Errors.Add($"{m.Id}: {e.Message}");
            rep.Pending.Add(m.Title);
            Mark(m, "pending", new JsonObject { ["error"] = e.Message, ["approved"] = true }); // retried without asking again
            Log($"[client] could not share '{m.Title}': {e.Message}");
            SendFailed(e);
            return;
        }
        SendProblem = SendProblemKind = null;
        string cls = Py.Truthy(res["class_name"]) ? Py.Str(res["class_name"]) : ""; // empty while the library is still sorting it
        rep.Shared.Add((m.Title, cls.Length > 0 ? cls : "being sorted"));
        Mark(m, "shared", cls.Length > 0 ? new JsonObject { ["class_name"] = cls, ["filed"] = false } : new JsonObject { ["filed"] = false });
        string withIt = Py.Strip(m.Transcript).Length > 0 ? " with its transcript" : "";
        Log($"[client] shared '{m.Title}'{withIt}" + (cls.Length > 0 ? $" → {cls}" : ""));
        if (message is not null) host.Notify("Study Stash", message); // otherwise the one notification is "it's filed"
    }

    /// <summary>Say why where it's seen: on the Study Stash page, plus one notification when only you can fix it (the
    /// library has a new password). Lectures wait here and are retried either way.</summary>
    void SendFailed(Exception e)
    {
        string kind, text;
        if (e is LibraryRefusedException { Status: 401 })
            (kind, text) = ("password", $"{Library} turned down this laptop's password, so lectures are waiting here.");
        else if (e is HttpRequestException or TaskCanceledException)
            (kind, text) = ("unreachable", $"Can't reach {Library} right now. Is its computer awake, with Tailscale "
                + "on? Lectures wait here and go as soon as it's back.");
        else
            (kind, text) = ("other", $"Couldn't send to {Library}: {e.Message}");
        if (kind == "password" && SendProblemKind != "password") host.Notify("Study Stash", $"{text} Open Study Stash to enter the new one.");
        (SendProblem, SendProblemKind) = (text, kind);
    }

    // --- one look ------------------------------------------------------------------------------------------------

    static string? Decision(JsonObject seen, string id) => Py.AsString((seen[id] as JsonObject)?["decision"]);

    public async Task<ClientReport> PollOnceAsync(DateOnly? since = null, CancellationToken ct = default)
    {
        var rep = new ClientReport();
        var seen = Seen;
        DateOnly from = since ?? Since();
        await using (var s = await granola.SessionAsync(ct))
        {
            var stubs = await granola.ListMeetingsAsync(s, since: from, ct: ct);
            rep.Listed = stubs.Count;
            var todo = stubs.Where(m => Decision(seen, m.Id) is not ("shared" or "skipped")).ToList();
            rep.Considered = todo.Count;
            Log($"[client] {stubs.Count} notes since {Transcripts.Day(from)}, {todo.Count} to consider");
            var full = new Dictionary<string, Meeting>();
            if (todo.Count > 0)
            {
                try
                {
                    foreach (var m in await granola.GetMeetingsAsync(s, todo.Select(m => m.Id).ToList(), ct)) full[m.Id] = m;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    rep.Errors.Add($"get_meetings: {e.Message}");
                    Log($"[client] get_meetings failed, using list data: {e.Message}");
                }
            }
            foreach (var stub in todo)
            {
                var m = full.GetValueOrDefault(stub.Id) ?? stub;
                if (m.NotesMarkdown.Length == 0) m.NotesMarkdown = stub.NotesMarkdown;
                if (Cc.IncludeTranscripts && m.Transcript.Length == 0)
                    m.Transcript = await granola.GetTranscriptAsync(s, m.Id, ct) is { Length: > 0 } t ? t : CopiedTranscript(m.Title, m.Date);
                await Handle(m, seen[m.Id] as JsonObject ?? [], rep);
            }
            if (Cc.IncludeTranscripts && Copies is not null) await ReshareWithCopiedTranscripts(s, rep, ct: ct);
        }
        await CheckFiled(rep);
        State["last_poll"] = Iso(host.Clock(), seconds: false);
        SaveState();
        return rep;
    }

    async Task Handle(Meeting m, JsonObject entry, ClientReport rep)
    {
        string copyState = Cc.IncludeTranscripts ? CopyState() : CopyOff;
        string? message = null;
        string? ended = HandledSince(Transcripts.IsoTime(m.Date));
        if (ended == "asking")
        {
            rep.Pending.Add(m.Title); // the "Save it?" popup from the end of the recording is still up
            return;
        }
        bool? decision;
        if (Py.Truthy(entry["approved"]))
        {
            decision = true; // they already said yes; the library just didn't take it yet
        }
        else if (Py.Strip(m.Transcript).Length == 0 && copyState != CopyOff && ended is null)
        {
            // Nothing copied when the recording ended (the laptop was off, or it was recorded elsewhere).
            string? choice = AskSave(m, copyState);
            if (choice == "save")
            {
                if (copyState == CopyNeedsPermission)
                {
                    AllowCopying();
                }
                else
                {
                    m.Transcript = CaptureFor(m.Title) ?? "";
                    if (m.Transcript.Length == 0)
                        message = $"Saved “{m.Title}” without its transcript. Open it in Granola any time and the transcript is added.";
                }
            }
            decision = choice switch { "save" or "without" => true, "skip" => false, _ => null };
        }
        else if (ended == "saved")
        {
            decision = true; // they already said Save when the recording ended
        }
        else
        {
            decision = Decide(m);
        }
        if (decision is null)
        {
            rep.Pending.Add(m.Title);
            Mark(m, "pending");
        }
        else if (decision == false)
        {
            rep.Skipped.Add(m.Title);
            Mark(m, "skipped");
            Log($"[client] skipped '{m.Title}'");
        }
        else
        {
            await Share(m, rep, message);
        }
    }

    /// <summary>Titles of recent lectures sent without a transcript: opening one in Granola adds it.</summary>
    public HashSet<string> MissingTranscripts(int days = 14)
    {
        var cutoff = host.Clock() - TimeSpan.FromDays(days);
        return Seen.Select(kv => kv.Value as JsonObject).OfType<JsonObject>()
            .Where(e => Py.AsString(e["decision"]) == "shared" && !Py.Truthy(e["transcript_chars"]) && When(e["at"]) > cutoff)
            .Select(e => Transcripts.NormTitle(Py.AsString(e["title"]) ?? "")).ToHashSet();
    }

    /// <summary>Lectures already sent without a transcript: send them again once one was copied from the app.</summary>
    async Task ReshareWithCopiedTranscripts(IGranolaSession s, ClientReport rep, int limit = 5, CancellationToken ct = default)
    {
        var waiting = new List<(string Id, string Body)>();
        foreach (var (id, node) in Seen)
        {
            if (node is not JsonObject entry || Py.AsString(entry["decision"]) != "shared") continue;
            string day = Py.Truthy(entry["start"]) ? Py.Str(entry["start"]) : Py.AsString(entry["date"]) ?? "";
            string body = CopiedTranscript(Py.AsString(entry["title"]) ?? "", day);
            double had = entry["transcript_chars"] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? Py.NumberValue(v) : 0;
            if (body.Length > 0 && Py.Strip(body).Length > 1.1 * had) waiting.Add((id, body));
        }
        if (waiting.Count == 0) return;
        waiting = waiting.Take(limit).ToList();
        Dictionary<string, Meeting> full;
        try
        {
            full = (await granola.GetMeetingsAsync(s, waiting.Select(w => w.Id).ToList(), ct)).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Last());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            rep.Errors.Add($"get_meetings (re-share): {e.Message}");
            return;
        }
        foreach (var (id, body) in waiting)
        {
            if (!full.TryGetValue(id, out var m)) continue;
            m.Transcript = body;
            JsonObject res;
            try
            {
                res = await Push(m);
            }
            catch (Exception e) when (e is not OperationCanceledException || e is TaskCanceledException)
            {
                rep.Errors.Add($"{id}: {e.Message}");
                continue;
            }
            var before = (Seen[id] as JsonObject)?["class_name"];
            string cls = Py.Truthy(res["class_name"]) ? Py.Str(res["class_name"]) : Py.Truthy(before) ? Py.Str(before) : "";
            Mark(m, "shared", new JsonObject { ["class_name"] = cls, ["filed"] = false, ["resent"] = true });
            rep.Reshared.Add(m.Title);
            Log($"[client] sent '{m.Title}' again with its transcript ({body.Length} chars)");
        }
    }

    static bool IsFalse(JsonNode? v) => v is JsonValue val && val.GetValueKind() == System.Text.Json.JsonValueKind.False;

    /// <summary>Say so once the library has summarized and filed what was sent.</summary>
    async Task CheckFiled(ClientReport rep, int limit = 10)
    {
        var cutoff = host.Clock() - TimeSpan.FromHours(FiledCheckHours);
        var waiting = Seen.Where(kv => kv.Value is JsonObject e && Py.AsString(e["decision"]) == "shared" && IsFalse(e["filed"]) && When(e["at"]) > cutoff)
            .Take(limit).ToList();
        foreach (var (id, node) in waiting)
        {
            var entry = (JsonObject)node!;
            string url = $"{Cc.ServerUrl.TrimEnd('/')}/api/notes/{Uri.EscapeDataString(id)}/status";
            JsonObject? info;
            try
            {
                info = await host.Get(url, Cc.PoolKey);
            }
            catch (Exception e) when (e is not OperationCanceledException || e is TaskCanceledException)
            {
                continue; // the library can't be reached right now: try again next time
            }
            if (info is null)
            {
                entry["filed"] = null; // a library too old to say; stop asking
                continue;
            }
            if (info["status"] is JsonNode status && Py.AsString(status) != "done") continue;
            entry["filed"] = true;
            entry["class_name"] = Py.Truthy(info["class_name"]) ? info["class_name"]!.DeepClone() : entry["class_name"]?.DeepClone();
            string cls = Py.Truthy(entry["class_name"]) ? Py.Str(entry["class_name"]) : "";
            string where = cls.Length > 0 ? $" under {cls}" : "";
            string how = Py.Truthy(entry["resent"]) ? " Its notes were rewritten from the transcript."
                : Py.Truthy(info["summary_model"]) ? " Notes written from the transcript." : "";
            rep.Filed.Add((Py.AsString(entry["title"]) ?? "", cls));
            host.Notify("Study Stash", $"“{Py.Str(entry["title"])}” is in {Library}{where}.{how}");
        }
    }

    /// <summary>Something to follow up on soon: the library finishing what was just sent.</summary>
    public bool Busy()
    {
        var recent = host.Clock() - TimeSpan.FromMinutes(30);
        return Seen.Any(kv => kv.Value is JsonObject e && Py.AsString(e["decision"]) == "shared" && IsFalse(e["filed"]) && When(e["at"]) > recent);
    }

    public async Task RunLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            while (wake.CurrentCount > 0) await wake.WaitAsync(stop);
            try
            {
                await PollOnceAsync(ct: stop);
                LastError = SendProblem;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log($"[client] error: {e.Message}\n{e}");
            }
            int wait = Busy() ? QuickPollSeconds : Cc.PollIntervalSeconds;
            try
            {
                if (await wake.WaitAsync(TimeSpan.FromSeconds(wait), stop))
                    await Task.Delay(TimeSpan.FromSeconds(5), stop); // let a burst of copies settle, then check
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
