using System;
using System.Collections.Generic;
using System.Linq;

namespace SmClient;

/// <summary>The names MapDecor (client/MapDecor.cs, fix/manufactorum-fluorescent 2026-11-04, extended by fix/manufactorum-salmon-2 2026-11-14) does not draw on Manufactorum; engine-free so the tests can pin them.</summary>
public static class MapDecorLists
{
    /// <summary>Object definitions removed wherever they are placed.</summary>
    public static readonly string[] Always =
    [
        "backpanel_a", "backpanel_b", "backpanel_c",
        "tech_panel_a", "tech_panel_b", "tech_panel_c",
        "tech_panel_pipe_a", "tech_panel_pipe_b", "tech_panel_pipe_c",
        "handle_bar_a", "accessories_doorbutton",
        // owner 2026-11-07: the console stations' short posts (two glowing red-white dots, a red band, dash rows and thin cable lines), 49 placements, "e_adm_dressing".
        "tech_panel_pipe_e",
    ];
    /// <summary>Object definitions removed only near a board (see the class remarks): the fittings' pipes and dome, and the seals and scrolls pinned on the boards.</summary>
    public static readonly string[] Near = ["tech_panel_pipe_g", "tech_panel_tank_a", "acc_seal_01", "acc_seal_02", "acc_seal_03", "acc_scroll_01", "acc_scroll_02"];
    /// <summary>
    /// Surfaces removed from meshes that stay: the cabinet <c>tech_interface_b</c> carries two olive <c>backPanel</c> plates in its front.
    /// (mesh name, material name); the cabinet's other surfaces stay.
    /// </summary>
    public static readonly (string Mesh, string Material)[] Surfaces =
    [
        ("tech_interface_b", "backPanel"),
        // owner 2026-11-14 (fix/manufactorum-salmon-2): the salmon `tech_stations` surfaces of the objects that stay: the big horizontal pipe's collars and slabs
        // (Pipe_detail_A, 1 placement) and the console cabinets' glowing parts (tech_interface_a 4, tech_interface_b 10 placements).
        // Also the salmon surface of the vertical pipe runs (tech_panel_pipe_g, 16 placements) and the tanks (tech_panel_tank_a, 7), which stay as plain dark pipes and
        // tanks (66% and 75% of their triangles are non-salmon `Pipes_1m_LODH`, and their collision stays, so nothing invisible can be bumped into); within 6 m of a board
        // they are still removed whole (Near), as since 2026-11-04.
        ("Pipe_detail_A", "tech_stations"), ("tech_interface_a", "tech_stations"), ("tech_interface_b", "tech_stations"),
        ("tech_panel_pipe_g", "tech_stations"), ("tech_panel_tank_a", "tech_stations"),
    ];

    public const string Map = "mp_tdm_09";
    /// <summary>How close to a board (horizontal metres) a <see cref="Near"/> definition counts as part of the station.</summary>
    public const float NearRadius = 6f;

    /// <summary>Whether the material range <paramref name="material"/> of <paramref name="mesh"/> is not drawn on <paramref name="map"/> (never with <paramref name="old"/>, the restore switch).</summary>
    public static bool HidesSurface(string map, string mesh, string material, bool old) =>
        !old && map.Equals(Map, StringComparison.OrdinalIgnoreCase)
        && Surfaces.Any(s => s.Mesh.Equals(mesh, StringComparison.OrdinalIgnoreCase) && s.Material.Equals(material, StringComparison.OrdinalIgnoreCase));

    /// <summary>The indices of the placements (definition, horizontal place) not to draw; empty on other maps or with <paramref name="old"/>.</summary>
    public static HashSet<int> Hidden(string map, bool old, IReadOnlyList<(string Definition, float X, float Y)> placements)
    {
        var hidden = new HashSet<int>();
        if (old || !map.Equals(Map, StringComparison.OrdinalIgnoreCase)) return hidden;
        var always = new HashSet<string>(Always, StringComparer.OrdinalIgnoreCase);
        var near = new HashSet<string>(Near, StringComparer.OrdinalIgnoreCase);
        var boards = new List<(float X, float Y)>();
        for (int i = 0; i < placements.Count; i++)
        {
            var (d, x, y) = placements[i];
            // The boards' places (backpanel_*, and the cabinets whose plates are not drawn) anchor the Near rule.
            if (d.StartsWith("backpanel", StringComparison.OrdinalIgnoreCase) || d.Equals("tech_interface_b", StringComparison.OrdinalIgnoreCase)) boards.Add((x, y));
            if (always.Contains(d)) hidden.Add(i);
        }
        for (int i = 0; i < placements.Count; i++)
        {
            var (d, x, y) = placements[i];
            if (near.Contains(d) && boards.Any(b => (b.X - x) * (b.X - x) + (b.Y - y) * (b.Y - y) <= NearRadius * NearRadius)) hidden.Add(i);
        }
        return hidden;
    }

}
