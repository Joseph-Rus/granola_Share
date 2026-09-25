// Study Stash for Windows: a native window around the pages granola-share already serves, like the Mac
// app (macos/StudyStash.swift).
//
// "This PC" is the laptop's page (setup and status) from the background service on 127.0.0.1.
// "Library" is your library, signed in with the password this PC already has. If the background
// helper isn't installed yet (someone ran Study-Stash-Laptop-Setup.exe), the app installs it.
//
// The same app is the library's own window on the PC that keeps it (Study-Stash-Library-Setup.exe
// writes role=library into study-stash.ini next to it; `--library` does the same). There it shows only
// the library, and before there is one, it runs the library's setup in a window for its questions.
//
// Built on .NET Framework 4.8 and WebView2, both part of Windows 10 and 11. Build: windows/build.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace StudyStash
{
    // Where things are ------------------------------------------------------------------------------

    static class Where
    {
        public static readonly string UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string DataDir = Env("GRANOLA_SHARE_HOME") != null
            ? Environment.ExpandEnvironmentVariables(Env("GRANOLA_SHARE_HOME"))
            : Path.Combine(UserHome, ".granola-share");
        // granola-share: install.ps1's folder with its own Python, or (0.4.1 and before) uv's granola-share.exe.
        static readonly string Bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "granola-share", "python", "python.exe");
        public static string Engine => Env("GRANOLA_SHARE_ENGINE")
            ?? (File.Exists(Bundled) ? Bundled : Path.Combine(UserHome, ".local", "bin", "granola-share.exe"));
        public static bool HasEngine => File.Exists(Engine);

        /// <summary>The command line for granola-share with these arguments.</summary>
        public static string EngineArgs(params string[] args) =>
            (Engine == Bundled ? "-m granola_share.cli " : "") + string.Join(" ", args.Select(Quote));
        public static readonly string Own = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Study Stash");
        public const string InstallScript = "https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.ps1";
        /// <summary>This copy is the library's own app (Study-Stash-Library-Setup.exe), not the laptop's.</summary>
        public static bool LibraryMode;

        public static bool ReadLibraryMode(string[] args)
        {
            if (args.Contains("--library")) return true;
            try
            {
                var ini = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "study-stash.ini");
                return File.ReadAllLines(ini).Any(l => l.Replace(" ", "").ToLowerInvariant() == "role=library");
            }
            catch (Exception) { return false; }
        }

        public static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        public static string DataFile(string name)
        {
            try { return File.ReadAllText(Path.Combine(DataDir, name), Encoding.UTF8).Trim(); }
            catch (Exception) { return null; }
        }

        public static int PagePort() => int.TryParse(DataFile("ui_port"), out var p) ? p : 8765;
        public static string PageUrl() => $"http://127.0.0.1:{PagePort()}/?t={DataFile("ui_token") ?? ""}";

        /// <summary>One `key = value` from our TOML files. config.py writes strings JSON-style.</summary>
        public static string TomlValue(string text, string key)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0 || line.Substring(0, eq).Trim() != key) continue;
                var value = line.Substring(eq + 1).Trim();
                if (!value.StartsWith("\"")) return value.Split('#')[0].Trim();
                return JsonString(value);
            }
            return null;
        }

        static string JsonString(string s)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= s.Length) break;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                        break;
                    default: sb.Append(s[i]); break;
                }
            }
            return null;
        }

        /// <summary>A JavaScript string literal.</summary>
        public static string Js(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20 || c == '\u2028' || c == '\u2029' || c == '<') sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public static string Html(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var body = arg.Replace("\"", "\\\"");
            if (body.EndsWith("\\")) body += "\\";
            return "\"" + body + "\"";
        }
    }

    /// <summary>What this PC is: the laptop (sends lectures), the library's computer, or both.</summary>
    sealed class Place
    {
        public bool Sends = true;
        public Uri Library;
        public string Key;
        public string LibraryName;

        public static Place Read()
        {
            var client = Where.DataFile("client.toml");
            var server = Where.DataFile("config.toml");
            var p = new Place { Sends = !Where.LibraryMode && (server == null || client != null) };
            var url = client == null || Where.LibraryMode ? null : Where.TomlValue(client, "server_url");
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                p.Library = u;
                var key = Where.TomlValue(client, "pool_key");
                p.Key = string.IsNullOrEmpty(key) ? null : key;
                p.LibraryName = Where.TomlValue(client, "pool_name");
            }
            else if (server != null)
            {
                int port = int.TryParse(Where.TomlValue(server, "web_port"), out var n) ? n : 8787;
                p.Library = new Uri($"http://127.0.0.1:{port}/");  // on the library's computer: no password needed
                p.LibraryName = Where.TomlValue(server, "pool_name");
            }
            return p;
        }
    }

    // Colors that follow Windows' light or dark mode -------------------------------------------------

    sealed class Theme
    {
        public bool Dark;
        public Color Bar, Track, Thumb, Text, Text2, Hover, Line, Canvas;

        public static Theme Current()
        {
            bool dark = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    dark = k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception) { }
            return dark
                ? new Theme { Dark = true, Bar = Rgb(0x202020), Track = Rgb(0x2c2c2c), Thumb = Rgb(0x464646), Text = Rgb(0xffffff),
                              Text2 = Rgb(0xc8c8c8), Hover = Rgb(0x2d2d2d), Line = Rgb(0x111111), Canvas = Rgb(0x1c1c1e) }
                : new Theme { Bar = Rgb(0xf3f3f3), Track = Rgb(0xe3e3e3), Thumb = Rgb(0xffffff), Text = Rgb(0x1a1a1a),
                              Text2 = Rgb(0x5f5f5f), Hover = Rgb(0xe8e8e8), Line = Rgb(0xe0e0e0), Canvas = Rgb(0xf5f5f7) };
        }

        static Color Rgb(int hex) => Color.FromArgb((hex >> 16) & 0xff, (hex >> 8) & 0xff, hex & 0xff);
    }

    /// <summary>The This PC / Library switch, drawn like Windows 11's segmented controls.</summary>
    sealed class Segmented : Control
    {
        public readonly string[] Labels;
        public Theme Theme = Theme.Current();
        public event Action<int> Changed;
        int selected;

        public Segmented(params string[] labels)
        {
            Labels = labels;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTabList;
            AccessibleName = "Show";
        }

        public int Selected
        {
            get => selected;
            set { if (value != selected) { selected = value; Invalidate(); } }
        }

        void Pick(int i)
        {
            if (i < 0 || i >= Labels.Length || i == selected) return;
            Selected = i;
            Changed?.Invoke(i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = DeviceDpi / 96f;
            using (var track = new SolidBrush(Theme.Track)) Rounded(g, track, new RectangleF(0, 0, Width - 1, Height - 1), 7 * s);
            float w = (Width - 4 * s) / Labels.Length;
            for (int i = 0; i < Labels.Length; i++)
            {
                var seg = new RectangleF(2 * s + i * w, 2 * s, w, Height - 4 * s - 1);
                if (i == selected)
                    using (var thumb = new SolidBrush(Theme.Thumb)) Rounded(g, thumb, seg, 5 * s);
                TextRenderer.DrawText(g, Labels[i], Font, Rectangle.Round(seg), i == selected ? Theme.Text : Theme.Text2,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(1, 1, Width - 3, Height - 3));
        }

        static void Rounded(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = new GraphicsPath())
            {
                float d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            Pick((int)(e.X / (Width / (float)Labels.Length)));
        }

        protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || base.IsInputKey(k);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Pick(selected - 1);
            else if (e.KeyCode == Keys.Right) Pick(selected + 1);
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    // The app's own screens (before the pages exist), in the same look as the pages. WebView2 would
    // answer F5 by reloading the screen itself, so on these screens F5 asks the app to try again. ----

    static class Pages
    {
        static string icon;

        static string IconUri()
        {
            if (icon != null) return icon;
            try
            {
                using (var s = typeof(Pages).Assembly.GetManifestResourceStream("icon.png"))
                using (var m = new MemoryStream())
                {
                    s.CopyTo(m);
                    icon = "data:image/png;base64," + Convert.ToBase64String(m.ToArray());
                }
            }
            catch (Exception) { icon = ""; }
            return icon;
        }

        public static string Page(string title, string text, bool spinner = false, bool log = false, string detail = "",
                                  params (string label, string action, bool primary)[] buttons)
        {
            var btns = string.Concat(buttons.Select(b =>
                $"<button class=\"{(b.primary ? "primary" : "")}\" onclick=\"studyStash('{b.action}')\">{b.label}</button>"));
            var top = spinner ? "<div class=\"spin\"></div>" : $"<img src=\"{IconUri()}\" alt=\"\">";
            var pre = log || detail.Length > 0 ? $"<pre id=\"log\">{Where.Html(detail)}</pre>" : "";
            return @"<!doctype html><html><head><meta charset=""utf-8""><meta name=""color-scheme"" content=""light dark""><style>
:root{--canvas:#f5f5f7;--group:#fff;--label:#1d1d1f;--label-2:rgba(60,60,67,.64);--fill:rgba(118,118,128,.12);--accent:#0067c0}
@media (prefers-color-scheme:dark){:root{--canvas:#1c1c1e;--group:#2c2c2e;--label:#f5f5f7;--label-2:rgba(235,235,245,.62);
  --fill:rgba(118,118,128,.26);--accent:#4cc2ff}}
html,body{height:100%}
body{margin:0;display:grid;place-items:center;background:var(--canvas);color:var(--label);
  font:400 15px/1.45 ""Segoe UI Variable Text"",""Segoe UI"",system-ui,sans-serif;user-select:none;cursor:default}
main{width:min(30rem,100% - 3rem);text-align:center}
img{width:96px;height:96px;margin-bottom:.8rem}
h1{font:600 1.6rem/1.2 ""Segoe UI Variable Display"",""Segoe UI"",system-ui,sans-serif;margin:0 0 .5rem}
p{color:var(--label-2);margin:0 auto 1.4rem;max-width:26rem}
button{font:500 .9375rem/1 ""Segoe UI"",system-ui,sans-serif;padding:.62rem 1.2rem;border-radius:6px;border:0;margin:0 .25rem;
  background:var(--fill);color:var(--label);cursor:pointer}
button.primary{background:var(--accent);color:#fff}
@media (prefers-color-scheme:dark){button.primary{color:#000}}
button:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
.spin{width:26px;height:26px;margin:0 auto 1rem;border-radius:50%;border:3px solid var(--fill);border-top-color:var(--label-2);
  animation:s .9s linear infinite}@keyframes s{to{transform:rotate(360deg)}}
pre{text-align:left;font:12px/1.5 Consolas,ui-monospace,monospace;background:var(--group);border-radius:8px;padding:.7rem .9rem;
  max-height:11rem;overflow:auto;white-space:pre-wrap;color:var(--label-2);user-select:text}
pre:empty{display:none}
</style></head><body><main>" + top + $"<h1>{title}</h1><p>{text}</p><div>{btns}</div>{pre}" +
@"</main><script>function addLog(t){var l=document.getElementById('log');if(!l)return;
l.textContent+=(l.textContent?'\n':'')+t;l.scrollTop=l.scrollHeight;}
document.addEventListener('keydown',function(e){if(e.key==='F5'||(e.ctrlKey&&(e.key==='r'||e.key==='R'))){
  e.preventDefault();studyStash('retry');}});</script></body></html>";
        }

        public static string LibraryWelcome() => Page("Set up your library",
            "This PC will keep your lectures: it writes their study notes with a model that runs here, sorts them by "
            + "class, and serves them to your laptop and phone. Setup opens a window with a few questions, and can install "
            + "Tailscale and Ollama for you. It takes about five minutes, longer if it downloads a model.",
            buttons: ("Set Up", "setup-library", true));

        public static string Welcome() => Page("Welcome to Study Stash",
            "It sends the lectures you record in Granola to your library, where your own model writes their study notes. "
            + "First it installs a small helper that runs in the background. That takes about a minute.",
            buttons: ("Install and Continue", "install", true));
    }

    // The window -------------------------------------------------------------------------------------

    sealed class MainForm : Form
    {
        readonly Panel bar = new Panel();
        readonly Panel host = new Panel();
        readonly Segmented tabs = new Segmented("This PC", "Library");
        readonly Button back = new Button(), reload = new Button();
        readonly ToolTip tips = new ToolTip();
        readonly WebView2 laptop = new WebView2(), library = new WebView2();
        WebView2 current;
        Theme theme = Theme.Current();
        Place place = Place.Read();
        Uri libraryShown;
        bool signingIn, ready;
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        // CI's check that the app really starts: GRANOLA_SHARE_SELFTEST=<file> writes the first page's title there and quits.
        readonly string selfTest = Where.Env("GRANOLA_SHARE_SELFTEST");

        public MainForm()
        {
            Text = "Study Stash";
            try
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("study-stash.ico")) Icon = new Icon(s);
            }
            catch (Exception) { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;
            MinimumSize = new Size(Scale(520), Scale(460));
            LoadBounds();

            foreach (var (button, glyph, tip) in new[] { (back, "\uE72B", "Back (Alt+Left)"), (reload, "\uE72C", "Reload (F5)") })
            {
                button.Text = glyph;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.TabStop = true;
                button.AccessibleName = tip.Split(' ')[0];
                tips.SetToolTip(button, tip);
                bar.Controls.Add(button);
            }
            back.Click += (s, e) => { if (current?.CoreWebView2?.CanGoBack == true) current.CoreWebView2.GoBack(); };
            reload.Click += (s, e) => Reload();
            tabs.Changed += i => { if (i == 1) ShowLibrary(); else ShowLaptop(); };
            bar.Controls.Add(tabs);
            bar.Dock = DockStyle.Top;
            bar.Paint += (s, e) => { using (var p = new Pen(theme.Line)) e.Graphics.DrawLine(p, 0, bar.Height - 1, bar.Width, bar.Height - 1); };
            bar.Resize += (s, e) => LayoutBar();
            host.Dock = DockStyle.Fill;
            foreach (var v in new[] { laptop, library })
            {
                v.Dock = DockStyle.Fill;  // both visible while WebView2 starts; then Show() keeps one
                host.Controls.Add(v);
            }
            Controls.Add(host);
            Controls.Add(bar);
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnPreferences;
            FormClosed += (s, e) => { SystemEvents.UserPreferenceChanged -= OnPreferences; SaveBounds(); };
            Load += async (s, e) => await StartAsync();
        }

        int Scale(int px) => (int)Math.Round(px * DeviceDpi / 96f);

        // Windows 11's icon font, or Windows 10's
        static readonly Font Glyphs = new Font(FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons")
            ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 10f);

        void LayoutBar()
        {
            bar.Height = Scale(48);
            int b = Scale(34), pad = Scale(8), mid = (bar.Height - b) / 2;
            back.SetBounds(pad, mid, b, b);
            reload.SetBounds(bar.Width - pad - b, mid, b, b);
            int w = Scale(220), h = Scale(32);
            tabs.SetBounds((bar.Width - w) / 2, (bar.Height - h) / 2, w, h);
            back.Font = reload.Font = Glyphs;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); LayoutBar(); }

        void OnPreferences(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
                BeginInvoke((Action)ApplyTheme);
        }

        void ApplyTheme()
        {
            theme = Theme.Current();
            BackColor = host.BackColor = theme.Canvas;
            bar.BackColor = theme.Bar;
            tabs.Theme = theme;
            tabs.Font = Font;
            tabs.BackColor = theme.Bar;
            foreach (var button in new[] { back, reload })
            {
                button.ForeColor = theme.Text;
                button.BackColor = theme.Bar;
                button.FlatAppearance.MouseOverBackColor = theme.Hover;
                button.FlatAppearance.MouseDownBackColor = theme.Track;
            }
            laptop.DefaultBackgroundColor = library.DefaultBackgroundColor = theme.Canvas;  // no white flash in dark mode
            DarkTitleBar(theme.Dark);
            bar.Invalidate();
            tabs.Invalidate();
        }

        // -- launch

        async Task StartAsync()
        {
            LayoutBar();
            try
            {
                Directory.CreateDirectory(Where.Own);
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(Where.Own, Where.LibraryMode ? "WebView2-library" : "WebView2"));
                foreach (var v in new[] { laptop, library }) await Prepare(v, env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (selfTest != null) { File.WriteAllText(selfTest, "no WebView2 runtime"); Close(); return; }
                var answer = MessageBox.Show(this, "Study Stash needs Microsoft Edge WebView2, which comes with Windows 11 and most "
                    + "Windows 10 computers. Download it from Microsoft now?", "Study Stash", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) OpenInBrowser("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                Close();
                return;
            }
            ready = true;
            CleanUpAfterUpdate();
            place = Place.Read();
            tabs.Visible = place.Sends;
            if (place.Sends) { Show(laptop); await OpenLaptop(); }
            else { Show(library); OpenLibrary(); }
        }

        async Task Prepare(WebView2 v, CoreWebView2Environment env)
        {
            await v.EnsureCoreWebView2Async(env);
            var core = v.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            // Copy buttons use navigator.clipboard, which plain-http pages (the library) don't get: copy natively.
            // Ctrl+1 and Ctrl+2 switch tabs, as ⌘1 and ⌘2 do on a Mac.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"(function(){
  var post=function(m){try{window.chrome.webview.postMessage(m);}catch(e){}};
  try{Object.defineProperty(navigator,'clipboard',{value:{writeText:function(t){post('copy:'+String(t));return Promise.resolve();}},configurable:true});}catch(e){}
  window.studyStash=function(a){post('app:'+a);};
  document.addEventListener('keydown',function(e){if(e.ctrlKey&&!e.altKey&&!e.shiftKey&&(e.key==='1'||e.key==='2')){
    e.preventDefault();post('app:'+(e.key==='1'?'laptop':'library'));}},true);
})();");
            core.WebMessageReceived += (s, e) =>
            {
                string m;
                try { m = e.TryGetWebMessageAsString(); } catch (ArgumentException) { return; }
                OnMessage(v, m);
            };
            core.NewWindowRequested += (s, e) =>  // target=_blank: "Open your library" lands in its tab
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) Route(u);
            };
            core.NavigationStarting += (s, e) => OnNavigationStarting(e);
            core.NavigationCompleted += (s, e) => OnNavigationCompleted(v, e);
            core.DocumentTitleChanged += (s, e) => { if (v == current) UpdateTitle(); };
            core.HistoryChanged += (s, e) => { if (v == current) back.Enabled = core.CanGoBack; };
        }

        void Show(WebView2 v)
        {
            current = v;
            laptop.Visible = v == laptop;
            library.Visible = v == library;
            tabs.Selected = v == library ? 1 : 0;
            back.Enabled = v.CoreWebView2?.CanGoBack == true;
            UpdateTitle();
            if (ready) v.Focus();
        }

        void UpdateTitle()
        {
            var t = current?.CoreWebView2?.DocumentTitle;
            Text = string.IsNullOrEmpty(t) || t == "Study Stash" || t.StartsWith("data:") || t.StartsWith("about:")
                ? "Study Stash" : $"{t} - Study Stash";
        }

        // -- This PC

        static async Task<bool> PageAnswers()
        {
            try
            {
                var r = await http.GetAsync($"http://127.0.0.1:{Where.PagePort()}/healthz");
                return r.IsSuccessStatusCode && (await r.Content.ReadAsStringAsync()).Contains("granola-share");
            }
            catch (Exception) { return false; }
        }

        async Task OpenLaptop()
        {
            if (await PageAnswers()) laptop.CoreWebView2.Navigate(Where.PageUrl());
            else if (Where.HasEngine) await StartHelper(false);
            else laptop.CoreWebView2.NavigateToString(Pages.Welcome());
        }

        async Task StartHelper(bool install)
        {
            laptop.CoreWebView2.NavigateToString(Pages.Page("Starting Study Stash", "This takes a few seconds.", spinner: true));
            var args = new List<string> { "--home", Where.DataDir, "client", "open", "--no-browser" };
            if (install) args.Add("--install");
            var (code, output) = await Run(Where.Engine, Where.EngineArgs(args.ToArray()), null);
            if (code == 0)
            {
                place = Place.Read();
                laptop.CoreWebView2.Navigate(Where.PageUrl());
            }
            else
            {
                Problem(laptop, "Study Stash didn't start", "Its background helper didn't answer. Try again, or look at its log.", output);
            }
        }

        async Task InstallHelper()
        {
            laptop.CoreWebView2.NavigateToString(Pages.Page("Installing",
                "Getting the background helper. This needs the internet and takes about a minute.", spinner: true, log: true));
            var command = $"$env:GRANOLA_SHARE_NO_SETUP='1'; irm {Where.InstallScript} | iex";
            var (code, output) = await Run("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                line => BeginInvoke((Action)(() => _ = laptop.CoreWebView2?.ExecuteScriptAsync($"addLog({Where.Js(line)})"))));
            if (code == 0 && Where.HasEngine) await StartHelper(true);
            else Problem(laptop, "The install didn't finish", "Check that this PC is online, then try again.", output, "install");
        }

        /// <summary>Runs a command without a console window; each output line goes to `line` as it comes.</summary>
        static Task<(int code, string output)> Run(string exe, string args, Action<string> line)
        {
            var done = new TaskCompletionSource<(int, string)>();
            var all = new StringBuilder();
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                },
                EnableRaisingEvents = true,
            };
            p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            DataReceivedEventHandler take = (s, e) =>
            {
                if (e.Data == null) return;
                lock (all) all.AppendLine(e.Data);
                line?.Invoke(e.Data);
            };
            p.OutputDataReceived += take;
            p.ErrorDataReceived += take;
            p.Exited += (s, e) =>
            {
                p.WaitForExit();  // lets the output readers finish
                string text;
                lock (all) text = all.ToString();
                done.TrySetResult((p.ExitCode, text));
                p.Dispose();
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                done.TrySetResult((-1, e.Message));
            }
            return done.Task;
        }

        // -- Library

        void OpenLibrary()
        {
            place = Place.Read();
            if (place.Library == null && Where.LibraryMode)
            {
                libraryShown = null;
                _ = SetupOrWelcome();  // reopened part way through setup: back to it
                return;
            }
            if (place.Library == null)
            {
                library.CoreWebView2.NavigateToString(Where.LibraryMode ? Pages.LibraryWelcome() : Pages.Page("No library yet",
                    "Connect this PC to your library on the This PC tab. Its lectures show up here.",
                    buttons: ("Go to This PC", "laptop", true)));
                libraryShown = null;
                return;
            }
            libraryShown = place.Library;
            library.CoreWebView2.Navigate(place.Library.ToString());
        }

        /// <summary>The library's setup, as a page in this window: installs the background helper first if it
        /// isn't here (hidden, showing its progress), then starts `granola-share setup --page` and shows it.
        /// No PowerShell or Command Prompt window.</summary>
        async Task SetUpLibrary()
        {
            if (await SetupPageAnswers()) return;
            if (!Where.HasEngine)
            {
                library.CoreWebView2.NavigateToString(Pages.Page("Installing",
                    "Getting the library's background helper. This needs the internet and takes a minute or two.",
                    spinner: true, log: true));
                var command = $"$env:GRANOLA_SHARE_ROLE='server'; $env:GRANOLA_SHARE_NO_SETUP='1'; irm {Where.InstallScript} | iex";
                var (code, output) = await Run("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    line => BeginInvoke((Action)(() => _ = library.CoreWebView2?.ExecuteScriptAsync($"addLog({Where.Js(line)})"))));
                if (code != 0 || !Where.HasEngine)
                {
                    Problem(library, "The install didn't finish", "Check that this PC is online, then try again.", output,
                            "setup-library");
                    return;
                }
            }
            library.CoreWebView2.NavigateToString(Pages.Page("Opening setup", "This takes a few seconds.", spinner: true));
            try { File.Delete(Path.Combine(Where.DataDir, "setup_port")); } catch (Exception) { }
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo(Where.Engine,
                    Where.EngineArgs("--home", Where.DataDir, "setup", "--page", "--no-browser"))
                    { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception e)
            {
                Problem(library, "Setup didn't start", Where.Html(e.Message), retry: "setup-library");
                return;
            }
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                if (await SetupPageAnswers()) return;
                if (p.HasExited) break;
            }
            Problem(library, "Setup didn't start", "Its page didn't open. Try again.", retry: "setup-library");
        }

        async Task SetupOrWelcome()
        {
            if (!await SetupPageAnswers()) library.CoreWebView2.NavigateToString(Pages.LibraryWelcome());
        }

        /// <summary>The setup page, if it's running (say the app was closed part way through): show it.</summary>
        async Task<bool> SetupPageAnswers()
        {
            var port = Where.DataFile("setup_port");
            if (!int.TryParse(port, out _)) return false;
            try
            {
                var r = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
                if (!r.IsSuccessStatusCode || !(await r.Content.ReadAsStringAsync()).Contains("setup")) return false;
            }
            catch (Exception) { return false; }
            library.CoreWebView2.Navigate($"http://127.0.0.1:{port}/?t={Where.DataFile("ui_token") ?? ""}");
            return true;
        }

        /// <summary>Start the library's background service (it also starts when you sign in to Windows).</summary>
        async Task StartLibrary()
        {
            library.CoreWebView2.NavigateToString(Pages.Page("Starting your library", "This takes a few seconds.", spinner: true));
            await Run(Where.Engine, Where.EngineArgs("--home", Where.DataDir, "autostart", "install", "--role", "server"), null);
            await WaitForLibrary();
            OpenLibrary();
        }

        async Task WaitForLibrary()
        {
            for (int i = 0; i < 30 && place.Library != null; i++)
            {
                try { if ((await http.GetAsync(place.Library)).IsSuccessStatusCode) return; } catch (Exception) { }
                await Task.Delay(1000);
            }
        }

        /// <summary>The library asks for its password once per browser; this PC already has it, so fill it in.</summary>
        void SignInIfAsked(WebView2 v)
        {
            if (v != library || place.Library == null || !Uri.TryCreate(v.Source?.ToString(), UriKind.Absolute, out var u)) return;
            if (u.Host != place.Library.Host) return;
            if (u.AbsolutePath == "/login")
            {
                if (signingIn || u.Query.Contains("bad=1") || place.Key == null) return;
                signingIn = true;
                _ = v.CoreWebView2.ExecuteScriptAsync("(function(){var f=document.querySelector('form[action=\"/login\"]');"
                    + $"if(f){{f.elements.password.value={Where.Js(place.Key)};f.submit();}}}})();");
            }
            else
            {
                signingIn = false;
            }
        }

        // -- switching

        async void ShowLaptop()
        {
            if (!place.Sends || !ready) return;
            Show(laptop);
            if (laptop.Source == null || laptop.Source.ToString() == "about:blank") await OpenLaptop();
        }

        void ShowLibrary()
        {
            if (!ready) return;
            Show(library);
            var now = Place.Read();
            if (libraryShown == null || now.Library != libraryShown || library.Source == null || library.Source.ToString() == "about:blank")
            {
                place = now;
                OpenLibrary();
            }
        }

        bool IsLocal(Uri u) => u.Host == "127.0.0.1" || u.Host == "localhost";
        bool IsLibrary(Uri u) => place.Library != null && u.Host == place.Library.Host && u.Port == place.Library.Port;
        bool Ours(Uri u) => IsLocal(u) || (place.Library != null && u.Host == place.Library.Host);

        /// <summary>Where a link goes: the library's tab, This PC's tab, or the browser for anything else.</summary>
        void Route(Uri u)
        {
            if (IsLibrary(u))
            {
                Show(library);
                libraryShown = place.Library;
                library.CoreWebView2.Navigate(u.ToString());
            }
            else if (IsLocal(u) && Where.LibraryMode)
            {
                Show(library);
                library.CoreWebView2.Navigate(u.ToString());
            }
            else if (IsLocal(u) && u.AbsolutePath == "/library")
            {
                ShowLibrary();  // the page's "Open your library": the Library tab signs in by itself
            }
            else if (IsLocal(u))
            {
                ShowLaptop();
                laptop.CoreWebView2.Navigate(u.ToString());
            }
            else
            {
                OpenInBrowser(u.ToString());
            }
        }

        void Reload()
        {
            if (current?.CoreWebView2 == null) return;
            var src = current.Source?.ToString() ?? "";
            if (src == "" || src.StartsWith("about:") || src.StartsWith("data:"))
            {
                if (current == library) OpenLibrary(); else _ = OpenLaptop();
            }
            else
            {
                current.CoreWebView2.Reload();
            }
        }

        void Problem(WebView2 v, string title, string text, string detail = "", string retry = "retry")
        {
            var buttons = new List<(string, string, bool)> { ("Try Again", retry, true) };
            if (v == laptop) buttons.Add(("Show Log", "log", false));
            if (v == library && place.Library?.Host == "127.0.0.1" && Where.HasEngine) buttons.Add(("Start It", "start-library", false));
            var tail = detail.Length > 1500 ? detail.Substring(detail.Length - 1500) : detail;
            v.CoreWebView2.NavigateToString(Pages.Page(title, text, detail: tail, buttons: buttons.ToArray()));
        }

        // -- web view events

        void OnNavigationStarting(CoreWebView2NavigationStartingEventArgs e)
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;
            if (u.Scheme == "about" || u.Scheme == "data" || u.Scheme == "blob" || Ours(u)) return;
            e.Cancel = true;
            OpenInBrowser(e.Uri);  // release notes, Tailscale, Granola's download page: the default browser
        }

        void OnNavigationCompleted(WebView2 v, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (v == current) UpdateTitle();
            if (selfTest != null && v == current)
            {
                File.WriteAllText(selfTest, "ok: " + v.CoreWebView2.DocumentTitle);
                Close();
                return;
            }
            if (!e.IsSuccess)
            {
                var s = e.WebErrorStatus;
                bool unreachable = s == CoreWebView2WebErrorStatus.CannotConnect || s == CoreWebView2WebErrorStatus.ServerUnreachable
                    || s == CoreWebView2WebErrorStatus.HostNameNotResolved || s == CoreWebView2WebErrorStatus.Timeout
                    || s == CoreWebView2WebErrorStatus.Disconnected || s == CoreWebView2WebErrorStatus.ConnectionReset;
                if (!unreachable) return;  // a download, or a navigation we cancelled
                if (v == laptop)
                {
                    Problem(v, "Study Stash isn't answering", "Its background helper may be restarting. Try again in a moment.");
                }
                else
                {
                    var name = place.Library?.Host ?? "your library";
                    Problem(v, "Can't reach your library", place.Library?.Host == "127.0.0.1"
                        ? "The library on this PC isn't running. It starts when you sign in to Windows; try again in a moment."
                        : $"Nothing answered at {Where.Html(name)}. Is its computer awake, with Tailscale on?", s.ToString());
                }
                return;
            }
            SignInIfAsked(v);
        }

        async void OnMessage(WebView2 v, string message)
        {
            if (message == null) return;
            if (message.StartsWith("copy:"))
            {
                var text = message.Substring(5);
                if (text.Length > 0) try { Clipboard.SetText(text); } catch (ExternalException) { }
                return;
            }
            if (!message.StartsWith("app:")) return;
            switch (message.Substring(4))
            {
                case "install": await InstallHelper(); break;
                case "setup-library": await SetUpLibrary(); break;
                case "start-library": await StartLibrary(); break;
                case "laptop": ShowLaptop(); break;
                case "library": ShowLibrary(); break;
                case "log": Directory.CreateDirectory(Path.Combine(Where.DataDir, "logs")); OpenInBrowser(Path.Combine(Where.DataDir, "logs")); break;
                default: if (v == library) OpenLibrary(); else await OpenLaptop(); break;
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            switch (keys)
            {
                case Keys.Control | Keys.D1: ShowLaptop(); return true;
                case Keys.Control | Keys.D2: ShowLibrary(); return true;
                case Keys.F5: case Keys.Control | Keys.R: Reload(); return true;
            }
            return base.ProcessCmdKey(ref msg, keys);
        }

        static void OpenInBrowser(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { }
        }

        /// <summary>An update replaces this app's files while it runs by renaming the old ones to .old first.</summary>
        static void CleanUpAfterUpdate()
        {
            try
            {
                var dir = Path.GetDirectoryName(Application.ExecutablePath);
                foreach (var f in Directory.GetFiles(dir, "*.old", SearchOption.AllDirectories))
                    try { File.Delete(f); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // -- window placement and the title bar

        string BoundsFile => Path.Combine(Where.Own, Where.LibraryMode ? "window-library.txt" : "window.txt");

        void LoadBounds()
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Scale(1120), Scale(780));
            try
            {
                var n = File.ReadAllText(BoundsFile).Split(',').Select(int.Parse).ToArray();
                var r = new Rectangle(n[0], n[1], n[2], n[3]);
                if (Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                    if (n.Length > 4 && n[4] == 1) WindowState = FormWindowState.Maximized;
                }
            }
            catch (Exception) { }
        }

        void SaveBounds()
        {
            try
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Directory.CreateDirectory(Where.Own);
                File.WriteAllText(BoundsFile, $"{r.X},{r.Y},{r.Width},{r.Height},{(WindowState == FormWindowState.Maximized ? 1 : 0)}");
            }
            catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        void DarkTitleBar(bool dark)
        {
            int on = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)  // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(Handle, 19, ref on, 4);   // its number on Windows 10 before 20H1
            }
            catch (Exception) { }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);

        static bool SameProgram(Process a, Process b)
        {
            try { return string.Equals(a.MainModule.FileName, b.MainModule.FileName, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return true; }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Where.LibraryMode = Where.ReadLibraryMode(args);
            var name = "StudyStash.Window." + Environment.UserName + (Where.LibraryMode ? ".library" : "");
            using (var one = new Mutex(true, name, out bool first))
            {
                if (!first && Where.Env("GRANOLA_SHARE_SELFTEST") == null)
                {
                    // Already open: bring that window forward instead of opening a second one.
                    var me = Process.GetCurrentProcess();
                    var other = Process.GetProcessesByName(me.ProcessName).FirstOrDefault(p => p.Id != me.Id
                        && p.MainWindowHandle != IntPtr.Zero && SameProgram(p, me));
                    if (other != null)
                    {
                        if (IsIconic(other.MainWindowHandle)) ShowWindow(other.MainWindowHandle, 9);  // SW_RESTORE
                        SetForegroundWindow(other.MainWindowHandle);
                    }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
