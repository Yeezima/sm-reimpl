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
    public static CustomMap Load(string path)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new MapException("too-big", $"The map file is larger than {MaxFileBytes / 1_000_000} MB.");
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new MapException("bad-file", $"Cannot read the map file '{path}': {e.Message}"); }
        return CustomMap.Compile(MapDocument.Parse(text));
    }

    /// <summary>True for a wire name that is only letters, digits and '_' after <c>cm_</c> (so it can never name a path).</summary>
    public static bool IsPlainWireName(string? name) =>
        CustomMap.IsCustomName(name) && name!.Length <= 24 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>
    /// The file in <see cref="SearchDirs"/> for a wire name that came from the server (<c>cm_arena</c>), or null. The name is never treated as a path; a file matches when its name (without
    /// <c>.smmap</c>) gives that wire name, or its <c>name</c> field does (read from the first 4 KB of the file, not by parsing it).
    /// </summary>
    public static string? Locate(string wireName)
    {
        if (!IsPlainWireName(wireName)) return null;
        var files = new List<string>();
        foreach (var dir in SearchDirs().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*.smmap").Order(StringComparer.OrdinalIgnoreCase)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var file in files)
            if (CustomMap.WireName(Path.GetFileNameWithoutExtension(file)) == wireName) return file;
        foreach (var file in files)
            if (WireNameOfFile(file) == wireName) return file;
        return null;
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
    public static CustomMap LoadForPlay(string wireName, string? explicitFile)
    {
        if (!IsPlainWireName(wireName)) throw new MapException("bad-map-name", "The server named a custom map in a form this client does not accept.");
        string? file = explicitFile is not null && File.Exists(explicitFile) ? explicitFile : Locate(wireName);
        if (file is null)
            throw new MapException("missing-map", $"This server is playing the custom map '{wireName}', and its .smmap file is not on this PC. " +
                $"Get the file from whoever made the map and put it in {new MapLibrary(null).MapsDir} (or start the client with --custom-map <file>).");
        var map = Load(file);
        if (map.Name != wireName)
            throw new MapException("wrong-map", $"The file {Path.GetFileName(file)} is the map '{map.Name}', but the server is playing '{wireName}'.");
        return map;
    }
}
