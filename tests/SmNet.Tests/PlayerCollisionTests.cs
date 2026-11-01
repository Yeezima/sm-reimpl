using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/player-collision (protocol 43): players' capsules block each other in the shared movement step. Walking into a player, an angled walk
/// sliding around one, a head-on run with a lagged view, two players on one spot, spawns, a charge into an ally, knockback into a player, a jump-pack
/// landing on a head, a melee lunge, the dead, order independence, the wire and the server's bound, prediction at 150 ms RTT / 5 % loss in a crowd,
/// and the drawn separation. Synthetic configs only (no install).
/// </summary>
public class PlayerCollisionTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = new();
    const float Reach = 0.9f;   // two capsule radii (0.45 m each)
    const float Tol = 2e-3f;

    static PlayerState Player(int id, Vector3 at, float yaw = 0f, SimConfig? c = null) =>
        Combat.Respawned(new PlayerState(id, at, Vector3.Zero, yaw, true), c ?? Config);

    static BodyView Bodies(params (int Id, Vector3 Feet)[] bodies) => BodyView.Of(bodies.Select(b => new PlayerBody(b.Id, b.Feet, b.Feet)));

    static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    // ---------------------------------------------------------------- the movement step

    [Fact]
    public void Walking_into_a_standing_player_stops_at_contact_and_loses_the_speed_into_him()
    {
        var body = new Vector3(0f, 3f, 0f);
        var s = Player(1, Vector3.Zero);
        float closest = float.MaxValue;
        using (PlayerCollision.Use(Bodies((2, body))))
            for (int k = 0; k < 60; k++)
            {
                s = Combat.StepPlayer(s, new PlayerInput((uint)k + 1, 0f, 1f, 0f, InputButtons.Sprint), Config, out _);
                closest = MathF.Min(closest, Flat(s.Position, body));
            }
        output.WriteLine($"stopped at y {s.Position.Y:F4} (contact {3f - Reach:F4}), closest {closest:F4} m, velocity {s.Velocity}");
        Assert.InRange(closest, Reach - Tol, Reach + 0.05f);
        Assert.InRange(s.Velocity.Y, -0.01f, 0.01f);   // the speed into him is gone, as against a wall

        // Without the scope (or with collision off) the same walk passes through him: the rules only apply inside one.
        var free = Player(1, Vector3.Zero);
        for (int k = 0; k < 60; k++) free = Combat.StepPlayer(free, new PlayerInput((uint)k + 1, 0f, 1f, 0f, InputButtons.Sprint), Config, out _);
        Assert.True(free.Position.Y > 4f);
    }

    [Fact]
    public void An_angled_walk_slides_around_the_body_without_entering_it()
    {
        var body = new Vector3(0.3f, 3f, 0f);
        var s = Player(1, Vector3.Zero);
        float closest = float.MaxValue;
        using (PlayerCollision.Use(Bodies((2, body))))
            for (int k = 0; k < 60; k++)
            {
                s = Combat.StepPlayer(s, new PlayerInput((uint)k + 1, 0f, 1f, 0f, InputButtons.None), Config, out _);
                closest = MathF.Min(closest, Flat(s.Position, body));
            }
        output.WriteLine($"ended at {s.Position}, closest {closest:F4} m");
        Assert.True(closest >= Reach - Tol, $"entered the body: {closest:F4} m");
        Assert.True(s.Position.Y > 4f, "the slide should carry it past the body");
        Assert.True(s.Position.X < 0f, "it slides off on the side away from the body");
    }

    [Fact]
    public void A_melee_lunge_or_any_glide_stops_at_contact_range()
    {
        var body = new Vector3(0f, 2f, 0f);
        var s = Player(1, Vector3.Zero);
        using (PlayerCollision.Use(Bodies((2, body))))
            for (int k = 0; k < 10; k++) s = Movement.Glide(s, 0f, new Vector2(0f, 15f), Config);
        Assert.InRange(Flat(s.Position, body), Reach - Tol, Reach + Tol);
        Assert.Equal(0f, s.Velocity.Y, 3);
    }

    [Fact]
    public void Knockback_does_not_push_a_player_into_another()
    {
        var c = Config;
        var body = new Vector3(0f, 1.6f, 0f);
        var hit = Reaction.Start(Player(1, Vector3.Zero), c, new ReactionEntry(0f, ReactionKind.Stagger, Backward: 3f), ReactionDirection.Back, new Vector2(0f, 1f));
        Assert.True(hit.PushDistance > 0 && hit.PushTicks > 0);
        var s = hit;
        float closest = float.MaxValue;
        using (PlayerCollision.Use(Bodies((2, body))))
            for (int k = 0; k < 30; k++)
            {
                s = Combat.StepPlayer(s, new PlayerInput((uint)k + 1, 0f, 0f, 0f, InputButtons.None), c, out _);
                closest = MathF.Min(closest, Flat(s.Position, body));
            }
        var free = hit;
        for (int k = 0; k < 30; k++) free = Combat.StepPlayer(free, new PlayerInput((uint)k + 1, 0f, 0f, 0f, InputButtons.None), c, out _);
        output.WriteLine($"knockback 3 m toward a body 1.6 m away: stopped at {s.Position.Y:F3} (closest {closest:F4}); without bodies at {free.Position.Y:F3}");
        Assert.True(free.Position.Y > 2.5f);
        Assert.True(closest >= Reach - Tol);
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(0f)]      // straight onto the head: the tie direction
    [InlineData(-0.6f)]
    public void A_fall_or_jump_pack_landing_on_a_player_slides_off_and_never_ends_inside(float offset)
    {
        var body = Vector3.Zero;
        foreach (bool pack in new[] { false, true })
        {
            var s = Player(1, new Vector3(offset, 0f, 6f)) with { Grounded = false };
            float worstInside = 0f;
            int held = 0;
            using (PlayerCollision.Use(Bodies((2, body))))
                for (int k = 0; k < 90; k++)
                {
                    s = pack ? Movement.Fly(s, 0f, Vector2.Zero, s.Velocity.Z, 20f, Config)
                             : Movement.Step(s, new PlayerInput((uint)k + 1, 0f, 0f, 0f, InputButtons.None), Config);
                    float d = Flat(s.Position, body);
                    if (s.Position.Z < 1.9f - Tol && d < Reach - Tol) worstInside = MathF.Max(worstInside, Reach - d);
                    if (MathF.Abs(s.Position.Z - 1.9f) < 1e-4f) held++;
                }
            output.WriteLine($"offset {offset} ({(pack ? "jump pack" : "fall")}): ended at {s.Position} grounded {s.Grounded}, {held} ticks on the head, worst inside {worstInside:F4} m");
            Assert.Equal(0f, worstInside);
            Assert.True(s.Grounded && s.Position.Z == 0f, "it lands on the floor beside him");
            Assert.True(Flat(s.Position, body) >= Reach - Tol);
            Assert.InRange(held, 1, 12);   // held on the head for a few ticks while it slides off at 6 m/s
        }
    }

    [Fact]
    public void The_dead_and_bodies_above_the_head_band_do_not_collide()
    {
        var dead = Player(2, new Vector3(0f, 2f, 0f)) with { Alive = false };
        var view = PlayerCollision.See([dead, Player(3, new Vector3(0f, 4f, 2.5f))], 1, 10, 11, Config)!;
        Assert.Single(view.Bodies);   // the dead one is not a body
        var s = Player(1, Vector3.Zero);
        using (PlayerCollision.Use(view))
            for (int k = 0; k < 60; k++) s = Combat.StepPlayer(s, new PlayerInput((uint)k + 1, 0f, 1f, 0f, InputButtons.None), Config, out _);
        Assert.True(s.Position.Y > 5f, "walks through the corpse's place and under the player standing 2.5 m up");
    }

    [Fact]
    public void Others_are_carried_on_by_their_velocity_up_to_the_cap()
    {
        var runner = Player(2, new Vector3(0f, 10f, 0f)) with { Velocity = new Vector3(0f, 6f, 0f) };
        var charger = Player(3, new Vector3(5f, 0f, 0f)) with { Velocity = new Vector3(30f, 0f, 0f) };   // 4 m in 4 ticks
        var view = PlayerCollision.See([runner, charger], 1, 100, 104, Config)!;
        Assert.Equal(new Vector3(0f, 10f + 6f * 4f / 30f, 0f), view.Bodies[0].Feet);   // 4 ticks
        Assert.Equal(5f + PlayerCollision.MaxExtrapolation, view.Bodies[1].Feet.X, 4);   // capped at 3 m
        var far = PlayerCollision.See([runner], 1, 100, 140, Config)!;
        Assert.Equal(10f + 6f * PlayerCollision.MaxExtrapolationTicks / 30f, far.Bodies[0].Feet.Y, 4);   // at most 10 ticks
        Assert.Null(PlayerCollision.See([runner], 1, 100, 104, Config with { PlayerBodies = false }));
    }

    // ---------------------------------------------------------------- the world

    /// <summary>Steps the world with every player's input naming the tick <paramref name="lag"/> ticks back (the newest a client could hold).</summary>
    static void Run(World w, int ticks, Func<int, PlayerState, PlayerInput> input, int lag = 1, Action? each = null)
    {
        for (int k = 0; k < ticks; k++)
        {
            uint body = w.Tick + 1 > (uint)lag ? w.Tick + 1 - (uint)lag : 0;
            uint view = w.Tick;
            w.Step(id => input(id, w.Players[id]) with { ViewTick = view, BodyTick = body });
            each?.Invoke();
        }
    }

    static float Clearance(World w) => PlayerCollision.Clearance(w.Players.Values, w.Config);

    [Theory]
    [InlineData(1, 0.05f)]
    [InlineData(4, 0.2f)]     // a local bot's lag: both close the last stale gap in the same ticks (17 cm, pushed apart within a few ticks)
    [InlineData(9, 0.2f)]     // 150 ms RTT
    public void Two_players_sprinting_straight_at_each_other_stop_face_to_face(int lag, float allowed)
    {
        foreach (bool on in new[] { false, true })
        {
            var c = Config with { PlayerBodies = on };
            var w = new World(c, null, [new SpawnPoint(new Vector3(0f, -6f, 0f), 0f), new SpawnPoint(new Vector3(0f, 6f, 0f), MathF.PI)]);
            w.Spawn(1); w.Spawn(2);
            float worst = float.MaxValue;
            Run(w, 90, (id, p) => new PlayerInput(0, 0f, 1f, id == 1 ? 0f : MathF.PI, InputButtons.Sprint), lag, () => worst = MathF.Min(worst, Clearance(w)));
            var a = w.Players[1].Position; var b = w.Players[2].Position;
            output.WriteLine($"lag {lag} ticks, collision {(on ? "on" : "off")}: worst clearance {worst:F3} m, ended {a.Y:F3} / {b.Y:F3}, final clearance {Clearance(w):F3} m");
            if (!on) { Assert.True(a.Y > b.Y, "without collision they run through each other"); continue; }
            Assert.True(a.Y < b.Y, "they never pass through each other");
            Assert.InRange(Clearance(w), -0.01f, 0.05f);   // face to face, pressing
            Assert.True(worst > -allowed, $"overlapped {-worst:F3} m");
        }
    }

    [Theory]
    [InlineData(1, 0.05f)]
    [InlineData(4, 0.2f)]
    [InlineData(9, 0.4f)]
    public void Two_players_sprinting_slightly_off_head_on_slide_past_each_other(int lag, float allowed)
    {
        var w = new World(Config, null, [new SpawnPoint(new Vector3(0.2f, -6f, 0f), 0f), new SpawnPoint(new Vector3(0f, 6f, 0f), MathF.PI)]);
        w.Spawn(1); w.Spawn(2);
        float worst = float.MaxValue;
        Run(w, 90, (id, p) => new PlayerInput(0, 0f, 1f, id == 1 ? 0f : MathF.PI, InputButtons.Sprint), lag, () => worst = MathF.Min(worst, Clearance(w)));
        var a = w.Players[1].Position; var b = w.Players[2].Position;
        output.WriteLine($"lag {lag} ticks: worst clearance {worst:F3} m, ended {a} / {b}");
        Assert.True(a.Y > b.Y + 10f, "they slide past each other and run on");
        Assert.True(a.X > b.X, "each passes on its own side");
        Assert.True(worst > -allowed, $"overlapped {-worst:F3} m");
    }

    [Fact]
    public void Two_players_on_one_spot_are_pushed_apart_both_ways_within_a_few_ticks()
    {
        var w = new World(Config, null, [new SpawnPoint(new Vector3(1f, 1f, 0f), 0f)]);
        w.Spawn(1); w.Spawn(2);
        Assert.Equal(w.Players[1].Position, w.Players[2].Position);   // one spawn point: the least crowded is still on him
        int ticks = 0;
        Run(w, 30, (id, p) => new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), 1, () => { if (Clearance(w) < -0.01f) ticks++; });
        var a = w.Players[1].Position; var b = w.Players[2].Position;
        output.WriteLine($"apart after {ticks} ticks overlapping: {a} / {b}, clearance {Clearance(w):F3} m");
        Assert.True(Clearance(w) >= -0.01f);
        Assert.InRange(ticks, 1, 8);
        Assert.True(Vector3.Dot(a - new Vector3(1f, 1f, 0f), b - new Vector3(1f, 1f, 0f)) < 0f, "they leave in opposite directions");
        Assert.True(w.Players.Values.All(p => p.Velocity == Vector3.Zero), "the push is not velocity");
    }

    [Fact]
    public void Spawns_never_overlap_while_a_free_spot_exists_and_a_crowded_spawn_takes_the_least_crowded_spot()
    {
        // Sixteen players on a 4 x 4 grid of points 3 m apart, then a round restart (everyone respawns at once).
        var points = Enumerable.Range(0, 16).Select(i => new SpawnPoint(new Vector3(i % 4 * 3f, i / 4 * 3f, 0f), 0f)).ToArray();
        var w = new World(Config, null, points);
        for (int id = 1; id <= 16; id++) w.Spawn(id);
        Assert.True(Clearance(w) >= 0f, $"spawn overlap {Clearance(w):F3}");
        // Without points: the placeholder circle (ids 13 apart landed 0.65 m apart before).
        var flat = new World(Config);
        for (int id = 1; id <= 16; id++) flat.Spawn(id);
        output.WriteLine($"placeholder spawns: clearance {Clearance(flat):F3} m");
        Assert.True(Clearance(flat) >= 0f);
        // Three points, three players: the second skips the point 1 m from the first; the third finds every point taken and takes the least
        // crowded one (1 m from the first), not the rotation's (on top of the first, as before).
        var few = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(1f, 0f, 0f), 0f), new SpawnPoint(new Vector3(10f, 0f, 0f), 0f)]);
        for (int id = 1; id <= 3; id++) few.Spawn(id);
        Assert.Equal(new Vector3(10f, 0f, 0f), few.Players[2].Position);
        Assert.Equal(new Vector3(1f, 0f, 0f), few.Players[3].Position);
    }

    [Fact]
    public void A_charge_into_an_ally_stops_at_contact_range_not_inside_him()
    {
        foreach (bool on in new[] { false, true })
        {
            var c = RushConfig with { PlayerBodies = on };
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0.3f, 5f, 0f), MathF.PI)]) { Rules = new MatchRules { TimeLimit = 0f } };
            w.Spawn(1, 0); w.Spawn(2, 0);
            float worst = float.MaxValue;
            bool charged = false;
            int k = 0;
            Run(w, 60, (id, p) => id == 1 && k < 3 ? new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | (k == 2 ? InputButtons.MeleeHeavy : InputButtons.None))
                                     : new PlayerInput(0, 0f, 0f, p.Yaw, InputButtons.None), 1,
                () => { k++; charged |= w.Players[1].Rush == RushPhase.Rush; worst = MathF.Min(worst, Clearance(w)); });
            output.WriteLine($"collision {(on ? "on" : "off")}: charged {charged}, closest clearance {worst:F3} m, ended at {w.Players[1].Position}, phase {w.Players[1].Rush}");
            Assert.True(charged);
            if (on) Assert.True(worst >= -0.01f, $"the charge went {-worst:F3} m into the ally");
        }
    }

    static SimConfig RushConfig => new()
    {
        Weapons = [new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f)],
        Classes = [new ClassLoadout("marine", 0, -1, [new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f)]) { MeleeRush = RushTests.Spec, Side = Team.SpaceMarines }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    /// <summary>A MeleeLockTests-shaped attack: closes (distance − 1.5 m) before the hit at tick 6.5, then a 0.6 m follow-through; variants 1, 3.5 and 7 m.</summary>
    static MeleeStep LungeStep()
    {
        static float T(float ticks) => ticks / 30f;
        float duration = T(30.5f), hit = T(6.5f);
        MeleeVariant Variant(float distance) => new(distance, Enumerable.Range(0, 61).Select(i =>
        {
            float t = duration * i / 60f;
            return MathF.Max(0f, distance - 1.5f) * Math.Clamp(t / hit, 0f, 1f) + 0.6f * Math.Clamp((t - hit) / (duration - hit), 0f, 1f);
        }).ToArray());
        return new MeleeStep(duration, hit, T(12.5f), T(18.5f), T(15.5f), T(21.5f), 1f, 0.5f, 3.5f)
        {
            Reach = new MeleeReach(0f, T(3f), 0f, hit, 3.5f, 1f, [Variant(1f), Variant(3.5f), Variant(7f)]),
        };
    }

    [Fact]
    public void A_locked_melee_combo_lunges_up_to_the_target_and_never_into_him()
    {
        var c0 = Config with { MeleeSteps = [LungeStep(), LungeStep(), LungeStep()], MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f, HealthMax = 1e6f };
        foreach (bool on in new[] { false, true })
        {
            var c = c0 with { PlayerBodies = on };
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 2.1f, 0f), MathF.PI)]);
            w.Spawn(1); w.Spawn(2);
            float worst = float.MaxValue;
            int k = 0;
            // Three presses, each in the last attack's combo window, all locked on player 2.
            Run(w, 100, (id, p) => id == 1
                    ? new PlayerInput(0, 0f, 0f, 0f, k is 0 or 16 or 32 ? InputButtons.Melee : InputButtons.None, MeleeTarget: 2)
                    : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None), 2,
                () => { k++; worst = MathF.Min(worst, Clearance(w)); });
            output.WriteLine($"collision {(on ? "on" : "off")}: attacker ended at {w.Players[1].Position.Y:F3} (target at 2.1), closest clearance {worst:F3} m");
            if (on) Assert.True(worst >= -0.01f, $"the lunges went {-worst:F3} m into the target");
        }
    }

    [Fact]
    public void A_knockback_does_not_carry_the_victim_into_a_bystander()
    {
        // sim/light-melee-flinch: a light hit knocks nobody back any more, so the shove is a heavy strike (the stagger).
        var shove = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 0f, 0f) { PrimaryReaction = new ReactionEntry(0f, ReactionKind.Stagger, Backward: 3f, Duration: 0.4f) };
        var c0 = Config with { Classes = [new ClassLoadout("default", 0, -1, [shove]) { MeleeHeavy = [shove] }], MeleeRange = 4f, MeleeConeDegrees = 30f, HealthMax = 1000f };
        foreach (bool on in new[] { false, true })
        {
            var c = c0 with { PlayerBodies = on };
            // Attacker, the victim 2.05 m ahead and a bystander 2.06 m behind the victim, beyond the 4 m reach (spawn points 2 m apart: none is skipped).
            var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 2.05f, 0f), MathF.PI), new SpawnPoint(new Vector3(0.2f, 4.1f, 0f), MathF.PI)]);
            w.Spawn(1); w.Spawn(2); w.Spawn(3);
            float worst23 = float.MaxValue;
            int k = 0;
            bool pushed = false;
            Run(w, 40, (id, p) => new PlayerInput(0, 0f, 0f, id == 1 ? 0f : MathF.PI, id == 1 && k == 0 ? InputButtons.MeleeHeavy : InputButtons.None), 2, () =>
            {
                k++;
                pushed |= w.Players[2].PushDistance > 0;
                worst23 = MathF.Min(worst23, Flat(w.Players[2].Position, w.Players[3].Position) - Reach);
            });
            output.WriteLine($"collision {(on ? "on" : "off")}: knocked back {pushed}, victim at {w.Players[2].Position}, bystander at {w.Players[3].Position}, closest {worst23:F3} m");
            Assert.True(pushed);
            if (on) Assert.True(worst23 >= -0.01f, $"the knockback carried the victim {-worst23:F3} m into the bystander");
            else Assert.True(worst23 < -0.3f, "control: without collision the knockback carries the victim through the bystander");
        }
    }

    [Fact]
    public void A_jump_pack_landing_on_a_players_head_slides_off()
    {
        // The Assault (class 0) jumps straight up; the Tactical (class 1) walks under him and stops there; the Assault comes down on the head.
        var c = JumpPackTests.Config;
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(2f, 0f, 0f), 0f)]);
        w.Spawn(1, 0); w.Spawn(2, 1);
        int k = 0, onHead = 0;
        float worstInside = 0f;
        bool flew = false;
        Run(w, 150, (id, p) =>
        {
            if (id == 1) return new PlayerInput(0, 0f, 0f, 0f, k is >= 0 and < 12 ? InputButtons.Jump : InputButtons.None);
            bool under = p.Position.X <= 0.02f;
            // Walk -X (yaw π/2 faces −X) until under him, then stand.
            return new PlayerInput(0, 0f, under ? 0f : 1f, MathF.PI / 2f, InputButtons.None);
        }, 1, () =>
        {
            k++;
            var a = w.Players[1].Position; var b = w.Players[2].Position;
            flew |= a.Z > 2f;
            if (MathF.Abs(a.Z - (b.Z + 1.9f)) < 1e-3f) onHead++;
            if (a.Z < b.Z + 1.9f - 0.01f && Flat(a, b) < Reach - 0.01f) worstInside = MathF.Max(worstInside, Reach - Flat(a, b));
        });
        var a = w.Players[1]; var b = w.Players[2];
        output.WriteLine($"assault ended at {a.Position} (grounded {a.Grounded}), tactical at {b.Position}; {onHead} ticks on the head, worst inside {worstInside:F3} m");
        Assert.True(flew, "the Assault jumped");
        Assert.True(b.Position.X < 0.1f, "the Tactical got under him");
        Assert.True(onHead > 0, "he came down on the head");
        Assert.Equal(0f, worstInside);
        Assert.True(a.Grounded && Flat(a.Position, b.Position) >= Reach - 0.01f, "he slid off and stands beside him");
    }

    [Fact]
    public void The_result_does_not_depend_on_the_order_the_server_steps_the_players()
    {
        // Eight players walking into one crowd (each at its own placeholder spot), added (and so stepped) in two orders: identical states.
        var a = StepInOrder([1, 2, 3, 4, 5, 6, 7, 8]);
        var b = StepInOrder([8, 6, 4, 2, 7, 5, 3, 1]);
        Assert.Equal(a, b);
        output.WriteLine($"crowd clearance after 4 s of pushing and 1 s still: {PlayerCollision.Clearance(a, Config):F3} m (players {string.Join(", ", a.Select(p => $"{p.Position.X:F2},{p.Position.Y:F2}"))})");
        Assert.True(PlayerCollision.Clearance(a, Config) > -0.01f, "nobody is left inside another once they stop pushing");
    }

    /// <summary>The crowd with every player spawned at its own placeholder spot (added in <paramref name="order"/>), walking at the middle.</summary>
    static PlayerState[] StepInOrder(int[] order)
    {
        var w = new World(Config);
        foreach (int id in order) w.Spawn(id);
        Run(w, 150, (id, p) =>
        {
            if (w.Tick > 120) return new PlayerInput(0, 0f, 0f, p.Yaw, InputButtons.None);   // the last second: everyone stands still
            var to = -new Vector2(p.Position.X, p.Position.Y);
            float yaw = MathF.Atan2(-to.X, to.Y) + 0.3f * MathF.Sin(id + w.Tick * 0.1f);
            return new PlayerInput(0, 0.2f * MathF.Sin(id * 1.3f), 1f, yaw, id % 2 == 0 ? InputButtons.Sprint : InputButtons.None);
        }, lag: 3);
        return w.Players.Values.OrderBy(p => p.PlayerId).ToArray();
    }

    public enum Hostile { NoBodyTick, VeryOldBodyTick, OldLagOverTheWire, NeverAcked }

    /// <summary>
    /// The review's must-fix: a hostile client must not walk through a standing player by naming no body tick (lag byte 0), a very old one, a lag beyond the
    /// cap over the wire, or by never acknowledging a snapshot. The server's world (BoundBodies) raises every such tick to the oldest picture allowed.
    /// </summary>
    [Theory]
    [InlineData(Hostile.NoBodyTick)]
    [InlineData(Hostile.VeryOldBodyTick)]
    [InlineData(Hostile.OldLagOverTheWire)]
    [InlineData(Hostile.NeverAcked)]
    public void A_hostile_client_cannot_shed_the_bodies_and_walk_through_a_standing_player(Hostile how)
    {
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 2.1f, 0f), 0f)]) { BoundBodies = true };
        w.Spawn(1); w.Spawn(2);
        for (int k = 0; k < 60; k++)
        {
            uint t = w.Tick + 1;
            var walk = new PlayerInput(t, 0, 1, 0, InputButtons.None);
            var hostile = how switch
            {
                Hostile.NoBodyTick => walk,                                                                     // lag byte 0
                Hostile.VeryOldBodyTick => walk with { BodyTick = t > 45 ? t - 45 : 1 },                         // 1.5 s old
                Hostile.OldLagOverTheWire => Protocol.Quantize(walk with { BodyTick = t > 45 ? t - 45 : 1 }),    // the wire drops it: 0
                _ => GameServer.WithBoundedBody(walk with { BodyTick = t > 45 ? t - 45 : 1 }, ack: 0, slack: 7), // no ack: the server's own bound decides
            };
            w.Step(id => id == 1 ? hostile : new PlayerInput(t, 0, 0, 0, InputButtons.None, BodyTick: w.Tick));
        }
        output.WriteLine($"{how}: the walker ended at y {w.Players[1].Position.Y:F3} (the standing player at 2.1)");
        Assert.True(w.Players[1].Position.Y <= 2.1f - Reach + Tol, $"{how}: walked {w.Players[1].Position.Y - (2.1f - Reach):F3} m into the standing player");
    }

    [Fact]
    public void A_real_game_servers_world_bounds_the_body_tick()
    {
        // The re-review: dropping BoundBodies from the server's world must fail a test.
        var h = new Harness(LinkConditions.Perfect, 1, 0);
        Assert.True(h.Server.World.BoundBodies);
    }

    [Fact]
    public void A_hostile_45_tick_old_picture_from_before_a_player_stepped_in_is_raised_to_the_24_tick_bound()
    {
        // Player 2 walks in from 4 m to the side and stands at (0, 2.1) from about tick 25; the hostile walker sets off at tick 60 naming the picture
        // 45 ticks back, when player 2 was still on the way (that picture would let it through). The world raises it to 24 ticks back: blocked.
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(4f, 2.1f, 0f), 0f)]) { BoundBodies = true };
        w.Spawn(1); w.Spawn(2);
        bool farInOldPicture = false;
        float worst = float.MaxValue;
        for (int k = 0; k < 100; k++)
        {
            uint t = w.Tick + 1;
            var walker = new PlayerInput(t, 0, k >= 60 ? 1 : 0, 0, InputButtons.None, BodyTick: t > 45 ? t - 45 : 1);
            var mover = w.Players[2];
            var stepper = new PlayerInput(t, 0, mover.Position.X > 0.02f ? 1 : 0, MathF.PI / 2f, InputButtons.None, BodyTick: w.Tick);   // yaw π/2: walk −X
            if (k is >= 60 and < 75 && w.BodiesAt(t > 45 ? t - 45 : 1)?.FirstOrDefault(p => p.PlayerId == 2) is { } then && MathF.Abs(then.Position.X) > 1f) farInOldPicture = true;
            w.Step(id => id == 1 ? walker : stepper);
            if (k >= 60) worst = MathF.Min(worst, Flat(w.Players[1].Position, w.Players[2].Position) - Reach);
        }
        output.WriteLine($"player 2 stood at {w.Players[2].Position}; the walker ended at {w.Players[1].Position}; closest {worst:F3} m; 45-tick picture had player 2 away: {farInOldPicture}");
        Assert.True(farInOldPicture, "the 45-tick-old picture must not show the blocking player (else the test proves nothing)");
        Assert.True(MathF.Abs(w.Players[2].Position.X) < 0.5f, "player 2 stands in the walker's path");
        Assert.True(worst >= -Tol, $"the walker went {-worst:F3} m into the standing player (it may only slide around him)");
    }

    [Fact]
    public void Outside_the_server_an_input_naming_no_body_tick_steps_without_bodies()
    {
        // Tools and the many older world tests (BoundBodies off): 0 still means no bodies; a tick older than the cap is raised (never none).
        World Pair(bool bound)
        {
            var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, 2.1f, 0f), 0f)]) { BoundBodies = bound };
            w.Spawn(1); w.Spawn(2);
            return w;
        }
        static PlayerInput Walk(int id) => id == 1 ? new PlayerInput(0, 0, 1, 0, InputButtons.None) : new PlayerInput(0, 0, 0, 0, InputButtons.None);
        var through = Pair(false);
        for (int k = 0; k < 40; k++) through.Step(Walk);
        Assert.True(through.Players[1].Position.Y > 3f);
        var old = Pair(false);
        for (int k = 0; k < 40; k++) { uint t = old.Tick + 1; old.Step(id => Walk(id) with { BodyTick = t > 45 ? t - 45 : 1 }); }
        Assert.True(old.Players[1].Position.Y <= 2.1f - Reach + Tol);
        var blocked = Pair(false);
        Run(blocked, 40, (id, p) => Walk(id));
        Assert.NotNull(blocked.BodiesAt(blocked.Tick));
        Assert.InRange(blocked.Players[1].Position.Y, 2.1f - Reach - 0.05f, 2.1f - Reach + Tol);
        var off = new World(Config with { PlayerBodies = false });
        off.Spawn(1); off.Step(id => new PlayerInput(0, 0, 0, 0, InputButtons.None));
        Assert.Null(off.BodiesAt(off.Tick));   // nothing recorded when collision is off
    }

    [Fact]
    public void Being_pushed_apart_does_not_end_a_roll_as_a_wall_would()
    {
        // A stale snapshot shows a body overlapping just ahead (it has since moved on): the push apart (back, 0.1 m a tick) runs against a forward roll
        // that is not blocked. The roll must last as long as without that body; the push is left out of the evade's wall test.
        var c = EvadeTests.Config;
        var v = new List<Vector3>(); var idx = new List<int>();
        v.AddRange([new(-60, -60, 0), new(60, -60, 0), new(60, 60, 0), new(-60, 60, 0)]); idx.AddRange([0, 1, 2, 0, 2, 3]);
        var floor = new StaticMesh(v.ToArray(), idx.ToArray());
        var level = new CollisionWorld(floor, floor);
        int Roll(BodyView? view)
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 1 }, c);
            int ticks = 0;
            using (PlayerCollision.Use(view))
                for (int k = 0; k < 40; k++)
                {
                    s = Combat.StepPlayer(s, new PlayerInput((uint)k + 1, 0f, k == 0 ? 1f : 0f, 0f, k == 0 ? InputButtons.Jump : InputButtons.None), c, out _, level);
                    if (s.EvadeTicks > 0) ticks++;
                }
            return ticks;
        }
        var stale = BodyView.Of([new PlayerBody(2, new Vector3(0f, 9f, 0f), new Vector3(0f, 0.5f, 0f))], self: Vector3.Zero);
        int plain = Roll(null), pushed = Roll(stale);
        output.WriteLine($"roll ticks: {plain} alone, {pushed} while pushed back by a stale overlap");
        Assert.True(plain > 10);
        Assert.Equal(plain, pushed);
    }

    [Fact]
    public void A_ground_pound_onto_a_head_lands_in_the_tick_it_touches_the_head()
    {
        // The Assault jumps straight up, the Tactical walks under the dive's path (it drifts about 1 m forward, the pound's 50 degree limit), the Assault
        // pounds looking down: the impact comes on the head, at its height.
        var c = JumpPackTests.Config;
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(2f, 1.2f, 0f), 0f)]);
        w.Spawn(1, 0); w.Spawn(2, 1);
        int k = 0;
        var impacts = new List<JumpEvent>();
        Run(w, 120, (id, p) =>
        {
            if (id == 1) return new PlayerInput(0, 0f, 0f, 0f, k < 20 ? InputButtons.Jump : k == 26 ? InputButtons.Melee : InputButtons.None, -1.4f);
            return new PlayerInput(0, 0f, p.Position.X <= 0.02f ? 0f : 1f, MathF.PI / 2f, InputButtons.None);
        }, 1, () => { k++; impacts.AddRange(w.LastJumps.Where(j => j.Player == 1 && j.Kind == JumpEventKind.Impact)); });
        var a = w.Players[1]; var b = w.Players[2];
        output.WriteLine($"impacts: {string.Join(", ", impacts.Select(j => $"tick {j.Tick} at {j.Position}"))}; assault ended at {a.Position} (grounded {a.Grounded}), tactical at {b.Position}");
        var impact = Assert.Single(impacts);
        Assert.InRange(impact.Position.Z, 1.8f, 2.0f);   // on the head (its top as seen), not after sliding down beside him
        Assert.True(a.Grounded && a.Position.Z == 0f && Flat(a.Position, b.Position) >= Reach - 0.01f, "then he slid off and stands beside him");
    }

    // ---------------------------------------------------------------- the wire and the server

    [Fact]
    public void Protocol_43_carries_the_body_tick_as_a_lag_byte_and_drops_it_out_of_range()
    {
        Assert.Equal(52, Protocol.Version);
        Assert.Equal(40, Protocol.InputBytes);
        var inputs = new[]
        {
            new PlayerInput(1000, 0, 1, 0, InputButtons.None, BodyTick: 995),
            new PlayerInput(1000, 0, 1, 0, InputButtons.None, BodyTick: 0),
            new PlayerInput(1000, 0, 1, 0, InputButtons.None, BodyTick: 1000 - PlayerCollision.MaxBodyLagTicks),
            new PlayerInput(1000, 0, 1, 0, InputButtons.None, BodyTick: 1000 - PlayerCollision.MaxBodyLagTicks - 1),   // too old: none
            new PlayerInput(1000, 0, 1, 0, InputButtons.None, BodyTick: 1000),   // not before the input: none
            new PlayerInput(1000, 0, 1, 0, InputButtons.None, BodyTick: 1200),
        };
        var bytes = Protocol.Write(new InputBatch(inputs, 7, 990));
        Assert.Equal(1 + 4 + 4 + 1 + inputs.Length * Protocol.InputBytes, bytes.Length);
        var read = Assert.IsType<InputBatch>(Protocol.Read(bytes)).Inputs;
        Assert.Equal(new uint[] { 995, 0, 1000 - PlayerCollision.MaxBodyLagTicks, 0, 0, 0 }, read.Select(i => i.BodyTick).ToArray());
        Assert.Equal(read.Select(i => i.BodyTick), inputs.Select(i => Protocol.Quantize(i).BodyTick));   // the client predicts with what the wire carries
    }

    [Fact]
    public void The_server_raises_a_body_tick_older_than_the_acknowledged_snapshot_allows()
    {
        var input = new PlayerInput(500, 0, 1, 0, InputButtons.None, BodyTick: 450);
        Assert.Equal(493u, GameServer.WithBoundedBody(input, ack: 500, slack: 7).BodyTick);
        Assert.Equal(450u, GameServer.WithBoundedBody(input, ack: 455, slack: 7).BodyTick);   // within the slack
        Assert.Equal(0u, GameServer.WithBoundedBody(input with { BodyTick = 0 }, ack: 500, slack: 7).BodyTick);
        Assert.Equal(450u, GameServer.WithBoundedBody(input, ack: 0, slack: 7).BodyTick);
    }

    [Fact]
    public void The_config_hash_changes_only_when_player_collision_is_off()
    {
        Assert.True(Config.PlayerBodies);
        Assert.Equal(new SimConfig().Hash(), (Config with { PlayerBodies = true }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { PlayerBodies = false }).Hash());
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(43)]
    [InlineData(44)]
    public void Prediction_matches_the_server_in_a_crowd_at_150ms_rtt_and_5pct_loss(int seed)
    {
        var results = new Dictionary<bool, (int Compared, int Mismatches, double P99, float Max, float Worst, int Contacts, double MeanOverlap, float Final)>();
        foreach (bool on in new[] { false, true })
        {
            var c = Config with { PlayerBodies = on };
            var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
            var h = new Harness(link, seed, 8, c, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
            var truth = new Dictionary<uint, Dictionary<int, PlayerState>>();
            var predicted = Enumerable.Range(0, 8).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
            var probe = new OverlapProbe();
            int contacts = 0;
            double overlapSum = 0; int overlapSamples = 0;
            // Everyone walks or sprints at the middle with a wobble, and now and then backs off: a jostling crowd of eight.
            PlayerInput Input(int i, uint t)
            {
                var me = h.Clients[i].Predicted;
                var to = -new Vector2(me.Position.X, me.Position.Y);
                float yaw = MathF.Atan2(-to.X, to.Y) + 0.5f * MathF.Sin(t * 0.07f + i);
                uint phase = (t + (uint)i * 17) % 150;
                float forward = phase < 110 ? 1f : -1f;
                if (h.Server.World.Tick > 510) return new PlayerInput(t, 0f, 0f, me.Yaw, InputButtons.None);   // the last 3 s: everyone stands still
                return new PlayerInput(t, 0.4f * MathF.Sin(t * 0.05f + i * 2f), forward, yaw, phase % 50 < 25 ? InputButtons.Sprint : InputButtons.None);
            }
            h.Run(TimeSpan.FromSeconds(20), Input, () =>
            {
                var w = h.Server.World;
                if (!truth.ContainsKey(w.Tick))
                {
                    truth[w.Tick] = new Dictionary<int, PlayerState>(w.Players);
                    float clearance = PlayerCollision.Clearance(w.Players.Values, c);
                    probe.Add(clearance);
                    if (clearance < 0.05f) contacts++;
                    if (w.Tick <= 510) { overlapSum += MathF.Max(0f, -clearance); overlapSamples++; }
                }
                for (int i = 0; i < 8; i++)
                    if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
            });
            int compared = 0, mismatches = 0;
            var errors = new List<float>();
            for (int i = 0; i < 8; i++)
            {
                var client = h.Clients[i];
                Assert.Equal(ClientStatus.Connected, client.Status);
                errors.AddRange(client.CorrectionErrors);
                uint from = truth.Keys.Min() + 90;
                foreach (var (tick, pred) in predicted[i])
                {
                    if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(client.PlayerId, out var real)) continue;
                    compared++;
                    if (pred.Position != real.Position || pred.Velocity != real.Velocity) mismatches++;
                }
            }
            double p99 = Harness.Percentile(errors, 0.99);
            float final = PlayerCollision.Clearance(h.Server.World.Players.Values, c);
            results[on] = (compared, mismatches, p99, errors.Count == 0 ? 0f : errors.Max(), probe.Closest, contacts, overlapSum / Math.Max(1, overlapSamples), final);
            output.WriteLine($"collision {(on ? "on " : "off")}: {compared} ticks compared, {mismatches} position mismatches; corrections {errors.Count}, " +
                             $"{errors.Count(e => e > 0.001f)} above 1 mm, p99 {p99:F4} m, max {results[on].Max:F4} m; server {probe.Summary()}; " +
                             $"mean closest-pair overlap while pushing {results[on].MeanOverlap:F3} m; after 3 s standing still {final:F3} m");
        }
        var with = results[true];
        Assert.True(with.Compared > 2000, $"only {with.Compared} ticks compared");
        Assert.True(with.Contacts > 100, $"the crowd should be in contact often ({with.Contacts} ticks)");
        Assert.True(with.Mismatches <= results[false].Mismatches, $"collision added mispredictions: {with.Mismatches} against {results[false].Mismatches} without");
        Assert.True(with.P99 < 0.01, $"p99 correction {with.P99:F4} m");
        Assert.True(results[false].Worst < -0.5f, "without collision the crowd overlaps deeply (control)");
        Assert.True(with.MeanOverlap < 0.5 * results[false].MeanOverlap, $"mean overlap {with.MeanOverlap:F3} m with collision against {results[false].MeanOverlap:F3} m without");
        Assert.True(with.Final > -0.01f, $"still {-with.Final:F3} m inside each other after standing still for 3 s");
    }

    // ---------------------------------------------------------------- drawing

    [Fact]
    public void Drawn_remotes_are_moved_apart_from_each_other_and_the_own_player_by_at_most_a_radius()
    {
        var c = Config;
        var own = new Vector3(0f, 0f, 0f);
        var remotes = new[]
        {
            Player(2, new Vector3(0.5f, 0f, 0f)),    // 0.4 m into the own body: moved all of it
            Player(3, new Vector3(5f, 0f, 0f)),
            Player(4, new Vector3(5.3f, 0f, 0f)),    // 0.6 m into player 3: half each
            Player(5, new Vector3(10f, 0f, 0f)) with { Alive = false },
            Player(6, new Vector3(10.1f, 0f, 0f)),   // beside a corpse: not moved
        };
        var drawn = new DrawnBodies().Separate(remotes, own, 1, c);
        Assert.Equal(Reach, drawn[0].Position.X, 3);
        Assert.Equal(Reach, drawn[2].Position.X - drawn[1].Position.X, 3);
        Assert.Equal(4.7f, drawn[1].Position.X, 3);   // 0.3 m each
        Assert.Equal(remotes[3], drawn[3]);
        Assert.Equal(remotes[4], drawn[4]);
        // Deep overlaps are moved at most a capsule radius from where they are interpolated.
        var deep = new DrawnBodies().Separate([Player(2, new Vector3(0.01f, 0f, 0f))], own, 1, c);
        Assert.True(Flat(deep[0].Position, remotes[0].Position with { X = 0.01f }) <= DrawnBodies.MaxShift + 1e-4f);
        // Nothing to do: the same list.
        var apart = new[] { Player(2, new Vector3(3f, 0f, 0f)) };
        Assert.Same(apart, new DrawnBodies().Separate(apart, own, 1, c));
        Assert.Same(remotes, new DrawnBodies().Separate(remotes, own, 1, c with { PlayerBodies = false }));
    }
}
