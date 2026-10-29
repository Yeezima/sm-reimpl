using System.Diagnostics;

namespace SmLauncher.Core;

/// <summary>
/// Which launcher wins. A launcher is only ever replaced by a strictly newer one (file version, set by release.ps1 from the build commit's time), compared
/// with the launcher that is there now, never just with the one running: a launcher started from an old unpacked zip must not downgrade the installed one.
/// </summary>
public static class LauncherVersions
{
    public static Version Normalise(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>
    /// Put <paramref name="candidate"/> in place of the launcher at the target? Yes when there is none, when its version cannot be read (a broken file),
    /// or when the candidate is newer; never when the candidate's own version is unknown.
    /// </summary>
    public static bool ShouldReplace(Version? candidate, Version? installed, bool installedExists) =>
        candidate is not null && (!installedExists || installed is null || Normalise(candidate) > Normalise(installed));

    /// <summary>The file version of an exe (read from its resources; nothing is run), or null.</summary>
    public static Version? Of(string exe)
    {
        try
        {
            if (!File.Exists(exe)) return null;
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0 && info.FilePrivatePart == 0) return null;
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The launcher a verified version folder carries ("0 - SM Reimpl Launcher.exe"), only when its file list names it and the file matches that hash, and
    /// only when it is newer than <paramref name="than"/>. Null otherwise.
    /// </summary>
    public static string? NewerBundled(string? versionDir, Version than)
    {
        if (versionDir is null) return null;
        string path = Path.Combine(versionDir, PackageLayout.BundledLauncher);
        if (!File.Exists(path)) return null;
        var theirs = Of(path);
        if (theirs is null || Normalise(theirs) <= Normalise(than)) return null;
        return Manifest.FileMatches(versionDir, PackageLayout.BundledLauncher) ? path : null;
    }

    /// <summary>
    /// Puts a copy of <paramref name="source"/> at <paramref name="target"/>: copied to &lt;target&gt;.new (a stale one is replaced) and checked, then swapped in
    /// with File.Replace (ReplaceFile: the old file becomes &lt;target&gt;.old in the same step; a running exe may be renamed on Windows), or with two renames
    /// when ReplaceFile refuses. Returns the backup's path (null when there was no launcher there). The caller keeps the backup until the new launcher has
    /// shown it works (<see cref="Restore"/> otherwise). Never meant for the running launcher (see SelfUpdateHelper). When it throws, the target is the
    /// launcher that was there before (its own fallback is rolled back) and the temporary copy is gone.
    /// </summary>
    public static string? Place(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        string tmp = target + ".new";
        if (File.Exists(tmp)) File.Delete(tmp);
        File.Copy(source, tmp);
        try
        {
            if (!string.Equals(Hashes.Sha256File(tmp), Hashes.Sha256File(source), StringComparison.Ordinal)) throw new IOException("The copied launcher differs from its source.");
            if (!File.Exists(target)) { File.Move(tmp, target); return null; }
            string backup = target + ".old";
            if (File.Exists(backup)) { try { File.Delete(backup); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { backup = target + "." + Guid.NewGuid().ToString("N")[..6] + ".old"; } }
            try { File.Replace(tmp, target, backup, ignoreMetadataErrors: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!File.Exists(target))
                {
                    // ReplaceFile got as far as the backup: finish it, or put the backup back
                    try { File.Move(tmp, target); }
                    catch (Exception) { if (File.Exists(backup)) File.Move(backup, target); throw; }
                    return File.Exists(backup) ? backup : null;
                }
                File.Move(target, backup);
                try { File.Move(tmp, target); }
                catch (Exception) { File.Move(backup, target); throw; }                                          // roll our own fallback back
            }
            return backup;
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
    }

    /// <summary>
    /// The backup a new launcher was told to remove (--self-updated): accepted only when it lies beside <paramref name="exe"/> and its name is
    /// "&lt;exe file name&gt;*.old", so a command line can never make the launcher delete another file.
    /// </summary>
    public static bool IsOwnBackup(string exe, string? backup)
    {
        if (string.IsNullOrEmpty(backup)) return false;
        try
        {
            string full = Path.GetFullPath(backup);
            return string.Equals(Path.GetDirectoryName(full), Path.GetDirectoryName(Path.GetFullPath(exe)), StringComparison.OrdinalIgnoreCase) &&
                   System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(Path.GetFileName(exe) + "*.old", Path.GetFileName(full), ignoreCase: true);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// Removes what a self-update leaves beside the launcher: &lt;exe&gt;*.old and &lt;exe&gt;.new, and with <paramref name="markers"/> also the hand-over
    /// signs &lt;exe&gt;.ok and &lt;exe&gt;.crashed (not while a helper may still be reading them). True when nothing is left.
    /// </summary>
    public static bool CleanOld(string exe, bool markers = true)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        if (!Directory.Exists(dir)) return true;
        bool clean = true;
        var files = Directory.EnumerateFiles(dir, Path.GetFileName(exe) + "*.old").Append(exe + ".new");
        if (markers) files = files.Concat([SelfUpdateHelper.OkMarker(exe), SelfUpdateHelper.CrashMarker(exe)]);
        foreach (var f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(f)) continue;
            try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { clean = false; }
        }
        return clean;
    }

    /// <summary>Puts the backup back over a new launcher that did not start.</summary>
    public static void Restore(string backup, string target) => File.Move(backup, target, overwrite: true);
}
