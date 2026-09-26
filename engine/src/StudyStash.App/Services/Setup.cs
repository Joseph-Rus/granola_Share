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
        m.ModelSize = host.Model.Size.Replace(".0 ", " ");
        if (cc.ServerUrl.Length > 0 && host.Library == LibraryState.Connected)
        {
            m.LibraryOk = true;
            m.LibraryResult = $"Connected to {cc.PoolName}.";
        }

        m.OnAllowMic = () => Task.Run(() =>
        {
            host.AskMic();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Refresh(m, host));
        });
        m.OnMicSettings = () => Dialogs.OpenUrl(host.MicSettingsUrl);
        m.OnTaskbarSettings = () => Dialogs.OpenUrl("ms-settings:taskbar");
        m.OnRetryModel = () => _ = host.DownloadModelAsync();
        m.OnConnect = () => ConnectAsync(m, host);
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

    static async Task ConnectAsync(SetupModel m, AppHost host)
    {
        m.Connecting = true;
        m.LibraryResult = null;
        try
        {
            if (m.ThisComputer)
            {
                string done = await LibraryHere.ThisComputer().CreateAsync(host.Home, m.LibraryName, m.Password, Person());
                host.Save(s => s.LibraryHere = true);
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
