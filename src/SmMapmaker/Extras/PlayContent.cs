using System.Numerics;
using SmContent;
using SmFormats.Havok;

namespace SmMapmaker;

/// <summary>
/// Where a compile finds the content of SM1 placements (docs/mapmaker.md "SM1 pieces in play"): the meshes of the installed game's maps. The installed game in production
/// (<see cref="InstalledSm1Scenes"/>), the editor's already loaded maps in the Mapmaker, a synthetic one in tests.
/// </summary>
public interface ISm1Scenes
{
    /// <summary>
    /// The scene of <paramref name="map"/> with at least the meshes of <paramref name="definitions"/> loaded.
    /// </summary>
    /// <exception cref="MapException"><c>missing-install</c> (no install), <c>unknown-map</c> (the install has no such map), <c>bad-file</c>.</exception>
    MapScene Scene(string map, IReadOnlySet<string> definitions);

    /// <summary>The map's own collision triangles (the character layers merged), for prefabs whose collision is <c>original-cut</c>; throws like <see cref="Scene"/>.</summary>
    (Vector3[] Vertices, int[] Indices) OriginalCollision(string map);
}

/// <summary>One remembered <see cref="DecorResult"/> and the key it was built for.</summary>
public sealed class DecorCache
{
    readonly object _gate = new();
    string? _key;
    DecorResult? _result;

    public bool TryGet(string key, out DecorResult? result)
    {
        lock (_gate) { result = _key == key ? _result : null; return result is not null; }
    }

    public void Set(string key, DecorResult result) { lock (_gate) { _key = key; _result = result; } }

    public DecorResult GetOrBuild(string key, Func<DecorResult> build)
    {
        lock (_gate)
        {
            if (_key == key && _result is not null) return _result;
            var built = build();
            _key = key; _result = built;
            return built;
        }
    }

    /// <summary>The key of everything in <paramref name="doc"/> that decoration depends on: its objects that are not pieces (placements, groups, prefab instances with overrides) and the embedded prefabs.</summary>
    public static string KeyOf(MapDocument doc, string tag)
    {
        var sb = new System.Text.StringBuilder(tag).Append('\n');
        foreach (var o in doc.Objects) if (o.Piece is null) sb.Append(o.ToNode().ToJsonString()).Append('\n');
        foreach (var p in doc.EmbeddedPrefabs.OrderBy(p => p.Id, StringComparer.Ordinal)) sb.Append(p.ToNode().ToJsonString()).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

/// <summary>Reads maps from the user's install (read-only), each map once per process: scenes are cached by map and loaded again only when a later request needs more definitions.</summary>
public sealed class InstalledSm1Scenes : ISm1Scenes
{
    readonly string _gameDir;
    readonly bool _textures;
    readonly Dictionary<string, (MapScene Scene, HashSet<string> Definitions)> _scenes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (Vector3[], int[])> _collision = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="textures">True for the client (it draws the scenes it collided with); the server reads meshes only.</param>
    public InstalledSm1Scenes(string gameDir, bool textures) { _gameDir = gameDir; _textures = textures; }

    public string GameDir => _gameDir;

    /// <summary>Finds the install (explicit folder, SM_GAME_DIR, settings.json, Steam; never config/local.json); null when there is none.</summary>
    public static InstalledSm1Scenes? TryCreate(string? gameDir, bool textures) =>
        GameInstall.TryLocate(gameDir, repoRoot: null) is { } loc ? new InstalledSm1Scenes(loc.Dir, textures) : null;

    public MapScene Scene(string map, IReadOnlySet<string> definitions)
    {
        lock (_gate) return SceneLocked(map, definitions);
    }

    MapScene SceneLocked(string map, IReadOnlySet<string> definitions)
    {
        if (_scenes.TryGetValue(map, out var have) && definitions.All(have.Definitions.Contains)) return have.Scene;
        var wanted = new HashSet<string>(definitions, StringComparer.OrdinalIgnoreCase);
        if (have.Definitions is not null) wanted.UnionWith(have.Definitions);
        MapScene scene;
        try { scene = MapScene.Load(_gameDir, map, extendedLighting: false, onlyDefinitions: wanted, textures: _textures); }
        catch (ArgumentException) { throw new MapException("unknown-map", $"'{map}' is not the name of a Space Marine map."); }
        catch (DirectoryNotFoundException e) { throw new MapException("missing-install", e.Message); }
        catch (FileNotFoundException) { throw new MapException("unknown-map", $"The Space Marine install on this PC has no map '{map}'."); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new MapException("bad-file", $"Could not read the Space Marine map '{map}': {e.Message}");
        }
        _scenes[map] = (scene, wanted);
        return scene;
    }

    readonly object _gate = new();

    public (Vector3[] Vertices, int[] Indices) OriginalCollision(string map)
    {
        lock (_gate) return OriginalCollisionLocked(map);
    }

    (Vector3[] Vertices, int[] Indices) OriginalCollisionLocked(string map)
    {
        if (_collision.TryGetValue(map, out var have)) return have;
        MapContent content;
        try { content = MapContent.Load(_gameDir, map); }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new MapException("unknown-map", $"The collision of the Space Marine map '{map}' cannot be read on this PC: {e.Message}");
        }
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var name in MapContent.CharacterLayers)
        {
            var layer = content.Layer(name);
            if (layer is null) continue;
            int first = vertices.Count;
            vertices.AddRange(layer.Vertices);
            foreach (int i in layer.Indices) indices.Add(first + i);
        }
        return _collision[map] = ([.. vertices], [.. indices]);
    }
}

/// <summary>
/// What <see cref="CustomMap.Compile(MapDocument, CompileOptions?)"/> reads besides the document: the SM1 scenes for SM1 placements and the library for custom (glTF) assets and
/// prefabs a map does not carry. <see cref="Strict"/> (play): anything a placement needs that this PC lacks is a <see cref="MapException"/>, so a client or server never plays a different world;
/// lenient (the editor, tools): the placement is left out and a note says so.
/// </summary>
public sealed class CompileOptions
{
    public ISm1Scenes? Scenes { get; init; }
    public MapLibrary? Library { get; init; }
    public bool Strict { get; init; }
    /// <summary>The editor keeps the last decoration here, so dragging a piece or a spawn area does not unfold and triangulate every SM1 placement again (null: no caching).</summary>
    public DecorCache? Cache { get; init; }
    /// <summary>Part of the cache key that is not in the document: what the scene source holds right now (the editor's loaded maps).</summary>
    public string CacheTag { get; init; } = "";

    /// <summary>The way a server, a client or a tool plays or checks a map file: this PC's install and library, strict.</summary>
    /// <param name="gameDir">An explicit install folder (<c>--game-dir</c>), or null to look.</param>
    /// <param name="textures">The client reads textures too (it draws the same scenes).</param>
    public static CompileOptions ForPlay(string? gameDir = null, bool textures = false) =>
        new() { Scenes = InstalledSm1Scenes.TryCreate(gameDir, textures), Library = new MapLibrary(null), Strict = true };
}
