using System.Numerics;
using SmContent;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>mapmaker-classic S2: the headless import path (InstalledSm1Source) hides the same placements Classic does. Synthetic placements only.</summary>
public class Sm1HiddenTests
{
    static Sm1Object Put(string definition, float x, float y) =>
        new(definition + "_" + x, definition, "l_main", Matrix4x4.CreateTranslation(x, y, 0), 0);

    [Fact]
    public void Manufactorum_like_scene_hides_what_the_client_hides()
    {
        Sm1Object[] scene =
        [
            Put("backpanel_a", 0, 0),          // 0 always
            Put("tech_panel_pipe_g", 5, 0),    // 1 near a board
            Put("tech_panel_pipe_g", 50, 0),   // 2 far: stays
            Put("tech_interface_a", 100, 0),   // 3 stays
            Put("acc_seal_01", 103, 0),        // 4 near a Manufactorum console: papers hidden
            Put("acc_seal_01", 300, 0),        // 5 far: stays
            Put("wall_main", 0, 0),            // 6 stays
        ];
        var hidden = InstalledSm1Source.HiddenPlacements("mp_tdm_09", scene);
        Assert.Equal(new[] { 0, 1, 4 }, hidden.OrderBy(i => i).ToArray());
        // identical to the client's call
        Assert.Equal(MapDecorLists.Hidden("mp_tdm_09", false, scene.Select(o => (o.Definition, o.Transform.Translation.X, o.Transform.Translation.Y)).ToList()).OrderBy(i => i), hidden.OrderBy(i => i));
        Assert.Empty(InstalledSm1Source.HiddenPlacements("mp_tdm_09", scene, old: true));
        Assert.Empty(InstalledSm1Source.HiddenPlacements("mp_tdm_18", scene));   // a map without a set hides nothing
    }

    [Fact]
    public void Sm1Map_carries_the_set()
    {
        var map = new Sm1Map("mp_tdm_09", [Put("backpanel_a", 0, 0)], new Dictionary<string, Sm1Definition>(), [])
        { Hidden = InstalledSm1Source.HiddenPlacements("mp_tdm_09", [Put("backpanel_a", 0, 0)]) };
        Assert.NotNull(map.Hidden);
        Assert.Contains(0, map.Hidden!);
    }
}
