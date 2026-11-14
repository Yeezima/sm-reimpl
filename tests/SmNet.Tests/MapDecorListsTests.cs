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

    [Fact]
    public void Another_map_hides_nothing()
    {
        Assert.False(MapDecorLists.HidesSurface("mp_tdm_01", "tech_interface_b", "tech_stations", old: false));
        Assert.Empty(MapDecorLists.Hidden("mp_tdm_01", false, [("backpanel_a", 0f, 0f), ("tech_panel_pipe_e", 0f, 0f)]));
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
}
