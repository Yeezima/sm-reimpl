using System;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;
using Xunit;
using Num = System.Numerics;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Musou Ork hit reactions (owner 2026-11-13, reference v3): the envelope measured from the reference, the owner's strength table, the size scaling and the no-growth re-kick (<see cref="MusouOrkFlinchRules"/>).</summary>
public class MusouOrkFlinchRulesTests
{
    [Theory]   // the reference's excess over the idle sway at 0, 42, 83, 125, 167, 208, 250, 292, 333 ms, as a share of its 8.4 peak: 1, 1, .89, .68, .49, .32, .19, .10, .06
    [InlineData(0.042f, 1.0f)]
    [InlineData(0.083f, 0.89f)]
    [InlineData(0.125f, 0.68f)]
    [InlineData(0.167f, 0.49f)]
    [InlineData(0.208f, 0.32f)]
    [InlineData(0.250f, 0.19f)]
    [InlineData(0.292f, 0.10f)]
    public void The_envelope_follows_the_reference_within_a_tenth(float age, float reference) =>
        Assert.InRange(MusouOrkFlinchRules.Envelope(age), reference - 0.1f, reference + 0.1f);

    [Fact]
    public void The_envelope_rises_in_a_frame_holds_and_is_over_by_about_330_ms()
    {
        Assert.Equal(0f, MusouOrkFlinchRules.Envelope(0f));
        Assert.Equal(1f, MusouOrkFlinchRules.Envelope(MusouOrkFlinchRules.Rise), 3);
        Assert.Equal(1f, MusouOrkFlinchRules.Envelope(MusouOrkFlinchRules.Rise + MusouOrkFlinchRules.Hold * 0.5f));
        Assert.Equal(0f, MusouOrkFlinchRules.Envelope(MusouOrkFlinchRules.Seconds));
        Assert.InRange(MusouOrkFlinchRules.Seconds, 0.30f, 0.36f);
        float last = 1f;
        for (float t = MusouOrkFlinchRules.Rise + MusouOrkFlinchRules.Hold; t < MusouOrkFlinchRules.Seconds; t += 0.005f)
        {
            float v = MusouOrkFlinchRules.Envelope(t);
            Assert.True(v <= last + 1e-6f); last = v;
        }
    }

    [Theory]   // the owner's typed table (2026-11-13)
    [InlineData("sm_mp_pistol_bolt_pistol", 1f)]
    [InlineData("sm_mp_ranged_bolter", 1f)]
    [InlineData("sm_mp_pistol_plasma_pistol", 1f)]
    [InlineData("sm_mp_ranged_heavy_bolter", 1.3f)]
    [InlineData("sm_mp_ranged_plasma_gun", 1.3f)]
    [InlineData("sm_mp_ranged_sniper_rifle", 1.5f)]
    [InlineData("sm_mp_ranged_melta", 1.5f)]
    [InlineData("sm_mp_exotic_las_cannon", 1.5f)]
    [InlineData("sm_mp_exotic_auto_cannon", 1.8f)]
    [InlineData(null, 1f)]
    public void A_shot_flinches_by_its_weapon(string? weapon, float expected) => Assert.Equal(expected, MusouOrkFlinchRules.ShotStrength(weapon), 3);

