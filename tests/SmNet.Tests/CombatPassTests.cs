using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/combat-pass (the owner's playtest notes of 2026-10-30; protocol 45): W + F is always the charge (item 8), reloads run on while the weapon is holstered (10),
/// the headshot multipliers (12; the data's values, the bolt pistol's one-shot head), directional melee in eight directions (19) and the ledge charge's fall
/// pose delay (21; client rule <see cref="FallRules.ChargeShowsFall"/>). Synthetic specs (RushTests' rush).
/// </summary>
public class CombatPassTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f, Deg = MathF.PI / 180f;
    const int Marine = 0;

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 25.4f, 0.133f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.55f, 1f, 0.5f, 0.505f);
    /// <summary>The data's bolt pistol (the server's listing): 40 damage, head x2.5, body x1, shield head x1, shield body x0.7, fall-off 30 to 40 m to x0.78.</summary>
    static readonly WeaponSpec BoltPistol = new("sm_mp_pistol_bolt_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 40f, 0.25f, 10, 80, 0.75f, 250f, 30f, 40f, 0.78f, 2.5f, 1f, 1f, 0.7f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 20f, 5f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };

    /// <summary>A strike whose hit (0.6 s) comes after its move-on time (0.3 s), as some weapons'.</summary>
    static readonly MeleeStep Late = new(1f, 0.6f, 0.7f, 0.9f, 0.9f, 0.3f, 10f, 5f, 0f);

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, BoltPistol],
        Classes =
        [
            new ClassLoadout("marine", 0, 1, [Light]) { MeleeHeavy = [Heavy], MeleeRush = RushTests.Spec, Evade = 0, Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, 1, [Light]) { MeleeHeavy = [Heavy], MeleeRush = RushTests.Spec, Evade = 0, Side = Team.Chaos },
            new ClassLoadout("late", 0, 1, [Late]) { Evade = 0, Side = Team.SpaceMarines },
        ],
        Evades = [EvadeTests.Roll with { TargetableModeTime = 0.3f, CollisionModeTime = 0.2f }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 100f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
        HeadMultiplier = 2.55f, ShieldHeadMultiplier = 0.5f, ShieldBodyMultiplier = 0.505f,
    };

    static PlayerState Fresh(float yaw = 0f) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, yaw, true) { Class = Marine }, Config);
    static PlayerState Step(PlayerState s, InputButtons b = InputButtons.None, float mx = 0f, float my = 0f, float yaw = 0f) => Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b), Config, out _);

    // ---------------------------------------------------------------- 8: W + F is always the charge

    [Fact]
    public void F_with_W_during_the_charge_spacing_is_lost_and_never_a_stun_strike_while_F_without_W_still_strikes()
    {
        // The spacing since the last charge is not over (RushLock): the press used to fall through to the heavy strike.
        var s = Fresh() with { RushLock = 12 };
        var withW = Step(s, InputButtons.MeleeHeavy, 0f, 1f);
        Assert.Equal((RushPhase.None, 0, false), (withW.Rush, withW.MeleeStep, withW.MeleeHeavy));
        var withWA = Step(s, InputButtons.MeleeHeavy, -1f, 1f);
        Assert.Equal((RushPhase.None, 0), (withWA.Rush, withWA.MeleeStep));
        var noW = Step(s, InputButtons.MeleeHeavy, 0f, 0f);
        Assert.Equal((1, true), (noW.MeleeStep, noW.MeleeHeavy));   // F alone is the stun strike
        // Over: W + F charges.
        Assert.Equal(RushPhase.Rush, Step(Fresh(), InputButtons.MeleeHeavy, 0f, 1f).Rush);
        // Held F: the press edge only; a W + F held through the spacing does not strike when the spacing ends without a new press.
        var held = withW;
        for (int k = 0; k < 20; k++) held = Step(held, InputButtons.MeleeHeavy, 0f, 1f);
        Assert.Equal((RushPhase.None, 0), (held.Rush, held.MeleeStep));
    }

    [Fact]
    public void A_second_W_F_inside_the_spacing_after_a_real_charge_is_not_a_stun_strike()
    {
        var s = Step(Fresh(), InputButtons.MeleeHeavy, 0f, 1f);
        Assert.Equal(RushPhase.Rush, s.Rush);
        for (int k = 0; k < 36 && s.Rush != RushPhase.None; k++) s = Step(s, InputButtons.None, 0f, 0f);
        s = Step(s, InputButtons.None, 0f, 1f);
        var again = Step(s, InputButtons.MeleeHeavy, 0f, 1f);
        output.WriteLine($"second press: rush {again.Rush}, lock {s.RushLock}, melee {again.MeleeStep}");
        Assert.Equal(0, again.MeleeStep);
    }

    [Fact]
    public void A_W_F_press_in_the_spacing_is_buffered_for_the_window_and_starts_the_charge_when_allowed_never_a_strike()
    {
        var c = Config with { InputBufferWindow = 0.3f };
        PlayerState Go(PlayerState s, InputButtons b) => Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, b), c, out _);
        // The spacing ends inside the window: the press is replayed and charges.
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, c) with { RushLock = 4 };
        s = Go(s, InputButtons.MeleeHeavy);
        for (int k = 0; k < 8 && s.Rush == RushPhase.None; k++) { Assert.Equal(0, s.MeleeStep); s = Go(s, InputButtons.None); }
        Assert.Equal(RushPhase.Rush, s.Rush);
        // The spacing outlasts the window: the press is forgotten, still no strike.
        var late = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, c) with { RushLock = 25 };
        late = Go(late, InputButtons.MeleeHeavy);
        for (int k = 0; k < 20; k++) { late = Go(late, InputButtons.None); Assert.Equal((RushPhase.None, 0), (late.Rush, late.MeleeStep)); }
    }

    [Fact]
    public void A_buffered_W_F_press_replayed_after_W_was_released_is_dropped_not_a_stun_strike_and_not_a_charge()
    {
        var c = Config with { InputBufferWindow = 0.3f };
        PlayerState Go(PlayerState s, float my, InputButtons b) => Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out _);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, c) with { RushLock = 5 };
        s = Go(s, 1f, InputButtons.MeleeHeavy);
        Assert.True(s.BufferedCharge);
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [s], Self: 1))));
        Assert.True(back.Players[0].BufferedCharge);
        for (int k = 0; k < 25; k++)
        {
            s = Go(s, 0f, k == 0 ? InputButtons.None : InputButtons.None);   // W released before the charge is allowed
            Assert.Equal((RushPhase.None, 0, false), (s.Rush, s.MeleeStep, s.MeleeHeavy));
        }
        Assert.Equal((BufferedAction.None, false), (s.Buffered, s.BufferedCharge));
        // F alone (no W) buffered is a plain strike press and is kept as before.
        var plain = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, c) with { RushLock = 5 };
        plain = Go(plain, 0f, InputButtons.MeleeHeavy);
        Assert.Equal((1, true), (plain.MeleeStep, plain.MeleeHeavy));
    }

    [Fact]
    public void W_F_in_the_middle_of_a_combo_queues_no_stun_strike()
    {
        var c = Config with { InputBufferWindow = 0.3f };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, c);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), c, out _);
        Assert.Equal(1, s.MeleeStep);
        for (int k = 0; k < 4; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.MeleeHeavy), c, out _);
        Assert.False(s.MeleeQueuedHeavy);
        for (int k = 0; k < 45; k++) { s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), c, out _); Assert.False(s.MeleeHeavy); }
    }

    // ---------------------------------------------------------------- 10: reloads continue while holstered

    [Fact]
    public void A_reload_in_progress_keeps_running_when_the_weapon_is_switched_away_and_completes_on_its_timer()
    {
        var s = Fresh() with { Clip = 5 };
        s = Step(s, InputButtons.Reload);
        Assert.Equal(Rifle.ReloadTime, s.ReloadLeft, 3);   // the press starts it; it runs from the next tick
        s = Step(s, InputButtons.Switch);   // to the pistol
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        Assert.Equal(0f, s.ReloadLeft);                                    // the pistol is not reloading
        Assert.True(s.OtherReloadLeft > 0f && s.OtherReloadLeft < Rifle.ReloadTime, $"{s.OtherReloadLeft}");   // the rifle's reload goes on
        Assert.Equal(5, s.OtherClip);
        int ticks = 0;
        while (s.OtherReloadLeft > 0f && ticks++ < 60) s = Step(s);
        output.WriteLine($"the holstered rifle finished {ticks} ticks later: clip {s.OtherClip}, reserve {s.OtherReserve}");
        Assert.Equal(30, s.OtherClip);
        Assert.Equal(270 - 25, s.OtherReserve);
        Assert.InRange(ticks, 20, 31);   // 1.0 s in all, not restarted
        Assert.Equal(10, s.Clip);        // the pistol in hand untouched
    }

    [Fact]
    public void A_reload_interrupted_by_a_switch_and_back_is_kept_and_the_holstered_reload_travels_in_the_own_record()
    {
        var s = Fresh() with { Clip = 5 };
        s = Step(s, InputButtons.Reload);
        s = Step(s, InputButtons.Switch);
        s = Step(s); s = Step(s);
        while (s.SwitchLeft > 0f) s = Step(s);   // the switch time passes before another switch
        float left = s.OtherReloadLeft;
        Assert.InRange(left, 0.01f, 0.9f);
        s = Step(s, InputButtons.Switch);   // back to the rifle
        Assert.Equal(WeaponSlot.Ranged, s.Slot);
        Assert.InRange(s.ReloadLeft, left - 2 * Dt - 1e-3f, left);   // what was left of its own reload (it may run a tick on the switch tick)
        Assert.Equal(0f, s.OtherReloadLeft);
        // Wire: the holstered reload travels in the own record.
        var p = Fresh() with { OtherReloadLeft = 0.5f, ThirdReloadLeft = 0.25f };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [p], Self: 1))));
        Assert.Equal((0.5f, 0.25f), (back.Players[0].OtherReloadLeft, back.Players[0].ThirdReloadLeft));
    }

    // ---------------------------------------------------------------- 12: headshots

    [Fact]
    public void A_bolt_pistol_headshot_kills_a_full_health_marine_whose_armour_is_gone_and_only_breaks_armour_otherwise()
    {
        var bare = Fresh() with { Shield = 0f };
        var (dead, _, health) = Combat.ApplyDamage(bare, BoltPistol.Damage, HitZone.Head, BoltPistol, Config);
        Assert.Equal(100f, health, 3);   // 40 x 2.5
        Assert.False(dead.Alive);
        var body = Combat.ApplyDamage(bare, BoltPistol.Damage, HitZone.Body, BoltPistol, Config);
        Assert.Equal(40f, body.HealthDamage, 3);
        Assert.True(body.After.Alive);
        // With armour up the head hit takes the armour first (shield head x1: 40 of 100).
        var armoured = Combat.ApplyDamage(Fresh(), BoltPistol.Damage, HitZone.Head, BoltPistol, Config);
        Assert.Equal((40f, 0f), (armoured.ShieldDamage, armoured.HealthDamage));
        // At 40 m the fall-off (x0.78) leaves 40 x 0.78 x 2.5 = 78: not a kill.
        var far = Combat.ApplyDamage(bare, BoltPistol.Damage * 0.78f, HitZone.Head, BoltPistol, Config);
        Assert.True(far.After.Alive);
        Assert.Equal(78f, far.HealthDamage, 2);
        // The bolter's data: head x2.55 on 25.4.
        Assert.Equal(25.4f * 2.55f, Combat.ApplyDamage(bare, Rifle.Damage, HitZone.Head, Rifle, Config).HealthDamage, 3);
    }

    // ---------------------------------------------------------------- 19: directional melee

    static readonly (float X, float Y, float Offset)[] Eight =
    [
        (0f, 1f, 0f), (1f, 1f, -45f), (1f, 0f, -90f), (1f, -1f, -135f), (0f, -1f, 180f), (-1f, -1f, 135f), (-1f, 0f, 90f), (-1f, 1f, 45f), (0f, 0f, 0f),
    ];

    static float Off(float yaw, float view) => MeleeLock.Delta(view, yaw) / Deg;

    [Fact]
    public void A_melee_press_faces_the_sticks_way_of_eight_relative_to_the_view_and_keeps_it_through_the_attack()
    {
        const float view = 0.4f;
        foreach (var (x, y, offset) in Eight)
        {
            var s = Step(Fresh(view), InputButtons.Melee, x, y, view);
            Assert.Equal(1, s.MeleeStep);
            Assert.Equal(MathF.Abs(offset), MathF.Abs(Off(s.Yaw, view)), 2);
            if (MathF.Abs(offset) != 180f) Assert.Equal(offset, Off(s.Yaw, view), 2);
            var held = s;
            for (int k = 0; k < 4; k++) held = Step(held, InputButtons.None, 0f, 0f, view + 0.3f);   // the view turns: the strike follows it, keeping its direction
            Assert.Equal(1, held.MeleeStep);
            Assert.Equal(MathF.Abs(offset), MathF.Abs(Off(held.Yaw, view + 0.3f)), 2);
            while (held.MeleeStep > 0) held = Step(held, InputButtons.None, 0f, 0f, view);
            Assert.Equal(EvadeDirection.Forward, held.MeleeDirection);
        }
    }

    [Fact]
    public void The_heavy_strike_is_directional_too_without_W()
    {
        var s = Step(Fresh(), InputButtons.MeleeHeavy, 0f, -1f);
        Assert.Equal((1, true), (s.MeleeStep, s.MeleeHeavy));
        Assert.Equal(180f, MathF.Abs(Off(s.Yaw, 0f)), 2);
        Assert.Equal(EvadeDirection.Back, s.MeleeDirection);
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [s], Self: 1))));
        Assert.Equal(EvadeDirection.Back, back.Players[0].MeleeDirection);
    }

    [Fact]
    public void S_with_the_melee_press_strikes_the_player_behind_and_no_stick_strikes_the_one_in_front()
    {
        foreach (bool back in new[] { true, false })
        {
            var spawns = new[] { new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, -1.2f, 0), 0f), new SpawnPoint(new Vector3(0, 1.2f, 0), 0f) };
            var w = new World(Config, null, spawns) { Rules = new MatchRules { TimeLimit = 0f } };
            w.Spawn(1, Marine, team: Team.SpaceMarines);
            w.Spawn(2, 1, team: Team.Chaos);
            w.Spawn(3, 1, team: Team.Chaos);
            Assert.Equal(new Vector3(0, -1.2f, 0), w.Players[2].Position);
            for (int k = 0; k < 24; k++)
            {
                uint view = w.Tick;
                int tick = k;
                w.Step(id => (id == 1 ? new PlayerInput(0, 0f, back && tick == 0 ? -1f : 0f, 0f, tick == 0 ? InputButtons.Melee : InputButtons.None) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None)) with { ViewTick = view });
            }
            float behind = w.Players[2].Health + w.Players[2].Shield, front = w.Players[3].Health + w.Players[3].Shield;
            output.WriteLine($"{(back ? "S + melee" : "melee")}: behind {behind}, in front {front}; attacker yaw {w.Players[1].Yaw:F2}, melee step {w.Players[1].MeleeStep}");
            Assert.True(back ? behind < 200f && front == 200f : front < 200f && behind == 200f);
        }
    }

    [Fact]
    public void A_strike_whose_hit_comes_after_its_move_on_time_still_lands_behind_with_S()
    {
        var spawns = new[] { new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, -1.2f, 0), 0f), new SpawnPoint(new Vector3(0, 1.2f, 0), 0f) };
        var w = new World(Config, null, spawns) { Rules = new MatchRules { TimeLimit = 0f } };
        w.Spawn(1, 2, team: Team.SpaceMarines);
        w.Spawn(2, 1, team: Team.Chaos);
        w.Spawn(3, 1, team: Team.Chaos);
        for (int k = 0; k < 30; k++)
        {
            uint view = w.Tick;
            int tick = k;
            w.Step(id => (id == 1 ? new PlayerInput(0, 0f, tick == 0 ? -1f : 0f, 0f, tick == 0 ? InputButtons.Melee : InputButtons.None) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None)) with { ViewTick = view });
        }
        float behind = w.Players[2].Health + w.Players[2].Shield, front = w.Players[3].Health + w.Players[3].Shield;
        output.WriteLine($"late hit: behind {behind}, in front {front}");
        Assert.True(behind < 200f && front == 200f);
    }

    // ---------------------------------------------------------------- 21: the ledge charge's fall pose

    [Theory]
    [InlineData(0.1, false)]
    [InlineData(0.29, false)]
    [InlineData(0.3, true)]
    [InlineData(2.0, true)]
    [InlineData(double.NaN, false)]
    public void A_charge_held_in_the_air_shows_the_fall_pose_0_3_s_after_the_body_leaves_the_ground(double inAir, bool falls) =>
        Assert.Equal(falls, FallRules.ChargeShowsFall(inAir));
}
