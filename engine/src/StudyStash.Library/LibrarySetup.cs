using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StudyStash.Core;

namespace StudyStash.Library;

/// <summary>An answer on the setup page the person has to fix (the Python engine's ValueError): shown next to it.</summary>
public sealed class SetupProblem(string message) : Exception(message);

/// <summary>One slow thing the page shows progress for.</summary>
public sealed record JobView(bool Running, long Done, long Total, string Note, bool Error);

/// <summary>Slow things (downloads, installers, the model's first answer) run in the background; the page shows their
/// progress and what they say.</summary>
public sealed class Jobs
{
    readonly Lock gate = new();
    readonly Dictionary<string, JobView> state = [];

    public bool Running(string name)
    {
        lock (gate) return state.TryGetValue(name, out var j) && j.Running;
    }

    public JobView? Get(string name)
    {
        lock (gate) return state.GetValueOrDefault(name);
    }

    void Update(string name, Func<JobView, JobView> change)
    {
        lock (gate) state[name] = change(state[name]);
    }

    /// <summary>Runs `work(progress, note)`; what it returns becomes the note, and an exception an error note.</summary>
    public bool Start(string name, Func<Action<long, long>, Action<string>, Task<string?>> work, Func<Task>? after = null)
    {
        lock (gate)
        {
            if (state.TryGetValue(name, out var j) && j.Running) return false;
            state[name] = new JobView(true, 0, 0, "", false);
        }
        void Progress(long done, long total) => Update(name, j => j with { Done = done, Total = total });
        void Note(string text) => Update(name, j => j with { Note = Py.Strip(text) });
        _ = Task.Run(async () =>
        {
            try
            {
                string? said = await work(Progress, Note);
                if (!string.IsNullOrEmpty(said)) Update(name, j => j with { Note = said });
            }
            catch (Exception e)
            {
                Update(name, j => j with { Note = e.Message, Error = true });
            }
            finally
            {
                Update(name, j => j with { Running = false });
                if (after is not null) await after();
            }
        });
        return true;
    }

    public Dictionary<string, JobView> View()
    {
        lock (gate) return new Dictionary<string, JobView>(state);
    }
}

/// <summary>
/// Everything setup asks of this computer, so tests can answer instead. What only looks (memory, Ollama's models,
/// Tailscale) looks at this computer. What changes it (installing apps, the firewall, sleep, starting at login, opening
/// windows) is off unless <see cref="ThisComputer"/> turns it on: a test that forgets one gets an error, not an installer.
/// </summary>
public sealed class SetupHost
{
    static Exception Off(string what) => new InvalidOperationException($"{what} is off for this setup.");

