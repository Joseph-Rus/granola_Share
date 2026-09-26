using StudyStash.App.Services;
using StudyStash.Audio;
using StudyStash.Core;
using Mic = StudyStash.Audio.MicAccess;

namespace StudyStash.App.Tests;

/// <summary>A hand-set stand-in for <see cref="AppHost"/>'s own state, so <see cref="Problems.For"/> can be checked
/// state by state without driving a real recorder, Whisper or library.</summary>
sealed class FakeProblemSource : IProblemSource
{
    public string? RecorderProblem { get; set; }
    public Mic Access { get; set; } = Mic.Allowed;
    public Mic MicAccess() => Access;
    public string? WhisperProblem { get; set; }
    public string? DownloadProblem { get; set; }
    public bool ModelReady { get; set; } = true;
    public DownloadProgress? Downloading { get; set; }
    public AppRole Role { get; set; } = AppRole.Laptop;
    public LibraryServiceState? LocalLibraryState { get; set; }
    public string? LocalLibraryFailure { get; set; }
    public LibraryState Library { get; set; } = LibraryState.Connected;
}

/// <summary>What's wrong right now, in the order it's decided (<see cref="Problems.For"/>).</summary>
public class ProblemsTests
{
    static FakeProblemSource Ready() => new();

    [Fact]
    public void Nothing_is_wrong_once_the_library_and_the_model_are_ready() => Assert.Null(Problems.For(Ready()));

    [Fact]
    public void A_full_disk_comes_first_even_with_every_other_problem_too()
    {
        var host = Ready();
        host.RecorderProblem = RecordingWords.DiskFull;
        host.Access = Mic.Denied;
        host.ModelReady = false;
        host.Library = LibraryState.Unreachable;
        Assert.Equal(ProblemKind.DiskFull, Problems.For(host)!.Kind);
    }

    [Fact]
    public void A_denied_microphone_says_to_turn_it_on()
    {
        var host = Ready();
        host.Access = Mic.Denied;
        var p = Problems.For(host)!;
        Assert.Equal(ProblemKind.MicDenied, p.Kind);
        Assert.True(p.HasAction);
    }

    [Fact]
    public void A_restricted_microphone_is_the_same_problem_as_denied()
    {
        var host = Ready();
        host.Access = Mic.Restricted;
        Assert.Equal(ProblemKind.MicDenied, Problems.For(host)!.Kind);
    }

    [Fact]
    public void A_microphone_that_stopped_says_no_microphone()
    {
        var host = Ready();
        host.RecorderProblem = RecordingWords.MicStopped;
        var p = Problems.For(host)!;
        Assert.Equal(ProblemKind.NoMic, p.Kind);
        Assert.Equal("No microphone", p.Title);
    }

    [Fact]
    public void Whisper_that_wont_start_says_so_without_repeating_itself_in_the_detail()
    {
        var host = Ready();
        host.WhisperProblem = "Whisper couldn't start: the model file is damaged";
        var p = Problems.For(host)!;
        Assert.Equal(ProblemKind.WhisperFailed, p.Kind);
        Assert.Equal("the model file is damaged", p.Detail);
    }

    [Fact]
    public void A_stopped_download_can_be_tried_again()
    {
        var host = Ready();
        host.ModelReady = false;
        host.DownloadProblem = AppHost.DownloadStopped;
        var p = Problems.For(host)!;
        Assert.Equal(ProblemKind.DownloadFailed, p.Kind);
        Assert.Equal("Try again", p.ActionLabel);
    }

    [Fact]
    public void No_model_and_nothing_downloading_offers_to_download_it()
    {
        var host = Ready();
        host.ModelReady = false;
        Assert.Equal(ProblemKind.NoModel, Problems.For(host)!.Kind);
    }

    [Fact]
    public void A_model_thats_downloading_has_no_action_button()
    {
        var host = Ready();
        host.ModelReady = false;
        host.Downloading = new DownloadProgress(1_900_000_000, 3_100_000_000, 0);
        var p = Problems.For(host)!;
        Assert.Equal(ProblemKind.Downloading, p.Kind);
        Assert.False(p.HasAction);
    }

    [Fact]
    public void This_computers_own_library_that_stopped_can_be_started_again()
    {
        var host = Ready();
        host.Role = AppRole.Both;
        host.LocalLibraryState = LibraryServiceState.Failed;
        host.LocalLibraryFailure = "it couldn't bind its port";
        var p = Problems.For(host)!;
        Assert.Equal(ProblemKind.LibraryStopped, p.Kind);
        Assert.Equal("it couldn't bind its port", p.Detail);
    }

    [Fact]
    public void A_laptop_with_no_library_of_its_own_never_shows_library_stopped()
    {
        // Role stays Laptop: LocalLibraryState is only ever set for Both/Library, but the check itself must still
        // require the role, not just the state, to be a library problem.
        var host = Ready();
        host.LocalLibraryState = LibraryServiceState.Failed;
        Assert.NotEqual(ProblemKind.LibraryStopped, Problems.For(host)?.Kind);
    }

    [Fact]
    public void A_changed_password_says_so()
    {
        var host = Ready();
        host.Library = LibraryState.WrongPassword;
        Assert.Equal(ProblemKind.WrongPassword, Problems.For(host)!.Kind);
    }

    [Fact]
    public void An_unreachable_library_says_lectures_wait()
    {
        var host = Ready();
        host.Library = LibraryState.Unreachable;
        var p = Problems.For(host)!;
        Assert.Equal(ProblemKind.Unreachable, p.Kind);
        Assert.Contains("wait", p.Detail);
    }

    [Fact]
    public void No_library_at_all_points_at_settings()
    {
        var host = Ready();
        host.Library = LibraryState.NotSetUp;
        Assert.Equal(ProblemKind.NotSetUp, Problems.For(host)!.Kind);
    }

    [Fact]
    public void Starting_the_librarys_own_state_isnt_a_problem_by_itself()
    {
        var host = Ready();
        host.Library = LibraryState.Starting;
        Assert.Null(Problems.For(host));
    }

    [Theory]
    [InlineData(LibraryState.Connected, false, true, null, "Library connected · Model ready")]
    [InlineData(LibraryState.Unreachable, false, true, null, "Can't reach your library · Model ready")]
    [InlineData(LibraryState.NotSetUp, false, false, null, "No library yet · No transcription model")]
    public void The_dropdowns_status_line_reads_exactly(LibraryState library, bool localLibraryRunning, bool modelReady, DownloadProgress? downloading, string expected) =>
        Assert.Equal(expected, AppHost.StatusText(library, localLibraryRunning, modelReady, downloading));
}
