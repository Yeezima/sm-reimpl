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

    public (Vector3[] Vertices, int[] Indices) OriginalCollision(string map)
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

    /// <summary>The way a server, a client or a tool plays or checks a map file: this PC's install and library, strict.</summary>
    /// <param name="gameDir">An explicit install folder (<c>--game-dir</c>), or null to look.</param>
    /// <param name="textures">The client reads textures too (it draws the same scenes).</param>
    public static CompileOptions ForPlay(string? gameDir = null, bool textures = false) =>
        new() { Scenes = InstalledSm1Scenes.TryCreate(gameDir, textures), Library = new MapLibrary(null), Strict = true };
}
