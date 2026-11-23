using System;
using System.Collections.Generic;
using System.Numerics;
using SmClient.ImpactRules;
using SmSim;
using static SmClient.ImpactRules.ClassicHitDirRules;

namespace SmNet.Tests;

/// <summary>classic-hit-effects (brief 2026-11-23 rev 1; owner 2026-11-23 question card "Extended only"): the engine-free rules of the directional hit look in Classic PvP.</summary>
public class ClassicHitDirTests
{
    static PlayerState Player(float shield, float health = 100f) => new(3, new Vector3(0, 0, 0), Vector3.Zero, 0f, true) { Alive = true, Health = health, Shield = shield, Team = Team.Chaos };

    static ContentExtras Extended => ContentExtras.All;

    // ---- the hit result to the visual class ----

    [Fact]
    public void A_damage_maps_to_sparks_mixed_or_blood()
    {
        Assert.Equal(HitVisual.Sparks, Classify(shieldTaken: true, healthTaken: false, shieldLeftAfter: true));    // armour only
        Assert.Equal(HitVisual.Sparks, Classify(shieldTaken: true, healthTaken: false, shieldLeftAfter: false));   // the last armour point broken, no health lost
        Assert.Equal(HitVisual.Mixed, Classify(shieldTaken: false, healthTaken: true, shieldLeftAfter: true));     // health lost with armour left
        Assert.Equal(HitVisual.Blood, Classify(shieldTaken: false, healthTaken: true, shieldLeftAfter: false));    // bare armour
        Assert.Equal(HitVisual.Blood, Classify(shieldTaken: true, healthTaken: true, shieldLeftAfter: false));     // armour broken and health hit
        Assert.Equal(HitVisual.None, Classify(false, false, true));                                                // no damage (a miss, a wall hit): nothing
        Assert.True(HasSparks(HitVisual.Mixed) && HasBlood(HitVisual.Mixed));
        Assert.False(HasBlood(HitVisual.Sparks) || HasSparks(HitVisual.Blood) || HasBlood(HitVisual.None) || HasSparks(HitVisual.None));
    }

    [Theory]
    [InlineData(100f, 10f, 1f, true, HitVisual.Sparks)]    // armour only
    [InlineData(10f, 10f, 1f, false, HitVisual.Sparks)]    // breaks the last armour point, no health lost
    [InlineData(4f, 30f, 1f, false, HitVisual.Blood)]      // breaks armour and damages health
    [InlineData(100f, 10f, 0f, true, HitVisual.Mixed)]     // a weapon that ignores armour: health lost with armour left
    [InlineData(0f, 10f, 1f, false, HitVisual.Blood)]      // unarmoured
    public void The_mark_of_a_real_damage_classifies_as_the_brief_says(float shield, float damage, float shieldMultiplier, bool armourLeft, HitVisual expected)
    {
        var before = Player(shield);
        var after = Combat.ApplyDamage(before, damage, shieldMultiplier, 1f, new SimConfig()).After;
        Assert.Equal(armourLeft, after.Shield > 0f);
        bool armourUpBefore = before.Shield > 0f;
        Assert.Equal(expected, Classify(after.DamageShield, after.DamageHealth, ShieldLeftAfterMark(after.DamageShield, after.DamageHealth, armourUpBefore)));
        // the exact own view (the hit report's shield after) agrees with the mark's inference
        Assert.Equal(expected, Classify(after.DamageShield, after.DamageHealth, after.Shield > 0f));
    }

    [Fact]
    public void Heavier_and_lethal_hits_are_bloodier_within_the_mesh()
    {
        Assert.True(BloodAmount(false, false) < BloodAmount(true, false));
        Assert.True(BloodAmount(true, false) < BloodAmount(false, true));
        Assert.True(StreakCount(1f) < StreakCount(HeavyAmount) && StreakCount(HeavyAmount) < StreakCount(LethalAmount));
        Assert.True(StreakCount(LethalAmount) <= MaxStreaks && StreakCount(100f) == MaxStreaks);
        Assert.InRange(SprayLife(false), 0.7f, 1.0f);   // the reference's plume is mostly gone about 0.8 s after contact
        Assert.True(SprayLife(true) < SprayLife(false));
    }

    // ---- who draws what ----

    [Fact]
    public void Faithful_draws_directional_sparks_but_no_spray_and_no_decal()
    {
        var look = LookFor(ContentExtras.None, oldLook: false);
        Assert.Equal(DirLook.MeleeSparks, look);
        Assert.False(look.HasFlag(DirLook.BloodSpray));
        Assert.False(look.HasFlag(DirLook.HeadshotDecal));
    }

