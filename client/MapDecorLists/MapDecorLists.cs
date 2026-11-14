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
        // owner 2026-11-14 (fix/manufactorum-salmon-2): the same salmon fittings wherever else they are placed (the vertical pipe runs with glowing bands and elbows, the
        // cylinder tanks with salmon panels): tech_panel_pipe_g 16 placements, tech_panel_tank_a 7 (they were removed only within 6 m of a board before).
        "tech_panel_pipe_g", "tech_panel_tank_a",
    ];
    /// <summary>Object definitions removed only near a board (see the class remarks): the fittings' pipes and dome, and the seals and scrolls pinned on the boards.</summary>
    public static readonly string[] Near = ["acc_seal_01", "acc_seal_02", "acc_seal_03", "acc_scroll_01", "acc_scroll_02"];
    /// <summary>
    /// Surfaces removed from meshes that stay: the cabinet <c>tech_interface_b</c> carries two olive <c>backPanel</c> plates in its front.
    /// (mesh name, material name); the cabinet's other surfaces stay.
    /// </summary>
    public static readonly (string Mesh, string Material)[] Surfaces =
    [
        ("tech_interface_b", "backPanel"),
        // owner 2026-11-14 (fix/manufactorum-salmon-2): the salmon `tech_stations` surfaces of the objects that stay: the big horizontal pipe's collars and slabs
        // (Pipe_detail_A, 1 placement) and the console cabinets' glowing parts (tech_interface_a 4, tech_interface_b 10 placements).
        ("Pipe_detail_A", "tech_stations"), ("tech_interface_a", "tech_stations"), ("tech_interface_b", "tech_stations"),
    ];

}
