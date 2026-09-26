using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.App.ViewModels;

/// <summary>One connected tool: a library grant (signed in from the web, or a token made in Settings), or this
/// computer's own Claude Code / Claude Desktop. The row owns its own Remove, closed over its id.</summary>
public sealed class AiConnectionRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"Signed in from the web" · "Token" · "This computer".</summary>
    public string Detail { get; init; } = "";
    public string UsedWords { get; init; } = "";
    public bool HasUsedWords => UsedWords.Length > 0;
    /// <summary>Windows keeps the used-time in the one subtitle line, Mac on the row's own right side.</summary>
    public string WinDetail => HasUsedWords ? $"{Detail} · {UsedWords}" : Detail;
    public bool CanRemove { get; init; }
    public IAsyncRelayCommand? Remove { get; internal set; }
    /// <summary>terminal for Claude, code for Codex, hub for anything else — the row's icon tile.</summary>
    public string Icon => Name switch { "Claude Code" or "Claude" or "Claude Desktop" => "terminal", "Codex" => "code", _ => "hub" };
    /// <summary>The first row in the list shows no separator above it.</summary>
    public bool First { get; set; }
}

/// <summary>
/// Settings → AI tool access (design 14): the off switch, what Claude and other MCP tools may read, connecting a
/// tool, and the connected list. Reads and drives one library's AI (<see cref="IAiLibrary"/>); the two things it
/// can't do through that (checking and changing what's on this computer's own Claude Code / Claude Desktop) go
/// through hooks the host sets, so this model never touches a file or a process itself.
/// </summary>
public sealed partial class AiAccessModel : ObservableObject
{
    readonly IAiLibrary library;
    readonly Func<DateTime> now;
    bool loading;

    public AiAccessModel(IAiLibrary library, Func<DateTime>? now = null)
    {
        this.library = library;
        this.now = now ?? (() => DateTime.Now);
    }