    public string System { get; init; } = Machine.Platform;
    public Func<string, Task<List<(string Name, double SizeGb)>?>> ListModels { get; init; } = host => Ollama.ListModelsAsync(host);
    public Func<bool> OllamaInstalled { get; init; } = () => Ollama.Installed();
    public Func<string, Task<bool>> StartOllama { get; init; } = _ => throw Off("Starting Ollama");
    public Func<Action<string>, Action<long, long>, Task<bool>> InstallOllama { get; init; } = (_, _) => throw Off("Installing Ollama");
    public Func<string, string, Action<long, long>, Task<(bool Ok, string Why)>> PullModel { get; init; } = (_, _, _) => throw Off("Downloading a model");
    public Func<string, string, Task<(double? Seconds, string Why)>> TryModel { get; init; } = (host, model) => Ollama.TryModelAsync(host, model);
    public Func<TailscaleInfo> Tailscale { get; init; } = () => HostInfo.Tailscale();
    public Func<Action<string>, Action<long, long>, Task<bool>> InstallTailscale { get; init; } = (_, _) => throw Off("Installing Tailscale");
    public Func<bool> OpenTailscale { get; init; } = () => throw Off("Opening Tailscale");
    public Func<int, bool?> Firewall { get; init; } = port => Machine.FirewallOpen(port);
    public Func<int, Task<bool>> OpenFirewall { get; init; } = _ => throw Off("Changing Windows Firewall");
    public Func<int?> SleepMinutes { get; init; } = () => Machine.SleepMinutes();
    public Func<bool> KeepAwake { get; init; } = () => throw Off("Changing when this computer sleeps");
    public Func<double?> RamGb { get; init; } = Machine.TotalRamGb;
    public Func<double?> DiskFree { get; init; } = () => Machine.DiskFreeGb();
    public Func<int, Task<string>> PortStatus { get; init; } = port => HostInfo.PortStatusAsync(port);
    public Func<string, string, Task> InstallAutostart { get; init; } = (_, _) => throw Off("Starting the library at login");
    public Func<Config, Task<bool>> WaitHealthy { get; init; } = cfg => HostInfo.WaitForServerAsync(cfg);
    public Func<string?> TailscaleExe { get; init; } = HostInfo.TailscaleExe;
    public Func<string> HostName { get; init; } = Machine.HostName;
    public Action<string> OpenUrl { get; init; } = _ => throw Off("Opening a browser");
    /// <summary>How long `tailscale up` may wait for someone to sign in.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The real thing, for the setup page this computer serves.</summary>
    public static SetupHost ThisComputer() => new()
    {
        StartOllama = host => Ollama.StartAsync(host),
        InstallOllama = (note, progress) => Ready.InstallOllamaAsync(note, progress, Machine.Platform, Machine.Run, Ready.Download),
        PullModel = (model, host, progress) => Ollama.PullAsync(model, host, progress),
        // The page looks again every few seconds, so it doesn't wait for Tailscale's installer.
        InstallTailscale = (note, progress) => Ready.InstallTailscaleAsync(note, progress, Machine.Platform, Machine.Run, Ready.Download, remote: false),
        OpenTailscale = () => Ready.OpenTailscale(Machine.Platform, Machine.Run),
        OpenFirewall = port => Ready.OpenFirewallAsync(port, Machine.Run),
        KeepAwake = () => Ready.KeepAwake(Machine.Platform, Machine.Run),
        InstallAutostart = (role, home) => Task.Run(() => Autostart.Install(role, home, ServicePlaces.Default, Machine.Run)),
        OpenUrl = AppPage.OpenUrl,
    };
}

/// <summary>What this computer has, as setup last looked.</summary>
public sealed record SetupChecks(double? Ram, double? Disk, TailscaleInfo Tailscale, List<(string Name, double SizeGb)>? Models, bool OllamaInstalled);

/// <summary>
/// The library's setup as a page, so nobody needs a terminal (library_setup.py): the six steps of `granola-share
/// setup`, done with buttons and fields. Answers are kept in setup_draft.json as you go, and config.toml is written
/// only when you finish: that's how the app knows the library exists.
/// </summary>
public sealed partial class LibrarySetup
{
    public const int SetupPort = 8764;
    public const string Releases = "https://github.com/Joseph-Rus/study-stash/releases/latest/download";

