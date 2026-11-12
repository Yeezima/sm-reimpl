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

public class ArmourBreakRingTests
{
    [Fact]
    public void A_report_of_no_armour_flashes_at_once_and_only_once()
    {
        var t = new ArmourFlashTracker();
        t.Hit(2, 0.0, armourUp: true, regenDelay: 7f);
        t.Broke(2, 1.0, 7f);
        Assert.True(t.At(2, 1.0).Break > 0.9f);
        Assert.False(t.BreakRing(2, 1.0));          // pvp-hit-fx: the engulfing glow by default
        ArmourFlashTracker.Ring = true;              // --old-break-ring
        try { Assert.True(t.BreakRing(2, 1.0)); } finally { ArmourFlashTracker.Ring = false; }
        Assert.True(t.TryTakeBreak(out _));
        t.Broke(2, 2.5, 7f);                       // still down: no new break
        Assert.False(t.BreakRing(2, 2.5));
        Assert.False(t.TryTakeBreak(out _));
    }
}

/// <summary>fx/melee-hit-sound: the procedural on-hit sounds (client/ImpactRules/MeleeHitSynth.cs).</summary>
public class MeleeHitSynthTests
{
    static double RmsDb(float[] x, float a, float b)
    {
        int i0 = (int)(a * MeleeHitSynth.SampleRate), i1 = Math.Min(x.Length, (int)(b * MeleeHitSynth.SampleRate));
        double s = 0; for (int i = i0; i < i1; i++) s += x[i] * x[i];
        return 10 * Math.Log10(s / Math.Max(1, i1 - i0) + 1e-12);
    }

    [Fact]
    public void Test_aid_renders_the_sounds_to_wav_files_when_a_folder_is_named()
    {
        // SM_RENDER_HIT_DIR=<folder> dotnet test --filter MeleeHitSynthTests: writes a_<seed>.wav and b_<seed>.wav for measuring against the references (no assertion without the variable).
        var dir = Environment.GetEnvironmentVariable("SM_RENDER_HIT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var (sound, tag) in new[] { (HitSound.Sizzle, "a"), (HitSound.Roar, "b") })
            for (int seed = 1; seed <= 3; seed++)
            {
                var pcm = MeleeHitSynth.ToPcm16(MeleeHitSynth.Render(sound, seed));
                using var w = new BinaryWriter(File.Create(Path.Combine(dir, $"{tag}_{seed}.wav")));
                w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(MeleeHitSynth.SampleRate); w.Write(MeleeHitSynth.SampleRate * 2); w.Write((short)2); w.Write((short)16);
                w.Write("data"u8); w.Write(pcm.Length * 2); foreach (var s in pcm) w.Write(s);
            }
    }

    [Theory]
    [InlineData(HitSound.Sizzle)]
    [InlineData(HitSound.Roar)]
    public void A_render_is_finite_quiet_at_its_ends_and_at_the_target_level(HitSound sound)
    {
        var x = MeleeHitSynth.Render(sound, 5);
        Assert.Equal((int)(MeleeHitSynth.Seconds(sound) * MeleeHitSynth.SampleRate), x.Length);
        Assert.All(x, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1f));
        Assert.InRange(RmsDb(x, 0f, MeleeHitSynth.Seconds(sound)), 20 * Math.Log10(MeleeHitSynth.TargetRms(sound)) - 0.5, 20 * Math.Log10(MeleeHitSynth.TargetRms(sound)) + 0.5);
        Assert.True(Math.Abs(x[^1]) < 0.02f);
    }

    [Theory]
    [InlineData(HitSound.Sizzle)]
    [InlineData(HitSound.Roar)]
    public void A_hit_peaks_at_once_and_is_all_bass(HitSound sound)
    {
        var x = MeleeHitSynth.Render(sound, 1);
        Assert.True(RmsDb(x, 0f, 0.05f) > RmsDb(x, 0.2f, 0.4f) + 3);     // loud at the contact, with no swell
        double lp = 0, lpEnergy = 0, hpEnergy = 0, a = 1 - Math.Exp(-2 * Math.PI * 400.0 / MeleeHitSynth.SampleRate);
        foreach (var v in x) { lp += a * (v - lp); lpEnergy += lp * lp; hpEnergy += (v - lp) * (v - lp); }
        Assert.True(lpEnergy > 4 * hpEnergy, $"low {lpEnergy}, rest {hpEnergy}");
    }

