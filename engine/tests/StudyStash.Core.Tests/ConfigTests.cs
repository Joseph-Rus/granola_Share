namespace StudyStash.Core.Tests;

/// <summary>
/// The library's config.toml and the laptop's client.toml: they come back byte for byte, a file from before the rename
/// still loads, and the home folder is the one an install already has.
/// </summary>
public class ConfigTests
{
    /// <summary>On Windows, Python spells "/srv/notes" as "\srv\notes" too.</summary>
    static string Native(string toml) => OperatingSystem.IsWindows()
        ? string.Join("\n", toml.Split('\n').Select(l => l.StartsWith("pool_dir = ")
            ? "pool_dir = \"" + Py.NormPath(l[12..^1]).Replace("\\", "\\\\") + "\"" : l))
        : toml;

    static string Home(TempDir dir, string toml)
    {
        File.WriteAllText(dir["config.toml"], toml);
        return dir.Path;
    }

    [Fact]
    public void Server_config_roundtrip()
    {
        using var dir = new TempDir();
        var cfg = new Config(dir.Path, dir["pool"])
        {
            PoolName = "Fall \"26\" pool", PoolPassword = "s3cret", WebPort = 9000, AutoUpdate = false,
            OllamaModel = "qwen3:1.7b", MinConfidence = 0.7,
            Classes = [new ClassDef("CS 101", ["cs101", "intro"], "Intro to programming"), new ClassDef("Bio 110")],
        };
        Assert.Equal(dir["config.toml"], Configs.Save(cfg));
        var back = Configs.Load(dir.Path);
        Assert.Equal("Fall \"26\" pool", back.PoolName);
        Assert.Equal("s3cret", back.PoolPassword);
        Assert.Equal(9000, back.WebPort);
        Assert.False(back.AutoUpdate);
        Assert.Equal("qwen3:1.7b", back.OllamaModel);
        Assert.Equal(0.7, back.MinConfidence);
        Assert.Equal(["CS 101", "Bio 110"], back.ClassNames());
        Assert.Equal(["cs101", "intro"], back.Classes[0].Aliases);
        Assert.Equal("Intro to programming", back.Classes[0].Description);
        Assert.Empty(back.Classes[1].Aliases);
        Assert.Contains("[[classes]]", Configs.Dump(cfg));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dir["config.toml"]));
    }

    [Fact]
    public void Client_config_roundtrip()
    {
        using var dir = new TempDir();
        Configs.SaveClient(new ClientConfig(dir.Path)
        {
            ServerUrl = "http://mini:8787", PoolKey = "pw", PoolName = "Pool", DisplayName = "Sam", AutoUpdate = false,
        });
        var back = Configs.LoadClient(dir.Path);
        Assert.Equal("http://mini:8787", back.ServerUrl);
        Assert.Equal("pw", back.PoolKey);
        Assert.Equal("Sam", back.DisplayName);
        Assert.False(back.AutoUpdate);
        Assert.Equal("Pool", back.PoolName);
        Assert.Equal(dir["client.toml"], back.ConfigPath);
    }

    [Fact]
    public void Missing_files_give_defaults()
    {
        using var dir = new TempDir();
        Assert.Equal(8787, Configs.Load(dir.Path).WebPort);
        Assert.Equal("", Configs.LoadClient(dir.Path).ServerUrl);
        Assert.Equal(Configs.DefaultPoolDir, Configs.Load(dir.Path).PoolDir);
        Assert.EndsWith("Study Stash", Configs.DefaultPoolDir);
    }

    [Fact]
    public void A_starter_config_says_study_stash_and_keeps_lectures_in_documents()
    {
        using var dir = new TempDir();
        string path = Configs.WriteExample(dir.Path);
        Assert.StartsWith("# Study Stash library settings.", File.ReadAllText(path));
        Assert.Equal(Configs.DefaultPoolDir, Configs.Load(dir.Path).PoolDir);
        Assert.Equal(["Example 101"], Configs.Load(dir.Path).ClassNames());
    }

    [Fact]
    public void An_old_config_still_loads_and_saves_without_granola_keys()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir["config.toml"], Golden.Text("config-0.4.toml"));
        File.WriteAllText(dir["client.toml"], Golden.Text("client-0.4.toml"));

        var cfg = Configs.Load(dir.Path);
        Assert.Equal("Fall \"26\" \u2014 Caf\u00e9 \\ notes", cfg.PoolName);
        Assert.Equal(Py.NormPath("/srv/Lecture notes"), cfg.PoolDir);
        Assert.Equal("maple-otter", cfg.PoolPassword);
        Assert.Equal(9000, cfg.WebPort);
        Assert.Equal("qwen3:1.7b", cfg.OllamaModel);
        Assert.Equal("gemma4:e4b", cfg.SummaryModel);
        Assert.Equal(16384, cfg.SummaryMaxContext);
        Assert.Equal(["CS 101", "Bio \U0001f9ec 110", "Calc II"], cfg.ClassNames());
        Assert.Equal(["cs101", "intro programming"], cfg.Classes[0].Aliases);

        var cc = Configs.LoadClient(dir.Path);
        Assert.Equal("http://mini.example.ts.net:8787", cc.ServerUrl);
        Assert.Equal("tulip 2027", cc.PoolKey);
        Assert.Equal("Fall", cc.PoolName);
        Assert.Equal("Sam \u2615", cc.DisplayName);

        // The next save is today's file, with the same settings and none of the old keys.
        string saved = Configs.Dump(cfg), savedClient = Configs.DumpClient(cc);
        Assert.Equal(Native(Golden.Text("config.toml")), saved);
        Assert.Equal(Golden.Text("client.toml"), savedClient);
        foreach (string gone in (string[])["granola", "mcp_url", "oauth", "server_sync", "copy_transcripts", "keep_granola_notes"])
        {
            Assert.DoesNotContain(gone, saved, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(gone, savedClient, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Home_is_study_stash_unless_only_the_old_folder_exists()
    {
        using var dir = new TempDir();
        static string? None(string _) => null;
        string home = Py.NormPath(dir[".study-stash"]), before = Py.NormPath(dir[".granola-share"]);

        Assert.Equal(home, Configs.HomeFor(None, dir.Path));
        Directory.CreateDirectory(before);
        Assert.Equal(before, Configs.HomeFor(None, dir.Path)); // an install from before the rename keeps its lectures
        Directory.CreateDirectory(home);
        Assert.Equal(home, Configs.HomeFor(None, dir.Path));

        // It only looks: nothing is created, moved or deleted.
        Directory.Delete(home);
        Directory.Delete(before);
        Assert.Equal(home, Configs.HomeFor(None, dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void Either_variable_sets_the_home_folder()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir[".granola-share"]);
        Func<string, string?> Env(params (string Name, string Value)[] set) => name => set.FirstOrDefault(v => v.Name == name).Value;
        string mine = Py.NormPath(dir["mine"]), old = Py.NormPath(dir["old"]);

        Assert.Equal(mine, Configs.HomeFor(Env(("STUDYSTASH_HOME", mine), ("GRANOLA_SHARE_HOME", old)), dir.Path));
        Assert.Equal(mine, Configs.HomeFor(Env(("STUDYSTASH_HOME", mine)), dir.Path));
        Assert.Equal(old, Configs.HomeFor(Env(("GRANOLA_SHARE_HOME", old)), dir.Path));
        // Set but empty counts as not set.
        Assert.Equal(old, Configs.HomeFor(Env(("STUDYSTASH_HOME", ""), ("GRANOLA_SHARE_HOME", old)), dir.Path));
        Assert.Equal(Py.NormPath(dir[".granola-share"]), Configs.HomeFor(Env(("STUDYSTASH_HOME", ""), ("GRANOLA_SHARE_HOME", "")), dir.Path));
        Assert.False(Directory.Exists(mine));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Emoji_and_accents_survive_a_save()
    {
        using var dir = new TempDir();
        Configs.Save(new Config(dir.Path, dir["pool"])
        {
            PoolName = "Café \"notes\" \\ 2026", Classes = [new ClassDef("Bio 🧬", ["genetics — intro"])],
        });
        Assert.Contains("name = \"Bio \\U0001f9ec\"", File.ReadAllText(dir["config.toml"]));
        var back = Configs.Load(dir.Path);
        Assert.Equal(["Bio 🧬"], back.ClassNames());
        Assert.Equal(["genetics — intro"], back.Classes[0].Aliases);
        Assert.Equal("Café \"notes\" \\ 2026", back.PoolName);
    }

    [Theory]
    [InlineData("config.toml")]
    [InlineData("config-default.toml")]
    public void Library_config_comes_back_byte_for_byte(string name)
    {
        using var dir = new TempDir();
        string golden = Native(Golden.Text(name));
        Assert.Equal(golden, Configs.Dump(Configs.Load(Home(dir, golden))));
    }

    [Fact]
    public void Laptop_config_comes_back_byte_for_byte()
    {
        using var dir = new TempDir();
        string golden = Golden.Text("client.toml");
        File.WriteAllText(dir["client.toml"], golden);
        Assert.Equal(golden, Configs.DumpClient(Configs.LoadClient(dir.Path)));
    }

    [Fact]
    public void A_hand_edited_file_reads_as_python_reads_it()
    {
        // Wrong types, doubled slashes, "false" in quotes (which Python reads as true): all as Python does.
        using var dir = new TempDir();
        var cfg = Configs.Load(Home(dir, Golden.Text("config-hand.toml")));
        Assert.Equal(Native(Golden.Text("config-hand-saved.toml")), Configs.Dump(cfg));
        Assert.True(cfg.OllamaEnabled);
        Assert.Equal(8192, cfg.SummaryMaxContext);
    }

    [Fact]
    public void The_same_model_writes_and_sorts_unless_one_is_chosen()
    {
        var cfg = new Config("/h", "/p") { OllamaModel = "small:1b" };
        Assert.Equal("small:1b", cfg.EffectiveSummaryModel);
        cfg.SummaryModel = "big:35b";
        Assert.Equal("big:35b", cfg.EffectiveSummaryModel);
    }
}
