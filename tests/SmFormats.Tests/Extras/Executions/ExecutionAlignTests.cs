using System.Numerics;
using SmSim;
using SmSim.Extras.Executions;

namespace SmFormats.Tests.Extras.Executions;

/// <summary>
/// Executions X2: <see cref="ExecutionAlign"/> on synthetic levels (no original bytes): open ground, a wall, a gap, a ledge, steps, slopes, a kill volume, a
/// spawn-room blocker, a sight blocker; the slide limit; the fixed variant order; and that a failure leaves both characters where they were.
/// </summary>
public class ExecutionAlignTests
{
    static readonly SimConfig Config = new();

    /// <summary>A row with the victim <paramref name="forward"/> m ahead of the attacker, facing it, and optional root paths (attacker frame / victim frame).</summary>
    internal static ExecutionRow Row(string id, float forward = 1f, Vector4[]? attackerRoot = null, Vector4[]? victimRoot = null, float right = 0f) =>
        new(id, "*", "*", ExecutionTargets.ChaosMarine, "SM_A_" + id, "SM_V_" + id, 2f, 2f,
            [new(ExecutionEventKind.Kill, 1.2f, "kill"), new(ExecutionEventKind.End, 2f, "end")], new Vector3(right, forward, 0f), MathF.PI, "SM_Base_MP", "Seq_" + id)
        {
            AttackerRoot = attackerRoot ?? [],
            VictimRoot = victimRoot ?? [],
        };

    static PlayerState Body(int id, Vector3 at, float yaw = 0f, Team team = Team.None) =>
        new(id, at, Vector3.Zero, yaw, true) { Alive = true, Team = team };