    [Theory]
    [InlineData(HitSound.Sizzle)]
    [InlineData(HitSound.Roar)]
    public void Musous_render_has_no_bass_left_at_the_same_length_and_within_full_scale(HitSound sound)
    {
        var x = MeleeHitSynth.Render(sound, 1, noBass: true);
        Assert.Equal(MeleeHitSynth.Render(sound, 1).Length, x.Length);
        Assert.All(x, v => Assert.True(float.IsFinite(v) && Math.Abs(v) <= 1f));
        double Low(float[] y)
        {
            double a = 1 - Math.Exp(-2 * Math.PI * 150.0 / MeleeHitSynth.SampleRate), p = 0, q = 0, low = 0, all = 0;
            foreach (var v in y) { p += a * (v - p); q += a * (p - q); low += q * q; all += v * v; }
            return low / all;
        }
        Assert.True(Low(x) < 0.08, $"{Low(x)} of the energy under 150 Hz");
        Assert.True(Low(MeleeHitSynth.Render(sound, 1)) > 0.4);
    }

    [Fact]
    public void Another_seed_is_another_noise_and_the_same_seed_is_the_same()
    {
        Assert.NotEqual(MeleeHitSynth.Render(HitSound.Sizzle, 1)[5000], MeleeHitSynth.Render(HitSound.Sizzle, 2)[5000]);
        Assert.Equal(MeleeHitSynth.Render(HitSound.Roar, 3)[7000], MeleeHitSynth.Render(HitSound.Roar, 3)[7000]);
    }

    [Fact]
    public void The_variation_stays_within_its_spreads_and_differs_between_hits()
    {
        var seen = new HashSet<float>();
        for (uint i = 0; i < 200; i++)
        {
            var (pitch, db) = MeleeHitSynth.Variation(i);
            Assert.InRange(pitch, 1f - MeleeHitSynth.PitchSpread, 1f + MeleeHitSynth.PitchSpread);
            Assert.InRange(db, -MeleeHitSynth.LevelSpreadDb, MeleeHitSynth.LevelSpreadDb);
            seen.Add(pitch);
        }
        Assert.True(seen.Count > 150);
    }

    [Theory]
    [InlineData("sm_mp_melee_powersword", HitSound.Sizzle)]
    [InlineData("csm_mp_melee_powersword", HitSound.Sizzle)]
    [InlineData("sm_mp_melee_power_axe", HitSound.Sizzle)]
    [InlineData("sm_mp_melee_2hand_thunder_hammer", HitSound.Roar)]
    [InlineData("csm_mp_melee_2hand_power_mace", HitSound.Roar)]
    public void Each_replaced_weapon_gets_its_sound(string wargear, HitSound expected) => Assert.Equal(expected, MeleeHitSynth.Replaces(wargear));

    [Theory]
    [InlineData("csm_mp_melee_chain_axe")]
    [InlineData("sm_mp_melee_combat_knife")]
    [InlineData("csm_mp_melee_combat_knife")]
    [InlineData("sm_mp_melee_chainsword")]
    [InlineData("csm_mp_melee_chainsword_gold")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_weapons_keep_their_own(string? wargear) => Assert.Null(MeleeHitSynth.Replaces(wargear));
}

/// <summary>fix/melta-burn-onhit: the melta's whole-body burn starts on the hit, not on death.</summary>
public class MeltaBurnRulesTests
{
    [Fact]
    public void A_melta_body_hit_on_another_player_starts_the_burn_at_once()
        => Assert.True(MeltaBurnRules.Due(MeltaBurnRules.IsMelta("wargear_sm_meltagun"), bodyHit: true, victim: 3, ownId: 1, now: 10.0, lastAt: double.NegativeInfinity));

