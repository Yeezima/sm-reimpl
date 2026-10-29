namespace SmFormats;

/// <summary>
/// Path checks that keep every write out of the user's game installation (CLAUDE.md: the install is read-only).
/// </summary>
public static class InstallPaths
{
    /// True if either directory contains the other, or they are the same. A protective guard ("never write inside the install or the
    /// repository"), so it always ignores case: it fails closed even where a case-insensitive mount sits on Linux.
    public static bool Overlaps(string dirA, string dirB) => IsWithin(dirA, dirB) || IsWithin(dirB, dirA);

    /// True if <paramref name="path"/> equals <paramref name="dir"/> or lies below it.
    /// Separator-aware, so "cacheX" is not inside "cache". Always ordinal-ignore-case: this is the guard behind "the install is
    /// read-only", and treating "Data" and "data" as the same folder can only refuse a write, never allow one into the install.
    /// For a check that must not be too generous (a path that is allowed only inside a folder) use <see cref="IsWithinOsRule"/>.
    public static bool IsWithin(string path, string dir) => IsWithin(path, dir, StringComparison.OrdinalIgnoreCase);

    /// <summary><see cref="IsWithin(string, string, StringComparison)"/> with the running OS's rule (<see cref="PathComparison"/>).</summary>
    public static bool IsWithinOsRule(string path, string dir) => IsWithin(path, dir, PathComparison);

    /// <summary>The comparison the running OS uses for paths: ordinal-ignore-case on Windows and macOS, ordinal on Linux and others.</summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary><see cref="IsWithin(string, string)"/> with an explicit case rule (the tests exercise both on every OS).</summary>
    public static bool IsWithin(string path, string dir, StringComparison comparison)
    {
        string d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return p.Equals(d, comparison)
            || p.StartsWith(d + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// The game root for an archive path: the nearest ancestor holding the game executable or Steam app id file,
    /// else the outermost ancestor with a "data" folder, else the archive's own folder.
    /// </summary>
    public static string FindInstallRoot(string archivePath)
    {
        string start = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        string? withData = null;
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "SpaceMarine.exe")) || File.Exists(Path.Combine(d.FullName, "steam_appid.txt")))
                return d.FullName;
            if (Directory.Exists(Path.Combine(d.FullName, "data"))) withData = d.FullName;
        }
        return withData ?? start;
    }
}
