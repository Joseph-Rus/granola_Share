using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.Wave;

namespace StudyStash.Audio;

/// <summary>Plays a lecture's recording from a moment (an answer's "18:05"): AVAudioPlayer on a Mac, WASAPI on
/// Windows. One at a time; playing another stops the last.</summary>
public static class Player
{
    static IDisposable? playing;

    public static void Play(string wavPath, double fromSeconds)
    {
        Stop();
        try
        {
            if (OperatingSystem.IsMacOS()) playing = Mac.Play(wavPath, fromSeconds);
            else if (OperatingSystem.IsWindows()) playing = Win.Play(wavPath, fromSeconds);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException or IOException or InvalidOperationException)
        {
        }
    }

    public static void Stop()
    {
        playing?.Dispose();
        playing = null;
    }

    [SupportedOSPlatform("macos")]
    static class Mac
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)]
        static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr r, IntPtr s);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a, IntPtr b);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern void SendDouble(IntPtr r, IntPtr s, double a);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern bool SendBool(IntPtr r, IntPtr s);

        sealed class Handle(IntPtr player) : IDisposable
        {
            public void Dispose()
            {
                Send(player, sel_registerName("stop"));
                Send(player, sel_registerName("release"));
            }
        }

        public static IDisposable? Play(string path, double at)
        {
            NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
            IntPtr str = Send(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), Marshal.StringToCoTaskMemUTF8(path));
            IntPtr url = Send(objc_getClass("NSURL"), sel_registerName("fileURLWithPath:"), str);
            IntPtr player = Send(Send(objc_getClass("AVAudioPlayer"), sel_registerName("alloc")), sel_registerName("initWithContentsOfURL:error:"), url, IntPtr.Zero);
            if (player == IntPtr.Zero) return null;
            SendDouble(player, sel_registerName("setCurrentTime:"), at);
            SendBool(player, sel_registerName("play"));
            return new Handle(player);
        }
    }

    [SupportedOSPlatform("windows")]
    static class Win
    {
        sealed class Handle(WasapiPlayer player, WaveFileReader reader) : IDisposable
        {
            public void Dispose()
            {
                player.Stop();
                player.Dispose();
                reader.Dispose();
            }
        }

        public static IDisposable Play(string path, double at)
        {
            var reader = new WaveFileReader(path) { CurrentTime = TimeSpan.FromSeconds(at) };
            var player = new WasapiPlayerBuilder().WithDefaultDeviceStreamRouting().Build();
            player.Init(reader);
            player.Play();
            return new Handle(player, reader);
        }
    }
}