    [Theory]
    [InlineData(false, true, 3, 1, 10.0, -1e9)]   // not a melta
    [InlineData(true, false, 3, 1, 10.0, -1e9)]   // a wall
    [InlineData(true, true, -1, 1, 10.0, -1e9)]   // no victim
    [InlineData(true, true, 1, 1, 10.0, -1e9)]    // the own body
    [InlineData(true, true, 3, 1, 10.1, 10.0)]    // inside the gap
    public void Other_events_do_not_start_it(bool melta, bool body, int victim, int own, double now, double last)
        => Assert.False(MeltaBurnRules.Due(melta, body, victim, own, now, last));

    [Fact]
    public void It_starts_again_after_the_gap_and_covers_head_to_feet_and_arms()
    {
        Assert.True(MeltaBurnRules.Due(true, true, 3, 1, 10.3, 10.0));
        Assert.Contains("rig_head", MeltaBurnRules.Bones);
        Assert.Contains("rig_rfoot", MeltaBurnRules.Bones);
        Assert.Contains("rig_lupperarm", MeltaBurnRules.Bones);
        Assert.Contains("rig_rhand", MeltaBurnRules.Bones);
        Assert.True(MeltaBurnRules.IsMelta("wargear_csm_meltagun") && !MeltaBurnRules.IsMelta("wargear_sm_bolter"));
    }
}

/// <summary>fix/hit-decals-map-only (owner, 2026-11-08): a ranged shot that hits a player leaves no decal; one that strikes the map does.</summary>
public class MeltaKillAttributionTests
{
    static readonly string[] Names = ["wargear_sm_bolter", "wargear_sm_meltagun", "wargear_csm_meltagun"];

    [Theory]
    [InlineData(SmSim.KillCause.Weapon, 1, true)]
    [InlineData(SmSim.KillCause.Weapon, 2, true)]
    [InlineData(SmSim.KillCause.Weapon, 0, false)]    // another gun
    [InlineData(SmSim.KillCause.Weapon, 3, false)]    // index out of range
    [InlineData(SmSim.KillCause.Weapon, -1, false)]
    [InlineData(SmSim.KillCause.Thrown, 1, false)]    // the melta bomb is a thrown item: its index is into the thrown list, never the weapons
    [InlineData(SmSim.KillCause.Melee, 1, false)]
    [InlineData(SmSim.KillCause.JumpPack, 1, false)]
    [InlineData(SmSim.KillCause.Perk, 1, false)]
    [InlineData(SmSim.KillCause.World, 1, false)]
    public void Only_a_weapon_kill_by_a_melta_burns(SmSim.KillCause cause, int index, bool burns) => Assert.Equal(burns, MeltaBurnRules.KillBurns(cause, index, Names));

    [Theory]
    [InlineData(true, "wargear_sm_meltagun", true)]
    [InlineData(true, "wargear_csm_meltagun", true)]
    [InlineData(true, "wargear_sm_bolter", false)]
    [InlineData(true, null, false)]
    [InlineData(false, "wargear_sm_meltagun", false)]   // a blast, a melee or a barrage death
    public void The_crowds_burn_needs_a_shot_and_a_melta_in_the_killers_hand(bool shot, string? inHand, bool burns) => Assert.Equal(burns, MeltaBurnRules.CrowdKillBurns(shot, inHand));
}

public class HitDecalRulesTests
{
    [Theory]
    [InlineData(SmSim.ShotHit.Body, 3, false)]
    [InlineData(SmSim.ShotHit.Head, 7, false)]
    [InlineData(SmSim.ShotHit.World, -1, true)]
    [InlineData(SmSim.ShotHit.Body, -1, true)]   // a Musou enemy (no player id): unchanged
    public void Player_hit_draws_no_decal_world_hit_does(SmSim.ShotHit hit, int victim, bool decal) =>
        Assert.Equal(decal, HitDecalRules.DrawsContainer(hit, victim));
}

/// <summary>classic-no-player-hit-fx (owner, 2026-11-11): Classic PvP draws no on-hit effects on a player a shot struck; Musou Mode and the restore flag draw them all.</summary>
public class PlayerHitFxRulesTests
{
    const SmSim.ContentExtras Faithful = SmSim.ContentExtras.None;
    const SmSim.ContentExtras Extended = SmSim.ContentExtras.Orks | SmSim.ContentExtras.Gore | SmSim.ContentExtras.LoyalistBarks;
    const SmSim.ContentExtras Musou = SmSim.ContentExtras.Musou;

