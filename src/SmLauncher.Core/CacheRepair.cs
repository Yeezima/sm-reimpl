namespace SmLauncher.Core;

/// <summary>
/// "Repair: clear caches": which folder that may delete. Only &lt;data root&gt;\cache, the data root found exactly as the game finds it
/// (SmContent.GameInstall.DataRootFor: SM_DATA_DIR when it is not blank, trimmed and made full, else %LOCALAPPDATA%\sm-reimpl), and never when that
/// root is a drive root or the user profile (or above it), nor when cache is a link (its target is not ours to empty).
/// </summary>
public static class CacheRepair
{
    public const string DataDirEnvVar = "SM_DATA_DIR";

    public static string DataRoot(string? smDataDir, string localAppData) =>
        !string.IsNullOrWhiteSpace(smDataDir) ? Path.GetFullPath(smDataDir.Trim()) : Path.Combine(localAppData, "sm-reimpl");

    /// <summary>The cache folder to clear (it may not exist), or why it must not be cleared.</summary>
    public static (string? Cache, string? Refusal) Target(string? smDataDir, string localAppData, string userProfile)
    {
        string root;
        try { root = Path.TrimEndingDirectorySeparator(DataRoot(smDataDir, localAppData)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return (null, $"{DataDirEnvVar} is not a usable folder path."); }
        string? driveRoot = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(driveRoot) || string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), root, StringComparison.OrdinalIgnoreCase))
            return (null, $"The data folder {root} is a drive root; the launcher does not clear anything there.");
        string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile));
        if (string.Equals(root, profile, StringComparison.OrdinalIgnoreCase) || Updater.IsInsideFolder(profile, root))
            return (null, $"The data folder {root} is your user folder (or holds it); the launcher does not clear anything there.");
        string cache = Path.Combine(root, "cache");
        if (Directory.Exists(cache) && (File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
            return (null, $"{cache} is a link to another folder; the launcher does not follow it. Remove it yourself if you want.");
        return (cache, null);
    }
}
