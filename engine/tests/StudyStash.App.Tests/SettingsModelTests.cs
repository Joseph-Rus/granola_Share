using Avalonia.Headless.XUnit;
using StudyStash.App.Platform;
using StudyStash.App.Services;
using StudyStash.Core;

namespace StudyStash.App.Tests;

/// <summary>Login items as a test sees them: what they say, and every call to change them.</summary>
public sealed class CountingLoginItems(bool starts = false, bool refuse = false) : ILoginItems
{
    public List<bool> Calls { get; } = [];

    public bool StartsAtLogin(string home) => starts;

    public void StartAtLogin(bool on, string home)
    {
        Calls.Add(on);
        if (refuse) throw new InvalidOperationException("Start at login is off here.");
    }
}

public class SettingsModelTests
{
    /// <summary>A folder set up with something other than the defaults everywhere Settings shows.</summary>
    static TempHome SetUpHome()
    {
        var home = new TempHome();
        new AppSettings { SetupDone = true, Language = "fr", ComputerAudio = true, KeepAudioDays = 7, Shortcuts = false }.Save(home.Path);
        var cc = Configs.LoadClient(home.Path);
        cc.DisplayName = "Sam";
        Configs.SaveClient(cc);
        return home;
    }

    [AvaloniaFact]
    public void Opening_settings_changes_nothing()
    {
        using var home = SetUpHome();
        byte[] app = File.ReadAllBytes(home["app.json"]), client = File.ReadAllBytes(home["client.toml"]);
        var login = new CountingLoginItems(starts: true);
        using var host = new AppHost(home.Path, log: _ => { }, loginItems: login);

        using var model = SettingsModel.Make(host);

        Assert.True(model.StartAtLogin);
        Assert.Equal("fr", model.Language);
        Assert.False(model.Shortcuts);
        Assert.Equal("Sam", model.DisplayName);
        Assert.Empty(login.Calls);
        Assert.Equal(app, File.ReadAllBytes(home["app.json"]));
        Assert.Equal(client, File.ReadAllBytes(home["client.toml"]));
    }

    [AvaloniaFact]
    public void What_the_student_changes_is_saved()
    {
        using var home = SetUpHome();
        var login = new CountingLoginItems();
        using var host = new AppHost(home.Path, log: _ => { }, loginItems: login);
        using var model = SettingsModel.Make(host);

        model.StartAtLogin = true;
        model.Language = "de";
        model.Shortcuts = true;

        Assert.Equal([true], login.Calls);
        var saved = AppSettings.Load(home.Path);
        Assert.Equal("de", saved.Language);
        Assert.True(saved.Shortcuts);
    }

    [AvaloniaFact]
    public void Start_at_login_that_cant_be_turned_on_stays_off()
    {
        using var home = SetUpHome();
        var login = new CountingLoginItems(refuse: true);
        using var host = new AppHost(home.Path, log: _ => { }, loginItems: login);
        using var model = SettingsModel.Make(host);

        model.StartAtLogin = true;

        Assert.Equal([true], login.Calls);
        Assert.False(model.StartAtLogin);
    }
}
