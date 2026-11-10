using System;
using System.Collections.Generic;
using System.Linq;
using SmNet.Extras.Musou;
using Xunit;
using Num = System.Numerics;

namespace SmNet.Tests.Extras.Musou;

/// <summary>The Musou weight pass, MW1: the ground contacts from foot tags and bones (<see cref="MusouWeightRules"/>, <see cref="MusouContactTracker"/>).</summary>
public class MusouWeightContactTests
{
    const double Dt = 1.0 / 60.0;

    static MusouBoneSample[] Standing()
    {
        var b = new MusouBoneSample[MusouWeightRules.Parts];
        for (int i = 0; i < b.Length; i++)
        {
            var part = (MusouContactPart)i;
            float h = part switch
            {
                MusouContactPart.LeftFoot or MusouContactPart.RightFoot => 0.03f,
                MusouContactPart.LeftKnee or MusouContactPart.RightKnee => 0.5f,
                MusouContactPart.LeftHand or MusouContactPart.RightHand => 0.9f,
                MusouContactPart.Pelvis => 1.0f,
                _ => 1.5f,
            };
            b[i] = new MusouBoneSample(new Num.Vector3(i * 0.1f, h, 0f), h);
        }
        return b;
    }

    static MusouBoneSample[] With(MusouBoneSample[] b, MusouContactPart part, float height, float x = 0f)
    {
        var c = (MusouBoneSample[])b.Clone();
        c[(int)part] = new MusouBoneSample(new Num.Vector3(x, height, 0f), height);
        return c;
    }

    [Fact]
    public void FootTagsMapToFeetKindsAndStrengths()
    {
        Assert.Equal((MusouContactPart.LeftFoot, MusouContactKind.Step, 0.35f), MusouWeightRules.FromFootTag("EVT_Left_Foot", "SM_NAV_Run"));
        Assert.Equal((MusouContactPart.RightFoot, MusouContactKind.Step, 0.55f), MusouWeightRules.FromFootTag("EVT_Right_Foot_Rush", "SM_NAV_Sprint"));
        Assert.Equal(MusouContactKind.Plant, MusouWeightRules.FromFootTag("EVT_Left_Foot", "SM_MEL_CS_Knife_X1")!.Value.Kind);
        Assert.Equal((MusouContactKind.Plant, 0.8f), (MusouWeightRules.FromFootTag("EVT_Bullrush_Stop_Right", null)!.Value.Kind, MusouWeightRules.FromFootTag("EVT_Bullrush_Stop_Right", null)!.Value.Strength));
        Assert.Null(MusouWeightRules.FromFootTag("EVT_Fire", "SM_NAV_Run"));
        Assert.False(MusouWeightRules.IsFootTag("ability_melee_x1"));
    }

    [Fact]
    public void AKneeComingDownTouchesOnceThenNotWhileResting()
    {
        var t = new MusouContactTracker();
        var into = new List<MusouGroundContact>();
        var b = Standing();
        double now = 0;
        t.Step(1, true, b, true, 0f, false, now, into);
        // The knee drops from 0.5 m to 0.1 m over 0.2 s, then rests there.
        for (int k = 1; k <= 12; k++) { now += Dt; t.Step(1, true, With(b, MusouContactPart.LeftKnee, Math.Max(0.1f, 0.5f - k * 0.04f)), true, 0f, false, now, into); }
        for (int k = 0; k < 30; k++) { now += Dt; t.Step(1, true, With(b, MusouContactPart.LeftKnee, 0.1f), true, 0f, false, now, into); }
        var knee = into.Where(c => c.Part == MusouContactPart.LeftKnee).ToList();
        Assert.Single(knee);
        Assert.Equal(MusouContactKind.Touch, knee[0].Kind);
        Assert.InRange(knee[0].Strength, 0.2f, 0.8f);
        Assert.Equal(0f, knee[0].At.Y, 3);   // on the ground under the bone
    }

    [Fact]
    public void FeetAreLeftToTheTagsUnlessTheCallerSaysSo()
    {
        var into = new List<MusouGroundContact>();
        foreach (bool feet in new[] { false, true })
        {
            var t = new MusouContactTracker();
            into.Clear();
            var b = Standing();
            double now = 0;
            t.Step(1, true, With(b, MusouContactPart.RightFoot, 0.3f), true, 0f, feet, now, into);
            for (int k = 1; k <= 10; k++) { now += Dt; t.Step(1, true, With(b, MusouContactPart.RightFoot, Math.Max(0.03f, 0.3f - k * 0.04f)), true, 0f, feet, now, into); }
            Assert.Equal(feet ? 1 : 0, into.Count(c => c.Part == MusouContactPart.RightFoot));
        }
    }