    [Fact]
    public void Melee_is_light_1_3_heavy_2_charge_2_5_and_hammers_and_axes_a_step_up()
    {
        Assert.Equal(1.3f, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_power_sword", false, false), 3);
        Assert.Equal(2f, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_power_sword", true, false), 3);
        Assert.Equal(2.5f, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_power_sword", true, true), 3);
        Assert.Equal(1.3f * MusouOrkFlinchRules.HeavyWeaponStep, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_2hand_thunder_hammer", false, false), 3);
        Assert.Equal(2f * MusouOrkFlinchRules.HeavyWeaponStep, MusouOrkFlinchRules.MeleeStrength("sm_mp_melee_power_axe", true, false), 3);
        Assert.True(MusouOrkFlinchRules.HeavyWeaponStep > 1f);
    }

    [Fact]
    public void A_blast_kicks_2_5_beside_it_fading_to_1_at_the_edge()
    {
        Assert.Equal(2.5f, MusouOrkFlinchRules.BlastStrength(0f, 8f), 3);
        Assert.Equal(1.75f, MusouOrkFlinchRules.BlastStrength(4f, 8f), 3);
        Assert.Equal(1f, MusouOrkFlinchRules.BlastStrength(8f, 8f), 3);
        Assert.Equal(1f, MusouOrkFlinchRules.BlastStrength(20f, 8f), 3);
        Assert.Equal(1f, MusouOrkFlinchRules.BlastStrength(1f, 0f), 3);
    }

    [Fact]
    public void The_rank_and_file_flinch_fully_the_elites_less_and_the_bosses_a_little()
    {
        foreach (var t in new[] { MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.Gretchin }) Assert.Equal(1f, MusouOrkFlinchRules.SizeScale(t));
        foreach (var t in new[] { MusouEnemyType.Nob, MusouEnemyType.ArdBoy }) Assert.InRange(MusouOrkFlinchRules.SizeScale(t), 0.3f, 0.9f);
        foreach (var t in new[] { MusouEnemyType.BossBoy, MusouEnemyType.BossNob, MusouEnemyType.Warboss }) Assert.InRange(MusouOrkFlinchRules.SizeScale(t), 0.05f, 0.3f);
        Assert.True(MusouOrkFlinchRules.SizeScale(MusouEnemyType.Warboss) <= MusouOrkFlinchRules.SizeScale(MusouEnemyType.BossNob));
        Assert.Equal(MusouOrkFlinchRules.MaxStrength, MusouOrkFlinchRules.Amplitude(10f, MusouEnemyType.Boy));
    }

    [Fact]
    public void Far_away_shows_nothing()
    {
        Assert.Equal(1f, MusouOrkFlinchRules.DistanceScale(30f));
        Assert.Equal(0f, MusouOrkFlinchRules.DistanceScale(MusouOrkFlinchRules.FarMetres + 1f));
    }

    [Fact]
    public void A_burst_of_hits_re_kicks_to_the_same_size_and_never_grows()
    {
        var track = new MusouOrkFlinchTrack();
        var away = new Num.Vector2(0f, 1f);
        float most = 0f;
        for (int k = 0; k < 60; k++)   // 12 hits a second for 5 s, at 60 fps
        {
            if (k % 5 == 0) track.Kick(away, 1f);
            Assert.True(track.Step(1f / 60f));
            most = MathF.Max(most, track.Current().Length());
        }
        Assert.InRange(most, 0.98f, 1.0001f);
    }

    [Fact]
    public void A_kick_returns_to_rest_and_a_second_from_another_side_swings_through_without_a_jump()
    {
        var track = new MusouOrkFlinchTrack();
        track.Kick(new Num.Vector2(1f, 0f), 1f);
        float t = 0f; while (track.Step(1f / 240f)) t += 1f / 240f;
        Assert.InRange(t, MusouOrkFlinchRules.Seconds - 0.01f, MusouOrkFlinchRules.Seconds + 0.01f);
        Assert.Equal(Num.Vector2.Zero, track.Current());
        track.Kick(new Num.Vector2(1f, 0f), 1f);
        for (int i = 0; i < 12; i++) track.Step(1f / 240f);   // 50 ms: full size
        var before = track.Current();
        track.Kick(new Num.Vector2(-1f, 0f), 1f);
        Assert.Equal(before, track.Current());   // the kick starts from where it is
        float prev = before.X;
        for (int i = 0; i < 10; i++) { track.Step(1f / 240f); Assert.True(track.Current().X <= prev + 1e-5f); prev = track.Current().X; }
        Assert.True(track.Current().Length() <= 1.0001f);
    }

    [Fact]
    public void The_whole_pose_stays_inside_the_references_size()
    {
        // at strength 1: 38 degrees of torso, 14 cm back, 9 cm down (the proxy's "V2 reaction +30%", translated)
        Assert.InRange(MusouOrkFlinchRules.Spine1 + MusouOrkFlinchRules.Spine2 + MusouOrkFlinchRules.Spine3 + MusouOrkFlinchRules.Neck + MusouOrkFlinchRules.Head, 30f, 46f);
        Assert.InRange(MusouOrkFlinchRules.PelvisBack, 0.02f, 0.2f);
        Assert.InRange(MusouOrkFlinchRules.PelvisDrop, 0.02f, 0.1f);
    }
}
