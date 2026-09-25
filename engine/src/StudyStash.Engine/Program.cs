using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using StudyStash.Core;
using StudyStash.Library;

// The C# engine. The Study Stash apps still start the Python one: these commands are for trying this one out.
//   The library:  run | serve | setup --page | init | login | logout | sync | tools
//   The laptop:   client run | client open | client once | client login
//   Both:         doctor | update | autostart | config-check | version
string[] valued = ["--home", "--role", "--server", "--key"];
string? Option(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
bool Flag(string name) => args.Contains(name);
// The command and its words, wherever the options are: a service runs `studystash --home DIR run`.
var words = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !valued.Contains(args[i - 1]))).ToList();
string home = Path.GetFullPath(Option("--home") is string h ? Py.ExpandUser(h) : Configs.DefaultHome);
static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

// Ctrl+C, or the service manager's SIGTERM: stop cleanly, as the Python engine's web server does, so a setup page
// takes its setup_port file with it.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    stop.Cancel();
});

string command = string.Join(" ", words.Take(words.FirstOrDefault() == "client" ? 2 : 1));
if (command is not ("version" or "")) Directory.CreateDirectory(home);
try
{
    return command switch
    {
        "run" => await Library(ownSync: true, updates: true),
        "serve" => await Library(ownSync: false, updates: false),
        "setup" when Flag("--page") => await Setup(),
        "init" => Print($"Config: {Configs.WriteExample(home)}\nEdit it, or run `granola-share setup` for the guided version."),
        "login" => await SignIn(GranolaOAuth.For(Configs.Load(home))),
        "logout" => Logout(),
        "sync" => await SyncCommand(),
        "tools" => await Tools(),
        "client run" => await ClientRun(),
        "client open" => await ClientOpen(),
        "client once" => await ClientOnce(),
        "client login" => await SignIn(GranolaOAuth.For(Configs.LoadClient(home))),
        "doctor" => await Doctor.RunAsync(home, Option("--role"), DoctorHost.ThisComputer()),
        "update" => await Update(),
        "autostart" => AutostartCommand(),
        "config-check" => ConfigCheck(),
        "version" => Print(Engine.Version),
        _ => Print("usage: studystash run | serve | setup --page [--no-browser] | init | login [--no-browser] | logout\n"
            + "       | sync [--once] [--no-ollama] | tools [--probe]\n"
            + "       | client run [--no-ui] | client open [--install] [--server URL] [--key KEY] [--no-browser]\n"
            + "       | client once [--auto] | client login [--no-browser]\n"
            + "       | doctor [--role server|client] | update [--check] [--force]\n"
            + "       | autostart install|uninstall|status --role server|client | config-check | version   (each takes --home DIR)", 2),
    };
}
catch (OAuthException e)
{
    return Print(e.Message, 1);
}

static int Print(string text, int code = 0)
{
    (code == 0 ? Console.Out : Console.Error).WriteLine(text);
    return code;
}

// Windows has no service manager to keep a service running: the Startup file's copy hands over to a windowless one,
// which runs the service as a child and starts it again whenever it stops. True when this copy was that handover.
bool UnderWindowsKeepAlive(string role)
{
    if (!OperatingSystem.IsWindows() || Env(Autostart.ServiceEnv) != "1" || Env(Autostart.ChildEnv) is not null) return false;
    if (Env(Autostart.SupervisorEnv) is null) Autostart.Detach(args);
    else Autostart.KeepAlive(home, role, args);
    return true;
}

// --- the library ----------------------------------------------------------------------------------------------

// Its pages, the laptop API, the pipeline, and (with server_sync, for `run`) its own Granola sync and updates.
async Task<int> Library(bool ownSync, bool updates)
{
    if (UnderWindowsKeepAlive("server")) return 0;
    var cfg = Configs.Load(home);
    if (!File.Exists(cfg.ConfigPath)) return Print($"Not set up yet: run `granola-share setup` (no {cfg.ConfigPath}).", 1);
    if (cfg.AdminPassword.Length == 0) // configs from 0.1 have none; Settings needed one then
    {
        cfg.AdminPassword = Http.TokenUrlSafe(12);
        Configs.Save(cfg);
        Console.WriteLine($"Created an admin password for the Settings page (see {cfg.ConfigPath}).");
    }
    if (Flag("--no-ollama")) cfg.OllamaEnabled = false;
    Console.WriteLine($"studystash {Engine.Version}: pool '{cfg.PoolName}' on port {cfg.WebPort}");
    using var store = new Store(cfg.DbPath, cfg.PoolDir);
    var pipeline = new Pipeline(cfg, store);
    var working = pipeline.Start(stop.Token);
    Task syncing = Task.CompletedTask, updating = Task.CompletedTask;
    if (ownSync && cfg.ServerSync)
    {
        if (!File.Exists(cfg.TokensPath))
            Console.WriteLine("server_sync is on but this server is not logged in to Granola; run `granola-share login`. "
                + "Continuing with the web UI only.");
        else
            syncing = Sync.RunLoopAsync(cfg, GranolaClient.For(cfg.McpUrl, GranolaOAuth.For(cfg)), store, onQueued: pipeline.Wake, stop: stop.Token);
    }
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(k => k.Listen(
        IPAddress.TryParse(cfg.WebHost, out var ip) ? ip : IPAddress.Any, cfg.WebPort, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1));
    // Settings' "Update now": on a Mac or Linux this restarts the service onto the new version, this copy included.
    var app = LibraryWeb.Build(builder, cfg, store, pipeline, new LibraryWebOptions { Apply = (rel, h) => Updates.ApplyAsync(rel, h, UpdateHost.ThisComputer()) });
    await app.StartAsync(stop.Token);
    // Under launchd or systemd, a new version is installed and this copy stops: the service manager starts the new one.
    if (updates) updating = Updates.StartAutoUpdate(home, () => Configs.Load(home).AutoUpdate, _ => stop.Cancel(), Console.WriteLine, stop.Token);
    await Until(stop.Token);
    await app.StopAsync(CancellationToken.None);
    await Task.WhenAll(working, syncing, updating);
    return 0;
}