    [Fact]
    public void ASlidingTouchingPartLeavesASkidTrail()
    {
        var t = new MusouContactTracker();
        var into = new List<MusouGroundContact>();
        var b = With(Standing(), MusouContactPart.Pelvis, 0.2f);
        double now = 0;
        t.Step(1, false, b, true, 0f, false, now, into);
        // The pelvis on the ground, sliding at 5 m/s for 0.5 s.
        for (int k = 1; k <= 30; k++) { now += Dt; t.Step(1, false, With(b, MusouContactPart.Pelvis, 0.2f, x: (float)(k * Dt * 5.0)), true, 0f, false, now, into); }
        var skids = into.Where(c => c.Kind == MusouContactKind.Skid && c.Part == MusouContactPart.Pelvis).ToList();
        Assert.InRange(skids.Count, 5, 7);   // every 0.08 s
        Assert.All(skids, s => Assert.InRange(s.GroundSpeed, 4.5f, 5.5f));
        Assert.All(skids, s => Assert.False(s.Own));
    }

    [Fact]
    public void ALandingAfterAFallPuffsBothFeetByFallSpeedAndQuietsTheBones()
    {
        var t = new MusouContactTracker();
        var into = new List<MusouGroundContact>();
        var b = Standing();
        double now = 0;
        t.Step(1, true, b, true, 0f, true, now, into);
        for (int k = 0; k < 40; k++) { now += Dt; t.Step(1, true, b, false, -9.8f * (float)(k * Dt) - 20f, true, now, into); }   // 0.67 s in the air, falling past 20 m/s
        now += Dt;
        t.Step(1, true, With(b, MusouContactPart.LeftKnee, 0.12f), true, 0f, true, now, into);   // lands; the knee drops at once (the bones are quiet)
        var landings = into.Where(c => c.Kind == MusouContactKind.Landing).ToList();
        Assert.Equal(2, landings.Count);
        Assert.Equal(1f, landings[0].Strength);
        Assert.DoesNotContain(into, c => c.Kind == MusouContactKind.Touch);
    }

    static MusouGroundContact Contact(MusouContactKind kind, bool own = true, float strength = 0.5f, MusouContactPart part = MusouContactPart.LeftFoot) =>
        new(1, own, part, kind, Num.Vector3.Zero, strength, 0f);

    [Fact]
    public void PuffsAreDustAndGritSizedByStrengthAndHalvedForAllies()
    {
        var into = new MusouPuff[2];
        Assert.Equal(1, MusouPuffRules.Of(Contact(MusouContactKind.Step, strength: 0.35f), "dirt", gpu: true, 1, into));
        Assert.Equal(MusouPuffRules.Dust, into[0].Effect);
        float own = into[0].Scale;
        Assert.Equal(1, MusouPuffRules.Of(Contact(MusouContactKind.Step, own: false, strength: 0.35f), "dirt", gpu: true, 2, into));
        Assert.Equal(own * MusouPuffRules.AllyScale, into[0].Scale, 4);
        Assert.Equal(2, MusouPuffRules.Of(Contact(MusouContactKind.Plant, strength: 0.8f), "stone", gpu: true, 0, into));
        Assert.Equal(MusouPuffRules.Grit, into[1].Effect);
        Assert.Equal(2, MusouPuffRules.Of(Contact(MusouContactKind.Landing, strength: 1f), "dirt", gpu: true, 0, into));
        Assert.Equal(1, MusouPuffRules.Of(Contact(MusouContactKind.Landing, strength: 1f, part: MusouContactPart.RightFoot), "dirt", gpu: true, 0, into));   // one grit burst a landing
        Assert.True(into[0].Priority >= 1);
    }

    [Fact]
    public void AlliesAndTheCpuPathKeepEveryOtherStepAndNoPuffOffBodies()
    {
        var into = new MusouPuff[2];
        Assert.Equal(0, MusouPuffRules.Of(Contact(MusouContactKind.Step, own: false), "dirt", gpu: true, 1, into));
        Assert.Equal(1, MusouPuffRules.Of(Contact(MusouContactKind.Step, own: false), "dirt", gpu: true, 2, into));
        Assert.Equal(0, MusouPuffRules.Of(Contact(MusouContactKind.Skid), "dirt", gpu: false, 3, into));
        Assert.Equal(1, MusouPuffRules.Of(Contact(MusouContactKind.Skid), "dirt", gpu: true, 3, into));
        Assert.Equal(2, MusouPuffRules.Of(Contact(MusouContactKind.Plant), "dirt", gpu: false, 3, into));   // plants and landings are never halved
        Assert.Equal(0, MusouPuffRules.Of(Contact(MusouContactKind.Plant), "flesh", gpu: true, 0, into));
        Assert.True(MusouPuffRules.SurfaceDust("metal") < MusouPuffRules.SurfaceDust("dirt"));
    }

