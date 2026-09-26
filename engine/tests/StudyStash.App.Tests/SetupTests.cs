using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StudyStash.App.Platform;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;
using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App.Tests;

/// <summary>Setup: what this computer is for, and the microphone check on its first step.</summary>
public sealed class SetupTests
{
    static AppHost Host(TempHome home, Func<IAudioSource>? mic = null, ILoginItems? login = null) =>
        new(home.Path, mic, log: _ => { }, loginItems: login ?? new CountingLoginItems());

    /// <summary>A minimal server on a free port that always answers /api/health with one status: enough to check
    /// what Setup says about it, with no real library behind it.</summary>
    static async Task<(string Url, WebApplication App)> FakeServerAsync(HttpStatusCode status)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebHostBuilderKestrelExtensions.ConfigureKestrel(builder.WebHost, k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/api/health", () => Results.StatusCode((int)status));
        await app.StartAsync();
        string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (url, app);
    }

    [Fact]
    public void Laptop_and_Both_keep_the_microphone_and_model_library_only_skips_them()
    {
        var m = SetupModel.For(SkinKind.Mac);
        Assert.Equal(["Microphone", "Library", "Transcription model", "Classes"], m.Steps.Select(s => s.Title));
        Assert.Equal([1, 2, 3, 4], m.Steps.Select(s => s.Number));

        m.SetRole(AppRole.Both, SkinKind.Mac);
        Assert.Equal(["Microphone", "Library", "Transcription model", "Classes"], m.Steps.Select(s => s.Title));

        m.SetRole(AppRole.Library, SkinKind.Mac);
        Assert.Equal(["Library", "Classes"], m.Steps.Select(s => s.Title));
        Assert.Equal([1, 2], m.Steps.Select(s => s.Number));
    }

    [Fact]
    public void Windows_adds_a_taskbar_step_in_every_role()
    {
        var m = SetupModel.For(SkinKind.Win);
        Assert.Equal(SetupStep.Taskbar, m.Steps[^1].Step);

        m.SetRole(AppRole.Library, SkinKind.Win);
        Assert.Equal(["Library", "Classes", "Taskbar"], m.Steps.Select(s => s.Title));
    }

    [Fact]
    public void Back_and_next_keep_the_step_checks_right()
    {
        var m = SetupModel.For(SkinKind.Mac); // Microphone
        m.NextCommand.Execute(null); // -> Library
        Assert.True(m.Steps[0].Done);
        Assert.False(m.Steps[1].Done);

        m.NextCommand.Execute(null); // -> Model
        m.NextCommand.Execute(null); // -> Classes
        Assert.True(m.Steps[0].Done);
        Assert.True(m.Steps[1].Done);
        Assert.True(m.Steps[2].Done);

        m.BackCommand.Execute(null); // -> Model: no longer done, even though it was
        Assert.True(m.Steps[0].Done);
        Assert.True(m.Steps[1].Done);
        Assert.False(m.Steps[2].Done);
    }

    [Fact]
    public void The_microphone_steps_link_skips_straight_to_a_library_only_setup()
    {
        var m = SetupModel.For(SkinKind.Mac);
        m.PickOnlyLibraryCommand.Execute(null);

        Assert.Equal(AppRole.Library, m.Role);
        Assert.True(m.OnlyLibrary);
        Assert.True(m.ThisComputer);
        Assert.Equal(SetupStep.Library, m.Step);
        Assert.DoesNotContain(m.Steps, s => s.Step is SetupStep.Microphone or SetupStep.Model);
    }

    [Fact]
    public async Task Library_only_starts_no_model_download()
    {
        using var home = new TempHome();
        using var host = Host(home);
        var m = Setup.Make(host);

        m.PickOnlyLibraryCommand.Execute(null);
        m.NextCommand.Execute(null); // Library isn't connected yet, so Next only re-says that: no download starts

        Assert.False(host.ModelReady);
        Assert.Null(host.Downloading);
        await Task.Delay(50, TestContext.Current.CancellationToken); // nothing was ever scheduled to start
        Assert.Null(host.Downloading);
    }

    [Fact]
    public void Finish_saves_role_and_setup_done_and_only_toggles_login_when_ticked()
    {
        using var home = new TempHome();
        var login = new CountingLoginItems();
        using var host = Host(home, login: login);
        var m = Setup.Make(host);
        m.SetRole(AppRole.Both, SkinKind.Mac);

        m.StartAtLogin = false;
        Setup.Finish(m, host);
        Assert.True(AppSettings.Load(home.Path).SetupDone);
        Assert.Equal(AppRole.Both, AppSettings.Load(home.Path).Role);
        Assert.Empty(login.Calls);

        m.StartAtLogin = true;
        Setup.Finish(m, host);
        Assert.Equal([true], login.Calls);
    }

    [Fact]
    public async Task Connecting_elsewhere_with_the_wrong_password_says_so()
    {
        var (url, app) = await FakeServerAsync(HttpStatusCode.Unauthorized);
        try
        {
            using var home = new TempHome();
            using var host = Host(home);
            var m = Setup.Make(host);
            m.Address = url;
            m.Password = "whatever";

            await m.ConnectCommand.ExecuteAsync(null);

            Assert.False(m.LibraryOk);
            Assert.Equal("That password isn't right.", m.LibraryResult);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Adding_an_already_shown_class_sets_its_times_instead_of_posting_a_duplicate()
    {
        int posts = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebHostBuilderKestrelExtensions.ConfigureKestrel(builder.WebHost, k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapPost("/classes", () => { posts++; return Results.Ok(new { }); });
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            string url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var home = new TempHome();
            using var host = Host(home);
            var cc = host.Client();
            cc.ServerUrl = url;
            Configs.SaveClient(cc);
            var t = host.Timetable;
            t.Classes.Add(new TimetableClass("CS 101", []));
            host.SaveTimetable(t);
            var m = Setup.Make(host);

            m.NewClass = "CS 101";
            m.NewWhen = "Tue Thu 10:00-11:15";
            await m.AddClassCommand.ExecuteAsync(null);

            Assert.Equal(0, posts);
            Assert.Single(m.Classes, c => c.Name == "CS 101");
            Assert.Contains("Tue", m.Classes.Single(c => c.Name == "CS 101").When);
            Assert.Single(host.Timetable.Classes, c => c.Name == "CS 101");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Mic_check_hears_speech_within_two_seconds()
    {
        string wav = Path.Combine(AppContext.BaseDirectory, "Fixtures", "speech.wav");
        using var check = new MicCheck();
        check.Open(() => new FileMicrophone(wav, speed: 4));
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!check.Heard && DateTime.UtcNow < deadline) await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(check.Heard);
            Assert.Equal(MicCheck.Bars, check.Levels().Length);
        }
        finally
        {
            check.Close();
        }
    }
}
