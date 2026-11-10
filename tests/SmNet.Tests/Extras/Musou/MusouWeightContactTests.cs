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
}