static async Task Until(CancellationToken token)
{
    try
    {
        await Task.Delay(Timeout.Infinite, token);
    }
    catch (OperationCanceledException)
    {
    }
}

async Task<int> Setup()
{
    await SetupWeb.ServeAsync(home, browser: !Flag("--no-browser"), stop: stop.Token);
    return 0;
}

async Task<int> SignIn(GranolaOAuth oauth)
{
    await oauth.LoginAsync(openBrowser: !Flag("--no-browser"), ct: stop.Token);
    return 0;
}

int Logout()
{
    GranolaOAuth.For(Configs.Load(home)).Logout();
    return Print("Logged out.");
}

// The library's own pull from its own Granola account.
async Task<int> SyncCommand()
{
    var cfg = Configs.Load(home);
    if (Flag("--no-ollama")) cfg.OllamaEnabled = false;
    var client = GranolaClient.For(cfg.McpUrl, GranolaOAuth.For(cfg));
    using var store = new Store(cfg.DbPath, cfg.PoolDir);
    var pipeline = new Pipeline(cfg, store);
    if (Flag("--once"))
    {
        var rep = await Sync.SyncOnceAsync(cfg, client, store, ct: stop.Token);
        Console.WriteLine($"listed={rep.Listed} new={rep.New} queued={rep.Queued.Count} errors={rep.Errors.Count}");
        foreach (string e in rep.Errors) Console.WriteLine("  error: " + e);
        Console.WriteLine($"filed {await pipeline.RunPendingAsync(stop.Token)} note(s)");
        return 0;
    }
    var working = pipeline.Start(stop.Token);
    await Sync.RunLoopAsync(cfg, client, store, onQueued: pipeline.Wake, stop: stop.Token);
    await working;
    return 0;
}

// The MCP tools Granola has, for debugging; with --probe, a look at the account and the newest lectures too.
async Task<int> Tools()
{
    var cfg = Configs.Load(home);
    var client = GranolaClient.For(cfg.McpUrl, GranolaOAuth.For(cfg));
    await using var s = await client.SessionAsync(stop.Token);
    var tools = await client.ToolsAsync(s, stop.Token);
    Console.WriteLine(PyJson.Dumps(new JsonObject(tools.Select(t => KeyValuePair.Create(t.Key,
        (JsonNode?)new JsonObject { ["description"] = t.Value.Description, ["schema"] = t.Value.Schema.DeepClone() }))), indent: 2));
    if (!Flag("--probe")) return 0;
    if (tools.ContainsKey("get_account_info"))
    {
        Console.WriteLine("\n== get_account_info ==");
        Console.WriteLine(Py.Head(PyJson.Dumps(await client.CallAsync(s, "get_account_info", new JsonObject(), stop.Token), indent: 2), 3000));
    }
    var stubs = await client.ListMeetingsAsync(s, ct: stop.Token);
    Console.WriteLine($"\n== list_meetings: {stubs.Count} meetings ==");
    foreach (var m in stubs.Take(5))
        Console.WriteLine($"- {m.Id}  {Py.Head(m.Date, 10)}  {m.Title}  folder={Py.StrRepr(m.Folder)}  notes={m.NotesMarkdown.Length} chars");
    if (stubs.Count == 0) return 0;
    var full = await client.GetMeetingsAsync(s, [stubs[0].Id], stop.Token);
    if (full.Count > 0)
    {
        Console.WriteLine($"\n== get_meetings({stubs[0].Id}) keys: {Py.Repr(new JsonArray(full[0].Raw.Select(kv => kv.Key).Order(StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()))} ==");
        Console.WriteLine(Py.Head(full[0].NotesMarkdown, 800));
    }
    string transcript = await client.GetTranscriptAsync(s, stubs[0].Id, stop.Token);
    Console.WriteLine($"\n== transcript: {transcript.Length} chars ==" + (transcript.Length > 0 ? "" : " (none: free Granola plans don't share transcripts)"));
    return 0;
}

