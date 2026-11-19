using System.Text.Json;
using System.Text.Json.Nodes;
using SmContent;

namespace SmMapmaker;

/// <summary>
/// What the library lists of one original map: the names of its definitions that draw something, and how many more draw nothing (markers, lights, groups).
/// Names only: no mesh, texture or placement data (so the cache file holds nothing of the original's bytes).
/// </summary>
public sealed record Sm1IndexEntry(string Map, string DisplayName, IReadOnlyList<string> Definitions, int Empty)
{
    public JsonObject ToNode(string stamp) => new()
    {
        ["version"] = Sm1Index.Version, ["stamp"] = stamp, ["map"] = Map, ["display"] = DisplayName, ["empty"] = Empty,
        ["definitions"] = new JsonArray(Definitions.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
    };
}

/// <summary>
/// The library's index of every Classic PvP map's definitions (owner 2026-11-19: "the SM1 definitions library must always be fully populated"). Each map's
/// definition names are read once from the install and kept as a small JSON file under the private cache (<c>%LOCALAPPDATA%\sm-reimpl\cache\mapmaker</c>),
/// valid while the map's archive is unchanged; the editor builds the missing ones one at a time in the background.
/// </summary>
public static class Sm1Index
{
    /// <summary>Bump when what is listed (which definitions count as drawn) changes.</summary>
    public const int Version = 1;

    public static string DefaultCacheDir => Path.Combine(GameInstall.DataRoot, "cache", "mapmaker", "sm1-definitions");

    /// <summary>The Classic PvP maps the install lists, in display-name order (empty without an install or when the catalog is missing).</summary>
    public static IReadOnlyList<MapEntry> Maps(string gameDir) =>
        [.. MapCatalog.Read(gameDir).OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Name, StringComparer.Ordinal)];

    /// <summary>Identifies the map's archives as they are now (sizes and write times), so a patched install renews the entry.</summary>
    public static string Stamp(string gameDir, string map)
    {
        string chunks = Path.Combine(gameDir, "data", "chunks");
        var parts = new List<string>();
        try
        {
            foreach (var f in Directory.GetFiles(chunks, map + "__*.oppc").Order(StringComparer.Ordinal))
            {
                var i = new FileInfo(f);
                parts.Add($"{i.Name}:{i.Length}:{i.LastWriteTimeUtc.Ticks}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return string.Join("|", parts);
    }

    static string FileOf(string cacheDir, string map) => Path.Combine(cacheDir, map + ".json");

    /// <summary>The cached entry when it exists, is of this version and has this stamp; else null (a corrupt file counts as missing).</summary>
    public static Sm1IndexEntry? TryRead(string cacheDir, string map, string stamp)
    {
        try
        {
            string path = FileOf(cacheDir, map);
            if (!File.Exists(path) || new FileInfo(path).Length > 8_000_000) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            if (o["version"]?.GetValue<int>() != Version || o["stamp"]?.GetValue<string>() != stamp || o["map"]?.GetValue<string>() != map) return null;
            var names = o["definitions"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            return new Sm1IndexEntry(map, o["display"]?.GetValue<string>() ?? map, names, o["empty"]?.GetValue<int>() ?? 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }

    /// <summary>Writes the entry (best effort: a cache that cannot be written only costs a rebuild next time).</summary>
    public static void Write(string cacheDir, Sm1IndexEntry entry, string stamp)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            string path = FileOf(cacheDir, entry.Map), tmp = path + ".tmp";
            File.WriteAllText(tmp, entry.ToNode(stamp).ToJsonString());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The names of the scene's definitions that draw something (the nearest-LOD parts), sorted; the rest are only counted.</summary>
    public static Sm1IndexEntry FromScene(MapScene scene, string displayName)
    {
        var drawn = new List<string>();
        int empty = 0;
        foreach (var d in scene.Definitions.Values)
            if (d.NearestLod().Any()) drawn.Add(d.Name); else empty++;
        drawn.Sort(StringComparer.OrdinalIgnoreCase);
        return new Sm1IndexEntry(scene.Map, displayName, drawn, empty);
    }

    /// <summary>The entry for <paramref name="map"/>: from the cache when valid, else read from the install (slow: seconds) and cached.</summary>
    public static Sm1IndexEntry Load(string gameDir, MapEntry map, string cacheDir)
    {
        string stamp = Stamp(gameDir, map.Name);
        if (TryRead(cacheDir, map.Name, stamp) is { } cached) return cached;
        var entry = FromScene(MapScene.Load(gameDir, map.Name), map.DisplayName);
        Write(cacheDir, entry, stamp);
        return entry;
    }
}