    public string Home { get; }
    public SetupHost Host { get; }
    public Jobs Jobs { get; } = new();
    public Config Cfg { get; }
    public bool Fresh { get; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    // library: its name and password are saved; models: the model step is answered; finished: config.toml is written.
    public bool DraftLibrary { get; private set; }
    public bool DraftModels { get; private set; }
    public bool DraftAutostart { get; private set; } = true;
    public bool Finished { get; internal set; }

    readonly Lock gate = new();
    (DateTime At, SetupChecks Checks)? checkedAt;

    public LibrarySetup(string home, SetupHost? host = null)
    {
        Home = Py.NormPath(home);
        Host = host ?? new SetupHost();
        Cfg = Configs.Load(Home);
        Fresh = !File.Exists(Cfg.ConfigPath);
        DraftLibrary = DraftModels = !Fresh;
        LoadDraft();
        if (Cfg.PoolPassword.Length == 0) Cfg.PoolPassword = Http.TokenUrlSafe(9);
    }

    // --- the draft ---------------------------------------------------------------------------------------------

    public string DraftPath => Path.Combine(Home, "setup_draft.json");

    void LoadDraft()
    {
        JsonObject data;
        try
        {
            if (Py.JsonLoads(Py.ReadText(DraftPath)) is not JsonObject o) return;
            data = o;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return;
        }
        string? Text(string key) => data.ContainsKey(key) ? (Py.Truthy(data[key]) ? Py.Str(data[key]) : "") : null;
        if (Text("pool_name") is string name) Cfg.PoolName = name;
        if (Text("pool_password") is string password) Cfg.PoolPassword = password;
        if (data["web_port"] is JsonValue port && port.GetValueKind() == System.Text.Json.JsonValueKind.Number) Cfg.WebPort = (int)Py.NumberValue(port);
        if (Text("summary_model") is string summary) Cfg.SummaryModel = summary;
        if (Text("ollama_model") is string sort) Cfg.OllamaModel = sort;
        if (data.ContainsKey("ollama_enabled")) Cfg.OllamaEnabled = Py.Truthy(data["ollama_enabled"]);
        if (data.ContainsKey("auto_update")) Cfg.AutoUpdate = Py.Truthy(data["auto_update"]);
        if (Py.Truthy(data["pool_dir"])) Cfg.PoolDir = Py.NormPath(Py.Str(data["pool_dir"]));
        if (data["classes"] is JsonArray classes)
            Cfg.Classes = classes.OfType<JsonObject>().Select(c => new ClassDef(Py.Str(c["name"]),
                (c["aliases"] as JsonArray ?? new JsonArray()).Select(Py.Str).ToList(), c["description"] is null ? "" : Py.Str(c["description"]))).ToList();
        if (data.ContainsKey("library")) DraftLibrary = Py.Truthy(data["library"]);
        if (data.ContainsKey("models")) DraftModels = Py.Truthy(data["models"]);
        if (data.ContainsKey("autostart")) DraftAutostart = Py.Truthy(data["autostart"]);
    }

    void SaveDraft()
    {
        lock (gate)
        {
            var c = Cfg;
            var data = new JsonObject
            {
                ["pool_name"] = c.PoolName, ["pool_password"] = c.PoolPassword, ["pool_dir"] = c.PoolDir, ["web_port"] = c.WebPort,
                ["summary_model"] = c.SummaryModel, ["ollama_model"] = c.OllamaModel, ["ollama_enabled"] = c.OllamaEnabled,
                ["auto_update"] = c.AutoUpdate,
                ["classes"] = new JsonArray(c.Classes.Select(k => (JsonNode?)new JsonObject
                {
                    ["name"] = k.Name, ["aliases"] = new JsonArray(k.Aliases.Select(a => (JsonNode?)a).ToArray()), ["description"] = k.Description,
                }).ToArray()),
                ["library"] = DraftLibrary, ["models"] = DraftModels, ["autostart"] = DraftAutostart,
            };
            Directory.CreateDirectory(Home);
            Py.WriteText(DraftPath, PyJson.Dumps(data, indent: 1));
            checkedAt = null;
        }
    }

    // --- what this computer has --------------------------------------------------------------------------------

    /// <summary>The page asks every second or two, so this looks at most every few seconds.</summary>
    public async Task<SetupChecks> ChecksAsync(bool fresh = false)
    {
        if (!fresh && checkedAt is var (at, cached) && (DateTime.UtcNow - at).TotalSeconds <= 4) return cached;
        var models = await Host.ListModels(Cfg.OllamaHost);
        var checks = new SetupChecks(Host.RamGb(), Host.DiskFree(), Host.Tailscale(), models, models is not null || Host.OllamaInstalled());
        checkedAt = (DateTime.UtcNow, checks);
        return checks;
    }

    void Forget() => checkedAt = null;

    public async Task<List<string>> NamesAsync() => ((await ChecksAsync()).Models ?? []).Select(m => m.Name).ToList();

    public async Task<bool> ModelReadyAsync(string model) => Ollama.HasModel(await NamesAsync(), model);

    // --- step 1: Tailscale and Ollama ----------------------------------------------------------------------------

    public async Task<string> FixTailscaleAsync()
    {
        var ts = (await ChecksAsync(fresh: true)).Tailscale;
        if (ts.Running) return "Tailscale is connected.";
        if (!ts.Installed)
        {
            Jobs.Start("tailscale", async (progress, note) =>
            {
                note("Downloading Tailscale...");
                bool ok = await Host.InstallTailscale(note, progress);
                return ok || Host.System is "Darwin" or "Windows"
                    ? "Tailscale's installer is open: click through it, then sign in with the same account as your laptop."
                    : "Tailscale didn't install.";
            }, () => { Forget(); return Task.CompletedTask; });
            return "Downloading Tailscale.";
        }
        if (Host.OpenTailscale()) return "Tailscale is open: sign in there, with the same account as your laptop.";
        return ConnectTailscale(ts);
    }

    [GeneratedRegex(@"https://login\.tailscale\.com/\S+")]
    private static partial Regex TailscaleLogin();

    /// <summary>Where there's no Tailscale app to open, `tailscale up` prints a sign-in link: open it.</summary>
    string ConnectTailscale(TailscaleInfo ts)
    {
        string? exe = ts.Exe.Length > 0 ? ts.Exe : Host.TailscaleExe();
        if (exe is null) return "Open Tailscale and sign in.";
        Jobs.Start("tailscale", async (_, note) =>
        {
            var psi = new ProcessStartInfo(exe, ["up"])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Tailscale didn't start.");
            async Task Watch(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is string line)
                {
                    var link = TailscaleLogin().Match(line);
                    if (!link.Success) continue;
                    Host.OpenUrl(link.Value);
                    note($"Sign in to Tailscale in the browser tab that opened: {link.Value}");
                }
            }
            // `tailscale up` waits for the sign-in. Nobody signing in mustn't leave the button off for good.
            using var cts = new CancellationTokenSource(Host.ConnectTimeout);
            try
            {
                await Task.WhenAll(Watch(p.StandardOutput), Watch(p.StandardError)).WaitAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return "Tailscale didn't connect.";
            }
            return p.ExitCode == 0 ? "Tailscale is connected." : "Tailscale didn't connect.";
        }, () => { Forget(); return Task.CompletedTask; });
        return "Connecting Tailscale...";
    }

