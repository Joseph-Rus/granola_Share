using StudyStash.Core;

namespace StudyStash.Audio;

/// <summary>This computer's microphone, whichever system it runs.</summary>
public static class Microphones
{
    /// <summary>The microphone, and on Windows (if asked) what the computer plays too.</summary>
    public static IAudioSource Open(bool withComputerAudio = false)
    {
        if (OperatingSystem.IsMacOS()) return new MacMicrophone();
        if (OperatingSystem.IsWindows()) return new WindowsSound(withComputerAudio);
        throw new PlatformNotSupportedException("Recording works on a Mac or a Windows PC.");
    }

    /// <summary>Recording the computer's own sound (a lecture on Zoom) works on Windows; a Mac needs more for it.</summary>
    public static bool CanRecordComputerAudio => OperatingSystem.IsWindows();

    public static MicAccess Access()
    {
        if (OperatingSystem.IsMacOS()) return MacPermissions.Microphone();
        if (OperatingSystem.IsWindows()) return WindowsPermissions.Microphone();
        return MicAccess.Unknown;
    }

    /// <summary>Make the system ask the person, where it asks (a Mac). The answer comes later.</summary>
    public static void Ask()
    {
        if (OperatingSystem.IsMacOS()) MacPermissions.AskForMicrophone();
    }

    /// <summary>Where the person turns the microphone on for Study Stash.</summary>
    public static string SettingsUrl => OperatingSystem.IsWindows() ? WindowsPermissions.MicrophoneSettings
        : OperatingSystem.IsMacOS() ? MacPermissions.MicrophoneSettingsUrl : "";
}