    [Theory]
    [InlineData(SmSim.ShotHit.Body, Faithful)]
    [InlineData(SmSim.ShotHit.Head, Faithful)]
    [InlineData(SmSim.ShotHit.Body, Extended)]   // Annihilation or Seize Ground with added content (the Ork Boy) is Classic too
    [InlineData(SmSim.ShotHit.Head, Extended)]
    public void Classic_draws_no_hit_effects_on_a_player(SmSim.ShotHit hit, SmSim.ContentExtras extras)
    {
        Assert.True(PlayerHitFxRules.Classic(extras));
        Assert.False(PlayerHitFxRules.DrawsHitFx(hit, extras, restore: false));
    }

    [Theory]
    [InlineData(SmSim.ShotHit.Body)]
    [InlineData(SmSim.ShotHit.Head)]
    [InlineData(SmSim.ShotHit.World)]
    public void Musou_draws_them_all(SmSim.ShotHit hit)
    {
        Assert.False(PlayerHitFxRules.Classic(Musou));
        Assert.True(PlayerHitFxRules.DrawsHitFx(hit, Musou, restore: false));
        Assert.True(PlayerHitFxRules.DrawsBreakFx(melee: false, Musou, restore: false));
    }

    [Theory]
    [InlineData(Faithful)]
    [InlineData(Extended)]
    [InlineData(Musou)]
    public void A_shot_on_the_map_keeps_its_effects_in_every_mode(SmSim.ContentExtras extras)
    {
        Assert.True(PlayerHitFxRules.DrawsHitFx(SmSim.ShotHit.World, extras, restore: false));
        Assert.True(PlayerHitFxRules.DrawsHitFx(SmSim.ShotHit.None, extras, restore: false));
    }

    [Theory]
    [InlineData(SmSim.ShotHit.Body, Faithful)]
    [InlineData(SmSim.ShotHit.Head, Extended)]
    public void The_restore_flag_brings_them_back_in_Classic(SmSim.ShotHit hit, SmSim.ContentExtras extras)
    {
        Assert.Equal("player-hit-fx", PlayerHitFxRules.RestoreFlag);
        Assert.Contains(PlayerHitFxRules.RestoreFlag, SmNet.LeanBot.PresentationAids);   // a bot given it loads the presentation, like the other before / after switches
        Assert.True(PlayerHitFxRules.DrawsHitFx(hit, extras, restore: true));
        Assert.True(PlayerHitFxRules.DrawsBreakFx(melee: false, extras, restore: true));
    }

    [Fact]
    public void A_break_a_shot_caused_plays_its_break_effect_in_Classic_again_the_old_look_switch_drops_it()
    {
        // owner's answers 2026-11-11: break particles appear when a shot breaks armour.
        Assert.True(PlayerHitFxRules.DrawsBreakFx(melee: false, Faithful, restore: false));
        Assert.True(PlayerHitFxRules.DrawsBreakFx(melee: false, Extended, restore: false));
        Assert.Equal("old-classic-hit-fx", PlayerHitFxRules.OldLookFlag);
        Assert.Contains(PlayerHitFxRules.OldLookFlag, SmNet.LeanBot.PresentationAids);
        Assert.False(PlayerHitFxRules.DrawsBreakFx(melee: false, Faithful, restore: false, oldLook: true));
        Assert.True(PlayerHitFxRules.DrawsBreakFx(melee: true, Faithful, restore: false, oldLook: true));
    }

