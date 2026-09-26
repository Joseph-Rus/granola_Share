using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace StudyStash.Core.Tests;

/// <summary>Finding and installing a release (D2, D4): the right installer by system and role, its checksum, and
/// actually swapping it in. The Mac installs here are real (real hdiutil/ditto/codesign) of stand-in bundles in a
/// scratch folder; everything that would touch a real service is a fake.</summary>
public class UpdaterTests
{
    static Release Rel(string tag, Dictionary<string, string>? assets = null) => new(tag, Updates.ParseVersion(tag), "u", "p", assets);

    static readonly UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>A Mac bundle's Info.plist with just the keys this file's tests read.</summary>
    static string Plist(string version, string? role = null) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleIdentifier</key><string>com.study-stash.app</string>
            <key>CFBundleShortVersionString</key><string>{version}</string>
            {(role is null ? "" : $"<key>StudyStashRole</key><string>{role}</string>")}
        </dict>
        </plist>
        """;

    static string ArgAfter(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    static string Sha256Sums(string name, byte[] bytes) => $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {name}\n";

    [Fact]
    public void Installer_names_come_from_the_system_and_role()
    {
        Assert.Equal("Study-Stash-Laptop.dmg", Updates.Installer("Darwin", "laptop"));
        Assert.Equal("Study-Stash-Library.dmg", Updates.Installer("Darwin", "library"));
        Assert.Equal("Study-Stash-Laptop-Setup.exe", Updates.Installer("Windows", "laptop"));
        Assert.Equal("Study-Stash-Library-Setup.exe", Updates.Installer("Windows", "library"));
        Assert.Null(Updates.Installer("Linux", "laptop"));
        Assert.Null(Updates.Installer("Linux", "library"));
        Assert.Null(Updates.Installer("Darwin", null));
        Assert.Null(Updates.Installer("Darwin", "unknown"));
        Assert.Equal(4, Updates.Installers.Count);
        Assert.Equal(4, Updates.Installers.Distinct().Count());
    }

    [Fact]
    public async Task The_newest_release_lists_the_four_installers_and_their_checksums()
    {
        var github = new FakeOllama((path, _) => path == "/repos/Joseph-Rus/study-stash/releases/latest" ? System.Text.Json.Nodes.JsonNode.Parse("""
            {"tag_name": "v0.5.0", "html_url": "https://github.com/Joseph-Rus/study-stash/releases/tag/v0.5.0",
             "assets": [{"name": "Study-Stash-Laptop.dmg", "browser_download_url": "https://dl/laptop.dmg"},
                        {"name": "Study-Stash-Library.dmg", "browser_download_url": "https://dl/library.dmg"},
                        {"name": "Study-Stash-Laptop-Setup.exe", "browser_download_url": "https://dl/laptop.exe"},
                        {"name": "Study-Stash-Library-Setup.exe", "browser_download_url": "https://dl/library.exe"},
                        {"name": "SHA256SUMS.txt", "browser_download_url": "https://dl/sums.txt"}]}
            """)! : (System.Net.HttpStatusCode.NotFound, "{}"));
        var rel = await Updates.LatestAsync(github.Client());
        Assert.Equal("v0.5.0", rel!.Tag);
        Assert.Equal([0, 5, 0], rel.Version);
        Assert.Equal("https://github.com/Joseph-Rus/study-stash/archive/refs/tags/v0.5.0.tar.gz", rel.Url);
        foreach (string name in Updates.Installers) Assert.True(rel.Assets!.ContainsKey(name), name);
        Assert.True(rel.Assets!.ContainsKey(Updates.ChecksumsAsset));
        var none = new FakeOllama((_, _) => (System.Net.HttpStatusCode.NotFound, "{}"));
        Assert.Null(await Updates.LatestAsync(none.Client()));
    }

    [Fact]
    public void Checksums_parse_shasum_output()
    {
        var map = Checksums.Parse("ABCDEF  Study-Stash-Laptop.dmg\r\ndeadBEEF *Study-Stash-Library.dmg\n\nnot a valid checksum line at all\n");
        Assert.Equal("abcdef", map["Study-Stash-Laptop.dmg"]);
        Assert.Equal("deadbeef", map["Study-Stash-Library.dmg"]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void A_build_folder_is_never_updated_and_says_so()
    {
        using var t = new TempDir();
        string bin = Path.Combine(t.Path, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);
        Assert.Equal("This copy runs from a build folder, so it doesn't update itself.",
            Updates.WhyNotUpdatable(UpdateHost.ThisComputer(bin, "Darwin")));
        Assert.Equal("This copy runs from a build folder, so it doesn't update itself.",
            Updates.WhyNotUpdatable(UpdateHost.ThisComputer(t.Path, "Windows")));
    }

    [Fact]
    public void A_translocated_bundle_is_told_to_move_to_applications()
    {
        using var t = new TempDir();
        string contents = Path.Combine(t.Path, "AppTranslocation", "D1E2F3", "d", "Study Stash.app", "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS", "arm64"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), Plist("0.4.4", "laptop"));
        var host = UpdateHost.ThisComputer(Path.Combine(contents, "MacOS", "arm64"), "Darwin");
        Assert.Equal("", host.AppDir);
        Assert.Equal("Move Study Stash into Applications, then open it again, so it can update itself.", Updates.WhyNotUpdatable(host));
    }

    [Fact]
    public void An_unwritable_bundle_folder_cant_update_itself()
    {
        if (OperatingSystem.IsWindows()) return; // Machine.Writable's probe write isn't blockable this way there
        using var t = new TempDir();
        string parent = t["locked"];
        string contents = Path.Combine(parent, "Study Stash.app", "Contents");
        Directory.CreateDirectory(Path.Combine(contents, "MacOS", "arm64"));
        File.WriteAllText(Path.Combine(contents, "Info.plist"), Plist("0.4.4", "laptop"));
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var host = UpdateHost.ThisComputer(Path.Combine(contents, "MacOS", "arm64"), "Darwin");
            Assert.Equal("Study Stash's folder isn't writable, so it can't update itself here.", Updates.WhyNotUpdatable(host));
        }
        finally
        {
            File.SetUnixFileMode(parent, Executable);
        }
    }

    [Fact]
    public void Linux_never_updates_itself()
    {
        Assert.Equal("Study Stash updates itself on a Mac or a Windows PC.", Updates.WhyNotUpdatable(new UpdateHost { System = "Linux" }));
    }

    [Fact]
    public void A_bare_host_changes_nothing()
    {
        var bare = new UpdateHost();
        Assert.Throws<InvalidOperationException>(() => bare.Run("launchctl", [], TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => bare.SpawnDetached(["cmd"]));
        Assert.Throws<InvalidOperationException>(() => bare.VersionOf("StudyStash"));
        Assert.Equal("", bare.AppDir);
        Assert.Equal("This copy runs from a build folder, so it doesn't update itself.", Updates.WhyNotUpdatable(bare));
        Assert.Equal(Machine.Platform, UpdateHost.ThisComputer().System);
    }

    [Fact]
    public async Task A_release_without_this_computers_installer_waits_for_the_next()
    {
        using var dir = new TempDir();
        string appDir = dir["Study Stash.app"];
        Directory.CreateDirectory(appDir);
        var host = new UpdateHost { System = "Darwin", AppDir = appDir, Role = "laptop" };
        var said = new List<string>();
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.8"), dir["home"], host, said.Add));
        Assert.Equal($"v9.9.8 has no {Updates.MacLaptopAsset} yet; the next check tries again.", said.Single());
    }

    [Fact]
    public async Task A_mismatched_or_missing_checksum_leaves_the_app_alone()
    {
        using var dir = new TempDir();
        string appDir = dir["Study Stash.app"];
        Directory.CreateDirectory(Path.Combine(appDir, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(appDir, "Contents", "MacOS", "StudyStash"), "old");
        byte[] dmg = [9, 9, 9];
        var downloads = new FakeDownloads(new()
        {
            ["https://dl/laptop.dmg"] = dmg,
            ["https://dl/wrong-sums.txt"] = Encoding.UTF8.GetBytes($"{new string('0', 64)}  {Updates.MacLaptopAsset}\n"),
        });
        var host = new UpdateHost
        {
            System = "Darwin", AppDir = appDir, Role = "laptop", Http = downloads.Client(),
            Run = (_, _, _) => throw new InvalidOperationException("must not mount an unverified download"),
        };
        var said = new List<string>();
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", new()
        {
            [Updates.MacLaptopAsset] = "https://dl/laptop.dmg", [Updates.ChecksumsAsset] = "https://dl/wrong-sums.txt",
        }), dir["home"], host, said.Add));
        Assert.Equal("The download didn't match its checksum, so this version stays.", said[^1]);
        // no SHA256SUMS.txt at all: still refuses, rather than trust an unverified download
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", new() { [Updates.MacLaptopAsset] = "https://dl/laptop.dmg" }), dir["home"], host, said.Add));
        Assert.Equal("The download didn't match its checksum, so this version stays.", said[^1]);
        Assert.Equal("old", File.ReadAllText(Path.Combine(appDir, "Contents", "MacOS", "StudyStash")));
    }

    [Fact]
    public async Task A_download_that_doesnt_run_here_leaves_this_version_alone()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        string appsFolder = dir["Applications"];
        string oldApp = Path.Combine(appsFolder, "Study Stash.app");
        Directory.CreateDirectory(Path.Combine(oldApp, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(oldApp, "Contents", "MacOS", "StudyStash"), "keep");
        byte[] dmg = [1, 2, 3];
        var downloads = new FakeDownloads(new() { ["https://dl/laptop.dmg"] = dmg, ["https://dl/sums.txt"] = Encoding.UTF8.GetBytes(Sha256Sums(Updates.MacLaptopAsset, dmg)) });
        // the mounted volume says it's a different version than the release claims
        Runner run = (exe, args, t) =>
        {
            if (exe == "hdiutil" && args[0] == "attach")
            {
                string contents = Path.Combine(ArgAfter(args, "-mountpoint"), "Study Stash.app", "Contents");
                Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
                File.WriteAllText(Path.Combine(contents, "MacOS", "StudyStash"), "#!/bin/sh\necho 1.0.0\n");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(contents, "MacOS", "StudyStash"), Executable);
                File.WriteAllText(Path.Combine(contents, "Info.plist"), Plist("1.0.0"));
                return new ProcResult(0, "");
            }
            return exe == "ditto" ? Machine.Run(exe, args, t) : new ProcResult(0, "");
        };
        var host = new UpdateHost { System = "Darwin", AppDir = oldApp, Role = "laptop", Http = downloads.Client(), Run = run, VersionOf = Updates.VersionOf };
        var said = new List<string>();
        Assert.False(await Updates.ApplyAsync(Rel("v9.9.9", new()
        {
            [Updates.MacLaptopAsset] = "https://dl/laptop.dmg", [Updates.ChecksumsAsset] = "https://dl/sums.txt",
        }), dir["home"], host, said.Add));
        Assert.Equal("The v9.9.9 download didn't run on this Mac, so this version stays.", said[^1]);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(oldApp, "Contents", "MacOS", "StudyStash")));
        Assert.False(Directory.Exists(Path.Combine(appsFolder, ".Study Stash.app.new")));
    }

    /// <summary>A service file for this role that runs the app in `appDir`, as Autostart writes it.</summary>
    static void Service(ServicePlaces places, string system, string role, string exe)
    {
        string path = Autostart.ServicePath(role, places, system);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<string>{exe}</string>");
    }

    [Fact]
    public async Task A_mac_update_swaps_the_app_in_and_restarts_its_services()
    {
        if (OperatingSystem.IsWindows()) return; // a fake hdiutil stands in for the DMG
        using var dir = new TempDir();
        string appsFolder = dir["Applications"];
        string oldApp = Path.Combine(appsFolder, "Study Stash.app");
        Directory.CreateDirectory(Path.Combine(oldApp, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(oldApp, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(oldApp, "Contents", "MacOS", "StudyStash"), "old");
        File.WriteAllText(Path.Combine(oldApp, "Contents", "Info.plist"), Plist("0.4.4", "laptop"));
        File.WriteAllText(Path.Combine(oldApp, "Contents", "Resources", "stale.txt"), "old");
        var places = new ServicePlaces(dir["agents"], dir["startup"], dir["systemd"]);
        Service(places, "Darwin", "server", Path.Combine(oldApp, "Contents", "MacOS", "StudyStash"));
        byte[] dmg = [1, 2, 3];
        var downloads = new FakeDownloads(new() { ["https://dl/laptop.dmg"] = dmg, ["https://dl/sums.txt"] = Encoding.UTF8.GetBytes(Sha256Sums(Updates.MacLaptopAsset, dmg)) });
        var calls = new List<List<string>>();
        Runner run = (exe, args, t) =>
        {
            lock (calls) calls.Add([exe, .. args]);
            if (exe == "hdiutil" && args[0] == "attach")
            {
                string contents = Path.Combine(ArgAfter(args, "-mountpoint"), "Study Stash.app", "Contents");
                Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
                File.WriteAllText(Path.Combine(contents, "MacOS", "StudyStash"), "#!/bin/sh\necho 9.9.9\n");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(contents, "MacOS", "StudyStash"), Executable);
                File.WriteAllText(Path.Combine(contents, "Info.plist"), Plist("9.9.9", "laptop"));
                return new ProcResult(0, "");
            }
            return exe == "ditto" ? Machine.Run(exe, args, t) : new ProcResult(0, "");
        };
        var spawned = new List<IReadOnlyList<string>>();
        var host = new UpdateHost
        {
            System = "Darwin", AppDir = oldApp, Role = "laptop", Http = downloads.Client(), Run = run, Places = places,
            VersionOf = Updates.VersionOf, RelaunchPid = Environment.ProcessId, RelaunchArgs = ["--background"], SpawnDetached = spawned.Add,
        };
        var said = new List<string>();
        bool ok = await Updates.ApplyAsync(Rel("v9.9.9", new()
        {
            [Updates.MacLaptopAsset] = "https://dl/laptop.dmg", [Updates.ChecksumsAsset] = "https://dl/sums.txt",
        }), dir["home"], host, said.Add);
        Assert.True(ok, string.Join(" | ", said));
        Assert.Equal("#!/bin/sh\necho 9.9.9\n", File.ReadAllText(Path.Combine(oldApp, "Contents", "MacOS", "StudyStash")));
        Assert.False(File.Exists(Path.Combine(oldApp, "Contents", "Resources", "stale.txt"))); // the whole bundle is replaced
        Assert.False(Directory.Exists(oldApp + ".old"));
        Assert.False(Directory.Exists(Path.Combine(appsFolder, ".Study Stash.app.new")));
        Assert.Equal(["Installing v9.9.9...", "Installed v9.9.9.", "Restarted the server service."], said);
        Assert.Contains(calls, c => c is ["hdiutil", "attach", ..]);
        Assert.Contains(calls, c => c is ["hdiutil", "detach", ..]);
        Assert.Contains(calls, c => c[0] == "codesign");
        var waiter = Assert.Single(spawned);
        Assert.Equal("/bin/sh", waiter[0]);
        Assert.Equal("-c", waiter[1]);
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), waiter[3]);
        Assert.Equal(oldApp, waiter[4]);
        Assert.Equal("--background", waiter[5]);
    }

    [Fact]
    public async Task Windows_downloads_the_roles_setup_and_runs_it_quietly()
    {
        using var dir = new TempDir();
        byte[] setup = [1, 2, 3, 4, 5];
        var downloads = new FakeDownloads(new()
        {
            ["https://dl/library-setup.exe"] = setup, ["https://dl/sums.txt"] = Encoding.UTF8.GetBytes(Sha256Sums(Updates.WindowsLibraryAsset, setup)),
        });
        var spawned = new List<IReadOnlyList<string>>();
        var host = new UpdateHost
        {
            System = "Windows", AppDir = dir["install"], Role = "library", Http = downloads.Client(), SpawnDetached = spawned.Add, TempDir = dir["temp"],
        };
        Directory.CreateDirectory(host.AppDir);
        var said = new List<string>();
        string home = dir["home"];
        var rel = Rel("v9.9.9", new()
        {
            [Updates.WindowsLibraryAsset] = "https://dl/library-setup.exe", [Updates.ChecksumsAsset] = "https://dl/sums.txt",
        });
        Assert.True(await Updates.ApplyAsync(rel, home, host, said.Add));
        var args = Assert.Single(spawned);
        string setupPath = Path.Combine(dir["temp"], "Study Stash update", Updates.WindowsLibraryAsset);
        Assert.Equal(setup, await File.ReadAllBytesAsync(setupPath));
        Assert.Equal([setupPath, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/relaunch=1", $"/LOG={Path.Combine(home, "logs", "update.log")}"],
            args);
        Assert.Equal($"Installing v9.9.9 in the background (log: {Path.Combine(home, "logs", "update.log")}).", said[^1]);

        // the laptop copy never downloads the library installer
        var laptopSpawned = new List<IReadOnlyList<string>>();
        var laptop = new UpdateHost
        {
            System = "Windows", AppDir = dir["install2"], Role = "laptop", Http = downloads.Client(), SpawnDetached = laptopSpawned.Add, TempDir = dir["temp2"],
        };
        Directory.CreateDirectory(laptop.AppDir);
        Assert.False(await Updates.ApplyAsync(rel, dir["home2"], laptop, said.Add));
        Assert.Empty(laptopSpawned);
        Assert.Equal($"v9.9.9 has no {Updates.WindowsLaptopAsset} yet; the next check tries again.", said[^1]);
    }

    [Fact]
    public async Task Auto_update_only_restarts_a_supervised_install()
    {
        using var dir = new TempDir();
        string appDir = dir["Study Stash.app"];
        Directory.CreateDirectory(Path.Combine(appDir, "Contents"));
        File.WriteAllText(Path.Combine(appDir, "Contents", "Info.plist"), Plist("0.4.4"));
        var host = new UpdateHost { System = "Darwin", AppDir = appDir };
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
        File.WriteAllText(Path.Combine(appDir, "Contents", "Info.plist"), Plist("99.0.0"));
        Assert.Equal("restarting", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true, Latest, Apply, exits.Add));
        Assert.Single(applied);
        // a failed install keeps this version running
        File.WriteAllText(Path.Combine(appDir, "Contents", "Info.plist"), Plist("0.4.4"));
        Assert.Equal("install failed", await Updates.CheckAndUpdateAsync(dir.Path, host, logs.Add, true, Latest,
            (_, _, _, _) => Task.FromResult(false), exits.Add));
        Assert.Equal(2, exits.Count);
    }

    [Fact]
    public async Task Auto_update_waits_for_another_updater()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "update.lock"), "123"); // another engine takes the same file
        string appDir = dir["Study Stash.app"];
        Directory.CreateDirectory(appDir);
        var host = new UpdateHost { System = "Darwin", AppDir = appDir };
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
        var host = new UpdateHost { System = "Windows", AppDir = dir["install"] };
        bool? restart = null;
        var exits = new List<int>();
        Assert.Equal("handed off", await Updates.CheckAndUpdateAsync(dir.Path, host, _ => { }, true, () => Task.FromResult<Release?>(Rel("v99.0.0")),
            (_, _, _, r) => { restart = r; return Task.FromResult(true); }, exits.Add));
        Assert.True(restart);
        Assert.Empty(exits);
    }

    /// <summary>Only runs when STUDYSTASH_LIVE_DMG points at a real DMG (T1's build-app.sh output): a real
    /// hdiutil/ditto/codesign update of a stand-in bundle to that DMG, checksum included.</summary>
    [Fact]
    public async Task Live_a_real_dmg_updates_a_stand_in_bundle()
    {
        string? dmg = Environment.GetEnvironmentVariable("STUDYSTASH_LIVE_DMG");
        if (string.IsNullOrEmpty(dmg) || !OperatingSystem.IsMacOS()) return;
        using var dir = new TempDir();
        string appsFolder = dir["Applications"];
        string standIn = Path.Combine(appsFolder, "Study Stash.app");
        Directory.CreateDirectory(Path.Combine(standIn, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(standIn, "Contents", "MacOS", "StudyStash"), "stand-in, not the real app");
        File.WriteAllText(Path.Combine(standIn, "Contents", "Info.plist"), Plist("0.0.0"));
        byte[] dmgBytes = await File.ReadAllBytesAsync(dmg);
        var downloads = new FakeDownloads(new()
        {
            ["https://dl/laptop.dmg"] = dmgBytes, ["https://dl/sums.txt"] = Encoding.UTF8.GetBytes(Sha256Sums(Updates.MacLaptopAsset, dmgBytes)),
        });
        var host = new UpdateHost
        {
            System = "Darwin", AppDir = standIn, Role = "laptop", Http = downloads.Client(), Run = Machine.Run,
            Places = new ServicePlaces(dir["agents"], dir["startup"], dir["systemd"]), VersionOf = Updates.VersionOf,
        };
        var said = new List<string>();
        var clock = Stopwatch.StartNew();
        bool ok = await Updates.ApplyAsync(new Release("v" + Engine.Version, Updates.ParseVersion(Engine.Version), "u", "p", new Dictionary<string, string>
        {
            [Updates.MacLaptopAsset] = "https://dl/laptop.dmg", [Updates.ChecksumsAsset] = "https://dl/sums.txt",
        }), dir["home"], host, said.Add);
        clock.Stop();
        Assert.True(ok, string.Join("; ", said));
        Assert.Equal(Engine.Version, Updates.VersionOf(Path.Combine(standIn, "Contents", "MacOS", "StudyStash")));
        Console.WriteLine($"[live dmg update] took {clock.Elapsed}: {string.Join(" / ", said)}");
    }
}