// --- the laptop -------------------------------------------------------------------------------------------------

// The background service: the watcher, plus the Study Stash page for setup and status.
async Task<int> ClientRun()
{
    if (UnderWindowsKeepAlive("client")) return 0;
    var rt = new LaptopRuntime(home, LaptopHost.ThisComputer(restart: stop.Cancel), stop);
    var cc = rt.Config();
    Console.WriteLine($"studystash {Engine.Version}: "
        + (rt.Configured() ? $"watching Granola for '{cc.PoolName}'" : "waiting for setup in the Study Stash page"));
    if (rt.Configured()) rt.StartWatching();
    else if (Flag("--no-ui")) return Print("Not set up yet: open Study Stash, or run `granola-share client setup`.", 1);
    if (Flag("--no-ui")) await Until(stop.Token);
    else await LaptopWeb.ServeAsync(rt, stop: stop.Token);
    return 0;
}

// What the Study Stash icon and the installer run: start the service if needed, then open its page.
async Task<int> ClientOpen()
{
    LaptopWeb.WritePrefill(home, Option("--server") ?? Env("GRANOLA_SHARE_SERVER"), Option("--key") ?? Env("GRANOLA_SHARE_KEY"));
    bool install = Flag("--install");
    string? url = await LaptopApp.OpenAsync(home, install, browser: !Flag("--no-browser"));
    if (url is null) return 1;
    if (Flag("--no-browser")) return Print(url);
    Console.WriteLine("Study Stash is open." + (install ? " Finish setting up there." : ""));
    return Print($"If nothing opened, go to: {url}");
}

async Task<int> ClientOnce()
{
    var cc = Configs.LoadClient(home);
    if (cc.ServerUrl.Length == 0) return Print("Not set up yet: run `granola-share client setup`.", 1);
    if (Flag("--auto")) cc.Mode = "auto";
    var host = LaptopHost.ThisComputer();
    var client = new ShareClient(cc, host.Granola(cc), host, cc.CopyTranscripts ? new TranscriptStore(home) : null);
    var rep = await client.PollOnceAsync(ct: stop.Token);
    Console.WriteLine($"listed={rep.Listed} considered={rep.Considered} shared={rep.Shared.Count} skipped={rep.Skipped.Count} "
        + $"pending={rep.Pending.Count} errors={rep.Errors.Count}");
    foreach (string e in rep.Errors) Console.WriteLine("  error: " + e);
    return 0;
}

// --- both -------------------------------------------------------------------------------------------------------

async Task<int> Update()
{
    Release? rel;
    try
    {
        rel = await Updates.LatestAsync();
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
    {
        return Print($"Could not check for updates: {e.Message}", 1);
    }
    if (rel is null) return Print("No releases are published yet.", 1);
    if (!Updates.IsNewer(rel) && !Flag("--force")) return Print($"studystash {Engine.Version} is the newest version.");
    Console.WriteLine($"studystash {Engine.Version} → {rel.Tag}  ({rel.Page})");
    if (Flag("--check")) return 0;
    return await Updates.ApplyAsync(rel, home, UpdateHost.ThisComputer()) ? 0 : 1;
}

int AutostartCommand()
{
    string? action = words.ElementAtOrDefault(1), role = Option("--role");
    if (action is not ("install" or "uninstall" or "status") || role is not ("server" or "client"))
        return Print("usage: studystash autostart install|uninstall|status --role server|client", 2);
    if (action == "status") return Print(Autostart.Status(role));
    if (action == "uninstall") return Print(Autostart.Uninstall(role, ServicePlaces.Default, Machine.Run) ? "Removed." : "Nothing to remove.");
    return Print("Installed: " + Autostart.Install(role, home, ServicePlaces.Default, Machine.Run));
}

// Reads config.toml and client.toml and writes them back in memory: "same" means this engine would leave them byte
// for byte as they are. Only line numbers are shown, never contents: these files hold the library password.
int ConfigCheck()
{
    int differ = 0;
    foreach (var (name, dump) in new (string, Func<string>)[]
             {
                 ("config.toml", () => Configs.Dump(Configs.Load(home))),
                 ("client.toml", () => Configs.DumpClient(Configs.LoadClient(home))),
             })
    {
        string path = Path.Combine(home, name);
        if (!File.Exists(path))
        {
            Console.WriteLine($"{name}: not on this computer");
            continue;
        }
        var before = Py.SplitLines(Py.ReadText(path));
        var after = Py.SplitLines(dump());
        var lines = Enumerable.Range(0, Math.Max(before.Count, after.Count))
            .Where(i => i >= before.Count || i >= after.Count || before[i] != after[i]).Select(i => i + 1).ToList();
        Console.WriteLine(lines.Count == 0 ? $"{name}: same" : $"{name}: differs on line {string.Join(", ", lines)}");
        if (lines.Count > 0) differ++;
    }
    return differ == 0 ? 0 : 1;
}
