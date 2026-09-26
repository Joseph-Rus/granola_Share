using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.Library;

/// <summary>
/// The engine's commands (`studystash ...`, or the Study Stash app given a command): the library, the laptop, and
/// both. The app runs its library and its MCP server through these, so the bundle has one program.
/// </summary>
public static class Cli
{
    /// <summary>The words that make the app the engine instead of opening its windows.</summary>
    public static readonly string[] Commands = ["run", "serve", "setup", "init", "doctor", "update", "autostart", "version", "mcp", "ai"];

    /// <summary>True when these arguments name a command (options may come first: <c>--home DIR run</c>).</summary>
    public static bool IsCommand(IReadOnlyList<string> args)
    {
        string[] valued = ["--home", "--role"];
        for (int i = 0; i < args.Count; i++)
        {
            if (valued.Contains(args[i])) { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            return Commands.Contains(args[i]);
        }
        return false;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        // The commands:
        //   The library:   run | serve | setup --page | init
        //   Claude and AI: mcp | ai
        //   Both:          doctor | update | autostart | version
        string[] valued = ["--home", "--role"];
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

        string command = words.FirstOrDefault() ?? "";
        if (command is not ("version" or "")) Directory.CreateDirectory(home);
        return command switch
        {
            "run" => await Library(updates: true),
            "serve" => await Library(updates: false),
            "setup" when Flag("--page") => await Setup(),
            "init" => Print($"Config: {Configs.WriteExample(home)}\nEdit it, or run `studystash setup --page` for the guided version."),
            "mcp" => await Mcp(),
            "ai" => await AiCommand(),
            "doctor" => await Doctor.RunAsync(home, Option("--role"), DoctorHost.ThisComputer()),
            "update" => await Update(),
            "autostart" => AutostartCommand(),
            "version" => Print(Engine.Version),
            _ => Print("usage: studystash run | serve | setup --page [--no-browser] | init\n"
                + "       | doctor [--role server|client] | update [--check] [--force]\n"
                + "       | autostart install|uninstall|status --role server|client | version\n"
                + "       | mcp   (the MCP server for Claude, over stdin and stdout)\n"
                + "       | ai [use PROVIDER [--job notes|sort|ask|agent] [--model M] | test [PROVIDER] | ask QUESTION]   (each takes --home DIR)", 2),
        };

        // Where `mcp` reads the library: the laptop's library, or this computer's own.
        static (string? Url, string Key) McpTarget(string home)
        {
            var cc = Configs.LoadClient(home);
            if (cc.ServerUrl.Length > 0) return (cc.ServerUrl, cc.PoolKey);
            var cfg = Configs.Load(home);
            return File.Exists(cfg.ConfigPath) ? ($"http://127.0.0.1:{cfg.WebPort}", cfg.PoolPassword) : (null, "");
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

        // Its pages, the laptop API, the pipeline, Claude's door, and (for `run`) updates.
        async Task<int> Library(bool updates)
        {
            if (UnderWindowsKeepAlive("server")) return 0;
            var cfg = Configs.Load(home);
            if (!File.Exists(cfg.ConfigPath)) return Print($"Not set up yet: run `studystash setup --page` (no {cfg.ConfigPath}).", 1);
            if (cfg.AdminPassword.Length == 0) // configs from 0.1 have none; Settings needed one then
            {
                cfg.AdminPassword = Http.TokenUrlSafe(12);
                Configs.Save(cfg);
                Console.WriteLine($"Created an admin password for the Settings page (see {cfg.ConfigPath}).");
            }
            if (Flag("--no-ollama")) cfg.OllamaEnabled = false;
            Console.WriteLine($"studystash {Engine.Version}: pool '{cfg.PoolName}' on port {cfg.WebPort}");
            using var store = new Store(cfg.DbPath, cfg.PoolDir);
            // Notes, sorting and Ask use the AI picked in Settings (ai.json): Ollama unless another is chosen.
            var ai = new AiJobs(home, () => cfg.OllamaHost);
            var pipeline = new Pipeline(cfg, store, ai.SortAsync, ai.SummarizeAsync, notesModel: () => ai.Describe("notes", cfg));
            var working = pipeline.Start(stop.Token);
            Task updating = Task.CompletedTask;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(
                IPAddress.TryParse(cfg.WebHost, out var ip) ? ip : IPAddress.Any, cfg.WebPort, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1));
            // Settings' "Update now": on a Mac or Linux this restarts the service onto the new version, this copy included.
            // One record of who may read through Claude, for the library's Settings and for Claude's door alike.
            var access = new ClaudeAccess(home);
            // Search over files that aren't lectures (the Canvas mirror, the AI's files, readable folders).
            var fileIndex = LibraryWeb.MakeFileIndex(cfg, store);
            var indexing = fileIndex.RunAsync(Console.WriteLine, stop.Token);
            // Canvas, through the Chrome extension: one queue for the sync and for AIs' reads.
            var canvas = new StudyStash.Core.Canvas.CanvasSync(home, c => store.ClassDir(c)) { KnownClass = c => cfg.ClassNames().Contains(c) };
            // After a sync, an AI explores each class it hasn't explored yet (Settings can ask again).
            var scout = new StudyStash.Core.Canvas.Scout(home, c => store.ClassDir(c), ai);
            canvas.Synced += classes =>
            {
                var s = StudyStash.Core.Canvas.CanvasSettings.Load(home);
                if (ai.AgentReady().Ok)
                    scout.Queue(classes.Where(c => !s.Scouts.ContainsKey(c) && !File.Exists(Path.Combine(store.ClassDir(c), "Canvas", "canvas-recipe.md"))).ToArray());
            };
            var app = LibraryWeb.Build(builder, cfg, store, pipeline, new LibraryWebOptions
            {
                Apply = (rel, h) => Updates.ApplyAsync(rel, h, UpdateHost.ThisComputer()), Claude = access, Reach = ClaudeReach.ThisComputer(),
                AskChat = ai.Ask(() => cfg), Ai = ai, Canvas = canvas, Scout = scout, Files = fileIndex,
                Inbox = new Inbox(cfg.PoolDir, () => cfg.ClassNames(), c => store.ClassDir(c), ai, new History(cfg.PoolDir)),
            });
            await app.StartAsync(stop.Token);
            // Claude's door: MCP and its sign-in, on this computer only; Tailscale Serve or Funnel passes it on when that's on.
            var claudeBuilder = WebApplication.CreateSlimBuilder();
            claudeBuilder.Logging.ClearProviders();
            claudeBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, ClaudeWeb.PortFor(cfg)));
            var claude = ClaudeWeb.Build(claudeBuilder, cfg, new LibraryReader(cfg, store), access, canvas, fileIndex);
            try
            {
                await claude.StartAsync(stop.Token);
            }
            catch (IOException e)
            {
                Console.WriteLine($"Claude's port {ClaudeWeb.PortFor(cfg)} is taken ({e.Message}); Claude can't connect until it's free.");
            }
            // Under launchd or systemd, a new version is installed and this copy stops: the service manager starts the new one.
            if (updates) updating = Updates.StartAutoUpdate(home, () => Configs.Load(home).AutoUpdate, _ => stop.Cancel(), Console.WriteLine, stop.Token);
            await Until(stop.Token);
            await app.StopAsync(CancellationToken.None);
            await claude.StopAsync(CancellationToken.None);
            await Task.WhenAll(working, updating, indexing);
            return 0;
        }