    [Fact]
    public void Faithful_health_hits_keep_the_look_of_d090566a()
    {
        // The red health-hit sparks and glows of 2026-11-13 stay (the burst stays off), and the blood spray does not replace them.
        foreach (var kind in new[] { PlayerHitFxRules.DamageKind.Ranged, PlayerHitFxRules.DamageKind.Melee })
            Assert.Equal(PlayerHitFxRules.HitLook.Glows | PlayerHitFxRules.HitLook.Sparks, PlayerHitFxRules.LookFor(kind, ContentExtras.None, particles: true, burstRestore: false));
        Assert.False(LookFor(ContentExtras.None, false).HasFlag(DirLook.BloodSpray));
        Assert.True(PlayerHitFxRules.BareHitFlash(ContentExtras.None, false, false, false));
        Assert.False(PlayerHitFxRules.ArmourUpOfMark(shield: false, health: true));   // a health mark still reads red
    }

    [Fact]
    public void Extended_draws_the_spray_and_the_decal_and_the_old_flag_restores_the_base_look()
    {
        Assert.Equal(DirLook.MeleeSparks | DirLook.BloodSpray | DirLook.HeadshotDecal, LookFor(Extended, false));
        Assert.Equal(DirLook.MeleeSparks | DirLook.BloodSpray | DirLook.HeadshotDecal, LookFor(ContentExtras.Gore, false));
        Assert.Equal(DirLook.MeleeSparks, LookFor(ContentExtras.Orks | ContentExtras.Autocannon, false));   // a profile without the gore flag has no blood
        Assert.Equal(DirLook.None, LookFor(Extended, oldLook: true));
        Assert.Equal(DirLook.None, LookFor(ContentExtras.None, oldLook: true));
    }

    [Fact]
    public void Musou_and_Warfront_are_unchanged()
    {
        Assert.Equal(DirLook.None, LookFor(ContentExtras.Musou, false));
        Assert.Equal(DirLook.None, LookFor(ContentExtras.Musou | ContentExtras.Warfront, false));
        Assert.Equal(DirLook.None, LookFor(ContentExtras.Musou | ContentExtras.Gore, false));
    }

    // ---- direction from weapon motion and shot ----

    static List<PathPoint> Stroke(Vector3 from, Vector3 velocity, int n = 8, double dt = 1.0 / 60)
    {
        var p = new List<PathPoint>();
        for (int i = 0; i < n; i++) p.Add(new PathPoint(i * dt, from + velocity * (float)(i * dt)));
        return p;
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]     // left to right
    [InlineData(-1f, 0f, 0f)]    // reverse
    [InlineData(0f, -1f, 0f)]    // overhead (down)
    [InlineData(0.7071f, -0.7071f, 0f)]   // diagonal
    [InlineData(0f, 0f, -1f)]    // thrust (away from the viewer)
    public void The_direction_follows_the_weapon_path(float x, float y, float z)
    {
        var dir = new Vector3(x, y, z);
        var got = WeaponDirection(Stroke(new Vector3(0, 1.3f, 0), dir * 12f), 0.06);
        Assert.NotNull(got);
        Assert.True(Vector3.Dot(got!.Value, Vector3.Normalize(dir)) > 0.999f, $"{got}");
    }

    [Fact]
    public void A_curved_swing_reads_the_tangent_at_the_contact_and_alternating_combo_steps_differ()
    {
        // a quarter arc of 1 m radius about the attacker: at the start it moves +X, at the end -Z
        var arc = new List<PathPoint>();
        for (int i = 0; i <= 20; i++) { double a = i / 20.0 * System.Math.PI / 2; arc.Add(new PathPoint(i * 0.01, new Vector3((float)System.Math.Sin(a), 0, (float)System.Math.Cos(a)))); }
        var early = WeaponDirection(arc, 0.01, 0.02)!.Value;
        var late = WeaponDirection(arc, 0.19, 0.02)!.Value;
        Assert.True(early.X > 0.95f, $"{early}");
        Assert.True(late.Z < -0.95f, $"{late}");
        var step1 = WeaponDirection(Stroke(Vector3.Zero, new Vector3(10, 0, 0)), 0.05)!.Value;
        var step2 = WeaponDirection(Stroke(Vector3.Zero, new Vector3(-10, 0, 0)), 0.05)!.Value;
        Assert.True(Vector3.Dot(step1, step2) < -0.99f);
    }

