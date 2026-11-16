using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// Finding and loading custom maps for play (docs/mapmaker.md "Playing a map"). The server is given a file (<c>--custom-map</c>); a client is told only
/// the map's wire name (<c>cm_...</c>) and looks for the file by that name in the folders below, or is given it (<c>--custom-map</c>). Either way both
/// compile the same document, and the handshake's map hash (<see cref="CustomMap.Hash"/>) tells them when their copies differ.
/// </summary>
public static class CustomMaps
{
    /// <summary>The folders searched for <c>*.smmap</c>, in order: <c>SM_MAPS_DIR</c>, the Mapmaker library's <c>maps</c> folder, <c>maps</c> beside the program, <c>maps</c> in the working folder.</summary>
    public static IEnumerable<string> SearchDirs()
    {
        if (Environment.GetEnvironmentVariable("SM_MAPS_DIR") is { Length: > 0 } env) yield return env;
        yield return new MapLibrary(null).MapsDir;
        yield return Path.Combine(AppContext.BaseDirectory, "maps");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "maps");
    }

    /// <summary>Compiles the map file at <paramref name="path"/>; a plain <see cref="MapException"/> (code <c>bad-file</c>) when it is not a readable <c>.smmap</c>.</summary>
    public static CustomMap Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new MapException("bad-file", $"Cannot read the map file '{path}': {e.Message}"); }
        return CustomMap.Compile(MapDocument.Parse(text));
    }

    /// <summary>The file for <paramref name="nameOrPath"/>: an existing file, or the file in <see cref="SearchDirs"/> whose map has that wire name (<c>cm_arena</c>); null when there is none.</summary>
    public static string? Locate(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        string wire = CustomMap.IsCustomName(nameOrPath) ? nameOrPath : CustomMap.WireName(nameOrPath);
        foreach (var dir in SearchDirs().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.smmap").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (WireNameOfFile(file) == wire) return file;
            }
        }
        return null;
    }

    /// <summary>The wire name of the map in <paramref name="file"/> without compiling it, or null when it cannot be read.</summary>
    public static string? WireNameOfFile(string file)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject o && o["name"]?.GetValue<string>() is { } name) return CustomMap.WireName(name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        return null;
    }

    /// <summary>Loads the map a client was told to play: its file found by <paramref name="wireName"/> (or <paramref name="explicitFile"/>), compiled, with a message that says what to do when it is missing.</summary>
    public static CustomMap LoadForPlay(string wireName, string? explicitFile)
    {
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
