using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core;

/// <summary>One line of `doctor`: ok, warn or fail, what it found, and how to fix it.</summary>
public sealed record Check(string Name, string State, string Detail, string Fix = "");

/// <summary>What the laptop's copy of the library's API needs: its health check.</summary>
public static class LibraryApi
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>GET /api/health; throws with a sentence a person can read on any failure (client.check_server).</summary>
    public static async Task<JsonObject> CheckServerAsync(string serverUrl, string poolKey, HttpClient? http = null)
    {
        string url = serverUrl.TrimEnd('/') + "/api/health";
        HttpResponseMessage r;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + poolKey);
            r = await (http ?? Http).SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            throw new InvalidOperationException($"could not reach {url}: {e.Message}", e);
        }
        using (r)
        {
            if (r.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("wrong password");
            if (r.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"unexpected response {(int)r.StatusCode} from {url}");
            return Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject ?? throw new JsonException("not an object");
        }
    }
}

/// <summary>Everything `doctor` asks of this computer, so tests can answer instead.</summary>
public sealed class DoctorHost
{
    public string System { get; init; } = Machine.Platform;
    /// <summary>GET a URL with the library password; its status and body. Throws when nothing answers.</summary>
    public Func<string, string, Task<(int Status, string Body)>> HealthGet { get; init; } = GetAsync;
    public Func<string, Task<List<(string Name, double SizeGb)>?>> ListModels { get; init; } = host => Ollama.ListModelsAsync(host);
    public Func<string, string> ServiceStatus { get; init; } = role => Autostart.Status(role);
    public Func<TailscaleInfo> Tailscale { get; init; } = () => HostInfo.Tailscale();
    public Func<int?>? SleepMinutes { get; init; }
    public Func<Task<Release?>> Latest { get; init; } = () => Updates.CachedLatestAsync();
    public Func<bool> OllamaInstalled { get; init; } = () => Ollama.Installed();
    public Func<int, bool?> Firewall { get; init; } = port => Machine.FirewallOpen(port);
    public Func<string> HostName { get; init; } = Machine.HostName;
    // the laptop
    public Func<string, string, Task<JsonObject>> CheckServer { get; init; } = (url, key) => LibraryApi.CheckServerAsync(url, key);
    public bool Unicode { get; init; } = Doctor.ConsoleIsUnicode();

    public static DoctorHost ThisComputer() => new();

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    static async Task<(int, string)> GetAsync(string url, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + password);
        using var r = await Http.SendAsync(request);
        return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
    }
}

/// <summary>`doctor` (doctor.py): check every piece of a setup and say how to fix what is broken.</summary>
public static class Doctor
{
    public const string Ok = "ok", Warn = "warn", Fail = "fail";

