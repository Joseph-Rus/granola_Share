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
    public string WebHost { get; set; } = "0.0.0.0";
    public int WebPort { get; set; } = 8787;
    public string PoolPassword { get; set; } = "";
    public string AdminPassword { get; set; } = ""; // from 0.2; the password above does the same now
    public bool AutoUpdate { get; set; } = true;
    public bool OllamaEnabled { get; set; } = true;
    public string OllamaHost { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen3.6:35b-a3b"; // sorts lectures into classes
    public double MinConfidence { get; set; } = 0.6;
    public bool SummaryEnabled { get; set; } = true; // write study notes from each lecture's transcript
    public string SummaryModel { get; set; } = ""; // blank = the sorting model
    public int SummaryMaxContext { get; set; } = 32768;
    public List<ClassDef> Classes { get; set; } = [];

    public string EffectiveSummaryModel => SummaryModel.Length > 0 ? SummaryModel : OllamaModel;
    public string DbPath => Path.Combine(Home, "state.db");
    public string ConfigPath => Path.Combine(Home, "config.toml");
    public string LogDir => Path.Combine(Home, "logs");

    public List<string> ClassNames() => Classes.Select(c => c.Name).ToList();
}

/// <summary>The laptop you record on: which library its lectures go to. Lives in client.toml.</summary>
public sealed class ClientConfig(string home)
{
    public string Home { get; set; } = Py.NormPath(home);
    public string ServerUrl { get; set; } = "";
    public string PoolKey { get; set; } = "";
    public string PoolName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool AutoUpdate { get; set; } = true;

    public string ConfigPath => Path.Combine(Home, "client.toml");
    public string LogDir => Path.Combine(Home, "logs");
}

/// <summary>
/// Reading and writing config.toml and client.toml. A file an older version wrote still loads: the keys Study Stash
/// no longer uses are ignored, and the next save leaves them out.
/// </summary>
public static class Configs
{
    public const string Unsorted = "Unsorted";

    /// <summary>Where settings, recordings and the library live on this computer.</summary>
    public static string DefaultHome => HomeFor(Environment.GetEnvironmentVariable, Py.UserHome());

    /// <summary>
    /// STUDYSTASH_HOME, or GRANOLA_SHARE_HOME (what installs from before the rename set), else ~/.study-stash; but an
    /// install from before the rename that only has ~/.granola-share keeps using it, so its lectures, passwords and
    /// service stay where they are. Only looks: it never creates, moves or deletes a folder.
    /// </summary>
    internal static string HomeFor(Func<string, string?> env, string userHome)
    {
        foreach (string name in (string[])["STUDYSTASH_HOME", "GRANOLA_SHARE_HOME"])
            if (env(name) is { Length: > 0 } set) return Py.ExpandUser(set);
        string home = Path.Combine(userHome, ".study-stash"), before = Path.Combine(userHome, ".granola-share");
        return Py.NormPath(!Directory.Exists(home) && Directory.Exists(before) ? before : home);
    }

    /// <summary>The folder a new library keeps its lectures in: Documents/Study Stash.</summary>
    public static string DefaultPoolDir
    {
        get
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Py.NormPath(Path.Combine(docs.Length > 0 ? docs : Path.Combine(Py.UserHome(), "Documents"), "Study Stash"));
        }
    }

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
            "# Study Stash library settings. Edit freely, or change them on the library's Settings page.",
            $"pool_name = {Toml(cfg.PoolName)}",
            $"pool_dir = {Toml(cfg.PoolDir)}",
            $"web_host = {Toml(cfg.WebHost)}",
            $"web_port = {Toml(cfg.WebPort)}",
            "# Your laptop and browser use this. Blank = no login (only safe if nothing else can reach this computer).",
            $"pool_password = {Toml(cfg.PoolPassword)}",
            "# From 0.2: a second password that also logs in to the web UI. Not needed any more.",
            $"admin_password = {Toml(cfg.AdminPassword)}",
            "# Install new releases automatically (checked every few hours by the background service).",
            $"auto_update = {Toml(cfg.AutoUpdate)}",
            "",
            "[ollama]",
            $"enabled = {Toml(cfg.OllamaEnabled)}",
            $"host = {Toml(cfg.OllamaHost)}",
            "# Model that sorts lectures into classes.",
            $"model = {Toml(cfg.OllamaModel)}",
            "# Below this confidence a lecture goes to Unsorted for you to file.",
            $"min_confidence = {Toml(cfg.MinConfidence)}",
            "",
            "[summary]",
            "# Write study notes from each lecture's transcript.",
            $"enabled = {Toml(cfg.SummaryEnabled)}",
            "# Ollama model that writes the notes. Blank = same as the sorting model.",
            $"model = {Toml(cfg.SummaryModel)}",
            "# Largest context (tokens) to ask for; longer transcripts are summarized in parts, then merged.",
            $"max_context = {Toml(cfg.SummaryMaxContext)}",
            "",
            "# Classes lectures are sorted into. A lecture recorded for a class, or whose title matches a name or alias,",
            "# is filed before the model is asked.",
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
            "# Study Stash laptop settings. Change them in the Study Stash app.",
            $"server_url = {Toml(cc.ServerUrl)}",
            $"pool_key = {Toml(cc.PoolKey)}",
            $"pool_name = {Toml(cc.PoolName)}",
            $"display_name = {Toml(cc.DisplayName)}",
            "# Install new releases of Study Stash automatically.",
            $"auto_update = {Toml(cc.AutoUpdate)}",
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

    /// <summary>`studystash init`: a starter config.toml, if there is none.</summary>
    public static string WriteExample(string? home = null)
    {
        home = Py.ExpandUser(home ?? DefaultHome);
        string path = Path.Combine(home, "config.toml");
        if (!File.Exists(path))
        {
            Save(new Config(home, DefaultPoolDir)
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
        return new Config(home, Py.ExpandUser(Str(Get(data, "pool_dir", DefaultPoolDir))))
        {
            PoolName = Str(Get(data, "pool_name", "Lecture notes")),
            WebHost = Str(Get(data, "web_host", "0.0.0.0")),
            WebPort = Int(Get(data, "web_port", 8787L)),
            PoolPassword = Str(Get(data, "pool_password", "")),
            AdminPassword = Str(Get(data, "admin_password", "")),
            AutoUpdate = Bool(Get(data, "auto_update", true)),
            OllamaEnabled = Bool(Get(ollama, "enabled", true)),
            OllamaHost = Str(Get(ollama, "host", "http://localhost:11434")),
            OllamaModel = Str(Get(ollama, "model", "qwen3.6:35b-a3b")),
            MinConfidence = Float(Get(ollama, "min_confidence", 0.6)),
            SummaryEnabled = Bool(Get(summary, "enabled", true)),
            SummaryModel = Str(Get(summary, "model", "")),
            SummaryMaxContext = Int(Get(summary, "max_context", 32768L)),
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
            AutoUpdate = Bool(Get(data, "auto_update", true)),
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