    [Fact]
    public void The_bare_armour_model_flash_is_back_in_Classic_not_with_the_old_look_or_the_burst_restore()
    {
        Assert.True(PlayerHitFxRules.BareHitFlash(Faithful, restore: false, oldLook: false, oldHealthHit: false));
        Assert.True(PlayerHitFxRules.BareHitFlash(Extended, restore: false, oldLook: false, oldHealthHit: false));
        Assert.False(PlayerHitFxRules.BareHitFlash(Musou, restore: false, oldLook: false, oldHealthHit: false));   // Musou keeps pvp-hit-fx's burst instead
        Assert.False(PlayerHitFxRules.BareHitFlash(Faithful, restore: true, oldLook: false, oldHealthHit: false));
        Assert.False(PlayerHitFxRules.BareHitFlash(Faithful, restore: false, oldLook: true, oldHealthHit: false));
        Assert.True(PlayerHitFxRules.BareHitFlash(Musou, restore: false, oldLook: false, oldHealthHit: true));   // --old-health-hit: the old re-flash everywhere
    }

    [Fact]
    public void The_new_presentation_switches_load_the_presentation_for_a_bot()
    {
        Assert.Contains("melta-burn-on-hit", SmNet.LeanBot.PresentationAids);
        Assert.Contains("old-pack-death-flash", SmNet.LeanBot.PresentationAids);
    }

    [Fact]
    public void A_classic_body_hit_is_bare_whatever_the_victim_id_while_the_map_rule_is_unchanged()
    {
        // HitDecalRules keeps drawing the container for a body hit with no player id (a Musou enemy); the Classic rule stops it first for any body or head hit.
        Assert.True(HitDecalRules.DrawsContainer(SmSim.ShotHit.Body, -1));
        Assert.False(PlayerHitFxRules.DrawsHitFx(SmSim.ShotHit.Body, Faithful, restore: false));
        Assert.True(PlayerHitFxRules.OnPlayer(SmSim.ShotHit.Head) && !PlayerHitFxRules.OnPlayer(SmSim.ShotHit.World) && !PlayerHitFxRules.OnPlayer(SmSim.ShotHit.None));
    }
}

/// <summary>onhit-range-intensity (owner, 2026-11-13): the hit effects keep their size on screen at range and are 30 % stronger.</summary>
public class HitFxScaleRulesTests
{
    [Fact]
    public void At_the_reference_distance_and_view_nothing_grows_and_the_intensity_is_thirty_percent()
    {
        Assert.Equal(1f, HitFxScaleRules.RangeScale(HitFxScaleRules.RefDistance, HitFxScaleRules.RefFov, HitFxScaleRules.MaxSpot), 3);
        Assert.Equal(1f, HitFxScaleRules.RangeScale(5f, 70f, 6f));   // closer than the reference: never smaller than the data's size
        Assert.Equal(1.3f, HitFxScaleRules.Intensity);
        Assert.Equal(1.3f, HitFxScaleRules.ParticleScale(12f, 70f, oldRange: false, oldIntensity: false), 3);
    }

    [Fact]
    public void A_hip_view_grows_with_distance_up_to_each_cap_and_a_scope_is_already_magnified()
    {
        float at40 = HitFxScaleRules.RangeScale(40f, 70f, HitFxScaleRules.MaxSpot), at80 = HitFxScaleRules.RangeScale(80f, 70f, HitFxScaleRules.MaxSpot);
        Assert.Equal(40f / 12f, at40, 2);
        Assert.Equal(HitFxScaleRules.MaxSpot, HitFxScaleRules.RangeScale(500f, 70f, HitFxScaleRules.MaxSpot));
        Assert.True(at80 > at40);
        Assert.Equal(HitFxScaleRules.MaxGlow, HitFxScaleRules.RangeScale(80f, 70f, HitFxScaleRules.MaxGlow));
        Assert.True(HitFxScaleRules.RangeScale(80f, 10f, HitFxScaleRules.MaxSpot) < at80 / 4f);   // the Stalker's 10 degrees at 80 m
    }

    [Fact]
    public void The_restore_switches_each_remove_their_part_and_nonsense_views_change_nothing()
    {
        Assert.Equal(1.3f, HitFxScaleRules.ParticleScale(80f, 70f, oldRange: true, oldIntensity: false), 3);
        Assert.Equal(HitFxScaleRules.MaxParticle, HitFxScaleRules.ParticleScale(80f, 70f, oldRange: false, oldIntensity: true), 3);
        Assert.Equal(1f, HitFxScaleRules.ParticleScale(80f, 70f, oldRange: true, oldIntensity: true));
        Assert.Equal(1f, HitFxScaleRules.RangeScale(float.NaN, 70f, 6f));
        Assert.Equal(1f, HitFxScaleRules.RangeScale(80f, 0f, 6f));
    }

