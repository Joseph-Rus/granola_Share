using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.App.ViewModels;

/// <summary>An engine in a select or a menu: what's shown, and the id posted.</summary>
public sealed record EngineChoice(string Id, string Name);

/// <summary>One row of the AI engines pane: an installed engine (or Ollama, always) with its state and what its
/// button does. The row owns its own commands, closed over its id, so a view binds them straight from the row.</summary>
public sealed partial class AiEngineRow : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string About { get; init; } = "";
    public string State { get; init; } = "";
    public string StateWords { get; init; } = "";
    public bool Good { get; init; }
    public bool Warn { get; init; }
    public bool Muted { get; init; }
    public string ActionWords { get; init; } = "";
    public bool IsPrimary { get; init; }
    /// <summary>The row's button opens the Options menu (models, Check it works) instead of acting straight away.</summary>
    public bool NeedsOptions { get; init; }
    public string Model { get; init; } = "";
    public List<ModelOption> Models { get; init; } = [];
    public string Site { get; init; } = "";

    /// <summary>Ready to shown rows: Sign in / Start / Download / Check / Get it, whichever this row's state needs.</summary>
    public IAsyncRelayCommand ActCommand { get; internal set; } = null!;
    /// <summary>"Check it works", from the Options menu (ready/limited/failed rows).</summary>
    public IAsyncRelayCommand CheckCommand { get; internal set; } = null!;
    /// <summary>Picking a model from the Options menu.</summary>
    public IAsyncRelayCommand<ModelOption> PickModelCommand { get; internal set; } = null!;
    /// <summary>Not-installed rows in the "Add an engine" menu: opens the engine's site.</summary>
    public IRelayCommand AddCommand { get; internal set; } = null!;
}

/// <summary>
/// Settings → AI engines (design 13): who writes the notes and who answers questions, every engine's state and what
/// to do about it, and the Ollama fallback. Reads and drives one library's AI (`IAiLibrary`); the view is just this.
/// </summary>
public sealed partial class AiEnginesModel : ObservableObject
{
    readonly IAiLibrary library;
    bool loading;

    public AiEnginesModel(IAiLibrary library) => this.library = library;

    public ObservableCollection<AiEngineRow> Engines { get; } = [];
    /// <summary>Not-installed engines: the "Add an engine" menu.</summary>
    public ObservableCollection<AiEngineRow> AddChoices { get; } = [];
    public List<EngineChoice> NotesChoices { get; private set; } = [];
    public List<EngineChoice> AskChoices { get; private set; } = [];

