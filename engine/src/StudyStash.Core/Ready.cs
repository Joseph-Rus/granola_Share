using System.Text;

namespace StudyStash.Core;

/// <summary>What the laptop has: the Granola app (null when it isn't there), whether Granola makes one for this
/// system, and Tailscale.</summary>
public sealed record LaptopInfo(string? Granola, bool GranolaHere, TailscaleInfo Tailscale);

/// <summary>Downloads `url` to `dest`, calling progress(done, total) as it goes; returns dest.</summary>
public delegate Task<string> Fetch(string url, string dest, Action<long, long>? progress);

/// <summary>
/// Get each computer ready (ready.py). The library's: Tailscale, so your laptop and phone reach it from anywhere, and
/// Ollama, which writes the study notes. The laptop's: Granola, which records the lectures, and Tailscale.
///
/// Installs use each app's official download, the way you'd install it by hand: the Ollama app and Tailscale's own
/// installer. Nothing here takes admin rights by itself. When an installer needs them, the system asks you (macOS for
/// your password, Windows with its permission prompt).
/// </summary>
public static class Ready
{
    public const string OllamaMac = "https://ollama.com/download/Ollama-darwin.zip";
    public const string OllamaWindows = "https://ollama.com/download/OllamaSetup.exe";
    public const string OllamaLinux = "curl -fsSL https://ollama.com/install.sh | sh";
    public const string TailscaleMac = "https://pkgs.tailscale.com/stable/Tailscale-latest-macos.pkg";
    public const string TailscaleWindows = "https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe";
    public const string TailscaleLinux = "curl -fsSL https://tailscale.com/install.sh | sh";
    public const string TailscaleDownload = "https://tailscale.com/download";
    public const string GranolaDownload = "https://www.granola.ai/download";
    /// <summary>Every Tailscale address is in this range.</summary>
    public const string Tailnet = "100.64.0.0/10";