    sealed class Mesh
    {
        readonly List<Vector3> _v = [];
        readonly List<int> _i = [];
        public Mesh Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = _v.Count; _v.AddRange([a, b, c, d]); _i.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); return this;
        }
        /// <summary>A floor rectangle at height z.</summary>
        public Mesh Floor(float x0, float y0, float x1, float y1, float z = 0f) => Quad(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));
        /// <summary>An axis-aligned box (all six faces).</summary>
        public Mesh Box(Vector3 min, Vector3 max)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0));
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1));
            Quad(P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0));
            Quad(P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0));
            Quad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));
            return this;
        }
        public StaticMesh Build() => new(_v.ToArray(), _i.ToArray());
    }

    static CollisionWorld World(Mesh m, IReadOnlyList<KillVolume>? kills = null, IReadOnlyList<SpawnBlocker>? blockers = null)
    {
        var mesh = m.Build();
        return new CollisionWorld(mesh, mesh, kills, blockers);
    }

    static CollisionWorld Open() => World(new Mesh().Floor(-20, -20, 20, 20));

    /// <summary>The root motion of a clip that moves <paramref name="metres"/> along its own forward (Y) in <paramref name="samples"/> steps.</summary>
    static Vector4[] Forward(float metres, int samples = 9) =>
        Enumerable.Range(0, samples).Select(i => new Vector4(0f, metres * i / (samples - 1), 0f, 0f)).ToArray();

    [Fact]
    public void On_open_ground_the_attacker_slides_into_place_and_the_victim_stays()
    {
        var a = Body(1, new Vector3(0.3f, 0f, 0f), yaw: 0.4f);
        var v = Body(2, new Vector3(0f, 1.6f, 0f), yaw: 2f);
        var r = ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, Open());
        Assert.True(r.Ok, r.Failure.ToString());
        Assert.Equal(0, r.Variant);
        Assert.Equal(v.Position, r.VictimAt);   // the victim's feet never move
        // The attacker faces the victim and stands 1.2 m from it.
        var flat = new Vector2(v.Position.X - r.Attacker.Position.X, v.Position.Y - r.Attacker.Position.Y);
        Assert.Equal(1.2f, flat.Length(), 1);
        var facing = MeleeLock.Forward(r.Attacker.Yaw);
        Assert.True(Vector2.Dot(Vector2.Normalize(flat), facing) > 0.999f);
        Assert.Equal(ExecutionAlign.Wrap(r.Attacker.Yaw + MathF.PI), r.VictimYaw, 4);
        Assert.True(r.Slide <= ExecutionAlign.MaxSlide && r.SlideTicks > 0);
        // A lateral offset: the victim stands to the attacker's right.
        var side = ExecutionAlign.Align(a, v, [Row("s", forward: 1f, right: 0.5f)], Config, Open());
        Assert.True(side.Ok);
        var local = ExecutionAlign.ToWorld(new Vector2(0.5f, 1f), side.Attacker.Yaw);
        Assert.Equal(v.Position.X, side.Attacker.Position.X + local.X, 1);
        Assert.Equal(v.Position.Y, side.Attacker.Position.Y + local.Y, 1);
    }

    [Fact]
    public void A_slide_over_one_metre_fails_and_no_level_still_checks_the_slide()
    {
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 2.3f, 0f));
        var far = ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, Open());
        Assert.False(far.Ok);
        Assert.Equal(ExecutionAlignFailure.TooFar, far.Failure);
        Assert.Equal(a, far.Attacker);
        Assert.True(ExecutionAlign.Align(a, v with { Position = new Vector3(0f, 2.1f, 0f) }, [Row("x", forward: 1.2f)], Config, null).Ok);   // 0.9 m
        Assert.Equal(ExecutionAlignFailure.TooFar, ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, null).Failure);
        // Too close: the attacker backs off up to the same limit.
        var close = ExecutionAlign.Align(a, v with { Position = new Vector3(0f, 0.5f, 0f) }, [Row("x", forward: 1.2f)], Config, Open());
        Assert.True(close.Ok);
        Assert.True(close.Attacker.Position.Y < -0.6f);
    }

    [Fact]
    public void Variants_are_tried_in_order_and_none_fitting_is_a_clean_failure()
    {
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 1.5f, 0f));
        var rows = new[] { Row("far", forward: 3f), Row("near", forward: 1.2f), Row("also", forward: 1.4f) };
        var r = ExecutionAlign.Align(a, v, rows, Config, Open());
        Assert.True(r.Ok);
        Assert.Equal(1, r.Variant);
        Assert.Equal("near", r.Row!.Id);
        Assert.Equal([ExecutionAlignFailure.TooFar, ExecutionAlignFailure.None, ExecutionAlignFailure.None], r.Tried);
        var again = ExecutionAlign.Align(a, v, rows, Config, Open());
        Assert.Equal((r.Variant, r.Attacker, r.Distance), (again.Variant, again.Attacker, again.Distance));   // deterministic
        // A seed picks among the moves that fit, the same way every time.
        var picks = Enumerable.Range(0, 64).Select(k => ExecutionAlign.Align(a, v, rows, Config, Open(), ExecutionChoice.Seed((uint)k, 1, 2)).Row!.Id).ToList();
        Assert.Equal(["also", "near"], picks.Distinct().Order());
        Assert.Equal(picks, Enumerable.Range(0, 64).Select(k => ExecutionAlign.Align(a, v, rows, Config, Open(), ExecutionChoice.Seed((uint)k, 1, 2)).Row!.Id));
        Assert.NotEqual(ExecutionChoice.Seed(10, 1, 2), ExecutionChoice.Seed(10, 2, 1));
        var none = ExecutionAlign.Align(a, v, [Row("far", forward: 3f), Row("farther", forward: 4f)], Config, Open());
        Assert.False(none.Ok);
        Assert.Equal(-1, none.Variant);
        Assert.Null(none.Row);
        Assert.Equal([ExecutionAlignFailure.TooFar, ExecutionAlignFailure.TooFar], none.Tried);
        Assert.Equal((a, v.Position, v.Yaw), (none.Attacker, none.VictimAt, none.VictimYaw));
        Assert.Equal(ExecutionAlignFailure.NoVariant, ExecutionAlign.Align(a, v, [], Config, Open()).Failure);
    }

    [Fact]
    public void A_wall_in_the_slide_or_a_root_path_blocks_and_nothing_is_pulled_through_it()
    {
        // A wall between the attacker and its place.
        var wall = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 0.5f, 0), new(3, 0.6f, 3)));
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 2.0f, 0f));
        var r = ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, wall);
        Assert.False(r.Ok);
        Assert.Equal(ExecutionAlignFailure.Blocked, r.Failure);
        Assert.Equal(a, r.Attacker);
        // A wall behind the victim, into which its clip throws it back 1.3 m.
        var behind = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 2.6f, 0), new(3, 2.8f, 3)));
        var thrown = ExecutionAlign.Align(a, Body(2, new Vector3(0f, 1.2f, 0f)), [Row("x", forward: 1.2f, victimRoot: Forward(-1.3f))], Config, behind);
        Assert.Equal(ExecutionAlignFailure.Blocked, thrown.Failure);
        Assert.True(ExecutionAlign.Align(a, Body(2, new Vector3(0f, 1.2f, 0f)), [Row("x", forward: 1.2f, victimRoot: Forward(-0.5f))], Config, behind).Ok);
        // The attacker's own clip stepping 2.5 m forward through the victim into the far wall.
        Assert.Equal(ExecutionAlignFailure.Blocked,
            ExecutionAlign.Align(a, Body(2, new Vector3(0f, 1.2f, 0f)), [Row("x", forward: 1.2f, attackerRoot: Forward(2.5f))], Config, behind).Failure);
        Assert.True(ExecutionAlign.Align(a, Body(2, new Vector3(0f, 1.2f, 0f)), [Row("x", forward: 1.2f, attackerRoot: Forward(1.6f))], Config, behind).Ok);
    }

    [Fact]
    public void A_gap_under_the_slide_or_a_path_fails_even_when_the_capsule_would_bridge_it()
    {
        // Floor with a 0.25 m slot across y = 0.4 .. 0.65.
        var slot = World(new Mesh().Floor(-20, -20, 20, 0.4f).Floor(-20, 0.65f, 20, 20));
        var a = Body(1, Vector3.Zero);
        var r = ExecutionAlign.Align(a, Body(2, new Vector3(0f, 2.0f, 0f)), [Row("x", forward: 1.2f)], Config, slot);
        Assert.False(r.Ok);
        Assert.Equal(ExecutionAlignFailure.Gap, r.Failure);
        // No slide over it, but the victim's clip takes it back across a slot behind it.
        var behind = World(new Mesh().Floor(-20, -20, 20, 1.6f).Floor(-20, 2.0f, 20, 20));
        Assert.Equal(ExecutionAlignFailure.Gap,
            ExecutionAlign.Align(a, Body(2, new Vector3(0f, 1.2f, 0f)), [Row("x", forward: 1.2f, victimRoot: Forward(-1f))], Config, behind).Failure);
    }

    [Fact]
    public void A_ledge_fails_when_a_path_leaves_it_and_passes_when_it_stays_on_top()
    {
        // A 3 m high platform ending at y = 2.0 (a sheer drop past it).
        var ledge = World(new Mesh().Floor(-20, -20, 20, 2.0f, 3f).Floor(-20, 2.0f, 20, 20, 0f).Quad(new(-20, 2.0f, 0), new(20, 2.0f, 0), new(20, 2.0f, 3), new(-20, 2.0f, 3)));
        var a = Body(1, new Vector3(0f, 0f, 3f));
        var v = Body(2, new Vector3(0f, 1.2f, 3f));
        Assert.Equal(ExecutionAlignFailure.Gap, ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f, victimRoot: Forward(-1.2f))], Config, ledge).Failure);
        Assert.True(ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f, victimRoot: Forward(-0.5f))], Config, ledge).Ok);
    }

    [Fact]
    public void The_slide_never_crosses_a_gap_between_two_platforms()
    {
        // Two 3 m platforms, y below 2.0 and y above 2.6, with the ground between them; the pair on the same height.
        var split = World(new Mesh().Floor(-20, -20, 20, 2.0f, 3f).Floor(-20, 2.6f, 20, 20, 3f).Floor(-20, 2.0f, 20, 2.6f, 0f)
            .Quad(new(-20, 2.0f, 0), new(20, 2.0f, 0), new(20, 2.0f, 3), new(-20, 2.0f, 3)).Quad(new(-20, 2.6f, 0), new(20, 2.6f, 0), new(20, 2.6f, 3), new(-20, 2.6f, 3)));
        var a = Body(1, new Vector3(0f, 1.5f, 3f));
        var v = Body(2, new Vector3(0f, 3.2f, 3f));
        // The place 0.8 m short of the victim is y = 2.4, over the gap: the slide (0.9 m, inside the limit) must not be taken.
        var r = ExecutionAlign.Align(a, v, [Row("x", forward: 0.8f)], Config, split);
        Assert.False(r.Ok);
        Assert.Equal(ExecutionAlignFailure.Gap, r.Failure);
        Assert.Equal(a, r.Attacker);
    }

    [Fact]
    public void Low_walls_and_overhangs_on_a_path_block_and_a_victim_behind_turns_the_pair()
    {
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 1.2f, 0f));
        var row = Row("x", forward: 1.2f, victimRoot: Forward(-1f));
        // A thin wall 0.6 m high (above the step height, under the old waist sweep) behind the victim.
        var low = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 1.85f, 0), new(3, 1.87f, 0.6f)));
        Assert.Equal(ExecutionAlignFailure.Blocked, ExecutionAlign.Align(a, v, [row], Config, low).Failure);
        // A beam from 1.5 to 1.8 m above the ground on the same path.
        var beam = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 1.85f, 1.5f), new(3, 1.95f, 1.8f)));
        Assert.Equal(ExecutionAlignFailure.Blocked, ExecutionAlign.Align(a, v, [row], Config, beam).Failure);
        // A beam above the capsule's top (2.0 m and up) is clear.
        var high = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 1.85f, 2.0f), new(3, 1.95f, 2.3f)));
        Assert.True(ExecutionAlign.Align(a, v, [row], Config, high).Ok);
        // The victim behind the attacker: the attacker turns to face it, nobody moves.
        var behind = ExecutionAlign.Align(Body(1, Vector3.Zero, yaw: 0f), Body(2, new Vector3(0f, -1.2f, 0f)), [Row("x", forward: 1.2f)], Config, Open());
        Assert.True(behind.Ok);
        Assert.Equal(0f, behind.Slide, 3);
        Assert.Equal(MathF.PI, MathF.Abs(behind.Attacker.Yaw), 3);
        Assert.Equal(0f, ExecutionAlign.Wrap(behind.VictimYaw), 3);
    }

    [Fact]
    public void An_airborne_attacker_and_a_bystander_on_the_slide_fail_and_the_push_is_kept()
    {
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 2.0f, 0f));
        Assert.Equal(ExecutionAlignFailure.NotGrounded, ExecutionAlign.Align(a with { Grounded = false }, v, [Row("x", forward: 1.2f)], Config, Open()).Failure);
        // Someone standing where the attacker would slide to.
        Assert.Equal(ExecutionAlignFailure.Bystander,
            ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, Open(), bystanders: [new Vector3(0.3f, 0.7f, 0f)]).Failure);
        Assert.True(ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, Open(), bystanders: [new Vector3(3f, 0.7f, 0f), new Vector3(0f, -0.5f, 0f)]).Ok);
        // A prior push of the caller's own step (a body overlapping the attacker), which the alignment's glides must leave as it was.
        using (PlayerCollision.Use(PlayerCollision.See([a, Body(3, new Vector3(0.5f, 0f, 0f))], 1, 0, 0, Config)))
            Movement.Glide(a, 0f, Vector2.Zero, Config, Open());
        var push = PlayerCollision.LastPush;
        Assert.NotEqual(Vector2.Zero, push);
        Assert.True(ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, Open()).Ok);
        Assert.Equal(push, PlayerCollision.LastPush);
        Assert.Equal(ExecutionAlignFailure.Bystander,
            ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f)], Config, Open(), bystanders: [new Vector3(0.3f, 0.7f, 0f)]).Failure);
        Assert.Equal(push, PlayerCollision.LastPush);
    }

    [Fact]
    public void Steps_up_to_the_step_height_pass_and_higher_ones_fail()
    {
        var low = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 1.6f, 0), new(3, 4f, 0.25f)));
        var high = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 1.6f, 0), new(3, 4f, 0.6f)));
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 1.2f, 0f));
        var row = Row("x", forward: 1.2f, victimRoot: Forward(-1f));
        Assert.True(ExecutionAlign.Align(a, v, [row], Config, low).Ok);
        var r = ExecutionAlign.Align(a, v, [row], Config, high);
        Assert.False(r.Ok);
        Assert.Contains(r.Failure, new[] { ExecutionAlignFailure.Step, ExecutionAlignFailure.Blocked });
        // The pair itself on two levels more than a step apart.
        Assert.Equal(ExecutionAlignFailure.Step, ExecutionAlign.Align(a, v with { Position = new Vector3(0f, 1.2f, 0.6f) }, [Row("x", forward: 1.2f)], Config, high).Failure);
    }

    [Fact]
    public void A_walkable_slope_passes_and_a_steep_one_fails()
    {
        static CollisionWorld Ramp(float degrees)
        {
            float rise = MathF.Tan(degrees * MathF.PI / 180f) * 3f;   // 3 m long, from y = 1.6
            return World(new Mesh().Floor(-20, -20, 20, 1.6f).Quad(new(-20, 1.6f, 0), new(20, 1.6f, 0), new(20, 4.6f, rise), new(-20, 4.6f, rise)));
        }
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 1.2f, 0f));
        var row = Row("x", forward: 1.2f, victimRoot: Forward(-1f));
        Assert.True(ExecutionAlign.Align(a, v, [row], Config, Ramp(20f)).Ok);
        var steep = ExecutionAlign.Align(a, v, [row], Config, Ramp(75f));
        Assert.False(steep.Ok);
        Assert.Contains(steep.Failure, new[] { ExecutionAlignFailure.Slope, ExecutionAlignFailure.Step, ExecutionAlignFailure.Blocked });
    }

    [Fact]
    public void A_kill_volume_on_a_path_fails()
    {
        var kill = new KillVolume(new Vector3(0f, 2.2f, 0.5f), new Vector3(3f, 0.2f, 1f), 0f);
        var world = World(new Mesh().Floor(-20, -20, 20, 20), kills: [kill]);
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 1.2f, 0f));
        Assert.Equal(ExecutionAlignFailure.KillVolume, ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f, victimRoot: Forward(-1.2f))], Config, world).Failure);
        Assert.True(ExecutionAlign.Align(a, v, [Row("x", forward: 1.2f, victimRoot: Forward(-0.5f))], Config, world).Ok);
    }

    [Fact]
    public void A_spawn_blocker_stops_only_the_side_it_blocks()
    {
        // A Chaos spawn room's blocker across y = 2.0 .. 3.0: it stops Space Marines, not Chaos.
        var blocker = new SpawnBlocker(Team.Chaos, new Vector3(0f, 2.5f, 1.5f), new Vector3(6f, 1f, 3f), 0f);
        var world = World(new Mesh().Floor(-20, -20, 20, 20), blockers: [blocker]);
        var marine = Body(1, Vector3.Zero, team: Team.SpaceMarines);
        var victim = Body(2, new Vector3(0f, 1.2f, 0f), team: Team.Chaos);
        var row = Row("x", forward: 1.2f, attackerRoot: Forward(1.8f));
        var r = ExecutionAlign.Align(marine, victim, [row], Config, world);
        Assert.False(r.Ok);
        Assert.Contains(r.Failure, new[] { ExecutionAlignFailure.SpawnBlocker, ExecutionAlignFailure.Blocked });
        Assert.True(ExecutionAlign.Align(marine with { Team = Team.Chaos }, victim, [row], Config, world).Ok);
        // The probe names it when a path stands in the room.
        Assert.Equal(ExecutionAlignFailure.SpawnBlocker, ExecutionAlign.Probe([new Vector3(0f, 2.5f, 0f)], Team.SpaceMarines, Config, world));
        Assert.Equal(ExecutionAlignFailure.None, ExecutionAlign.Probe([new Vector3(0f, 2.5f, 0f)], Team.Chaos, Config, world));
    }

    [Fact]
    public void Without_sight_through_weapon_collision_it_fails()
    {
        // A screen that weapons hit but characters pass (weapon collision only), between the pair.
        var character = new Mesh().Floor(-20, -20, 20, 20).Build();
        var weapons = new Mesh().Floor(-20, -20, 20, 20).Quad(new(-3, 0.6f, 0), new(3, 0.6f, 0), new(3, 0.6f, 3), new(-3, 0.6f, 3)).Build();
        var world = new CollisionWorld(character, weapons);
        var r = ExecutionAlign.Align(Body(1, Vector3.Zero), Body(2, new Vector3(0f, 1.2f, 0f)), [Row("x", forward: 1.2f)], Config, world);
        Assert.False(r.Ok);
        Assert.Equal(ExecutionAlignFailure.NoSight, r.Failure);
    }

    [Fact]
    public void Distance_variants_cover_the_gap_without_a_slide_and_blend_the_attackers_path()
    {
        // As the original: variants at 1, 3.5 and 7 m (their root moves 0.5, 1.5, 3 m forward); the victim stands where it is.
        var row = Row("v", forward: 3.48f, right: 0.07f) with
        {
            AttackerVariants = [new("SM_v_7m", 7f, Forward(3f)), new("SM_v_1m", 1f, Forward(0.5f)), new("SM_v_3.5m", 3.5f, Forward(1.5f))],
        };
        Assert.Equal((1f, 7f), row.DistanceBand);
        var a = Body(1, Vector3.Zero);
        var v = Body(2, new Vector3(0f, 2.2f, 0f));
        var r = ExecutionAlign.Align(a, v, [row], Config, Open());
        Assert.True(r.Ok, r.Failure.ToString());
        Assert.Equal(0, r.SlideTicks);
        Assert.Equal(0f, r.Slide, 3);
        Assert.Equal(a.Position, r.Attacker.Position);
        Assert.Equal(MathF.Sqrt(2.2f * 2.2f - 0.07f * 0.07f), r.Distance, 3);
        var (near, far, w) = row.Blend(r.Distance);
        Assert.Equal(("SM_v_1m", "SM_v_3.5m"), (row.AttackerVariants[near].Clip, row.AttackerVariants[far].Clip));
        Assert.Equal((r.Distance - 1f) / 2.5f, w, 4);
        Assert.Equal(0.5f + w, row.AttackerRootAt(r.Distance)[^1].Y, 3);
        Assert.Equal(row.Blend(0.2f), (near, near, 0f));   // below the band: the shortest alone
        Assert.Equal(3f, row.AttackerRootAt(9f)[^1].Y, 4);   // above: the longest alone
        // Closer than the band's shortest variant: the attacker backs off to it (a slide under 1 m).
        var close = ExecutionAlign.Align(a, v with { Position = new Vector3(0f, 0.4f, 0f) }, [row], Config, Open());
        Assert.True(close.Ok);
        Assert.Equal(1f, close.Distance, 4);
        Assert.True(close.Slide is > 0.5f and < 0.7f);
        // The blended path decides the terrain check: a wall 2.4 m ahead stops the 7 m variant's long run only.
        var wall = World(new Mesh().Floor(-20, -20, 20, 20).Box(new(-3, 2.4f, 0), new(3, 2.6f, 3)));
        Assert.True(ExecutionAlign.Align(a, Body(2, new Vector3(0f, 1.5f, 0f)), [row with { VictimRoot = [] }], Config, wall).Ok);
        Assert.Equal(ExecutionAlignFailure.Blocked, ExecutionAlign.Align(Body(1, new Vector3(0f, 0f, 0f)), Body(2, new Vector3(0f, 6.9f, 0f)), [row], Config, wall).Failure);
    }

    [Fact]
    public void Root_paths_are_placed_in_the_character_frame()
    {
        var path = ExecutionAlign.RootPath(new Vector3(1f, 2f, 3f), MathF.PI / 2f, [new Vector4(0f, 1f, 0.5f, 0f), new Vector4(1f, 0f, 0f, 0f)]);
        // Yaw +90 degrees faces -X: forward 1 m goes to x - 1; right 1 m goes to y + 1.
        Assert.Equal(new Vector3(1f, 2f, 3f), path[0]);
        Assert.Equal(0f, path[1].X, 4); Assert.Equal(2f, path[1].Y, 4); Assert.Equal(3.5f, path[1].Z, 4);
        Assert.Equal(1f, path[2].X, 4); Assert.Equal(3f, path[2].Y, 4);
    }
}
