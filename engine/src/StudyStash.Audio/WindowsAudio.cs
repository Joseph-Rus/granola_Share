using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using StudyStash.Core;

namespace StudyStash.Audio;

/// <summary>
/// Windows sound through WASAPI: the default microphone and, for a lecture on Zoom or Teams, what the computer plays.
/// Both become 16 kHz mono and are added together; the microphone keeps time, since Windows sends nothing for
/// the computer's sound while it's silent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSound(bool withComputerAudio) : IAudioSource
{
    const int MaxLag = Sound.Rate; // a second of the computer's sound waiting for the microphone
    readonly Lock gate = new();
    readonly Queue<float> played = new();
    WasapiRecorder? mic, loop;
    Resampler? micRate, loopRate;

    public string Name => withComputerAudio ? "Microphone and computer audio" : "Microphone";
    public int SampleRate => Sound.Rate;
    public int Channels => 1;
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;

    public void Start()
    {
        if (mic is not null) return;
        using (var devices = new MMDeviceEnumerator())
        {
            if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var input) || input is null)
                throw new InvalidOperationException("There's no microphone. Plug one in, or check Settings → System → Sound.");
        }
        // Windows converts to 16 kHz mono itself; the resampler covers a driver that won't.
        var wanted = WaveFormat.CreateIeeeFloatWaveFormat(Sound.Rate, 1);
        try
        {
            mic = new WasapiRecorderBuilder().WithDefaultDeviceStreamRouting().WithFormat(wanted).Build();
            micRate = new Resampler(mic.WaveFormat.SampleRate, mic.WaveFormat.Channels);
            var micFormat = mic.WaveFormat;
            mic.DataAvailable += (buffer, flags, _, _) => OnMic(Floats(buffer, micFormat, flags));
            mic.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Failed?.Invoke(Explain(e.Exception));
            };
            if (withComputerAudio)
            {
                try
                {
                    loop = new WasapiRecorderBuilder().WithLoopbackCapture().WithFormat(wanted).Build();
                    loopRate = new Resampler(loop.WaveFormat.SampleRate, loop.WaveFormat.Channels);
                    var loopFormat = loop.WaveFormat;
                    loop.DataAvailable += (buffer, flags, _, _) => OnPlayed(Floats(buffer, loopFormat, flags));
                }
                catch (COMException)
                {
                    loop = null; // no speakers: the microphone alone
                }
            }
            mic.StartRecording();
            loop?.StartRecording();
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            Stop();
            throw new InvalidOperationException(Explain(e));
        }
    }

    /// <summary>The words for a device error. A device that went away mid-lecture (unplugged, AUDCLNT_E_DEVICE_INVALIDATED)
    /// raises <see cref="Failed"/> with these, and the recorder opens the default microphone again before pausing.</summary>
    static string Explain(Exception e) =>
        e is UnauthorizedAccessException || (e is COMException c && (uint)c.HResult == 0x80070005)
            ? RecordingWords.CantHearWindows
            : $"The microphone stopped ({e.Message}).";

    void OnPlayed(float[] raw)
    {
        var s = loopRate!.Process(raw);
        lock (gate)
        {
            foreach (float x in s) played.Enqueue(x);
            while (played.Count > MaxLag) played.Dequeue();
        }
    }

    void OnMic(float[] raw)
    {
        var s = micRate!.Process(raw);
        if (loop is not null)
        {
            lock (gate)
            {
                for (int i = 0; i < s.Length && played.Count > 0; i++) s[i] = Math.Clamp(s[i] + played.Dequeue(), -1f, 1f);
            }
        }
        if (s.Length > 0) Samples?.Invoke(s);
    }

    /// <summary>A WASAPI buffer as interleaved floats: 32-bit float, or 16/24/32-bit integers. A buffer Windows marks
    /// silent is silence, whatever it holds.</summary>
    internal static float[] Floats(ReadOnlySpan<byte> buffer, WaveFormat format, AudioClientBufferFlags flags = AudioClientBufferFlags.None)
    {
        int bits = format.BitsPerSample, size = Math.Max(1, bits / 8), n = buffer.Length / size;
        if (flags.HasFlag(AudioClientBufferFlags.Silent)) return new float[n];
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible x && x.SubFormat == IeeeFloat);
        if (isFloat && bits == 32) return MemoryMarshal.Cast<byte, float>(buffer[..(n * 4)]).ToArray();
        var result = new float[n];
        for (int i = 0; i < n; i++)
        {
            var at = buffer.Slice(i * size, size);
            result[i] = bits switch
            {
                16 => BitConverter.ToInt16(at) / 32768f,
                24 => (at[0] | at[1] << 8 | (sbyte)at[2] << 16) / 8388608f,
                32 => BitConverter.ToInt32(at) / 2147483648f,
                _ => 0,
            };
        }
        return result;
    }

    static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    public void Stop()
    {
        try
        {
            mic?.StopRecording();
            loop?.StopRecording();
        }
        catch (COMException)
        {
        }
        mic?.Dispose();
        loop?.Dispose();
        mic = null;
        loop = null;
    }

    public void Dispose() => Stop();
}

[SupportedOSPlatform("windows")]
public static class WindowsPermissions
{
    public const string MicrophoneSettings = "ms-settings:privacy-microphone";
    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>Whether Windows lets desktop apps use the microphone: the switches in Settings → Privacy & security →
    /// Microphone.</summary>
    public static MicAccess Microphone()
    {
        string? Read(RegistryKey root, string sub) => root.OpenSubKey(sub)?.GetValue("Value") as string;
        if (Read(Registry.LocalMachine, Store) == "Deny") return MicAccess.Restricted;
        if (Read(Registry.CurrentUser, Store) == "Deny" || Read(Registry.CurrentUser, Store + @"\NonPackaged") == "Deny") return MicAccess.Denied;
        using var devices = new MMDeviceEnumerator();
        return devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var d) && d is not null ? MicAccess.Allowed : MicAccess.Unknown;
    }
}
