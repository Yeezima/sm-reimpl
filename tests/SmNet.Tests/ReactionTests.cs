using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M28: hit reactions (interrupt, stun, stagger) and heavy strikes: each source (light and heavy melee hits, the heavy combo's
/// stomp, blasts by intensity, the ground pound), immunity (evade hyper armour, jump-pack flight, an attack's window without
/// <c>BRANCH_HitReact</c>, a reaction not yet re-reactable), what a reaction interrupts and blocks, how it ends (time, the
/// evade "stun break", melee, stick), knockback through the capsule controller, blindness, prediction exactness at 150 ms RTT
/// and 5% loss, and protocol 17. Synthetic timings, placed half a tick off the 30 Hz grid.
/// </summary>
public class ReactionTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    static float T(float ticks) => ticks / 30f;

    static readonly ReactionEntry Interrupt = new(0f, ReactionKind.Interrupt);
    static readonly ReactionEntry Stun = new(0f, ReactionKind.Stun, Backward: 2f, Duration: 0.2f);       // 2 m over 6 ticks
    static readonly ReactionEntry Stagger = new(0f, ReactionKind.Stagger, Backward: 3f, Duration: 0.3f); // 3 m over 9 ticks

    /// <summary>Light attack: hit at tick 7, combo window ticks 13..19, fire from 16, moves from 22, over at 31; no HitReact ticks 6..8.</summary>
    static readonly MeleeStep Light = new MeleeStep(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), 10f, 5f, 3.5f)
        { PrimaryReaction = Interrupt, SecondaryReaction = Interrupt, NoReactFrom = T(5.5f), NoReactTo = T(8.5f) };

    /// <summary>Heavy strike: hit at tick 10, no combo, moves from 30, over at 41.</summary>
    static MeleeStep HeavyStep(ReactionEntry primary, ReactionEntry secondary, SplashSpec? stomp = null) =>
        new(T(40.5f), T(9.5f), 0f, 0f, T(25.5f), T(29.5f), stomp is null ? 15f : 0f, stomp is null ? 5f : 0f, 3.5f)
        { PrimaryReaction = primary, SecondaryReaction = secondary, Stomp = stomp };

    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);

    /// <summary>The knife's stomp shape: radius 7, full to 4.5 m, 0.5 at 6, 0.15 at 7; stagger from 0.8, stun from 0.61, else interrupt.</summary>
    static readonly SplashSpec StompBlast = new(7f, Plain with { Damage = 5f }, [new(0f, 1f), new(4.5f, 1f), new(6f, 0.5f), new(7f, 0.15f)], true, true, "target_all")
    {
        Reactions = [new(0.8f, ReactionKind.Stagger), new(0.61f, ReactionKind.Stun), new(0f, ReactionKind.Interrupt)],
    };

    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.2f, 10, 50, 1.3f, 100f, 100f, 100f, 1f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);

    const int Tactical = 0, Assault = 1, Stomper = 2;

    static readonly IReadOnlyList<MeleeStep> Heavies = [HeavyStep(Stun, Interrupt), HeavyStep(Stagger, Interrupt), HeavyStep(Stun, Stun), HeavyStep(default, default, StompBlast)];

    static readonly SimConfig Config = new()
    {
        Weapons = [Pistol, Rifle],
        Classes =
        [
            new ClassLoadout("tactical", 1, 0, [Light, Light, Light]) { MeleeHeavy = Heavies, Evade = 0, Side = Team.SpaceMarines },
            new ClassLoadout("assault", -1, 0, [Light, Light, Light]) { MeleeHeavy = Heavies, JumpPack = 0, Side = Team.Chaos },
            new ClassLoadout("stomper", 1, 0, [Light]) { MeleeHeavy = [HeavyStep(default, default, StompBlast)], Side = Team.SpaceMarines },
        ],
        JumpPacks = [JumpPackTests.Pack],
        Evades = [EvadeTests.Roll with { TargetableModeTime = 0.3f, CollisionModeTime = 0.2f }],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f,
    };

    /// <summary>Reach everywhere (for targets that roll or fly away).</summary>
    static readonly SimConfig Long = Config with { MeleeRange = 40f, MeleeConeDegrees = 180f, MeleeVerticalReach = 60f };

    static World Arena(SimConfig c, CollisionWorld? collision, params (int Class, Vector3 At)[] players)
    {
        var world = new World(c, collision, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray());
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class);
        for (int i = 0; i < players.Length; i++) Assert.Equal(players[i].At, world.Players[i + 1].Position);
        return world;
    }

    /// <summary>Steps the world with a script: (tick from 0, player id) → buttons and stick; player 1 faces +Y, others face −Y.</summary>
    static void Run(World w, int ticks, Func<int, int, (InputButtons Buttons, float X, float Y)> script, Action<int>? each = null)
    {
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            w.Step(id => { var (b, x, y) = script(tick, id); return new PlayerInput(0, x, y, id == 1 ? 0f : MathF.PI, b); });
            each?.Invoke(k);
        }
    }

    static (InputButtons, float, float) None => (InputButtons.None, 0f, 0f);
    static (InputButtons, float, float) Press(InputButtons b) => (b, 0f, 0f);

    // ---------------------------------------------------------------- sources

    [Fact]
    public void A_light_hit_interrupts_its_target_from_the_front_and_a_heavy_strike_from_rest_stuns_it_with_knockback()
    {
        var w = Arena(Config, null, (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 2, 0)));
        var reactions = new List<ReactionEvent>();
        Run(w, 8, (k, id) => id == 1 && k == 0 ? Press(InputButtons.Melee) : None, _ => reactions.AddRange(w.LastReactions));
        var e = Assert.Single(reactions);
        Assert.Equal((1, 2, ReactionKind.Interrupt, ReactionDirection.Front, 0), (e.Source, e.Target, e.Kind, e.Direction, e.PushDistance));
        Assert.Equal(8u, e.Tick);   // the press is world tick 1 (attack time 0), the hit lands 7 ticks later
        var v = w.Players[2];
        Assert.Equal((ReactionKind.Interrupt, 0), (v.Reaction, v.ReactionTicks));
        Assert.Equal(ReactionKind.None, w.Players[1].Reaction);   // the attacker is not affected

        var w2 = Arena(Config, null, (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 2, 0)));
        var start = w2.Players[2].Position;
        reactions.Clear();
        Run(w2, 11, (k, id) => id == 1 && k == 0 ? Press(InputButtons.MeleeHeavy) : None, _ => reactions.AddRange(w2.LastReactions));
        Assert.True(w2.Players[1].MeleeHeavy);
        e = Assert.Single(reactions);
        Assert.Equal((ReactionKind.Stun, 32), (e.Kind, e.PushDistance));   // 2 m in 1/16 m
        var stunned = w2.Players[2];
        Assert.Equal((6, 0), (stunned.PushTicks, Reaction.YawByte(Vector2.UnitY)));
        Assert.Equal(Reaction.YawByte(Vector2.UnitY), stunned.PushYaw);
        Run(w2, 6, (_, _) => None);
        Assert.Equal(2f, w2.Players[2].Position.Y - start.Y, 3);           // knocked 2 m back, along the attacker → target line
        Run(w2, 3, (_, _) => None);
        Assert.Equal(2f, w2.Players[2].Position.Y - start.Y, 3);           // and no further
    }

    [Fact]
    public void A_heavy_press_in_a_light_attacks_combo_window_chains_into_the_heavy_strike_after_that_many_light_ones()
    {
        // X then Y: light attack 1, then heavy strike 2 (the stagger).
        var w = Arena(Config, null, (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 2, 0)));
        var kinds = new List<ReactionKind>();
        var steps = new List<(int Step, bool Heavy)>();
        Run(w, 60, (k, id) => id != 1 ? None : k == 0 ? Press(InputButtons.Melee) : k == 10 ? Press(InputButtons.MeleeHeavy) : None, _ =>
        {
            kinds.AddRange(w.LastReactions.Select(r => r.Kind));
            steps.Add((w.Players[1].MeleeStep, w.Players[1].MeleeHeavy));
        });
        Assert.Contains((2, true), steps);
        Assert.Equal(13, steps.IndexOf((2, true)));   // light 1's combo window opens 13 ticks after its press
        // Light 1's interrupt (front clip) is 16 ticks (0.53 s) old when heavy 2 lands, past its HitReact branch (0.37 s): the
        // stagger replaces it.
        Assert.Equal([ReactionKind.Interrupt, ReactionKind.Stagger], kinds);

        // A heavy press during a heavy strike does nothing; a light press in its window is not possible (no window): the strike ends.
        var w2 = Arena(Config, null, (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 30, 0)));
        Run(w2, 45, (k, id) => id == 1 && k is 0 or 5 ? Press(InputButtons.MeleeHeavy) : None);
        Assert.Equal((0, false), (w2.Players[1].MeleeStep, w2.Players[1].MeleeHeavy));
    }

    [Fact]
    public void The_stomp_reacts_by_intensity_stagger_near_stun_further_interrupt_at_the_edge_and_nothing_beyond()
    {
        // Stomper at the origin; targets at 3 m (intensity 1), 5.5 m (0.667), 6.5 m (0.325), 7.5 m (outside).
        var w = Arena(Config, null, (Stomper, Vector3.Zero), (Tactical, new Vector3(3, 0, 0)), (Tactical, new Vector3(0, -5.5f, 0)),
            (Tactical, new Vector3(-6.5f, 0, 0)), (Tactical, new Vector3(0, 7.5f, 0)));
        var reactions = new List<ReactionEvent>();
        Run(w, 14, (k, id) => id == 1 && k == 0 ? Press(InputButtons.MeleeHeavy) : None, _ => reactions.AddRange(w.LastReactions));
        output.WriteLine(string.Join("; ", reactions));
        Assert.Equal([(2, ReactionKind.Stagger), (3, ReactionKind.Stun), (4, ReactionKind.Interrupt)], reactions.Select(r => (r.Target, r.Kind)).OrderBy(r => r.Target));
        Assert.All(reactions, r => Assert.Equal(11u, r.Tick));   // the stomp lands at the step's hit time (10 ticks after the press)
        Assert.Equal(ReactionKind.None, w.Players[1].Reaction);  // ignore_self
        Assert.Equal(ReactionKind.None, w.Players[5].Reaction);
        Assert.Equal(1000f - 5f, w.Players[2].Health);            // the stomp's damage
    }

    [Fact]
    public void The_ground_pound_stuns_within_its_full_intensity_and_interrupts_only_at_0_8()
    {
        var pack = JumpPackTests.Pack with { PoundReactions = [new(0.81f, "reaction_mp_stun"), new(0.8f, "reaction_mp_melee")] };
        var c = Config with { JumpPacks = [pack] };
        var w = Arena(c, null, (Assault, Vector3.Zero), (Tactical, new Vector3(2.5f, 0, 0)), (Tactical, new Vector3(0, -6.5f, 0)));
        var reactions = new List<ReactionEvent>();
        bool pounded = false;
        for (int k = 0; k < 300 && reactions.Count == 0; k++)
        {
            var p = w.Players[1];
            var b = k == 0 ? InputButtons.Jump : !pounded && p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
            if (b == InputButtons.Melee) pounded = true;
            w.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, b, -1.4f) : new PlayerInput(0, 0, 0, 0f, InputButtons.None));
            reactions.AddRange(w.LastReactions);
        }
        // About 2.5 m: intensity 1 → stun; 6.5 m or more (the dive leans toward +Y): intensity under 0.25 → below every entry → none.
        var e = Assert.Single(reactions);
        Assert.Equal((2, ReactionKind.Stun, 0), (e.Target, e.Kind, e.PushDistance));
        Assert.Equal(ReactionKind.None, w.Players[3].Reaction);
        // The entry for an intensity: the largest threshold not above it.
        ReactionEntry[] pound = [new(0.81f, ReactionKind.Stun), new(0.8f, ReactionKind.Interrupt)];
        Assert.Equal(ReactionKind.Stun, Reaction.Pick(pound, 0.81f)!.Value.Kind);
        Assert.Equal(ReactionKind.Interrupt, Reaction.Pick(pound, 0.805f)!.Value.Kind);
        Assert.Null(Reaction.Pick(pound, 0.79f));
    }

    [Fact]
    public void A_stomp_kill_is_a_melee_kill_and_nothing_reacts_past_the_last_fall_off_point()
    {
        // A lethal stomp: the kill feed and the ragdoll take it as melee (review fix), not as a weapon with no index.
        var lethal = Config with
        {
            Classes = [.. Config.Classes.Take(2), Config.Classes[Stomper] with { MeleeHeavy = [HeavyStep(default, default, StompBlast with { Damage = Plain with { Damage = 5000f } })] }],
        };
        var w = Arena(lethal, null, (Stomper, Vector3.Zero), (Tactical, new Vector3(3, 0, 0)));
        var kills = new List<KillEvent>();
        Run(w, 14, (k, id) => id == 1 && k == 0 ? Press(InputButtons.MeleeHeavy) : None, _ => kills.AddRange(w.LastKills));
        var kill = Assert.Single(kills);
        Assert.Equal((1, 2, KillCause.Melee), (kill.Killer, kill.Victim, kill.Cause));

        // Zero intensity (inside the radius, past a fall-off point of 0): no damage and no reaction.
        var faded = Config with
        {
            Classes = [.. Config.Classes.Take(2), Config.Classes[Stomper] with { MeleeHeavy = [HeavyStep(default, default, StompBlast with { FallOff = [new(0f, 1f), new(4f, 0f)] })] }],
        };
        var w2 = Arena(faded, null, (Stomper, Vector3.Zero), (Tactical, new Vector3(5, 0, 0)));
        var reactions = new List<ReactionEvent>();
        Run(w2, 14, (k, id) => id == 1 && k == 0 ? Press(InputButtons.MeleeHeavy) : None, _ => reactions.AddRange(w2.LastReactions));
        Assert.Empty(reactions);
        Assert.Equal(1000f, w2.Players[2].Health);
    }

    [Fact]
    public void A_blast_blinds_allies_too_but_reacts_only_on_those_it_may_harm()
    {
        var blinding = Config with
        {
            Classes = [.. Config.Classes.Take(2), Config.Classes[Stomper] with { MeleeHeavy = [HeavyStep(default, default, StompBlast with { Blindness = 2f })] }],
        };
        var world = new World(blinding, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(2, 0, 0), 0f), new SpawnPoint(new Vector3(-2, 0, 0), 0f)])
            { Rules = new MatchRules { TimeLimit = 0f } };
        world.Spawn(1, Stomper);    // marines
        world.Spawn(2, Assault);    // chaos
        world.Spawn(3, Tactical);   // marines: an ally of 1
        Assert.Equal((Team.SpaceMarines, Team.Chaos, Team.SpaceMarines), (world.Players[1].Team, world.Players[2].Team, world.Players[3].Team));
        Run(world, 11, (k, id) => id == 1 && k == 0 ? Press(InputButtons.MeleeHeavy) : None);   // the stomp lands in the 11th tick
        Assert.Equal((ReactionKind.Stagger, 60), (world.Players[2].Reaction, world.Players[2].BlindTicks));   // 2 s = 60 ticks
        Assert.Equal((ReactionKind.None, 60), (world.Players[3].Reaction, world.Players[3].BlindTicks));
        Assert.Equal(0, world.Players[1].BlindTicks);   // ignore_self
        Run(world, 59, (_, _) => None);
        Assert.Equal((1, 1), (world.Players[2].BlindTicks, world.Players[3].BlindTicks));
        Run(world, 1, (_, _) => None);
        Assert.Equal((0, 0), (world.Players[2].BlindTicks, world.Players[3].BlindTicks));
    }

    // ---------------------------------------------------------------- immunity and gating

    [Fact]
    public void The_evades_hyper_armour_negates_a_reaction_but_not_the_damage_and_after_it_the_reaction_ends_the_evade()
    {
        foreach (var (pressAt, armoured) in new[] { (4, true), (-12, false) })
        {
            // The target rolls (Jump with the stick to the side); the attacker's heavy strike lands at tick 10.
            var w = Arena(Long, null, (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 3, 0)));
            int offset = Math.Max(0, -pressAt);
            float health = w.Players[2].Health;
            var reactions = new List<ReactionEvent>();
            Run(w, offset + 11, (k, id) => id == 1 ? (k == offset ? Press(InputButtons.MeleeHeavy) : None)
                : k == offset + pressAt ? (InputButtons.Jump, 1f, 0f) : None, _ => reactions.AddRange(w.LastReactions));
            var target = w.Players[2];
            Assert.True(target.Health < health);   // damage applies either way
            if (armoured)
            {
                Assert.Empty(reactions);
                Assert.Equal((ReactionKind.None, 7), (target.Reaction, target.EvadeTicks));   // 0.23 s of the roll: within its 0.3 s
            }
            else
            {
                Assert.Equal(ReactionKind.Stun, Assert.Single(reactions).Kind);
                Assert.Equal((0, ReactionKind.Stun), (target.EvadeTicks, target.Reaction));   // the roll is over
                Assert.True(target.EvadeLock > 0);                                              // its spacing stays
            }
        }
    }

    [Fact]
    public void Jump_pack_flight_is_immune_but_a_landing_is_not()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Assault }, Config);
        foreach (var phase in new[] { JumpPhase.Ascent, JumpPhase.Hang, JumpPhase.Fall, JumpPhase.Dive })
            Assert.False(Reaction.CanReact(s with { Jump = phase }, Config));
        foreach (var phase in new[] { JumpPhase.None, JumpPhase.TakeOff, JumpPhase.Landed, JumpPhase.Recover })
            Assert.True(Reaction.CanReact(s with { Jump = phase }, Config));
        var landed = Reaction.Start(s with { Jump = JumpPhase.Landed, JumpTicks = 3, JumpLandMoving = true }, Config, Interrupt, ReactionDirection.Front, Vector2.UnitY);
        Assert.Equal((JumpPhase.None, 0, false), (landed.Jump, landed.JumpTicks, landed.JumpLandMoving));
        Assert.False(Reaction.CanReact(s with { Alive = false }, Config));
    }

    [Fact]
    public void An_attack_in_its_window_without_HitReact_is_not_interrupted_and_outside_it_is()
    {
        foreach (var (victimPress, interrupted) in new[] { (1, false), (4, true) })
        {
            // Both swing at each other: the attacker's hit lands at tick 7, when the victim's own attack is at tick 7 − press.
            var w = Arena(Config, null, (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 2, 0)));
            Run(w, 8, (k, id) => (id == 1 && k == 0) || (id == 2 && k == victimPress) ? Press(InputButtons.Melee) : None);
            var victim = w.Players[2];
            if (interrupted) Assert.Equal((ReactionKind.Interrupt, 0), (victim.Reaction, victim.MeleeStep));
            else Assert.Equal((ReactionKind.None, 1), (victim.Reaction, victim.MeleeStep));
        }
    }

    [Fact]
    public void A_reaction_is_replaced_only_from_its_HitReact_branch()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Tactical }, Config);
        var front = Reaction.Placeholders[0].Front;   // ReactAt 0.373 s
        var interrupted = Reaction.Start(s, Config, Interrupt, ReactionDirection.Front, Vector2.UnitY);
        Assert.False(Reaction.CanReact(interrupted with { ReactionTicks = 11 }, Config));   // 0.367 s
        Assert.True(Reaction.CanReact(interrupted with { ReactionTicks = 12 }, Config));    // 0.4 s
        Assert.True(front.ReactAt is > 0.36f and < 0.4f);
        var stunned = Reaction.Start(s, Config, Stun, ReactionDirection.Front, Vector2.UnitY);   // no HitReact: never before its end
        Assert.False(Reaction.CanReact(stunned with { ReactionTicks = 15 }, Config));
    }

    // ---------------------------------------------------------------- interrupts, blocking, ending

    [Fact]
    public void A_reaction_interrupts_a_reload_a_charge_a_throw_an_attack_and_an_evade_and_stops_the_body()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Tactical }, Config) with
        {
            ReloadLeft = 0.5f, Charging = true, ChargeTime = 1f, PendingShot = 2, PendingLeft = 0.1f, Throwing = true, ThrowTime = 0.1f,
            MeleeStep = 2, MeleeTime = 0.4f, MeleeQueued = true, MeleeQueuedHeavy = true, MeleeHeavy = true, EvadeTicks = 20, EvadeLock = 4,
            Aiming = true, Velocity = new Vector3(5, 6, -1), Grenades = 1,
        };
        var r = Reaction.Start(s, Config, Stagger, ReactionDirection.Back, -Vector2.UnitX);
        Assert.Equal((ReactionKind.Stagger, ReactionDirection.Back, 0), (r.Reaction, r.ReactionDirection, r.ReactionTicks));
        Assert.Equal((0f, false, 0f, 0, 0f, false, 0f), (r.ReloadLeft, r.Charging, r.ChargeTime, r.PendingShot, r.PendingLeft, r.Throwing, r.ThrowTime));
        Assert.Equal((0, 0f, false, false, false), (r.MeleeStep, r.MeleeTime, r.MeleeQueued, r.MeleeQueuedHeavy, r.MeleeHeavy));
        Assert.Equal((0, 4, false, 1), (r.EvadeTicks, r.EvadeLock, r.Aiming, r.Grenades));   // the throw had not released: nothing spent
        Assert.Equal(new Vector3(0, 0, -1), r.Velocity);
        Assert.Equal((48, 9, 64), (r.PushDistance, r.PushTicks, r.PushYaw));                 // 3 m, 0.3 s; −X is yaw π/2
    }

    [Fact]
    public void While_reacting_inputs_do_nothing_but_turn_and_held_buttons_stay_held_then_the_reaction_ends_on_time()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Tactical }, Config);
        s = Reaction.Start(s, Config, Stun, ReactionDirection.Front, Vector2.UnitY);   // F clip: ends at 0.533 s (16 ticks)
        int clip = s.Clip;
        var all = InputButtons.Fire | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Grenade | InputButtons.Reload | InputButtons.Switch | InputButtons.Aim | InputButtons.Sprint;
        int reacting = 0;
        for (int k = 0; k < 16; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 1f, 1f, 0.3f * k, all), Config, out var e);
            if (s.Reaction != ReactionKind.None) reacting++;
            Assert.False(e.Fired);
            Assert.Equal(0, e.MeleeHitStep);
            Assert.False(e.Threw);
            Assert.Equal((0, false, WeaponSlot.Ranged, false), (s.MeleeStep, s.Throwing, s.Slot, s.Aiming));
            Assert.True(s.FireHeld && s.MeleeHeld && s.MeleeHeavyHeld && s.GrenadeHeld && s.ReloadHeld && s.SwitchHeld);
            Assert.Equal(0.3f * k, s.Yaw, 5);   // turning applies
        }
        Assert.Equal(16, reacting);
        Assert.Equal(clip, s.Clip);
        Assert.True(Vector2.Distance(Vector2.Zero, new(s.Position.X, s.Position.Y)) is > 1.99f and < 2.01f);   // only the knockback moved it
        // Tick 17: 16 ticks (0.533 s) played: over; the held fire (full auto) now shoots, the stick moves.
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Fire), Config, out var after);
        Assert.Equal(ReactionKind.None, s.Reaction);
        Assert.True(after.Fired);
        Assert.True(s.Velocity.Y > 0f);
    }

    [Fact]
    public void Jump_melee_and_the_stick_end_a_reaction_from_their_branches_and_act_in_that_tick()
    {
        // sim/combat-2 (item 36): the light kinds hold nobody; the stun (front clip) is the holding one: Evade from 0.333 s (10 ticks): the "stun break" roll;
        // it has no melee or run branch of its own: both fall back to its end, 0.533 s (16 ticks).
        EndsAt(InputButtons.Jump, 0f, 9, 10);
        EndsAt(InputButtons.Melee, 0f, 15, 16);
        EndsAt(InputButtons.MeleeHeavy, 0f, 15, 16);
        EndsAt(InputButtons.None, 1f, 15, 16);
    }

    /// <summary>
    /// A back interrupt (BHR: Evade 0.2 s, Melee 0.72 s, Run = End 0.92 s); <paramref name="button"/> pressed (or the stick
    /// pushed) after <paramref name="notBefore"/> ticks does not end it, after <paramref name="at"/> ticks it does, in that tick.
    /// </summary>
    static void EndsAt(InputButtons button, float stick, int notBefore, int at)
    {
        PlayerState Played(int ticks)
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Tactical }, Config);
            s = Reaction.Start(s, Config, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.UnitY);
            for (int k = 0; k < ticks; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Config, out _);
            return s;
        }
        var early = Combat.StepPlayer(Played(notBefore), new PlayerInput(0, stick, 0f, 0f, button), Config, out _);
        Assert.Equal(ReactionKind.Stun, early.Reaction);
        var late = Combat.StepPlayer(Played(at), new PlayerInput(0, stick, 0f, 0f, button), Config, out _);
        Assert.Equal(ReactionKind.None, late.Reaction);
        if (button == InputButtons.Jump) Assert.True(late.EvadeTicks > 0);              // the roll started
        if (button == InputButtons.Melee) Assert.Equal((1, false), (late.MeleeStep, late.MeleeHeavy));
        if (button == InputButtons.MeleeHeavy) Assert.Equal((1, true), (late.MeleeStep, late.MeleeHeavy));
        if (stick != 0f) Assert.True(late.Velocity.X > 0f);
    }

    // ---------------------------------------------------------------- knockback and collision

    static CollisionWorld Walled(float wallY)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0));
        Quad(new(-50, wallY, -1), new(50, wallY, -1), new(50, wallY, 4), new(-50, wallY, 4));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    [Fact]
    public void Knockback_moves_the_body_through_the_capsule_controller_and_a_wall_stops_it()
    {
        // Open ground: the full 3 m of the stagger (X then Y).
        foreach (var (wall, expected) in new[] { (100f, 3f), (3.5f, 3.5f - 2f - Config.CapsuleRadius) })
        {
            var w = Arena(Config, Walled(wall), (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 2, 0)));
            var reactions = new List<ReactionEvent>();
            Run(w, 50, (k, id) => id != 1 ? None : k == 0 ? Press(InputButtons.Melee) : k == 10 ? Press(InputButtons.MeleeHeavy) : None,
                _ => reactions.AddRange(w.LastReactions));
            Assert.Contains(reactions, r => r.Kind == ReactionKind.Stagger && r.PushDistance == 48);
            float moved = w.Players[2].Position.Y - 2f;
            output.WriteLine($"wall at {wall}: moved {moved:F3} m");
            Assert.Equal(expected, moved, 2);
            Assert.True(w.Players[2].Position.Y + Config.CapsuleRadius <= wall + 1e-3f);
        }
    }

    // ---------------------------------------------------------------- prediction

    /// <summary>
    /// A config for the network test: no damage, long reactions (2 s clips) and long knockbacks, so that reactions are still
    /// running and pushing well after the client learns of them.
    /// </summary>
    static SimConfig NetConfig()
    {
        static ReactionClip Slow(string name) => new(name, 2f, 1.6f, 1.6f, 1.2f, 1.4f, 0.9f);
        var specs = new[]
        {
            new ReactionSpec(ReactionKind.Interrupt, false, Slow("i"), Slow("i"), Slow("i"), Slow("i")),
            new ReactionSpec(ReactionKind.Stun, true, Slow("sf"), Slow("sr"), Slow("sb"), Slow("sl")),
            new ReactionSpec(ReactionKind.Stagger, false, Slow("g"), Slow("g"), Slow("g"), Slow("g")),
        };
        var light = Light with { Damage = 0f, SecondaryDamage = 0f };
        IReadOnlyList<MeleeStep> heavy =
        [
            HeavyStep(Stun with { Backward = 4f, Duration = 1.1f }, Interrupt) with { Damage = 0f, SecondaryDamage = 0f },
            HeavyStep(Stagger with { Backward = 5f, Duration = 1.3f }, Interrupt) with { Damage = 0f, SecondaryDamage = 0f },
        ];
        return Long with
        {
            Reactions = specs,
            Weapons = [Pistol with { Damage = 0f, ReserveSize = 5000 }, Rifle with { Damage = 0f, ReserveSize = 5000 }],
            Classes = Config.Classes.Select(k => k with { Melee = [light, light, light], MeleeHeavy = heavy, Side = Team.None }).ToArray(),
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
    }

    [Theory]
    [InlineData(281)]
    [InlineData(282)]
    public void Prediction_matches_the_server_through_reactions_and_knockbacks_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = NetConfig();
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 3; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = Tactical });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        var reactedAt = new HashSet<(uint Tick, int Target)>();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Melee);
            uint phase = (t + (uint)i * 37) % 150;
            if (phase is 0 or 1) buttons |= InputButtons.MeleeHeavy;                                // heavy 1: stun, 4 m knockback
            if (phase is 60 or 61) buttons |= InputButtons.Melee;                                   // light 1 …
            if (phase is 70 or 71) buttons |= InputButtons.MeleeHeavy;                              // … then heavy 2: stagger, 5 m
            if (phase is 100 or 101 or 115 or 116) buttons |= InputButtons.Jump;                    // rolls (the stun break)
            if (phase is 30 or 31 or 32) buttons |= InputButtons.Fire;
            return move with { Buttons = buttons, Pitch = -0.2f };
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            foreach (var r in h.Server.World.LastReactions) reactedAt.Add((r.Tick, r.Target));
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        // A client cannot predict being hit: ticks within Window of a reaction started on it are not compared.
        const uint Window = 16;
        int reactingCompared = 0, pushingCompared = 0;
        for (int i = 0; i < n; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            var starts = reactedAt.Where(r => r.Target == id).Select(r => r.Tick).ToList();
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                if (starts.Any(s => s <= tick && tick < s + Window)) continue;
                compared++;
                if (real.Reaction != ReactionKind.None) reactingCompared++;
                if (real.Reaction != ReactionKind.None && real.ReactionTicks <= real.PushTicks) pushingCompared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; reactions on it {starts.Count}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.Empty(mismatches);
        }
        output.WriteLine($"ticks compared during reactions {reactingCompared}, during knockbacks {pushingCompared}; reactions {reactedAt.Count}");
        Assert.True(reactedAt.Count >= 10, $"only {reactedAt.Count} reactions");
        Assert.True(reactingCompared >= 100, $"only {reactingCompared} reacting ticks compared");
        Assert.True(pushingCompared >= 20, $"only {pushingCompared} knockback ticks compared");
    }

    [Fact]
    public void The_own_record_carries_everything_a_reaction_replays_from()
    {
        // Server side: a stagger with knockback against a wall; the client replays the same inputs from the record.
        var w = Arena(Config, Walled(4f), (Tactical, Vector3.Zero), (Tactical, new Vector3(0, 2, 0)));
        Run(w, 24, (k, id) => id != 1 ? None : k == 0 ? Press(InputButtons.Melee) : k == 10 ? Press(InputButtons.MeleeHeavy) : None);
        var victim = w.Players[2];
        Assert.Equal((0, 48), (victim.ReactionTicks, victim.PushDistance));   // heavy 2 lands in the 24th tick
        Assert.Equal(ReactionKind.Stagger, victim.Reaction);
        var record = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(w.Tick, 0, 0, [victim], Self: 2)))).Players[0];
        Assert.Equal(victim, record);
        var server = victim; var client = record;
        for (int k = 0; k < 40; k++)
        {
            var input = new PlayerInput((uint)k, k % 5 == 0 ? 1f : 0f, 0f, 0.1f * k, k % 7 == 0 ? InputButtons.Jump | InputButtons.Fire : InputButtons.None);
            server = Combat.StepPlayer(server, input, Config, out _, w.Collision);
            client = Combat.StepPlayer(client, Protocol.Quantize(input), Config, out _, w.Collision);
            Assert.Equal(server, client);
        }
    }

    // ---------------------------------------------------------------- protocol 17

    [Fact]
    public void Protocol_17_carries_reactions_and_heavy_strikes_in_every_record_and_the_knockback_and_blindness_in_the_own()
    {
        Assert.Equal(49, Protocol.Version);
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true)
        {
            Alive = true, Reaction = ReactionKind.Stagger, ReactionDirection = ReactionDirection.Left, ReactionTicks = 7,
            PushYaw = 200, PushDistance = 48, PushTicks = 9, BlindTicks = 105, MeleeHeavyHeld = true,
        };
        var other = new PlayerState(4, Vector3.One, Vector3.Zero, 1f, true) { Alive = true, MeleeStep = 3, MeleeTime = 0.25f, MeleeHeavy = true, BlindTicks = 9, PushDistance = 3, PushTicks = 1 };
        var third = new PlayerState(5, Vector3.One, Vector3.Zero, 1f, true) { Alive = true, Reaction = ReactionKind.Stun, ReactionDirection = ReactionDirection.Right, ReactionTicks = 255 };
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 1, 1, [me, other, third], Self: 3))));
        Assert.Equal(me, back.Players[0]);
        Assert.Equal(Protocol.Slim(other), back.Players[1]);
        Assert.Equal((3, 0.25f, true, 0, 0), (back.Players[1].MeleeStep, back.Players[1].MeleeTime, back.Players[1].MeleeHeavy, back.Players[1].BlindTicks, back.Players[1].PushDistance));
        Assert.Equal((ReactionKind.Stun, ReactionDirection.Right, 255), (back.Players[2].Reaction, back.Players[2].ReactionDirection, back.Players[2].ReactionTicks));
        // A reaction clears the melee step on the wire (they never coexist in the simulation).
        Assert.Equal(0, Protocol.Slim(third with { MeleeStep = 2, MeleeTime = 1f }).MeleeStep);
        // The heavy buffered press.
        var queued = me with { Reaction = ReactionKind.None, ReactionDirection = ReactionDirection.Front, ReactionTicks = 0, PushDistance = 0, PushTicks = 0, PushYaw = 0, MeleeStep = 1, MeleeQueued = true, MeleeQueuedHeavy = true };
        Assert.Equal(queued, Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 1, 1, [queued], Self: 3)))).Players[0]);
    }

    [Fact]
    public void Protocol_17_refuses_malformed_melee_bytes_and_knockbacks()
    {
        var p = new PlayerState(4, Vector3.One, Vector3.Zero, 1f, true) { Alive = true, MeleeStep = 2, MeleeTime = 0.5f };
        var slim = Protocol.Write(new Snapshot(9, 1, 1, [p]));
        // The slim record ends: … slot [^31], melee byte [^30], time [^29..^26], evade [^25], then 24 bytes of blocks (match,
        // kill count, point count, hits, shot count).
        Assert.Equal(2, slim[^30]);
        Assert.NotNull(Protocol.Read(slim));
        byte[] With(byte melee, float? time = null)
        {
            var b = (byte[])slim.Clone(); b[^30] = melee;
            if (time is float t) BitConverter.GetBytes(t).CopyTo(b, b.Length - 29);
            return b;
        }
        Assert.NotNull(Protocol.Read(With(64 | 2)));             // heavy step 2
        Assert.Null(Protocol.Read(With(64)));                    // heavy without a step
        Assert.Null(Protocol.Read(With(128, 3f)));               // a reaction of kind 0
        Assert.NotNull(Protocol.Read(With(128 | (2 << 2) | 1, 3f)));
        Assert.Null(Protocol.Read(With(128 | 16 | (2 << 2), 3f)));   // unknown bits with a reaction
        Assert.Null(Protocol.Read(With(128 | (2 << 2), 3.5f)));  // ticks not whole
        Assert.Null(Protocol.Read(With(128 | (2 << 2), 256f)));  // ticks beyond a byte
        Assert.Null(Protocol.Read(With(128 | (2 << 2), float.NaN)));

        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, Reaction = ReactionKind.Stun, ReactionTicks = 1, PushYaw = 7, PushDistance = 16, PushTicks = 3 };
        var own = Protocol.Write(new Snapshot(9, 1, 1, [me], Self: 3));
        Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0]);
        // The own record ends with yaw [^32], distance [^31], ticks [^30], blindness [^29], (protocol 18) the charge lock [^28] and (protocol 20) the ready and raise ticks [^27..^26] (u16 since protocol 21), [^25].
        Assert.Equal((7, 16, 3), ((int)own[^32], (int)own[^31], (int)own[^30]));
        var noTicks = (byte[])own.Clone(); noTicks[^30] = 0;
        Assert.Null(Protocol.Read(noTicks));                     // a distance without ticks
        var yawOnly = (byte[])own.Clone(); yawOnly[^31] = 0; yawOnly[^30] = 0;
        Assert.Null(Protocol.Read(yawOnly));                     // a yaw without a knockback
        var outside = Protocol.Write(new Snapshot(9, 1, 1, [me with { Reaction = ReactionKind.None, ReactionTicks = 0 }], Self: 3));
        Assert.Null(Protocol.Read(outside));                     // a knockback outside a reaction
    }

    [Fact]
    public void The_fullest_snapshot_with_every_player_reacting_carries_the_guaranteed_shots_within_the_budget()
    {
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new PlayerState(i, new Vector3(1e4f), new Vector3(100f), 3f, true)
        {
            Alive = true, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Primary = 254, Pistol = 253, MeleeChoice = 254,
            Reaction = ReactionKind.Stagger, ReactionDirection = ReactionDirection.Left, ReactionTicks = 255,
            PushYaw = 255, PushDistance = 255, PushTicks = 255, BlindTicks = 255, MeleeHeavyHeld = true,
            EvadeTicks = 63, EvadeDirection = EvadeDirection.BackLeft,
        }).ToArray();
        var projectiles = Enumerable.Range(0, Protocol.MaxSnapshotProjectiles).Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(k), ProjectileFlags.Stuck)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Playing, Team.None, uint.MaxValue, 65535, 65535),
            Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(65535 - k, 65535, 1, KillCause.Thrown, 254, Vector3.UnitX))],
            Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)],
            Shots: ShotTests.ManyShots(players, 40)));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        output.WriteLine($"protocol {Protocol.Version}: {bytes.Length} bytes with {back.Shots!.Length} shot records (budget {Protocol.SnapshotBudget})");
        // Protocol 17: the own record's 4 reaction bytes cost the fifth guaranteed shot record of protocol 16 (1199 bytes).
        Assert.Equal((1194, Protocol.MaxSnapshotBytes, 4, Protocol.MinSnapshotShots), (bytes.Length, bytes.Length, back.Shots.Length, back.Shots.Length));
        Assert.True(bytes.Length <= Protocol.SnapshotBudget && bytes.Length + Protocol.ShotBytes > Protocol.SnapshotBudget);
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);
    }

    [Fact]
    public void The_config_hash_covers_reactions_heavy_strikes_and_blast_effects()
    {
        var seen = new HashSet<ulong> { Config.Hash() };
        var classes = Config.Classes.ToArray();
        SimConfig WithHeavy(Func<MeleeStep, MeleeStep> change)
        {
            var k = classes.ToArray();
            k[0] = k[0] with { MeleeHeavy = [change(Heavies[0]), .. Heavies.Skip(1)] };
            return Config with { Classes = k };
        }
        SimConfig WithLight(Func<MeleeStep, MeleeStep> change)
        {
            var k = classes.ToArray();
            k[0] = k[0] with { Melee = [change(Light), Light, Light] };
            return Config with { Classes = k };
        }
        foreach (var c in new[]
        {
            WithHeavy(m => m with { PrimaryReaction = m.PrimaryReaction with { Backward = 2.5f } }),
            WithHeavy(m => m with { SecondaryReaction = default }),
            WithLight(m => m with { NoReactTo = m.NoReactTo + 0.01f }),
            WithHeavy(m => m with { Stomp = StompBlast }),
            WithHeavy(m => m with { Stomp = StompBlast with { Blindness = 1f } }),
            Config with { Reactions = [.. Reaction.Placeholders.Take(2)] },
            Config with { Reactions = [Reaction.Placeholders[0] with { Front = Reaction.Placeholders[0].Front with { EvadeAt = 0.5f } }, .. Reaction.Placeholders.Skip(1)] },
        })
            Assert.True(seen.Add(c.Hash()), "a change left the hash unchanged");
    }

    // ---------------------------------------------------------------- sim/combat-2 (item 36): the light reactions hold nobody

    static PlayerState Reacting(ReactionEntry entry, int cls = Tactical)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls }, Config);
        return Reaction.Start(s, Config, entry, ReactionDirection.Front, Vector2.UnitY);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("jump")]
    [InlineData("melee")]
    [InlineData("heavy")]
    [InlineData("fire")]
    [InlineData("aim")]
    [InlineData("reload")]
    public void An_interrupt_is_left_by_any_action_on_the_next_tick_a_stagger_and_a_stun_are_not(string action)
    {
        var input = action switch
        {
            "move" => new PlayerInput(0, 0f, 1f, 0f, InputButtons.None),
            "jump" => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Jump),
            "melee" => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee),
            "heavy" => new PlayerInput(0, 0f, 0f, 0f, InputButtons.MeleeHeavy),
            "fire" => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire),
            "aim" => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Aim),
            _ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Reload),
        };
        foreach (var entry in new[] { Interrupt })
        {
            var s = Reacting(entry);
            if (action == "reload") s = s with { Clip = 5 };
            // One quiet tick (the clip plays), then the action: the reaction ends in that very tick and the action happens.
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Config, out _);
            Assert.Equal(entry.Kind, s.Reaction);
            s = Combat.StepPlayer(s, input, Config, out var e);
            Assert.Equal(ReactionKind.None, s.Reaction);
            switch (action)
            {
                case "move": Assert.True(s.Velocity.Y > 0f); break;
                case "jump": Assert.True(s.EvadeTicks > 0); break;
                case "melee": Assert.Equal((1, false), (s.MeleeStep, s.MeleeHeavy)); break;
                case "heavy": Assert.Equal((1, true), (s.MeleeStep, s.MeleeHeavy)); break;
                case "fire": Assert.True(e.Fired); break;
                case "aim": Assert.True(s.Aiming); break;
                default: Assert.True(s.ReloadLeft > 0f); break;
            }
        }
        // The stagger and the stun hold (owner, review of sim/combat-2): the same input on the same tick does nothing.
        foreach (var heavy in new[] { new ReactionEntry(0f, ReactionKind.Stagger), Stun })
        {
            var held = Combat.StepPlayer(Combat.StepPlayer(Reacting(heavy), new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Config, out _), input, Config, out var fired);
            Assert.Equal(heavy.Kind, held.Reaction);
            Assert.False(fired.Fired);
            Assert.Equal((0, 0), (held.MeleeStep, held.EvadeTicks));
        }
    }

    [Fact]
    public void An_interrupt_still_interrupts_the_action_it_hits_and_pushes_before_input_takes_over()
    {
        // The hit stops the swing (Reaction.Start), the knockback runs its 9 ticks even with the stick held, then the stick moves the body.
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Tactical }, Config);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), Config, out _);
        Assert.Equal(1, s.MeleeStep);
        s = Reaction.Start(s, Config, Interrupt with { Backward = 3f, Duration = 0.3f }, ReactionDirection.Front, Vector2.UnitY);
        Assert.Equal((0, ReactionKind.Interrupt), (s.MeleeStep, s.Reaction));
        for (int k = 0; k < 9; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), Config, out _);
            Assert.Equal(ReactionKind.Interrupt, s.Reaction);
        }
        Assert.True(MathF.Abs(s.Position.Y - 3f) < 0.05f, $"pushed {s.Position.Y}");
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.None), Config, out _);
        Assert.Equal(ReactionKind.None, s.Reaction);
        Assert.True(s.Velocity.Y > 0f);
    }

    [Fact]
    public void A_light_reaction_runs_to_its_end_when_nothing_is_pressed_and_only_the_stun_holds()
    {
        var s = Reacting(Interrupt);
        int k = 0;
        while (s.Reaction != ReactionKind.None && k < 60) { s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Config, out _); k++; }
        Assert.InRange(k, 29, 36);   // the interrupt front clip's end, 0.98 s
        Assert.False(Reaction.Holds(ReactionKind.Interrupt));
        Assert.True(Reaction.Holds(ReactionKind.Stagger) && Reaction.Holds(ReactionKind.Stun));
    }
}
