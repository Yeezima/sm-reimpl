using SmClient.Customizer.Model;
using SmContent.Customization;
using SmSim;

namespace SmFormats.Tests;

/// <summary>Codebase fix 4: a Classic map-rotation rejoin from the front end carries the loadout saved now, with the first join's side intent.</summary>
public class RotationJoinPlanTests
{
    static JoinPlan Plan(int primary, Team side, int slot) =>
        new(Selection.CustomSlot(slot), new LoadoutChoice(Class: 1, Primary: primary), side, null, "x", AppearanceCode.Default, AppearanceCode.Default, false);

    [Fact]
    public void The_rotation_plan_is_the_loadout_saved_now_with_the_first_joins_side()
    {
        var a = Plan(3, Team.None, 1);          // joined as A, side Automatic
        var b = Plan(7, Team.SpaceMarines, 2);  // saved B in the match; the services' side is concrete or not, the join's intent wins
        var rotated = JoinPlan.ForRotation(a, b);
        Assert.Equal(7, rotated.Choice.Primary);
        Assert.Equal(b.Selection, rotated.Selection);
        Assert.Equal(Team.None, rotated.Side);
    }

    [Fact]
    public void Services_not_ready_keep_the_original_plan()
    {
        var a = Plan(3, Team.Chaos, 1);
        Assert.Same(a, JoinPlan.ForRotation(a, null));
    }
}
