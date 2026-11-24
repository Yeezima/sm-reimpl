using System.Text.RegularExpressions;
using SmContent;
using SmFormats.Obpk;

namespace SmTool;

/// <summary>
/// <c>smtool find-entry &lt;game-dir|auto&gt; &lt;regex&gt;... [--under &lt;data/chunks&gt;]... [--names-only]</c>: which archives of the install hold an entry
/// whose file name matches any of the patterns (case-insensitive). Reads only each archive's table of contents (read-only, nothing extracted), so a sweep
/// of all 2,736 archives answers "where is this mesh, rig or blueprint" in one run (chaos-inventory, 2026-11-25; before it, a throwaway reader outside the
/// repository did this). Default folders: <c>data/chunks</c> and <c>data/packages</c>. Output: one line per hit, <c>archive  size  entry path</c>, the archive
/// relative to the game folder; <c>--names-only</c> prints each distinct matching file name once with the archives that hold it.
/// </summary>
public static class FindEntryCommand
{
    public sealed record Hit(string Archive, string EntryPath, string FileName, long Size);

    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        var patterns = new List<Regex>();
        var unders = new List<string>();
        bool namesOnly = false;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--under" && i + 1 < rest.Length) unders.Add(rest[++i]);
            else if (rest[i] == "--names-only") namesOnly = true;
            else patterns.Add(new Regex(rest[i], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        if (patterns.Count == 0)
        {
            Console.Error.WriteLine("find-entry <game-dir|auto> <regex>... [--under <folder>]... [--names-only]");
            return 2;
        }
        if (unders.Count == 0) unders.AddRange(["data/chunks", "data/packages"]);
        var archives = unders.Select(u => Path.Combine(gameDir, u)).Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.oppc", SearchOption.AllDirectories)).Order(StringComparer.OrdinalIgnoreCase);
        var hits = Search(archives, patterns, gameDir, msg => Console.Error.WriteLine(msg)).ToList();
        if (namesOnly)
            foreach (var g in hits.GroupBy(h => h.FileName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"{g.Key}  [{string.Join(", ", g.Select(h => Path.GetFileNameWithoutExtension(h.Archive)).Distinct().Take(8))}{(g.Select(h => h.Archive).Distinct().Count() > 8 ? ", ..." : "")}]");
        else
            foreach (var h in hits) Console.WriteLine($"{h.Archive}  {h.Size,10}  {h.EntryPath}");
        Console.Error.WriteLine($"{hits.Count} hits");
        return 0;
    }

    /// <summary>Every entry of the archives whose file name matches a pattern. <paramref name="relativeTo"/> shortens the archive path; an archive that fails to open is reported and skipped.</summary>
    public static IEnumerable<Hit> Search(IEnumerable<string> archivePaths, IReadOnlyList<Regex> patterns, string? relativeTo = null, Action<string>? warn = null)
    {
        foreach (var path in archivePaths)
        {
            List<Hit> found = [];
            try
            {
                using var a = ObpkArchive.Open(path);
                string label = relativeTo is null ? Path.GetFileName(path) : Path.GetRelativePath(relativeTo, path).Replace('\\', '/');
                foreach (var e in a.Entries)
                    if (patterns.Any(p => p.IsMatch(e.FileName)))
                        found.Add(new Hit(label, e.Path(a.GroupName(e)), e.FileName, e.Size));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
            {
                warn?.Invoke($"{Path.GetFileName(path)}: {ex.Message}");
            }
            foreach (var h in found) yield return h;
        }
    }
}
