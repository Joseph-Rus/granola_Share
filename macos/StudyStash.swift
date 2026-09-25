// Study Stash for Mac: a native window around the pages granola-share already serves.
//
// "This Mac" is the laptop's page (setup and status) from the background service on 127.0.0.1.
// "Library" is your library on the Mac mini, signed in with the password this Mac already has.
// On the Mac mini itself there's only the library. If the background helper isn't installed yet
// (someone opened the app straight from the DMG), the app installs it, without the Terminal.
//
// The same app, built as "Study Stash Library" (Info.plist StudyStashRole = library; build.sh makes
// both), is the library computer's own window: it shows only the library, and before there is one,
// it runs the library's setup in Terminal, where its questions are.
//
// Build: macos/build.sh (swiftc, no Xcode project).

import AppKit
import WebKit

// MARK: - Where things are

let fm = FileManager.default
let userHome = fm.homeDirectoryForCurrentUser
let dataDir: URL = {
    if let h = ProcessInfo.processInfo.environment["GRANOLA_SHARE_HOME"], !h.isEmpty {
        return URL(fileURLWithPath: (h as NSString).expandingTildeInPath)
    }
    return userHome.appendingPathComponent(".granola-share")
}()
let env = ProcessInfo.processInfo.environment
let engine = env["GRANOLA_SHARE_ENGINE"].map { URL(fileURLWithPath: $0) }
    ?? userHome.appendingPathComponent(".local/bin/granola-share")
let installScript = "https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.sh"
/// This copy is the library computer's app (Study-Stash-Library.dmg), not the laptop's.
let libraryMode = (Bundle.main.object(forInfoDictionaryKey: "StudyStashRole") as? String) == "library"
    || CommandLine.arguments.contains("--library")

func dataFile(_ name: String) -> String? {
    guard let s = try? String(contentsOf: dataDir.appendingPathComponent(name), encoding: .utf8) else { return nil }
    return s.trimmingCharacters(in: .whitespacesAndNewlines)
}

/// One `key = value` from our TOML files. config.py writes strings JSON-style, so JSON decodes them.
func tomlValue(_ text: String, _ key: String) -> String? {
    for raw in text.components(separatedBy: .newlines) {
        let line = raw.trimmingCharacters(in: .whitespaces)
        guard line.hasPrefix(key), let eq = line.firstIndex(of: "=") else { continue }
        guard line[..<eq].trimmingCharacters(in: .whitespaces) == key else { continue }
        let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
        guard value.hasPrefix("\"") else {
            return value.components(separatedBy: "#")[0].trimmingCharacters(in: .whitespaces)
        }
        var escaped = false
        for (i, ch) in value.enumerated() where i > 0 {
            if escaped { escaped = false; continue }
            if ch == "\\" { escaped = true; continue }
            if ch == "\"" {
                let quoted = String(value.prefix(i + 1))
                let arr = try? JSONSerialization.jsonObject(with: Data("[\(quoted)]".utf8)) as? [String]
                return arr?.first
            }
        }
        return nil
    }
    return nil
}

func js(_ s: String) -> String {
    let data = (try? JSONSerialization.data(withJSONObject: [s])) ?? Data("[\"\"]".utf8)
    let text = String(data: data, encoding: .utf8) ?? "[\"\"]"
    return String(text.dropFirst().dropLast())
}

/// What this Mac is: the laptop (sends lectures), the Mac mini (keeps the library), or both.
struct Place {
    var sends = true
    var library: URL?
    var key: String?
    var libraryName: String?

    static func read() -> Place {
        let client = dataFile("client.toml")
        let server = dataFile("config.toml")
        var p = Place(sends: !libraryMode && (server == nil || client != nil))
        if !libraryMode, let c = client, let s = tomlValue(c, "server_url"), !s.isEmpty, let u = URL(string: s) {
            p.library = u
            p.key = tomlValue(c, "pool_key").flatMap { $0.isEmpty ? nil : $0 }
            p.libraryName = tomlValue(c, "pool_name")
        } else if let s = server {
            let port = Int(tomlValue(s, "web_port") ?? "") ?? 8787
            p.library = URL(string: "http://127.0.0.1:\(port)")  // on the Mac mini itself: no password needed
            p.libraryName = tomlValue(s, "pool_name")
        }
        return p
    }
}

func pagePort() -> Int { Int(dataFile("ui_port") ?? "") ?? 8765 }
func pageURL() -> URL { URL(string: "http://127.0.0.1:\(pagePort())/?t=\(dataFile("ui_token") ?? "")")! }

