using System.Collections;
using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace StudyStash.Core;

public sealed class ClassDef(string name, List<string>? aliases = null, string description = "")
{
    public string Name { get; set; } = name;
    public List<string> Aliases { get; set; } = aliases ?? [];
    public string Description { get; set; } = description;
}

/// <summary>The library: where lectures are summarized, sorted, and served. Lives in config.toml.</summary>
public sealed class Config(string home, string poolDir)
{
    public string Home { get; set; } = Py.NormPath(home);
    public string PoolDir { get; set; } = Py.NormPath(poolDir);
    public string PoolName { get; set; } = "Lecture notes";
    public int PollIntervalSeconds { get; set; } = 300;
    public string WebHost { get; set; } = "0.0.0.0";
    public int WebPort { get; set; } = 8787;
    public string PoolPassword { get; set; } = "";
    public string AdminPassword { get; set; } = ""; // from 0.2; the password above does the same now
    public string McpUrl { get; set; } = Configs.McpUrl;
    public int OauthCallbackPort { get; set; } = 3334;
    public string OauthPrompt { get; set; } = "login"; // always show Granola's account picker
    public bool ServerSync { get; set; } // also pull the library computer's own Granola account
    public bool AutoUpdate { get; set; } = true;
    public bool OllamaEnabled { get; set; } = true;
    public string OllamaHost { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen3.6:35b-a3b"; // sorts notes into classes
    public double MinConfidence { get; set; } = 0.6;
    public bool IncludeTranscripts { get; set; } = true;
    public bool SummaryEnabled { get; set; } = true; // write our own notes from the transcript when there is one
    public string SummaryModel { get; set; } = ""; // blank = the sorting model
    public int SummaryMaxContext { get; set; } = 32768;
    public bool KeepGranolaNotes { get; set; } // also keep Granola's own summary when we wrote one
    public List<ClassDef> Classes { get; set; } = [];

    public string EffectiveSummaryModel => SummaryModel.Length > 0 ? SummaryModel : OllamaModel;
    public string DbPath => Path.Combine(Home, "state.db");
    public string TokensPath => Path.Combine(Home, "tokens.json");
    public string ClientPath => Path.Combine(Home, "oauth_client.json");
    public string ConfigPath => Path.Combine(Home, "config.toml");
    public string DebugDir => Path.Combine(Home, "debug");
    public string LogDir => Path.Combine(Home, "logs");

    public List<string> ClassNames() => Classes.Select(c => c.Name).ToList();
}

/// <summary>The laptop you record on: watches your Granola account and sends finished lectures on. Lives in client.toml.</summary>
public sealed class ClientConfig(string home)
{
    public string Home { get; set; } = Py.NormPath(home);
    public string ServerUrl { get; set; } = "";
    public string PoolKey { get; set; } = "";
    public string PoolName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Mode { get; set; } = "auto"; // auto (send every lecture) | ask (Send/Skip popup each time)
    public int PollIntervalSeconds { get; set; } = 180;
    public bool IncludeTranscripts { get; set; } = true;
    public int ShareLookbackDays { get; set; } = 7;
    public int DialogTimeoutSeconds { get; set; } = 300;
    public bool AutoUpdate { get; set; } = true;
    // macOS: press Granola's own "Copy transcript" when a lecture ends. Off unless you turn it on: it
    // automates the Granola app, which may go against Granola's terms of service.
    public bool CopyTranscripts { get; set; }
    public string McpUrl { get; set; } = Configs.McpUrl;
    public int OauthCallbackPort { get; set; } = 3334;
    public string OauthPrompt { get; set; } = "login";

    public string ConfigPath => Path.Combine(Home, "client.toml");
    public string StatePath => Path.Combine(Home, "client_state.json");
    public string TokensPath => Path.Combine(Home, "tokens.json");
    public string ClientPath => Path.Combine(Home, "oauth_client.json");
    public string LogDir => Path.Combine(Home, "logs");
}

/// <summary>Reading and writing config.toml and client.toml, line for line as the Python engine does.</summary>
public static class Configs
{
    public const string Unsorted = "Unsorted";
    public const string McpUrl = "https://mcp.granola.ai/mcp";