    [ObservableProperty] public partial bool On { get; set; } = true;
    [ObservableProperty] public partial bool ReadLectures { get; set; } = true;
    [ObservableProperty] public partial bool ReadNotes { get; set; } = true;
    [ObservableProperty] public partial bool ReadCanvas { get; set; } = true;
    [ObservableProperty] public partial bool ReadAudio { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string? Say { get; set; }
    [ObservableProperty] public partial bool OlderLibrary { get; set; }
    [ObservableProperty] public partial bool Offline { get; set; }
    [ObservableProperty] public partial bool InClaudeCode { get; set; }
    [ObservableProperty] public partial bool InClaudeDesktop { get; set; }
    [ObservableProperty] public partial string? PublicUrl { get; set; }
    [ObservableProperty] public partial bool HasPassword { get; set; }

    public ObservableCollection<AiConnectionRow> Connected { get; } = [];
    public bool HasConnections => Connected.Count > 0;

    /// <summary>What to type into Claude Code's own terminal, and what to paste into Codex's config.toml — the
    /// host fills these in once, from <c>ClaudeSetup</c>.</summary>
    public string ClaudeCodeCommand { get; set; } = "";
    public string CodexSetup { get; set; } = "";
    public string McpJson { get; set; } = "";

    /// <summary>Puts text on the clipboard (write-only: this model never reads it).</summary>
    public Func<string, Task>? Copy { get; set; }
    /// <summary>Whether Claude Code / Claude Desktop already have Study Stash, asked fresh each <see cref="Load"/>.</summary>
    public Func<bool>? CheckInClaudeCode { get; set; }
    public Func<bool>? CheckInClaudeDesktop { get; set; }
    /// <summary>Adds Study Stash to Claude Desktop's own config. Answers what to say.</summary>
    public Func<Task<string>>? AddToClaudeDesktop { get; set; }
    /// <summary>Takes Study Stash out of Claude Desktop's own config. Answers what to say.</summary>
    public Func<Task<string>>? RemoveFromClaudeDesktopHook { get; set; }
    /// <summary>Turns on Claude's address on the web (POST /api/v2/claude/reach). The new address, or null if it
    /// couldn't.</summary>
    public Func<Task<string?>>? TurnOnWeb { get; set; }
    /// <summary>Revokes one library grant (DELETE /api/v2/claude/connections/{id}). Whether it worked.</summary>
    public Func<string, Task<bool>>? RevokeConnection { get; set; }

    partial void OnOnChanged(bool value)
    {
        if (!loading) _ = PostAsync(on: value);
    }

    partial void OnReadLecturesChanged(bool value) => PostReading();
    partial void OnReadNotesChanged(bool value) => PostReading();
    partial void OnReadCanvasChanged(bool value) => PostReading();
    partial void OnReadAudioChanged(bool value) => PostReading();

    void PostReading()
    {
        if (!loading) _ = PostAsync(reading: new ReadingScopes(ReadLectures, ReadNotes, ReadCanvas, ReadAudio));
    }

    [RelayCommand] public void ToggleLectures() => ReadLectures = !ReadLectures;
    [RelayCommand] public void ToggleNotes() => ReadNotes = !ReadNotes;
    [RelayCommand] public void ToggleCanvas() => ReadCanvas = !ReadCanvas;
    [RelayCommand] public void ToggleAudio() => ReadAudio = !ReadAudio;

    [RelayCommand]
    public Task Refresh() => Load();

    /// <summary>Reads the library's tool access, and this computer's own Claude Code / Claude Desktop, once: called
    /// when the pane opens.</summary>
    public async Task Load()
    {
        InClaudeCode = CheckInClaudeCode?.Invoke() ?? false;
        InClaudeDesktop = CheckInClaudeDesktop?.Invoke() ?? false;
        ToolAccessInfo? info;
        try
        {
            info = await library.AccessAsync();
        }
        catch
        {
            Offline = true;
            return;
        }
        Apply(info);
    }

    void Apply(ToolAccessInfo? info)
    {
        loading = true;
        try
        {
            if (info is null)
            {
                OlderLibrary = true;
                Offline = false;
                return;
            }
            OlderLibrary = false;
            Offline = false;
            On = info.On;
            ReadLectures = info.Reading.Lectures;
            ReadNotes = info.Reading.Notes;
            ReadCanvas = info.Reading.Canvas;
            ReadAudio = info.Reading.Audio;
            PublicUrl = info.PublicUrl;
            HasPassword = info.HasPassword;

            List<AiConnectionRow> rows = [];
            foreach (var c in info.Connections)
            {
                var row = new AiConnectionRow
                {
                    Id = c.Id,
                    Name = c.Name,
                    Detail = c.Kind == "signin" ? "Signed in from the web" : "Token",
                    UsedWords = c.LastUsed is double lu ? AiWords.UsedWords(Epoch(lu), now()) : "",
                    CanRemove = true,
                };
                row.Remove = new AsyncRelayCommand(() => RemoveAsync(row.Id));
                rows.Add(row);
            }
            if (InClaudeCode) rows.Add(new AiConnectionRow { Id = "claude-code", Name = "Claude Code", Detail = "This computer" });
            if (InClaudeDesktop) rows.Add(NewDesktopRow());
            if (rows.Count > 0) rows[0].First = true;
            Connected.Clear();
            foreach (var row in rows) Connected.Add(row);
            OnPropertyChanged(nameof(HasConnections));
        }
        finally
        {
            loading = false;
        }
    }

    static DateTime Epoch(double seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).LocalDateTime;

    AiConnectionRow NewDesktopRow()
    {
        var row = new AiConnectionRow { Id = "claude-desktop", Name = "Claude Desktop", Detail = "This computer", CanRemove = true };
        row.Remove = new AsyncRelayCommand(RemoveDesktopAsync);
        return row;
    }

    [RelayCommand]
    public async Task CopyClaudeCode()
    {
        if (Copy is null) return;
        await Copy(ClaudeCodeCommand);
        Say = "Copied. Paste it into a terminal and press Return.";
    }

    [RelayCommand]
    public async Task CopyCodex()
    {
        if (Copy is null) return;
        await Copy(CodexSetup);
        Say = "Copied. Paste it into ~/.codex/config.toml.";
    }

    [RelayCommand]
    public async Task CopyOther()
    {
        if (Copy is null) return;
        await Copy(McpJson);
        Say = "Copied. Paste it into the tool's settings.";
    }

    [RelayCommand]
    public async Task AddClaudeDesktop()
    {
        if (AddToClaudeDesktop is null) return;
        Say = await AddToClaudeDesktop();
        InClaudeDesktop = CheckInClaudeDesktop?.Invoke() ?? InClaudeDesktop;
        if (InClaudeDesktop && Connected.All(c => c.Id != "claude-desktop"))
        {
            var row = NewDesktopRow();
            row.First = Connected.Count == 0;
            Connected.Add(row);
            OnPropertyChanged(nameof(HasConnections));
        }
    }

    [RelayCommand]
    public async Task TurnOnClaudeWeb()
    {
        if (TurnOnWeb is null) return;
        Busy = true;
        try
        {
            var url = await TurnOnWeb();
            PublicUrl = url;
            Say = url is not null ? "Claude on the web can reach your library now." : "Couldn't turn on Claude on the web.";
        }
        finally
        {
            Busy = false;
        }
    }

    async Task RemoveAsync(string id)
    {
        if (RevokeConnection is null) return;
        Busy = true;
        try
        {
            if (await RevokeConnection(id)) await Load();
        }
        finally
        {
            Busy = false;
        }
    }

    async Task RemoveDesktopAsync()
    {
        if (RemoveFromClaudeDesktopHook is null) return;
        Busy = true;
        try
        {
            Say = await RemoveFromClaudeDesktopHook();
            InClaudeDesktop = CheckInClaudeDesktop?.Invoke() ?? false;
            var row = Connected.FirstOrDefault(c => c.Id == "claude-desktop");
            if (!InClaudeDesktop && row is not null)
            {
                Connected.Remove(row);
                OnPropertyChanged(nameof(HasConnections));
            }
        }
        finally
        {
            Busy = false;
        }
    }

    async Task PostAsync(bool? on = null, ReadingScopes? reading = null)
    {
        Busy = true;
        try
        {
            var info = await library.SetAccessAsync(on, reading);
            Apply(info);
        }
        catch (LibraryRefusedException ex)
        {
            Say = ex.Message;
        }
        catch
        {
            Offline = true;
        }
        finally
        {
            Busy = false;
        }
    }
}
