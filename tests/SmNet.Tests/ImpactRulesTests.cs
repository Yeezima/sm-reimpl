using System.Numerics;
using SmClient.ImpactRules;

namespace SmNet.Tests;

/// <summary>fx/impact-pass: the impact pass's engine-free rules (client/ImpactRules): the default look, the thrust trail along its path, the sword flare and the melee hit gate.</summary>
public class ImpactRulesTests
{
    [Fact]
    public void The_remaster_is_the_default_look_and_current_none_and_classic_name_no_preset()
    {
        Assert.Equal("remaster", LookChoice.PresetOf(null));
        Assert.Null(LookChoice.PresetOf("current"));
        Assert.Null(LookChoice.PresetOf("None"));
        Assert.Null(LookChoice.PresetOf("classic"));
        Assert.Equal("forge", LookChoice.PresetOf("forge"));
        Assert.Equal("remaster", LookChoice.PresetOf("remaster"));
        Assert.True(LookChoice.IsNone("CURRENT"));
        Assert.False(LookChoice.IsNone("sm1"));
    }

    static void Trail(Vector3[] path, float metres, Vector3[] pts) => TrailPath.ByDistance(path.Length, i => path[i], metres, pts);

    [Fact]
    public void A_trail_lies_along_the_last_metres_of_the_path_from_its_newest_point()
    {
        // A straight path along +X, 1 m apart, newest last (x = 10).
        var path = Enumerable.Range(0, 11).Select(i => new Vector3(i, 0, 0)).ToArray();
        var pts = new Vector3[5];
        Trail(path, 4f, pts);
        Assert.Equal([10f, 9f, 8f, 7f, 6f], pts.Select(p => MathF.Round(p.X, 4)));
    }

    [Fact]
    public void A_path_shorter_than_the_trail_ends_at_its_oldest_point()
    {
        var path = new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 2.5f, 0) };
        var pts = new Vector3[6];
        Trail(path, 10f, pts);
        Assert.Equal(new Vector3(0, 2.5f, 0), pts[0]);
        Assert.Equal(new Vector3(0, 0.5f, 0), pts[1]);   // 2 m back along the path
        Assert.All(pts[2..], p => Assert.Equal(Vector3.Zero, p));
    }

    [Fact]
    public void A_stationary_path_draws_no_trail_and_an_empty_one_is_left_alone()
    {
        var still = Enumerable.Repeat(new Vector3(3, 4, 5), 6).ToArray();
        var pts = new Vector3[4];
        Trail(still, 3f, pts);
        Assert.All(pts, p => Assert.Equal(new Vector3(3, 4, 5), p));
        var untouched = new Vector3[3];
        TrailPath.ByDistance(0, _ => Vector3.One, 3f, untouched);
        Assert.All(untouched, p => Assert.Equal(Vector3.Zero, p));
    }

    [Fact]
    public void The_sword_flare_is_full_at_the_hit_and_gone_after_its_time()
    {
        Assert.Equal(1f, SwordFlare.At(0f));
        Assert.Equal(0.25f, SwordFlare.At(SwordFlare.Seconds / 2f), 4);
        Assert.Equal(0f, SwordFlare.At(SwordFlare.Seconds));
        Assert.Equal(0f, SwordFlare.At(-0.1f));
        Assert.Equal(0f, SwordFlare.At(float.NaN));
    }

    [Fact]
    public void A_hit_time_fires_once_per_attack_even_through_a_rewind()
    {
        var gate = new MeleeHitGate();
        gate.Idle(1);
        Assert.False(gate.Passed(1, 1, false, 0.1f, 0.3f, 100));
        Assert.True(gate.Passed(1, 1, false, 0.33f, 0.3f, 100));
        // A reconcile takes the attack back before its hit, then forward past it again: the same attack, no second effect.
        Assert.False(gate.Passed(1, 1, false, 0.2f, 0.3f, 100));
        Assert.False(gate.Passed(1, 1, false, 0.4f, 0.3f, 100));
        // A rewind into "no attack" and back.
        gate.Idle(1);
        Assert.False(gate.Passed(1, 1, false, 0.5f, 0.3f, 101));
        // The next attack (same step, a later start) fires.
        gate.Idle(1);
        Assert.False(gate.Passed(1, 1, false, 0.05f, 0.3f, 130));
        Assert.True(gate.Passed(1, 1, false, 0.3f, 0.3f, 130));
        // The next step of the combo fires too.
        Assert.False(gate.Passed(1, 2, false, 0.1f, 0.25f, 140));
        Assert.True(gate.Passed(1, 2, false, 0.26f, 0.25f, 140));
    }

    [Fact]
    public void A_player_first_seen_mid_attack_does_not_fire_for_a_hit_already_passed()
    {
        var gate = new MeleeHitGate();
        Assert.False(gate.Passed(7, 1, true, 0.6f, 0.4f, 50));
        Assert.False(gate.Passed(7, 1, true, 0.7f, 0.4f, 50));
        // First seen before the hit: it fires when the hit time passes.
        Assert.False(gate.Passed(8, 1, false, 0.1f, 0.4f, 60));
        Assert.True(gate.Passed(8, 1, false, 0.45f, 0.4f, 60));
    }

    [Fact]
    public void Start_ticks_count_back_from_the_tick_and_players_who_left_are_forgotten()
    {
        Assert.Equal(91L, MeleeHitGate.StartTick(100, 0.3f, 1f / 30f));
        var gate = new MeleeHitGate();
        gate.Idle(1); gate.Idle(2);
        gate.Prune(id => id == 2);
        Assert.Equal(1, gate.Count);
        // Forgotten: player 1 counts as first seen again.
        Assert.False(gate.Passed(1, 1, false, 0.5f, 0.3f, 10));
    }

    [Fact]
    public void A_stalled_drawn_clock_still_fires_once_when_the_state_reaches_the_hit_and_a_stale_time_does_not_fire_early()
    {
        const float fx = 0.735f, hit = 0.767f;
        var gate = new MeleeHitGate();
        gate.Idle(1);
        // First stomp frame with the kick's stale drawn time (1.0 s) while the state is at 0.03 s: no early fire.
        Assert.False(gate.Passed(1, 1, true, MeleeHitGate.EffectTime(1.0f, 0.03f, fx, hit), fx, 100));
        // The drawn clock stalls at 0.2 s; the state passes the hit: fires once, then never again for that attack.
        Assert.False(gate.Passed(1, 1, true, MeleeHitGate.EffectTime(0.2f, 0.7f, fx, hit), fx, 100));
        Assert.True(gate.Passed(1, 1, true, MeleeHitGate.EffectTime(0.2f, 0.8f, fx, hit), fx, 100));
        Assert.False(gate.Passed(1, 1, true, MeleeHitGate.EffectTime(0.74f, 0.83f, fx, hit), fx, 100));
        // A normal drawn clock reaching the landing first fires at it, and the state's later hit time does not fire a second time.
        gate.Idle(1);
        Assert.False(gate.Passed(1, 1, true, MeleeHitGate.EffectTime(0.5f, 0.53f, fx, hit), fx, 130));
        Assert.True(gate.Passed(1, 1, true, MeleeHitGate.EffectTime(0.736f, 0.77f, fx, hit), fx, 130));
        Assert.False(gate.Passed(1, 1, true, MeleeHitGate.EffectTime(0.8f, 0.83f, fx, hit), fx, 130));
    }
}