func pageAnswers(_ done: @escaping (Bool) -> Void) {
    var req = URLRequest(url: URL(string: "http://127.0.0.1:\(pagePort())/healthz")!)
    req.timeoutInterval = 2
    URLSession.shared.dataTask(with: req) { data, resp, _ in
        let ok = (resp as? HTTPURLResponse)?.statusCode == 200
            && String(decoding: data ?? Data(), as: UTF8.self).contains("granola-share")
        DispatchQueue.main.async { done(ok) }
    }.resume()
}

/// Runs a command in the background; each output line and the end come back on the main thread.
func run(_ path: String, _ args: [String], line: ((String) -> Void)? = nil, done: @escaping (Int32, String) -> Void) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: path)
    p.arguments = args
    var env = ProcessInfo.processInfo.environment
    env["PATH"] = "\(userHome.path)/.local/bin:/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
    p.environment = env
    let pipe = Pipe()
    p.standardOutput = pipe
    p.standardError = pipe
    let lock = NSLock()
    var all = ""
    pipe.fileHandleForReading.readabilityHandler = { h in
        let d = h.availableData
        guard !d.isEmpty else { return }
        let s = String(decoding: d, as: UTF8.self)
        lock.lock(); all += s; lock.unlock()
        if let line = line {
            DispatchQueue.main.async { s.split(separator: "\n").forEach { line(String($0)) } }
        }
    }
    p.terminationHandler = { p in
        pipe.fileHandleForReading.readabilityHandler = nil
        let rest = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        lock.lock(); all += rest; let out = all; lock.unlock()
        DispatchQueue.main.async { done(p.terminationStatus, out) }
    }
    do { try p.run() } catch { done(-1, "\(error.localizedDescription)") }
}

// MARK: - The app's own screens (before the pages exist), in the same look as the pages

enum Screen {
    static func iconURI() -> String {
        guard let tiff = NSApp.applicationIconImage?.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
              let png = rep.representation(using: .png, properties: [:]) else { return "" }
        return "data:image/png;base64," + png.base64EncodedString()
    }

    static func page(icon: Bool = true, spinner: Bool = false, title: String, text: String,
                     buttons: [(String, String, Bool)] = [], log: Bool = false, detail: String = "") -> String {
        let btns = buttons.map { label, action, primary in
            "<button class=\"\(primary ? "primary" : "")\" onclick=\"webkit.messageHandlers.app.postMessage('\(action)')\">\(label)</button>"
        }.joined()
        let esc = detail.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
        return """
        <!doctype html><html><head><meta charset="utf-8"><meta name="color-scheme" content="light dark"><style>
        :root{--canvas:#f5f5f7;--group:#fff;--label:#1d1d1f;--label-2:rgba(60,60,67,.64);--fill:rgba(118,118,128,.12);--accent:#007aff}
        @media (prefers-color-scheme:dark){:root{--canvas:#1c1c1e;--group:#2c2c2e;--label:#f5f5f7;--label-2:rgba(235,235,245,.62);
          --fill:rgba(118,118,128,.26);--accent:#0a84ff}}
        html,body{height:100%}
        body{margin:0;display:grid;place-items:center;background:var(--canvas);color:var(--label);
          font:400 15px/1.45 -apple-system,BlinkMacSystemFont,"Helvetica Neue",sans-serif;-webkit-font-smoothing:antialiased;
          -webkit-user-select:none;cursor:default}
        main{width:min(30rem,100% - 3rem);text-align:center}
        img{width:96px;height:96px;margin-bottom:.8rem}
        h1{font-size:1.6rem;letter-spacing:-.02em;margin:0 0 .5rem}
        p{color:var(--label-2);margin:0 auto 1.4rem;max-width:26rem}
        button{font:500 .9375rem/1 -apple-system,sans-serif;padding:.62rem 1.2rem;border-radius:9px;border:0;margin:0 .25rem;
          background:var(--fill);color:var(--accent);cursor:pointer}
        button.primary{background:var(--accent);color:#fff}
        .spin{width:26px;height:26px;margin:0 auto 1rem;border-radius:50%;border:3px solid var(--fill);border-top-color:var(--label-2);
          animation:s .9s linear infinite}@keyframes s{to{transform:rotate(360deg)}}
        pre{text-align:left;font:12px/1.5 ui-monospace,Menlo,monospace;background:var(--group);border-radius:10px;padding:.7rem .9rem;
          max-height:11rem;overflow:auto;white-space:pre-wrap;color:var(--label-2);-webkit-user-select:text}
        pre:empty{display:none}
        </style></head><body><main>
        \(icon && !spinner ? "<img src=\"\(iconURI())\" alt=\"\">" : "")\(spinner ? "<div class=\"spin\"></div>" : "")
        <h1>\(title)</h1><p>\(text)</p><div>\(btns)</div>
        \(log || !esc.isEmpty ? "<pre id=\"log\">\(esc)</pre>" : "")
        </main><script>function addLog(t){var l=document.getElementById('log');if(!l)return;
        l.textContent+=(l.textContent?'\\n':'')+t;l.scrollTop=l.scrollHeight;}</script></body></html>
        """
    }