    [Fact]
    public void The_caps_hold_for_the_spot_the_particles_and_the_glow_card_and_the_old_switches_give_one()
    {
        Assert.Equal(6f, HitFxScaleRules.RangeScale(1000f, 70f, HitFxScaleRules.MaxSpot));
        Assert.Equal(3f, HitFxScaleRules.RangeScale(1000f, 70f, HitFxScaleRules.MaxParticle));
        Assert.Equal(1.5f, HitFxScaleRules.RangeScale(1000f, 70f, HitFxScaleRules.MaxGlow));
        Assert.Equal(3f * 1.3f, HitFxScaleRules.ParticleScale(1000f, 70f, false, false), 3);
        Assert.Equal(1f, HitFxScaleRules.ParticleScale(1000f, 70f, oldRange: true, oldIntensity: true));
        Assert.Equal(1f, HitFxScaleRules.ParticleScale(0f, 70f, oldRange: false, oldIntensity: true));
    }
}

/// <summary>onhit-range-intensity (owner 2026-11-13): the look of a hit on a player by damage kind and mode: Classic gets the glows and sparks, never the large burst; Musou keeps the burst on ranged hits.</summary>
public class HitLookRulesTests
{
    const SmSim.ContentExtras Faithful = SmSim.ContentExtras.None;
    const SmSim.ContentExtras Extended = SmSim.ContentExtras.Orks | SmSim.ContentExtras.Gore | SmSim.ContentExtras.LoyalistBarks;
    const SmSim.ContentExtras Musou = SmSim.ContentExtras.Musou;
    const PlayerHitFxRules.HitLook GlowsSparks = PlayerHitFxRules.HitLook.Glows | PlayerHitFxRules.HitLook.Sparks;

    [Theory]
    [InlineData(PlayerHitFxRules.DamageKind.Ranged, Faithful)]
    [InlineData(PlayerHitFxRules.DamageKind.Ranged, Extended)]
    [InlineData(PlayerHitFxRules.DamageKind.Melee, Faithful)]
    [InlineData(PlayerHitFxRules.DamageKind.Charge, Faithful)]
    [InlineData(PlayerHitFxRules.DamageKind.Pound, Faithful)]
    [InlineData(PlayerHitFxRules.DamageKind.Slash, Extended)]
    [InlineData(PlayerHitFxRules.DamageKind.Blast, Faithful)]
    public void Classic_shows_the_glows_and_sparks_for_every_damage_and_never_the_large_burst(PlayerHitFxRules.DamageKind kind, SmSim.ContentExtras extras) =>
        Assert.Equal(GlowsSparks, PlayerHitFxRules.LookFor(kind, extras, particles: true, burstRestore: false));

    [Fact]
    public void Musou_keeps_the_large_burst_on_ranged_hits_only()
    {
        Assert.Equal(GlowsSparks | PlayerHitFxRules.HitLook.Burst, PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Ranged, Musou, true, false));
        Assert.Equal(GlowsSparks, PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Melee, Musou, true, false));
        Assert.Equal(GlowsSparks, PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Blast, Musou, true, false));
    }

    [Fact]
    public void The_restore_switches_each_change_only_their_part()
    {
        Assert.Equal(PlayerHitFxRules.HitLook.Glows, PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Ranged, Faithful, particles: false, burstRestore: false));   // --no-player-hit-fx: glows only
        Assert.Equal(PlayerHitFxRules.HitLook.Glows, PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Melee, Extended, false, false));
        Assert.Equal(GlowsSparks | PlayerHitFxRules.HitLook.Burst, PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Ranged, Faithful, true, burstRestore: true));   // --player-hit-burst
        Assert.Equal(GlowsSparks, PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Melee, Faithful, true, burstRestore: true));   // a melee hit never had a burst
        Assert.Equal("no-player-hit-fx", PlayerHitFxRules.NoParticlesFlag);
        Assert.Equal("player-hit-burst", PlayerHitFxRules.BurstFlag);
        Assert.Contains(PlayerHitFxRules.BurstFlag, SmNet.LeanBot.PresentationAids);
        Assert.Contains("no-damage-glow", SmNet.LeanBot.PresentationAids);
    }
}

