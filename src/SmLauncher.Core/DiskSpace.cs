namespace SmLauncher.Core;

/// <summary>The disk is full, or would be: the update stops at once (no retry, nothing blamed on the network) and the previous version stays.</summary>
public sealed class DiskFullException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Free-space checks before a download or an unpack, and recognising a full disk in an I/O error.</summary>
public static class DiskSpace
{
    /// <summary>Kept free on top of what an update needs (logs, the pointer files, Windows itself).</summary>
    public const long Margin = 64L << 20;

    /// <summary>Free bytes on the drive of a path (DriveInfo.AvailableFreeSpace, the quota of this user); unknown = no limit.</summary>
    public static long DefaultFreeBytes(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return long.MaxValue;
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return long.MaxValue; }   // a share or an odd root: the write itself will tell
    }

    /// <summary>Throws <see cref="DiskFullException"/> when the drive of <paramref name="path"/> has less than <paramref name="bytes"/> plus the margin free.</summary>
    public static void Require(string path, long bytes, string what, Func<string, long>? freeBytes = null)
    {
        if (bytes <= 0) return;
        long free = (freeBytes ?? DefaultFreeBytes)(path);
        if (free < bytes + Margin)
            throw new DiskFullException($"Not enough disk space to {what}: it needs {Mb(bytes + Margin)} on {Path.GetPathRoot(Path.GetFullPath(path))} and {Mb(Math.Max(0, free))} are free. Free some space, then try again.");
    }

    /// <summary>ERROR_DISK_FULL (112) or ERROR_HANDLE_DISK_FULL (39) in an I/O error.</summary>
    public static bool IsDiskFull(Exception e) => e is DiskFullException || (e is IOException && (e.HResult & 0xFFFF) is 112 or 39);

    public static DiskFullException Wrap(Exception e, string what) =>
        e as DiskFullException ?? new DiskFullException($"The disk is full, so the launcher could not {what}. Free some space, then try again; the previous version is unchanged.", e);

    static string Mb(long b) => $"{b / 1048576.0:0} MB";
}
