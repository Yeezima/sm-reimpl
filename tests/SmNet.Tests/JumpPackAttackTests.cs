using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Protocol 30 (branch sim/jump-pack-and-falls; the owner's descriptions and answers of 2026-10-28): the walk-off drop (straight down, no air
/// steering), a charge in the air keeps its speed until it lands, the Assault / Raptor dash attack (a melee press in the dash's window: the
/// lunge, its stun, Furious Charge's values, its run-out and wall stop) and the jump pack's sword slash (below 8 m at the press, the melee cone
/// with the light interrupt, no stun, knockback or pound blast); prediction under 150 ms RTT and 5% loss; the wire and the config hash.
/// Synthetic specs shaped like the install's values.
/// </summary>
public class JumpPackAttackTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    const int Assault = 0, Marine = 1;
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static Vector2[] Line(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];

    // The dash attack: a 12 m/s lunge to BRANCH_toIdle at 25 ticks (0.833 s), its hit state, run-out and wall stop.
    static readonly RushMove Lunge = new("SM_JMP_DashAtk_F", Line(24f, 24), 2f, 25f / 30f, 25f / 30f, 25f / 30f, 25f / 30f);
    static readonly RushMove HitState = new("SM_MEL_PA_XATK_Combo3FIN", [Vector2.Zero, Vector2.Zero], 0.7f, 0.7f, 19f / 30f, 20f / 30f, 0.7f);
    static readonly RushMove RunOut = new("SM_NAV_Sprint_to_Idle_F", Line(3f, 14), 1.4f, 27f / 30f, 14f / 30f, 14f / 30f, 14f / 30f);
    static readonly RushMove WallStop = new("SM_NAV_Bullrush2Idle_F_Collision_01", Line(0.5f, 10), 32f / 30f, 31f / 30f, 8f / 30f, 20f / 30f, 31f / 30f);
    static readonly RushSpec DashCharge = new("ability_dash_attack", Lunge, WallStop, Plain with { Damage = 75f, ShieldDamage = 25f }, new ReactionEntry(0f, ReactionKind.Stun),
        Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f) { ContactUntil = 25f / 30f, WallFrom = 0f, WallHitAngle = 60f, HyperArmourUntil = 0f };   // feel/jump-pack-2: walls from the first tick, 60 degrees (the reader's)
    internal static readonly DashAttackSpec DashAttack = new(DashCharge, HitState, RunOut) { MaxHeight = 3f };
    // The slash: 60 melee and the interrupt to everyone in the cone 4 ticks after the impact; acts again at 19 ticks.
    internal static readonly SlashSpec Slash = new("ability_melee_dash", "SM_MEL_CS_XATK_Hv1_LS_2_7m", 1.7333f, 4f / 30f, 19f / 30f, Plain with { Damage = 60f, Type = DamageType.Melee },
        new ReactionEntry(0f, ReactionKind.Interrupt), 4f);
    const string Furious = "mp_perk_improved_bullrush";

    static EvadeMove Window(EvadeMove m) => m with { DashAttackAt = 10f / 30f };
    static readonly EvadeSpec Dash = JumpPackDashTests.Dash with
    {
        Forward = Window(JumpPackDashTests.Dash.Forward), Back = Window(JumpPackDashTests.Dash.Back), Left = Window(JumpPackDashTests.Dash.Left), Right = Window(JumpPackDashTests.Dash.Right),
    };
    static readonly JumpPackSpec BasePack = JumpPackDashTests.Config.JumpPacks[0] with { DashAttack = DashAttack, Slash = Slash };
    static readonly JumpPackSpec Pack = BasePack with
    {
        Variants = [new([Furious], BasePack with { DashAttack = DashAttack with { Charge = DashCharge with { Primary = Plain with { Damage = 100f, ShieldDamage = 50f } } } })],
    };

    /// <summary>The Assault (pack, dash, dash attack, slash; Furious Charge choosable) and a marine with the bull rush (RushTests' 10 m/s, 1.2 s clip).</summary>
    internal static readonly SimConfig Config = JumpPackDashTests.Config with
    {
        Classes =
        [
            JumpPackDashTests.Config.Classes[0] with { PerkChoices = [0] },
            JumpPackDashTests.Config.Classes[1] with { MeleeRush = RushTests.Spec },
        ],
        JumpPacks = [Pack], Evades = [Dash],
        Perks = [new PerkSpec("Furious Charge", Furious, PerkKind.Class, 1)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>The M14 pack (no dash: Space always jumps) with the slash, for the pound-or-slash tests.</summary>
    static readonly SimConfig JumpConfig = JumpPackTests.Config with { JumpPacks = [JumpPackTests.Pack with { Slash = Slash }] };

    static PlayerState Fresh(SimConfig c, int cls, Vector3 at = default, float yaw = 0f, int perk = -1) =>
        Combat.Respawned(new PlayerState(1, at, Vector3.Zero, yaw, true) { Class = cls, Perk1 = perk }, c);

    static SelfEvents Step(ref PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f, float pitch = 0f, CollisionWorld? world = null)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b, pitch), c, out var e, world);
        return e;
    }

    // ---------------------------------------------------------------- level pieces

    sealed class Level
    {
        readonly List<Vector3> _v = new();
        readonly List<int> _i = new();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = _v.Count; _v.AddRange([a, b, c, d]); _i.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        public Level Box(Vector3 min, Vector3 max)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0)); Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1));
            Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0)); Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1)); Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
            return this;
        }
        public CollisionWorld World() { var m = new StaticMesh(_v.ToArray(), _i.ToArray()); return new CollisionWorld(m, m); }
    }

    /// <summary>A floor at z = 0 and a plateau <paramref name="height"/> m high whose edge is the line y = 0 (the plateau at y &lt; 0).</summary>
    static CollisionWorld Ledge(float height) => new Level().Box(new(-100, -100, -1), new(100, 200, 0)).Box(new(-100, -100, 0), new(100, 0, height)).World();

    // ---------------------------------------------------------------- 1. the walk-off drop

    /// <summary>Walks (or sprints) +Y off a plateau 3 m high from 5 m before its edge; the stick then pushes sideways (no steering in the air).</summary>
    (PlayerState Left, PlayerState Landed, List<PlayerState> Air) WalkOff(bool sprint)
    {
        var world = Ledge(3f);
        var s = Fresh(Config, Marine, new Vector3(0, -5f, 3f));
        PlayerState? left = null, landed = null;
        var air = new List<PlayerState>();
        for (int k = 0; k < 120 && landed is null; k++)
        {
            bool inAir = left is not null;
            Step(ref s, Config, sprint && !inAir ? InputButtons.Sprint : InputButtons.None, inAir ? 1f : 0f, inAir ? 0.3f : 1f, world: world);
            if (left is null && !s.Grounded) left = s;
            else if (left is not null && !s.Grounded) air.Add(s);
            else if (left is not null && s.Grounded) landed = s;
        }
        Assert.NotNull(left); Assert.NotNull(landed);
        return (left!.Value, landed!.Value, air);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Walking_or_sprinting_off_a_ledge_drops_straight_down_without_air_steering(bool sprint)
    {
        var (left, landed, air) = WalkOff(sprint);
        float speed = new Vector2(left.Velocity.X, left.Velocity.Y).Length();
        output.WriteLine($"{(sprint ? "sprint" : "walk")}: left the ground at {left.Position} at {speed:F2} m/s; landed at {landed.Position} after {air.Count + 1} ticks in the air");
        Assert.Equal(sprint ? 10f : 6.5f, speed, 2);
        // The first tick in the air keeps the velocity it left with (it clears the edge); from the second on there is none (zeroed, not decayed).
        Assert.Equal(speed, new Vector2(air[0].Velocity.X, air[0].Velocity.Y).Length(), 3);
        Assert.All(air.Skip(1), p => Assert.Equal(Vector2.Zero, new Vector2(p.Velocity.X, p.Velocity.Y)));
        // The stick (pushed right and a little forward in the air) does not steer: no sideways motion at all.
        Assert.Equal(left.Position.X, landed.Position.X, 4);
        // Straight down: the landing is within about a capsule of the edge (before protocol 30 a 3 m drop carried 3.6 m walking, 5.5 m sprinting).
        float carried = landed.Position.Y - left.Position.Y;
        Assert.InRange(carried, 0f, 0.9f);
        Assert.Equal(0f, landed.Position.Z, 2);
    }

    [Theory]
    [InlineData(0.6f, false)]   // a curb above the step height: no dead stop, the body keeps its speed
    [InlineData(0.95f, false)]  // just under the 1 m threshold
    [InlineData(1.5f, true)]    // a real drop: straight down
    public void Only_a_drop_more_than_a_metre_deep_stops_a_running_body_a_curb_does_not(float height, bool straight)
    {
        Assert.Equal(1f, Config.StraightDropAbove);
        var world = Ledge(height);
        var s = Fresh(Config, Marine, new Vector3(0, -5f, height));
        PlayerState? left = null;
        float minAir = float.MaxValue;
        for (int k = 0; k < 90; k++)
        {
            bool wasAir = left is not null;
            Step(ref s, Config, InputButtons.Sprint, 0f, 1f, world: world);
            if (left is null && !s.Grounded) left = s;
            else if (left is not null && !s.Grounded) minAir = MathF.Min(minAir, new Vector2(s.Velocity.X, s.Velocity.Y).Length());
            if (wasAir && s.Grounded) break;
        }
        Assert.NotNull(left);
        float carried = s.Position.Y - left!.Value.Position.Y;
        output.WriteLine($"{height} m: left at {new Vector2(left.Value.Velocity.X, left.Value.Velocity.Y).Length():F2} m/s, slowest in the air {minAir:F2} m/s, carried {carried:F2} m");
        if (straight) { Assert.Equal(0f, minAir); Assert.InRange(carried, 0f, 0.9f); }
        else { Assert.Equal(10f, minAir, 2); Assert.True(carried > 1.5f, $"carried {carried}"); }
    }

    [Fact]
    public void A_roll_over_a_curb_keeps_rolling_and_one_off_a_real_drop_stops_travelling_but_plays_on()
    {
        var c = Config with { Classes = [Config.Classes[0], Config.Classes[1] with { Evade = 1 }], Evades = [Dash, EvadeTests.Roll] };
        (float Carried, int RollAirTicks) Roll(float height)
        {
            var world = Ledge(height);
            var s = Fresh(c, Marine, new Vector3(0, -1f, height));
            Step(ref s, c, InputButtons.Jump, 0f, 1f, world: world);
            PlayerState? left = null;
            int rolling = 0;
            for (int k = 0; k < 90 && !(left is not null && s.Grounded); k++)
            {
                Step(ref s, c, InputButtons.None, world: world);
                if (left is null && !s.Grounded) left = s;
                else if (left is not null && !s.Grounded && Evade.Evading(s)) rolling++;
            }
            return (s.Position.Y - left!.Value.Position.Y, rolling);
        }
        var curb = Roll(0.6f);
        var drop = Roll(6f);
        output.WriteLine($"curb 0.6 m: carried {curb.Carried:F2} m; drop 6 m: carried {drop.Carried:F2} m, {drop.RollAirTicks} ticks rolling in the air");
        Assert.True(curb.Carried > 1f, $"curb: carried {curb.Carried}");
        Assert.InRange(drop.Carried, 0f, 1.0f);   // stops travelling after its first tick in the air
        Assert.True(drop.RollAirTicks > 3);         // the clip plays on in the air
    }

    [Fact]
    public void A_roll_carried_off_a_ledge_is_not_ended_by_input_in_the_air_and_then_drops_straight()
    {
        // The Tactical roll (EvadeTests.Roll, 1 s): rolled forward off a plateau 12 m high from just before the edge, the stick held all along.
        var c = Config with { Classes = [Config.Classes[0], Config.Classes[1] with { Evade = 1 }], Evades = [Dash, EvadeTests.Roll] };
        var world = Ledge(12f);
        var s = Fresh(c, Marine, new Vector3(0, -1f, 12f));
        Step(ref s, c, InputButtons.Jump, 0f, 1f, world: world);
        Assert.True(Evade.Evading(s));
        var move = EvadeTests.Roll.Forward;
        bool endedInAir = false;
        int airTicks = 0, rollAirTicks = 0;
        for (int k = 0; k < 90 && !(s.Grounded && airTicks > 0); k++)
        {
            var before = s;
            Step(ref s, c, InputButtons.None, 0f, 1f, world: world);
            if (!s.Grounded) { airTicks++; if (Evade.Evading(s)) rollAirTicks++; }
            // Ended in the air before its clip's end (the stick past its run branch would do that on the ground).
            if (Evade.Evading(before) && !Evade.Evading(s) && !s.Grounded && before.EvadeTicks * Dt + 1e-4f < move.Duration) endedInAir = true;
        }
        output.WriteLine($"roll off a 12 m ledge: {airTicks} ticks in the air, {rollAirTicks} of them rolling; landed at {s.Position}");
        Assert.False(endedInAir);           // the stick (past the roll's run branch) did not end it in the air
        Assert.True(rollAirTicks > 3);      // it rolled on in the air …
        Assert.True(airTicks > rollAirTicks);   // … then dropped
    }

    // ---------------------------------------------------------------- 2. the airborne charge

    [Fact]
    public void A_charge_off_a_ledge_keeps_its_speed_until_it_lands_then_ends_into_normal_movement()
    {
        var world = Ledge(8f);
        var s = Fresh(Config, Marine, new Vector3(0, -9f, 8f));
        for (int k = 0; k < 12; k++) Step(ref s, Config, InputButtons.Sprint, 0f, 1f, world: world);
        Step(ref s, Config, InputButtons.Sprint | InputButtons.MeleeHeavy, 0f, 1f, world: world);
        Assert.Equal(RushPhase.Rush, s.Rush);
        PlayerState? left = null;
        int airTicks = 0, frozenAt = -1;
        var speeds = new List<float>();
        for (int k = 0; k < 120 && !(left is not null && s.Grounded); k++)
        {
            // In the air: the stick, Fire and Aim (inputs that end the rush from its move branch) and Jump: none ends it.
            bool inAir = left is not null;
            Step(ref s, Config, inAir ? InputButtons.Fire | InputButtons.Aim | InputButtons.Jump : InputButtons.None, inAir ? 1f : 0f, 0f, world: world);
            if (left is null && !s.Grounded) { left = s; frozenAt = s.RushTicks; }
            else if (left is not null && !s.Grounded)
            {
                airTicks++;
                Assert.Equal(RushPhase.Rush, s.Rush);
                Assert.Equal(frozenAt, s.RushTicks);   // the clip waits
                speeds.Add(new Vector2(s.Velocity.X, s.Velocity.Y).Length());
            }
        }
        Assert.NotNull(left);
        float leftSpeed = new Vector2(left!.Value.Velocity.X, left.Value.Velocity.Y).Length();
        output.WriteLine($"charge left the ledge at {left.Value.Position} at {leftSpeed:F2} m/s after {frozenAt} rush ticks; {airTicks} ticks in the air; landed at {s.Position} ({s.Rush}, {s.RushTicks} ticks)");
        Assert.Equal(10f, leftSpeed, 2);
        Assert.All(speeds, v => Assert.Equal(leftSpeed, v, 3));   // the speed it left with, held
        // Longer in the air than the clip had left (the rule ends it by time only on the ground): 8 m is about 0.9 s.
        Assert.True(airTicks * Dt > RushTests.Spec.Rush.End - frozenAt * Dt, $"{airTicks} ticks in the air");
        Assert.True(s.Position.Y - left.Value.Position.Y > 8f, $"carried {s.Position.Y - left.Value.Position.Y:F2} m");   // before: about 5 m (the clip ended in the air, then the fall kept its speed)
        // feel/melee-charge-2 (protocol 42, the owner's request): landed after an 8 m fall, the charge has ended (no resumed clip, no second shoulder bash);
        // the speed is kept (at most sprint speed) for the slowdown; the spacing lock stays.
        Assert.Equal((RushPhase.None, 0), (s.Rush, s.RushTicks));
        Assert.True(s.Grounded);
        float landed = new Vector2(s.Velocity.X, s.Velocity.Y).Length();
        Assert.InRange(landed, 0.1f, Config.SprintSpeed + 1e-3f);
        output.WriteLine($"landed at {landed:F2} m/s; the charge ended on the landing tick");
        // The next tick is normal movement: with W held it runs (the walk's acceleration toward run speed), never a charge tick.
        var run = s;
        Step(ref run, Config, InputButtons.None, 0f, 1f, world: world);
        Assert.Equal(RushPhase.None, run.Rush);
        Assert.True(run.Position.Y > s.Position.Y);
        // With no key the body slows down and stands (the ground rule, or the sprint stop where the config has one).
        var stand = s;
        for (int k = 0; k < 30; k++) Step(ref stand, Config, InputButtons.None, world: world);
        Assert.Equal(RushPhase.None, stand.Rush);
        Assert.True(new Vector2(stand.Velocity.X, stand.Velocity.Y).Length() < 0.05f, "still sliding after a second");
    }

    [Theory]
    [InlineData(0.3f, false)]   // a step: the clip resumes (or never left the ground)
    [InlineData(1.5f, true)]    // a real drop: the landing ends the charge
    [InlineData(4f, true)]
    public void Only_a_landing_after_a_real_fall_ends_a_ledge_charge(float height, bool ends)
    {
        Assert.Equal(RushSpec.DefaultLandEndDrop, RushTests.Spec.LandEndDrop);
        var world = Ledge(height);
        var s = Fresh(Config, Marine, new Vector3(0, -2.5f, height));
        Step(ref s, Config, InputButtons.MeleeHeavy, 0f, 1f, world: world);
        Assert.Equal(RushPhase.Rush, s.Rush);
        bool left = false;
        PlayerState? landedState = null;
        for (int k = 0; k < 60 && landedState is null; k++)
        {
            var before = s;
            Step(ref s, Config, InputButtons.None, world: world);
            if (!s.Grounded) left = true;
            else if (left) landedState = s;
            if (s.Rush == RushPhase.None && !left) break;   // ended on the plateau (no drop reached)
        }
        output.WriteLine($"{height} m: left the ground {left}; landing state {landedState?.Rush} ({landedState?.RushTicks} ticks)");
        if (ends) Assert.Equal(RushPhase.None, landedState!.Value.Rush);
        else if (landedState is { } l) Assert.Equal(RushPhase.Rush, l.Rush);   // a curb-sized drop: M29's resume
    }

    [Fact]
    public void The_landing_rule_is_the_fall_speed_of_half_a_metre()
    {
        // √(2 g h) with h = 0.5 m and the config's gravity (20): 4.47 m/s at the landing tick's start.
        PlayerState Falling(float vz, RushPhase phase) => new(1, default, new Vector3(3f, 0f, -vz), 0f, false) { Rush = phase, RushTicks = 4 };
        float edge = MathF.Sqrt(2f * Config.Gravity * RushSpec.DefaultLandEndDrop);
        Assert.True(Rush.EndsOnLanding(Falling(edge + 0.01f, RushPhase.Rush), RushTests.Spec, Config));
        Assert.False(Rush.EndsOnLanding(Falling(edge - 0.05f, RushPhase.Rush), RushTests.Spec, Config));
        Assert.True(Rush.EndsOnLanding(Falling(0f, RushPhase.DashAttack), DashCharge, Config));   // the dash attack held at its end: any landing
        Assert.False(Rush.EndsOnLanding(Falling(9f, RushPhase.Collision), RushTests.Spec, Config));
        Assert.True(Rush.EndsOnLanding(Falling(edge - 0.05f, RushPhase.Rush), RushTests.Spec with { LandEndDrop = 0.2f }, Config));
        // Landed keeps the speed up to sprint speed and clears the charge (not its spacing).
        var landed = Rush.Landed(new PlayerState(1, default, new Vector3(30f, 0f, 0f), 0f, true) { Rush = RushPhase.Rush, RushTicks = 5, RushLock = 20 }, Config);
        Assert.Equal((RushPhase.None, 0, 20), (landed.Rush, landed.RushTicks, landed.RushLock));
        Assert.Equal(Config.SprintSpeed, landed.Velocity.X, 4);
        // The hash: the default writes nothing new; another value does.
        var c2 = Config with { Classes = [Config.Classes[0], Config.Classes[1] with { MeleeRush = RushTests.Spec with { LandEndDrop = 1f } }] };
        Assert.NotEqual(Config.Hash(), c2.Hash());
    }

    [Fact]
    public void A_held_charge_landing_into_a_wall_on_the_same_tick_ends_and_takes_no_wall_stop()
    {
        // A plateau 3 m up (y < 0), the charge goes off its edge at 10 m/s; a wall across the lower floor at y = W. For each W the landing tick is checked:
        // when the wall blocks that tick's move, the landing still ends the charge (protocol 42): no collision stop, normal movement from the next tick.
        int blockedLandings = 0;
        for (float wall = 3.0f; wall <= 9.0f; wall += 0.1f)
        {
            var world = new Level().Box(new(-100, -100, -1), new(100, 200, 0)).Box(new(-100, -100, 0), new(100, 0, 3f)).Box(new(-100, wall, 0), new(100, wall + 1f, 6f)).World();
            var s = Fresh(Config, Marine, new Vector3(0, -2f, 3f));
            Step(ref s, Config, InputButtons.MeleeHeavy, 0f, 1f, world: world);
            Assert.Equal(RushPhase.Rush, s.Rush);
            bool left = false;
            for (int k = 0; k < 60; k++)
            {
                var before = s;
                Step(ref s, Config, InputButtons.None, world: world);
                if (!s.Grounded) { left = true; if (s.Rush != RushPhase.Rush) break; continue; }   // a wall stop in the air: not this case
                if (!left) { if (s.Rush != RushPhase.Rush) break; continue; }
                // The landing tick.
                float moved = new Vector2(s.Position.X - before.Position.X, s.Position.Y - before.Position.Y).Length();
                float wished = new Vector2(before.Velocity.X, before.Velocity.Y).Length() * Dt;
                bool blocked = before.Rush == RushPhase.Rush && moved < 0.5f * wished;
                if (blocked)
                {
                    blockedLandings++;
                    output.WriteLine($"wall at y {wall:F1}: the landing tick moved {moved:F2} of {wished:F2} m; now {s.Rush} ({s.RushTicks} ticks)");
                    Assert.Equal((RushPhase.None, 0), (s.Rush, s.RushTicks));   // ended by the landing, not the wall's collision stop
                    var next = s;
                    Step(ref next, Config, InputButtons.None, 0f, 1f, world: world);
                    Assert.Equal(RushPhase.None, next.Rush);
                }
                break;
            }
        }
        Assert.True(blockedLandings > 0, "no wall position blocked the landing tick");
    }

    [Fact]
    public void A_dash_attack_held_at_its_end_landing_on_a_player_connects_and_takes_its_hit_state()
    {
        // The Assault dashes forward off a 0 m plateau that ends just past the dash attack's press point over a floor 8 m lower; the lunge reaches its end in the
        // air (held), lands, and a target stands where it comes down. The landing tick's contact (protocol 42: the landing ends the dash attack) still counts:
        // the hit, the stun and the dash attack's hit state.
        (float PressY, bool LandingContact, RushPhase After, ReactionKind Target, bool Held) Run(float edge, float targetY)
        {
            var world = new Level().Box(new(-100, -100, -9), new(100, 200, -8)).Box(new(-100, -100, -1), new(100, edge, 0)).World();
            var w = new World(Config, world, [new SpawnPoint(new Vector3(0, -6f, 0f), 0f), new SpawnPoint(new Vector3(0, targetY, -8f), MathF.PI)]);
            w.Spawn(1, Assault); w.Spawn(2, Marine);
            float pressY = float.NaN; bool pressed = false, held = false;
            for (int k = 0; k < 150; k++)
            {
                int tick = k;
                uint view = w.Tick;
                var before = w.Players[1];
                bool press = !pressed && before.EvadeTicks >= 10 && Rush.CanDashAttack(before, Config, world);
                if (press) { pressed = true; pressY = before.Position.Y; }
                w.Step(id => id == 1
                    ? new PlayerInput(0, 0f, tick == 0 ? 1f : 0f, 0f, tick == 0 ? InputButtons.Jump : press ? InputButtons.Melee : InputButtons.None) with { ViewTick = view }
                    : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None) with { ViewTick = view });
                var after = w.Players[1];
                if (after.Rush == RushPhase.DashAttack && after.RushTicks == 25 && !after.Grounded) held = true;
                if (w.LastRushes.Count > 0) return (pressY, !before.Grounded && after.Grounded, after.Rush, w.Players[2].Reaction, held);
                if (pressed && after.Rush == RushPhase.None && after.Grounded) return (pressY, false, after.Rush, w.Players[2].Reaction, held);
            }
            return (pressY, false, RushPhase.None, ReactionKind.None, held);
        }
        var dry = Run(1000f, 500f);   // the press point on an endless plateau (no target in reach)
        Assert.False(float.IsNaN(dry.PressY));
        float edge = dry.PressY + 1f;
        bool found = false;
        for (float y = edge + 2f; y <= edge + 16f && !found; y += 0.25f)
        {
            var r = Run(edge, y);
            if (!r.LandingContact) continue;
            found = true;
            output.WriteLine($"target at y {y:F2} (edge {edge:F2}): contact on the landing tick (held at its end: {r.Held}); charger {r.After}, target {r.Target}");
            Assert.True(r.Held);
            Assert.Equal(RushPhase.DashHit, r.After);
            Assert.Equal(ReactionKind.Stun, r.Target);
        }
        Assert.True(found, "no target position gave a contact on the landing tick");
    }

    [Fact]
    public void A_ledge_charge_landing_on_a_player_still_connects_and_takes_the_stop()
    {
        // A plateau 3 m up; the target stands where the charge comes down. The landing tick's move reaches it: the hit, the stun and the collision stop,
        // as any contact (feel/melee-charge-2: the landing ended the charge, its last move still counts).
        var world = Ledge(3f);
        bool hitOnLanding = false;
        for (float y = 3.0f; y <= 9.0f && !hitOnLanding; y += 0.25f)
        {
            var w = new World(Config, world, [new SpawnPoint(new Vector3(0, -2f, 3f), 0f), new SpawnPoint(new Vector3(0, y, 0f), MathF.PI)]);
            w.Spawn(1, Marine); w.Spawn(2, Marine);
            for (int k = 0; k < 60; k++)
            {
                int tick = k;
                uint view = w.Tick;
                var before = w.Players[1];
                w.Step(id => id == 1
                    ? new PlayerInput(0, 0f, tick == 0 ? 1f : 0f, 0f, tick == 0 ? InputButtons.MeleeHeavy : InputButtons.None) with { ViewTick = view }
                    : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None) with { ViewTick = view });
                var after = w.Players[1];
                if (w.LastRushes.Count > 0)
                {
                    if (!before.Grounded && after.Grounded)
                    {
                        hitOnLanding = true;
                        output.WriteLine($"target at y {y}: contact on the landing tick; charger now {after.Rush}, target {w.Players[2].Reaction}");
                        Assert.Equal(RushPhase.Collision, after.Rush);
                        Assert.Equal(ReactionKind.Stun, w.Players[2].Reaction);
                    }
                    break;
                }
                if (after.Grounded && !before.Grounded) break;   // landed without a contact
            }
        }
        Assert.True(hitOnLanding, "no target position gave a contact on the landing tick");
    }

    [Fact]
    public void A_charge_leaving_the_ground_in_its_clips_burst_keeps_that_speed_uncapped()
    {
        // A clip shaped like the install's bull rush: 30 m/s for its first 0.2 s, then 5 m/s to its end at 1.0 s (mean 10 m/s). The owner's decision
        // (2026-10-29: "no a ledge charge should not be capped"): the held speed is the burst's.
        var burst = new RushMove("burst", [.. Enumerable.Range(0, 11).Select(k => new Vector2(0f, k <= 2 ? 3f * k : 6f + 0.5f * (k - 2)))], 1f, 1f, 0.6f, 0.5f, 1f);
        var c = Config with { Classes = [Config.Classes[0], Config.Classes[1] with { MeleeRush = RushTests.Spec with { Rush = burst } }] };
        var world = Ledge(5f);
        var s = Fresh(c, Marine, new Vector3(0, -3.5f, 5f));
        for (int k = 0; k < 8 && s.Rush == RushPhase.None; k++) Step(ref s, c, InputButtons.Sprint | (k == 7 ? InputButtons.MeleeHeavy : InputButtons.None), 0f, 1f, world: world);
        Assert.Equal(RushPhase.Rush, s.Rush);
        float left = -1f;
        var held = new List<float>();
        for (int k = 0; k < 60 && !(left >= 0f && s.Grounded); k++)
        {
            bool wasGrounded = s.Grounded;
            Step(ref s, c, InputButtons.None, world: world);
            if (wasGrounded && !s.Grounded) left = new Vector2(s.Velocity.X, s.Velocity.Y).Length();
            else if (!wasGrounded && !s.Grounded) held.Add(new Vector2(s.Velocity.X, s.Velocity.Y).Length());
        }
        output.WriteLine($"left the ground at {left:F2} m/s; held {string.Join(", ", held.Take(3).Select(v => v.ToString("F2")))} …");
        Assert.True(left > 20f, $"left at {left}");   // in the burst
        Assert.NotEmpty(held);
        Assert.All(held, v => Assert.Equal(left, v, 3));   // held exactly, not capped
    }

    [Fact]
    public void A_charge_in_the_air_still_connects_with_a_player_below_the_ledge()
    {
        var world = Ledge(1.2f);
        var w = new World(Config, world, [new SpawnPoint(new Vector3(0, -8f, 1.2f), 0f), new SpawnPoint(new Vector3(0, 3.5f, 0f), MathF.PI)]);
        w.Spawn(1, Marine); w.Spawn(2, Marine);
        bool airContact = false;
        for (int k = 0; k < 60 && !airContact; k++)
        {
            int tick = k;
            uint view = w.Tick;
            var before = w.Players[1];
            w.Step(id => id == 1
                ? new PlayerInput(0, 0f, tick <= 12 ? 1f : 0f, 0f, (tick <= 12 ? InputButtons.Sprint : InputButtons.None) | (tick == 12 ? InputButtons.MeleeHeavy : InputButtons.None)) with { ViewTick = view }
                : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None) with { ViewTick = view });
            if (w.LastRushes.Count > 0) { airContact = !before.Grounded; output.WriteLine($"contact at tick {tick}: the charger {(before.Grounded ? "on the ground" : "in the air")} at {before.Position}"); }
        }
        Assert.True(airContact);
        Assert.Equal(ReactionKind.Stun, w.Players[2].Reaction);
    }

    // ---------------------------------------------------------------- 3. the dash attack

    /// <summary>Taps Jump (stick forward) and steps the dash's flight until <paramref name="evadeTicks"/> ticks have played.</summary>
    static PlayerState DashTo(SimConfig c, int evadeTicks, int perk = -1, Vector3 at = default)
    {
        var s = Fresh(c, Assault, at, perk: perk);
        Step(ref s, c, InputButtons.Jump, 0f, 1f);
        Step(ref s, c, InputButtons.None, 0f, 1f);   // released before the decision: the dash
        while (s.EvadeTicks < evadeTicks) Step(ref s, c, InputButtons.None);
        return s;
    }

    [Fact]
    public void A_melee_press_from_the_dash_attack_branch_and_within_3_m_of_the_ground_starts_the_lunge()
    {
        // Before BRANCH_Dash_Attack (0.333 s = 10 ticks played): the press does nothing (an evade ignores melee).
        var early = DashTo(Config, 9);
        Assert.False(Rush.CanDashAttack(early, Config, null));
        Step(ref early, Config, InputButtons.Melee);
        Assert.Equal((RushPhase.None, true, 0), (early.Rush, Evade.Evading(early), early.MeleeStep));
        // From it: the lunge starts, the dash ends, the body faces the view.
        var s = DashTo(Config, 10);
        Assert.True(Rush.CanDashAttack(s, Config, null));
        float height = s.Position.Z;
        Step(ref s, Config, InputButtons.Melee, yaw: 0.2f);
        output.WriteLine($"dash attack pressed {height:F2} m above the ground");
        Assert.Equal((RushPhase.DashAttack, 1, false, 0.2f), (s.Rush, s.RushTicks, Evade.Evading(s), s.Yaw));
        Assert.Equal(30, s.RushLock);   // the ability's 1 s spacing
        // The height gate: more than 3 m above the ground (here the flat ground at z = 0), no dash attack.
        var high = DashTo(Config, 10);
        Assert.False(Rush.CanDashAttack(high with { Position = high.Position with { Z = 3.2f } }, Config, null));
        Assert.True(Rush.CanDashAttack(high with { Position = high.Position with { Z = 2.9f } }, Config, null));
        // Not in the dash's landing part, not for a class without one, not twice within the spacing.
        Assert.False(Rush.CanDashAttack(s with { Rush = RushPhase.None, EvadeTicks = 12, EvadeLanded = true }, Config, null));
    }

    [Fact]
    public void The_lunge_runs_out_into_its_stop_and_is_held_in_the_air_until_it_lands()
    {
        // From 1.6 m up in the dash: the lunge plays in the air (gravity pulls), lands, reaches BRANCH_toIdle on the ground, then the run-out.
        var s = DashTo(Config, 10);
        Step(ref s, Config, InputButtons.Melee);
        var phases = new List<(RushPhase, int, bool)>();
        for (int k = 0; k < 80 && s.Rush != RushPhase.None; k++) { Step(ref s, Config, InputButtons.None); phases.Add((s.Rush, s.RushTicks, s.Grounded)); }
        int lunge = phases.Count(p => p.Item1 == RushPhase.DashAttack), stop = phases.Count(p => p.Item1 == RushPhase.DashStop);
        output.WriteLine($"lunge {lunge + 1} ticks, run-out {stop} ticks; landed after {phases.FindIndex(p => p.Item3)} ticks");
        Assert.Equal(24, lunge);   // 25 ticks to BRANCH_toIdle, the press tick included
        Assert.Equal(27, stop);    // the run-out to its end (0.9 s)
        Assert.Equal(RushPhase.None, s.Rush);
        // Started higher than the lunge lasts (a ledge): the clip's end waits in the air, the speed held, until the landing; then the run-out.
        var airborne = DashTo(Config, 10);
        // A floor 2.9 m below the press (within the gate), dropping away 2 m ahead of the press point (the lunge goes on over the void).
        var shelf = new Level().Box(new(-100, -100, -40), new(100, 200, -39)).Box(new(-5, -100, -2f), new(5, airborne.Position.Y + 2f, airborne.Position.Z - 2.9f)).World();
        Assert.True(Rush.CanDashAttack(airborne, Config, shelf));
        Step(ref airborne, Config, InputButtons.Melee, world: shelf);
        Assert.Equal(RushPhase.DashAttack, airborne.Rush);
        int heldTicks = 0; float heldSpeed = -1f;
        for (int k = 0; k < 200 && !airborne.Grounded; k++)
        {
            Step(ref airborne, Config, InputButtons.Fire, 1f, 0f, world: shelf);
            if (!airborne.Grounded && airborne.Rush == RushPhase.DashAttack && airborne.RushTicks == 25)
            {
                heldTicks++;
                float v = new Vector2(airborne.Velocity.X, airborne.Velocity.Y).Length();
                if (heldSpeed < 0f) heldSpeed = v; else Assert.Equal(heldSpeed, v, 3);
            }
        }
        output.WriteLine($"over the void: the lunge's end held {heldTicks} ticks in the air at {heldSpeed:F2} m/s");
        Assert.True(heldTicks > 10);
        // feel/melee-charge-2 (protocol 42): the landing of a lunge held at its end ends the dash attack on the landing tick (no run-out); normal movement follows.
        Assert.True(airborne.Grounded);
        Assert.Equal((RushPhase.None, 0), (airborne.Rush, airborne.RushTicks));
        Step(ref airborne, Config, InputButtons.None, world: shelf);
        Assert.Equal(RushPhase.None, airborne.Rush);   // no DashStop
    }

    [Fact]
    public void A_wall_ahead_stops_the_lunge_into_its_knocked_back_state()
    {
        var world = new Level().Box(new(-100, -100, -1), new(100, 200, 0)).Box(new(-20, 9.5f, 0), new(20, 10.5f, 5)).World();
        var s = Fresh(Config, Assault);
        Step(ref s, Config, InputButtons.Jump, 0f, 1f, world: world);
        Step(ref s, Config, InputButtons.None, 0f, 1f, world: world);
        while (s.EvadeTicks < 10) Step(ref s, Config, InputButtons.None, world: world);
        Step(ref s, Config, InputButtons.Melee, world: world);
        Assert.Equal(RushPhase.DashAttack, s.Rush);
        for (int k = 0; k < 40 && s.Rush == RushPhase.DashAttack; k++) Step(ref s, Config, InputButtons.None, world: world);
        output.WriteLine($"stopped at {s.Position} in {s.Rush}");
        Assert.Equal(RushPhase.DashCollision, s.Rush);
        Assert.True(s.Position.Y < 9.5f - Config.CapsuleRadius + 0.05f);
    }

    [Theory]
    [InlineData(false, 100f)]   // 75 + 25 shield, the owner's stun
    [InlineData(true, 150f)]    // Furious Charge: the data's 100 + 50, stun
    public void The_dash_attack_connects_with_the_primary_hit_and_a_stun(bool furious, float total)
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 10f, 0), MathF.PI)]);
        w.Spawn(1, Assault, perk1Choice: furious ? 0 : -1);
        w.Spawn(2, Marine);
        Assert.Equal(furious ? Furious : null, Loadouts.Upgrades(Config, w.Players[1]).First);
        HitEvent? hit = null;
        int pressAt = -1;
        for (int k = 0; k < 60 && hit is null; k++)
        {
            int tick = k;
            uint view = w.Tick;
            var me = w.Players[1];
            bool press = pressAt < 0 && Rush.CanDashAttack(me, Config, null);
            if (press) pressAt = tick;
            w.Step(id => id == 1
                ? new PlayerInput(0, 0f, tick <= 1 ? 1f : 0f, 0f, (tick == 0 ? InputButtons.Jump : InputButtons.None) | (press ? InputButtons.Melee : InputButtons.None)) with { ViewTick = view }
                : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None) with { ViewTick = view });
            foreach (var h in w.LastHits) if (h.Shooter == 1 && h.Target == 2) hit = h;
        }
        Assert.NotNull(hit);
        output.WriteLine($"pressed at tick {pressAt}; hit {hit}");
        Assert.Equal(total, hit!.Value.ShieldDamage + hit.Value.HealthDamage, 3);
        Assert.True(hit.Value.Melee);
        Assert.Equal(ReactionKind.Stun, w.Players[2].Reaction);
        Assert.Equal(RushPhase.DashHit, w.Players[1].Rush);   // BOOL_Dash_Hit: the hit state
        Assert.Single(w.LastRushes);
    }

    // ---------------------------------------------------------------- 4. the sword slash

    [Fact]
    public void A_melee_press_in_a_jump_below_8_m_slashes_and_from_8_m_up_pounds()
    {
        var pack = JumpConfig.JumpPacks[0];
        Assert.True(JumpPack.Slashes(pack, 7.99f));
        Assert.False(JumpPack.Slashes(pack, 8f));
        Assert.False(JumpPack.Slashes(JumpPackTests.Pack, 1f));   // a pack without the slash always pounds
        // A tap (a 4 m jump): the press at the apex is the slash; the landing is a SlashLand (the regular landing's blast), not an Impact.
        var s = Fresh(JumpConfig, Assault);
        Step(ref s, JumpConfig, InputButtons.Jump);
        while (s.Jump != JumpPhase.Hang) Step(ref s, JumpConfig, InputButtons.None);
        Step(ref s, JumpConfig, InputButtons.Melee, pitch: -1.4f);
        Assert.Equal((JumpPhase.Dive, true), (s.Jump, s.JumpSlash));
        var kinds = new List<JumpEventKind>();
        for (int k = 0; k < 60 && s.Jump != JumpPhase.None; k++) kinds.Add(Step(ref s, JumpConfig, InputButtons.None).Jump);
        Assert.Contains(JumpEventKind.SlashLand, kinds);
        Assert.DoesNotContain(JumpEventKind.Impact, kinds);
        Assert.False(s.JumpSlash);
        // A full hold (18 m): the press at the apex, far above 8 m, is the pound.
        var p = Fresh(JumpConfig, Assault);
        for (int k = 0; k < 13; k++) Step(ref p, JumpConfig, InputButtons.Jump);
        while (p.Jump != JumpPhase.Hang) Step(ref p, JumpConfig, InputButtons.None);
        Assert.True(p.Position.Z >= 8f, $"apex {p.Position.Z}");
        Step(ref p, JumpConfig, InputButtons.Melee, pitch: -1.4f);
        Assert.Equal((JumpPhase.Dive, false), (p.Jump, p.JumpSlash));
        var poundKinds = new List<JumpEventKind>();
        for (int k = 0; k < 60 && p.Jump != JumpPhase.None; k++) poundKinds.Add(Step(ref p, JumpConfig, InputButtons.None).Jump);
        Assert.Contains(JumpEventKind.Impact, poundKinds);
    }

    [Fact]
    public void The_slash_hits_everyone_in_the_cone_with_the_interrupt_no_stun_no_knockback_and_no_pound_blast()
    {
        var w = new World(JumpConfig, null,
        [
            new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(-1.2f, 2.6f, 0), MathF.PI), new SpawnPoint(new Vector3(1.2f, 2.6f, 0), MathF.PI),
            new SpawnPoint(new Vector3(0, -2.5f, 0), 0f),
        ]);
        w.Spawn(1, Assault);
        for (int id = 2; id <= 4; id++) w.Spawn(id, 1);
        int landedAt = -1, hitAt = -1;
        var melee = new List<HitEvent>();
        var splash = new List<HitEvent>();
        for (int k = 0; k < 120 && hitAt < 0; k++)
        {
            int tick = k;
            var me = w.Players[1];
            var buttons = tick == 0 ? InputButtons.Jump : me.Jump == JumpPhase.Hang && !me.MeleeHeld ? InputButtons.Melee : InputButtons.None;
            w.Step(id => id == 1 ? new PlayerInput(0, 0f, 0f, 0f, buttons, -1.4f) : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None));
            if (w.LastJumps.Any(j => j.Kind == JumpEventKind.SlashLand)) landedAt = tick;
            melee.AddRange(w.LastHits.Where(h => h.Melee));
            splash.AddRange(w.LastHits.Where(h => h.Splash));
            if (w.LastHits.Any(h => h.Melee)) hitAt = tick;
        }
        output.WriteLine($"landed at tick {landedAt}, cone hit at {hitAt}: {string.Join("; ", melee)}; blasts {splash.Count}");
        Assert.Equal(4, hitAt - landedAt);   // the impact clip's hit event (0.133 s)
        Assert.Equal([2, 3], melee.Select(h => h.Target).Order());   // the two in front, not the one behind
        Assert.All(melee, h => Assert.Equal(60f, h.ShieldDamage + h.HealthDamage, 3));   // every target the one profile
        Assert.All([2, 3], id => Assert.Equal((ReactionKind.Interrupt, 0), (w.Players[id].Reaction, w.Players[id].PushDistance)));
        Assert.Equal(ReactionKind.None, w.Players[4].Reaction);
        // No pound blast (25 + 50 shield, 7 m): only the regular landing's (5, 4 m) as any landing (ours).
        Assert.All(splash, h => Assert.True(h.Landing && h.ShieldDamage + h.HealthDamage <= 5f + 1e-3f, $"{h}"));
        Assert.Equal(JumpPhase.Recover, w.Players[1].Jump);
    }

    [Fact]
    public void The_slash_recovers_at_its_run_branch_and_the_pound_at_its_get_up()
    {
        var s = Fresh(JumpConfig, Assault);
        Step(ref s, JumpConfig, InputButtons.Jump);
        while (s.Jump != JumpPhase.Hang) Step(ref s, JumpConfig, InputButtons.None);
        Step(ref s, JumpConfig, InputButtons.Melee, pitch: -1.4f);
        while (s.Jump == JumpPhase.Dive) Step(ref s, JumpConfig, InputButtons.None);
        int recover = 0;
        bool hit = false;
        while (s.Jump == JumpPhase.Recover) { recover++; hit |= Step(ref s, JumpConfig, InputButtons.None).SlashHit; }
        Assert.True(hit);
        Assert.Equal(19, recover);   // to 19 ticks after the impact (the impact tick counted 0)
    }

    // ---------------------------------------------------------------- 6. interruptions (review of 2026-10-29)

    /// <summary>A dash attack under way: the lunge (<paramref name="ticks"/> after the press) or, past its end, the run-out.</summary>
    static PlayerState Lunging(int ticks)
    {
        var s = DashTo(Config, 10);
        Step(ref s, Config, InputButtons.Melee);
        for (int k = 0; k < ticks; k++) Step(ref s, Config, InputButtons.None);
        return s;
    }

    [Theory]
    [InlineData(3)]    // the lunge, in the air
    [InlineData(30)]   // the run-out, on the ground
    public void A_stun_or_a_death_during_the_lunge_or_the_run_out_ends_the_dash_attack(int ticks)
    {
        var s = Lunging(ticks);
        Assert.Equal(ticks < 24 ? RushPhase.DashAttack : RushPhase.DashStop, s.Rush);
        Assert.True(Reaction.CanReact(s, Config));   // no hyper armour in the dash attack
        var stunned = Reaction.Start(s, Config, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.Zero);
        Assert.Equal((RushPhase.None, 0, s.RushLock), (stunned.Rush, stunned.RushTicks, stunned.RushLock));   // the spacing stays
        Step(ref stunned, Config, InputButtons.Melee);
        Assert.Equal((RushPhase.None, ReactionKind.Stun), (stunned.Rush, stunned.Reaction));
        var dead = Combat.Killed(s, Config);
        Assert.Equal((RushPhase.None, 0, 0), (dead.Rush, dead.RushTicks, dead.RushLock));
        Assert.Equal(RushPhase.None, Combat.Respawned(dead, Config).Rush);
    }

    [Fact]
    public void A_stun_in_the_slashs_recovery_before_its_hit_cancels_the_hit()
    {
        var s = Fresh(JumpConfig, Assault);
        Step(ref s, JumpConfig, InputButtons.Jump);
        while (s.Jump != JumpPhase.Hang) Step(ref s, JumpConfig, InputButtons.None);
        Step(ref s, JumpConfig, InputButtons.Melee, pitch: -1.4f);
        while (s.Jump == JumpPhase.Dive) Step(ref s, JumpConfig, InputButtons.None);
        Assert.Equal((JumpPhase.Recover, true), (s.Jump, s.JumpSlash));
        Step(ref s, JumpConfig, InputButtons.None);   // 1 tick after the impact; the hit is due at 4
        Assert.True(Reaction.CanReact(s, JumpConfig));
        s = Reaction.Start(s, JumpConfig, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.Zero);
        Assert.Equal((JumpPhase.None, false), (s.Jump, s.JumpSlash));
        bool hit = false;
        for (int k = 0; k < 30; k++) hit |= Step(ref s, JumpConfig, InputButtons.None).SlashHit;
        Assert.False(hit);   // the slash's cone never resolves (ours: a reaction ends the recovery and its pending hit)
    }

    [Fact]
    public void The_hit_state_ends_on_the_stick_at_its_run_branch_and_on_Jump_at_its_evade_branch_with_a_take_off()
    {
        var hit = Fresh(Config, Assault) with { Rush = RushPhase.DashHit, RushTicks = 1 };
        var stick = hit;
        int ticks = 0;
        while (stick.Rush == RushPhase.DashHit && ticks < 40) { Step(ref stick, Config, InputButtons.None, 0f, 1f); ticks++; }
        Assert.Equal(RushPhase.None, stick.Rush);
        Assert.Equal(19, ticks);   // over in the tick that starts with 19 ticks played (BRANCH_Run 0.633 s)
        var jump = hit;
        int jt = 0;
        while (jump.Rush == RushPhase.DashHit && jt < 40) { Step(ref jump, Config, (jt % 2 == 0) ? InputButtons.Jump : InputButtons.None); jt++; }
        Assert.Equal(RushPhase.None, jump.Rush);
        Assert.Equal(JumpPhase.TakeOff, jump.Jump);   // the press that ends it starts the pack's take-off (a tap: a dash)
        Assert.True(jt >= 20, $"ended after {jt} ticks");   // not before BRANCH_Evade 0.667 s
        // Without input it runs to its end (0.7 s).
        var idle = hit;
        int it = 0;
        while (idle.Rush == RushPhase.DashHit && it < 40) { Step(ref idle, Config, InputButtons.None); it++; }
        Assert.Equal(21, it);
    }

    [Fact]
    public void A_round_restart_mid_dash_attack_respawns_the_player_clean()
    {
        var rules = new MatchRules { TimeLimit = 1.5f, RestartDelay = 0.2f, CountdownTime = 0.1f };
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(60, 0, 0), 0f)]) { Rules = rules };
        w.Spawn(1, Assault, team: Team.SpaceMarines);
        w.Spawn(2, Marine, team: Team.Chaos);
        int restartsMidAttack = 0;
        for (int k = 0; k < 2000 && restartsMidAttack == 0; k++)
        {
            var me = w.Players[1];
            int tick = k;
            // A dash 30 ticks into each 54-tick round (countdown 3, play 45, restart 6) and the dash attack in its window: the lunge runs through the restart.
            var buttons = w.Tick % 54 == 30 && me.Rush == RushPhase.None && me.EvadeTicks == 0 && me.Jump == JumpPhase.None && !me.JumpHeld ? InputButtons.Jump
                : Rush.CanDashAttack(me, Config, null) && !me.MeleeHeld ? InputButtons.Melee : InputButtons.None;
            w.Step(id => id == 1 ? new PlayerInput(0, 0f, 1f, (tick / 90) % 4 * MathF.PI / 2f, buttons) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
            if (w.LastRespawns.Contains(1) && Rush.IsDash(me.Rush))
            {
                restartsMidAttack++;
                var after = w.Players[1];
                output.WriteLine($"respawned at tick {w.Tick} during {me.Rush} ({me.RushTicks} ticks): now {after.Rush}, evade {after.EvadeTicks}, jump {after.Jump}");
                Assert.Equal((RushPhase.None, 0, 0, 0, JumpPhase.None), (after.Rush, after.RushTicks, after.RushLock, after.EvadeTicks, after.Jump));
            }
        }
        Assert.Equal(1, restartsMidAttack);
    }

    // ---------------------------------------------------------------- 5. wire, hash and prediction

    [Fact]
    public void Protocol_30_carries_the_dash_attacks_phases_and_the_slash_and_a_full_snapshot_stays_within_1200_bytes()
    {
        Assert.Equal(43, Protocol.Version);
        Assert.Equal(1200, Protocol.MaxSnapshotBytes);
        foreach (var phase in new[] { RushPhase.DashAttack, RushPhase.DashHit, RushPhase.DashStop, RushPhase.DashCollision })
        {
            var p = new PlayerState(4, Vector3.One, Vector3.Zero, 1f, false) { Alive = true, Rush = phase, RushTicks = 25, RushLock = 7 };
            var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 1, 1, [p, p with { PlayerId = 5 }], Self: 4))));
            Assert.Equal(p, back.Players[0]);
            Assert.Equal((phase, 25), (back.Players[1].Rush, back.Players[1].RushTicks));
        }
        // The slash: slot byte bit 7 with the pounding flag, in every record.
        var slash = new PlayerState(4, Vector3.One, Vector3.Zero, 1f, false) { Alive = true, Jump = JumpPhase.Dive, JumpSlash = true, JumpTicks = 40, JumpMark = 30 };
        var read = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 1, 1, [slash, slash with { PlayerId = 5, Jump = JumpPhase.Recover, Grounded = true }], Self: 4))));
        Assert.Equal(slash, read.Players[0]);
        Assert.Equal((JumpPhase.Dive, true), (read.Players[1].Jump, read.Players[1].JumpSlash));   // slim: pounding, slash
        var slim = Protocol.Write(new Snapshot(9, 1, 1, [slash with { Jump = JumpPhase.None, JumpSlash = false }]));
        var orphan = (byte[])slim.Clone(); orphan[^31] |= 128;   // the slash bit without the pounding flag
        Assert.Null(Protocol.Read(orphan));
        // Sixteen players at their largest with the new values: the budget holds.
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new PlayerState(i, new Vector3(1e4f), new Vector3(100f), 3f, false)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
            Cooldown = 1, ReloadLeft = 1, SinceDamage = 1, RespawnLeft = 1, SwitchLeft = 1, Pitch = 1, Rush = RushPhase.DashCollision, RushTicks = 255, RushLock = 255,
            Jump = JumpPhase.Dive, JumpSlash = true, JumpTicks = 65535, JumpMark = 65535, JumpPower = 110f, Brace = BracePhase.Braced, Venting = true, ReadyTicks = 300, RaiseTicks = 9,
        }).ToArray();
        var projectiles = Enumerable.Range(0, Protocol.MaxSnapshotProjectiles).Select(k => new ProjectileView(k, 0, 0, new Vector3(1e3f), ProjectileFlags.None)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles));
        output.WriteLine($"16 players with dash-attack phases and slashes (protocol {Protocol.Version}): {bytes.Length} bytes");
        Assert.True(bytes.Length <= Protocol.SnapshotBudget, $"{bytes.Length} bytes");
        var back2 = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back2.Players);
    }

    [Fact]
    public void The_config_hash_covers_the_dash_attack_and_the_slash_and_leaves_configs_without_them_as_they_were()
    {
        var without = JumpPackDashTests.Config;
        var with = without with { JumpPacks = [without.JumpPacks[0] with { DashAttack = DashAttack }] };
        var seen = new HashSet<ulong> { without.Hash(), with.Hash() };
        Assert.Equal(2, seen.Count);
        Assert.True(seen.Add((without with { JumpPacks = [without.JumpPacks[0] with { Slash = Slash }] }).Hash()));
        Assert.True(seen.Add((with with { JumpPacks = [with.JumpPacks[0] with { DashAttack = DashAttack with { MaxHeight = 2.5f } }] }).Hash()));
        Assert.True(seen.Add((without with { JumpPacks = [without.JumpPacks[0] with { Slash = Slash with { MinPoundHeight = 7f } }] }).Hash()));
        Assert.True(seen.Add((without with { Evades = [Dash] }).Hash()));   // the dash attack's window on the dash's flights
        // A pack and an evade without them hash as before protocol 30 (the new fields are written only when set).
        Assert.Equal(without.Hash(), (without with { JumpPacks = [without.JumpPacks[0] with { DashAttack = null, Slash = null }] }).Hash());
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void Prediction_matches_the_server_through_dash_attacks_slashes_and_charges_off_ledges_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // A staircase of plateaus 3 m apart down +Y, wide along X; both spawn on the top one, 120 m apart. The charger runs down +Y off the ledges; the
        // Assault dashes along X (back and forth) on the top plateau and jumps there. No damage (hits are not predicted); they never meet.
        var level = new Level().Box(new(-200, -100, -1), new(200, 300, 0));
        for (int k = 0; k < 4; k++) level.Box(new(-200, -100, 0), new(200, 14f * (k + 1), 12f - 3f * k));
        var collision = level.World();
        var config = Config with
        {
            Classes = Config.Classes.Select(k => k with { Side = Team.None, MeleeRush = k.MeleeRush is { } r ? r with { Primary = Plain, Secondary = Plain } : null }).ToArray(),
            JumpPacks = [Pack with { Landings = [], Pound = null }],
            Weapons = Config.Weapons.Select(w => w with { Damage = 0f }).ToArray(),
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var map = new GameMap("stairs", collision, [new SpawnPoint(new Vector3(-60, 2, 12), 0f), new SpawnPoint(new Vector3(60, 2, 12), 0f)], 81);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        int[] classes = [Assault, Marine];
        for (int i = 0; i < classes.Length; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)classes[i], LoadMap = _ => new LoadedMap(collision, 81) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = classes.Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            var me = h.Clients[i].Predicted;
            if (classes[i] == Assault)
            {
                // Every 150 ticks: a dash (a tap with the stick forward) and the dash attack in its window, then a short jump and the slash early in
                // the ascent (2 to 3 m up: the synthetic pack's lowest jump peaks at 8 m). Alternately along +X and -X.
                uint cycle = t % 150;
                float yaw = t / 150 % 2 == 0 ? -MathF.PI / 2f : MathF.PI / 2f;
                var b = InputButtons.None;
                if (cycle == 0 || cycle is >= 80 and < 86) b = InputButtons.Jump;   // 80..85: held past the dash's decision, a jump
                if (cycle == 13) b = InputButtons.Melee;
                if (me.Jump == JumpPhase.Ascent && me.JumpTicks >= 9 && !me.MeleeHeld) b |= InputButtons.Melee;
                return new PlayerInput(t, 0f, cycle < 2 ? 1f : 0f, yaw, b, -1.2f);
            }
            // The charger: sprint along +Y and charge, off the next ledge.
            uint phase = t % 120;
            bool sprint = phase < 24;
            return new PlayerInput(t, 0f, sprint ? 1f : 0f, 0f, (sprint ? InputButtons.Sprint : InputButtons.None) | (phase == 23 ? InputButtons.MeleeHeavy : InputButtons.None));
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < classes.Length; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        int dashAttacks = 0, slashes = 0, heldTicks = 0, landingEnds = 0;
        for (int i = 0; i < classes.Length; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            dashAttacks += mine.Count(p => p.Rush == RushPhase.DashAttack && p.RushTicks == 1);
            slashes += mine.Count(p => p.JumpSlash && p.Jump == JumpPhase.Recover && p.JumpTicks == 0);
            heldTicks += mine.Count(p => p.Rush == RushPhase.Rush && !p.Grounded);
            // feel/melee-charge-2: a charge held in the air that ends on its landing tick (the 3 m drops are real falls).
            landingEnds += mine.Zip(mine.Skip(1)).Count(p => p.First.Rush == RushPhase.Rush && !p.First.Grounded && p.Second.Rush == RushPhase.None && p.Second.Grounded);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches, {c.CorrectionErrors.Count(e => e > 1e-4f)} corrections; ended at {mine[^1].Position}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.Empty(mismatches);
        }
        output.WriteLine($"dash attacks {dashAttacks}, slashes {slashes}, ticks of charges held in the air {heldTicks}, charges ended by their landing {landingEnds}");
        Assert.True(landingEnds >= 2, $"only {landingEnds} charges ended by a landing");
        Assert.True(dashAttacks >= 4, $"only {dashAttacks} dash attacks");
        Assert.True(slashes >= 3, $"only {slashes} slashes");
        Assert.True(heldTicks >= 15, $"only {heldTicks} held ticks");
    }
}
