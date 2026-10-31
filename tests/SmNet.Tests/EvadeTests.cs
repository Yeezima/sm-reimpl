using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M21: the evade roll (start, direction, the clip's path, branch times, spacing, walls and ledges, blocked actions),
/// prediction exactness, protocol 13 and the config hash. A synthetic spec shaped like the install's ranged roll (1 s here,
/// 9 m, control back at 0.6 s).
/// </summary>
public class EvadeTests(ITestOutputHelper output)
{
    const int Assault = 0, Tactical = 1, Plain = 2;
    const float Dt = 1f / 30f;

    static readonly float[] Profile = [0f, 1.5f, 3f, 4.5f, 6f, 7f, 7.5f, 8f, 8.5f, 8.8f, 9f];   // metres at 0, 0.1, … 1 s
    static EvadeMove Clip(string name, Vector2 axis) => new(name, Profile.Select(d => axis * d).ToArray(), 1f, 0.6f, 0.6f, 0.7f, 0.7f, 1f);
    internal static readonly EvadeSpec Roll = new("roll", Clip("F", Vector2.UnitY), Clip("B", -Vector2.UnitY), Clip("L", -Vector2.UnitX), Clip("R", Vector2.UnitX))
    {
        MinInterval = 0.8f, AlignForward = true, WallHitAngle = 25f, AnimationSet = "evade", Camera = "evade_default_mp",
    };

    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.2f, 10, 50, 1.3f, 100f, 100f, 100f, 1f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 0.5f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);

    internal static readonly SimConfig Config = new()
    {
        Weapons = [Pistol, Rifle],
        Classes =
        [
            new ClassLoadout("assault", -1, 0, SimConfig.PlaceholderMeleeSteps) { JumpPack = 0, Evade = 0 },   // a pack wins over an evade
            new ClassLoadout("tactical", 1, 0, SimConfig.PlaceholderMeleeSteps) { Evade = 0 },
            new ClassLoadout("plain", 1, 0, SimConfig.PlaceholderMeleeSteps),
        ],
        JumpPacks = [JumpPackTests.Pack],
        Evades = [Roll],
    };

    // The heavy classes' hop: 6 m in 1 s, off the ground from 0.1 s to 0.6 s on a parabola peaking at 0.288 m (shaped like the
    // install's heavy bolter evade: 0.29 m, 0.267 s, there).
    const int Heavy = 3;
    static readonly float[] HopHeights = [0f, 0f, 0.192f, 0.288f, 0.288f, 0.192f, 0f, 0f, 0f, 0f, 0f];
    static EvadeMove HopClip(string name, Vector2 axis) => new(name, Profile.Select(d => axis * d * (2f / 3f)).ToArray(), 1f, 0.45f, 0.45f, 1f, 0.6f, 1f) { Height = HopHeights };
    internal static readonly EvadeSpec Hop = new("hop", HopClip("HF", Vector2.UnitY), HopClip("HB", -Vector2.UnitY), HopClip("HL", -Vector2.UnitX), HopClip("HR", Vector2.UnitX))
    {
        MinInterval = 0.8f, WallHitAngle = 25f, AnimationSet = "heavy_evade", TargetableModeTime = 0.322f, CollisionModeTime = 0.322f,
    };
    internal static readonly SimConfig HopConfig = Config with
    {
        Classes = [.. Config.Classes, new ClassLoadout("heavy", 1, 0, SimConfig.PlaceholderMeleeSteps) { Evade = 1 }],
        Evades = [Roll with { TargetableModeTime = 0.695f, CollisionModeTime = 0.546f }, Hop],
    };

    static PlayerState Fresh(int cls = Tactical, Vector3 at = default, SimConfig? c = null) =>
        Combat.Respawned(new PlayerState(1, at, Vector3.Zero, 0f, true) { Class = cls }, c ?? Config);

    static SelfEvents Step(ref PlayerState s, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f, CollisionWorld? world = null, SimConfig? c = null)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b), c ?? Config, out var e, world);
        return e;
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Y);
    static Vector2 World(float yaw, Vector2 local) =>
        new Vector2(MathF.Cos(yaw), MathF.Sin(yaw)) * local.X + new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw)) * local.Y;

    // ---------------------------------------------------------------- start and direction

    [Fact]
    public void Jump_starts_an_evade_only_on_classes_with_an_evade_and_no_jump_pack()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        Assert.Equal((1, EvadeDirection.Forward, 24), (s.EvadeTicks, s.EvadeDirection, s.EvadeLock));   // 0.8 s spacing = 24 ticks
        Assert.True(Evade.Evading(s));
        Assert.Equal(Roll, Loadouts.Evade(Config, s));

        var a = Fresh(Assault);
        Step(ref a, InputButtons.Jump);
        Assert.Equal((JumpPhase.TakeOff, 0, 0), (a.Jump, a.EvadeTicks, a.EvadeLock));   // the pack's take-off
        Assert.Null(Loadouts.Evade(Config, a));

        var p = Fresh(Plain);
        Step(ref p, InputButtons.Jump);
        Assert.Equal((0, 0, Vector3.Zero), (p.EvadeTicks, p.EvadeLock, p.Position));   // nothing happens
        Assert.Null(Loadouts.Evade(new SimConfig(), Fresh(0, c: new SimConfig()).Class));   // no install: no evade
    }

    [Theory]
    [InlineData(0f, 0f, EvadeDirection.Forward, 0f)]
    [InlineData(0.05f, 0.05f, EvadeDirection.Forward, 0f)]          // inside the dead zone: no direction
    [InlineData(0f, 1f, EvadeDirection.Forward, 0f)]
    [InlineData(0f, -1f, EvadeDirection.Back, 0f)]
    [InlineData(-1f, 0f, EvadeDirection.Left, 0f)]
    [InlineData(1f, 0.9f, EvadeDirection.Right, 0f)]
    [InlineData(-1f, -1f, EvadeDirection.Back, 0f)]                 // an exact diagonal rolls forward or back
    [InlineData(0.5f, 1f, EvadeDirection.Forward, -0.4636476f)]      // a forward roll turns to the stick (Align To Nav Direction)
    [InlineData(-1f, 1f, EvadeDirection.Forward, 0.7853982f)]
    public void The_direction_comes_from_the_stick_at_the_press_and_a_forward_roll_aligns_to_it(float mx, float my, EvadeDirection expected, float turn)
    {
        const float view = 0.3f;
        var s = Fresh();
        Step(ref s, InputButtons.Jump, mx, my, view);
        Assert.Equal(expected, s.EvadeDirection);
        Assert.Equal(view + turn, s.Yaw, 5);
        // The roll moves along its clip in the body's frame; the stick, view and buttons afterwards change neither.
        var start = Flat(s.Position) - World(s.Yaw, Roll.Move(expected).At(Dt));
        for (int n = 2; n <= 15; n++)
        {
            Step(ref s, InputButtons.None, -my, mx, view + n);   // a different stick and view every tick
            Assert.Equal(view + turn, s.Yaw, 5);
            var want = start + World(view + turn, Roll.Move(expected).At(n * Dt));
            Assert.True(Vector2.Distance(want, Flat(s.Position)) < 1e-3f, $"tick {n}: {Flat(s.Position)} vs {want}");
        }
        // Without alignment a forward roll keeps the view's yaw.
        var unaligned = Config with { Evades = [Roll with { AlignForward = false }] };
        var u = Fresh(c: unaligned);
        Step(ref u, InputButtons.Jump, mx, my, view, c: unaligned);
        Assert.Equal(view, u.Yaw, 5);
    }

    // ---------------------------------------------------------------- the path and the duration

    [Fact]
    public void The_roll_follows_the_clip_path_for_its_duration_then_ends()
    {
        const float yaw = 1f;
        var s = Fresh();
        var path = new List<Vector2>();
        for (int n = 1; n <= 30; n++)
        {
            Step(ref s, n == 1 ? InputButtons.Jump : InputButtons.None, yaw: yaw);
            path.Add(Flat(s.Position));
            Assert.Equal(n, s.EvadeTicks);
            Assert.Equal(n * Dt, Evade.Time(s, Config), 5);
            Assert.Equal(Math.Min(1f, n * Dt), Evade.Progress(s, Config), 4);
            var want = World(yaw, Roll.Forward.At(n * Dt));
            Assert.True(Vector2.Distance(want, path[^1]) < 1e-3f, $"tick {n}: {path[^1]} vs {want}");
            Assert.True(s.Grounded);
        }
        Assert.Equal(9f, path[^1].Length(), 3);                   // the whole clip: 9 m
        Assert.Equal(15f, (path[2] - path[1]).Length() / Dt, 2);   // 1.5 m per 0.1 s early on
        Assert.Equal("F", Evade.Move(s, Config)!.Animation);
        Step(ref s, InputButtons.None, yaw: yaw);                  // the clip has played: the evade is over, walking resumes
        Assert.Equal((0, EvadeDirection.Forward), (s.EvadeTicks, s.EvadeDirection));
        Assert.Equal(0f, Evade.Progress(s, Config));
        Assert.Null(Evade.Move(s, Config));
        Assert.True(Flat(s.Velocity).Length() < 1f);               // the clip ends almost still (0.2 m in its last 0.1 s)
    }

    [Fact]
    public void The_stick_ends_the_roll_from_its_move_time_and_not_before()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        for (int n = 2; n <= 18; n++) { Step(ref s, InputButtons.Sprint, 1f, 0f); Assert.Equal(n, s.EvadeTicks); }   // ignored before 0.6 s
        Assert.Equal(Roll.Forward.At(18 * Dt).Y, s.Position.Y, 3);
        Assert.Equal(0f, s.Position.X, 4);
        Step(ref s, InputButtons.Sprint, 1f, 0f);               // 18 ticks played = 0.6 s: control returns
        Assert.Equal(0, s.EvadeTicks);
        Assert.True(s.Velocity.X > 0f);                          // walking right, from the roll's velocity
    }

    // ---------------------------------------------------------------- blocked actions

    [Fact]
    public void Firing_is_blocked_until_the_ranged_branch_where_fire_ends_the_roll_and_shoots_at_once()
    {
        var s = Fresh();
        Assert.False(Step(ref s, InputButtons.Jump | InputButtons.Fire).Fired);
        int clip = s.Clip;
        for (int n = 2; n <= 18; n++) { Assert.False(Step(ref s, InputButtons.Fire).Fired); Assert.Equal(n, s.EvadeTicks); }
        Assert.Equal(clip, s.Clip);
        Assert.True(Step(ref s, InputButtons.Fire).Fired);      // 0.6 s
        Assert.Equal((0, clip - 1), (s.EvadeTicks, s.Clip));
        // Aim mode is off while evading; Aim held from the ranged branch ends the roll and aims.
        var a = Fresh();
        Step(ref a, InputButtons.Jump | InputButtons.Aim);
        Assert.False(a.Aiming);
        for (int n = 2; n <= 18; n++) { Step(ref a, InputButtons.Aim); Assert.False(a.Aiming); }
        Step(ref a, InputButtons.Aim);
        Assert.True(a.Aiming);
        Assert.Equal(0, a.EvadeTicks);
    }

    [Fact]
    public void Melee_and_switch_presses_are_lost_before_their_branches_and_end_the_roll_after()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        for (int n = 2; n <= 21; n++) { Step(ref s, n % 2 == 0 ? InputButtons.Melee | InputButtons.Switch : InputButtons.None); Assert.Equal((n, 0), (s.EvadeTicks, s.MeleeStep)); }
        Assert.Equal(WeaponSlot.Ranged, s.Slot);
        Step(ref s, InputButtons.Melee);                        // 21 ticks played = 0.7 s
        Assert.Equal((0, 1), (s.EvadeTicks, s.MeleeStep));
        var w = Fresh();
        Step(ref w, InputButtons.Jump);
        for (int n = 2; n <= 21; n++) Step(ref w, InputButtons.None);
        Step(ref w, InputButtons.Switch);
        Assert.Equal((0, WeaponSlot.Pistol), (w.EvadeTicks, w.Slot));
        // An evade cannot start during a melee attack, nor with a melee press in the same tick.
        var m = Fresh();
        Step(ref m, InputButtons.Melee);
        Step(ref m, InputButtons.Jump);
        Assert.Equal((0, 1), (m.EvadeTicks, m.MeleeStep));
        var both = Fresh();
        Step(ref both, InputButtons.Melee | InputButtons.Jump);
        Assert.Equal((0, 1), (both.EvadeTicks, both.MeleeStep));
    }

    [Fact]
    public void A_reload_carries_on_during_the_roll_and_completes_on_time()
    {
        var s = Fresh() with { Clip = 3 };
        Step(ref s, InputButtons.Reload);
        Assert.Equal(0.5f, s.ReloadLeft, 5);
        Step(ref s, InputButtons.Jump);
        Assert.Equal(1, s.EvadeTicks);
        Assert.Equal(0.5f - Dt, s.ReloadLeft, 5);
        for (int n = 0; n < 14; n++) Step(ref s, InputButtons.None);
        Assert.Equal((30, 0f), (s.Clip, s.ReloadLeft));         // 15 ticks of 1/30 s: done
        Assert.True(s.EvadeTicks > 0);
        // A reload press during the roll starts none.
        var r = Fresh() with { Clip = 3 };
        Step(ref r, InputButtons.Jump);
        Step(ref r, InputButtons.Reload);
        Assert.Equal(0f, r.ReloadLeft);
    }

    // ---------------------------------------------------------------- spacing and chaining

    [Theory]
    [InlineData(24, false)]
    [InlineData(25, true)]   // 24 ticks after the press = 0.8 s
    public void A_new_evade_waits_for_the_spacing_after_the_previous_press(int pressAt, bool starts)
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        for (int k = 2; k < pressAt; k++) Step(ref s, InputButtons.None, k >= 19 ? 1f : 0f, 0f);   // the stick ends the roll at 0.6 s
        Assert.Equal(0, s.EvadeTicks);
        Step(ref s, InputButtons.Jump, 1f, 0f);
        Assert.Equal(starts ? 1 : 0, s.EvadeTicks);
        if (starts) Assert.Equal(EvadeDirection.Right, s.EvadeDirection);
    }

    [Fact]
    public void Presses_during_the_roll_are_lost_and_one_at_its_end_chains_the_next()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        for (int n = 2; n <= 30; n++)
        {
            Step(ref s, n % 3 == 0 ? InputButtons.Jump : InputButtons.None);   // presses after the spacing too (0.8 s) but before NextAt
            Assert.Equal((n, EvadeDirection.Forward), (s.EvadeTicks, s.EvadeDirection));
        }
        Assert.True(s.JumpHeld);                                // tick 30 pressed: the next tick needs a new press edge
        Step(ref s, InputButtons.None);
        Assert.Equal(0, s.EvadeTicks);                          // ended at the clip's end
        var c = Fresh();
        Step(ref c, InputButtons.Jump);
        for (int n = 2; n <= 30; n++) Step(ref c, InputButtons.None);
        var before = c.Position;
        Step(ref c, InputButtons.Jump, 0f, -1f);                 // 1 s played = NextAt: chains into a back roll
        Assert.Equal((1, EvadeDirection.Back, 24), (c.EvadeTicks, c.EvadeDirection, c.EvadeLock));
        Assert.True(c.Position.Y < before.Y);
    }

    // ---------------------------------------------------------------- collision

    static CollisionWorld Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    static (Vector3, Vector3, Vector3, Vector3) Floor(float z, float x0 = -60, float x1 = 60, float y0 = -60, float y1 = 60) =>
        (new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

    static (Vector3, Vector3, Vector3, Vector3) Wall(Vector2 a, Vector2 b, float height = 4f) =>
        (new(a.X, a.Y, -1f), new(b.X, b.Y, -1f), new(b.X, b.Y, height), new(a.X, a.Y, height));

    [Fact]
    public void A_wall_met_head_on_stops_the_roll_and_ends_it()
    {
        var world = Mesh(Floor(0f), Wall(new(-50, 5), new(50, 5)));
        var s = Fresh();
        Step(ref s, InputButtons.Jump, world: world);
        int n = 1;
        while (s.EvadeTicks > 0 && n < 40) { Step(ref s, InputButtons.None, world: world); n++; Assert.True(s.Position.Y <= 5f - Config.CapsuleRadius + 0.01f, $"{s.Position}"); }
        output.WriteLine($"ended after {n} ticks at y {s.Position.Y:F3}");
        Assert.True(n < 30, $"the roll lasted {n} ticks");      // ended by the wall before the clip's end
        Assert.InRange(s.Position.Y, 5f - Config.CapsuleRadius - 0.05f, 5f - Config.CapsuleRadius + 0.01f);
        // Control: the same roll on open ground lasts the clip.
        var open = Mesh(Floor(0f));
        var o = Fresh();
        Step(ref o, InputButtons.Jump, world: open);
        for (int k = 2; k <= 30; k++) { Step(ref o, InputButtons.None, world: open); Assert.Equal(k, o.EvadeTicks); }
        Assert.Equal(9f, o.Position.Y, 2);
    }

    [Fact]
    public void A_wall_met_at_a_shallow_angle_is_slid_along_and_the_roll_goes_on()
    {
        // The wall runs 20° off the roll's direction (70° from head-on), 0.94 m from the start.
        var d = new Vector2(-MathF.Sin(20f * MathF.PI / 180f), MathF.Cos(20f * MathF.PI / 180f));
        var normal = new Vector2(d.Y, -d.X);
        var p = new Vector2(1f, 0f);
        var world = Mesh(Floor(0f), Wall(p - d * 30f, p + d * 30f));
        var s = Fresh();
        Step(ref s, InputButtons.Jump, world: world);
        for (int k = 2; k <= 30; k++)
        {
            Step(ref s, InputButtons.None, world: world);
            Assert.Equal(k, s.EvadeTicks);
            Assert.True(Vector2.Dot(Flat(s.Position) - p, normal) <= -Config.CapsuleRadius + 0.01f, $"tick {k}: through the wall at {s.Position}");
        }
        Assert.True(s.Position.Y > 6f, $"{s.Position}");          // slid along it
        Assert.True(s.Position.X < -0.5f, $"{s.Position}");
    }

    [Fact]
    public void Rolling_off_a_ledge_falls_and_the_roll_carries_on_through_the_air()
    {
        var world = Mesh(Floor(3f, y1: 2f), Floor(0f, y0: 2f), Wall(new(-60, 2), new(60, 2), 3f));
        var s = Fresh(at: new Vector3(0, 0, 3));
        Step(ref s, InputButtons.Jump, world: world);
        bool fell = false;
        for (int k = 2; k <= 30; k++)
        {
            Step(ref s, InputButtons.None, world: world);
            Assert.Equal(k, s.EvadeTicks);
            fell |= !s.Grounded && s.Position.Z < 3f && s.Position.Z > 0f;
        }
        Assert.True(fell);
        Assert.Equal(0f, s.Position.Z, 2);
        Assert.True(s.Grounded);
        // sim/combat-2 (item 43, owner's decision 2026-10-30): the roll keeps its horizontal travel through the fall (the whole clip's path, 9 m); before, it stopped after
        // one tick in the air over a real drop and ended at 4 m.
        Assert.InRange(s.Position.Y, 8.5f, 9.05f);
        // An evade does not start in the air.
        var air = Fresh(at: new Vector3(0, 0, 10)) with { Grounded = false };
        Step(ref air, InputButtons.Jump);
        Assert.Equal(0, air.EvadeTicks);
    }

    [Fact]
    public void A_roll_goes_up_a_step_without_ending_as_a_wall_hit()
    {
        // A 0.3 m step (under the 0.4 m step height) at y = 3.
        var world = Mesh(Floor(0f, y1: 3f), Wall(new(-60, 3), new(60, 3), 0.3f), Floor(0.3f, y0: 3f));
        var s = Fresh();
        Step(ref s, InputButtons.Jump, world: world);
        for (int k = 2; k <= 30; k++)
        {
            Step(ref s, InputButtons.None, world: world);
            Assert.Equal(k, s.EvadeTicks);   // never ended early
        }
        Assert.Equal(0.3f, s.Position.Z, 2);
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Y, 8.5f, 9.05f);
    }

    [Fact]
    public void A_roll_goes_up_a_30_degree_ramp_without_ending_as_a_wall_hit()
    {
        // Flat to y = 2, then a 30° ramp 4 m long (rising 2.31 m), then a plateau.
        float rise = 4f * MathF.Tan(30f * MathF.PI / 180f);
        var world = Mesh(Floor(0f, y1: 2f),
            (new(-60, 2, 0), new(60, 2, 0), new(60, 6, rise), new(-60, 6, rise)),
            Floor(rise, y0: 6f));
        var s = Fresh();
        Step(ref s, InputButtons.Jump, world: world);
        for (int k = 2; k <= 30; k++)
        {
            Step(ref s, InputButtons.None, world: world);
            Assert.Equal(k, s.EvadeTicks);
        }
        output.WriteLine($"ended at {s.Position}");
        Assert.Equal(rise, s.Position.Z, 1);
        Assert.InRange(s.Position.Y, 7f, 9.05f);   // the controller walks the ramp's horizontal distance
    }

    // ---------------------------------------------------------------- the heavy hop

    [Fact]
    public void The_heavy_hop_follows_its_height_curve_off_the_ground_and_lands_where_it_ends()
    {
        foreach (var world in new[] { null, Mesh(Floor(0f)) })
        {
            var s = Fresh(Heavy, c: HopConfig);
            int airborne = 0;
            float highest = 0f;
            for (int n = 1; n <= 30; n++)
            {
                Step(ref s, n == 1 ? InputButtons.Jump : InputButtons.None, world: world, c: HopConfig);
                Assert.Equal(n, s.EvadeTicks);
                float want = Hop.Forward.HeightAt(n * Dt);
                Assert.True(MathF.Abs(s.Position.Z - want) < 0.01f, $"{(world is null ? "flat" : "mesh")} tick {n}: z {s.Position.Z} vs {want}");
                Assert.Equal(Hop.Forward.At(n * Dt).Y, s.Position.Y, 2);
                if (!s.Grounded) airborne++;
                highest = MathF.Max(highest, s.Position.Z);
            }
            output.WriteLine($"{(world is null ? "flat" : "mesh")}: {airborne} ticks in the air, highest {highest:F3} m");
            Assert.InRange(airborne, 13, 16);                  // 0.1 to 0.6 s
            Assert.InRange(highest, 0.27f, 0.289f);
            Assert.True(s.Grounded);
            Assert.Equal(0f, s.Position.Z, 3);
            Assert.Equal(6f, s.Position.Y, 2);                  // (2/3) of the roll's 9 m
        }
        // The Tactical roll never leaves the ground (control).
        var r = Fresh(c: HopConfig);
        Step(ref r, InputButtons.Jump, c: HopConfig);
        for (int n = 2; n <= 30; n++) { Step(ref r, InputButtons.None, c: HopConfig); Assert.True(r.Grounded); Assert.Equal(0f, r.Position.Z); }
        Assert.True(Hop.Forward.Hops);
        Assert.False(Roll.Forward.Hops);
        Assert.Equal(0.288f, Hop.Forward.PeakHeight, 4);
    }

    [Theory]
    [InlineData(1.8f)]   // met while rising
    [InlineData(5.5f)]   // met on the ground after landing
    public void The_hop_goes_up_a_0_3_m_step(float stepAt)
    {
        var world = Mesh(Floor(0f, y1: stepAt), Wall(new(-60, stepAt), new(60, stepAt), 0.3f), Floor(0.3f, y0: stepAt));
        var s = Fresh(Heavy, c: HopConfig);
        Step(ref s, InputButtons.Jump, world: world, c: HopConfig);
        for (int k = 2; k <= 30; k++)
        {
            Step(ref s, InputButtons.None, world: world, c: HopConfig);
            Assert.True(s.EvadeTicks == k, $"tick {k}: the hop ended at {s.Position}");
        }
        output.WriteLine($"step at {stepAt}: ended at {s.Position}");
        Assert.Equal(0.3f, s.Position.Z, 2);
        Assert.True(s.Grounded);
        Assert.InRange(s.Position.Y, 5.5f, 6.05f);
    }

    [Fact]
    public void The_hop_off_a_ledge_falls_after_its_curve_and_lands_below()
    {
        var world = Mesh(Floor(2f, y1: 1f), Wall(new(-60, 1), new(60, 1), 2f), Floor(0f, y0: 1f));
        var s = Fresh(Heavy, at: new Vector3(0, 0, 2), c: HopConfig);
        Step(ref s, InputButtons.Jump, world: world, c: HopConfig);
        bool belowStart = false;
        for (int k = 2; k <= 30; k++)
        {
            Step(ref s, InputButtons.None, world: world, c: HopConfig);
            Assert.Equal(k, s.EvadeTicks);
            belowStart |= s.Position.Z < 1.9f && !s.Grounded;
        }
        Assert.True(belowStart);                                // fell past the take-off height with gravity
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Z, 2);
        // sim/combat-2 (item 43): the hop keeps its travel through the fall (protocol 30's stop is gone): the clip's 6 m.
        Assert.Equal(6f, s.Position.Y, 1);
    }

    // ---------------------------------------------------------------- hyper armour

    [Fact]
    public void Hyper_armour_covers_the_start_of_an_evade_up_to_the_longer_property_window()
    {
        Assert.Equal(0.695f, HopConfig.Evades[0].HyperArmourTime, 5);
        Assert.Equal(0.322f, Hop.HyperArmourTime, 5);
        var s = Fresh(c: HopConfig);
        Assert.False(Evade.HyperArmour(s, HopConfig));          // not evading
        Step(ref s, InputButtons.Jump, c: HopConfig);
        var covered = new List<bool> { Evade.HyperArmour(s, HopConfig) };
        for (int n = 2; n <= 30; n++) { Step(ref s, InputButtons.None, c: HopConfig); covered.Add(Evade.HyperArmour(s, HopConfig)); }
        Assert.Equal(20, covered.TakeWhile(c => c).Count());    // 20 ticks = 0.667 s ≤ 0.695 s; the 21st (0.7 s) is past
        Assert.All(covered.Skip(20), Assert.False);
        var h = Fresh(Heavy, c: HopConfig);
        Step(ref h, InputButtons.Jump, c: HopConfig);
        int heavy = 1;
        while (Evade.HyperArmour(h, HopConfig)) { Step(ref h, InputButtons.None, c: HopConfig); heavy++; }
        Assert.Equal(10, heavy);                                // 9 ticks = 0.3 s ≤ 0.322 s, the 10th is out
        Assert.False(Evade.HyperArmour(Fresh(Assault, c: HopConfig) with { EvadeTicks = 1 }, HopConfig));   // a pack class has no evade
    }

    [Theory]
    [InlineData(241)]
    [InlineData(242)]
    public void Prediction_matches_the_server_while_hopping_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var level = Mesh(Floor(0f), Floor(1f, -4, 4, -4, 4), Wall(new(-4, -4), new(4, -4), 1f), Wall(new(4, -4), new(4, 4), 1f),
            Wall(new(4, 4), new(-4, 4), 1f), Wall(new(-4, 4), new(-4, -4), 1f), Floor(0.3f, 10, 20, -60, 60), Wall(new(10, -60), new(10, 60), 0.3f),
            Wall(new(-60, 12), new(60, 12)));
        var map = new GameMap("hop_test", level, [new SpawnPoint(new Vector3(0, 0, 1), 0f), new SpawnPoint(new Vector3(2, 1, 1), 1f), new SpawnPoint(new Vector3(12, 0, 0.3f), 2f)], 79);
        var config = Harmless(HopConfig);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        int[] classes = [Heavy, Heavy, Tactical];
        for (int i = 0; i < classes.Length; i++)
            h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)classes[i], LoadMap = _ => new LoadedMap(level, 79) });
        int airborne = PredictionMatches(h, classes, EvadeInput, 15, config);
        output.WriteLine($"{airborne} ticks evading in the air");
        Assert.True(airborne > 50, "too few hop ticks in the air");
    }

    [Fact]
    public void Death_ends_the_roll_and_a_respawn_clears_the_evade_state()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        Step(ref s, InputButtons.None);
        var dead = Combat.Killed(s, Config);
        Assert.Equal((0, EvadeDirection.Forward, 0), (dead.EvadeTicks, dead.EvadeDirection, dead.EvadeLock));
        Step(ref dead, InputButtons.Jump);
        Assert.Equal(0, dead.EvadeTicks);
        var back = Combat.Respawned(s with { EvadeTicks = 7, EvadeDirection = EvadeDirection.Left, EvadeLock = 9 }, Config);
        Assert.Equal((0, EvadeDirection.Forward, 0), (back.EvadeTicks, back.EvadeDirection, back.EvadeLock));
    }

    // ---------------------------------------------------------------- prediction

    internal static SimConfig Harmless(SimConfig c) => c with
    {
        Weapons = [Pistol with { Damage = 0f, ReserveSize = 5000 }, Rifle with { Damage = 0f, ReserveSize = 5000 }],
        Classes = c.Classes.Select(k => k with { Melee = k.Melee.Select(m => m with { Damage = 0f, SecondaryDamage = 0f }).ToArray() }).ToArray(),
        JumpPacks = [JumpPackTests.Pack with { Pound = null, Landings = [] }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static PlayerInput EvadeInput(int i, uint t)
    {
        var move = Harness.Scripted(i, t);
        var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Sprint);
        uint phase = (t + (uint)i * 17) % 60;
        if (phase is 0 or 1 or 30) buttons |= InputButtons.Jump;                 // a press every 1 s, a second one inside the roll
        if (phase is >= 10 and <= 25 && i % 2 == 0) buttons |= InputButtons.Fire;   // fire held through the ranged branch
        if (phase is 22 or 23 && i == 1 && (t + 17) / 60 % 3 == 0) buttons |= InputButtons.Melee;   // every third roll (an attack lasts 1.5 s)
        if (phase is 25 && i == 1) buttons |= InputButtons.Switch;
        if (phase is 40) buttons |= InputButtons.Reload;
        float mx = phase < 15 ? 0f : move.MoveX;                                     // no stick early in the roll, then the script's
        return move with { Buttons = buttons, MoveX = mx, MoveY = phase < 15 && i == 2 ? -1f : phase < 15 ? 0f : move.MoveY, Pitch = -0.2f };
    }

    /// <returns>Ticks in which some player was evading in the air (off a ledge).</returns>
    int PredictionMatches(Harness h, int[] classes, Func<int, uint, PlayerInput> input, int minEvades, SimConfig? config = null)
    {
        int airborne = 0;
        int n = classes.Length;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        h.Run(TimeSpan.FromSeconds(30), input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < n; i++)
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
            int evades = mine.Zip(mine.Skip(1)).Count(p => p.Second.EvadeTicks == 1);
            int directions = mine.Where(p => p.EvadeTicks > 0).Select(p => p.EvadeDirection).Distinct().Count();
            airborne += mine.Count(p => p.EvadeTicks > 0 && !p.Grounded);
            var corrections = c.CorrectionErrors.Count(e => e > 1e-4f);
            output.WriteLine($"client {i} ({(config ?? Config).Classes[classes[i]].Name}): compared {compared} ticks, {mismatches.Count} mismatches, {corrections} corrections; {evades} evades, {directions} directions");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            if (classes[i] is Tactical or Heavy) { Assert.True(evades >= minEvades, $"client {i}: only {evades} evades"); }
            else Assert.Equal(0, evades);
            Assert.Empty(mismatches);
            Assert.Equal(0, corrections);                       // the client never had to correct its prediction
        }
        return airborne;
    }

    [Theory]
    [InlineData(211)]
    [InlineData(212)]
    public void Prediction_matches_the_server_while_evading_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Harmless(Config);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        int[] classes = [Tactical, Tactical, Tactical, Assault];
        for (int i = 0; i < classes.Length; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)classes[i] });
        PredictionMatches(h, classes, EvadeInput, 15);
    }

    [Theory]
    [InlineData(221)]
    [InlineData(222)]
    public void Prediction_matches_the_server_while_evading_into_walls_and_off_ledges_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // The players spawn on a 1 m high, 8 m wide platform (ledges to roll off, its sides walls to roll into from below); a
        // wall at y = 12 and one at 20° to the y axis.
        var level = Mesh(Floor(0f), Floor(1f, -4, 4, -4, 4), Wall(new(-4, -4), new(4, -4), 1f), Wall(new(4, -4), new(4, 4), 1f),
            Wall(new(4, 4), new(-4, 4), 1f), Wall(new(-4, 4), new(-4, -4), 1f), Wall(new(-60, 12), new(60, 12)), Wall(new(-20, -40), new(-6, 0)));
        var map = new GameMap("evade_test", level, [new SpawnPoint(new Vector3(0, 0, 1), 0f), new SpawnPoint(new Vector3(2, 1, 1), 1f), new SpawnPoint(new Vector3(-2, -1, 1), 2f)], 78);
        var config = Harmless(Config);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        int[] classes = [Tactical, Tactical, Tactical];
        for (int i = 0; i < classes.Length; i++)
            h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)classes[i], LoadMap = _ => new LoadedMap(level, 78) });
        int airborne = PredictionMatches(h, classes, EvadeInput, 15);
        output.WriteLine($"{airborne} ticks evading in the air");
        Assert.True(airborne > 0, "nobody rolled off a ledge");
    }

    [Theory]
    [InlineData(231)]
    [InlineData(232)]
    public void A_rolling_player_killed_mid_roll_respawns_with_no_evade_state_and_prediction_matches_again(int seed)
    {
        // Real damage: two rifle hits kill (the first takes the 100 shield at ×0.5, the second the 100 health). Short respawn.
        var config = Config with
        {
            Weapons = [Pistol, Rifle with { Damage = 200f, ReserveSize = 5000, FallOffStart = 300f, FallOffEnd = 300f, FallOffEndMultiplier = 1f }],
            RespawnTime = 1f,
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var roller = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tactical });
        var shooter = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Plain });
        PlayerInput Input(int i, uint t)
        {
            if (i == 0)
            {
                // A roll every 40 ticks, cycling forward, right, back, left so the roller stays near the shooter.
                uint k = t % 160;
                var stick = (k / 40) switch { 0 => new Vector2(0, 1), 1 => new Vector2(1, 0), 2 => new Vector2(0, -1), _ => new Vector2(-1, 0) };
                bool press = k % 40 == 0;
                return new PlayerInput(t, press ? stick.X : 0f, press ? stick.Y : 0f, 0f, press ? InputButtons.Jump : InputButtons.None);
            }
            // The shooter aims at the roller's server position (the chest), firing in bursts (half a second every 2 s).
            var players = h.Server.World.Players;
            if (!players.TryGetValue(shooter.PlayerId, out var me) || !players.TryGetValue(roller.PlayerId, out var target)) return new PlayerInput(t, 0, 0, 0, InputButtons.None);
            var to = target.Position + new Vector3(0, 0, 1.2f) - (me.Position + new Vector3(0, 0, config.EyeHeight));
            float yaw = MathF.Atan2(-to.X, to.Y), pitch = MathF.Atan2(to.Z, new Vector2(to.X, to.Y).Length());
            return new PlayerInput(t, 0, 0, yaw, t % 60 < 15 ? InputButtons.Fire : InputButtons.None, pitch);
        }
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        h.Run(TimeSpan.FromSeconds(40), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        int id = roller.PlayerId;
        var mine = truth.Where(kv => kv.Value.ContainsKey(id)).Select(kv => (Tick: kv.Key, State: kv.Value[id])).ToList();
        // Deaths mid-roll: the server's state before the death was rolling, and the dead state has no evade.
        var deaths = mine.Zip(mine.Skip(1)).Where(p => p.First.State.Alive && !p.Second.State.Alive).ToList();
        int midRoll = deaths.Count(p => p.First.State.EvadeTicks > 0);
        Assert.All(mine.Where(p => !p.State.Alive), p => Assert.Equal((0, EvadeDirection.Forward, 0), (p.State.EvadeTicks, p.State.EvadeDirection, p.State.EvadeLock)));
        // Once a death snapshot has reached the roller, its own prediction while dead carries no evade either.
        Assert.All(predicted[0].Where(kv => !kv.Value.Alive), kv => Assert.Equal((0, 0), (kv.Value.EvadeTicks, kv.Value.EvadeLock)));
        // After each respawn, once settled (one second for the respawn snapshot to come back), prediction matches the
        // server exactly until the roller is next damaged (the client cannot predict others' hits).
        int compared = 0, rollsCompared = 0;
        var mismatches = new List<string>();
        var respawns = mine.Zip(mine.Skip(1)).Where(p => !p.First.State.Alive && p.Second.State.Alive).Select(p => p.Second.Tick).ToList();
        foreach (uint respawn in respawns)
        {
            foreach (var (tick, real) in mine.Where(p => p.Tick >= respawn + 30))
            {
                if (!real.Alive || real.Health < config.HealthMax || real.Shield < config.ShieldMax) break;   // the next hit
                if (!predicted[0].TryGetValue(tick, out var pred)) continue;
                compared++;
                if (real.EvadeTicks > 0) rollsCompared++;
                if (pred != real) mismatches.Add($"tick {tick} (respawn {respawn}): predicted {pred}; server {real}");
            }
        }
        output.WriteLine($"{deaths.Count} deaths ({midRoll} mid-roll), {respawns.Count} respawns; {compared} ticks compared after respawns ({rollsCompared} rolling), {mismatches.Count} mismatches");
        foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
        Assert.True(midRoll >= 1, $"no death mid-roll ({deaths.Count} deaths)");
        Assert.True(compared > 60 && rollsCompared > 20, $"only {compared} ticks ({rollsCompared} rolling) compared");
        Assert.Empty(mismatches);
        // The shooter is never hit: its prediction matches throughout.
        var shooterMismatches = predicted[1].Count(kv => kv.Key > truth.Keys.First() + 90 && truth.TryGetValue(kv.Key, out var w)
            && w.TryGetValue(shooter.PlayerId, out var real) && real != kv.Value);
        Assert.Equal(0, shooterMismatches);
    }

    // ---------------------------------------------------------------- protocol 13 and the hash

    [Fact]
    public void Protocol_13_carries_the_exact_evade_in_the_own_record_and_direction_and_ticks_in_slim_records_within_1200_bytes()
    {
        Assert.Equal(49, Protocol.Version);
        PlayerState Loaded(int id) => new(id, new Vector3(1e4f), new Vector3(100f), 3f, false)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
            Primary = 254, Pistol = 253, MeleeChoice = 254, MeleeStep = Protocol.MaxMeleeStep, Equipment = 254, Grenades = 255, PendingShot = 255, ProjectilesOut = 255,
            Jump = JumpPhase.Recover, JumpTicks = 65535, JumpMark = 65534, JumpPower = 55.25f, JumpHeld = true, Aiming = true,
            EvadeTicks = id * 16, EvadeDirection = (EvadeDirection)(id % 4), EvadeLock = 255,
        };
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(Loaded).ToArray();
        var projectiles = Enumerable.Range(0, 40).Select(k => new ProjectileView(60000 + k, 254, 255, new Vector3(k), ProjectileFlags.Detonated)).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7, Projectiles: projectiles, Match: new MatchView(MatchState.Playing, Team.None, uint.MaxValue, 65535, 65535),
            Kills: [.. Enumerable.Range(0, Protocol.MaxSnapshotKills).Select(k => new KillView(65535 - k, 65535, 1, KillCause.Thrown, 254))],
            Points: [.. Enumerable.Repeat(new ControlPointView(Team.Chaos, Team.Chaos, 1f, true), Protocol.MaxSnapshotPoints)], Hits: new HitView(1, 2, 3, 4, 5, HitFlags.All, 1f, 1f), Shots: ShotTests.ManyShots(players, Protocol.MaxSnapshotShots)));
        output.WriteLine($"16 players and {Protocol.MaxSnapshotProjectiles} projectiles (protocol {Protocol.Version}): {bytes.Length} bytes");
        Assert.Equal((1194, Protocol.MaxSnapshotBytes, 14), (bytes.Length, bytes.Length, Protocol.MaxSnapshotProjectiles));   // protocol 45 (own record + 8, 14 projectile records); protocol 44 (own record + 14, 15 projectile records); protocol 23 (21, 22: 1198; 20: 1197; 17-19: 1195; 16: 1199; 15: 1195 with 19 projectiles)
        Assert.True(bytes.Length <= 1200 && bytes.Length <= UdpServerTransport.MaxPacket);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);
        Assert.Equal((112, EvadeDirection.Right, 255), (back.Players[6].EvadeTicks, back.Players[6].EvadeDirection, back.Players[6].EvadeLock));   // own: exact
        Assert.Equal((48, EvadeDirection.Right, 0), (back.Players[2].EvadeTicks, back.Players[2].EvadeDirection, back.Players[2].EvadeLock));      // slim: no lock
        Assert.Equal((63, EvadeDirection.Forward), (back.Players[7].EvadeTicks, back.Players[7].EvadeDirection));                                 // slim: ticks capped at 63
        Assert.Equal(14, back.Projectiles!.Length);
        for (int k = 0; k < bytes.Length; k++) Assert.Null(Protocol.Read(bytes.AsSpan(0, k)));
        // Not evading: the direction is canonical (Forward) in slim records.
        Assert.Equal(EvadeDirection.Forward, Protocol.Slim(players[0] with { EvadeTicks = 0, EvadeDirection = EvadeDirection.Left }).EvadeDirection);
    }

    [Fact]
    public void Protocol_13_refuses_a_direction_without_an_evade_and_own_ticks_that_disagree_with_the_slim_byte()
    {
        var me = new PlayerState(3, Vector3.One, Vector3.Zero, 0f, true) { Alive = true, EvadeTicks = 5, EvadeDirection = EvadeDirection.Left, EvadeLock = 20 };
        var own = Protocol.Write(new Snapshot(1, 1, 1, [me], Self: 3));
        Assert.Equal(me, Assert.IsType<Snapshot>(Protocol.Read(own)).Players[0]);
        // Own record: the exact ticks [^47] and the lock [^46] sit before grenades, equipment, cooldown, throw time and three flag bytes (protocol 21: ready ticks in 2 bytes).
        Assert.Equal((5, 20), ((int)own[^47], (int)own[^46]));
        var disagree = (byte[])own.Clone(); disagree[^47] = 6;
        Assert.Null(Protocol.Read(disagree));
        var slim = Protocol.Write(new Snapshot(1, 1, 1, [me with { PlayerId = 4 }]));
        Assert.Equal(5 | (2 << 6), (int)slim[^25]);                  // the slim record's last byte: direction << 6 | ticks
        var orphan = (byte[])slim.Clone(); orphan[^25] = 2 << 6;   // a direction without ticks
        Assert.Null(Protocol.Read(orphan));
        var control = (byte[])slim.Clone(); control[^25] = 63 | (3 << 6);
        Assert.Equal((63, EvadeDirection.Right), (Assert.IsType<Snapshot>(Protocol.Read(control)).Players[0].EvadeTicks, Assert.IsType<Snapshot>(Protocol.Read(control)).Players[0].EvadeDirection));
        // Long evades: the own record keeps the exact count beyond the slim cap.
        var longer = me with { EvadeTicks = 200, EvadeDirection = EvadeDirection.Back };
        Assert.Equal(longer, Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(1, 1, 1, [longer], Self: 3)))).Players[0]);
    }

    [Fact]
    public void The_config_hash_covers_the_evades_and_the_classes_evade()
    {
        ulong h = Config.Hash();
        Assert.Equal(h, (Config with { Evades = [Roll with { Forward = Roll.Forward with { Path = Roll.Forward.Path.ToArray() } }] }).Hash());
        var seen = new HashSet<ulong> { h };
        EvadeSpec[] variants =
        [
            Roll with { Name = "x" }, Roll with { MinInterval = 0.7f }, Roll with { AlignForward = false }, Roll with { WallHitAngle = 30f },
            Roll with { MaxTurnRate = 41f }, Roll with { TargetableModeTime = 0.5f }, Roll with { CollisionModeTime = 0.5f }, Roll with { AnimationSet = "heavy_evade" },
            Roll with { Camera = "c" }, Roll with { Forward = Roll.Forward with { Animation = "F2" } }, Roll with { Back = Roll.Back with { Duration = 0.9f } },
            Roll with { Left = Roll.Left with { MoveAt = 0.5f } }, Roll with { Right = Roll.Right with { RangedAt = 0.5f } }, Roll with { Forward = Roll.Forward with { SwitchAt = 0.8f } },
            Roll with { Forward = Roll.Forward with { MeleeAt = 0.8f } }, Roll with { Forward = Roll.Forward with { NextAt = 0.9f } },
            Roll with { Forward = Roll.Forward with { Path = [.. Roll.Forward.Path.Take(10), new Vector2(0f, 9.5f)] } },
            Roll with { Forward = Roll.Forward with { Height = HopHeights } },
        ];
        foreach (var v in variants) Assert.True(seen.Add((Config with { Evades = [v] }).Hash()), $"{v} left the hash unchanged");
        Assert.True(seen.Add((Config with { Classes = [Config.Classes[0], Config.Classes[1] with { Evade = -1 }, Config.Classes[2]] }).Hash()));
        Assert.True(seen.Add((Config with { Evades = [] }).Hash()));
        Assert.Equal(new SimConfig().Hash(), new SimConfig { Evades = [] }.Hash());
    }

    [Fact]
    public void Direction_and_alignment_helpers_handle_boundaries()
    {
        Assert.Equal(EvadeDirection.Forward, Evade.DirectionOf(float.NaN, 1f));
        Assert.Equal(EvadeDirection.Forward, Evade.DirectionOf(0.0999f, 0f));
        Assert.Equal(EvadeDirection.Right, Evade.DirectionOf(0.1f, 0f));
        Assert.Equal(EvadeDirection.Forward, Evade.DirectionOf(0.5f, 0.5f));
        Assert.Equal(0.25f, Evade.AlignedYaw(0.25f, 0f, 0f));
        float back = Evade.AlignedYaw(0.25f, 0f, -1f);   // half a turn (either way round)
        Assert.Equal(MathF.Cos(0.25f + MathF.PI), MathF.Cos(back), 4);
        Assert.Equal(MathF.Sin(0.25f + MathF.PI), MathF.Sin(back), 4);
        Assert.Equal(24, Evade.LockTicks(Roll, Config));
        Assert.Equal(0, Evade.LockTicks(Roll with { MinInterval = 0f }, Config));
        Assert.Equal(255, Evade.LockTicks(Roll with { MinInterval = 100f }, Config));
        Assert.Equal(new Vector2(0f, 9f), Roll.Forward.Distance);
        Assert.Equal(Vector2.Zero, new EvadeMove("e", [], 1f, 0f, 0f, 0f, 0f, 0f).At(0.5f));
    }
}
