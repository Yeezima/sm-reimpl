using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/jump-pack-2 (the owner's request of 2026-10-29: "when an assault marine or raptor hops and then right clicks into a running charge, he should
/// collide with walls and play the same 'impact ricochet' animation that plays when a charge locks on to and sticks to and hits a target"): the dash
/// attack's lunge meets walls from its first tick and up to 60 degrees off head-on (<c>JumpPackAttackTuning.DashWallsFromStart</c>,
/// <c>DashWallHitAngle</c>), going into its wall stop (<see cref="RushPhase.DashCollision"/>, the bull rush's connect clip). Before, a wall only
/// blocked until 0.3 s into the lunge and a wall met more than 25 degrees off head-on was slid along. Synthetic level pieces, the
/// <see cref="JumpPackAttackTests"/> config (its lunge set like the reader's). No original bytes.
/// </summary>
public class DashAttackWallTests(ITestOutputHelper output)
{
    const int Assault = 0;
    static SimConfig Config => JumpPackAttackTests.Config;

    /// <summary>A floor at z = 0 and a 6 m high wall slab across the path, its face through (0, <paramref name="distance"/>) turned <paramref name="angle"/> degrees off head-on.</summary>
    static CollisionWorld Wall(float distance, float angle)
    {
        float a = angle * MathF.PI / 180f;
        var along = new Vector3(MathF.Cos(a), MathF.Sin(a), 0f);   // the face's direction in the ground plane
        var normal = new Vector3(MathF.Sin(a), -MathF.Cos(a), 0f);  // toward the player (−Y when head-on)
        var p = new Vector3(0f, distance, 0f);
        Vector3 a0 = p - along * 40f, a1 = p + along * 40f, b0 = a0 - normal * 1f, b1 = a1 - normal * 1f, up = new(0f, 0f, 6f);
        var v = new List<Vector3> { new(-200, -200, 0), new(200, -200, 0), new(200, 200, 0), new(-200, 200, 0) };
        var i = new List<int> { 0, 1, 2, 0, 2, 3 };
        void Quad(Vector3 q0, Vector3 q1, Vector3 q2, Vector3 q3) { int n = v.Count; v.AddRange([q0, q1, q2, q3]); i.AddRange([n, n + 1, n + 2, n, n + 2, n + 3, n, n + 2, n + 1, n, n + 3, n + 2]); }
        Quad(a0, a1, a1 + up, a0 + up);   // the face
        Quad(b0, b1, b1 + up, b0 + up);   // the back
        Quad(a0 + up, a1 + up, b1 + up, b0 + up);
        Quad(a0, b0, b0 + up, a0 + up); Quad(a1, b1, b1 + up, a1 + up);
        var mesh = new StaticMesh(v.ToArray(), i.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    /// <summary>Dashes forward (+Y) from the origin, presses melee at the dash attack's window, then lets the lunge play: the states after the press.</summary>
    static (PlayerState Press, List<PlayerState> After) Lunge(CollisionWorld world)
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Assault }, Config);
        void Step(InputButtons b, float my = 0f) => s = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b, 0f), Config, out _, world);
        Step(InputButtons.Jump, 1f);
        Step(InputButtons.None, 1f);   // released before the decision: the dash
        while (s.EvadeTicks is > 0 and < 10) Step(InputButtons.None);
        Step(InputButtons.Melee);
        var press = s;
        var after = new List<PlayerState>();
        for (int k = 0; k < 80 && s.Rush != RushPhase.None; k++) { Step(InputButtons.None); after.Add(s); }
        return (press, after);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(20f)]
    [InlineData(30f)]   // slid along before (more than 25 degrees off head-on)
    [InlineData(45f)]
    [InlineData(55f)]
    public void A_wall_met_up_to_60_degrees_off_head_on_stops_the_lunge_into_the_wall_stop(float angle)
    {
        foreach (float distance in new[] { 6.5f, 9f, 13f })
        {
            var (press, after) = Lunge(Wall(distance, angle));
            Assert.Equal(RushPhase.DashAttack, press.Rush);
            int stop = after.FindIndex(p => p.Rush == RushPhase.DashCollision);
            output.WriteLine($"{angle} degrees, wall {distance - press.Position.Y:F2} m ahead of the press: wall stop on lunge tick {(stop < 0 ? "none" : (stop + 2).ToString())} at {(stop < 0 ? after[^1].Position : after[stop].Position)}");
            Assert.True(stop >= 0, $"{angle} degrees, {distance} m: no wall stop ({string.Join(",", after.Select(p => p.Rush).Distinct())})");
            Assert.DoesNotContain(after.Take(stop), p => p.Rush != RushPhase.DashAttack);
            // Stopped on the first tick the wall held it back: the tick before it still made most of its way.
            var state = after[stop];
            Assert.Equal(Vector2.Zero, new Vector2(state.Velocity.X, state.Velocity.Y));
            Assert.Equal(0, state.RushTicks);   // the wall stop starts with this tick (Rush.Stopped)
            Assert.Equal("SM_NAV_Bullrush2Idle_F_Collision_01", Rush.MoveOf(Config, state)!.Animation);
        }
    }

    [Theory]
    [InlineData(70f)]
    [InlineData(80f)]
    public void A_glancing_wall_is_still_slid_along(float angle)
    {
        var (_, after) = Lunge(Wall(8f, angle));
        Assert.DoesNotContain(after, p => p.Rush == RushPhase.DashCollision);
        Assert.Contains(after, p => p.Rush == RushPhase.DashStop);
    }

    [Fact]
    public void A_wall_just_ahead_stops_the_lunge_at_once_not_after_pressing_against_it()
    {
        // The window opens about 5.5 m into the dash: a wall 1 m past that is met on the lunge's first ticks.
        var (press, after) = Lunge(Wall(7f, 0f));
        int stop = after.FindIndex(p => p.Rush == RushPhase.DashCollision);
        output.WriteLine($"wall {7f - press.Position.Y:F2} m ahead: wall stop on lunge tick {stop + 2} ({(stop + 1) / 30f:F3} s after the press)");
        Assert.InRange(stop, 0, 3);   // before feel/jump-pack-2 the lunge pressed against it until 0.3 s (tick 10)
    }

    [Fact]
    public void A_curb_and_a_ramp_are_no_walls()
    {
        foreach (var (name, world) in new[]
        {
            ("0.3 m curb", Box(new(-20, 9, 0), new(20, 60, 0.3f))),
            ("20 degree ramp", Ramp(9f, 20f)),
        })
        {
            var (_, after) = Lunge(world);
            output.WriteLine($"{name}: {string.Join(" ", after.Select(p => p.Rush).Distinct())}; ended at {after[^1].Position}");
            Assert.DoesNotContain(after, p => p.Rush == RushPhase.DashCollision);
        }

        static CollisionWorld Box(Vector3 min, Vector3 max)
        {
            var v = new List<Vector3> { new(-200, -200, 0), new(200, -200, 0), new(200, 200, 0), new(-200, 200, 0) };
            var i = new List<int> { 0, 1, 2, 0, 2, 3 };
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); i.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
            Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1)); Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1)); Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
            var m = new StaticMesh(v.ToArray(), i.ToArray());
            return new CollisionWorld(m, m);
        }
        static CollisionWorld Ramp(float from, float degrees)
        {
            float rise = MathF.Tan(degrees * MathF.PI / 180f) * 40f;
            var v = new[] { new Vector3(-200, -200, 0), new Vector3(200, -200, 0), new Vector3(200, from, 0), new Vector3(-200, from, 0),
                            new Vector3(-200, from, 0), new Vector3(200, from, 0), new Vector3(200, from + 40f, rise), new Vector3(-200, from + 40f, rise) };
            var m = new StaticMesh(v, [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
            return new CollisionWorld(m, m);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.7f)]   // the install's SimConfig.ActionLockScale (feel/jump-pack-2: the owner's request, an estimate)
    public void Jump_leaves_the_wall_stop_at_its_branch_times_the_configs_lock_scale(float scale)
    {
        var c = Config with { ActionLockScale = scale };
        var world = Wall(9f, 0f);
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Assault }, c);
        void Step(InputButtons b, float my = 0f) => s = Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b, 0f), c, out _, world);
        Step(InputButtons.Jump, 1f);
        Step(InputButtons.None, 1f);
        while (s.EvadeTicks is > 0 and < 10) Step(InputButtons.None);
        Step(InputButtons.Melee);
        for (int k = 0; k < 40 && s.Rush == RushPhase.DashAttack; k++) Step(InputButtons.None);
        Assert.Equal(RushPhase.DashCollision, s.Rush);
        while (!s.Grounded) Step(InputButtons.None);
        // Jump on every other tick (fresh presses) until the stop lets it go.
        int ticks = 0;
        for (int k = 0; k < 60 && s.Rush == RushPhase.DashCollision; k++) { int before = s.RushTicks; Step(k % 2 == 0 ? InputButtons.Jump : InputButtons.None); ticks = before; }
        Assert.NotEqual(RushPhase.DashCollision, s.Rush);
        float evadeAt = Rush.MoveOf(Config, s with { Rush = RushPhase.DashCollision })!.EvadeAt;
        output.WriteLine($"scale {scale}: Jump left the wall stop with {ticks} ticks played ({ticks / 30f:F3} s; the branch {evadeAt:F3} s x {scale} = {evadeAt * scale:F3} s)");
        Assert.InRange(ticks / 30f, evadeAt * scale - 1e-4f, evadeAt * scale + 2f / 30f);
    }

    // ---------------------------------------------------------------- the review: an enemy just in front of the wall

    /// <summary>A floor at z = 0 and closed boxes (min, max).</summary>
    static CollisionWorld Boxes(params (Vector3 Min, Vector3 Max)[] boxes)
    {
        var v = new List<Vector3> { new(-200, -200, 0), new(200, -200, 0), new(200, 200, 0), new(-200, 200, 0) };
        var i = new List<int> { 0, 1, 2, 0, 2, 3 };
        foreach (var (min, max) in boxes)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); i.AddRange([n, n + 1, n + 2, n, n + 2, n + 3, n, n + 2, n + 1, n, n + 3, n + 2]); }
            Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1)); Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0)); Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1)); Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
        }
        var m = new StaticMesh(v.ToArray(), i.ToArray());
        return new CollisionWorld(m, m);
    }

    static Vector2[] FastLine(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];

    /// <summary>
    /// The dash attack (Assault, player 1) or the bull rush (marine, player 1) at 90 m/s along +Y, an enemy standing 1 m in front of a wall that is
    /// <paramref name="wall"/> m out: the tick the charger reaches the wall is the first its move comes within reach of the enemy for some of the
    /// distances, which lost the hit before (the contact was tested only for a charge still charging after its move). Returns the charger's state
    /// after the contact, the hit (or null) and whether the contact came on the wall's tick.
    /// </summary>
    static (PlayerState Charger, HitEvent? Hit, bool SameTick, ReactionKind Reaction) AtTheWall(bool dash, float wall)
    {
        var fast = new RushMove("lunge", FastLine(180f, 60), 2f, 25f / 30f, 25f / 30f, 25f / 30f, 25f / 30f);
        var pack = Config.JumpPacks[0] with { DashAttack = JumpPackAttackTests.DashAttack with { Charge = JumpPackAttackTests.DashAttack.Charge with { Rush = fast } } };
        var rush = RushTests.Spec with { Rush = fast, WallFrom = 0f, WallHitAngle = 60f, ContactUntil = 25f / 30f };
        var c = Config with { JumpPacks = [pack], Classes = [Config.Classes[0], Config.Classes[1] with { MeleeRush = rush }] };
        // The dash attack starts 1.7 m up in the dash and flies level: its enemy stands on a 1.5 m pedestal against the wall (within the 1.5 m vertical reach).
        var world = Boxes((new(-40f, wall, 0f), new(40f, wall + 1f, 6f)), dash ? (new(-1f, wall - 1.4f, 0f), new(1f, wall, 1.5f)) : (new(-1f, -50f, -2f), new(1f, -49f, -1f)));
        var w = new World(c, world, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, wall - 1f, dash ? 1.5f : 0f), MathF.PI)]);
        w.Spawn(1, dash ? Assault : 1);
        w.Spawn(2, 1);
        HitEvent? hit = null;
        bool pressed = false, sameTick = false;
        for (int k = 0; k < 90 && hit is null; k++)
        {
            int tick = k;
            uint view = w.Tick;
            var me = w.Players[1];
            PlayerInput mine;
            if (dash)
            {
                bool press = !pressed && Rush.CanDashAttack(me, c, world);
                pressed |= press;
                mine = new PlayerInput(0, 0f, tick <= 1 ? 1f : 0f, 0f, (tick == 0 ? InputButtons.Jump : InputButtons.None) | (press ? InputButtons.Melee : InputButtons.None));
            }
            else
            {
                var run = new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint);
                bool press = !pressed && me.Rush == RushPhase.None && Rush.CanStart(me, run, rush, c);
                pressed |= press;
                mine = pressed && !press ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.None) : run with { Buttons = InputButtons.Sprint | (press ? InputButtons.MeleeHeavy : InputButtons.None) };
            }
            var before = w.Players[1];
            w.Step(id => (id == 1 ? mine : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = view });
            foreach (var h in w.LastHits) if (h.Shooter == 1 && h.Target == 2) hit = h;
            // The wall's tick: the charger ended the tick at the wall (its capsule against the face).
            if (hit is not null) sameTick = MathF.Abs(w.Players[1].Position.Y - (wall - c.CapsuleRadius)) < 0.06f && Rush.Charging(before.Rush);
        }
        return (w.Players[1], hit, sameTick, w.Players[2].Reaction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_enemy_a_metre_in_front_of_a_wall_is_hit_and_the_hit_wins_over_the_wall_stop(bool dash)
    {
        int same = 0;
        for (float wall = 12f; wall <= 18f; wall += 0.25f)
        {
            var (charger, hit, sameTick, reaction) = AtTheWall(dash, wall);
            output.WriteLine($"{(dash ? "dash attack" : "bull rush")}, wall {wall:F2} m, enemy {wall - 1f:F2} m: {(hit is { } h ? $"hit {h.ShieldDamage + h.HealthDamage:F0}" : "no hit")}, charger {charger.Rush} at {charger.Position.Y:F2}{(sameTick ? " (on the wall's tick)" : "")}, enemy {reaction}");
            Assert.NotNull(hit);
            Assert.Equal(100f, hit!.Value.ShieldDamage + hit.Value.HealthDamage, 3);
            Assert.Equal(ReactionKind.Stun, reaction);
            Assert.Equal(dash ? RushPhase.DashHit : RushPhase.Collision, charger.Rush);   // the hit's stop, not the wall's
            if (sameTick) same++;
        }
        output.WriteLine($"{same} placements connected on the wall's tick");
        Assert.True(same > 0, "no placement put the contact on the wall's tick");
    }
}