/// <summary>fx/power-flash: the power weapons' hit flash rules (client/ImpactRules/PowerFlashRules.cs).</summary>
public class PowerFlashRulesTests
{
    [Theory]
    [InlineData("sm_mp_melee_2hand_thunder_hammer", PowerKind.Hammer)]
    [InlineData("csm_mp_melee_2hand_power_mace", PowerKind.Maul)]
    [InlineData("sm_mp_melee_power_axe", PowerKind.Axe)]
    [InlineData("sm_mp_melee_powersword", PowerKind.Sword)]
    [InlineData("csm_mp_melee_powersword", PowerKind.Sword)]
    public void Each_power_wargear_names_its_weapon(string wargear, PowerKind kind) => Assert.Equal(kind, PowerFlashRules.KindOf(wargear));

    [Theory]
    [InlineData("sm_mp_melee_chainsword")]
    [InlineData("csm_mp_melee_chain_axe")]
    [InlineData("sm_mp_melee_combat_knife")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_melee_weapons_do_not_flash(string? wargear) => Assert.Null(PowerFlashRules.KindOf(wargear));

    [Fact]
    public void A_swing_gets_one_big_burst_then_five_small_then_none_and_the_next_swing_starts_again()
    {
        var b = new PowerFlashRules.Budget();
        Assert.Equal(PowerBurst.Lead, b.Take(1, 10.0));
        for (int i = 0; i < PowerFlashRules.SmallPerSwing; i++) Assert.Equal(PowerBurst.Small, b.Take(1, 10.0 + 0.01 * i));
        for (int i = 0; i < 14; i++) Assert.Equal(PowerBurst.None, b.Take(1, 10.1));   // the 20-enemy cleave: 1 + 5 + 14
        Assert.Equal(PowerBurst.Lead, b.Take(1, 10.0 + PowerFlashRules.SwingSeconds + 0.1));
    }

    [Fact]
    public void Another_attackers_swing_has_its_own_budget()
    {
        var b = new PowerFlashRules.Budget();
        Assert.Equal(PowerBurst.Lead, b.Take(1, 5.0));
        Assert.Equal(PowerBurst.Lead, b.Take(2, 5.0));
        Assert.Equal(PowerBurst.Small, b.Take(1, 5.1));
    }

    [Fact]
    public void A_camera_close_to_the_burst_sees_a_smaller_one_and_a_far_one_the_whole()
    {
        Assert.Equal(PowerFlashRules.CloseMin, PowerFlashRules.CloseFactor(0f));
        Assert.Equal(PowerFlashRules.CloseMin, PowerFlashRules.CloseFactor(PowerFlashRules.CloseNear));
        Assert.Equal(1f, PowerFlashRules.CloseFactor(PowerFlashRules.CloseFar));
        Assert.Equal(1f, PowerFlashRules.CloseFactor(30f));
        float prev = 0f;
        for (float d = 0f; d <= 6f; d += 0.25f) { float f = PowerFlashRules.CloseFactor(d); Assert.InRange(f, PowerFlashRules.CloseMin, 1f); Assert.True(f >= prev); prev = f; }
        Assert.False(float.IsNaN(PowerFlashRules.CloseFactor(float.NaN)));
    }
}
