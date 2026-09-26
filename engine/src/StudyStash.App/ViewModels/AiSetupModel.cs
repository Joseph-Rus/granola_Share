using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.App.ViewModels;

/// <summary>One engine in the library setup step's "Choose who writes your notes" list: a radio row, Ollama marked
/// Recommended, a not-signed-in CLI offering to sign in right there.</summary>
public sealed partial class AiSetupRow : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string About { get; init; } = "";
    public bool Recommended { get; init; }
    public bool ShowSignIn { get; init; }
    [ObservableProperty] public partial bool Selected { get; set; }

    public IRelayCommand SelectCommand { get; internal set; } = null!;
    public IAsyncRelayCommand SignInCommand { get; internal set; } = null!;
}

/// <summary>
/// The library setup wizard's AI step (design 15): "Choose who writes your notes" (a radio list of the engines on
/// this computer, Ollama recommended) and "Who answers your questions" (a select that starts as Same as notes). The
/// step's body only: the host's sidebar and Back/Continue footer are its own. Continue calls <see cref="SaveAsync"/>.
/// </summary>
public sealed partial class AiSetupModel : ObservableObject
{
    /// <summary>The Ask select's first choice: follow whichever engine writes the notes.</summary>
    public const string SameAsNotes = "";

    readonly IAiLibrary library;

    public AiSetupModel(IAiLibrary library) => this.library = library;

    public ObservableCollection<AiSetupRow> Engines { get; } = [];
    public List<EngineChoice> AskChoices { get; private set; } = [new(SameAsNotes, "Same as notes")];

    [ObservableProperty] public partial string SelectedNotes { get; set; } = "ollama";
    [ObservableProperty] public partial string SelectedAsk { get; set; } = SameAsNotes;
    [ObservableProperty] public partial string? Say { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial bool OlderLibrary { get; set; }
    [ObservableProperty] public partial bool Offline { get; set; }

    public string SelectedAskName => AskChoices.FirstOrDefault(c => c.Id == SelectedAsk)?.Name ?? SelectedAsk;
    public string OlderLibraryWords => AiWords.OlderLibraryWords;

    partial void OnSelectedAskChanged(string value) => OnPropertyChanged(nameof(SelectedAskName));

    /// <summary>Reads the library's AI once and fills the rows: called when the step opens.</summary>
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
        if (overview is null)
        {
            OlderLibrary = true;
            Offline = false;
            return;
        }
        OlderLibrary = false;
        Offline = false;
        SelectedNotes = overview.Notes;
        SelectedAsk = overview.Ask == overview.Notes ? SameAsNotes : overview.Ask;
        AskChoices = [new EngineChoice(SameAsNotes, "Same as notes"), .. overview.Engines.Select(e => new EngineChoice(e.Id, e.Name))];
        OnPropertyChanged(nameof(AskChoices));
        OnPropertyChanged(nameof(SelectedAskName));

        Engines.Clear();
        foreach (var e in overview.Engines.Where(e => e.Id == "ollama" || e.Installed))
        {
            var row = new AiSetupRow
            {
                Id = e.Id,
                Name = e.Name,
                About = AiWords.SetupAbout(e.Id, e.State),
                Recommended = e.Id == "ollama",
                ShowSignIn = e.State == "not_signed_in",
                Selected = e.Id == SelectedNotes,
            };
            row.SelectCommand = new RelayCommand(() => SelectedNotes = row.Id);
            row.SignInCommand = new AsyncRelayCommand(() => SignInAsync(row.Id));
            Engines.Add(row);
        }
    }

    partial void OnSelectedNotesChanged(string value)
    {
        foreach (var row in Engines) row.Selected = row.Id == value;
    }

    async Task SignInAsync(string id)
    {
        Busy = true;
        try
        {
            var said = await library.SignInAsync(id);
            if (said is null) { OlderLibrary = true; return; }
            Say = said.Said;
            if (said.Overview is not null) Apply(said.Overview);
            else await Load();
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

    /// <summary>The setup host calls this on Continue: saves the pick (ask follows notes when the student left it
    /// as Same as notes) and says whether it worked.</summary>
    public async Task<bool> SaveAsync()
    {
        Busy = true;
        try
        {
            string ask = SelectedAsk == SameAsNotes ? SelectedNotes : SelectedAsk;
            var overview = await library.DefaultsAsync(notes: SelectedNotes, ask: ask);
            if (overview is null) { OlderLibrary = true; return false; }
            return true;
        }
        catch (LibraryRefusedException ex)
        {
            Say = ex.Message;
            return false;
        }
        catch
        {
            Offline = true;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
