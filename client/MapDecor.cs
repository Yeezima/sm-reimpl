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
/// domes): they are removed only where they sit within <see cref="NearRadius"/> metres (horizontally) of a board or a <c>tech_interface_b</c> cabinet, so the monitor
/// consoles keep their cabling. The console stations themselves (<c>tech_interface_*</c>, <c>tech_panel_pipe_e</c>) stay. <c>--old-armoury-racks</c>
/// draws everything as before.
/// </para>
/// </summary>
public static class MapDecor
{
    public const string Map = "mp_tdm_09";
    /// <summary>Object definitions removed wherever they are placed.</summary>
    public static readonly string[] Always =
    [
        "backpanel_a", "backpanel_b", "backpanel_c",
        "tech_panel_a", "tech_panel_b", "tech_panel_c",
        "tech_panel_pipe_a", "tech_panel_pipe_b", "tech_panel_pipe_c",
        "handle_bar_a", "accessories_doorbutton",
    ];
    /// <summary>Object definitions removed only near a board (see the class remarks): the fittings' pipes and dome, and the seals and scrolls pinned on the boards.</summary>
    public static readonly string[] Near = ["tech_panel_pipe_g", "tech_panel_tank_a", "acc_seal_01", "acc_seal_02", "acc_seal_03", "acc_scroll_01", "acc_scroll_02"];
    /// <summary>
    /// Surfaces removed from meshes that stay: the cabinet <c>tech_interface_b</c> carries two olive <c>backPanel</c> plates in its front.
    /// (mesh name, material name); the cabinet's other surfaces stay.
    /// </summary>
    public static readonly (string Mesh, string Material)[] Surfaces = [("tech_interface_b", "backPanel")];

    /// <summary>Whether the material range <paramref name="material"/> of <paramref name="mesh"/> is not drawn on <paramref name="map"/>.</summary>
    public static bool HidesSurface(string map, string mesh, string material) =>
        !Old && map.Equals(Map, StringComparison.OrdinalIgnoreCase)
        && Surfaces.Any(s => s.Mesh.Equals(mesh, StringComparison.OrdinalIgnoreCase) && s.Material.Equals(material, StringComparison.OrdinalIgnoreCase));
    /// <summary>How close to a board (horizontal metres) a <see cref="Near"/> definition counts as part of the station.</summary>
    public const float NearRadius = 6f;

    /// <summary><c>--old-armoury-racks</c>: draw the boards and their fittings as before (before / after aid).</summary>
    public static bool Old => _old ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--old-armoury-racks") >= 0;
    static bool? _old;

    /// <summary>The indices (into <see cref="MapScene.Objects"/>) of the placements not to draw; empty on other maps or with the old switch.</summary>
    public static HashSet<int> Hidden(MapScene scene)
    {
        var hidden = new HashSet<int>();
        if (Old || !scene.Map.Equals(Map, StringComparison.OrdinalIgnoreCase)) return hidden;
        var always = new HashSet<string>(Always, StringComparer.OrdinalIgnoreCase);
        var near = new HashSet<string>(Near, StringComparer.OrdinalIgnoreCase);
        var boards = new List<System.Numerics.Vector3>();
        for (int i = 0; i < scene.Objects.Count; i++)
        {
            var o = scene.Objects[i];
            // The boards' places (backpanel_*, and the cabinets whose plates are not drawn) anchor the Near rule.
            if (o.Definition.StartsWith("backpanel", StringComparison.OrdinalIgnoreCase) || o.Definition.Equals("tech_interface_b", StringComparison.OrdinalIgnoreCase))
                boards.Add(o.Transform.Translation);
            if (always.Contains(o.Definition)) hidden.Add(i);
        }
        for (int i = 0; i < scene.Objects.Count; i++)
        {
            var o = scene.Objects[i];
            if (!near.Contains(o.Definition)) continue;
            var p = o.Transform.Translation;
            if (boards.Any(b => (b.X - p.X) * (b.X - p.X) + (b.Y - p.Y) * (b.Y - p.Y) <= NearRadius * NearRadius)) hidden.Add(i);
        }
        return hidden;
    }
}