    public async Task<string> FixOllamaAsync()
    {
        if ((await ChecksAsync(fresh: true)).Models is not null) return "Ollama is running.";
        Jobs.Start("ollama", async (progress, note) =>
        {
            if (!Host.OllamaInstalled())
            {
                note("Downloading Ollama...");
                if (!await Host.InstallOllama(note, progress)) return "Ollama didn't install. Get it from https://ollama.com, then come back.";
            }
            note("Starting Ollama...");
            return await Host.StartOllama(Cfg.OllamaHost) ? "Ollama is running." : "Ollama is installed but didn't start. Open it from your apps.";
        }, () => { Forget(); return Task.CompletedTask; });
        return "Working on it.";
    }

    // --- step 2: the library ----------------------------------------------------------------------------------------

    static string Field(JsonObject data, string key) => Py.Truthy(data[key]) ? Py.Str(data[key]) : "";

    public async Task<string> SetLibraryAsync(JsonObject data)
    {
        string name = Py.Strip(Field(data, "name")) is { Length: > 0 } n ? n : "Lecture notes";
        string password = Py.Strip(Field(data, "password"));
        if (password.Length < 4) throw new SetupProblem("Use a password of at least 4 characters.");
        string folder = Py.ExpandUser(Field(data, "folder") is { Length: > 0 } f ? f : Cfg.PoolDir);
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SetupProblem($"Can't write to {folder}: {e.Message}");
        }
        string port = Py.Strip(Field(data, "port") is { Length: > 0 } p ? p : Cfg.WebPort.ToString(CultureInfo.InvariantCulture));
        if (port.Length == 0 || !port.All(char.IsAsciiDigit) || !long.TryParse(port, CultureInfo.InvariantCulture, out long asked) || asked is < 1024 or > 65535)
            throw new SetupProblem("The port is a number from 1024 to 65535.");
        int number = (int)asked;
        if ((number != Cfg.WebPort || Fresh) && await Host.PortStatus(number) == "busy")
            throw new SetupProblem($"Another app is using port {port}. Try {number + 1}.");
        Cfg.PoolName = name;
        Cfg.PoolPassword = password;
        Cfg.PoolDir = folder;
        Cfg.WebPort = number;
        DraftLibrary = true;
        SaveDraft();
        return "Saved.";
    }

    // --- step 3: classes ------------------------------------------------------------------------------------------

    public string ChangeClasses(JsonObject data)
    {
        if (data.ContainsKey("remove"))
        {
            if (!int.TryParse(Py.Str(data["remove"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                throw new SetupProblem("Pick a class to remove.");
            if (i < 0 || i >= Cfg.Classes.Count) return "Already removed.";
            var gone = Cfg.Classes[i];
            Cfg.Classes.RemoveAt(i);
            SaveDraft();
            return $"Removed {gone.Name}.";
        }
        string name = Py.Strip(Field(data, "name"));
        if (name.Length == 0) throw new SetupProblem("Give the class a name, like CS 101.");
        if (Cfg.Classes.Any(c => c.Name.ToLowerInvariant() == name.ToLowerInvariant())) throw new SetupProblem($"{name} is already there.");
        var aliases = Field(data, "aliases").Split(',').Select(Py.Strip).Where(a => a.Length > 0).ToList();
        Cfg.Classes.Add(new ClassDef(name, aliases, Py.Strip(Field(data, "description"))));
        SaveDraft();
        return $"Added {name}.";
    }

    // --- step 4: the models -------------------------------------------------------------------------------------

    async Task<List<string>> MissingAsync(params string[] models)
    {
        var missing = new List<string>();
        foreach (string m in models.Distinct())
            if (!await ModelReadyAsync(m)) missing.Add(m);
        return missing;
    }

    public async Task<string> SetModelsAsync(JsonObject data)
    {
        string summary = Py.Strip(Field(data, "summary"));
        string sort = Py.Strip(Field(data, "sort")) is { Length: > 0 } s ? s : summary;
        if (summary.Length == 0) throw new SetupProblem("Pick a model.");
        Cfg.SummaryModel = summary;
        Cfg.OllamaModel = sort;
        Cfg.OllamaEnabled = true;
        DraftModels = true;
        SaveDraft();
        var missing = await MissingAsync(summary, sort);
        if (missing.Count > 0)
        {
            Download(missing[0]);
            return $"Downloading {missing[0]}.";
        }
        TryIt();
        return "Saved. Trying it once...";
    }

    public bool Download(string model) => Jobs.Start("pull", async (progress, note) =>
    {
        note($"Downloading {model}...");
        var (ok, why) = await Host.PullModel(model, Cfg.OllamaHost, progress);
        if (!ok)
        {
            string hint = why.Contains("newer version", StringComparison.OrdinalIgnoreCase) ? " Update Ollama, then try again." : "";
            throw new InvalidOperationException($"The download failed: {why}.{hint}");
        }
        return $"Downloaded {model}.";
    }, async () =>
    {
        Forget();
        var missing = await MissingAsync(Cfg.EffectiveSummaryModel, Cfg.OllamaModel);
        if (missing.Count > 0 && Jobs.Get("pull")?.Error != true) Download(missing[0]);
        else if (missing.Count == 0) TryIt();
    });

    public bool TryIt()
    {
        string model = Cfg.EffectiveSummaryModel;
        return Jobs.Start("try", async (_, note) =>
        {
            note($"Trying {model} once (the first load can take a minute)...");
            var (secs, why) = await Host.TryModel(Cfg.OllamaHost, model);
            if (secs is not double s) throw new InvalidOperationException($"{model} didn't answer: {why}. Pick a smaller model if this keeps happening.");
            string took = s < 1 ? "under a second" : $"{Py.FormatFixed(s, 0)} s";
            return $"{model} answered in {took}. It's ready to write study notes.";
        });
    }

    public string WithoutAi()
    {
        Cfg.OllamaEnabled = false;
        DraftModels = true;
        SaveDraft();
        return "The library will sort by Granola folder and title only, and keep Granola's notes.";
    }

    // --- step 5: keeping it running -------------------------------------------------------------------------------

    public string SetPrefs(JsonObject data)
    {
        DraftAutostart = Py.Truthy(data["autostart"]);
        Cfg.AutoUpdate = Py.Truthy(data["auto_update"]);
        SaveDraft();
        return "Saved.";
    }

    public string FixFirewall()
    {
        Jobs.Start("firewall", async (_, note) =>
        {
            note("Windows asks for permission: click Yes.");
            if (!await Host.OpenFirewall(Cfg.WebPort))
                throw new InvalidOperationException("The firewall didn't change. Click the button again, and Yes when Windows asks.");
            return $"Your laptop can reach port {Cfg.WebPort} over Tailscale and this network.";
        });
        return "Asking Windows...";
    }

    public string StayAwake()
    {
        if (Host.KeepAwake()) return "Done: this PC stays awake while it's plugged in.";
        throw new SetupProblem($"That didn't take. {Machine.SleepFix.GetValueOrDefault(Host.System, "")}.");
    }

    // --- step 6: finishing ---------------------------------------------------------------------------------------

    public string Finish()
    {
        if (!DraftLibrary) throw new SetupProblem("Save your library's name and password first.");
        Jobs.Start("finish", async (_, note) =>
        {
            note("Saving your library...");
            Configs.Save(Cfg);
            if (DraftAutostart)
            {
                note("Starting it, and setting it to start when you log in...");
                await Host.InstallAutostart("server", Home);
                if (!await Host.WaitHealthy(Cfg))
                    throw new InvalidOperationException($"Your library is saved, but it hasn't answered yet. Its log is {Path.Combine(Cfg.LogDir, "server.log")}.");
            }
            Finished = true;
            File.Delete(DraftPath);
            return "Your library is ready.";
        }, () => { Forget(); return Task.CompletedTask; });
        return "Finishing...";
    }

    public async Task<(List<string> Urls, string Password, string Name, (string Mac, string Windows) Commands)> ConnectInfoAsync()
    {
        var urls = HostInfo.ServerUrls(Cfg.WebPort, (await ChecksAsync()).Tailscale, Host.HostName());
        return (urls, Cfg.PoolPassword, Cfg.PoolName, HostInfo.InviteCommands(urls[0], Cfg.PoolPassword));
    }

    /// <summary>Changes whenever a step's look changes, so the page knows to reload.</summary>
    public async Task<string> KeyAsync()
    {
        var c = await ChecksAsync();
        var names = (await NamesAsync()).Order(StringComparer.Ordinal).Select(n => (JsonNode?)n).ToArray();
        return new JsonArray(HostInfo.TailscaleProblem(c.Tailscale), c.Models is not null, c.OllamaInstalled, new JsonArray(names),
            new JsonObject { ["library"] = DraftLibrary, ["models"] = DraftModels, ["autostart"] = DraftAutostart, ["finished"] = Finished },
            Cfg.Classes.Count,
            new JsonObject(Jobs.View().OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.Running))))
            .ToJsonString();
    }
}