    static let libraryWelcome = page(
        title: "Set up your library",
        text: "This Mac will keep your lectures: it writes their study notes with a model that runs here, sorts them "
            + "by class, and serves them to your laptop and phone. Setup opens Terminal with a few questions, and can "
            + "install Tailscale and Ollama for you. It takes about five minutes, longer if it downloads a model.",
        buttons: [("Set Up", "setup-library", true)])

    static let welcome = page(
        title: "Welcome to Study Stash",
        text: "It sends the lectures you record in Granola to your library, where your own model writes their "
            + "study notes. First it installs a small helper that runs in the background. That takes about a minute.",
        buttons: [("Install and Continue", "install", true)])
}

// MARK: - The app

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSToolbarDelegate, NSToolbarItemValidation,
    WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler, WKDownloadDelegate {

    var window: NSWindow!
    let container = NSView()
    var laptop: WKWebView!
    var library: WKWebView!
    var current: WKWebView!
    let tabs = NSSegmentedControl(labels: ["This Mac", "Library"], trackingMode: .selectOne, target: nil, action: nil)
    var place = Place.read()
    var libraryShown: URL?
    var signingIn = false
    var waiting: Timer?
    var downloads: [ObjectIdentifier: URL] = [:]
    // The README's screenshots (macos/tour.sh): GRANOLA_SHARE_TOUR="name=tab:path,…" shows each one and
    // saves GRANOLA_SHARE_TOUR_DIR/<name>.png, then quits. A path of "-" takes the tab as it is.
    var tour: [(name: String, tab: String, path: String)] = (env["GRANOLA_SHARE_TOUR"] ?? "")
        .split(separator: ",").compactMap { item in
            let kv = item.split(separator: "=", maxSplits: 1).map(String.init)
            guard kv.count == 2 else { return nil }
            let at = kv[1].split(separator: ":", maxSplits: 1).map(String.init)
            return (kv[0], at[0], at.count > 1 ? at[1] : "-")
        }
    var onLoaded: (() -> Void)?
    lazy var store: WKWebsiteDataStore = tour.isEmpty ? .default() : .nonPersistent()

    let backID = NSToolbarItem.Identifier("back")
    let tabsID = NSToolbarItem.Identifier("tabs")
    let reloadID = NSToolbarItem.Identifier("reload")

    // MARK: launch

    func applicationDidFinishLaunching(_ note: Notification) {
        buildMenu()
        laptop = makeWebView()
        library = makeWebView()
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1120, height: 780),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Study Stash"
        window.minSize = NSSize(width: 520, height: 460)
        window.delegate = self
        window.contentView = container
        if tour.isEmpty {
            window.setFrameAutosaveName("GranolaShareWindow")
            if !window.setFrameUsingName("GranolaShareWindow") { window.center() }
        } else {
            if let look = env["GRANOLA_SHARE_APPEARANCE"] { NSApp.appearance = NSAppearance(named: look == "dark" ? .darkAqua : .aqua) }
            window.setContentSize(NSSize(width: 1180, height: 760))
            window.center()
        }
        tabs.target = self
        tabs.action = #selector(tabChanged)
        tabs.segmentStyle = .automatic
        let bar = NSToolbar(identifier: "main")
        bar.delegate = self
        bar.displayMode = .iconOnly
        if #available(macOS 13.0, *) { bar.centeredItemIdentifiers = [tabsID] } else { bar.centeredItemIdentifier = tabsID }
        window.toolbar = bar
        window.toolbarStyle = .unified
        start()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        if !tour.isEmpty { DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self.nextShot() } }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ app: NSApplication) -> Bool { true }

    func start() {
        place = Place.read()
        tabs.isHidden = !place.sends
        if place.sends { show(laptop); openLaptop() } else { show(library); openLibrary() }
    }

    func makeWebView() -> WKWebView {
        let cfg = WKWebViewConfiguration()
        cfg.websiteDataStore = store  // keeps the sign-in cookies between launches (not on a tour)
        let ucc = WKUserContentController()
        ucc.add(self, name: "copy")
        ucc.add(self, name: "app")
        // Copy buttons use navigator.clipboard, which plain-http pages (the library) don't get: do it natively.
        ucc.addUserScript(WKUserScript(source: """
            (function(){var w=function(t){window.webkit.messageHandlers.copy.postMessage(String(t));return Promise.resolve();};
            try{Object.defineProperty(navigator,'clipboard',{value:{writeText:w},configurable:true});}catch(e){}})();
            """, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        cfg.userContentController = ucc
        let v = WKWebView(frame: .zero, configuration: cfg)
        v.navigationDelegate = self
        v.uiDelegate = self
        v.allowsBackForwardNavigationGestures = true
        v.setValue(false, forKey: "drawsBackground")  // no white flash in dark mode
        if #available(macOS 12.0, *) { v.underPageBackgroundColor = .windowBackgroundColor }
        return v
    }

    func show(_ v: WKWebView) {
        current = v
        container.subviews.forEach { $0.removeFromSuperview() }
        v.frame = container.bounds
        v.autoresizingMask = [.width, .height]
        container.addSubview(v)
        tabs.selectedSegment = v === library ? 1 : 0
        window.subtitle = v.title ?? ""
        window.makeFirstResponder(v)
    }

    // MARK: This Mac

    func openLaptop() {
        pageAnswers { ok in
            if ok {
                self.laptop.load(URLRequest(url: pageURL()))
            } else if fm.isExecutableFile(atPath: engine.path) {
                self.startHelper(install: false)
            } else {
                self.laptop.loadHTMLString(Screen.welcome, baseURL: nil)
            }
        }
    }

    func startHelper(install: Bool) {
        laptop.loadHTMLString(Screen.page(spinner: true, title: "Starting Study Stash", text: "This takes a few seconds."),
                              baseURL: nil)
        var args = ["--home", dataDir.path, "client", "open", "--no-browser"]
        if install { args.append("--install") }
        run(engine.path, args) { code, out in
            if code == 0 {
                self.place = Place.read()
                self.laptop.load(URLRequest(url: pageURL()))
            } else {
                self.problem(self.laptop, "Study Stash didn't start",
                             "Its background helper didn't answer. Try again, or look at its log.", out)
            }
        }
    }

    func installHelper() {
        laptop.loadHTMLString(Screen.page(spinner: true, title: "Installing",
                                          text: "Getting the background helper. This needs the internet and takes about a minute.",
                                          log: true), baseURL: nil)
        let cmd = "curl -fsSL \(installScript) | GRANOLA_SHARE_NO_SETUP=1 sh -s -- client"
        run("/bin/sh", ["-c", cmd], line: { l in
            self.laptop.evaluateJavaScript("addLog(\(js(l)))", completionHandler: nil)
        }) { code, out in
            if code == 0 && fm.isExecutableFile(atPath: engine.path) {
                self.startHelper(install: true)
            } else {
                self.problem(self.laptop, "The install didn't finish",
                             "Check that this Mac is online, then try again.", out, retry: "install")
            }
        }
    }

    // MARK: Library

    func openLibrary() {
        place = Place.read()
        waiting?.invalidate()
        guard let url = place.library else {
            library.loadHTMLString(libraryMode ? Screen.libraryWelcome : Screen.page(title: "No library yet",
                                               text: "Connect this Mac to your library on the This Mac tab. Its lectures show up here.",
                                               buttons: [("Go to This Mac", "laptop", true)]), baseURL: nil)
            libraryShown = nil
            return
        }
        libraryShown = url
        library.load(URLRequest(url: url))
    }

    /// The library's setup asks questions, so it runs in Terminal. This waits for the library to answer,
    /// then shows it.
    func setUpLibrary() {
        let script = FileManager.default.temporaryDirectory.appendingPathComponent("Set Up Study Stash Library.command")
        let text = """
            #!/bin/sh
            # Opened by Study Stash Library: sets up the library on this Mac.
            clear
            if curl -fsSL \(installScript) | sh -s -- server; then
              echo; echo "Done. Your library opens in Study Stash Library."
            else
              echo; echo "Setup didn't finish. Run it again from Study Stash Library."
            fi
            printf "Press Return to close this window. "; read _
            """
        do {
            try text.write(to: script, atomically: true, encoding: .utf8)
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        } catch {
            return problem(library, "Setup didn't start", error.localizedDescription, retry: "setup-library")
        }
        NSWorkspace.shared.open(script)
        library.loadHTMLString(Screen.page(spinner: true, title: "Setting up your library",
                                           text: "Answer the questions in the Terminal window. When setup is done, your library shows up here.",
                                           buttons: [("Open Setup Again", "setup-library", false)]), baseURL: nil)
        waitForLibrary()
    }

    /// Start the library's background service (it also starts when you log in).
    func startLibrary() {
        library.loadHTMLString(Screen.page(spinner: true, title: "Starting your library", text: "This takes a few seconds."),
                               baseURL: nil)
        run(engine.path, ["--home", dataDir.path, "autostart", "install", "--role", "server"]) { _, _ in self.waitForLibrary() }
    }

    /// Checks every two seconds until the library answers, then shows it.
    func waitForLibrary() {
        waiting?.invalidate()
        waiting = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] timer in
            guard let self = self else { return timer.invalidate() }
            let now = Place.read()
            guard let url = now.library else { return }
            var req = URLRequest(url: url)
            req.timeoutInterval = 2
            URLSession.shared.dataTask(with: req) { _, resp, _ in
                guard (resp as? HTTPURLResponse)?.statusCode == 200 else { return }
                DispatchQueue.main.async {
                    guard timer.isValid else { return }
                    timer.invalidate()
                    self.place = now
                    self.openLibrary()
                }
            }.resume()
        }
    }

    /// The library asks for its password once per browser; this Mac already has it, so fill it in.
    func signInIfAsked(_ v: WKWebView) {
        guard v === library, let u = v.url, u.host == place.library?.host else { return }
        if u.path == "/login" {
            guard !signingIn, !(u.query ?? "").contains("bad=1"), let key = place.key else { return }
            signingIn = true
            v.evaluateJavaScript("""
                (function(){var f=document.querySelector('form[action="/login"]');
                if(f){f.elements.password.value=\(js(key));f.submit();}})();
                """, completionHandler: nil)
        } else {
            signingIn = false
        }
    }

    // MARK: switching

    @objc func tabChanged() { tabs.selectedSegment == 1 ? showLibrary() : showLaptop() }

    @objc func showLaptop() {
        guard place.sends else { return }
        show(laptop)
        if laptop.url == nil { openLaptop() }
    }

    @objc func showLibrary() {
        show(library)
        let now = Place.read()
        if libraryShown == nil || now.library != libraryShown || library.url == nil {
            place = now
            openLibrary()
        }
    }

    /// Where a link goes: the library's tab, this Mac's tab, or the browser for anything else.
    func route(_ url: URL) {
        if let lib = place.library, url.host == lib.host, url.port == lib.port {
            show(library)
            libraryShown = lib
            library.load(URLRequest(url: url))
        } else if ["127.0.0.1", "localhost"].contains(url.host ?? "") {
            showLaptop()
            laptop.load(URLRequest(url: url))
        } else {
            NSWorkspace.shared.open(url)
        }
    }

    func ours(_ url: URL) -> Bool {
        let h = url.host ?? ""
        return h == "127.0.0.1" || h == "localhost" || (place.library.map { $0.host == h } ?? false)
    }

    func problem(_ v: WKWebView, _ title: String, _ text: String, _ detail: String = "", retry: String = "retry") {
        var buttons = [("Try Again", retry, true)]
        if v === laptop { buttons.append(("Show Log", "log", false)) }
        if v === library, place.library?.host == "127.0.0.1", fm.isExecutableFile(atPath: engine.path) {
            buttons.append(("Start It", "start-library", false))
        }
        v.loadHTMLString(Screen.page(title: title, text: text, buttons: buttons,
                                     detail: String(detail.suffix(1500))), baseURL: nil)
    }

    // MARK: toolbar

    func toolbarDefaultItemIdentifiers(_ t: NSToolbar) -> [NSToolbarItem.Identifier] {
        [backID, .flexibleSpace, tabsID, .flexibleSpace, reloadID]
    }

    func toolbarAllowedItemIdentifiers(_ t: NSToolbar) -> [NSToolbarItem.Identifier] {
        [backID, tabsID, reloadID, .flexibleSpace, .space]
    }

    func toolbar(_ t: NSToolbar, itemForItemIdentifier id: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        let item = NSToolbarItem(itemIdentifier: id)
        switch id {
        case backID:
            item.image = NSImage(systemSymbolName: "chevron.left", accessibilityDescription: "Back")
            item.label = "Back"
            item.toolTip = "Back"
            item.action = #selector(goBack)
            item.target = self
            item.isBordered = true
        case reloadID:
            item.image = NSImage(systemSymbolName: "arrow.clockwise", accessibilityDescription: "Reload")
            item.label = "Reload"
            item.toolTip = "Reload"
            item.action = #selector(reload)
            item.target = self
            item.isBordered = true
        case tabsID:
            item.view = tabs
            item.label = "Show"
        default:
            return nil
        }
        return item
    }

    func validateToolbarItem(_ item: NSToolbarItem) -> Bool {
        item.itemIdentifier == backID ? (current?.canGoBack ?? false) : true
    }

    @objc func goBack() { current?.goBack() }
    @objc func goForward() { current?.goForward() }
    @objc func reload() {
        if current?.url == nil || current?.url?.scheme == "about" {
            current === library ? openLibrary() : openLaptop()
        } else {
            current?.reload()
        }
    }
    @objc func zoomIn() { current.pageZoom = min(current.pageZoom + 0.1, 2.0) }
    @objc func zoomOut() { current.pageZoom = max(current.pageZoom - 0.1, 0.6) }
    @objc func zoomReset() { current.pageZoom = 1.0 }
    @objc func openDataFolder() { NSWorkspace.shared.open(dataDir) }

    // MARK: menus

    func buildMenu() {
        let main = NSMenu()
        func menu(_ title: String, _ items: [NSMenuItem]) -> NSMenu {
            let m = NSMenu(title: title)
            items.forEach { m.addItem($0) }
            let holder = NSMenuItem()
            holder.submenu = m
            main.addItem(holder)
            return m
        }
        func item(_ title: String, _ action: Selector?, _ key: String = "", _ mods: NSEvent.ModifierFlags = .command,
                  mine: Bool = false) -> NSMenuItem {
            let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
            i.keyEquivalentModifierMask = mods
            if mine { i.target = self }
            return i
        }
        _ = menu("Study Stash", [
            item("About Study Stash", #selector(NSApplication.orderFrontStandardAboutPanel(_:))),
            .separator(),
            item("Open Data Folder", #selector(openDataFolder), mine: true),
            .separator(),
            item("Hide Study Stash", #selector(NSApplication.hide(_:)), "h"),
            item("Hide Others", #selector(NSApplication.hideOtherApplications(_:)), "h", [.command, .option]),
            item("Show All", #selector(NSApplication.unhideAllApplications(_:))),
            .separator(),
            item("Quit Study Stash", #selector(NSApplication.terminate(_:)), "q"),
        ])
        _ = menu("Edit", [
            item("Undo", Selector(("undo:")), "z"),
            item("Redo", Selector(("redo:")), "z", [.command, .shift]),
            .separator(),
            item("Cut", #selector(NSText.cut(_:)), "x"),
            item("Copy", #selector(NSText.copy(_:)), "c"),
            item("Paste", #selector(NSText.paste(_:)), "v"),
            item("Select All", #selector(NSText.selectAll(_:)), "a"),
        ])
        _ = menu("View", [
            item("This Mac", #selector(showLaptop), "1", mine: true),
            item("Library", #selector(showLibrary), "2", mine: true),
            .separator(),
            item("Back", #selector(goBack), "[", mine: true),
            item("Forward", #selector(goForward), "]", mine: true),
            item("Reload", #selector(reload), "r", mine: true),
            .separator(),
            item("Actual Size", #selector(zoomReset), "0", mine: true),
            item("Zoom In", #selector(zoomIn), "+", mine: true),
            item("Zoom Out", #selector(zoomOut), "-", mine: true),
            .separator(),
            item("Enter Full Screen", #selector(NSWindow.toggleFullScreen(_:)), "f", [.command, .control]),
        ])
        let win = menu("Window", [
            item("Minimize", #selector(NSWindow.performMiniaturize(_:)), "m"),
            item("Zoom", #selector(NSWindow.performZoom(_:))),
            item("Close", #selector(NSWindow.performClose(_:)), "w"),
        ])
        NSApp.mainMenu = main
        NSApp.windowsMenu = win
    }

    // MARK: web view: navigation

    func webView(_ v: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url, let scheme = url.scheme?.lowercased() else { return decisionHandler(.allow) }
        if action.shouldPerformDownload { return decisionHandler(.download) }
        if ["about", "data", "blob"].contains(scheme) || ours(url) || action.targetFrame?.isMainFrame == false {
            return decisionHandler(.allow)
        }
        NSWorkspace.shared.open(url)  // release notes, Tailscale, mail links: the default app
        decisionHandler(.cancel)
    }

    func webView(_ v: WKWebView, decidePolicyFor response: WKNavigationResponse,
                 decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        let disposition = (response.response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Disposition") ?? ""
        if disposition.lowercased().hasPrefix("attachment") || !response.canShowMIMEType {
            return decisionHandler(.download)
        }
        decisionHandler(.allow)
    }

    func webView(_ v: WKWebView, didFinish nav: WKNavigation!) {
        if v === current { window.subtitle = v.title ?? "" }
        signInIfAsked(v)
        if v === current, let done = onLoaded, let u = v.url, u.scheme != "about", u.path != "/login" {
            onLoaded = nil
            done()
        }
    }

    func webView(_ v: WKWebView, didFailProvisionalNavigation nav: WKNavigation!, withError error: Error) {
        let e = error as NSError
        if e.code == NSURLErrorCancelled || (e.domain == "WebKitErrorDomain" && e.code == 102) { return }  // a download
        if v === laptop {
            problem(v, "Study Stash isn't answering", "Its background helper may be restarting. Try again in a moment.")
        } else {
            let host = place.library?.host ?? "your library"
            problem(v, "Can't reach your library",
                    place.library?.host == "127.0.0.1"
                        ? "The library on this Mac isn't running. It starts when you log in; try again in a moment."
                        : "Nothing answered at \(host). Is the Mac mini awake, with Tailscale on?",
                    e.localizedDescription)
        }
    }

    func webView(_ v: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }

    func webView(_ v: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    // MARK: web view: downloads go to Downloads, then show in Finder

    func download(_ d: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String,
                  completionHandler: @escaping (URL?) -> Void) {
        let dir = fm.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        let base = (suggestedFilename as NSString).deletingPathExtension
        let ext = (suggestedFilename as NSString).pathExtension
        var dest = dir.appendingPathComponent(suggestedFilename)
        var n = 2
        while fm.fileExists(atPath: dest.path) {
            dest = dir.appendingPathComponent(ext.isEmpty ? "\(base) \(n)" : "\(base) \(n).\(ext)")
            n += 1
        }
        downloads[ObjectIdentifier(d)] = dest
        completionHandler(dest)
    }

    func downloadDidFinish(_ d: WKDownload) {
        if let dest = downloads.removeValue(forKey: ObjectIdentifier(d)) {
            NSWorkspace.shared.activateFileViewerSelecting([dest])
        }
    }

    func download(_ d: WKDownload, didFailWithError error: Error, resumeData: Data?) {
        downloads.removeValue(forKey: ObjectIdentifier(d))
        let a = NSAlert()
        a.messageText = "The download didn't finish"
        a.informativeText = error.localizedDescription
        a.beginSheetModal(for: window)
    }

    // MARK: web view: dialogs and new windows

    func webView(_ v: WKWebView, runJavaScriptAlertPanelWithMessage message: String, initiatedByFrame frame: WKFrameInfo,
                 completionHandler: @escaping () -> Void) {
        let a = NSAlert()
        a.messageText = message
        a.beginSheetModal(for: window) { _ in completionHandler() }
    }

    func webView(_ v: WKWebView, runJavaScriptConfirmPanelWithMessage message: String, initiatedByFrame frame: WKFrameInfo,
                 completionHandler: @escaping (Bool) -> Void) {
        let a = NSAlert()
        let parts = message.components(separatedBy: "? ")
        a.messageText = parts.count > 1 ? parts[0] + "?" : message
        a.informativeText = parts.count > 1 ? parts.dropFirst().joined(separator: "? ") : ""
        let lower = message.lowercased()
        let ok = a.addButton(withTitle: lower.hasPrefix("delete") ? "Delete" : lower.hasPrefix("stop") ? "Remove" : "OK")
        a.addButton(withTitle: "Cancel")
        if lower.hasPrefix("delete") || lower.hasPrefix("stop") { ok.hasDestructiveAction = true }
        a.beginSheetModal(for: window) { r in completionHandler(r == .alertFirstButtonReturn) }
    }

    func webView(_ v: WKWebView, createWebViewWith cfg: WKWebViewConfiguration, for action: WKNavigationAction,
                 windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = action.request.url { route(url) }  // target=_blank: "Open your library" lands in its tab
        return nil
    }

    // MARK: messages from the pages

    func userContentController(_ c: WKUserContentController, didReceive m: WKScriptMessage) {
        if m.name == "copy", let s = m.body as? String {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(s, forType: .string)
            return
        }
        guard m.name == "app", let action = m.body as? String else { return }
        switch action {
        case "install": installHelper()
        case "setup-library": setUpLibrary()
        case "start-library": startLibrary()
        case "laptop": showLaptop()
        case "log": NSWorkspace.shared.open(dataDir.appendingPathComponent("logs"))
        default: m.webView === library ? openLibrary() : openLaptop()
        }
    }
}

// MARK: - the README's screenshots

extension AppDelegate {
    func nextShot() {
        guard !tour.isEmpty else { return NSApp.terminate(nil) }
        let shot = tour.removeFirst()
        let v: WKWebView = shot.tab == "library" ? library : laptop
        show(v)
        let snap = { DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { self.capture(shot.name) { self.nextShot() } } }
        guard shot.path != "-" else { return snap() }
        let base = shot.tab == "library" ? place.library : URL(string: "http://127.0.0.1:\(pagePort())")
        guard let url = shot.path == "/" && shot.tab != "library" ? pageURL() : URL(string: shot.path, relativeTo: base)
        else { return nextShot() }
        onLoaded = snap
        v.load(URLRequest(url: url))
    }

    /// The window as it looks: the title bar and toolbar from AppKit, the page from WebKit (drawn in
    /// another process, so it has to be snapshotted separately). Capturing your own window needs no
    /// Screen Recording permission.
    func capture(_ name: String, then: @escaping () -> Void) {
        // Blur what shouldn't be in a public picture: text matching GRANOLA_SHARE_TOUR_BLUR (only the
        // matching part), and whole elements matching GRANOLA_SHARE_TOUR_BLUR_SELECTOR.
        let pattern = env["GRANOLA_SHARE_TOUR_BLUR"] ?? "", selector = env["GRANOLA_SHARE_TOUR_BLUR_SELECTOR"] ?? ""
        guard !pattern.isEmpty || !selector.isEmpty, current.url?.scheme != "about" else { return snapshot(name, then: then) }
        current.evaluateJavaScript("""
            (function(re, sel){
              var blur = function(e){ e.style.filter = 'blur(7px)'; };
              if (sel) document.querySelectorAll(sel).forEach(blur);
              if (!re) return;
              var rx = new RegExp(re, 'g'), w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT), nodes = [];
              while (w.nextNode()) nodes.push(w.currentNode);
              nodes.forEach(function(n){
                var text = n.nodeValue, parts = [], last = 0, m;
                rx.lastIndex = 0;
                while ((m = rx.exec(text))) {
                  parts.push(document.createTextNode(text.slice(last, m.index)));
                  var s = document.createElement('span'); s.textContent = m[0]; blur(s); parts.push(s);
                  last = m.index + m[0].length;
                }
                if (!parts.length) return;
                parts.push(document.createTextNode(text.slice(last)));
                parts.forEach(function(p){ n.parentNode.insertBefore(p, n); });
                n.remove();
              });
            })(\(js(pattern)), \(js(selector)));
            """) { _, _ in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { self.snapshot(name, then: then) }
        }
    }

    func snapshot(_ name: String, then: @escaping () -> Void) {
        guard let frame = window.contentView?.superview,
              let chrome = frame.bitmapImageRepForCachingDisplay(in: frame.bounds) else { return then() }
        frame.cacheDisplay(in: frame.bounds, to: chrome)
        let v: WKWebView = current
        v.takeSnapshot(with: nil) { image, _ in
            let size = frame.bounds.size
            let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: chrome.pixelsWide, pixelsHigh: chrome.pixelsHigh,
                                       bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                       colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            out.size = size
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
            chrome.draw(in: frame.bounds)
            var r = v.convert(v.bounds, to: frame)
            if frame.isFlipped { r.origin.y = size.height - r.maxY }
            image?.draw(in: r)
            NSGraphicsContext.restoreGraphicsState()
            let dir = URL(fileURLWithPath: env["GRANOLA_SHARE_TOUR_DIR"] ?? NSTemporaryDirectory())
            try? out.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent("\(name).png"))
            then()
        }
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
