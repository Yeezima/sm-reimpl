using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmMapmaker;

/// <summary>
/// Finding and loading custom maps for play (docs/mapmaker.md "Playing a map"). The server is given a file (<c>--custom-map</c>, a path the operator typed); a client is told only
/// the map's wire name (<c>cm_...</c>) by the server and looks for the file by that name in the folders below, or is given it (<c>--custom-map</c>, again typed by its user). Either way
/// both compile the same document, and the handshake's map hash (<see cref="CustomMap.Hash"/>) tells them when their copies differ.
/// A name that comes from the network is never used as a path: it must be a plain <c>cm_</c> name (letters, digits, '_'), and only the search folders are looked in.
/// </summary>
public static class CustomMaps
{
    /// <summary>Largest <c>.smmap</c> file read at all (a shared file is somebody else's input).</summary>
    public const long MaxFileBytes = 8_000_000;

    /// <summary>The folders searched for <c>*.smmap</c>, in order: <c>SM_MAPS_DIR</c>, the Mapmaker library's <c>maps</c> folder, <c>maps</c> beside the program, <c>maps</c> in the working folder.</summary>
    public static IEnumerable<string> SearchDirs()
    {
        if (Environment.GetEnvironmentVariable("SM_MAPS_DIR") is { Length: > 0 } env) yield return env;
        yield return new MapLibrary(null).MapsDir;
        yield return Path.Combine(AppContext.BaseDirectory, "maps");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "maps");
    }

    /// <summary>Compiles the map file at <paramref name="path"/> (a path its user chose); a plain <see cref="MapException"/> (<c>bad-file</c>, <c>too-big</c>) when it is not a readable <c>.smmap</c>.</summary>
    public static CustomMap Load(string path, CompileOptions? options = null)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new MapException("too-big", $"The map file is larger than {MaxFileBytes / 1_000_000} MB.");
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new MapException("bad-file", $"Cannot read the map file '{path}': {e.Message}"); }
        // Play and tools: SM1 placements, prefabs and imported models come from this PC's install and library, and a PC that lacks one refuses (docs/mapmaker.md "SM1 pieces in play").
        var doc = MapDocument.Parse(text);
        return CustomMap.Compile(doc, options ?? (NeedsContent(doc) ? CompileOptions.ForPlay() : new CompileOptions { Strict = true }));
    }

    /// <summary>Whether the document has any object that places an SM1 definition, a prefab or an imported model (a graybox-only map needs no install and no library).</summary>
    public static bool NeedsContent(MapDocument doc) => doc.Objects.Any(o => o.Asset is not null || o.Prefab is not null);

    /// <summary>True for a wire name that is only letters, digits and '_' after <c>cm_</c> (so it can never name a path).</summary>
    public static bool IsPlainWireName(string? name) =>
        CustomMap.IsCustomName(name) && name!.Length <= 24 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// The file in <see cref="SearchDirs"/> for a wire name that came from the server (<c>cm_arena</c>), or null. The name is never treated as a path; a file matches when its name (without
    /// <c>.smmap</c>) gives that wire name, or its <c>name</c> field does (read from the first 4 KB of the file, not by parsing it).
    /// </summary>
    public static string? Locate(string wireName) => LocateAll(wireName).FirstOrDefault();

    /// <summary>
    /// Every file in <see cref="SearchDirs"/> that holds the map of this wire name, the most recently saved first. Many maps are left at the default name "My Map", so several files
    /// often share one wire name: a client that is not given its file (<c>--custom-map</c>) can only guess, and the guess is the newest save (the one just being tested), never
    /// the alphabetically first (which was an old copy: owner's 2026-11-19 report that the play test showed another map).
    /// </summary>
    public static IReadOnlyList<string> LocateAll(string wireName)
    {
        if (!IsPlainWireName(wireName)) return [];
        var files = new List<string>();
        foreach (var dir in SearchDirs().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*.smmap")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // The wire name is made from the map's name field, not its file name: a file called my-map-2.smmap can hold the map "My Map" (cm_my_map) while an older my-map.smmap holds another
        // copy of it. So the name field decides first; the file name is only a fallback for a file whose name field cannot be read.
        var byName = files.Where(f => WireNameOfFile(f) == wireName).ToList();
        if (byName.Count == 0) byName = files.Where(f => WireNameOfFile(f) is null && CustomMap.WireName(Path.GetFileNameWithoutExtension(f)) == wireName).ToList();
        return byName.OrderByDescending(Modified).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static DateTime Modified(string file) { try { return File.GetLastWriteTimeUtc(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; } }

    /// <summary>The other files (any folder searched, plus the folder of <paramref name="file"/>) whose map has the same wire name: a play test of one of them can be mistaken for another. Empty when the name is unique.</summary>
    public static IReadOnlyList<string> SameNameAs(string file)
    {
        string? wire = WireNameOfFile(file);
        if (wire is null) return [];
        string full = Path.GetFullPath(file);
        var dirs = SearchDirs().Append(Path.GetDirectoryName(full) ?? "").Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.smmap"))
                    if (!string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase) && WireNameOfFile(f) == wire && !found.Contains(f, StringComparer.OrdinalIgnoreCase)) found.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    static readonly Regex NameField = new("\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.){0,400})\"", RegexOptions.Compiled);

    /// <summary>The wire name of the map in <paramref name="file"/> from its <c>name</c> field in the first 4 KB, without parsing the file; null when it cannot be read.</summary>
    public static string? WireNameOfFile(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            var buffer = new byte[4096];
            int n = s.Read(buffer, 0, buffer.Length);
            var m = NameField.Match(System.Text.Encoding.UTF8.GetString(buffer, 0, n));
            if (!m.Success) return null;
            return CustomMap.WireName(JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\"") ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Loads the map a client was told to play: <paramref name="explicitFile"/> (typed by the user with <c>--custom-map</c>) when it exists, else the file for <paramref name="wireName"/> (from the
    /// server) found in the search folders, with a message that says what to do when it is missing.
    /// </summary>
    public static CustomMap LoadForPlay(string wireName, string? explicitFile, CompileOptions? options = null)
    {
        if (!IsPlainWireName(wireName)) throw new MapException("bad-map-name", "The server named a custom map in a form this client does not accept.");
        // A file the user named is the file, or the client says so: never fall back to looking a map up by name (that is how another map with the same name was played instead).
        if (!string.IsNullOrEmpty(explicitFile) && !File.Exists(explicitFile))
            throw new MapException("missing-map", $"The map file given with --custom-map ('{explicitFile}') does not exist, so the custom map '{wireName}' cannot be played.");
        string? file = !string.IsNullOrEmpty(explicitFile) ? explicitFile : Locate(wireName);
        if (file is null)
            throw new MapException("missing-map", $"This server is playing the custom map '{wireName}', and its .smmap file is not on this PC. " +
                $"Get the file from whoever made the map and put it in {new MapLibrary(null).MapsDir} (or start the client with --custom-map <file>).");
        var map = Load(file, options);
        if (map.Name != wireName)
            throw new MapException("wrong-map", $"The file {Path.GetFileName(file)} is the map '{map.Name}', but the server is playing '{wireName}'.");
        return map;
    }
}
