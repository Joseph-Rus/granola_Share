namespace StudyStash.Core.Tests;

/// <summary>tests/test_config.py, plus: both files come out byte for byte as the Python engine writes them.</summary>
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
            PoolName = "Fall \"26\" pool", PoolPassword = "s3cret", WebPort = 9000, ServerSync = true,
            OllamaModel = "qwen3:1.7b", MinConfidence = 0.7,
            Classes = [new ClassDef("CS 101", ["cs101", "intro"], "Intro to programming"), new ClassDef("Bio 110")],
        };
        Assert.Equal(dir["config.toml"], Configs.Save(cfg));
        var back = Configs.Load(dir.Path);
        Assert.Equal("Fall \"26\" pool", back.PoolName);
        Assert.Equal("s3cret", back.PoolPassword);
        Assert.Equal(9000, back.WebPort);
        Assert.True(back.ServerSync);
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
            ServerUrl = "http://mini:8787", PoolKey = "pw", PoolName = "Pool", DisplayName = "Sam", Mode = "auto",
            PollIntervalSeconds = 60,
        });
        var back = Configs.LoadClient(dir.Path);
        Assert.Equal("http://mini:8787", back.ServerUrl);
        Assert.Equal("pw", back.PoolKey);
        Assert.Equal("Sam", back.DisplayName);
        Assert.Equal(60, back.PollIntervalSeconds);
        Assert.Equal("Pool", back.PoolName);
        Assert.Equal(dir["tokens.json"], back.TokensPath);
    }

    [Fact]
    public void Missing_files_give_defaults()
    {
        using var dir = new TempDir();
        Assert.Equal(8787, Configs.Load(dir.Path).WebPort);
        Assert.Equal("auto", Configs.LoadClient(dir.Path).Mode);
        Assert.Equal(Py.ExpandUser("~/GranolaShare"), Configs.Load(dir.Path).PoolDir);
    }

    [Fact]
    public void Copying_transcripts_is_off_unless_turned_on()
    {
        using var dir = new TempDir();
        Assert.False(new ClientConfig(dir.Path).CopyTranscripts); // it automates the Granola app
        Configs.SaveClient(new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", CopyTranscripts = true });
        Assert.True(Configs.LoadClient(dir.Path).CopyTranscripts);
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
    public void Library_config_matches_python_byte_for_byte(string name)
    {
        using var dir = new TempDir();
        string golden = Native(Golden.Text(name));
        Assert.Equal(golden, Configs.Dump(Configs.Load(Home(dir, golden))));
    }

    [Fact]
    public void Laptop_config_matches_python_byte_for_byte()
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