    static string LogTail(string path, int n = 6)
    {
        List<string> lines;
        try
        {
            lines = Py.SplitLines(Encoding.UTF8.GetString(File.ReadAllBytes(path))).Where(l => Py.Strip(l).Length > 0).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
        return string.Join("\n", lines.TakeLast(n));
    }

    static async Task<Check> VersionCheckAsync(Func<Task<Release?>> latest)
    {
        var rel = await latest();
        if (Updates.IsNewer(rel)) return new Check("Version", Warn, $"{Engine.Version} installed, {rel!.Tag} is out", "run `studystash update`");
        return new Check("Version", Ok, Engine.Version + (rel is not null ? " (newest)" : ""));
    }

    // --- the library (the Mac mini) ---------------------------------------------------------------------------------

    public static async Task<List<Check>> ServerChecksAsync(Config cfg, DoctorHost? host = null)
    {
        host ??= new DoctorHost();
        string system = host.System;
        var output = new List<Check>();
        if (!File.Exists(cfg.ConfigPath)) return [new Check("Config", Fail, $"no {cfg.ConfigPath}", "run `studystash setup --page`")];
        int n = cfg.Classes.Count;
        output.Add(new Check("Config", cfg.PoolPassword.Length > 0 ? Ok : Warn, $"{cfg.ConfigPath}, {n} class{(n != 1 ? "es" : "")}",
            cfg.PoolPassword.Length > 0 ? "" : "no pool password: anyone who can reach the server can read and add notes"));
        if (n == 0)
            output.Add(new Check("Classes", Warn, "none yet, so everything lands in Unsorted",
                "add them in the web UI under Settings, or rerun `studystash setup --page`"));

        try
        {
            Directory.CreateDirectory(cfg.PoolDir);
            string probe = Path.Combine(cfg.PoolDir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            output.Add(new Check("Notes folder", Ok, cfg.PoolDir));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.Add(new Check("Notes folder", Fail, $"can't write to {cfg.PoolDir}: {e.Message}", "pick another folder in config.toml"));
        }

        string svc = host.ServiceStatus("server");
        string log = Path.Combine(cfg.LogDir, "server.log");
        try
        {
            var (status, body) = await host.HealthGet($"http://127.0.0.1:{cfg.WebPort}/api/health", cfg.PoolPassword);
            if (status == 200)
            {
                var info = Py.JsonLoads(body) as JsonObject;
                string ver = info is not null && Py.Truthy(info["version"]) ? Py.Str(info["version"]) : "an old version";
                bool stale = ver != Engine.Version;
                output.Add(new Check("Web server", stale ? Warn : Ok, $"answering on port {cfg.WebPort} (running {ver})",
                    stale ? "restart it to load this version: `studystash autostart install --role server`" : ""));
            }
            else
            {
                output.Add(new Check("Web server", Fail, $"port {cfg.WebPort} answered {status}",
                    "another app may be using the port; change web_port in config.toml"));
            }
        }
        catch (Exception) // whatever it was, nothing usable answered
        {
            string tail = LogTail(log);
            string fix = svc != "missing" && tail.Length > 0 ? "it is installed but not answering; recent log:\n" + tail
                : "start it: `studystash autostart install --role server` (or `studystash run` to watch it)";
            output.Add(new Check("Web server", Fail, $"nothing answers on port {cfg.WebPort}", fix));
        }

        output.Add(svc switch
        {
            "running" => new Check("Background service", Ok, "starts at login and restarts if it stops"),
            "stopped" => new Check("Background service", Fail, "installed but not running",
                $"`studystash autostart install --role server`, then check {log}"),
            _ => new Check("Background service", Warn, "not installed, so the library stops when you log out or reboot",
                "`studystash autostart install --role server`"),
        });

        if (cfg.OllamaEnabled)
        {
            var models = await host.ListModels(cfg.OllamaHost);
            if (models is null && host.OllamaInstalled())
            {
                output.Add(new Check("Ollama", Fail, $"installed, but not answering at {cfg.OllamaHost}; study notes and AI sorting are paused",
                    system switch
                    {
                        "Darwin" => "open the Ollama app (`open -a Ollama`)",
                        "Windows" => "open Ollama from the Start menu",
                        _ => "run `ollama serve`",
                    }));
            }
            else if (models is null)
            {
                output.Add(new Check("Ollama", Fail, "not installed, so study notes and AI sorting are paused",
                    "rerun `studystash setup --page`: it installs Ollama for you (or get it from https://ollama.com)"));
            }
            else
            {
                var names = models.Select(m => m.Name).ToList();
                foreach (var (label, model) in new[] { ("Summary model", cfg.EffectiveSummaryModel), ("Sorting model", cfg.OllamaModel) })
                {
                    if (label == "Summary model" && !cfg.SummaryEnabled) continue;
                    output.Add(Ollama.HasModel(names, model) ? new Check(label, Ok, model)
                        : new Check(label, Fail, $"{model} is not installed", $"`ollama pull {model}`, or pick an installed one in Settings"));
                }
            }
        }
        else
        {
            output.Add(new Check("Ollama", Warn, "AI is off: only folder and title rules sort lectures, and no summaries are written",
                "turn it on in Settings"));
        }

        var ts = host.Tailscale();
        string problem = HostInfo.TailscaleProblem(ts);
        if (problem.Length == 0)
            output.Add(new Check("Tailscale", Ok, "your laptop can reach " + HostInfo.ServerUrls(cfg.WebPort, ts, host.HostName())[0]));
        else if (!ts.Installed)
            output.Add(new Check("Tailscale", Warn, "not installed; your laptop can only reach this computer on the same Wi-Fi",
                "rerun `studystash setup --page` to install it, or get it from https://tailscale.com/download; "
                + "sign in on both computers with one account"));
        else
            output.Add(new Check("Tailscale", Warn, problem + "; your laptop can only reach this computer on the same Wi-Fi",
                "open Tailscale and sign in (same account as your laptop), or rerun `studystash setup --page`"));

        if (system == "Windows" && host.Firewall(cfg.WebPort) == false)
            output.Add(new Check("Firewall", Warn, $"Windows Firewall has no rule for port {cfg.WebPort}, so it may block your laptop",
                "rerun `studystash setup --page` and let it add the rule (Windows asks for permission)"));

        if (Machine.SleepFix.TryGetValue(system, out string? sleepFix))
        {
            int? mins = (host.SleepMinutes ?? (() => Machine.SleepMinutes(system)))();
            if (mins is > 0 or < 0)
                output.Add(new Check("Sleep", Warn, $"this computer sleeps after {mins} min idle, and the library goes offline with it", sleepFix));
        }

        output.Add(await VersionCheckAsync(host.Latest));
        return output;
    }

    // --- your laptop ------------------------------------------------------------------------------------------------

    /// <summary>The laptop: the library it sends to and whether that answers, Tailscale, and lectures still on their way.</summary>
    public static async Task<List<Check>> ClientChecksAsync(ClientConfig cc, DoctorHost? host = null)
    {
        host ??= new DoctorHost();
        const string connect = "open Study Stash and connect it to your library";
        if (!File.Exists(cc.ConfigPath)) return [new Check("Config", Fail, $"no {cc.ConfigPath}", connect)];
        if (cc.ServerUrl.Length == 0) return [new Check("Config", Fail, $"{cc.ConfigPath} names no library", connect)];
        var output = new List<Check> { new("Config", Ok, cc.ConfigPath) };
        try
        {
            var info = await host.CheckServer(cc.ServerUrl, cc.PoolKey);
            output.Add(new Check("Library", Ok, $"'{Py.Str(info["pool_name"])}' at {cc.ServerUrl}"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            output.Add(new Check("Library", Fail, $"{cc.ServerUrl}: {e.Message}",
                "is Tailscale on on both computers, and is the library's computer awake? "
                + $"Wrong address or password: {connect} again"));
        }
        var ts = host.Tailscale();
        string problem = HostInfo.TailscaleProblem(ts);
        if (problem.Length == 0)
            output.Add(new Check("Tailscale", Ok, "connected" + (ts.Dns.Length > 0 ? $" as {ts.Dns}" : "")));
        else
            output.Add(new Check("Tailscale", Warn, problem + "; this computer reaches the library only on the same Wi-Fi",
                ts.Installed ? "open Tailscale and sign in with the same account as your library's computer"
                    : $"install it from {Ready.TailscaleDownload} and sign in with the same account as your library's computer"));
        output.Add(RecordingsCheck(cc.Home, cc.PoolName.Length > 0 ? cc.PoolName : "your library"));
        output.Add(await VersionCheckAsync(host.Latest));
        return output;
    }

    /// <summary>Lectures recorded here that haven't reached the library yet, or that Whisper couldn't write down. Only
    /// looks: a laptop that never recorded has no recordings folder, and doctor doesn't make one.</summary>
    static Check RecordingsCheck(string home, string library)
    {
        var lectures = Directory.Exists(Path.Combine(home, "recordings")) ? new LectureStore(home).All() : [];
        static string Count(int n) => $"{n} lecture{(n != 1 ? "s" : "")}";
        var waiting = lectures.Where(l => l.State is LectureState.Transcribing or LectureState.Sending).ToList();
        if (waiting.Count > 0)
            return new Check("Recordings", Warn, $"{Count(waiting.Count)} waiting to reach {library}",
                waiting.FirstOrDefault(l => l.Error.Length > 0) is { } stuck ? $"last try: {stuck.Error}"
                    : "they go on their own while Study Stash is open and the library answers");
        var failed = lectures.Where(l => l.State == LectureState.Failed).ToList();
        if (failed.Count > 0)
            return new Check("Recordings", Warn, $"{Count(failed.Count)} couldn't be written down: {failed[0].Error}",
                "open Study Stash to see which");
        int filed = lectures.Count(l => l.State == LectureState.Filed);
        return new Check("Recordings", Ok, lectures.Count == 0 ? "nothing recorded yet" : $"{Count(filed)} filed in {library}");
    }

    // --- output -----------------------------------------------------------------------------------------------------

    /// <summary>Whether this console can show ✓ ✗ →: a Windows console on its old code page can't.</summary>
    public static bool ConsoleIsUnicode()
    {
        try
        {
            var encoding = (Encoding)Console.OutputEncoding.Clone();
            encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            encoding.GetBytes("✓✗!→");
            return true;
        }
        catch (Exception e) when (e is EncoderFallbackException or IOException or ArgumentException)
        {
            return false;
        }
    }

    static Dictionary<string, string> Marks(bool unicode) => unicode
        ? new() { [Ok] = "✓", [Warn] = "!", [Fail] = "✗", ["arrow"] = "→" }
        : new() { [Ok] = "ok", [Warn] = "!", [Fail] = "X", ["arrow"] = "->" };

    public static string FormatChecks(string title, IReadOnlyList<Check> checks, bool unicode = true)
    {
        var mk = Marks(unicode);
        int width = checks.Count > 0 ? checks.Max(c => c.Name.Length) : 0;
        var lines = new List<string> { title, "" };
        foreach (var c in checks)
        {
            lines.Add($"  {mk[c.State],2} {c.Name.PadRight(width)}  {c.Detail}");
            if (c.Fix.Length > 0 && c.State != Ok)
            {
                string pad = new(' ', width + 7);
                lines.Add($"{pad}{mk["arrow"]} {c.Fix.Replace("\n", "\n" + pad + "  ")}");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Print the checks for this computer's role(s). Returns 1 if anything failed.</summary>
    public static async Task<int> RunAsync(string home, string? role, DoctorHost host, Action<string>? print = null)
    {
        print ??= Console.WriteLine;
        var roles = role is not null ? [role]
            : new[] { ("server", "config.toml"), ("client", "client.toml") }.Where(r => File.Exists(Path.Combine(home, r.Item2))).Select(r => r.Item1).ToList();
        if (roles.Count == 0)
        {
            print($"Nothing is set up in {home} yet.\n"
                + "  The computer that keeps the library:  studystash setup --page\n"
                + "  Your laptop:                          open the Study Stash app");
            return 1;
        }
        bool failed = false;
        foreach (string r in roles)
        {
            var checks = r == "server" ? await ServerChecksAsync(Configs.Load(home), host) : await ClientChecksAsync(Configs.LoadClient(home), host);
            failed |= checks.Any(c => c.State == Fail);
            print(FormatChecks($"studystash {Engine.Version}: {(r == "server" ? "library" : "laptop")} ({home})", checks, host.Unicode) + "\n");
        }
        print(failed ? $"Fix the {Marks(host.Unicode)[Fail]} items above, then run `studystash doctor` again." : "All good.");
        return failed ? 1 : 0;
    }
}
