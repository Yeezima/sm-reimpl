using System;
using System.Linq;
using SmNet.Extras.Musou;
using Xunit;
using Num = System.Numerics;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Musou Ork reaction clips (owner 2026-11-17, "Use reaction clips"): which of the rig's additive flinches an Ork takes, and at what weight (<see cref="MusouOrkReactRules"/>); client presentation only.</summary>
public class MusouOrkReactRulesTests
{
    // Seven clips leaning the head every 51 degrees or so (the rig's own are measured at run time).
    static Num.Vector2[] Fan() => Enumerable.Range(0, 7).Select(i => new Num.Vector2(MathF.Cos(i * 0.9f), MathF.Sin(i * 0.9f))).ToArray();

    [Fact]
    public void The_clip_chosen_leans_the_way_of_the_flinch()
    {
        var clips = Fan();
        for (int deg = 0; deg < 360; deg += 15)
        {
            var d = new Num.Vector2(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
            int i = MusouOrkReactRules.Pick(d, clips, 5, out float sign);
            Assert.InRange(i, 0, 6);
            Assert.True(MathF.Abs(Num.Vector2.Dot(d, clips[i])) >= 0.75f, $"{deg}: clip {i} fits {Num.Vector2.Dot(d, clips[i])}");   // within about 41 degrees of the line
            Assert.Equal(Num.Vector2.Dot(d, clips[i]) >= 0f ? 1f : -1f, sign);
        }
    }

    [Fact]
    public void A_clip_leaning_the_opposite_way_is_laid_backwards()
    {
        var clips = new[] { new Num.Vector2(0f, 1f) };
        int i = MusouOrkReactRules.Pick(new Num.Vector2(0f, -1f), clips, 1, out float sign);
        Assert.Equal(0, i);
        Assert.Equal(-1f, sign);
    }

    [Fact]
    public void The_same_Ork_always_takes_the_same_clip_and_a_wave_does_not_all_take_one()
    {
        var clips = Fan();
        var d = new Num.Vector2(-1f, 0f);
        for (int id = 1; id < 200; id++) Assert.Equal(MusouOrkReactRules.Pick(d, clips, id, out _), MusouOrkReactRules.Pick(d, clips, id, out _));
        // two clips lean the same way: the ids of a wave split between them
        var twins = new[] { new Num.Vector2(1f, 0f), new Num.Vector2(0.999f, 0.04f), new Num.Vector2(0f, 1f) };
        var picked = Enumerable.Range(1, 200).Select(id => MusouOrkReactRules.Pick(new Num.Vector2(1f, 0f), twins, id, out _)).Distinct().Order().ToArray();
        Assert.Equal(new[] { 0, 1 }, picked);
    }

    [Fact]
    public void A_clip_that_does_not_move_the_head_is_never_taken_and_no_clip_means_none()
    {
        var clips = new[] { Num.Vector2.Zero, new Num.Vector2(1f, 0f) };
        for (int id = 1; id < 50; id++) Assert.Equal(1, MusouOrkReactRules.Pick(new Num.Vector2(0f, 1f), clips, id, out _));
        Assert.Equal(-1, MusouOrkReactRules.Pick(new Num.Vector2(1f, 0f), new[] { Num.Vector2.Zero }, 1, out float sign));
        Assert.Equal(0f, sign);
        Assert.Equal(-1, MusouOrkReactRules.Pick(Num.Vector2.Zero, clips, 1, out _));
    }

    [Fact]
    public void The_weight_scales_the_clips_head_travel_to_the_procedural_flinchs_and_never_passes_the_cap()
    {
        Assert.Equal(1f, MusouOrkReactRules.Weight(1f, 0.4f, 0.4f, 1f), 3);   // a clip that already moves the head as far
        Assert.Equal(2f, MusouOrkReactRules.Weight(2f, 0.4f, 0.4f, 1f), 3);   // twice the size, twice the weight (below the cap)
        Assert.Equal(0.8f / 0.4f * 0.5f, MusouOrkReactRules.Weight(0.5f, 0.8f, 0.4f, 1f), 3);
        Assert.Equal(-MusouOrkReactRules.WeightMax, MusouOrkReactRules.Weight(MusouOrkFlinchRules.MaxStrength, 0.4f, 0.05f, -1f), 3);   // a weak clip at the biggest flinch: the cap, backwards
        Assert.Equal(0f, MusouOrkReactRules.Weight(0f, 0.4f, 0.4f, 1f));
        Assert.Equal(0f, MusouOrkReactRules.Weight(1f, 0.4f, 0f, 1f));
        Assert.Equal(0f, MusouOrkReactRules.Weight(1f, 0f, 0.4f, 1f));
    }

    [Fact]
    public void The_clip_names_are_the_rigs_seven_additive_flinches_of_each_hand_set()
    {
        var melee = MusouOrkReactRules.ClipNames(false);
        var guns = MusouOrkReactRules.ClipNames(true);
        Assert.Equal(7, melee.Length);
        Assert.Equal(7, guns.Length);
        Assert.All(melee, n => Assert.StartsWith("BOY_1HM_REC_Flinch_", n));
        Assert.All(guns, n => Assert.StartsWith("BOY_2HR_REC_Flinch_", n));
        Assert.Equal(7, melee.Distinct().Count());
    }

    [Fact]
    public void A_clip_too_weak_to_carry_the_flinch_is_left_out_unless_all_are()
    {
        // clip 0 fits the direction exactly but moves the head 0.075 against a reference of 0.414 (5.5x, past the cap); clip 1 fits less well but is strong enough
        var dirs = new[] { new Num.Vector2(1f, 0f), new Num.Vector2(0.8f, 0.6f) };
        var lens = new[] { 0.075f, 0.4f };
        for (int id = 1; id < 50; id++) Assert.Equal(1, MusouOrkReactRules.Pick(new Num.Vector2(1f, 0f), dirs, lens, 0.414f, id, out _));
        var allWeak = new[] { 0.075f, 0.08f };
        Assert.Equal(0, MusouOrkReactRules.Pick(new Num.Vector2(1f, 0f), dirs, allWeak, 0.414f, 1, out _));   // none strong: the best fit stays
        // size 1 on a clip that was kept reaches the full weight (the 2x rule is not cut short)
        Assert.Equal(0.414f / 0.4f, MusouOrkReactRules.Weight(1f, 0.414f, 0.4f, 1f), 3);
    }
}
