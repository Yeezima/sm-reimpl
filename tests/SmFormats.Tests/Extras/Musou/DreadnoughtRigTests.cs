using SmContent;
using SmContent.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>Musou Dreadnought D2: the clip names the client picks (<see cref="DreadnoughtRig"/>): the sectors and the name patterns, and (install-gated, never in CI) that every name the pose code can ask for is in the rig.</summary>
public class DreadnoughtRigTests
{
    [Theory]
    [InlineData(0, "F")] [InlineData(20, "F")] [InlineData(30, "FR")] [InlineData(90, "R")] [InlineData(135, "BR")] [InlineData(180, "B")]
    [InlineData(-135, "BL")] [InlineData(-90, "L")] [InlineData(-45, "FL")] [InlineData(350, "F")] [InlineData(720, "F")]
    public void Sectors_are_eight_45_degree_wedges(float degrees, string expected) => Assert.Equal(expected, DreadnoughtRig.Direction(degrees));

    [Fact]
    public void Names_follow_the_rigs_pattern()
    {
        Assert.Equal("DN_NAV_Run_FL_AimF_01", DreadnoughtRig.Move(true, "FL", false, 0));
        Assert.Equal("DN_NAV_Walk_B_LAimDown", DreadnoughtRig.Move(false, "B", true, -1));
        Assert.Equal("DN_NAV_Run_R_AimUp", DreadnoughtRig.Move(true, "R", false, 1));
        Assert.Equal("DN_NAV_Idle_S_LAimF_01", DreadnoughtRig.Stand(true, 0));
        Assert.Equal("DN_NAV_AimUp_Mid", DreadnoughtRig.Stand(false, 1));
        Assert.Equal("DN_NAV_LAimDown_Mid", DreadnoughtRig.Stand(true, -1));
    }

    [Fact]
    public void The_rig_profile_points_at_the_versus_set() => Assert.Equal(("data/chunks/ebp_sets__mp_versus_set.oppc", "havok_sm_dreadnought_basic__hkxx"), (DreadnoughtRig.Profile.Archive, DreadnoughtRig.Profile.Entry));

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute() { if (GameInstall.TryLocate() is null) Skip = "Space Marine install not found (never in CI)"; }
    }

    [InstallFact]
    public void Every_clip_the_pose_code_can_ask_for_is_in_the_rig()
    {
        var names = DreadnoughtRig.Profile.LoadCharacter(GameInstall.TryLocate()!.Dir).AnimationNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var want = new List<string> { DreadnoughtRig.Entrance, DreadnoughtRig.Death, DreadnoughtRig.Sprint, DreadnoughtRig.Collision, DreadnoughtRig.Reload, DreadnoughtRig.FireAdditive, DreadnoughtRig.FireMeltaAdditive };
        foreach (bool melta in new[] { false, true })
            foreach (int aim in new[] { -1, 0, 1 })
            {
                want.Add(DreadnoughtRig.Stand(melta, aim));
                foreach (bool run in new[] { false, true })
                    foreach (var d in new[] { "F", "FR", "R", "BR", "B", "BL", "L", "FL" }) want.Add(DreadnoughtRig.Move(run, d, melta, aim));
            }
        Assert.Empty(want.Where(n => !names.Contains(n)));
    }
}