    [Fact]
    public void A_still_or_popping_weapon_gives_no_direction()
    {
        Assert.Null(WeaponDirection(Stroke(Vector3.Zero, Vector3.Zero), 0.05));
        Assert.Null(WeaponDirection(Stroke(Vector3.Zero, new Vector3(200, 0, 0)), 0.05));   // a pose pop, not a swing
        Assert.Null(WeaponDirection(new List<PathPoint> { new(0, Vector3.Zero) }, 0));
    }

    [Fact]
    public void Gunfire_uses_the_incoming_shot()
    {
        var d = ShotDirection(new Vector3(0, 0, 0), new Vector3(10, 0, 0))!.Value;
        Assert.Equal(Vector3.UnitX, d);
        Assert.Null(ShotDirection(Vector3.One, Vector3.One));
    }

    [Fact]
    public void Thrusts_stay_narrower_than_sweeps()
    {
        float yaw = 0f;   // facing +Y
        Assert.Equal(1f, Thrustness(new Vector3(0, 5, 0), yaw), 3);
        Assert.Equal(0f, Thrustness(new Vector3(5, 0, 0), yaw), 3);
        Assert.True(SpreadFor(1f) < SpreadFor(0f));
        Assert.Equal(ThrustSpread, SpreadFor(1f), 4);
        Assert.Equal(SweepSpread, SpreadFor(0f), 4);
    }

    [Fact]
    public void The_contact_is_the_closest_approach_on_the_body_surface()
    {
        var low = new Vector3(0, 0.9f, 0); var high = new Vector3(0, 1.65f, 0);
        var path = Stroke(new Vector3(-1.2f, 1.3f, 0.3f), new Vector3(12, 0, 0), 14);   // passes 0.3 m in front of the axis
        var (idx, dist) = ClosestApproach(path, low, high);
        Assert.InRange(path[idx].Position.X, -0.15f, 0.15f);
        Assert.InRange(dist, 0.29f, 0.35f);
        var contact = ContactOnBody(path[idx].Position, low, high);
        Assert.InRange(Vector3.Distance(contact, ClosestOnSegment(contact, low, high)), 0f, BodySurface + 1e-4f);
        Assert.Equal((-1, float.PositiveInfinity), ClosestApproach(new List<PathPoint>(), low, high));
    }

    [Fact]
    public void A_remote_spray_waits_for_the_drawn_weapon_but_not_forever_and_never_starts_before_contact()
    {
        Assert.True(WaitForContact(closestIndex: 7, count: 8, closestDistance: 1.0f, waited: 0.05));    // still approaching
        Assert.False(WaitForContact(closestIndex: 4, count: 8, closestDistance: 1.0f, waited: 0.05));   // passed its closest approach
        Assert.False(WaitForContact(closestIndex: 7, count: 8, closestDistance: 0.3f, waited: 0.05));   // touching now
        Assert.False(WaitForContact(closestIndex: 7, count: 8, closestDistance: 1.0f, waited: MaxWaitSeconds));
        Assert.Equal(0.0, PreAge(1.0, 1.2));             // a contact still ahead: no age
        Assert.Equal(0.05, PreAge(1.05, 1.0), 6);
        Assert.Equal(MaxPreAge, PreAge(3.0, 1.0));
    }

    // ---- remote melee pairing ----

    static MeleeCandidate C(int id, float x, float y, float yaw, double since = 0.05, Team team = Team.SpaceMarines, bool alive = true, bool own = false) =>
        new(id, team, new Vector3(x, y, 0), yaw, since, alive, false, own);