/// <summary>onhit-range-intensity (owner 2026-11-13): the glow of a server-confirmed damage on a player: armour or health colour, one per event, not on the own body, from a hit report or a started reaction.</summary>
public class DamageEventGlowRulesTests
{
    [Theory]
    [InlineData(10f, 0f, true)]    // armour took it: yellow
    [InlineData(10f, 20f, true)]   // armour took some, health too: yellow
    [InlineData(0f, 15f, false)]   // only health: red
    public void The_damage_split_picks_yellow_or_red(float shield, float health, bool up) => Assert.Equal(up, PlayerHitFxRules.ArmourUpOfDamage(shield, health));

    [Fact]
    public void No_damage_is_no_glow() => Assert.Null(PlayerHitFxRules.ArmourUpOfDamage(0f, 0f));

    [Fact]
    public void One_glow_per_damage_event_within_the_window()
    {
        Assert.True(PlayerHitFxRules.DedupePasses(10.0, double.NegativeInfinity));
        Assert.False(PlayerHitFxRules.DedupePasses(10.1, 10.0));   // the same event seen twice (report and reaction)
        Assert.False(PlayerHitFxRules.DedupePasses(10.24, 10.0));
        Assert.True(PlayerHitFxRules.DedupePasses(10.26, 10.0));   // the next blow of a combo
    }

    [Fact]
    public void Another_players_model_glows_but_not_the_own_body_or_nobody()
    {
        Assert.True(PlayerHitFxRules.ShowsOnModel(victim: 3, ownPlayer: 1));
        Assert.False(PlayerHitFxRules.ShowsOnModel(victim: 1, ownPlayer: 1));
        Assert.False(PlayerHitFxRules.ShowsOnModel(victim: 0, ownPlayer: 1));
    }

    [Fact]
    public void A_reaction_that_starts_or_restarts_confirms_a_damage_and_a_running_one_does_not()
    {
        var none = SmSim.ReactionKind.None;
        var kind = System.Enum.GetValues<SmSim.ReactionKind>().First(k => k != none);
        var other = System.Enum.GetValues<SmSim.ReactionKind>().First(k => k != none && k != kind);
        Assert.True(PlayerHitFxRules.ReactionStarted(none, 0, kind, 1));       // began
        Assert.True(PlayerHitFxRules.ReactionStarted(kind, 9, kind, 2));       // restarted: ticks counted up again
        Assert.True(PlayerHitFxRules.ReactionStarted(kind, 9, other, 9));      // another kind
        Assert.False(PlayerHitFxRules.ReactionStarted(kind, 2, kind, 3));      // still running
        Assert.False(PlayerHitFxRules.ReactionStarted(kind, 2, none, 0));      // ended: nothing to show
    }

    [Theory]
    [InlineData(true, false, 1, true)]    // melee damage the own player dealt
    [InlineData(false, true, 1, true)]    // splash damage
    [InlineData(false, false, 1, false)]  // a plain shot: it has its own record
    [InlineData(true, false, 0, false)]   // no hit
    public void A_report_needs_a_glow_only_for_melee_or_splash_damage(bool melee, bool splash, int hits, bool expected) => Assert.Equal(expected, PlayerHitFxRules.ReportNeedsGlow(melee, splash, hits));

    [Fact]
    public void Classic_and_Musou_pick_the_same_glow_for_a_melee_damage_and_differ_only_in_the_ranged_burst()
    {
        var classic = PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Melee, SmSim.ContentExtras.None, true, false);
        var musou = PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Melee, SmSim.ContentExtras.Musou, true, false);
        Assert.Equal(classic, musou);
        Assert.NotEqual(PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Ranged, SmSim.ContentExtras.None, true, false), PlayerHitFxRules.LookFor(PlayerHitFxRules.DamageKind.Ranged, SmSim.ContentExtras.Musou, true, false));
    }
}
