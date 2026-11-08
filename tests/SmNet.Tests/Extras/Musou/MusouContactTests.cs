using System.Numerics;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Musou combat feel (owner's brief, 2026-11-09): the predicted contact's ledger, the pilot weapons, the power sword's stroke, the shake that does not stack, the push back and the shot body.</summary>
public class MusouContactTests
{
    [Fact]
    public void A_report_within_the_window_matches_its_predicted_contact_once()
    {
        var l = new MusouContactLedger();
        l.Predict(7, 10.0, melee: true);
        Assert.True(l.Confirm(7, 10.19, melee: true));    // the measured 186 ms
        Assert.False(l.Confirm(7, 10.2, melee: true));    // a second report on that Ork (another hit) shows its own feedback
        Assert.Equal(1, l.Predicted);
        Assert.Equal(1, l.Matched);
        Assert.Equal(0, l.Unconfirmed);
    }

    [Fact]
    public void Two_shots_at_one_ork_are_two_contacts()
    {
        var l = new MusouContactLedger();
        l.Predict(3, 1.0, melee: false);
        l.Predict(3, 1.2, melee: false);
        Assert.True(l.Confirm(3, 1.19, melee: false));
        Assert.True(l.Confirm(3, 1.39, melee: false));
        Assert.Equal(0, l.Pending);
    }

    [Fact]
    public void A_shot_never_takes_a_predicted_strikes_place()
    {
        var l = new MusouContactLedger();
        l.Predict(5, 2.0, melee: true);                 // a strike the server may miss
        Assert.False(l.Confirm(5, 2.1, melee: false));  // an unpredicted shot on that Ork keeps its own feedback
        Assert.True(l.Confirm(5, 2.2, melee: true));
    }

    [Fact]
    public void A_contact_the_server_never_reports_expires_as_unconfirmed()
    {
        var l = new MusouContactLedger();
        l.Predict(4, 0.0, melee: true);
        l.Expire(MusouContactRules.MatchSeconds + 0.01);
        Assert.False(l.Confirm(4, 1.0, melee: true));
        Assert.Equal(1, l.Unconfirmed);
        Assert.False(l.Confirm(9, 1.0, melee: true));   // a report with nothing predicted: the server's own feedback plays
    }

    [Fact]
    public void The_pilot_is_the_power_sword_and_the_bolt_pistol()
    {
        Assert.True(MusouContactRules.PredictsMelee("sm_mp_melee_powersword"));
        Assert.True(MusouContactRules.PredictsMelee("csm_mp_melee_powersword"));
        Assert.False(MusouContactRules.PredictsMelee("sm_mp_melee_chainsword"));
        Assert.False(MusouContactRules.PredictsMelee("sm_mp_melee_2hand_thunder_hammer"));
        Assert.False(MusouContactRules.PredictsMelee(null));
        Assert.True(MusouContactRules.PredictsGun("sm_mp_pistol_bolt_pistol"));
        Assert.False(MusouContactRules.PredictsGun("sm_mp_ranged_bolter"));
        Assert.False(MusouContactRules.PredictsGun("sm_mp_pistol_plasma_pistol"));
    }

    [Fact]
    public void The_power_sword_stroke_is_faster_thinner_and_brighter_and_the_rest_keep_theirs()
    {
        var sword = MusouContactRules.SlashOf("sm_mp_melee_powersword");
        var chain = MusouContactRules.SlashOf("sm_mp_melee_chainsword");
        Assert.Equal(MusouContactRules.DefaultSlash, chain);
        Assert.Equal(MusouSlashRules.Seconds, chain.Seconds);
        Assert.True(sword.Seconds < chain.Seconds);
        Assert.True(sword.Width < chain.Width);
        Assert.True(sword.Brightness > chain.Brightness);
    }

    [Fact]
    public void Shakes_do_not_stack_past_the_strongest_plus_a_share_and_the_cap()
    {
        Assert.Equal(0.02f, MusouContactRules.CombineShake(0f, 0.02f), 5);
        Assert.Equal(0.02f + 0.01f * MusouContactRules.ShakeOthersShare, MusouContactRules.CombineShake(0.02f, 0.01f), 5);
        Assert.Equal(0.03f + 0.02f * MusouContactRules.ShakeOthersShare, MusouContactRules.CombineShake(0.02f, 0.03f), 5);
        float total = 0f;
        for (int i = 0; i < 20; i++) total = MusouContactRules.CombineShake(total, 0.0324f);   // twenty frags at once
        Assert.Equal(MusouContactRules.ShakeCap, total, 5);
        Assert.Equal(0.05f, MusouContactRules.CombineShake(0.05f, 0.01f), 5);   // never drops what is already shaking
        Assert.Equal(0.01f, MusouContactRules.CombineShake(0.01f, 0f), 5);
    }

    [Fact]
    public void The_push_goes_out_and_comes_back_within_its_time()
    {
        Assert.Equal(0f, MusouContactRules.RecoilAt(0f, 0.2f), 5);
        float peak = 0f;
        for (float t = 0f; t < MusouContactRules.RecoilSeconds; t += 0.005f) peak = System.MathF.Max(peak, MusouContactRules.RecoilAt(t, 0.2f));
        Assert.InRange(peak, 0.19f, 0.2001f);
        Assert.Equal(0f, MusouContactRules.RecoilAt(MusouContactRules.RecoilSeconds, 0.2f), 5);
        Assert.True(MusouContactRules.RecoilAt(MusouContactRules.RecoilSeconds * 0.9f, 0.2f) < 0.1f);
    }

    [Fact]
    public void The_rank_and_file_are_pushed_and_the_armoured_and_big_ones_are_not()
    {
        Assert.True(MusouContactRules.Recoils(MusouEnemyType.Boy));
        Assert.True(MusouContactRules.Recoils(MusouEnemyType.ShootaBoy));
        Assert.True(MusouContactRules.Recoils(MusouEnemyType.Gretchin));
        foreach (var t in new[] { MusouEnemyType.ArdBoy, MusouEnemyType.Nob, MusouEnemyType.ChaosMarine, MusouEnemyType.Warboss, MusouEnemyType.BossBoy, MusouEnemyType.BossNob, MusouEnemyType.BigShootaNob, MusouEnemyType.Weirdboy })
            Assert.False(MusouContactRules.Recoils(t));
    }

    [Fact]
    public void The_predicted_shot_meets_the_servers_body_shape()
    {
        var rules = MusouRules.Default;
        var (radius, height) = MusouSim.ShotBodyOf(rules, MusouEnemyType.Boy);
        Assert.Equal(2.15f, height, 3);
        var feet = new Vector3(0f, 10f, 0f);
        // Level at the chest from 10 m short: the cylinder's side, radius in front of the axis.
        var hit = MusouContactRules.RayBody(new Vector3(0f, 0f, 1.2f), Vector3.UnitY, feet, radius, height, 100f, out bool head);
        Assert.NotNull(hit);
        Assert.False(head);
        Assert.Equal(10f - radius, hit!.Value, 3);
        // Above the body cylinder's top: the head sphere.
        float headRadius = 0.22f * height / 1.9f;
        var top = MusouContactRules.RayBody(new Vector3(0f, 0f, height - headRadius * 0.4f), Vector3.UnitY, feet, radius, height, 100f, out head);
        Assert.NotNull(top);
        Assert.True(head);
        // Wide of the body, or past the range: none.
        Assert.Null(MusouContactRules.RayBody(new Vector3(radius + 0.3f, 0f, 1.2f), Vector3.UnitY, feet, radius, height, 100f, out _));
        Assert.Null(MusouContactRules.RayBody(new Vector3(0f, 0f, 1.2f), Vector3.UnitY, feet, radius, height, 5f, out _));
    }
}