    [Fact]
    public void The_attacker_is_the_enemy_whose_melee_just_struck_in_reach_and_facing()
    {
        var victim = Vector3.Zero;
        // yaw 0 faces +Y: a player at (0,-2) facing +Y faces the victim
        Assert.Equal(5, PickAttacker(3, Team.Chaos, victim, [C(5, 0, -2, 0f)]));
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, [C(5, 0, -2, 0f, team: Team.Chaos)]));     // an ally
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, [C(5, 0, -5, 0f)]));                       // out of reach
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, [C(5, 0, -2, 0f, since: 1.0)]));           // his attack ended long ago (a shot, a blast)
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, [C(5, 0, -2, System.MathF.PI)]));          // facing away
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, [C(5, 0, -2, 0f, alive: false)]));
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, []));
        Assert.Equal(9, PickAttacker(3, Team.Chaos, victim, [C(9, 0, -2, 0f, since: 0.6, own: true)]));   // the own prediction runs a round trip ahead
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, [C(9, 0, -2, 0f, since: 0.6, own: false)]));
    }

    [Fact]
    public void Two_attackers_striking_one_victim_in_the_same_tick_give_one_spray_for_the_better_placed()
    {
        // The server raises the victim's mark once per tick, so a single confirmation is drawn: the nearer, squarer attacker, whatever the order of the records.
        var near = C(5, 0, -1.2f, 0f);
        var far = C(7, 2.0f, -2.0f, 0f);
        Assert.Equal(5, PickAttacker(3, Team.Chaos, Vector3.Zero, [near, far]));
        Assert.Equal(5, PickAttacker(3, Team.Chaos, Vector3.Zero, [far, near]));
        // equally placed: the lower id, in any order
        var a = C(8, -1f, -1f, -System.MathF.PI / 4);
        var b = C(6, 1f, -1f, System.MathF.PI / 4);
        Assert.Equal(6, PickAttacker(3, Team.Chaos, Vector3.Zero, [a, b]));
        Assert.Equal(6, PickAttacker(3, Team.Chaos, Vector3.Zero, [b, a]));
    }

    [Fact]
    public void One_effect_per_victim_and_contact()
    {
        var ledger = new ContactLedger();
        Assert.True(ledger.First(victim: 3, attacker: 5, key: 100, now: 1.0));
        Assert.False(ledger.First(3, 5, 100, 1.1));   // the same contact seen again (a second mark of the same swing, a duplicate report)
        Assert.True(ledger.First(4, 5, 100, 1.1));    // the same swing hits a second player: his own spray
        Assert.True(ledger.First(3, 5, 102, 1.3));    // the next combo step: a new contact
        Assert.True(ledger.First(3, 6, 100, 1.3));    // another attacker
        Assert.True(ledger.First(3, 5, 100, 1.0 + ContactLedger.Seconds + 0.01));   // long after: a new contact
    }

    [Fact]
    public void The_armour_tracker_answers_up_down_or_unknown()
    {
        var t = new ArmourFlashTracker();
        Assert.Null(t.ArmourUp(4, 0.0));
        t.Hit(4, 1.0, armourUp: true, regenDelay: 5f);
        Assert.True(t.ArmourUp(4, 1.1));
        t.Broke(4, 1.2, 5f);
        Assert.False(t.ArmourUp(4, 1.3));
        Assert.True(t.ArmourUp(4, 6.3));   // recharged
    }

    // ---- the jump pack's air slash (owner 2026-11-24, question card "Yes, spray it") ----

    [Fact]
    public void The_air_slash_is_weapon_melee_and_stomps_charges_are_not()
    {
        Assert.True(IsWeaponMelee(alive: true, meleeStep: 0, stomp: false, airSlash: true));    // the slash: weapon melee, sprayed along the blade
        Assert.True(IsWeaponMelee(true, 2, false, false));                                       // a melee step
        Assert.False(IsWeaponMelee(true, 1, stomp: true, airSlash: false));                      // a stomp keeps its blast
        Assert.False(IsWeaponMelee(true, 0, false, false));                                      // no attack (a bull rush or dash attack contact is neither)
        Assert.False(IsWeaponMelee(alive: false, 2, false, true));                               // the dead strike nothing
    }

    [Fact]
    public void An_air_slash_pairs_within_its_reach_whatever_his_drawn_yaw()
    {
        var victim = new Vector3(0f, 4.2f, 0f);
        // facing away (yaw pi: -Y), 4.2 m off: a melee step does not pair, the slash does
        var step = new List<MeleeCandidate> { new(5, Team.SpaceMarines, Vector3.Zero, MathF.PI, 0.05, true) };
        var slash = new List<MeleeCandidate> { new(5, Team.SpaceMarines, Vector3.Zero, MathF.PI, 0.05, true, Slash: true) };
        Assert.Equal(0, PickAttacker(3, Team.Chaos, victim, step));
        Assert.Equal(5, PickAttacker(3, Team.Chaos, victim, slash));
        Assert.Equal(0, PickAttacker(3, Team.Chaos, new Vector3(0f, SlashPairReach + 0.1f, 0f), slash));   // past its reach
    }

    [Fact]
    public void An_air_slash_has_its_own_attack_key_per_slash()
    {
        var steps = new HashSet<long>();
        for (long tick = 0; tick < 200; tick++) for (int step = 0; step < 4; step++) { steps.Add(AttackKey(tick, step, false)); steps.Add(AttackKey(tick, step, true)); }
        for (int n = 1; n < 50; n++) Assert.DoesNotContain(SlashKey(n), steps);    // never a melee step's key
        Assert.NotEqual(SlashKey(1), SlashKey(2));                                   // each slash a new contact
        var ledger = new ContactLedger();
        Assert.True(ledger.First(7, 3, SlashKey(1), 10.0));
        Assert.False(ledger.First(7, 3, SlashKey(1), 10.1));                          // one effect per victim and slash
        Assert.True(ledger.First(8, 3, SlashKey(1), 10.1));                           // a second victim of the same slash
        Assert.True(ledger.First(7, 3, SlashKey(2), 10.2));                           // the next slash
    }

    // ---- the bull-rush charge (owner 2026-11-24, question card "Yes, spray charges too") ----

    [Fact]
    public void A_charge_connect_is_its_rush_or_collision_stop_only()
    {
        Assert.True(IsChargeContact(RushPhase.Rush));
        Assert.True(IsChargeContact(RushPhase.Collision));
        Assert.False(IsChargeContact(RushPhase.None));
        Assert.False(IsChargeContact(RushPhase.DashAttack));   // the jump pack's dash attack is not the bull rush
        Assert.False(IsChargeContact(RushPhase.DashHit));
    }

    [Fact]
    public void A_charge_sprays_along_the_chargers_velocity()
    {
        var d = ChargeDirection(new Vector3(6f, 0f, 0.5f), new Vector3(0f, 1f, 0f), 0f);
        Assert.Equal(1f, d.X, 3); Assert.Equal(0f, d.Z, 3);                                  // the rush's velocity, level
        d = ChargeDirection(new Vector3(0.2f, 0f, 0f), new Vector3(0f, 2f, 0f), 0f);
        Assert.Equal(1f, d.Y, 3);                                                            // stopped: the line to the victim
        d = ChargeDirection(Vector3.Zero, Vector3.Zero, MathF.PI / 2f);
        Assert.Equal(-1f, d.X, 3);                                                           // nothing else: his facing
    }

    [Fact]
    public void A_charge_has_its_own_key_per_rush_and_one_effect_per_victim()
    {
        var other = new HashSet<long>();
        for (int n = 0; n < 1000; n++) other.Add(SlashKey(n));
        for (long tick = 0; tick < 200; tick++) for (int step = 0; step < 4; step++) other.Add(AttackKey(tick, step, false));
        for (int n = 1; n < 100; n++) Assert.DoesNotContain(ChargeKey(n), other);
        var ledger = new ContactLedger();
        Assert.True(ledger.First(4, 9, ChargeKey(1), 3.0));
        Assert.False(ledger.First(4, 9, ChargeKey(1), 3.2));    // the same charge on him again
        Assert.True(ledger.First(4, 9, ChargeKey(2), 3.4));     // his next charge
    }

    // ---- the loose gunfire hit's reach (lead decision 2026-11-24) ----

    [Fact]
    public void A_fast_victims_loose_hit_at_4_8_m_pairs_and_a_slow_ones_does_not()
    {
        var chest = new Vector3(0f, 0f, 1.25f);
        var hits = new List<LooseHit> { new(new Vector3(4.8f, 0f, 1.25f), new Vector3(0f, 1f, 0f)) };
        float fast = LooseReach(drawnSpeed: 12f, drawLag: 0.12f);   // a jump pack: 3.5 + 1.44 m
        float slow = LooseReach(drawnSpeed: 2f, drawLag: 0.12f);    // walking: 3.74 m
        Assert.Equal(0, PickLooseHit(chest, fast, hits));
        Assert.Equal(-1, PickLooseHit(chest, slow, hits));          // a wrong shot that far from a slow victim stays unpaired
        Assert.Equal(LooseShotReachMax, LooseReach(40f, 0.15f));    // capped
        Assert.Equal(LooseShotReach, LooseReach(0f, 0.12f));
    }

    [Fact]
    public void Of_two_loose_hits_in_reach_the_one_whose_line_passes_closest_wins()
    {
        var chest = new Vector3(0f, 0f, 1.25f);
        var hits = new List<LooseHit>
        {
            new(new Vector3(1.0f, 0f, 1.25f), new Vector3(0f, 1f, 0f)),   // nearer end, but its line passes 1 m off him
            new(new Vector3(0f, 3.0f, 1.25f), new Vector3(0f, 1f, 0f)),   // farther end, its line runs through him
        };
        Assert.Equal(1, PickLooseHit(chest, 4f, hits));
        var same = new List<LooseHit> { new(new Vector3(0f, 3f, 1.25f), new Vector3(0f, 1f, 0f)), new(new Vector3(0f, 1f, 1.25f), new Vector3(0f, 1f, 0f)) };
        Assert.Equal(1, PickLooseHit(chest, 4f, same));                  // equal lines: the nearer end
    }
}