        // --- Claude -------------------------------------------------------------------------------------------------------

        // The MCP server over stdin/stdout, for Claude Code and Claude Desktop on this computer. A laptop reads its library
        // with the password it already has; the library's own computer reads itself.
        async Task<int> Mcp()
        {
            var (url, key) = McpTarget(home);
            if (url is null) return Print("Study Stash isn't set up on this computer yet: open the Study Stash app first.", 1);
            await ClaudeTools.RunStdioAsync(new RemoteLibrary(url, key), stop.Token);
            return 0;
        }

        // Which AI does the work: show it, pick one, or try one.
        async Task<int> AiCommand()
        {
            var settings = AiSettings.Load(home);
            string sub = words.ElementAtOrDefault(1) ?? "";
            if (sub == "use" && words.ElementAtOrDefault(2) is string pick)
            {
                if (AiProviders.All().All(p => p.Id != pick)) return Print($"There's no AI called {pick}: claude, codex, gemini or ollama.", 2);
                string model = Option("--model") ?? "";
                if (Option("--job") is string job)
                {
                    if (!AiSettings.Jobs.Contains(job)) return Print($"There's no kind of work called {job}: {string.Join(", ", AiSettings.Jobs)}.", 2);
                    settings.ByJob[job] = new AiChoice(pick, model);
                }
                else
                {
                    settings.Provider = pick;
                    if (model.Length > 0) settings.Models[pick] = model;
                }
                settings.Save(home);
            }
            else if (sub == "test")
            {
                string id = words.ElementAtOrDefault(2) ?? settings.Provider;
                var (ok, why) = await new AiJobs(home, () => Configs.Load(home).OllamaHost).TestAsync(id, Option("--model") ?? settings.Models.GetValueOrDefault(id, ""));
                return Print(ok ? $"{AiProviders.Get(id).Name} works." : $"{AiProviders.Get(id).Name} didn't answer: {why}", ok ? 0 : 1);
            }
            else if (sub == "ask" && words.Count > 2)
            {
                // As an agent, with the library's tools (Canvas too), reading this folder.
                var jobs = new AiJobs(home, () => Configs.Load(home).OllamaHost);
                bool wrote = false;
                await foreach (var e in jobs.AgentAsync(string.Join(" ", words.Skip(2)), Directory.GetCurrentDirectory(), write: false, job: Option("--job") ?? "agent", ct: stop.Token))
                {
                    if (e.Kind == "text") { Console.Write(e.Text); wrote = true; }
                    else if (e.Kind == "final" && !wrote) Console.Write(e.Text);
                    else if (e.Kind == "tool") Console.Error.WriteLine($"  [{e.Name}] {e.Path}");
                    else if (e.Kind == "error") return Print("\n" + e.Text, 1);
                }
                Console.WriteLine();
                return 0;
            }
            foreach (string job in AiSettings.Jobs)
            {
                var c = settings.For(job);
                Console.WriteLine($"{job,-6} {AiProviders.Get(c.Provider).Name}{(c.Model.Length > 0 ? " (" + c.Model + ")" : "")}");
            }
            foreach (var p in AiProviders.All())
                Console.WriteLine($"  {p.Id,-7} {(p.Available() ? "installed" : "not installed: " + p.Site)}{(settings.Tests.GetValueOrDefault(p.Id) is { Length: > 0 } t ? ", last test: " + t : "")}");
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
    }
}
