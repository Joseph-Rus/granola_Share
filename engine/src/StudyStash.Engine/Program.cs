using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using StudyStash.Core;
using StudyStash.Library;

// The C# engine. The Study Stash apps still start the Python one: these commands are for trying this one out.
string? Option(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
bool Flag(string name) => args.Contains(name);
string home = Option("--home") is string h ? Py.ExpandUser(h) : Configs.DefaultHome;

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

return args.FirstOrDefault() switch
{
    "run" => await Run(),
    "setup" when Flag("--page") => await Setup(),
    "config-check" => ConfigCheck(),
    "version" => Print(Engine.Version),
    _ => Print("usage: studystash run | setup --page [--no-browser] | config-check | version   (each takes --home DIR)", 2),
};

static int Print(string text, int code = 0)
{
    (code == 0 ? Console.Out : Console.Error).WriteLine(text);
    return code;
}

// The library: its pages, the laptop API, the pipeline, and (with server_sync) its own Granola sync.
async Task<int> Run()
{
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
    Task syncing = Task.CompletedTask;
    if (cfg.ServerSync)
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
    var app = LibraryWeb.Build(builder, cfg, store, pipeline);
    await app.StartAsync(stop.Token);
    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }
    await app.StopAsync(CancellationToken.None);
    await Task.WhenAll(working, syncing);
    return 0;
}

async Task<int> Setup()
{
    await SetupWeb.ServeAsync(home, browser: !Flag("--no-browser"), stop: stop.Token);
    return 0;
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
