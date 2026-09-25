using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StudyStash.Core;

namespace StudyStash.Library;

/// <summary>
/// What the app pages on this computer share: the library's setup page now, the laptop's page later. The Study
/// Stash app opens them with a token only this computer's account can read (ui_token).
/// </summary>
public static class AppPage
{
    public const string Cookie = "gs_app";

    public static string Csp(string nonce) => PageText.AppCsp.Replace("{nonce}", nonce);

    /// <summary>The token in ui_token, made the first time; a fresh one each run when it can't be saved.</summary>
    public static string Token(string home)
    {
        string path = Path.Combine(home, "ui_token");
        try
        {
            if (File.Exists(path)) return Py.Strip(Py.ReadText(path));
            Directory.CreateDirectory(home);
            string token = Http.TokenUrlSafe(24);
            Py.WriteText(path, token);
            Py.OwnerOnly(path);
            return token;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Http.TokenUrlSafe(24);
        }
    }

    /// <summary>The first port from `start` nobody is listening on, on this computer only.</summary>
    public static int FreePort(int start, int tries = 10)
    {
        for (int port = start; port < start + tries; port++)
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // Not on Windows: there the flag would let us share a port someone is listening on.
            if (!OperatingSystem.IsWindows()) s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                s.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }
        throw new InvalidOperationException($"ports {start} to {start + tries - 1} are all taken");
    }

    /// <summary>Open a page in the browser.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo(url) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [url]) { UseShellExecute = false, CreateNoWindow = true };
            using var _ = Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // someone opens it by hand: the address is in the log
        }
    }
}
