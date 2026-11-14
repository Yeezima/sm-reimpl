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
    public static bool Old => _old ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-armoury-racks") >= 0;
    static bool? _old;

    /// <summary>The indices (into <see cref="MapScene.Objects"/>) of the placements not to draw; empty on other maps or with the old switch.</summary>
    public static HashSet<int> Hidden(MapScene scene) =>
        MapDecorLists.Hidden(scene.Map, Old, scene.Objects.Select(o => (o.Definition, o.Transform.Translation.X, o.Transform.Translation.Y)).ToList());
}
