using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SmContent;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// The compiler's view of SM1 content inside the editor (docs/mapmaker.md "SM1 pieces in play"): the maps the editor already holds in memory, so the open map's check, bot route and play
/// test see the same solid SM1 pieces the game will. A map that is not loaded yet is not found (the compile is lenient in the editor: the pieces are left out and a note says so, and the
/// editor compiles again once the map is loaded). The original collision (for prefabs with <c>original-cut</c>) is read once from the install.
/// </summary>
public sealed class EditorScenes : ISm1Scenes
{
    static InstalledSm1Scenes? _installed;
    static bool _tried;
    readonly Dictionary<string, MapScene> _loaded;

    EditorScenes(Dictionary<string, MapScene> loaded) { _loaded = loaded; }

    /// <summary>A snapshot of the loaded maps (safe to use from another thread).</summary>
    public static EditorScenes Snapshot() =>
        new(MapHost.LoadedMaps.ToList().Select(m => (m, MapHost.Sm1For(m))).Where(x => x.Item2 is not null).ToDictionary(x => x.m, x => x.Item2!.Scene, StringComparer.OrdinalIgnoreCase));

    /// <summary>Changes when a map is loaded or dropped, so a cached compile is made again.</summary>
    public static string StateKey() => string.Join(",", MapHost.LoadedMaps.Order(StringComparer.OrdinalIgnoreCase));

    public static CompileOptions Options() => new() { Scenes = Snapshot(), Library = MapHost.Library, Strict = false };

    public MapScene Scene(string map, IReadOnlySet<string> definitions) =>
        _loaded.TryGetValue(map, out var scene) ? scene : throw new MapException("missing-install", $"the Space Marine map '{map}' is not loaded in the editor yet");

    public (Vector3[] Vertices, int[] Indices) OriginalCollision(string map)
    {
        if (!_tried) { _tried = true; _installed = InstalledSm1Scenes.TryCreate(null, textures: false); }
        return (_installed ?? throw new MapException("missing-install", "no Space Marine install was found")).OriginalCollision(map);
    }
}
