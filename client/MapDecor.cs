using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// fix/manufactorum-fluorescent (owner's request 2026-11-04; **deliberate change from the original**, docs/changes-from-original.md):
/// the placed objects of Manufactorum (<c>mp_tdm_09</c>) that draw as flat olive boards with salmon-red glowing shapes on them are
/// not drawn. The same drawing serves classic PvP and Musou Mode, which fights on this map.
/// <para>
/// Evidence (in-engine captures, 2026-11-04): the six wall stations at the two bases (each a row of wall boards, hung with a helmet,
/// tools, weapon silhouettes and a banner, and a pair of tall keypads, plus two small door-side stations) are built from objects whose
/// boards use the <c>backPanel</c> material (an emissive map tinted olive, EmissiveColor 0.27/0.29/0.10) and whose fittings use
/// <c>tech_stations</c> (emissive map tinted salmon, EmissiveColor 0.55/0.14/0.12) or the <c>green</c> keypad material; they render flat,
/// bright and unlit against the dark metal around them. Whether the original showed them this bright is not known (our shader
/// approximation of emissive maps, <see cref="MapMaterials"/>), so the objects are removed rather than retuned.
/// </para>
/// <para>
/// The list is by object definition name. <see cref="Always"/> names definitions used only by these stations (or, backpanel_c, as the
/// same boards elsewhere). <see cref="Near"/> names definitions that also dress other things (the console stations' cabling and
/// domes): they are removed only where they sit within <see cref="MapDecorLists.NearRadius"/> metres (horizontally) of a board or a <c>tech_interface_b</c> cabinet, so the monitor
/// consoles keep their cabling. The console stations themselves (<c>tech_interface_*</c>, the monitors) stay; their <c>tech_panel_pipe_e</c> posts are removed (owner, 2026-11-07). <c>--old-armoury-racks</c>
/// draws everything as before.
/// </para>
/// </summary>
public static class MapDecor
{
    public const string Map = MapDecorLists.Map;
    // The lists (engine-free, so the tests can read them) are in MapDecorLists/MapDecorLists.cs.
    public static string[] Always => MapDecorLists.Always;
    public static string[] Near => MapDecorLists.Near;
    public static (string Mesh, string Material)[] Surfaces => MapDecorLists.Surfaces;
    /// <summary>Whether the material range <paramref name="material"/> of <paramref name="mesh"/> is not drawn on <paramref name="map"/>.</summary>
    public static bool HidesSurface(string map, string mesh, string material) => MapDecorLists.HidesSurface(map, mesh, material, Old);

    /// <summary><c>--old-armoury-racks</c>: draw the boards and their fittings as before (before / after aid).</summary>
    /// <summary>Whether the engine's sky-box surfaces (shader <c>sky_*</c>) are not drawn on <paramref name="map"/>: every map but Manufactorum, and never with <c>--old-sky-boxes</c>.</summary>
    public static bool SkipsSkyBoxes(string map) => MapDecorLists.SkipsSkyBoxes(map, OldSkyBoxes);

    /// <summary>Whether the sky boxes are drawn as a decoded sky around the camera (<see cref="MapSky"/>) rather than not at all: the maps that skip them, unless <c>--no-sky-textures</c>.</summary>
    public static bool DrawsSkyTextures(string map) => MapDecorLists.DrawsSkyTextures(map, OldSkyBoxes, NoSkyTextures, RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility");
    /// <summary><c>--no-sky-textures</c>: the sky boxes are not drawn, as after classic-maps-all (before / after aid).</summary>
    public static bool NoSkyTextures => _noSkyTex ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-sky-textures") >= 0;
    static bool? _noSkyTex;

    /// <summary><c>--old-uv-sets</c>: the diffuv1 shaders' base maps through the first UV set again (before / after aid).</summary>
    public static bool OldUvSets => _oldUv ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-uv-sets") >= 0;
    static bool? _oldUv;

    /// <summary>Whether <paramref name="map"/> gets the dark floor plane under the level (<see cref="MapDecorLists.VoidFloorMaps"/>); <c>--no-void-floor</c> turns it off (before / after aid).</summary>
    public static bool HasVoidFloor(string map) => MapDecorLists.HasVoidFloor(map, Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-void-floor") >= 0);

    /// <summary><c>--old-sky-boxes</c>: draw the sky boxes as map geometry again, as before classic-maps-all (before / after aid).</summary>
    public static bool OldSkyBoxes => _oldSky ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-sky-boxes") >= 0;
    static bool? _oldSky;

    /// <summary>
    /// classic-maps-fidelity fix 1: whether the map's static level decals (<see cref="MapDecalView"/>) are drawn: on every map (Manufactorum too, owner 2026-11-18: "Yes, draw them") unless <c>--no-level-decals</c> (before / after aid; <c>--level-decals</c> is a no-op now).
    /// </summary>
    public static bool DrawsLevelDecals(string map) => Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-level-decals") < 0;

    /// <summary>classic-maps-fidelity round 2: whether the map's <c>base/OrthoLight</c> projectors (<see cref="MapOrthoLightView"/>) are drawn: on the ten new classic maps unless <c>--no-ortho-lights</c> (before / after aid); on Manufactorum only with <c>--ortho-lights</c> (its approved look stays).</summary>
    public static bool DrawsOrthoLights(string map) => MapDecorLists.IsNewClassicMap(map) ? Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-ortho-lights") < 0 : Array.IndexOf(OS.GetCmdlineUserArgs(), "--ortho-lights") >= 0;

    /// <summary>classic-maps-fidelity round 2, fix 3: whether the map's moving parts (fan blades turned by their BezierCurve, scrolling materials) are drawn and moved; <c>--no-moving-parts</c> is the before aid (the blades are then not drawn at all, as before).</summary>
    public static bool MovingParts => _moving ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-moving-parts") < 0;
    static bool? _moving;

    /// <summary>classic-maps-fidelity: <c>--old-lod-rule</c> draws the parts of <see cref="ObjectDefinition.NearestLod"/> again (every node named like a low LOD dropped, _LodH and _LodM drawn together); by default one member of each LOD-suffix family is drawn (<see cref="ObjectDefinition.HighestLod"/>).</summary>
    public static bool OldLodRule => _oldLod ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-lod-rule") >= 0;
    static bool? _oldLod;

    /// <summary>classic-maps-fidelity: <c>--old-map-lighting</c> reads only the layer <c>l_lights</c> and lowercase light classes, as before (every <c>MapScene.Load</c> of the client passes this).</summary>
    public static bool OldMapLighting => _oldLight ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-map-lighting") >= 0;
    static bool? _oldLight;

    /// <summary>classic-maps-fidelity pass 2, test aid only: <c>--hide-layer a,b</c> does not draw the placements of those layers (default none).</summary>
    public static HashSet<string> HiddenLayers => _hideLayers ??= ParseLayers();
    static HashSet<string>? _hideLayers;
    static HashSet<string> ParseLayers()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--hide-layer");
        return i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    }

    public static bool Old => _old ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-armoury-racks") >= 0;
    static bool? _old;

    /// <summary>The indices (into <see cref="MapScene.Objects"/>) of the placements not to draw; empty on other maps or with the old switch.</summary>
    public static HashSet<int> Hidden(MapScene scene) =>
        MapDecorLists.Hidden(scene.Map, Old, scene.Objects.Select(o => (o.Definition, o.Transform.Translation.X, o.Transform.Translation.Y)).ToList());
}
