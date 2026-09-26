using System.IO.Compression;
using System.Runtime.InteropServices;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_update.py: finding and installing a release. The installs here are real (a real zip, unpacked
/// and swapped in on this computer) but of a stand-in engine in a scratch folder; every service command is a fake.</summary>
public class UpdaterTests
{
    static Release Rel(string tag, string? engineUrl = null, string windowsApp = "", string macApp = "") =>
        new(tag, Updates.ParseVersion(tag), "u", "p", MacApp: macApp, WindowsApp: windowsApp,
            Assets: engineUrl is null ? null : new Dictionary<string, string>
            {
                [Updates.EngineAsset("Darwin")] = engineUrl, [Updates.EngineAsset("Windows")] = engineUrl, [Updates.EngineAsset("Linux")] = engineUrl,
            });

    /// <summary>An installed engine: a folder with its marker.</summary>
    static string Installed(TempDir dir, string version = "0.4.4", string name = "engine")
    {
        string folder = dir[name];
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Updates.EngineMarker), version + "\n");
        return folder;
    }

    static byte[] Zip(Dictionary<string, string> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
            {
                var entry = zip.CreateEntry(name);
                if (!OperatingSystem.IsWindows()) entry.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
                using var w = new StreamWriter(entry.Open());
                w.Write(text);
            }
        return buffer.ToArray();
    }

    /// <summary>A stand-in engine: a script that answers `version`.</summary>
    static Dictionary<string, string> Engine(string version) => new()
    {
        ["studystash"] = $"#!/bin/sh\necho {version}\n", [Updates.EngineMarker] = version + "\n", ["lib/part.txt"] = "new",
    };

    [Fact]
    public void Each_computer_has_its_own_download()
    {
        Assert.Equal("Study-Stash-engine-mac-arm64.zip", Updates.EngineAsset("Darwin", Architecture.Arm64));
        Assert.Equal("Study-Stash-engine-windows-x64.zip", Updates.EngineAsset("Windows", Architecture.X64));
        Assert.Equal("Study-Stash-engine-linux-arm64.zip", Updates.EngineAsset("Linux", Architecture.Arm64));
    }

    [Fact]
    public async Task Auto_update_only_restarts_a_supervised_install()
    {
        using var dir = new TempDir();
        var host = new UpdateHost { System = "Darwin", Dir = Installed(dir) };
        var rel = Rel("v99.0.0");
        var logs = new List<string>();
        var applied = new List<(string, bool)>();
        var exits = new List<int>();
        Task<bool> Apply(Release r, string home, Action<string> log, bool restart)
        {
            applied.Add((r.Tag, restart));
            return Task.FromResult(true);
        }
        Task<Release?> Latest() => Task.FromResult<Release?>(rel);
        // run by hand in a terminal: only say so
        Assert.Equal("available", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, false, Latest, Apply, exits.Add));
        Assert.Empty(applied);
        Assert.Equal("[update] v99.0.0 is available: run `studystash update`.", logs.Single());
        // under launchd or systemd: install, then stop so the service manager starts the new version
        Assert.Equal("restarting", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true, Latest, Apply, exits.Add));
        Assert.Equal([("v99.0.0", false)], applied);
        Assert.Equal([0], exits);
        Assert.Equal("[update] now on v99.0.0; restarting", logs[^1]);
        Assert.False(File.Exists(Path.Combine(dir.Path, "update.lock")));
        // nothing newer
        Assert.Equal("up to date", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true,
            () => Task.FromResult<Release?>(Rel("v0.0.1")), Apply, exits.Add));
        Assert.Equal("up to date", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true, () => Task.FromResult<Release?>(null), Apply, exits.Add));
        Assert.StartsWith("check failed: ", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true,
            () => throw new HttpRequestException("offline"), Apply, exits.Add));
        // another process already installed it: only restart onto it
        File.WriteAllText(Path.Combine(host.Dir, Updates.EngineMarker), "99.0.0");
        Assert.Equal("restarting", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true, Latest, Apply, exits.Add));
        Assert.Single(applied);
        // a failed install keeps this version running
        File.WriteAllText(Path.Combine(host.Dir, Updates.EngineMarker), "0.4.4");
        Assert.Equal("install failed", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true, Latest,
            (_, _, _, _) => Task.FromResult(false), exits.Add));
        Assert.Equal(2, exits.Count);
    }

    [Fact]
    public async Task A_build_folder_is_never_updated_and_says_so()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir["build"]);
        var host = new UpdateHost { System = "Darwin", Dir = dir["build"] };
        Assert.Equal("available", await Updates.CheckAndUpdateAsync(dir.Path, host, _ => { }, true, () => Task.FromResult<Release?>(Rel("v99.0.0")),
            (_, _, _, _) => throw new InvalidOperationException("must not install"), _ => throw new InvalidOperationException("must not stop")));
        var said = new List<string>();
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", "https://dl/e.zip"), dir["home"], host, said.Add));
        Assert.Contains("git pull", said.Single());
    }

    [Fact]
    public async Task A_bare_host_changes_nothing()
    {
        using var dir = new TempDir();
        var said = new List<string>();
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", "https://dl/e.zip"), dir["home"], new UpdateHost(), said.Add));
        Assert.Equal("No install folder was given, so there's nothing to update.", said.Single());
        var bare = new UpdateHost();
        Assert.Throws<InvalidOperationException>(() => bare.Run("launchctl", [], TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => bare.SpawnDetached(["cmd"]));
        Assert.Throws<InvalidOperationException>(() => bare.VersionOf("studystash"));
        Assert.Null(bare.AppsAt);
        var real = UpdateHost.ThisComputer();
        Assert.Equal(Updates.InstallDir, real.Dir);
        Assert.NotNull(real.AppsAt);
    }

    [Fact]
    public async Task Auto_update_waits_for_another_updater()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "update.lock"), "123"); // the Python engine takes the same file
        var host = new UpdateHost { System = "Darwin", Dir = Installed(dir) };
        Assert.Equal("another update is running", await Updates.CheckAndUpdateAsync(dir.Path, host, _ => { }, true,
            () => Task.FromResult<Release?>(Rel("v99.0.0")), (_, _, _, _) => throw new InvalidOperationException("must not install twice"), _ => { }));
        File.SetLastWriteTimeUtc(Path.Combine(dir.Path, "update.lock"), DateTime.UtcNow.AddHours(-1)); // left by a crash: stale
        Assert.Equal("restarting", await Updates.CheckAndUpdateAsync(dir.Path, host, _ => { }, true,
            () => Task.FromResult<Release?>(Rel("v99.0.0")), (_, _, _, _) => Task.FromResult(true), _ => { }));
    }

    [Fact]
    public async Task Windows_auto_update_hands_off_to_the_helper()
    {
        using var dir = new TempDir();
        var host = new UpdateHost { System = "Windows", Dir = Installed(dir) };
        bool? restart = null;
        var exits = new List<int>();
        Assert.Equal("handed off", await Updates.CheckAndUpdateAsync(dir.Path, host, _ => { }, true, () => Task.FromResult<Release?>(Rel("v99.0.0")),
            (_, _, _, r) => { restart = r; return Task.FromResult(true); }, exits.Add));
        Assert.True(restart);
        Assert.Empty(exits);
    }

    [Fact]
    public async Task A_release_without_this_computers_download_waits_for_the_next()
    {
        using var dir = new TempDir();
        var said = new List<string>();
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.8"), dir["home"], new UpdateHost { System = "Linux", Dir = Installed(dir) }, said.Add));
        Assert.Equal($"v9.9.8 has no {Updates.EngineAsset("Linux")} download yet; the next check tries again.", said.Single());
    }

    /// <summary>A service file for this role that runs the engine in `folder`, as Autostart writes it.</summary>
    static void Service(ServicePlaces places, string system, string role, string exe)
    {
        string path = Autostart.ServicePath(role, places, system);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<string>{exe}</string>");
    }

    [Fact]
    public async Task An_update_swaps_the_engine_folder_and_restarts_its_services()
    {
        if (OperatingSystem.IsWindows()) return; // a shell script stands in for the engine
        using var dir = new TempDir();
        string system = OperatingSystem.IsMacOS() ? "Darwin" : "Linux";
        string folder = Installed(dir);
        File.WriteAllText(Path.Combine(folder, "studystash"), "#!/bin/sh\necho 0.4.4\n");
        File.WriteAllText(Path.Combine(folder, "stale.txt"), "old");
        var places = new ServicePlaces(dir["agents"], dir["startup"], dir["systemd"]);
        Service(places, system, "server", Path.Combine(folder, "studystash"));
        var downloads = new FakeDownloads(new() { ["https://dl/e.zip"] = Zip(Engine("9.9.9")) });
        // Unpacking is real (ditto on a Mac); anything that would touch a service is only written down.
        var services = new FakeRunner();
        Runner run = (exe, args, t) => exe == "ditto" ? Machine.Run(exe, args, t) : services.Run(exe, args, t);
        var host = new UpdateHost { System = system, Dir = folder, Http = downloads.Client(), Run = run, Places = places, VersionOf = Updates.VersionOf };
        var said = new List<string>();
        Assert.True(await Updates.ApplyAsync(Rel("v9.9.9", "https://dl/e.zip"), dir["home"], host, said.Add));
        Assert.Equal("9.9.9", Updates.VersionOf(Path.Combine(folder, "studystash")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(folder, "lib", "part.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "stale.txt"))); // the whole folder is replaced
        Assert.False(Directory.Exists(folder + ".new"));
        Assert.False(Directory.Exists(folder + ".old"));
        Assert.Equal("9.9.9", Updates.InstalledVersion(folder));
        Assert.Equal(["Installing v9.9.9...", "Installed v9.9.9.", "Restarted the server service."], said);
        var restarted = Assert.Single(services.Calls);
        Assert.Contains(system == "Darwin" ? "kickstart" : "restart", restarted);
    }

    [Fact]
    public async Task A_download_that_doesnt_run_here_leaves_this_version_alone()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        string system = OperatingSystem.IsMacOS() ? "Darwin" : "Linux";
        string folder = Installed(dir);
        File.WriteAllText(Path.Combine(folder, "studystash"), "keep");
        var downloads = new FakeDownloads(new()
        {
            ["https://dl/wrong.zip"] = Zip(Engine("1.0.0")), // says it's another version
            ["https://dl/empty.zip"] = Zip(new() { ["readme.txt"] = "no engine in here" }),
        });
        var host = new UpdateHost
        {
            System = system, Dir = folder, Http = downloads.Client(), Run = Machine.Run, Places = new ServicePlaces(dir["a"], dir["b"], dir["c"]),
            VersionOf = Updates.VersionOf,
        };
        var said = new List<string>();
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", "https://dl/wrong.zip"), dir["home"], host, said.Add));
        Assert.Equal("The v9.9.9 download didn't run on this computer, so this version stays.", said[^1]);
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", "https://dl/empty.zip"), dir["home"], host, said.Add));
        Assert.Equal("Download failed: the download has no studystash", said[^1]);
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", "https://dl/gone.zip"), dir["home"], host, said.Add));
        Assert.StartsWith("Download failed: ", said[^1]);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "studystash")));
        Assert.False(Directory.Exists(folder + ".new"));
    }

    [Fact]
    public async Task Windows_stages_the_new_folder_and_a_helper_swaps_it_in()
    {
        using var dir = new TempDir();
        string folder = Installed(dir);
        var places = new ServicePlaces(dir["agents"], dir["startup"], dir["systemd"]);
        Service(places, "Windows", "server", Updates.EngineExe(folder, "Windows"));
        var appZip = Zip(new() { ["Study Stash.exe"] = "app v9", ["web/app.css"] = "css" });
        var engineZip = Zip(new() { ["studystash.exe"] = "exe", [Updates.EngineMarker] = "9.9.9\n" });
        var downloads = new FakeDownloads(new() { ["https://dl/e.zip"] = engineZip, ["https://dl/app.zip"] = appZip });
        var at = new AppPlaces(dir["Applications"], dir["mine"], dir["Local"], dir["Roaming"], dir["userhome"]);
        string appFolder = Apps.WindowsAppDir(Apps.LibraryAppName, at);
        Directory.CreateDirectory(appFolder);
        File.WriteAllText(Path.Combine(appFolder, "Study Stash.exe"), "app v8");
        var spawned = new List<IReadOnlyList<string>>();
        var host = new UpdateHost
        {
            System = "Windows", Dir = folder, Http = downloads.Client(), Run = new FakeRunner().Run, Places = places, AppsAt = at,
            VersionOf = exe => File.Exists(exe) ? "9.9.9" : null, SpawnDetached = spawned.Add,
        };
        var said = new List<string>();
        string home = dir["home"];
        Assert.True(await Updates.ApplyAsync(Rel("v9.9.9", "https://dl/e.zip", windowsApp: "https://dl/app.zip"), home, host, said.Add));
        Assert.Equal("exe", File.ReadAllText(Path.Combine(folder + ".new", "studystash.exe"))); // beside the one in use
        Assert.True(File.Exists(Path.Combine(folder, Updates.EngineMarker)));
        Assert.Equal("app v9", File.ReadAllText(Path.Combine(appFolder, "Study Stash.exe"))); // the app updates too
        Assert.Equal("css", File.ReadAllText(Path.Combine(appFolder, "web", "app.css")));
        string script = File.ReadAllText(Path.Combine(home, "update.cmd"));
        Assert.Equal(["cmd", "/c", Path.Combine(home, "update.cmd")], spawned.Single());
        Assert.StartsWith("@echo off\r\ntimeout /t 5 /nobreak >nul\r\n", script);
        Assert.Contains($"move \"{folder}\" \"{folder}.old\"", script);
        Assert.Contains($"move \"{folder}.new\" \"{folder}\"", script);
        Assert.Contains($"if not exist \"{folder}\" move \"{folder}.old\" \"{folder}\"", script); // put back if the move failed
        Assert.Contains($"\"{Updates.EngineExe(folder, "Windows")}\" --home \"{home}\" autostart install --role server", script);
        Assert.Contains("$_.ExecutablePath.StartsWith(", script); // stops only what runs from this folder
        Assert.Equal($"Installing v9.9.9 in the background (log: {Path.Combine(home, "logs", "update.log")}).", said[^1]);
    }

    [Fact]
    public async Task The_mac_app_is_replaced_wherever_it_used_to_be()
    {
        using var dir = new TempDir();
        var at = new AppPlaces(dir["Applications"], dir["mine"], dir["Local"], dir["Roaming"], dir["userhome"]);
        Directory.CreateDirectory(dir["Applications"]); // this account can write there, like a real /Applications
        string old = Path.Combine(dir["mine"], "Study Stash.app");
        Directory.CreateDirectory(Path.Combine(old, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(old, "Contents", "MacOS", "Study Stash"), "old");
        Assert.Equal(old, Apps.NativeInstalled(at));
        var downloads = new FakeDownloads(new() { ["https://dl/mac.zip"] = [1, 2, 3] });
        var ditto = new FakeRunner((_, args) =>
        {
            string exe = Path.Combine(args[^1], "Study Stash.app", "Contents", "MacOS", "Study Stash");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "new");
            return new ProcResult(0, "");
        });
        var said = new List<string>();
        string? dest = await Apps.InstallNativeAsync("https://dl/mac.zip", said.Add, downloads.Client(), ditto.Run, at: at);
        Assert.Equal(Path.Combine(dir["Applications"], "Study Stash.app"), dest); // this account can write there
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest!, "Contents", "MacOS", "Study Stash")));
        Assert.False(Directory.Exists(old));
        Assert.Equal($"Installed the Study Stash app in {dir["Applications"]}.", said.Single());
        var broken = new FakeRunner((_, _) => new ProcResult(0, ""));
        Assert.Null(await Apps.InstallNativeAsync("https://dl/mac.zip", said.Add, downloads.Client(), broken.Run, at: at));
        Assert.Equal("The Study Stash app in that release didn't unpack; keeping the one you have.", said[^1]);
        Assert.True(Directory.Exists(dest)); // still there
    }
}
