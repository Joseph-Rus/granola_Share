using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

/// <summary>An AI the library can use (Claude, ChatGPT, Gemini, a local model), or one of its models.</summary>
public sealed partial class AiChoiceRow : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    [ObservableProperty] public partial string About { get; set; } = "";
    [ObservableProperty] public partial bool Chosen { get; set; }
    [ObservableProperty] public partial bool Works { get; set; }
}

/// <summary>A Canvas course a class can be linked to ("Not on Canvas" has id 0).</summary>
public sealed record CanvasCourse(long Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A class and the Canvas course it is.</summary>
public sealed partial class CanvasLink : ObservableObject
{
    public string ClassName { get; init; } = "";
    public List<CanvasCourse> Courses { get; init; } = [];
    [ObservableProperty] public partial CanvasCourse? Course { get; set; }
    public Func<CanvasLink, Task>? OnChanged { get; set; }
    partial void OnCourseChanged(CanvasCourse? value) => OnChanged?.Invoke(this);
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
    public bool CanRecordComputerAudio => host.CanRecordComputerAudio;

    // Classes
    public ObservableCollection<TimetableRow> Rows { get; } = [];
    [ObservableProperty] public partial string NewClass { get; set; } = "";
    [ObservableProperty] public partial string NewTimes { get; set; } = "";
    [ObservableProperty] public partial string? ClassesSay { get; set; }

    // AI
    public ObservableCollection<AiChoiceRow> Ais { get; } = [];
    public ObservableCollection<AiChoiceRow> AiModels { get; } = [];
    [ObservableProperty] public partial string? AiSay { get; set; }
    [ObservableProperty] public partial bool AiBusy { get; set; }
    public bool HasAiModels => AiModels.Count > 1;

    // Canvas
    [ObservableProperty] public partial string CanvasUrl { get; set; } = "";
    [ObservableProperty] public partial string? CanvasSay { get; set; }
    [ObservableProperty] public partial string CanvasLine { get; set; } = "";
    [ObservableProperty] public partial bool CanvasBusy { get; set; }
    public ObservableCollection<CanvasLink> CanvasLinks { get; } = [];
    public bool HasCanvasLinks => CanvasLinks.Count > 0;

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
    public bool OnAi => Section == "AI";
    public bool OnCanvas => Section == "Canvas";
    public bool OnClaude => Section == "Claude";
    public bool OnGeneral => Section == "General";
    public bool HasWebUrl => !string.IsNullOrEmpty(WebUrl);
    public bool HasConnections => Connections.Count > 0;

    public static SettingsModel Make(AppHost host) => new(host);

    /// <summary>True while the constructor fills in what's already set: nothing is saved, and starting at login isn't
    /// touched, until the student changes something.</summary>
    readonly bool loading = true;
    /// <summary>Putting the box back after the login item couldn't be changed.</summary>
    bool settingLogin;

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
        StartAtLogin = host.LoginItems.StartsAtLogin(host.Home);
        ClaudeCommand = claude.ClaudeCodeCommand;
        foreach (var m in WhisperModels.All.Where(m => m.Id != WhisperModels.Tiny.Id))
            Models.Add(new ModelChoice { Model = m, Chosen = m.Id == host.Model.Id, Here = WhisperModels.IsDownloaded(host.Home, m) });
        foreach (var c in host.Timetable.Classes)
            Rows.Add(new TimetableRow { Name = c.Name, Times = string.Join(", ", c.Times.Select(t => t.Describe())), Dot = Skin.ClassDot(Math.Max(0, host.ColorOf(c.Name))) });
        foreach (var (name, color, _) in host.Classes().Where(c => Rows.All(r => r.Name != c.Name)))
            Rows.Add(new TimetableRow { Name = name, Dot = Skin.ClassDot(color) });
        Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasConnections));
        host.Changed += OnHostChanged;
        AiModels.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAiModels));
        CanvasLinks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasCanvasLinks));
        loading = false;
        _ = LoadCanvasAsync();
        Refresh();
        _ = LoadClaudeAsync();
        _ = LoadAiAsync();
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
        foreach (string p in new[] { nameof(OnLibrary), nameof(OnRecording), nameof(OnClasses), nameof(OnAi), nameof(OnCanvas), nameof(OnClaude), nameof(OnGeneral) }) OnPropertyChanged(p);
    }

    partial void OnWebUrlChanged(string? value) => OnPropertyChanged(nameof(HasWebUrl));
    partial void OnLanguageChanged(string value)
    {
        if (!loading) host.Save(s => s.Language = value.Trim());
    }

    partial void OnComputerAudioChanged(bool value)
    {
        if (!loading) host.Save(s => s.ComputerAudio = value);
    }

    partial void OnShortcutsChanged(bool value)
    {
        if (!loading) host.Save(s => s.Shortcuts = value);
    }

    /// <summary>Only the student's own tick adds (or takes away) the login item.</summary>
    partial void OnStartAtLoginChanged(bool value)
    {
        if (loading || settingLogin) return;
        try
        {
            host.LoginItems.StartAtLogin(value, host.Home);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            host.Log($"[app] start at login: {e.Message}");
            settingLogin = true;
            StartAtLogin = !value;
            settingLogin = false;
        }
    }

    partial void OnKeepAudioChanged(string value)
    {
        if (!loading && int.TryParse(value, out int days) && days >= 0) host.Save(s => s.KeepAudioDays = days);
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (loading) return;
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

    // --- AI ---------------------------------------------------------------------------------------------------------

    async Task LoadAiAsync() => await AiCallAsync(lib => lib.AiAsync(HttpMethod.Get));

    /// <summary>Ask the library, show what it says, and say what went wrong when it can't.</summary>
    async Task AiCallAsync(Func<RemoteLibrary, Task<JsonObject?>> call, string? done = null)
    {
        if (host.Remote() is not { } lib)
        {
            AiSay = "Connect to your library first.";
            return;
        }
        AiBusy = true;
        try
        {
            var r = await call(lib);
            ShowAi(r);
            AiSay = r?["tested"] is not null
                ? r["ok"]?.GetValue<bool>() == true ? "It works." : $"It didn't answer: {r["why"]?.GetValue<string>()}"
                : done;
        }
        catch (LibraryRefusedException e)
        {
            AiSay = e.Status == 404 ? "Your library runs an older Study Stash: update it to pick its AI here." : e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            AiSay = "Your library didn't answer.";
        }
        finally
        {
            AiBusy = false;
        }
    }

    void ShowAi(JsonObject? a)
    {
        if (a is null) return;
        string chosen = a["provider"]?.GetValue<string>() ?? "ollama";
        string model = a["jobs"]?["agent"]?["model"]?.GetValue<string>() ?? "";
        Ais.Clear();
        AiModels.Clear();
        foreach (var p in (a["providers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string id = p["id"]?.GetValue<string>() ?? "";
            bool installed = p["installed"]?.GetValue<bool>() == true;
            string test = p["test"]?.GetValue<string>() ?? "";
            Ais.Add(new AiChoiceRow
            {
                Id = id, Name = p["name"]?.GetValue<string>() ?? id, Chosen = id == chosen, Works = test == "works",
                About = !installed ? $"Not on the library's computer yet: {p["site"]?.GetValue<string>()}"
                    : id == "ollama" ? "On the library's computer: private and free."
                    : test is "works" or "" ? "With your own account and plan." : $"Last try failed: {test}",
            });
            if (id != chosen) continue;
            foreach (var m in (p["models"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string mid = m["id"]?.GetValue<string>() ?? "";
                AiModels.Add(new AiChoiceRow { Id = mid, Name = m["label"]?.GetValue<string>() ?? mid, Chosen = mid == model });
            }
        }
    }

    [RelayCommand]
    Task PickAi(AiChoiceRow ai) => AiCallAsync(lib => lib.AiAsync(HttpMethod.Post, "", new JsonObject { ["provider"] = ai.Id }), $"{ai.Name} does the library's work now.");

    [RelayCommand]
    Task PickAiModel(AiChoiceRow m) => AiCallAsync(lib => lib.AiAsync(HttpMethod.Post, "", new JsonObject { ["model"] = m.Id }), "Saved.");

    [RelayCommand]
    Task TestAi()
    {
        AiSay = "Trying it…";
        return AiCallAsync(lib => lib.AiAsync(HttpMethod.Post, "/test", new JsonObject()));
    }

    // --- Canvas -----------------------------------------------------------------------------------------------------

    async Task<JsonObject?> CanvasCallAsync(Func<RemoteLibrary, Task<JsonObject?>> call)
    {
        if (host.Remote() is not { } lib)
        {
            CanvasSay = "Connect to your library first.";
            return null;
        }
        CanvasBusy = true;
        try
        {
            var r = await call(lib);
            if (r is null) CanvasSay = "Your library runs an older Study Stash: update it to use Canvas.";
            else ShowCanvas(r);
            return r;
        }
        catch (LibraryRefusedException e)
        {
            CanvasSay = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            CanvasSay = "Your library didn't answer.";
        }
        finally
        {
            CanvasBusy = false;
        }
        return null;
    }

    Task LoadCanvasAsync() => CanvasCallAsync(lib => lib.CanvasSettingsAsync(HttpMethod.Get));

    bool showingCanvas;

    void ShowCanvas(JsonObject c)
    {
        showingCanvas = true;
        CanvasUrl = c["url"]?.GetValue<string>() ?? "";
        var available = (c["available"] as JsonObject ?? []).Select(kv => new CanvasCourse(long.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv.Value?.GetValue<string>() ?? kv.Key))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var linked = (c["courses"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<long>());
        CanvasLinks.Clear();
        foreach (var (name, _, _) in host.Classes())
        {
            var options = new List<CanvasCourse> { new(0, "Not on Canvas") };
            options.AddRange(available);
            if (linked.TryGetValue(name, out long id) && options.All(o => o.Id != id)) options.Add(new CanvasCourse(id, $"Course {id}"));
            var link = new CanvasLink { ClassName = name, Courses = options, Course = options.First(o => o.Id == linked.GetValueOrDefault(name)) };
            link.OnChanged = LinkChangedAsync;
            CanvasLinks.Add(link);
        }
        string error = c["error"]?.GetValue<string>() ?? "";
        string seen = c["extension_seen"]?.GetValue<string>() ?? "";
        bool live = DateTimeOffset.TryParse(seen, out var t) && DateTimeOffset.Now - t < TimeSpan.FromMinutes(5);
        bool syncing = c["syncing"]?.GetValue<bool>() == true;
        CanvasLine = error.Length > 0 ? error
            : syncing ? $"Syncing Canvas… {c["left"]} left."
            : !live ? (seen.Length == 0 ? "The Chrome extension isn't set up yet." : "Chrome hasn't checked in lately. Is it open?")
            : DateTimeOffset.TryParse(c["last_sync"]?.GetValue<string>(), out var last) ? $"Chrome is connected. Last sync {Shell.When(last.LocalDateTime)}." : "Chrome is connected.";
        showingCanvas = false;
    }

    async Task LinkChangedAsync(CanvasLink link)
    {
        if (showingCanvas) return;
        await CanvasCallAsync(lib => lib.CanvasSettingsAsync(HttpMethod.Post, "", new JsonObject
        {
            ["courses"] = new JsonObject { [link.ClassName] = link.Course?.Id ?? 0 }, ["sync"] = true,
        }));
        CanvasSay = link.Course is { Id: > 0 } c ? $"{link.ClassName} is {c.Name} on Canvas. It syncs within a minute." : $"{link.ClassName} isn't linked to Canvas now.";
    }

    [RelayCommand]
    async Task SaveCanvasUrl()
    {
        if (await CanvasCallAsync(lib => lib.CanvasSettingsAsync(HttpMethod.Post, "", new JsonObject { ["url"] = CanvasUrl })) is not null)
            CanvasSay = "Saved.";
    }

    /// <summary>Write the extension's folder on this computer, pointed at the library, and open Chrome's extensions page.</summary>
    [RelayCommand]
    async Task SetUpExtension()
    {
        var key = await CanvasCallAsync(lib => lib.CanvasSettingsAsync(HttpMethod.Get, "/extension"));
        if (key is null) return;
        string url = key["canvas"]?.GetValue<string>() ?? "";
        if (url.Length == 0)
        {
            CanvasSay = "Add your school's Canvas address first.";
            await LoadCanvasAsync();
            return;
        }
        string dir = Core.Canvas.Extension.Prepare(Core.Canvas.Extension.Folder(host.Home), host.Client().ServerUrl, key["key"]!.GetValue<string>(), url);
        await LoadCanvasAsync();
        CanvasSay = $"Ready. In Chrome: turn on Developer mode, click Load unpacked, and choose {dir} (it's open in Finder).";
        try
        {
            Machine.Open(dir);
            if (OperatingSystem.IsMacOS()) Machine.Run("open", ["-a", "Google Chrome", "chrome://extensions"], TimeSpan.FromSeconds(10));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    [RelayCommand]
    async Task FindCourses()
    {
        CanvasSay = "Asking Canvas through Chrome…";
        var r = await CanvasCallAsync(lib => lib.CanvasSettingsAsync(HttpMethod.Post, "/courses"));
        if (r is null) return;
        if (r["error"] is JsonValue e) CanvasSay = e.GetValue<string>();
        else
        {
            AutoLink(r);
            CanvasSay = "Found your courses. Check each class's below.";
        }
    }

    /// <summary>Link classes that aren't linked yet to the course whose name contains the class's name.</summary>
    void AutoLink(JsonObject r)
    {
        foreach (var link in CanvasLinks.Where(l => l.Course is null or { Id: 0 }))
        {
            string want = link.ClassName.ToLowerInvariant();
            var match = link.Courses.Where(c => c.Id > 0 && c.Name.Contains(want, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 1) link.Course = match[0];
        }
    }

    [RelayCommand]
    Task SyncCanvas() => CanvasCallAsync(lib => lib.CanvasSettingsAsync(HttpMethod.Post, "", new JsonObject { ["sync"] = true }))
        .ContinueWith(_ => CanvasSay = "Syncing on Chrome's next check, within a minute.", TaskScheduler.FromCurrentSynchronizationContext());

    [RelayCommand] static void Quit() => Shell.Quit();

    public void Dispose() => host.Changed -= OnHostChanged;
}
