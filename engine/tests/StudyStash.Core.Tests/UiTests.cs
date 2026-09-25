using System.Text.Json.Nodes;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>The pieces the library's pages are made of, and what this computer reports, against Python's answers.</summary>
public class UiTests
{
    static JsonArray L(string name) => (JsonArray)Golden.Library(name)!;

    [Fact]
    public void Page_pieces_match_python()
    {
        foreach (var c in L("esc")) Assert.Equal(c![1].S(), Ui.Esc(c[0].S()));
        foreach (var c in L("hue"))
        {
            Assert.Equal(c![1]?.GetValue<int>(), Ui.Hue(c[0].S()));
            Assert.Equal(c[2].S(), Ui.HueStyle(c[0].S()));
        }
        foreach (var c in L("class_url")) Assert.Equal(c![1].S(), Ui.ClassUrl(c[0].S()));
        foreach (var c in L("short_date")) Assert.Equal(c![1].S(), Ui.ShortDate(c[0]?.GetValue<string>()));
        foreach (var c in L("long_date")) Assert.Equal(c![1].S(), Ui.LongDate(c[0]?.GetValue<string>()));
        foreach (var c in L("snippet")) Assert.Equal(c![2].S(), Ui.Snippet(c[0].S(), c[1].S()));
    }

    [Fact]
    public void Markdown_keeps_math_and_drops_html_and_script_links()
    {
        string html = Ui.RenderMd("# Title\n\n- **bold** and $a < b$\n\n$$\\int_0^1 x\\,dx$$\n\n<div onclick=\"x()\">raw</div>\n\n"
            + "[ok](https://example.com) [bad](javascript:alert(1)) [data](data:text/html,x) ![img](vbscript:y)\n\n| a | b |\n|---|---|\n| 1 | 2 |");
        Assert.Contains("<h1", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("$a &lt; b$", html); // math is kept for KaTeX, escaped
        Assert.Contains("$$\\int_0^1 x\\,dx$$", html);
        Assert.Contains("&lt;div onclick=", html);
        Assert.DoesNotContain("<div", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("data:text", html);
        Assert.DoesNotContain("vbscript:", html);
        Assert.Contains("<table>", html);
        Assert.Equal("", Ui.RenderMd(null));
    }

    [Fact]
    public void Hosts_and_install_lines_match_python()
    {
        foreach (var c in L("tailscale_problem"))
        {
            var t = c![0]!.AsObject();
            var ts = new TailscaleInfo(t["installed"]?.GetValue<bool>() ?? false, t["running"]?.GetValue<bool>() ?? false, t["state"]?.S() ?? "");
            Assert.Equal(c[1].S(), HostInfo.TailscaleProblem(ts));
        }
        foreach (var c in L("invite"))
        {
            var (mac, windows) = HostInfo.InviteCommands(c![0].S(), c[1].S());
            Assert.Equal((c[2]!["mac"].S(), c[2]!["windows"].S()), (mac, windows));
        }
        foreach (var c in L("parse_version"))
            Assert.Equal(c![1]!.AsArray().Select(n => n!.GetValue<int>()), Updates.ParseVersion(c[0].S()));
    }

    [Fact]
    public void Tailscale_status_is_read_like_python_reads_it()
    {
        const string status = """
            {"BackendState": "Running", "Self": {"DNSName": "mini.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.9", "fd7a:115c::9"]}}
            """;
        var ts = HostInfo.Tailscale((_, _, _) => new ProcResult(0, status), ["/usr/bin/tailscale"]);
        Assert.Equal((true, true, "Running", "mini.tail1234.ts.net"), (ts.Installed, ts.Running, ts.State, ts.Dns));
        Assert.Equal(["100.64.0.9"], ts.Ips); // IPv4 only
        Assert.Equal(["http://mini.tail1234.ts.net:8787", "http://100.64.0.9:8787", "http://pc:8787"], HostInfo.ServerUrls(8787, ts, "pc"));
        var signedOut = HostInfo.Tailscale((_, _, _) => new ProcResult(1, """{"BackendState": "NeedsLogin", "Self": null}"""), ["/x"]);
        Assert.Equal("installed, but signed out", HostInfo.TailscaleProblem(signedOut));
        var silent = HostInfo.Tailscale((_, _, _) => null, ["/x"]);
        Assert.Equal((true, false, "installed, but not running"), (silent.Installed, silent.Running, HostInfo.TailscaleProblem(silent)));
        Assert.False(HostInfo.Tailscale(candidates: []).Installed);
        Assert.Equal(["http://pc:9"], HostInfo.ServerUrls(9, new TailscaleInfo(), "pc"));
    }

    [Fact]
    public void Sleep_and_the_firewall_are_read_from_the_commands_that_know()
    {
        const string pmset = "System-wide power settings:\nCurrently in use:\n standby              1\n sleep                10 (sleep prevented by x)\n displaysleep         10\n";
        Assert.Equal(10, Machine.MacSleepMinutes((_, _, _) => new ProcResult(0, pmset)));
        Assert.Null(Machine.MacSleepMinutes((_, _, _) => null));
        // powercfg's labels are in the PC's language; the last two values are plugged in and on battery, in seconds.
        const string powercfg = "Power Setting GUID: 29f6c1db\n  GUID Alias: STANDBYIDLE\n  Minimum Possible Setting: 0x00000000\n"
            + "  Maximum Possible Setting: 0xffffffff\n    Current AC Power Setting Index: 0x00000708\n    Current DC Power Setting Index: 0x00000384\n";
        Assert.Equal(30, Machine.WindowsSleepMinutes((_, _, _) => new ProcResult(0, powercfg)));
        Assert.Equal(true, Machine.FirewallOpen(8787, (_, _, _) => new ProcResult(0, "8787,8791\r\n")));
        Assert.Equal(false, Machine.FirewallOpen(8787, (_, _, _) => new ProcResult(0, "none")));
        Assert.Equal(true, Machine.FirewallOpen(8787, (_, _, _) => new ProcResult(0, "off")));
        Assert.Null(Machine.FirewallOpen(8787, (_, _, _) => new ProcResult(1, "")));
    }

    [Fact]
    public async Task A_port_is_free_ours_or_busy()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.Equal("busy", await HostInfo.PortStatusAsync(port)); // something that isn't a library
        }
        finally
        {
            listener.Stop();
        }
        Assert.Equal("free", await HostInfo.PortStatusAsync(port));
    }