    [ObservableProperty] public partial string SelectedNotes { get; set; } = "";
    [ObservableProperty] public partial string SelectedAsk { get; set; } = "";
    [ObservableProperty] public partial bool Fallback { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string? Say { get; set; }
    [ObservableProperty] public partial bool OlderLibrary { get; set; }
    [ObservableProperty] public partial bool Offline { get; set; }

    public string SelectedNotesName => NotesChoices.FirstOrDefault(c => c.Id == SelectedNotes)?.Name ?? SelectedNotes;
    public string SelectedAskName => AskChoices.FirstOrDefault(c => c.Id == SelectedAsk)?.Name ?? SelectedAsk;
    public bool HasAddChoices => AddChoices.Count > 0;
    public string OlderLibraryWords => AiWords.OlderLibraryWords;

    /// <summary>Opens a link (the app hands this to the OS browser); null in tests.</summary>
    public Action<string>? OpenUrl { get; set; }

    partial void OnSelectedNotesChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedNotesName));
        if (!loading) _ = PostDefaultsAsync(notes: value);
    }

    partial void OnSelectedAskChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedAskName));
        if (!loading) _ = PostDefaultsAsync(ask: value);
    }

    partial void OnFallbackChanged(bool value)
    {
        if (!loading) _ = PostDefaultsAsync(fallback: value);
    }

    [RelayCommand]
    public Task Refresh() => Load();

    /// <summary>Reads the library's AI once and fills every row: called when the pane opens.</summary>
    public async Task Load()
    {
        AiOverview? overview;
        try
        {
            overview = await library.EnginesAsync();
        }
        catch
        {
            Offline = true;
            return;
        }
        Apply(overview);
    }

    void Apply(AiOverview? overview)
    {
        loading = true;
        try
        {
            if (overview is null)
            {
                OlderLibrary = true;
                Offline = false;
                return;
            }
            OlderLibrary = false;
            Offline = false;
            NotesChoices = [.. overview.Engines.Select(e => new EngineChoice(e.Id, e.Name))];
            AskChoices = NotesChoices;
            OnPropertyChanged(nameof(NotesChoices));
            OnPropertyChanged(nameof(AskChoices));
            SelectedNotes = overview.Notes;
            SelectedAsk = overview.Ask;
            Fallback = overview.Fallback;

            Engines.Clear();
            AddChoices.Clear();
            foreach (var e in overview.Engines)
            {
                var row = BuildRow(e);
                if (e.Id == "ollama" || e.Installed) Engines.Add(row);
                else AddChoices.Add(row);
            }
            OnPropertyChanged(nameof(HasAddChoices));
        }
        finally
        {
            loading = false;
        }
    }

    AiEngineRow BuildRow(EngineInfo e)
    {
        var row = new AiEngineRow
        {
            Id = e.Id,
            Name = e.Name,
            Icon = AiWords.EngineIcon(e.Id),
            About = e.Installed || e.Id == "ollama" ? AiWords.EngineAbout(e.Id, e.State) : "Install it on your library's computer.",
            State = e.State,
            StateWords = AiWords.EngineStateWords(e.State),
            Good = AiWords.StateIsGood(e.State),
            Warn = AiWords.StateIsWarn(e.State),
            Muted = AiWords.StateIsMuted(e.State),
            ActionWords = e.Installed || e.Id == "ollama" ? AiWords.RowActionWords(e.State) : "Get it",
            IsPrimary = AiWords.RowActionPrimary(e.State),
            NeedsOptions = AiWords.NeedsOptions(e.State),
            Model = e.Model,
            Models = e.Models,
            Site = e.Site,
        };
        row.ActCommand = new AsyncRelayCommand(() => RunRowActionAsync(row));
        row.CheckCommand = new AsyncRelayCommand(() => CheckAsync(row.Id));
        row.PickModelCommand = new AsyncRelayCommand<ModelOption>(m => m is null ? Task.CompletedTask : PickModelAsync(row.Id, m.Id));
        row.AddCommand = new RelayCommand(() =>
        {
            if (row.Site.Length > 0) OpenUrl?.Invoke(row.Site);
            Say = $"Install {row.Name} on your library's computer.";
        });
        return row;
    }

    async Task RunRowActionAsync(AiEngineRow row)
    {
        Busy = true;
        try
        {
            AiSaid? said = row.State switch
            {
                "not_signed_in" => await library.SignInAsync(row.Id),
                "not_running" => await library.StartAsync(row.Id),
                "model_missing" => await library.DownloadAsync(row.Id),
                "unchecked" => await library.CheckAsync(row.Id),
                _ => null,
            };
            await AfterAction(said);
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

    async Task CheckAsync(string id)
    {
        Busy = true;
        try
        {
            await AfterAction(await library.CheckAsync(id));
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

    async Task PickModelAsync(string id, string model)
    {
        Busy = true;
        try
        {
            var overview = await library.ModelAsync(id, model);
            Apply(overview);
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

    async Task AfterAction(AiSaid? said)
    {
        if (said is null)
        {
            OlderLibrary = true;
            return;
        }
        Say = said.Said;
        if (said.Overview is not null) Apply(said.Overview);
        else await Load();
    }

    async Task PostDefaultsAsync(string? notes = null, string? ask = null, bool? fallback = null)
    {
        Busy = true;
        try
        {
            var overview = await library.DefaultsAsync(notes, ask, fallback);
            Apply(overview);
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
