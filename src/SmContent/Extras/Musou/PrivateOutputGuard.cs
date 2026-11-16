using System;
using System.IO;

namespace SmContent.Extras.Musou;

/// <summary>
/// Where an authoring tool may write data derived from the install (<c>smtool spear-rig-dump</c>: the marine's skeleton): never inside a git checkout and never inside the install itself. The
/// check looks at the **output path's own** parents for a <c>.git</c> (not the tool's working directory), and compares whole path segments (a trailing separator), so a sibling folder that
/// merely starts with the same letters is not mistaken for the inside.
/// </summary>
public static class PrivateOutputGuard
{
    /// <summary>Null when <paramref name="outDir"/> is fine; else why it is refused.</summary>
    public static string? Refuses(string outDir, string? installDir)
    {
        string full = Path.GetFullPath(outDir);
        for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                return $"it is inside the git checkout at {d.FullName}";
        if (installDir is { Length: > 0 } && IsInside(full, Path.GetFullPath(installDir))) return "it is inside the game install";
        return null;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="dir"/> or lies under it (whole segments, any case).</summary>
    public static bool IsInside(string path, string dir)
    {
        static string Trim(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        return Trim(path).StartsWith(Trim(dir), StringComparison.OrdinalIgnoreCase);
    }
}
