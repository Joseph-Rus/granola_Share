using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.App.Platform;
using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>A Whisper model in Settings: its name, size, what it's for, and whether it's here.</summary>
public sealed partial class ModelChoice : ObservableObject
{
    public required WhisperModel Model { get; init; }
    public string Name => Model.Name;
    public string About => $"{Model.Size}. {Model.About}";
    [ObservableProperty] public partial bool Chosen { get; set; }
    [ObservableProperty] public partial bool Here { get; set; }
}

/// <summary>A class in the timetable editor: its name and its times as typed.</summary>
public sealed partial class TimetableRow : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Times { get; set; } = "";
    [ObservableProperty] public partial bool Bad { get; set; }
    public Avalonia.Media.IBrush Dot { get; init; } = Avalonia.Media.Brushes.Gray;
}

/// <summary>A connection Claude has to the library, for the Claude section's list.</summary>
public sealed class ClaudeConnection
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>
/// Settings: Library (where it is; this computer's own), Recording (the model, language, the computer's sound, how
/// long audio stays), Classes (the timetable), Claude (Claude Code, Claude Desktop, and Claude on the web) and General.
/// </summary>
public sealed partial class SettingsModel : ObservableObject, IDisposable
{
    readonly AppHost host;
    readonly ClaudeSetup claude;

    [ObservableProperty] public partial string Section { get; set; } = "Library";

    // Library
    [ObservableProperty] public partial string LibraryLine { get; set; } = "";
    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string? LibrarySay { get; set; }
    [ObservableProperty] public partial bool LibraryHere { get; set; }
    [ObservableProperty] public partial string DisplayName { get; set; } = "";

    // Recording
    public ObservableCollection<ModelChoice> Models { get; } = [];
    [ObservableProperty] public partial string ModelLine { get; set; } = "";
    [ObservableProperty] public partial string Language { get; set; } = "";
    [ObservableProperty] public partial bool ComputerAudio { get; set; }
    [ObservableProperty] public partial string KeepAudio { get; set; } = "30";
    [ObservableProperty] public partial bool Shortcuts { get; set; }
    public bool CanRecordComputerAudio => Microphones.CanRecordComputerAudio;

    // Classes
    public ObservableCollection<TimetableRow> Rows { get; } = [];
    [ObservableProperty] public partial string NewClass { get; set; } = "";
    [ObservableProperty] public partial string NewTimes { get; set; } = "";
    [ObservableProperty] public partial string? ClassesSay { get; set; }

    // Claude
    [ObservableProperty] public partial string? ClaudeSay { get; set; }
    [ObservableProperty] public partial string ClaudeCommand { get; set; } = "";
    [ObservableProperty] public partial bool WebOn { get; set; }
    [ObservableProperty] public partial string? WebUrl { get; set; }
    [ObservableProperty] public partial string? WebSay { get; set; }
    [ObservableProperty] public partial bool WebBusy { get; set; }
    public ObservableCollection<ClaudeConnection> Connections { get; } = [];
    [ObservableProperty] public partial bool ClaudeNeedsNewLibrary { get; set; }

    // General
    [ObservableProperty] public partial bool StartAtLogin { get; set; }
    public string Version => Engine.Version;

    public bool OnLibrary => Section == "Library";
    public bool OnRecording => Section == "Recording";
    public bool OnClasses => Section == "Classes";
    public bool OnClaude => Section == "Claude";
    public bool OnGeneral => Section == "General";
    public bool HasWebUrl => !string.IsNullOrEmpty(WebUrl);
    public bool HasConnections => Connections.Count > 0;

    public static SettingsModel Make(AppHost host) => new(host);

