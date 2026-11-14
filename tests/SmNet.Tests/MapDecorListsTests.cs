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
    // the 2026-11-14 additions: the vertical pipe runs with salmon bands and the cylinder tanks, wherever they are placed
    [InlineData("tech_panel_pipe_g"), InlineData("tech_panel_tank_a")]
    public void Salmon_and_board_definitions_are_removed_wherever_they_are_placed(string definition) =>
        Assert.Contains(definition, MapDecorLists.Always, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_pipe_and_tank_are_no_longer_only_removed_near_a_board() =>
        Assert.DoesNotContain(MapDecorLists.Near, n => n is "tech_panel_pipe_g" or "tech_panel_tank_a");

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
