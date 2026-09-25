using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>
/// Making this computer the library: its config, its folder of notes, and its service, started now and at every
/// login; then the laptop side points at it. What changes this computer goes through <see cref="Machine"/>-like
/// hooks, so a test does it in a folder of its own.
/// </summary>
public sealed class LibraryHere
{
    public ServicePlaces Places { get; init; } = ServicePlaces.Default;
    public Runner Run { get; init; } = (_, _, _) => throw new InvalidOperationException("Installing the library's service is off here.");
    public IReadOnlyList<string>? Engine { get; init; }
    public Func<Config, Task<bool>> WaitHealthy { get; init; } = cfg => HostInfo.WaitForServerAsync(cfg);

    public static LibraryHere ThisComputer() => new() { Run = Machine.Run, Engine = [Platform.Desktop.Program] };

    /// <summary>The folder a new library keeps its notes in: Documents/Study Stash.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Study Stash");

    public async Task<string> CreateAsync(string home, string name, string password, string displayName)
    {
        name = name.Trim().Length > 0 ? name.Trim() : "My library";
        if (password.Trim().Length < 4) throw new ArgumentException("Use a password of at least 4 characters.");
        var cfg = Configs.Load(home);
        if (!File.Exists(cfg.ConfigPath)) cfg.PoolDir = DefaultFolder;
        cfg.PoolName = name;
        cfg.PoolPassword = password.Trim();
        if (cfg.AdminPassword.Length == 0) cfg.AdminPassword = StudyStash.Library.Http.TokenUrlSafe(12);
        Directory.CreateDirectory(cfg.PoolDir);
        Configs.Save(cfg);
        Autostart.Install("server", home, Places, Run, Engine);
        if (!await WaitHealthy(cfg)) throw new InvalidOperationException("The library didn't start. Its log is in the logs folder.");
        var cc = Configs.LoadClient(home);
        cc.ServerUrl = $"http://127.0.0.1:{cfg.WebPort}";
        cc.PoolKey = cfg.PoolPassword;
        cc.PoolName = cfg.PoolName;
        if (cc.DisplayName.Length == 0) cc.DisplayName = displayName;
        Configs.SaveClient(cc);
        return $"{name} is ready on this computer.";
    }
}
