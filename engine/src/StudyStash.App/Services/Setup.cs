using System.Globalization;
using StudyStash.App.ViewModels;
using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>What setup's buttons do, and what it shows as things change (the microphone's permission, the download).</summary>
public static class Setup
{
    /// <summary>"mac-mini" → "http://mac-mini:8787"; an https address or one with a port is kept as it is.</summary>
    public static string NormalizeAddress(string text)
    {
        string t = text.Trim().TrimEnd('/');
        if (t.Length == 0) return t;
        if (!t.Contains("://", StringComparison.Ordinal)) t = "http://" + t;
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.IsDefaultPort && u.Scheme == Uri.UriSchemeHttp && !t[(t.IndexOf("://", StringComparison.Ordinal) + 3)..].Contains(':'))
            t = $"{u.Scheme}://{u.Host}:8787{u.PathAndQuery.TrimEnd('/')}";
        return t;
    }

    static string Person() =>
        Environment.UserName is { Length: > 0 } u ? char.ToUpper(u[0], CultureInfo.InvariantCulture) + u[1..] : "Me";

    public static SetupModel Make(AppHost host)
    {
        var m = SetupModel.For(Skin.Current);
        var cc = host.Client();
        m.Address = cc.ServerUrl;
        m.LibraryName = $"{Person()}'s library";
        m.ModelName = host.Model.Name;
        m.ModelSize = About(host.Model.Bytes);
        if (cc.ServerUrl.Length > 0 && host.Library == LibraryState.Connected)
        {
            m.LibraryOk = true;
            m.LibraryResult = $"Connected to {cc.PoolName}.";
        }

        m.OnAllowMic = () => Task.Run(async () =>
        {
            host.AskMic();
            // A Mac answers the permission dialog later, in its own time: keep checking for up to a minute.
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1000);
                bool answered = host.MicAccess() != MicAccess.NotAsked;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Refresh(m, host));
                if (answered) return;
            }
        });
        m.OnMicSettings = () => Dialogs.OpenUrl(host.MicSettingsUrl);
        m.OnTaskbarSettings = () => Dialogs.OpenUrl("ms-settings:taskbar");
        m.OnRetryModel = () => _ = host.DownloadModelAsync();
        m.OnConnect = () => ConnectAsync(m, host);
        m.OnFind = () => FindAsync(m, host);
        m.OnAddClass = () => AddClassAsync(m, host);
        m.CanLeave = step =>
        {
            if (step == SetupStep.Library && !m.LibraryOk)
            {
                m.LibraryResult = m.ThisComputer ? "Create the library first." : "Connect to your library first.";
                return false;
            }
            return true;
        };
        m.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupModel.Step) && m.Step == SetupStep.Model) _ = host.DownloadModelAsync();
        };
        foreach (var c in host.Timetable.Classes)
            m.Classes.Add(new SetupClass { Name = c.Name, When = string.Join(", ", c.Times.Select(t => t.Describe())), Dot = Skin.ClassDot(Math.Max(0, host.ColorOf(c.Name))) });
        Refresh(m, host);
        return m;
    }

    public static void Refresh(SetupModel m, AppHost host)
    {
        var mic = host.MicAccess();
        m.MicAllowed = mic == MicAccess.Allowed;
        m.MicDenied = mic is MicAccess.Denied or MicAccess.Restricted;
        m.ModelReady = host.ModelReady;
        m.ModelProblem = host.DownloadProblem;
        if (host.ModelReady)
        {
            m.ModelProgress = 1;
            m.ModelDone = $"{host.Model.Name} is ready";
            m.ModelLeft = "";
        }
        else if (host.Downloading is { } d)
        {
            m.ModelProgress = d.Fraction;
            m.ModelDone = d.Amount;
            m.ModelLeft = d.Left() ?? "";
        }
    }

    /// <summary>A model's size for "The model is about 3 GB": whole gigabytes for the big ones, as the design says it.</summary>
    public static string About(long bytes) => bytes >= 2_500_000_000 ? $"{Math.Round(bytes / 1e9)} GB" : WhisperModel.SizeOf(bytes);

    /// <summary>Saves what setup decided (the role, that it's done) and, only if the box was ticked, starts Study
    /// Stash at login. Never touches login items otherwise: that would change this computer unasked.</summary>
    public static void Finish(SetupModel m, AppHost host)
    {
        host.Save(s =>
        {
            s.Role = m.Role;
            s.SetupDone = true;
        });
        if (m.StartAtLogin)
        {
            try
            {
                host.LoginItems.StartAtLogin(true, host.Home);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                host.Log($"[app] start at login: {e.Message}");
            }
        }
    }

    /// <summary>Every tick while setup's window is open: the microphone is open exactly while its step shows, it's
    /// allowed, and nothing is recording; copies its levels and whether it's heard anything into the model.</summary>
    public static void TickMic(SetupModel m, AppHost host, MicCheck mic)
    {
        if (m.OnMicrophone && m.MicAllowed && host.Recorder.Current is null) mic.Open(host.OpenMic);
        else mic.Close();
        if (mic.Heard) m.MicHeard = true;
        m.MicLevels = mic.Levels();
    }

    static async Task ConnectAsync(SetupModel m, AppHost host)
    {
        m.Connecting = true;
        m.LibraryResult = null;
        try
        {
            if (m.ThisComputer)
            {
                string done = await LibraryHere.ThisComputer().CreateAsync(host, m.LibraryName, m.Password, Person(), m.Role);
                m.LibraryOk = true;
                m.LibraryResult = done;
            }
            else
            {
                string url = NormalizeAddress(m.Address);
                if (url.Length == 0) throw new InvalidOperationException("Type your library's address, like http://mac-mini:8787.");
                var health = await LibraryApi.CheckServerAsync(url, m.Password.Trim());
                var cc = host.Client();
                cc.ServerUrl = url;
                cc.PoolKey = m.Password.Trim();
                cc.PoolName = health["pool_name"]?.GetValue<string>() ?? "";
                if (cc.DisplayName.Length == 0) cc.DisplayName = Person();
                Configs.SaveClient(cc);
                m.Address = url;
                m.LibraryOk = true;
                m.LibraryResult = $"Connected to {cc.PoolName}.";
            }
            await host.CheckLibraryAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or HttpRequestException or System.Text.Json.JsonException or IOException)
        {
            m.LibraryOk = false;
            m.LibraryResult = e.Message switch
            {
                "wrong password" => "That password isn't right.",
                var s when s.StartsWith("could not reach", StringComparison.Ordinal) => "Can't reach that address. Is the library's computer on, and is Tailscale connected?",
                var s => char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..],
            };
        }
        finally
        {
            m.Connecting = false;
        }
    }

    /// <summary>"Find it": looks for a library on this computer or your Tailscale network, with no password, and
    /// fills in the address (and, for a Tailscale one, says its name so you know whose password to type).</summary>
    static async Task FindAsync(SetupModel m, AppHost host)
    {
        m.Finding = true;
        m.LibraryResult = null;
        try
        {
            var cfg = Configs.Load(host.Home);
            var extra = new List<int>();
            if (File.Exists(cfg.ConfigPath)) extra.Add(cfg.WebPort);
            var found = await new LibraryFinder { ExtraPorts = extra }.FindAsync();
            if (found is null)
            {
                m.LibraryResult = "No library answered. Type its address, like http://mac-mini:8787.";
            }
            else
            {
                m.Address = found.Url;
                m.LibraryResult = found.Local ? $"Found a library on this {m.DeviceWord}."
                    : found.Name.Length > 0 ? $"Found {found.Name} on your Tailscale network. Type its password."
                    : "Found a library on your Tailscale network. Type its password.";
            }
        }
        finally
        {
            m.Finding = false;
        }
    }

    static async Task AddClassAsync(SetupModel m, AppHost host)
    {
        string name = m.NewClass.Trim();
        if (name.Length == 0) return;
        List<ClassTime> times = [];
        if (m.NewWhen.Trim().Length > 0)
        {
            if (ClassTime.ParseMany(m.NewWhen) is not { } parsed)
            {
                m.ClassProblem = "Write the days and times like “Tue Thu 10:00–11:15” or “MWF 9–9:50”.";
                return;
            }
            times = parsed;
        }
        m.ClassProblem = null;
        if (host.Remote() is { } lib)
        {
            try
            {
                await lib.AddClassAsync(name);
                await host.CheckLibraryAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
            {
                // An older library can't take classes over its API: the timetable still has it.
            }
        }
        var t = host.Timetable;
        t.Classes.RemoveAll(c => c.Name == name);
        t.Classes.Add(new TimetableClass(name, times));
        host.SaveTimetable(t);
        m.Classes.Add(new SetupClass { Name = name, When = string.Join(", ", times.Select(x => x.Describe())), Dot = Skin.ClassDot(Math.Max(0, host.ColorOf(name))) });
        m.NewClass = "";
        m.NewWhen = "";
    }
}