    public static string DefaultHome =>
        Py.ExpandUser(Environment.GetEnvironmentVariable("GRANOLA_SHARE_HOME") ?? "~/.granola-share");

    // --- writing -----------------------------------------------------------------------------------

    static string Toml(bool b) => b ? "true" : "false";
    static string Toml(int i) => i.ToString(CultureInfo.InvariantCulture);
    static string Toml(double d) => Py.FloatRepr(d);
    static string Toml(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Toml)) + "]";

    /// <summary>A TOML string: json.dumps' escapes, except an emoji, which TOML only takes as \UXXXXXXXX.</summary>
    static string Toml(string s)
    {
        var sb = new StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append("\\U").Append(char.ConvertToUtf32(s[i], s[i + 1]).ToString("x8", CultureInfo.InvariantCulture));
                i++;
                continue;
            }
            string one = PyJson.Dumps(s[i].ToString());
            sb.Append(one, 1, one.Length - 2);
        }
        return sb.Append('"').ToString();
    }

    public static string Dump(Config cfg)
    {
        var lines = new List<string>
        {
            "# granola-share server config. Edit freely, or rerun `granola-share setup`.",
            $"pool_name = {Toml(cfg.PoolName)}",
            $"pool_dir = {Toml(cfg.PoolDir)}",
            $"poll_interval_seconds = {Toml(cfg.PollIntervalSeconds)}",
            $"web_host = {Toml(cfg.WebHost)}",
            $"web_port = {Toml(cfg.WebPort)}",
            "# Your laptop and browser use this. Blank = no login (only safe if nothing else can reach this computer).",
            $"pool_password = {Toml(cfg.PoolPassword)}",
            "# From 0.2: a second password that also logs in to the web UI. Not needed any more.",
            $"admin_password = {Toml(cfg.AdminPassword)}",
            "# true = this server also pulls notes from its own Granola account (needs `granola-share login`).",
            $"server_sync = {Toml(cfg.ServerSync)}",
            "# Install new releases automatically (checked every few hours by the background service).",
            $"auto_update = {Toml(cfg.AutoUpdate)}",
            $"include_transcripts = {Toml(cfg.IncludeTranscripts)}",
            $"mcp_url = {Toml(cfg.McpUrl)}",
            $"oauth_callback_port = {Toml(cfg.OauthCallbackPort)}",
            "# \"login\" always shows Granola's account picker when signing in. Blank = let the sign-in page decide.",
            $"oauth_prompt = {Toml(cfg.OauthPrompt)}",
            "",
            "[ollama]",
            $"enabled = {Toml(cfg.OllamaEnabled)}",
            $"host = {Toml(cfg.OllamaHost)}",
            "# Model that sorts notes into classes.",
            $"model = {Toml(cfg.OllamaModel)}",
            "# Below this confidence a note goes to Unsorted for a human to file.",
            $"min_confidence = {Toml(cfg.MinConfidence)}",
            "",
            "[summary]",
            "# Write our own lecture notes from the transcript instead of using Granola's summary.",
            "# Granola only hands out transcripts on paid plans; notes without one keep Granola's summary.",
            $"enabled = {Toml(cfg.SummaryEnabled)}",
            "# Ollama model that writes the summary. Blank = same as the sorting model.",
            $"model = {Toml(cfg.SummaryModel)}",
            "# Largest context (tokens) to ask for; longer transcripts are summarized in parts, then merged.",
            $"max_context = {Toml(cfg.SummaryMaxContext)}",
            "# true = also keep Granola's own summary in the note file.",
            $"keep_granola_notes = {Toml(cfg.KeepGranolaNotes)}",
            "",
            "# Classes notes get sorted into. Aliases match Granola folder names and note titles",
            "# before the model is asked.",
        };
        foreach (var c in cfg.Classes)
        {
            lines.AddRange([
                "",
                "[[classes]]",
                $"name = {Toml(c.Name)}",
                $"aliases = {Toml(c.Aliases)}",
                $"description = {Toml(c.Description)}",
            ]);
        }
        return string.Join("\n", lines) + "\n";
    }

    public static string DumpClient(ClientConfig cc)
    {
        string[] lines =
        [
            "# granola-share laptop config. Change it in the Study Stash app, or rerun `granola-share client setup`.",
            $"server_url = {Toml(cc.ServerUrl)}",
            $"pool_key = {Toml(cc.PoolKey)}",
            $"pool_name = {Toml(cc.PoolName)}",
            $"display_name = {Toml(cc.DisplayName)}",
            "# \"auto\" sends every finished lecture; \"ask\" pops up Send/Skip for each one.",
            $"mode = {Toml(cc.Mode)}",
            $"poll_interval_seconds = {Toml(cc.PollIntervalSeconds)}",
            $"include_transcripts = {Toml(cc.IncludeTranscripts)}",
            $"share_lookback_days = {Toml(cc.ShareLookbackDays)}",
            $"dialog_timeout_seconds = {Toml(cc.DialogTimeoutSeconds)}",
            "# Install new releases automatically (checked every few hours by the background watcher).",
            $"auto_update = {Toml(cc.AutoUpdate)}",
            "# macOS: copy each transcript from the Granola window while it's in front (Granola's API only shares",
            "# transcripts on paid plans). Needs Accessibility access for python3.12.",
            $"copy_transcripts = {Toml(cc.CopyTranscripts)}",
            $"mcp_url = {Toml(cc.McpUrl)}",
            $"oauth_callback_port = {Toml(cc.OauthCallbackPort)}",
            "# \"login\" always shows Granola's account picker when signing in, so the right account connects.",
            $"oauth_prompt = {Toml(cc.OauthPrompt)}",
        ];
        return string.Join("\n", lines) + "\n";
    }

    public static string Save(Config cfg)
    {
        Directory.CreateDirectory(cfg.Home);
        Py.WriteText(cfg.ConfigPath, Dump(cfg));
        Py.OwnerOnly(cfg.ConfigPath);
        return cfg.ConfigPath;
    }

    public static string SaveClient(ClientConfig cc)
    {
        Directory.CreateDirectory(cc.Home);
        Py.WriteText(cc.ConfigPath, DumpClient(cc));
        Py.OwnerOnly(cc.ConfigPath);
        return cc.ConfigPath;
    }

    /// <summary>`granola-share init`: a starter config.toml, if there is none.</summary>
    public static string WriteExample(string? home = null)
    {
        home = Py.ExpandUser(home ?? DefaultHome);
        string path = Path.Combine(home, "config.toml");
        if (!File.Exists(path))
        {
            Save(new Config(home, Py.ExpandUser("~/GranolaShare"))
            {
                PoolPassword = "change-me",
                Classes = [new ClassDef("Example 101", ["ex101"], "Replace me with a real class")],
            });
        }
        return path;
    }

    // --- reading -----------------------------------------------------------------------------------

    static TomlTable Read(string path) =>
        File.Exists(path) ? TomlSerializer.Deserialize<TomlTable>(new UTF8Encoding(false).GetString(File.ReadAllBytes(path)))! : new TomlTable();

    static TomlTable Section(TomlTable data, string key) => data.TryGetValue(key, out var v) && v is TomlTable t ? t : new TomlTable();

    public static Config Load(string? home = null)
    {
        home = Py.ExpandUser(home ?? DefaultHome);
        var data = Read(Path.Combine(home, "config.toml"));
        var ollama = Section(data, "ollama");
        var summary = Section(data, "summary");
        var classes = new List<ClassDef>();
        if (data.TryGetValue("classes", out var list) && list is IEnumerable items)
        {
            foreach (var item in items)
            {
                if (item is not TomlTable c || !c.TryGetValue("name", out var name))
                    throw new FormatException("config.toml: every [[classes]] needs a name");
                var aliases = c.TryGetValue("aliases", out var a) && a is TomlArray arr ? arr.Select(x => Str(x!)).ToList() : [];
                classes.Add(new ClassDef(Str(name), aliases, Str(Get(c, "description", ""))));
            }
        }
        return new Config(home, Py.ExpandUser(Str(Get(data, "pool_dir", "~/GranolaShare"))))
        {
            PoolName = Str(Get(data, "pool_name", "Lecture notes")),
            PollIntervalSeconds = Int(Get(data, "poll_interval_seconds", 300L)),
            WebHost = Str(Get(data, "web_host", "0.0.0.0")),
            WebPort = Int(Get(data, "web_port", 8787L)),
            PoolPassword = Str(Get(data, "pool_password", "")),
            AdminPassword = Str(Get(data, "admin_password", "")),
            McpUrl = Str(Get(data, "mcp_url", McpUrl)),
            OauthCallbackPort = Int(Get(data, "oauth_callback_port", 3334L)),
            OauthPrompt = Str(Get(data, "oauth_prompt", "login")),
            ServerSync = Bool(Get(data, "server_sync", false)),
            AutoUpdate = Bool(Get(data, "auto_update", true)),
            OllamaEnabled = Bool(Get(ollama, "enabled", true)),
            OllamaHost = Str(Get(ollama, "host", "http://localhost:11434")),
            OllamaModel = Str(Get(ollama, "model", "qwen3.6:35b-a3b")),
            MinConfidence = Float(Get(ollama, "min_confidence", 0.6)),
            IncludeTranscripts = Bool(Get(data, "include_transcripts", true)),
            SummaryEnabled = Bool(Get(summary, "enabled", true)),
            SummaryModel = Str(Get(summary, "model", "")),
            SummaryMaxContext = Int(Get(summary, "max_context", 32768L)),
            KeepGranolaNotes = Bool(Get(summary, "keep_granola_notes", false)),
            Classes = classes,
        };
    }

    public static ClientConfig LoadClient(string? home = null)
    {
        home = Py.ExpandUser(home ?? DefaultHome);
        var data = Read(Path.Combine(home, "client.toml"));
        return new ClientConfig(home)
        {
            ServerUrl = Str(Get(data, "server_url", "")),
            PoolKey = Str(Get(data, "pool_key", "")),
            PoolName = Str(Get(data, "pool_name", "")),
            DisplayName = Str(Get(data, "display_name", "")),
            Mode = Str(Get(data, "mode", "auto")),
            PollIntervalSeconds = Int(Get(data, "poll_interval_seconds", 180L)),
            IncludeTranscripts = Bool(Get(data, "include_transcripts", true)),
            ShareLookbackDays = Int(Get(data, "share_lookback_days", 7L)),
            DialogTimeoutSeconds = Int(Get(data, "dialog_timeout_seconds", 300L)),
            AutoUpdate = Bool(Get(data, "auto_update", true)),
            CopyTranscripts = Bool(Get(data, "copy_transcripts", false)),
            McpUrl = Str(Get(data, "mcp_url", McpUrl)),
            OauthCallbackPort = Int(Get(data, "oauth_callback_port", 3334L)),
            OauthPrompt = Str(Get(data, "oauth_prompt", "login")),
        };
    }

    static object Get(TomlTable t, string key, object fallback) => t.TryGetValue(key, out var v) && v is not null ? v : fallback;

    // Python's str(), int(), float(), and bool() on what tomllib returns, so a hand-edited file reads the same.

    static string Str(object v) => v switch
    {
        string s => s,
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Py.FloatRepr(d),
        TomlArray a => "[" + string.Join(", ", a.Select(x => x is string s ? Py.StrRepr(s) : Str(x!))) + "]",
        _ => v.ToString() ?? "",
    };

    static int Int(object v) => v switch
    {
        long l => checked((int)l),
        double d => checked((int)Math.Truncate(d)),
        bool b => b ? 1 : 0,
        string s => int.Parse(Py.Strip(s).Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"config: {v} is not a whole number"),
    };

    static double Float(object v) => v switch
    {
        double d => d,
        long l => l,
        bool b => b ? 1 : 0,
        string s => double.Parse(Py.Strip(s).Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"config: {v} is not a number"),
    };

    static bool Bool(object v) => v switch
    {
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };
}
