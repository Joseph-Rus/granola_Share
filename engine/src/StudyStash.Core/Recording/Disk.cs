namespace StudyStash.Core;

/// <summary>The disk a recording goes to: whether it's full, and how much room is left on it.</summary>
public static class Disk
{
    const int NoSpace = 28; // ENOSPC, on macOS and Linux
    const int WindowsDiskFull = unchecked((int)0x80070070); // ERROR_DISK_FULL
    const int WindowsHandleDiskFull = unchecked((int)0x80070027); // ERROR_HANDLE_DISK_FULL

    /// <summary>The write failed because the disk has no room left (not a broken disk or a missing folder).</summary>
    public static bool IsFull(IOException e) => e.HResult is NoSpace or WindowsDiskFull or WindowsHandleDiskFull;

    /// <summary>Bytes free for this user on the disk holding <paramref name="path"/>; null when the system won't say
    /// (a network share, a disk that's gone).</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