    [Fact]
    public void AHopShorterThanTheAirTimeIsNoLanding()
    {
        var t = new MusouContactTracker();
        var into = new List<MusouGroundContact>();
        var b = Standing();
        double now = 0;
        t.Step(1, true, b, true, 0f, true, now, into);
        for (int k = 0; k < 5; k++) { now += Dt; t.Step(1, true, b, false, -1f, true, now, into); }   // 0.08 s
        now += Dt; t.Step(1, true, b, true, 0f, true, now, into);
        Assert.DoesNotContain(into, c => c.Kind == MusouContactKind.Landing);
        Assert.Equal(0.25f, MusouWeightRules.LandingStrength(1f));
        Assert.Equal(0.7f, MusouWeightRules.LandingStrength(9.8f), 2);
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(true, 0.05f)]
    public void TheRetimedStrikeKeepsItsHitAndEndAndNeverRunsBackward(bool heavy, float hold)
    {
        const float hit = 0.42f, end = 1.1f;
        float last = -1f;
        for (float t = 0f; t <= 1.3f; t += 0.002f)
        {
            float c = MusouPunchRules.ClipTime(t, hit, end, heavy, hold);
            Assert.True(c >= last - 1e-5f, $"backward at {t}");
            Assert.True(c - last < 0.02f || last < 0f, $"jump at {t}");   // continuous: no step larger than a frame's worth
            last = c;
        }
        Assert.Equal(0f, MusouPunchRules.ClipTime(0f, hit, end, heavy, hold));
        Assert.Equal(hit, MusouPunchRules.ClipTime(hit, hit, end, heavy, hold), 4);
        Assert.Equal(end, MusouPunchRules.ClipTime(end, hit, end, heavy, hold), 4);
        Assert.Equal(1.2f, MusouPunchRules.ClipTime(1.2f, hit, end, heavy, hold), 4);
        // The wind-up runs slower than the clock, the strike faster.
        float early = MusouPunchRules.ClipTime(0.1f, hit, end, heavy, hold);
        Assert.InRange(early, 0.075f, 0.09f);
        float late = MusouPunchRules.ClipTime(hit - 0.02f, hit, end, heavy, hold);
        Assert.True(hit - late > 0.02f, "the strike's last 20 ms cover more clip than that");
        if (hold > 0f) Assert.Equal(hit, MusouPunchRules.ClipTime(hit + hold * 0.9f, hit, end, heavy, hold), 4);
    }

    [Fact]
    public void OnlyHeavyStrikesAndFinishersThatMeetOrksAreAccented()
    {
        Assert.True(MusouPunchRules.Accented(heavy: true, step: 1, steps: 3, orksMet: 2));
        Assert.True(MusouPunchRules.Accented(heavy: false, step: 3, steps: 3, orksMet: 1));
        Assert.False(MusouPunchRules.Accented(heavy: false, step: 2, steps: 3, orksMet: 5));
        Assert.False(MusouPunchRules.Accented(heavy: true, step: 1, steps: 3, orksMet: 0));
        Assert.InRange(MusouPunchRules.Hold, 0.04f, 0.06f);   // the owner's 40 to 60 ms
        Assert.Equal(0f, MusouPunchRules.PushWeight(1f));
        Assert.True(MusouPunchRules.OrkPush("sm_mp_thunder_hammer") > MusouPunchRules.OrkPush("sm_mp_power_sword"));
        Assert.True(MusouPunchRules.OrkPush("sm_mp_combat_knife") < MusouPunchRules.OrkPush("sm_mp_chainsword"));
    }

    [Fact]
    public void SettlesComeFromLandingsAndPlantsAndEaseInAndOut()
    {
        Assert.Equal(0.14f, MusouWeightRules.SettleOf(Contact(MusouContactKind.Landing, strength: 1f))!.Value.Depth, 3);
        Assert.Null(MusouWeightRules.SettleOf(Contact(MusouContactKind.Landing, part: MusouContactPart.RightFoot)));   // one a landing
        Assert.True(MusouWeightRules.SettleOf(Contact(MusouContactKind.Plant, strength: 0.8f))!.Value.LeanDegrees > 0f);   // the stop leans back
        Assert.True(MusouWeightRules.SettleOf(Contact(MusouContactKind.Plant, strength: 0.6f))!.Value.LeanDegrees < 0f);   // a melee step leans into the blow
        Assert.Equal(0.5f * MusouWeightRules.SettleOf(Contact(MusouContactKind.Landing, strength: 1f))!.Value.Depth,
                     MusouWeightRules.SettleOf(Contact(MusouContactKind.Landing, own: false, strength: 1f))!.Value.Depth, 4);
        Assert.Null(MusouWeightRules.SettleOf(Contact(MusouContactKind.Step)));
        Assert.Equal(1f, MusouWeightRules.SettleWeight(0.06f, 0.06f, 0.25f), 3);
        Assert.Equal(0f, MusouWeightRules.SettleWeight(0.31f, 0.06f, 0.25f), 3);
        Assert.InRange(MusouWeightRules.SettleWeight(0.03f, 0.06f, 0.25f), 0.4f, 0.6f);
    }

    [Fact]
    public void OnlyStanceChangesGetTheLongerCrossFade()
    {
        Assert.Equal(MusouTransitionRules.StanceFloor, MusouTransitionRules.Blend(0.15f, stance: true));
        Assert.Equal(0.4f, MusouTransitionRules.Blend(0.4f, stance: true));
        Assert.Equal(0f, MusouTransitionRules.Blend(0f, stance: true));   // an instant change stays instant
        Assert.Equal(0.1f, MusouTransitionRules.Blend(0.1f, stance: false));   // clip entries keep theirs
    }
}