    /// <summary>Signed in over SSH: an installer window would open on this computer's own screen, where nobody sees it.</summary>
    public static bool RemoteSession() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));

    static readonly HttpClient DownloadHttp = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("study-stash/" + Engine.Version);
        return http;
    }

    /// <summary>Fetch `url` into `dest`. A download may take as long as it needs, but not a minute without a byte.</summary>
    public static async Task<string> DownloadAsync(string url, string dest, Action<long, long>? progress = null, HttpClient? http = null,
        CancellationToken ct = default)
    {
        var stalled = TimeSpan.FromSeconds(60);
        using var start = CancellationTokenSource.CreateLinkedTokenSource(ct);
        start.CancelAfter(stalled);
        using var r = await (http ?? DownloadHttp).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, start.Token);
        r.EnsureSuccessStatusCode();
        long total = r.Content.Headers.ContentLength ?? 0, done = 0;
        await using var source = await r.Content.ReadAsStreamAsync(ct);
        await using (var file = File.Create(dest))
        {
            var buffer = new byte[1 << 20];
            while (true)
            {
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                read.CancelAfter(stalled);
                int n = await source.ReadAsync(buffer, read.Token);
                if (n == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (progress is not null && total > 0) progress(done, total);
            }
        }
        return dest;
    }

    public static Task<string> Download(string url, string dest, Action<long, long>? progress) => DownloadAsync(url, dest, progress);

    /// <summary>Where an installer that outlives this command is saved: an installer window may still be reading it.</summary>
    public static string DownloadsFolder()
    {
        string folder = Path.Combine(Py.UserHome(), "Downloads");
        return Directory.Exists(folder) ? folder : Path.GetTempPath();
    }

    /// <summary>A folder of our own under the temporary folder, removed when done.</summary>
    public sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("study-stash-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Move a folder, even to another disk (Directory.Move can't): `ditto` keeps a Mac app's links and signature.</summary>
    public static void MoveFolder(string from, string to, Runner? run = null)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException) when (OperatingSystem.IsMacOS())
        {
            var p = (run ?? Machine.Run)("ditto", [from, to], TimeSpan.FromMinutes(10));
            if (p is not { ExitCode: 0 }) throw new IOException($"couldn't copy {from} to {to}");
            Directory.Delete(from, recursive: true);
        }
    }

    // --- Ollama --------------------------------------------------------------------------------------------------

    /// <summary>Install Ollama from ollama.com. True when it's installed afterwards. `applications` is where a Mac puts
    /// it: /Applications when this account can write there, otherwise ~/Applications. (What changes the computer takes
    /// its runner and download explicitly, so nothing installs by default.)</summary>
    public static async Task<bool> InstallOllamaAsync(Action<string> log, Action<long, long>? progress, string system, Runner run, Fetch fetch,
        string? applications = null, Func<string?, bool>? installed = null, Func<string, IReadOnlyList<string>, int?>? attached = null)
    {
        attached ??= Machine.RunAttached;
        try
        {
            if (system == "Darwin")
            {
                using var tmp = new TempFolder();
                string archive = await fetch(OllamaMac, Path.Combine(tmp.Path, "Ollama-darwin.zip"), progress);
                string unpacked = Path.Combine(tmp.Path, "unpacked");
                var p = run("ditto", ["-x", "-k", archive, unpacked], TimeSpan.FromMinutes(10));
                if (p is not { ExitCode: 0 } || !Directory.Exists(Path.Combine(unpacked, "Ollama.app")))
                {
                    log("    The download didn't unpack. Get Ollama from https://ollama.com/download instead.");
                    return false;
                }
                string apps = applications ?? (Machine.Writable("/Applications") ? "/Applications" : Path.Combine(Py.UserHome(), "Applications"));
                string dest = Path.Combine(apps, "Ollama.app");
                Directory.CreateDirectory(apps);
                if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                MoveFolder(Path.Combine(unpacked, "Ollama.app"), dest, run);
                log($"    Installed in {apps}.");
            }
            else if (system == "Windows")
            {
                using var tmp = new TempFolder();
                string setup = await fetch(OllamaWindows, Path.Combine(tmp.Path, "OllamaSetup.exe"), progress);
                log("    Running Ollama's installer (it installs in your account, no admin needed)...");
                // Its installer takes the usual Inno Setup options: progress only, no questions.
                var p = run(setup, ["/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES"], TimeSpan.FromMinutes(30));
                if (p is null) log("    Ollama's installer didn't finish.");
                else if (p.ExitCode != 0) log($"    Ollama's installer stopped (code {p.ExitCode}).");
            }
            else
            {
                log($"    Running Ollama's installer: {OllamaLinux}");
                log("    It asks for your password, because it sets Ollama up for the whole computer.");
                attached("sh", ["-c", OllamaLinux]);
            }
        }
        catch (Exception e)
        {
            log($"    Couldn't install Ollama: {e.Message}");
            return false;
        }
        return (installed ?? (s => Ollama.Installed(s)))(system);
    }

    // --- Tailscale -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Install Tailscale from tailscale.com. Its installers need an administrator's OK, so on a Mac and on Windows the
    /// installer opens for you to click through, and `ask` waits until you're done (the setup page doesn't wait: it
    /// looks again every few seconds). True when it's installed afterwards.
    /// </summary>
    public static async Task<bool> InstallTailscaleAsync(Action<string> log, Action<long, long>? progress, string system, Runner run, Fetch fetch,
        Action<string>? ask = null, bool? remote = null, Action<string>? start = null, Func<string?>? exe = null,
        Func<string, IReadOnlyList<string>, int?>? attached = null, string? downloads = null)
    {
        downloads ??= DownloadsFolder();
        ask ??= _ => { };
        start ??= Machine.Open; // Windows: asks to run it as administrator
        attached ??= Machine.RunAttached;
        bool overSsh = remote ?? RemoteSession();
        try
        {
            if (system == "Darwin")
            {
                if (overSsh)
                {
                    log("    Tailscale needs a few clicks on this Mac's own screen, and you're connected over SSH.");
                    log($"    At this Mac, install it from {TailscaleDownload}/mac, open it, and sign in.");
                    return false;
                }
                string pkg = await fetch(TailscaleMac, Path.Combine(downloads, "Tailscale.pkg"), progress);
                run("open", [pkg], TimeSpan.FromSeconds(60));
                log("    Tailscale's installer is open. Click through it; macOS asks for your password.");
                ask("    Press Enter when the installer has finished: ");
            }
            else if (system == "Windows")
            {
                string setup = await fetch(TailscaleWindows, Path.Combine(downloads, "tailscale-setup.exe"), progress);
                start(setup);
                log("    Tailscale's installer is open. Windows asks for permission first: click Yes, then Install.");
                ask("    Press Enter when the installer has finished: ");
            }
            else
            {
                log($"    Running Tailscale's installer: {TailscaleLinux}");
                log("    It asks for your password, because it sets Tailscale up for the whole computer.");
                attached("sh", ["-c", TailscaleLinux]);
            }
        }
        catch (Exception e)
        {
            log($"    Couldn't install Tailscale: {e.Message}");
            return false;
        }
        return (exe ?? HostInfo.TailscaleExe)() is not null;
    }

    /// <summary>Open the Tailscale app, where you sign in or turn it on.</summary>
    public static bool OpenTailscale(string system, Runner run, Action<string>? open = null)
    {
        open ??= Machine.Open;
        try
        {
            if (system == "Darwin") return run("open", ["-a", "Tailscale"], TimeSpan.FromSeconds(30)) is { ExitCode: 0 };
            if (system == "Windows")
            {
                string programs = Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } p ? p : @"C:\Program Files";
                string tray = Path.Combine(programs, "Tailscale", "tailscale-ipn.exe");
                if (File.Exists(tray))
                {
                    open(tray);
                    return true;
                }
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        return false;
    }

    // --- the laptop: Granola ---------------------------------------------------------------------------------------

    /// <summary>Where the Granola app is, or null. Granola has apps for macOS and Windows only.</summary>
    public static string? GranolaApp(string? system = null, Runner? run = null, AppPlaces? at = null)
    {
        system ??= Machine.Platform;
        at ??= AppPlaces.Default;
        if (system == "Darwin")
        {
            foreach (string app in new[] { Path.Combine(at.SystemApps, "Granola.app"), Path.Combine(at.PersonalApps, "Granola.app") })
                if (Directory.Exists(app)) return app;
            // anywhere else Spotlight knows about
            var p = (run ?? Machine.Run)("mdfind", ["kMDItemCFBundleIdentifier == \"com.granola.app\""], TimeSpan.FromSeconds(5));
            return Py.SplitLines(p?.Stdout ?? "").FirstOrDefault(line => line.EndsWith(".app", StringComparison.Ordinal));
        }
        if (system == "Windows")
        {
            // Its installer puts it in your account, in a folder named after the project (@granolaelectron).
            var roots = new List<string> { Path.Combine(at.LocalAppData, "Programs") };
            foreach (string name in new[] { "ProgramFiles", "ProgramW6432" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } root && !roots.Contains(root)) roots.Add(root);
            foreach (string root in roots)
                foreach (string folder in new[] { "@granolaelectron", "Granola" })
                    if (File.Exists(Path.Combine(root, folder, "Granola.exe"))) return Path.Combine(root, folder, "Granola.exe");
            return GranolaFromRegistry();
        }
        return null;
    }

    /// <summary>Granola's entry in Windows' installed apps, wherever it went. (A power-saving app from MiserWare is
    /// also called Granola: not that one.)</summary>
    static string? GranolaFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var apps = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (apps is null) continue;
                foreach (string name in apps.GetSubKeyNames())
                {
                    using var app = apps.OpenSubKey(name);
                    string Value(string key) => app?.GetValue(key)?.ToString() ?? "";
                    if (Value("DisplayName").StartsWith("Granola", StringComparison.Ordinal)
                        && !Value("Publisher").Contains("miserware", StringComparison.OrdinalIgnoreCase))
                        return Value("InstallLocation") is { Length: > 0 } where ? where
                            : Value("DisplayIcon").Split(',')[0] is { Length: > 0 } icon ? icon : Value("DisplayName");
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return null;
    }

    public static LaptopInfo LaptopChecks(string? system = null, Func<string, string?>? granola = null, Func<TailscaleInfo>? tailscale = null)
    {
        system ??= Machine.Platform;
        bool here = system is "Darwin" or "Windows";
        return new LaptopInfo(here ? (granola ?? (s => GranolaApp(s)))(system) : null, here, (tailscale ?? (() => HostInfo.Tailscale()))());
    }

    // --- Windows: the firewall -----------------------------------------------------------------------------------

    /// <summary>The programs Windows may have blocked: this engine's (Windows adds a block rule for a program when
    /// its "allow access?" prompt is dismissed).</summary>
    public static List<string> Programs() => Autostart.EngineCommand().Take(1).ToList();

    /// <summary>Our inbound rule for the library's port, open to Tailscale and this network only. Also removes the
    /// block rules Windows adds when its "allow access?" prompt was dismissed: they'd win.</summary>
    public static string FirewallScript(int port, IReadOnlyList<string>? programs = null)
    {
        string list = string.Join(",", (programs ?? Programs()).Select(p => "'" + p.Replace("'", "''") + "'"));
        string rule = Machine.FirewallRule;
        return $"Remove-NetFirewallRule -DisplayName '{rule}' -ErrorAction SilentlyContinue; "
            + $"New-NetFirewallRule -DisplayName '{rule}' "
            + "-Description 'Lets your laptop reach your Study Stash library. Added by granola-share setup.' "
            + $"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -RemoteAddress {Tailnet},LocalSubnet "
            + "-Profile Any | Out-Null; "
            + $"$py = @({list}); Get-NetFirewallApplicationFilter | Where-Object {{ $py -contains $_.Program }} "
            + "| Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } "
            + "| Remove-NetFirewallRule";
    }

    /// <summary>What asks Windows to run the rule's script as administrator: its permission prompt.</summary>
    public static string ElevateScript(string script) =>
        "try { Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden "
        + $"-ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}'; exit 0 }} catch {{ exit 1 }}";

    /// <summary>Add the rule, with Windows' permission prompt (changing the firewall needs an administrator).</summary>
    public static Task<bool> OpenFirewallAsync(int port, Runner run, IReadOnlyList<string>? programs = null) => Task.Run(() =>
    {
        var p = run("powershell", ["-NoProfile", "-NonInteractive", "-Command", ElevateScript(FirewallScript(port, programs))], TimeSpan.FromSeconds(300));
        return p is { ExitCode: 0 } && Machine.FirewallOpen(port, run) == true;
    });

    // --- sleep ---------------------------------------------------------------------------------------------------

    /// <summary>Windows: never sleep while plugged in (the screen can still turn off). True if it took.</summary>
    public static bool KeepAwake(string system, Runner run)
    {
        if (system != "Windows") return false;
        if (run("powercfg", ["/change", "standby-timeout-ac", "0"], TimeSpan.FromSeconds(20)) is null) return false;
        return Machine.WindowsSleepMinutes(run) == 0;
    }
}
