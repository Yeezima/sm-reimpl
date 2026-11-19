using System.Numerics;
using SmContent;

namespace SmMapmaker;

/// <summary>A placed object of an SM1 map layer (world transform as <see cref="MapScene"/> composes it).</summary>
public sealed record Sm1Object(string Name, string Definition, string Layer, Matrix4x4 Transform, int Depth);

/// <param name="Bounds">The nearest-LOD meshes' box in the definition's own frame, or null when it cannot be derived.</param>
/// <param name="Parts">Each nearest-LOD part's box in the definition's frame (null entries when unknown), in <c>ObjectDefinition.NearestLod()</c> order;
/// an <see cref="AssetRef.Part"/> indexes this list.</param>
/// <param name="PartNames">Each nearest-LOD part's node name in the same order (e.g. "CFactory_Gate"), or null.</param>
public sealed record Sm1Definition(string Name, int Visuals, Bounds? Bounds, IReadOnlyList<Bounds?>? Parts = null, IReadOnlyList<string>? PartNames = null);

/// <summary>What the Mapmaker needs of one SM1 map: its placed objects and definitions. No mesh, texture or material data.</summary>
public sealed class Sm1Map(string map, IReadOnlyList<Sm1Object> objects, IReadOnlyDictionary<string, Sm1Definition> definitions, IReadOnlyList<string> notes)
{
    public string Map { get; } = map;
    public IReadOnlyList<Sm1Object> Objects { get; } = objects;
    public IReadOnlyDictionary<string, Sm1Definition> Definitions { get; } = definitions;
    public IReadOnlyList<string> Notes { get; } = notes;
    /// <summary>Indices of placements the game does not draw either (armoury wall stations on Manufactorum); an import leaves them out. Null: none known.</summary>
    public IReadOnlySet<int>? Hidden { get; init; }
}

/// <summary>An original map's start areas and spawn-room blockers as the game's own reader finds them (the data the server spawns players from).</summary>
public sealed record Sm1Spawns(IReadOnlyList<MapStartArea> StartAreas, IReadOnlyList<MapBlocker> Blockers, IReadOnlyList<MapControlPoint>? ControlPoints = null);

/// <summary>Where SM1 map data comes from. The installed game in production; a synthetic fake in tests.</summary>
public interface ISm1Source
{
    /// <exception cref="MapException"><c>missing-install</c> (no install), <c>unknown-map</c> (the install has no such map), <c>bad-file</c>.</exception>
    Sm1Map LoadMap(string map);

    /// <summary>The map's start areas and spawn doors (what the dedicated server spawns players by), or null when the source cannot read them (an import then adds placeholders).</summary>
    Sm1Spawns? LoadSpawns(string map) => null;

    /// <summary>What Classic adds to the map besides drawn placements (invisible walls, kill boxes, movable cover), or null when the source cannot read it (an import then brings none; slice S3).</summary>
    Sm1Gameplay? LoadGameplay(string map) => null;
}

/// <summary>Reads maps from the user's install with <see cref="MapScene"/> (read-only; results are cached in memory per instance).</summary>
public sealed class InstalledSm1Source(string gameDir) : ISm1Source
{
    readonly Dictionary<string, Sm1Map> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string GameDir { get; } = gameDir;

    /// <summary>Finds the install (explicit folder, SM_GAME_DIR, settings.json, Steam); never reads config/local.json. Null when none is found.</summary>
    public static InstalledSm1Source? TryCreate(string? explicitDir = null) =>
        GameInstall.TryLocate(explicitDir, repoRoot: null) is { } loc ? new InstalledSm1Source(loc.Dir) : null;

    readonly Dictionary<string, MapContent?> _contents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game server's own reading of the map (<see cref="MapContent"/>, which also parses collision: seconds), once per map; null for a map that loader cannot read (co-op arenas).</summary>
    MapContent? Content(string map)
    {
        if (_contents.TryGetValue(map, out var cached)) return cached;
        MapContent? result = null;
        try { result = MapContent.Load(GameDir, map); }
        catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException) { }
        return _contents[map] = result;
    }

    public Sm1Spawns? LoadSpawns(string map) => Content(map) is { } c ? new Sm1Spawns(c.StartAreas, c.Blockers, c.ControlPoints) : null;

    public Sm1Gameplay? LoadGameplay(string map) => Content(map) is { } c ? new Sm1Gameplay(c.Walls, c.KillRegions, c.Props) : null;

    public Sm1Map LoadMap(string map)
    {
        if (_cache.TryGetValue(map, out var cached)) return cached;
        if (!Directory.Exists(System.IO.Path.Combine(GameDir, "data"))) throw new MapException("missing-install", $"No Space Marine install at '{GameDir}'.");
        MapScene scene;
        try { scene = MapScene.Load(GameDir, map); }
        catch (ArgumentException e) { throw new MapException("unknown-map", e.Message); }
        catch (DirectoryNotFoundException e) { throw new MapException("missing-install", e.Message); }
        catch (FileNotFoundException) { throw new MapException("unknown-map", $"The install has no map '{map}'."); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new MapException("bad-file", $"Could not read map '{map}': {e.Message}");
        }

        var definitions = new Dictionary<string, Sm1Definition>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in scene.Definitions.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
            definitions[d.Name] = new Sm1Definition(d.Name, d.Visuals.Count, BoundsOf(scene, d), PartBounds(scene, d), d.NearestLod().Select(v => v.RefNode).ToList());
        var objects = scene.Objects.Select(o => new Sm1Object(o.Name, o.Definition, o.Layer, o.Transform, o.Depth)).ToList();
        return _cache[map] = new Sm1Map(map, objects, definitions, scene.Notes) { Hidden = HiddenPlacements(map, objects) };   // the same placements Classic hides (shared SmContent.MapDecorLists)
    }

    /// <summary>The placements Classic does not draw on <paramref name="map"/> (indices into <paramref name="objects"/>), from the lists the client uses (<see cref="MapDecorLists"/>); <paramref name="old"/> is the client's <c>--old-armoury-racks</c> restore switch (off by default).</summary>
    public static IReadOnlySet<int> HiddenPlacements(string map, IReadOnlyList<Sm1Object> objects, bool old = false) =>
        MapDecorLists.Hidden(map, old, objects.Select(o => (o.Definition, o.Transform.Translation.X, o.Transform.Translation.Y)).ToList());

    /// <summary>Each nearest-LOD part's box in the definition's frame (null when its mesh block is missing).</summary>
    public static IReadOnlyList<Bounds?> PartBounds(MapScene scene, ObjectDefinition d) =>
        d.NearestLod().Select(part => scene.Meshes.TryGetValue(part.MeshName, out var mesh) && part.MeshId >= 0 && part.MeshId < mesh.Blocks.Count
            ? (Bounds?)new Bounds(mesh.Blocks[part.MeshId].BoundsMin, mesh.Blocks[part.MeshId].BoundsMax).Transformed(part.NodeTransform) : null).ToList();

    static Bounds? BoundsOf(MapScene scene, ObjectDefinition d)
    {
        Bounds? result = null;
        foreach (var part in d.NearestLod())
        {
            if (!scene.Meshes.TryGetValue(part.MeshName, out var mesh) || part.MeshId < 0 || part.MeshId >= mesh.Blocks.Count) continue;
            var block = mesh.Blocks[part.MeshId];
            var b = new Bounds(block.BoundsMin, block.BoundsMax).Transformed(part.NodeTransform);
            result = result is { } r ? r.Union(b) : b;
        }
        return result;
    }
}
