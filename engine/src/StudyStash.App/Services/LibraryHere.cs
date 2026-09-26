using System.Net;
using System.Net.Sockets;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>
/// Making this computer the library: writes its config, starts <see cref="LibraryService"/> as this app's own child
/// process (nothing installed), and points the laptop side at it. <paramref name="host"/> (see <see cref="CreateAsync"/>)
/// takes charge of that library from then on, so it stops with the app, even on the very first run, before
/// <see cref="AppHost.Start"/> would otherwise have started one itself.
/// </summary>
public sealed class LibraryHere
{
    /// <summary>The engine to run: the app itself (`StudyStash --home H serve`) unless a test gives its own.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    public static LibraryHere ThisComputer() => new();

    /// <summary>The folder a new library keeps its notes in: Documents/Study Stash.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Study Stash");

    static bool IsFree(int port)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        // Not on Windows: there the flag would let us share a port someone is listening on.
        if (!OperatingSystem.IsWindows()) s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>8787 if it (and 8788, for Claude) are free; otherwise the next pair that both are.</summary>
    static int FreePortPair()
    {
        for (int port = 8787; port < 8787 + 200; port++)
            if (IsFree(port) && IsFree(port + 1))
                return port;
        throw new InvalidOperationException("No free ports found for the library.");
    }

    /// <summary>
    /// Write config.toml for a library on this computer (or fill in one already here), start it, and point client.toml
    /// at it (both roles keep talking to it over the network, even Library-only, so the library window still works).
    /// </summary>
    public async Task<string> CreateAsync(AppHost host, string name, string password, string displayName,
        AppRole role = AppRole.Both, bool localOnly = false)
    {
        name = name.Trim().Length > 0 ? name.Trim() : "My library";
        if (password.Trim().Length < 4) throw new ArgumentException("Use a password of at least 4 characters.");
        var cfg = Configs.Load(host.Home);
        if (!File.Exists(cfg.ConfigPath))
        {
            cfg.PoolDir = DefaultFolder;
            cfg.WebPort = FreePortPair();
        }
        cfg.PoolName = name;
        cfg.PoolPassword = password.Trim();
        if (cfg.AdminPassword.Length == 0) cfg.AdminPassword = StudyStash.Library.Http.TokenUrlSafe(12);
        cfg.WebHost = localOnly ? "127.0.0.1" : "0.0.0.0";
        Directory.CreateDirectory(cfg.PoolDir);
        Configs.Save(cfg);
        var svc = Command is null ? new LibraryService(host.Home, cfg) : new LibraryService(host.Home, cfg, Command);
        await svc.StartAsync();
        if (svc.State is not (LibraryServiceState.Running or LibraryServiceState.Elsewhere))
            throw new InvalidOperationException("The library didn't start." + (svc.Failure is { Length: > 0 } f ? $" {f}." : "") + " Its log is in the logs folder.");
        host.UseLocalLibrary(svc);
        host.Save(s => s.Role = role);
        var cc = Configs.LoadClient(host.Home);
        cc.ServerUrl = $"http://127.0.0.1:{cfg.WebPort}";
        cc.PoolKey = cfg.PoolPassword;
        cc.PoolName = cfg.PoolName;
        if (cc.DisplayName.Length == 0) cc.DisplayName = displayName;
        Configs.SaveClient(cc);
        return $"{name} is ready on this {(OperatingSystem.IsMacOS() ? "Mac" : "PC")}.";
    }
}
