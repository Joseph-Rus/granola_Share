using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Xml;
using StudyStash.App.Platform;
using StudyStash.Core;

namespace StudyStash.App.Tests;

public class DesktopTests
{
    [Fact]
    public void The_login_item_starts_the_app_quietly_with_its_folder()
    {
        string plist = Desktop.LaunchAgentPlist("com.study-stash.app.0a1b2c3d",
            Desktop.LoginArgs("/Applications/Study Stash.app/Contents/MacOS/StudyStash", "/Users/sam/Study & Stash"));

        var doc = new XmlDocument { XmlResolver = null };
        using (var r = XmlReader.Create(new StringReader(plist), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore }))
            doc.Load(r);
        var strings = doc.SelectNodes("/plist/dict/array/string")!.Cast<XmlNode>().Select(n => n.InnerText).ToList();
        Assert.Equal(["/Applications/Study Stash.app/Contents/MacOS/StudyStash", "--background", "--home", "/Users/sam/Study & Stash"], strings);
        Assert.Equal("com.study-stash.app.0a1b2c3d", doc.SelectSingleNode("/plist/dict/key[.='Label']/following-sibling::string[1]")!.InnerText);
        Assert.Contains("<string>/Users/sam/Study &amp; Stash</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
    }

    [Fact]
    public void The_usual_folder_starts_without_naming_it()
    {
        Assert.Equal(["StudyStash", "--background"], Desktop.LoginArgs("StudyStash", Configs.DefaultHome));
        Assert.Equal("\"C:\\Apps\\StudyStash.exe\" --background", Desktop.RunCommand(@"C:\Apps\StudyStash.exe", Configs.DefaultHome));
        using var home = new TempHome();
        Assert.Equal($"\"StudyStash.exe\" --background --home \"{home.Path}\"", Desktop.RunCommand("StudyStash.exe", home.Path));
    }

    [Fact]
    public void Each_folder_has_a_login_item_of_its_own()
    {
        Assert.Equal(("com.study-stash.app", "Study Stash"), Desktop.LoginName(Configs.DefaultHome));
        Assert.Equal(("com.study-stash.app", "Study Stash"), Desktop.LoginName(Configs.DefaultHome + Path.DirectorySeparatorChar));

        using var a = new TempHome();
        using var b = new TempHome();
        var (label, value) = Desktop.LoginName(a.Path);
        Assert.Matches("^com\\.study-stash\\.app\\.[0-9a-f]{8}$", label);
        Assert.Equal($"Study Stash ({label[^8..]})", value);
        Assert.Equal((label, value), Desktop.LoginName(a.Path));
        Assert.NotEqual(label, Desktop.LoginName(b.Path).Label);
    }

    [Fact]
    public void In_tests_nothing_starts_at_login()
    {
        // Checked first: were it off, the next line would write a real login item.
        Assert.True(Desktop.SystemChangesOff);
        using var home = new TempHome();
        var e = Assert.Throws<InvalidOperationException>(() => Desktop.StartAtLogin(true, home.Path));
        Assert.Equal("Start at login is off here.", e.Message);
        Assert.Throws<InvalidOperationException>(() => Desktop.StartAtLogin(false, Configs.DefaultHome));
        Assert.False(Desktop.StartsAtLogin(Configs.DefaultHome));
        Assert.False(LoginItems.System.StartsAtLogin(home.Path));
    }

    [Fact]
    public void One_copy_holds_a_folder_at_a_time()
    {
        using var home = new TempHome();
        try
        {
            Assert.True(Desktop.Claim(home.Path));
            Assert.True(File.Exists(home["app.lock"]));
            Assert.False(Desktop.Claim(home.Path));
            Desktop.Release(home.Path);
            Assert.True(Desktop.Claim(home.Path));
        }
        finally
        {
            Desktop.Release(home.Path);
        }
    }

    [Fact]
    public async Task A_second_copy_hands_off_to_the_first()
    {
        using var home = new TempHome();
        using var stop = new CancellationTokenSource();
        var heard = new BlockingCollection<string>();
        Desktop.Listen(home.Path, heard.Add, stop.Token, post: a => a());
        try
        {
            Assert.True(await Task.Run(() => Desktop.HandOff(home.Path, "record")));
            Assert.True(heard.TryTake(out var word, TimeSpan.FromSeconds(5)));
            Assert.Equal("record", word);

            // Words it doesn't know are ignored.
            Assert.True(await Task.Run(() => Desktop.HandOff(home.Path, "delete everything")));
            Assert.True(await Task.Run(() => Desktop.HandOff(home.Path, "show")));
            Assert.True(heard.TryTake(out word, TimeSpan.FromSeconds(5)));
            Assert.Equal("show", word);
        }
        finally
        {
            stop.Cancel();
        }
    }

    [Fact]
    public async Task A_copy_that_says_nothing_doesnt_block_the_next()
    {
        using var home = new TempHome();
        using var stop = new CancellationTokenSource();
        var heard = new BlockingCollection<string>();
        Desktop.Listen(home.Path, heard.Add, stop.Token, post: a => a());
        try
        {
            // Wait until it listens, then connect and say nothing.
            Assert.True(await Task.Run(() => Desktop.HandOff(home.Path, "show")));
            Assert.True(heard.TryTake(out _, TimeSpan.FromSeconds(5)));
            using var silent = new NamedPipeClientStream(".", Desktop.PipeName(home.Path), PipeDirection.Out);
            await silent.ConnectAsync(5000, TestContext.Current.CancellationToken);

            Assert.True(await Task.Run(() => Desktop.HandOff(home.Path, "record")));
            Assert.True(heard.TryTake(out var word, TimeSpan.FromSeconds(6)));
            Assert.Equal("record", word);
        }
        finally
        {
            stop.Cancel();
        }
    }

    [Fact]
    public void Nobody_listening_means_no_hand_off()
    {
        using var home = new TempHome();
        var took = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(Desktop.HandOff(home.Path, "show", TimeSpan.FromMilliseconds(600)));
        Assert.InRange(took.Elapsed.TotalSeconds, 0.5, 4);
    }
}
