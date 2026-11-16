using SmClient;
using Xunit;

namespace SmNet.Tests;

/// <summary>fix/manufactorum-fluorescent (2026-11-04) and fix/manufactorum-salmon-2 (2026-11-14): the names the client does not draw on Manufactorum (client/MapDecor.cs).</summary>
public class MapDecorListsTests
{
    [Theory]
    [InlineData("backpanel_a"), InlineData("backpanel_b"), InlineData("backpanel_c")]
    [InlineData("tech_panel_a"), InlineData("tech_panel_b"), InlineData("tech_panel_c")]
    [InlineData("tech_panel_pipe_a"), InlineData("tech_panel_pipe_b"), InlineData("tech_panel_pipe_c")]
    [InlineData("handle_bar_a"), InlineData("accessories_doorbutton"), InlineData("tech_panel_pipe_e")]
    public void Salmon_and_board_definitions_are_removed_wherever_they_are_placed(string definition) =>
        Assert.Contains(definition, MapDecorLists.Always, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_pipe_runs_and_tanks_go_whole_only_near_a_board_elsewhere_only_their_salmon_surface()
    {
        Assert.Contains("tech_panel_pipe_g", MapDecorLists.Near);
        Assert.Contains("tech_panel_tank_a", MapDecorLists.Near);
        Assert.True(MapDecorLists.HidesSurface("mp_tdm_09", "tech_panel_pipe_g", "tech_stations", old: false));
        Assert.True(MapDecorLists.HidesSurface("mp_tdm_09", "tech_panel_tank_a", "tech_stations", old: false));
        Assert.False(MapDecorLists.HidesSurface("mp_tdm_09", "tech_panel_pipe_g", "Pipes_1m_LODH", old: false));   // the dark pipe stays
        var far = MapDecorLists.Hidden("mp_tdm_09", false, [("tech_panel_pipe_g", 0f, 0f), ("tech_panel_tank_a", 0f, 0f)]);
        Assert.Empty(far);   // no board within 6 m: the placements stay (their salmon surface is dropped at draw time)
        var near = MapDecorLists.Hidden("mp_tdm_09", false, [("backpanel_a", 0f, 0f), ("tech_panel_pipe_g", 5f, 0f), ("tech_panel_tank_a", 7f, 0f)]);
        Assert.Equal([0, 1], near.Order());   // the board itself and the pipe 5 m away; the tank at 7 m stays
    }

    [Theory]
    [InlineData("mp_tdm_17"), InlineData("mp_tdm_11"), InlineData("mp_tdm_18"), InlineData("mp_dlc1_map1"), InlineData("mp_dlc2_map2"), InlineData("none")]
    public void A_map_without_a_set_hides_nothing(string map)
    {
        Assert.Null(MapDecorLists.For(map));
        Assert.False(MapDecorLists.HidesSurface(map, "tech_interface_b", "tech_stations", old: false));
        Assert.Empty(MapDecorLists.Hidden(map, false, [("backpanel_a", 0f, 0f), ("tech_panel_pipe_e", 0f, 0f)]));
    }

    [Fact]
    public void Waste_management_hides_what_its_own_audit_found_with_manufactorums_names()
    {
        var set = MapDecorLists.For("MP_TDM_01")!;
        Assert.NotNull(set);
        Assert.Contains("tech_panel_d", set.Always);   // two placements beside its boards; not in Manufactorum's list
        Assert.DoesNotContain("tech_panel_d", MapDecorLists.Always);
        Assert.Contains(set.Surfaces, s => s.Mesh == "Pipe_Piece_A" && s.Material == "console_dif");
        Assert.Contains(set.Surfaces, s => s.Mesh == "tech_interface_a" && s.Material == "backPanel");
        Assert.DoesNotContain(MapDecorLists.Surfaces, s => s.Mesh == "Pipe_Piece_A");
        Assert.True(MapDecorLists.HidesSurface("mp_tdm_01", "tech_panel_pipe_g", "tech_stations", old: false));
        Assert.False(MapDecorLists.HidesSurface("mp_tdm_01", "tech_panel_pipe_g", "Pipes_1m_LODH", old: false));   // the dark pipe stays
        Assert.Contains(0, MapDecorLists.Hidden("mp_tdm_01", false, [("tech_panel_d", 0f, 0f)]));
        Assert.Empty(MapDecorLists.Hidden("mp_tdm_01", true, [("tech_panel_d", 0f, 0f)]));
        // a definition the map's audit did not find with such a material stays (Manufactorum's console posts, not placed with the salmon material here)
        Assert.Empty(MapDecorLists.Hidden("mp_tdm_01", false, [("tech_panel_pipe_e", 0f, 0f)]));
    }

    [Theory]
    [InlineData("mp_dlc1_map2", "tech_interface_a", "tech_stations"), InlineData("mp_dlc1_map2", "tech_interface_a", "backPanel")]
    [InlineData("mp_dlc2_map3", "tech_panel_tank_a", "tech_stations"), InlineData("mp_dlc2_map4", "Pipe_detail_A", "tech_stations")]
    public void The_other_kit_maps_hide_the_surfaces_their_audit_found(string map, string mesh, string material)
    {
        Assert.True(MapDecorLists.HidesSurface(map, mesh, material, old: false));
        Assert.False(MapDecorLists.HidesSurface(map, mesh, "Pipes_1m_LODH", old: false));
        Assert.False(MapDecorLists.HidesSurface(map, mesh, material, old: true));
    }

    [Theory]
    [InlineData("mp_dlc2_map1", "backpanel_a"), InlineData("mp_dlc2_map1", "tech_panel_d"), InlineData("mp_dlc2_map3", "handle_bar_a"), InlineData("mp_dlc2_map4", "tech_panel_a")]
    public void The_boards_and_fittings_of_the_other_kit_maps_are_not_drawn(string map, string definition)
    {
        Assert.Contains(0, MapDecorLists.Hidden(map, false, [(definition, 0f, 0f)]));
        Assert.Empty(MapDecorLists.Hidden(map, true, [(definition, 0f, 0f)]));
    }

    [Fact]
    public void Maps_that_hide_nothing_are_the_ones_whose_audit_found_no_salmon_or_olive_board_kit()
    {
        foreach (var map in new[] { "mp_tdm_11", "mp_tdm_17", "mp_tdm_18", "mp_dlc1_map1", "mp_dlc2_map2" }) Assert.Null(MapDecorLists.For(map));
        Assert.Equal(6, MapDecorLists.Sets.Count);
    }

    [Fact]
    public void The_restore_switch_hides_nothing_even_on_manufactorum()
    {
        Assert.True(MapDecorLists.HidesSurface("MP_TDM_09", "Pipe_detail_A", "tech_stations", old: false));   // map and names match without case
        Assert.False(MapDecorLists.HidesSurface("mp_tdm_09", "Pipe_detail_A", "tech_stations", old: true));
        Assert.Contains(0, MapDecorLists.Hidden("mp_tdm_09", false, [("tech_panel_pipe_e", 0f, 0f)]));
        Assert.Empty(MapDecorLists.Hidden("mp_tdm_09", true, [("tech_panel_pipe_e", 0f, 0f), ("backpanel_a", 0f, 0f)]));
    }

    [Theory]
    [InlineData("tech_interface_b", "backPanel")]
    [InlineData("Pipe_detail_A", "tech_stations"), InlineData("tech_interface_a", "tech_stations"), InlineData("tech_interface_b", "tech_stations")]
    public void Salmon_and_olive_surfaces_of_the_objects_that_stay_are_removed(string mesh, string material) =>
        Assert.Contains(MapDecorLists.Surfaces, s => s.Mesh.Equals(mesh, StringComparison.OrdinalIgnoreCase) && s.Material.Equals(material, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Nothing_is_listed_twice_and_no_definition_is_both_always_and_near()
    {
        Assert.Equal(MapDecorLists.Always.Length, MapDecorLists.Always.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(MapDecorLists.Always.Intersect(MapDecorLists.Near, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("mp_tdm_17", true), InlineData("mp_tdm_01", true), InlineData("mp_dlc2_map4", true), InlineData("MP_TDM_09", false), InlineData("mp_tdm_09", false)]
    public void The_sky_boxes_are_skipped_on_the_new_maps_and_never_on_manufactorum(string map, bool skipped)
    {
        Assert.Equal(skipped, MapDecorLists.SkipsSkyBoxes(map, old: false));
        Assert.False(MapDecorLists.SkipsSkyBoxes(map, old: true));   // --old-sky-boxes restores the drawing everywhere
    }

    [Theory]
    [InlineData("mp_tdm_17", true), InlineData("mp_dlc1_map1", true), InlineData("mp_tdm_09", false), InlineData("MP_TDM_09", false)]
    public void The_sky_boxes_are_drawn_as_a_sky_on_the_new_maps_only_and_the_switches_turn_it_off(string map, bool sky)
    {
        Assert.Equal(sky, MapDecorLists.DrawsSkyTextures(map, old: false, off: false));
        Assert.False(MapDecorLists.DrawsSkyTextures(map, old: false, off: true));    // --no-sky-textures
        Assert.False(MapDecorLists.DrawsSkyTextures(map, old: false, off: false, compatibility: true));   // the Compatibility renderer keeps the haze
        Assert.False(MapDecorLists.DrawsSkyTextures(map, old: true, off: false));    // --old-sky-boxes
    }

    [Theory]
    [InlineData("diffuv1_vtxtint_normdtluv1_specuv1", true), InlineData("diffuv1_vtxtint_normdtluv1_drvspec", true), InlineData("diff2_mskblenduv10_vtxtint_norm_drvspec", false), InlineData("diff_norm", false), InlineData(null, false)]
    public void Only_the_diffuv1_shaders_read_their_base_maps_through_the_second_uv_set(string? shader, bool second) =>
        Assert.Equal(second, MapDecorLists.UsesSecondUvSet(shader));

    [Theory]
    [InlineData("mp_tdm_11", true), InlineData("MP_TDM_11", true), InlineData("mp_tdm_09", false), InlineData("mp_tdm_17", false)]
    public void Only_basilica_gets_the_dark_floor_plane_and_the_switch_removes_it(string map, bool floor)
    {
        Assert.Equal(floor, MapDecorLists.HasVoidFloor(map, off: false));
        Assert.False(MapDecorLists.HasVoidFloor(map, off: true));
    }

    [Theory]
    [InlineData("mp_tdm_17", true), InlineData("MP_DLC2_MAP4", true), InlineData("mp_tdm_09", false), InlineData("MP_TDM_09", false), InlineData("tfw_war", false), InlineData("", false), InlineData(null, false)]
    public void The_new_map_drawing_rules_gate_on_the_imported_classic_maps_only(string? map, bool rules)
    {
        // self-lit cards, fog-free vista cards, the second UV set and the sky textures: never Manufactorum, Musou's maps, characters or weapons (no map name)
        Assert.Equal(rules, MapDecorLists.IsNewClassicMap(map));
        if (map is not null) Assert.Equal(rules, MapDecorLists.DrawsSkyTextures(map, old: false, off: false));
    }

    [Fact]
    public void The_sky_sits_on_the_far_plane_of_either_depth_convention()
    {
        Assert.Equal(0f, MapDecorLists.SkyFarZ(compatibility: false));    // reversed-Z: far is 0
        Assert.InRange(MapDecorLists.SkyFarZ(compatibility: true), 0.999f, 1f);   // OpenGL clip: far is +w
    }

    [Fact]
    public void The_ycocg_decode_matches_the_shader_formula()
    {
        const float c = 128f / 255f;
        static void Same((float R, float G, float B) got, float r, float g, float b) { Assert.Equal(r, got.R, 3); Assert.Equal(g, got.G, 3); Assert.Equal(b, got.B, 3); }
        Same(YCoCgSky.Decode(c, c, 0f, 0.5f), 0.5f, 0.5f, 0.5f);                 // no chroma: grey of luma Y
        Same(YCoCgSky.Decode(c + 0.2f, c, 0f, 0.5f), 0.7f, 0.5f, 0.3f);          // Co = 0.2: warm
        Same(YCoCgSky.Decode(c + 0.2f, c, 8f / 255f, 0.5f), 0.6f, 0.5f, 0.4f);   // scale B = 8/255 -> 2: chroma halved
        Same(YCoCgSky.Decode(c, c + 0.3f, 0f, 0.9f), 0.6f, 1f, 0.6f);            // Cg = 0.3: R = B = Y - Cg, G = Y + Cg (clamped)
    }

    [Fact]
    public void Only_the_engines_sky_shaders_count_as_sky()
    {
        Assert.True(MapDecorLists.IsSkyShader("sky_ycocg_bloomcontrol"));
        Assert.False(MapDecorLists.IsSkyShader("diff_unlit"));
        Assert.False(MapDecorLists.IsSkyShader(null));
    }
}
