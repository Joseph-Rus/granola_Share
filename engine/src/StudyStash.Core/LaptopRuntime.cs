using System.Text.Json.Nodes;

namespace StudyStash.Core;

/// <summary>The Granola sign-in the page started: running, how it ended, and when it started (seconds since 1970).</summary>
public sealed record SignInState(bool Running = false, string? Error = null, double? Started = null);

/// <summary>A slow thing the page started (installing Tailscale): running, or how it ended.</summary>
public sealed record JobState(bool Running = false, string? Error = null);

/// <summary>Whether transcripts can be copied from the Granola app here, and whether macOS allows it.</summary>
public sealed record CopyStatus(bool Available, bool Enabled, bool Allowed, string? Why);

/// <summary>
/// What runs in the laptop's background service (client_app.py's ClientRuntime): the watcher and the Granola sign-in,
/// started and restarted from the Study Stash page. Copying transcripts out of the Granola window isn't here: the
/// Python engine does that until the Study Stash app takes it over, and the watcher reads what it copied.
/// </summary>
public class LaptopRuntime(string home, LaptopHost host, CancellationTokenSource stop, Action<string>? log = null)
{
    public const string NoCopier = "copied by the Python engine until the Study Stash app does it";

    readonly Lock gate = new();
    Task? watcher;
    bool updating;
    (DateTime At, LaptopInfo Checks)? checkedAt;

    public string Home { get; } = Py.NormPath(home);
    public LaptopHost Host { get; } = host;
    /// <summary>Cancelled when the service should stop (and, as a service, start again).</summary>
    public CancellationTokenSource Stop { get; } = stop;
    public Action<string> Log { get; } = log ?? Console.WriteLine;
    public ShareClient? Client { get; set; }
    public SignInState Login { get; set; } = new();
    public JobState TailscaleJob { get; set; } = new();

    public ClientConfig Config() => Configs.LoadClient(Home);

    /// <summary>What this computer has, at most every few seconds (the setup page asks every two).</summary>
    public virtual LaptopInfo Readiness(bool fresh = false)
    {
        lock (gate)
        {
            if (fresh || checkedAt is not var (at, _) || (DateTime.UtcNow - at).TotalSeconds > 4) checkedAt = (DateTime.UtcNow, Host.Readiness());
            return checkedAt.Value.Checks;
        }
    }

    /// <summary>Install Tailscale (its own installer opens) or, when it's installed, open it to sign in.</summary>
    public string FixTailscale()
    {
        if (Host.TailscaleExe() is not null)
            return Host.OpenTailscale() ? "Tailscale is open. Sign in with the same account as your library's computer."
                : "Open Tailscale from your apps and sign in.";
        if (TailscaleJob.Running) return "Tailscale is downloading.";
        TailscaleJob = new JobState(true);
        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await Host.InstallTailscale(Log);
                TailscaleJob = new JobState(false, ok ? null : "it isn't installed yet");
            }
            catch (Exception e)
            {
                TailscaleJob = new JobState(false, e.Message);
            }
            lock (gate) checkedAt = null;
        });
        return "Downloading Tailscale. Its installer opens in a moment: click through it.";
    }

    public bool SignedIn() => File.Exists(Config().TokensPath);

    public bool Configured() => Config().ServerUrl.Length > 0 && SignedIn();

    public virtual bool Watching => watcher is { IsCompleted: false };

    /// <summary>The page's word on the last check: the watcher's last error.</summary>
    public virtual string? Problem => Client?.LastError;

    /// <summary>Why the library didn't take the last lecture ("password", "unreachable", "other"), while there's a problem.</summary>
    public virtual string? SendProblemKind => Client?.SendProblemKind;

    public virtual CopyStatus CopyStatus()
    {
        if (Host.System != "Darwin") return new CopyStatus(false, false, false, "only on macOS for now");
        return new CopyStatus(false, Config().CopyTranscripts, false, NoCopier);
    }

    public virtual void RequestPermission()
    {
    }

    public virtual void StartWatching()
    {
        lock (gate)
        {
            if (Watching) return;
            var cc = Config();
            Client = new ShareClient(cc, Host.Granola(cc), Host, cc.CopyTranscripts ? new TranscriptStore(Home) : null, Log);
            watcher = Client.RunLoopAsync(Stop.Token);
            if (!updating)
            {
                Host.StartUpdates(Home, Stop.Token, Log);
                updating = true;
            }
            Log($"[app] watching Granola for '{cc.PoolName}'");
        }
    }

    /// <summary>Settings changed on the page: the running watcher picks them up.</summary>
    public virtual void Reload()
    {
        var cc = Config();
        if (Client is not { } c) return;
        bool reconnected = (cc.ServerUrl, cc.PoolKey) != (c.Cc.ServerUrl, c.Cc.PoolKey);
        c.Cc = cc;
        c.Copies = cc.CopyTranscripts ? c.Copies ?? new TranscriptStore(Home) : c.Copies;
        if (reconnected && c.SendProblemKind is not null)
        {
            // The page just checked the new address and password: drop the old warning and send what's waiting now,
            // not at the next check minutes later.
            c.SendProblem = c.SendProblemKind = c.LastError = null;
            c.Wake();
        }
    }

    public virtual void CheckNow() => Client?.Wake();

    public virtual void StartLogin()
    {
        if (Login.Running) return;
        Login = new SignInState(true, null, Py.Time());
        _ = Task.Run(async () =>
        {
            try
            {
                await Host.SignIn(Config(), Log);
                Login = new SignInState();
                if (Configured()) StartWatching();
            }
            catch (Exception e)
            {
                Login = new SignInState(false, e.Message);
            }
        });
    }

    /// <summary>Stop in a second, so the page gets its answer first; as a service this starts again, into setup.</summary>
    public void RestartSoon() => _ = Task.Delay(1000).ContinueWith(_ => Stop.Cancel(), TaskScheduler.Default);

    /// <summary>What /api/state says about the sign-in.</summary>
    public JsonObject LoginJson() => new()
    {
        ["running"] = Login.Running, ["error"] = Login.Error, ["started"] = Login.Started is double t ? JsonValue.Create(t) : null,
    };
}
