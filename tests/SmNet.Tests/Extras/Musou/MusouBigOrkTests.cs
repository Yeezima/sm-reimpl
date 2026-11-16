using SmContent;
using SmContent.Extras.Musou;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The owner, 2026-11-09: "the elite and boss orks and the warboss need to have death animations. they also look like they move in slow motion because of the way they're scaled up. resolve these
/// issues." (<see cref="MusouBigOrkRules"/>). Engine-free: the client's choices (body or no body, the Warboss's death clip, the run's rate) from the same rules it uses. The look itself is checked with
/// the client's <c>--musou-crowd-test</c> stage (not here: it needs the install).
/// </summary>
public class MusouBigOrkTests
{
    static readonly MusouEnemyType[] Big =
    [
        MusouEnemyType.Nob, MusouEnemyType.ArdBoy, MusouEnemyType.BigShootaNob, MusouEnemyType.Warboss,
        MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob,
    ];

    static readonly MusouDeathKind[] Torn = [MusouDeathKind.BoltBlast, MusouDeathKind.Blast, MusouDeathKind.Barrage, MusouDeathKind.OrkBlast];

    /// <summary>The client's body decision (NetworkView.Musou.cs): torn by the gore unless the kind keeps its body, then the corpse floor's rule with the burst drawn.</summary>
    static bool LeavesBody(MusouDeathKind cause, MusouEnemyType type, bool old)
    {
        var plan = MusouGoreRules.Plan(cause, type, killer: 1, killerHasLascannon: false, metres: 10f, load: 1f, id: 7, tick: 100);
        bool gibbed = plan.Gibbed && !MusouBigOrkRules.KeepsBody(type, old);
        return MusouCorpseRules.LeavesBody(gibbed, burstDrawn: true, farUnseen: false, old: false);
    }

    [Fact]
    public void The_elites_and_bosses_are_the_big_orks_and_the_rest_are_not()
    {
        Assert.All(Big, t => Assert.True(MusouBigOrkRules.IsBig(t), t.ToString()));
        foreach (var t in Enum.GetValues<MusouEnemyType>().Except(Big)) Assert.False(MusouBigOrkRules.IsBig(t), t.ToString());
    }

    [Fact]
    public void A_blast_or_a_bolt_blast_tears_a_big_ork_apart_no_more_but_still_tears_a_boy()
    {
        foreach (var cause in Torn)
        {
            Assert.True(MusouGoreRules.Plan(cause, MusouEnemyType.BossNob, 1, false, 10f, 1f, 7, 100).Gibbed, "the gore still bursts him");   // the burst is drawn as before
            Assert.All(Big, t => Assert.True(LeavesBody(cause, t, old: false), $"{t} by {cause} left no body"));
            Assert.False(LeavesBody(cause, MusouEnemyType.Boy, old: false), $"a Boy by {cause} should still be torn apart");
            Assert.All(Big, t => Assert.False(LeavesBody(cause, t, old: true), $"old: {t} by {cause} kept a body"));
        }
        Assert.True(LeavesBody(MusouDeathKind.Shot, MusouEnemyType.BossNob, old: true));   // a plain shot always left one
    }

    [Fact]
    public void The_warboss_dies_by_his_get_up_played_backwards_and_the_old_switch_keeps_the_blast_reaction()
    {
        Assert.Equal("WB_Get_Up", WarbossRig.Death.Names[0]);
        Assert.True(WarbossRig.DeathReversed);
        Assert.Equal("WB_REC_Explode", WarbossRig.OldDeath.Names[0]);
        Assert.Equal(("RIG_LLegToe1", "RIG_RLegToe1", "RIG_LLegAnkle", "RIG_RLegAnkle"), (FootBones.Warboss.LeftToe, FootBones.Warboss.RightToe, FootBones.Warboss.LeftAnkle, FootBones.Warboss.RightAnkle));
    }

    [Theory]
    [InlineData(3f, 4.4f, 5.51f)]    // a Boss Nob: the Boy's run (5.51 m/s at scale 1, measured), his 4.4 m/s
    [InlineData(3f, 4.4f, 5.98f)]    // a Boss Shoota Boy: the gun run (5.98 m/s)
    [InlineData(2.19f, 4.6f, 3.98f)] // the Warboss: his walk (3.98 m/s on his own feet), drawn x2.19
    public void A_scaled_body_steps_the_square_root_of_its_scale_more_often_than_before(float scale, float speed, float clipSpeed)
    {
        float before = MusouGaitRules.Rate(speed, clipSpeed * MusouBigOrkRules.StrideScale(scale, 1f));
        float now = MusouGaitRules.Rate(speed, clipSpeed * MusouBigOrkRules.StrideScale(scale, MusouBigOrkRules.StrideExponent));
        Assert.Equal(MathF.Sqrt(scale), now / before, 3);
        Assert.InRange(now, 0.4f, 1f);   // no longer a quarter-speed stride
        // The price: the feet travel sqrt(scale) times the body's speed.
        float feet = now * clipSpeed * scale;
        Assert.Equal(MathF.Sqrt(scale), feet / speed, 2);
    }

    [Fact]
    public void An_ordinary_size_stride_is_unchanged()
    {
        Assert.Equal(1f, MusouBigOrkRules.StrideScale(1f, MusouBigOrkRules.StrideExponent));
        Assert.Equal(3f, MusouBigOrkRules.StrideScale(3f, 1f), 4);
        Assert.Equal(1f, MusouBigOrkRules.StrideScale(3f, 0f));
        Assert.Equal(MusouGaitRules.Rate(5.2f, 5.51f), MusouGaitRules.Rate(5.2f, 5.51f * MusouBigOrkRules.StrideScale(1f, MusouBigOrkRules.StrideExponent)));
    }
}