    SettingsModel(AppHost host)
    {
        this.host = host;
        claude = ClaudeSetup.ThisComputer(host.Home);
        var cc = host.Client();
        Address = cc.ServerUrl;
        DisplayName = cc.DisplayName;
        LibraryHere = host.Settings.LibraryHere;
        Language = host.Settings.Language;
        ComputerAudio = host.Settings.ComputerAudio;
        KeepAudio = host.Settings.KeepAudioDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Shortcuts = host.Settings.Shortcuts;
        StartAtLogin = Desktop.StartsAtLogin();
        ClaudeCommand = claude.ClaudeCodeCommand;
        foreach (var m in WhisperModels.All.Where(m => m.Id != WhisperModels.Tiny.Id))
            Models.Add(new ModelChoice { Model = m, Chosen = m.Id == host.Model.Id, Here = WhisperModels.IsDownloaded(host.Home, m) });
        foreach (var c in host.Timetable.Classes)
            Rows.Add(new TimetableRow { Name = c.Name, Times = string.Join(", ", c.Times.Select(t => t.Describe())), Dot = Skin.ClassDot(Math.Max(0, host.ColorOf(c.Name))) });
        foreach (var (name, color, _) in host.Classes().Where(c => Rows.All(r => r.Name != c.Name)))
            Rows.Add(new TimetableRow { Name = name, Dot = Skin.ClassDot(color) });
        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConnections));
        host.Changed += OnHostChanged;
        Refresh();
        _ = LoadClaudeAsync();
    }

    void OnHostChanged() => Dispatcher.UIThread.Post(Refresh);

    void Refresh()
    {
        var cc = host.Client();
        LibraryLine = host.Library switch
        {
            LibraryState.Connected when host.OlderLibrary => $"Connected to {cc.PoolName}. It runs an older Study Stash: update it to browse, search and ask from here.",
            LibraryState.Connected => $"Connected to {cc.PoolName} at {cc.ServerUrl}.",
            LibraryState.Unreachable => $"Can't reach {cc.ServerUrl} right now. Lectures wait here until it's back.",
            LibraryState.WrongPassword => "The library's password changed. Type the new one below.",
            _ => "No library yet.",
        };
        ModelLine = host.ModelReady ? $"{host.Model.Name} is ready."
            : host.Downloading is { } d ? $"Downloading {host.Model.Name}: {Math.Round(d.Fraction * 100)}%. {d.Left()}"
            : host.DownloadProblem ?? $"{host.Model.Name} isn't downloaded yet.";
        foreach (var m in Models)
        {
            m.Chosen = m.Model.Id == host.Model.Id;
            m.Here = WhisperModels.IsDownloaded(host.Home, m.Model);
        }
    }

    partial void OnSectionChanged(string value)
    {
        foreach (string p in new[] { nameof(OnLibrary), nameof(OnRecording), nameof(OnClasses), nameof(OnClaude), nameof(OnGeneral) }) OnPropertyChanged(p);
    }

    partial void OnWebUrlChanged(string? value) => OnPropertyChanged(nameof(HasWebUrl));
    partial void OnLanguageChanged(string value) => host.Save(s => s.Language = value.Trim());
    partial void OnComputerAudioChanged(bool value) => host.Save(s => s.ComputerAudio = value);
    partial void OnShortcutsChanged(bool value) => host.Save(s => s.Shortcuts = value);
    partial void OnStartAtLoginChanged(bool value) => Desktop.StartAtLogin(value, host.Home);

    partial void OnKeepAudioChanged(string value)
    {
        if (int.TryParse(value, out int days) && days >= 0) host.Save(s => s.KeepAudioDays = days);
    }

    partial void OnDisplayNameChanged(string value)
    {
        var cc = host.Client();
        if (cc.DisplayName == value.Trim()) return;
        cc.DisplayName = value.Trim();
        Configs.SaveClient(cc);
    }

    [RelayCommand] void Go(string section) => Section = section;

    [RelayCommand]
    async Task Connect()
    {
        LibrarySay = "Connecting…";
        string url = Setup.NormalizeAddress(Address);
        try
        {
            var health = await LibraryApi.CheckServerAsync(url, Password.Trim());
            var cc = host.Client();
            cc.ServerUrl = url;
            cc.PoolKey = Password.Trim();
            cc.PoolName = health["pool_name"]?.GetValue<string>() ?? "";
            Configs.SaveClient(cc);
            Address = url;
            Password = "";
            LibrarySay = $"Connected to {cc.PoolName}.";
            await host.CheckLibraryAsync();
        }
        catch (InvalidOperationException e)
        {
            LibrarySay = e.Message == "wrong password" ? "That password isn't right." : "Can't reach that address. Is the library's computer on, and Tailscale connected?";
        }
    }

    [RelayCommand]
    void OpenLibraryPage()
    {
        var cc = host.Client();
        if (cc.ServerUrl.Length > 0) Dialogs.OpenUrl(cc.ServerUrl);
    }

    [RelayCommand]
    void PickModel(ModelChoice choice)
    {
        host.Save(s => s.Model = choice.Model.Id);
        Refresh();
        if (!WhisperModels.IsDownloaded(host.Home, choice.Model)) _ = host.DownloadModelAsync(choice.Model);
    }

    [RelayCommand] void DownloadModel() => _ = host.DownloadModelAsync();

    [RelayCommand]
    async Task AddClass()
    {
        string name = NewClass.Trim();
        if (name.Length == 0) return;
        if (NewTimes.Trim().Length > 0 && ClassTime.ParseMany(NewTimes) is null)
        {
            ClassesSay = "Write the days and times like “Tue Thu 10:00–11:15” or “MWF 9–9:50”.";
            return;
        }
        if (host.Remote() is { } lib && !host.OlderLibrary)
        {
            try
            {
                await lib.AddClassAsync(name);
                await host.CheckLibraryAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
            {
                ClassesSay = "The library didn't take the class; it's in your timetable here.";
            }
        }
        Rows.Add(new TimetableRow { Name = name, Times = NewTimes.Trim(), Dot = Skin.ClassDot(Math.Max(0, host.ColorOf(name))) });
        NewClass = NewTimes = "";
        SaveTimetable();
    }

    [RelayCommand]
    void RemoveClass(TimetableRow row)
    {
        Rows.Remove(row);
        SaveTimetable();
    }

    [RelayCommand]
    void SaveTimetable()
    {
        var t = new Timetable();
        bool bad = false;
        foreach (var r in Rows)
        {
            var times = r.Times.Trim().Length == 0 ? [] : ClassTime.ParseMany(r.Times.Replace(",", " "));
            r.Bad = times is null;
            bad |= r.Bad;
            if (times is { Count: > 0 }) t.Classes.Add(new TimetableClass(r.Name, times));
        }
        host.SaveTimetable(t);
        ClassesSay = bad ? "Some times couldn't be read (marked): write them like “Tue Thu 10:00–11:15”." : "Saved.";
    }

    [RelayCommand]
    void AddToClaudeCode() => ClaudeSay = claude.AddToClaudeCode();

    [RelayCommand]
    void AddToClaudeDesktop() => ClaudeSay = claude.AddToClaudeDesktop();

    async Task LoadClaudeAsync()
    {
        if (host.Remote() is not { } lib) return;
        try
        {
            ShowClaude(await lib.ClaudeAsync(HttpMethod.Get));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
            ClaudeNeedsNewLibrary = e is LibraryRefusedException { Status: 404 };
        }
    }

    void ShowClaude(JsonObject? c)
    {
        if (c is null)
        {
            ClaudeNeedsNewLibrary = true;
            return;
        }
        WebUrl = c["public_url"]?.GetValue<string>();
        WebOn = WebUrl is not null;
        Connections.Clear();
        foreach (var g in (c["connections"] as JsonArray ?? []).OfType<JsonObject>())
        {
            double? used = g["last_used"] is JsonValue v && v.TryGetValue(out double t) ? t : null;
            Connections.Add(new ClaudeConnection
            {
                Id = g["id"]?.GetValue<string>() ?? "", Name = g["name"]?.GetValue<string>() ?? "Claude",
                Detail = used is double u ? $"Last used {Shell.When(DateTimeOffset.FromUnixTimeSeconds((long)u).LocalDateTime)}" : "Not used yet",
            });
        }
    }

    [RelayCommand]
    async Task ToggleWeb()
    {
        if (host.Remote() is not { } lib) return;
        WebBusy = true;
        WebSay = null;
        try
        {
            ShowClaude(await lib.ClaudeAsync(HttpMethod.Post, "/reach", new JsonObject { ["internet"] = true, ["on"] = !WebOn }));
            WebSay = WebOn ? "Claude on the web can reach your library now." : "Turned off: only your own devices reach the library.";
        }
        catch (LibraryRefusedException e)
        {
            WebSay = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            WebSay = "Your library didn't answer.";
        }
        finally
        {
            WebBusy = false;
        }
    }

    [RelayCommand]
    async Task Disconnect(ClaudeConnection c)
    {
        if (host.Remote() is not { } lib) return;
        try
        {
            ShowClaude(await lib.ClaudeAsync(HttpMethod.Delete, "/connections/" + Uri.EscapeDataString(c.Id)));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
            WebSay = "Couldn't disconnect it: the library didn't answer.";
        }
    }

    [RelayCommand] static void Quit() => Shell.Quit();

    public void Dispose() => host.Changed -= OnHostChanged;
}