    [Fact]
    public void Releases_compare_like_python_tuples()
    {
        var v9 = new Release("v9.9.9", [9, 9, 9], "", "");
        Assert.True(Updates.IsNewer(v9, "0.4.4"));
        Assert.False(Updates.IsNewer(v9, "9.9.9"));
        Assert.False(Updates.IsNewer(null));
        Assert.True(Updates.IsNewer(new Release("v0.4.4.1", [0, 4, 4, 1], "", ""), "0.4.4"));
        Assert.False(Updates.IsNewer(new Release("v0.4", [0, 4], "", ""), "0.4.0"));
    }

    [Fact]
    public async Task The_newest_release_is_read_from_github()
    {
        var github = new FakeOllama((path, _) => path == "/repos/Joseph-Rus/study-stash/releases/latest" ? JsonNode.Parse("""
            {"tag_name": "v0.5.0", "html_url": "https://github.com/Joseph-Rus/study-stash/releases/tag/v0.5.0",
             "assets": [{"name": "Study-Stash-mac.zip", "browser_download_url": "https://dl/mac.zip"},
                        {"name": "Study-Stash-helper-windows.zip", "browser_download_url": "https://dl/win.zip"}]}
            """)! : (System.Net.HttpStatusCode.NotFound, "{}"));
        var rel = await Updates.LatestAsync(github.Client());
        Assert.Equal(("v0.5.0", "https://dl/mac.zip", "https://dl/win.zip", ""), (rel!.Tag, rel.MacApp, rel.WindowsHelper, rel.WindowsApp));
        Assert.Equal([0, 5, 0], rel.Version);
        Assert.Equal("https://github.com/Joseph-Rus/study-stash/archive/refs/tags/v0.5.0.tar.gz", rel.Url);
        var none = new FakeOllama((_, _) => (System.Net.HttpStatusCode.NotFound, "{}"));
        Assert.Null(await Updates.LatestAsync(none.Client()));
    }
}
