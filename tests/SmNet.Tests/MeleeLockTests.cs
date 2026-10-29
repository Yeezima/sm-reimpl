using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/melee-lock (protocol 32): the melee target lock (selection: cone, range, height, line of sight, ranking; holding through a combo; losing a
/// target that dodges out), the alignment turn, the lunge (strike distance, the separation, the step without a target), the charge's steering,
/// the wire, and prediction against the server over a lossy link. Synthetic attacks only (no install).
/// </summary>
public class MeleeLockTests(ITestOutputHelper output)
{
    static float T(float ticks) => ticks / 30f;
    const float Strike = 1.5f;

    /// <summary>
    /// A synthetic distance variant: before the hit it closes (distance − strike) metres linearly; after it a 0.6 m follow-through step; 61 samples
    /// over the attack (the shape of the knife's clips, simplified).
    /// </summary>
    static MeleeVariant Variant(float distance, float duration, float hit) => new(distance, Enumerable.Range(0, 61).Select(i =>
    {
        float t = duration * i / 60f;
        return MathF.Max(0f, distance - Strike) * Math.Clamp(t / hit, 0f, 1f) + 0.6f * Math.Clamp((t - hit) / (duration - hit), 0f, 1f);
    }).ToArray());

    /// <summary>The MeleeTests step (hit at tick 7, combo window 13..19, moves again at 22, over at 31) with reach: alignment over ticks 0..3, variants 1, 3.5 and 7 m, start blend 1.</summary>
    static MeleeStep Step(float damage = 10f)
    {
        float duration = T(30.5f), hit = T(6.5f);
        return new MeleeStep(duration, hit, T(12.5f), T(18.5f), T(15.5f), T(21.5f), damage, damage / 2f, 3.5f)
        {
            Reach = new MeleeReach(0f, T(3f), 0f, hit, 3.5f, 1f, [Variant(1f, duration, hit), Variant(3.5f, duration, hit), Variant(7f, duration, hit)]),
        };
    }

    static readonly SimConfig Config = new()
    {
        MeleeSteps = [Step(), Step(), Step()],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        ShieldBodyMultiplier = 1f, BodyMultiplier = 1f,
        // feel/melee-charge-2: these tests pin the data's 3-tick window; its widening to MinAlignSpan is MeleeCharge2Tests' (the ease itself stays on here).
        MeleeLock = MeleeLockSpec.Default with { MinAlignSpan = 0f },
    };

    static PlayerState Player(int id, Vector3 at, float yaw = 0f, Team team = Team.None) =>
        Combat.Respawned(new PlayerState(id, at, Vector3.Zero, yaw, true) { Team = team }, Config);

    /// <summary>A point <paramref name="distance"/> m away at <paramref name="degrees"/> left of +Y (yaw positive turns left).</summary>
    static Vector3 At(float distance, float degrees)
    {
        float a = degrees * MathF.PI / 180f;
        return new Vector3(-MathF.Sin(a) * distance, MathF.Cos(a) * distance, 0f);
    }

    static Dictionary<int, PlayerState> View(params PlayerState[] players) => players.ToDictionary(p => p.PlayerId);

    static PlayerInput Press(uint tick = 0, float yaw = 0f, int target = 0) => new(tick, 0, 0, yaw, InputButtons.Melee, MeleeTarget: target);

    // ---------------------------------------------------------------- selection

    [Theory]
    [InlineData(3f, 40f, true)]
    [InlineData(3f, 50f, true)]     // 50° from the attacker is 38° from the apex 1 m behind it: inside the 45° cone
    [InlineData(3f, 65f, false)]    // 50° from the apex: outside
    [InlineData(6.9f, 0f, true)]
    [InlineData(7.1f, 0f, false)]   // beyond max_melee_target_distance
    [InlineData(1.2f, 80f, true)]   // nearer than the fan's start: the whole front half
    [InlineData(1.2f, 100f, false)] // … but not behind
    public void Lock_takes_enemies_in_the_cone_and_range(float distance, float degrees, bool locked)
    {
        var self = Player(1, Vector3.Zero);
        var view = View(Player(2, At(distance, degrees)));
        Assert.Equal(locked ? 2 : 0, MeleeLock.Choose(self, Press(), view, Config, null, 0));
    }

    [Fact]
    public void Lock_respects_the_vertical_reach_allies_and_the_dead()
    {
        var self = Player(1, Vector3.Zero, team: Team.SpaceMarines);
        Assert.Equal(0, MeleeLock.Choose(self, Press(), View(Player(2, At(3f, 0f) + new Vector3(0, 0, 1.6f), team: Team.Chaos)), Config, null, 0));
        Assert.Equal(2, MeleeLock.Choose(self, Press(), View(Player(2, At(3f, 0f) + new Vector3(0, 0, 1.4f), team: Team.Chaos)), Config, null, 0));
        Assert.Equal(0, MeleeLock.Choose(self, Press(), View(Player(2, At(3f, 0f), team: Team.SpaceMarines)), Config, null, 0));
        Assert.Equal(0, MeleeLock.Choose(self, Press(), View(Player(2, At(3f, 0f), team: Team.Chaos) with { Alive = false }), Config, null, 0));
    }

    [Fact]
    public void Lock_needs_a_line_of_sight()
    {
        var self = Player(1, Vector3.Zero);
        var view = View(Player(2, At(3f, 0f)));
        Assert.Equal(2, MeleeLock.Choose(self, Press(), view, Config, Level(wall: false), 0));
        Assert.Equal(0, MeleeLock.Choose(self, Press(), view, Config, Level(wall: true), 0));
    }

    [Fact]
    public void Lock_ranks_by_distance_band_then_angle_band()
    {
        var self = Player(1, Vector3.Zero);
        // Same 1 m distance band (3.x m): the straighter one wins although it is farther.
        Assert.Equal(3, MeleeLock.Choose(self, Press(), View(Player(2, At(3.1f, 30f)), Player(3, At(3.6f, 0f))), Config, null, 0));
        // Different bands: the nearer wins although it is off to the side.
        Assert.Equal(2, MeleeLock.Choose(self, Press(), View(Player(2, At(2.2f, 35f)), Player(3, At(5f, 0f))), Config, null, 0));
    }

    [Fact]
    public void Lock_searches_along_the_stick_when_it_is_pushed()
    {
        var self = Player(1, Vector3.Zero);
        var view = View(Player(2, At(3f, 90f)));   // to the left
        Assert.Equal(0, MeleeLock.Choose(self, Press(), view, Config, null, 0));
        Assert.Equal(2, MeleeLock.Choose(self, Press() with { MoveX = -1f }, view, Config, null, 0));   // stick left
    }

    [Fact]
    public void Only_attack_ticks_lock()
    {
        var self = Player(1, Vector3.Zero);
        var view = View(Player(2, At(3f, 0f)));
        Assert.Equal(0, MeleeLock.Choose(self, new PlayerInput(0, 0, 0, 0, InputButtons.None), view, Config, null, 0));
        Assert.Equal(0, MeleeLock.Choose(self with { MeleeHeld = true }, Press(), view, Config, null, 0));   // held, not a press
        Assert.Equal(2, MeleeLock.Choose(self with { MeleeStep = 1 }, new PlayerInput(0, 0, 0, 0, InputButtons.None), view, Config, null, 0));
        Assert.Equal(0, MeleeLock.Choose(self, Press(), null, Config, null, 0));   // no view yet
    }

    [Fact]
    public void A_combo_keeps_its_target_and_a_target_that_dodges_out_is_lost()
    {
        var self = Player(1, Vector3.Zero) with { MeleeStep = 2 };
        // A nearer enemy steps in: the lock stays on 2 while 2 is still valid.
        Assert.Equal(2, MeleeLock.Choose(self, Press(), View(Player(2, At(3f, 0f)), Player(3, At(1.5f, 5f))), Config, null, 2));
        // 2 rolls out beyond the range plus the leash (8 m): the next search takes 3.
        Assert.Equal(3, MeleeLock.Choose(self, Press(), View(Player(2, At(8.2f, 0f)), Player(3, At(1.5f, 5f))), Config, null, 2));
        // Within the leash (7.8 m) the lock holds although a fresh search would not take it.
        Assert.Equal(2, MeleeLock.Choose(self, Press(), View(Player(2, At(7.8f, 0f))), Config, null, 2));
        // Behind the attacker after a roll: outside both the attack's cone (its heading) and the search cone (the view): lost, nobody else to take.
        Assert.Equal(0, MeleeLock.Choose(self, Press(), View(Player(2, At(2f, 150f))), Config, null, 2));
        // Under way, the stick toward a side keeps a target there (the search cone along the stick) although the heading looks away.
        Assert.Equal(2, MeleeLock.Choose(self, Press() with { MoveX = -1f }, View(Player(2, At(3f, 90f))), Config, null, 2));
    }

    [Fact]
    public void The_server_accepts_only_locks_its_phase_allows()
    {
        var self = Player(1, Vector3.Zero);
        // feel/melee-charge-2: 2 stands 80° off (63° from the apex): outside even the held cone (45° plus the 15° hysteresis).
        var view = View(Player(2, At(3f, 80f)), Player(3, At(7.5f, 0f)));
        var press = Press(target: 2);
        // An attack's start: the search cone along the view (2 is 80° off) and 7 m (3 is 7.5 m away) refuse both.
        Assert.Null(MeleeLock.Target(self, press, 2, view, Config, null, MeleeLock.Phase.MeleeStart));
        Assert.Null(MeleeLock.Target(self, press, 3, view, Config, null, MeleeLock.Phase.MeleeStart));
        // Under way: the leash takes 3 (7.5 ≤ 8) in the heading's cone; 2 stays outside every cone.
        Assert.NotNull(MeleeLock.Target(self with { MeleeStep = 1 }, press, 3, view, Config, null, MeleeLock.Phase.Melee));
        Assert.Null(MeleeLock.Target(self with { MeleeStep = 1 }, press, 2, view, Config, null, MeleeLock.Phase.Melee));
        // A modified client naming 2 anyway gets no turn and no lunge: the attack faces the view and stands (the 1 m clip).
        var s = self;
        for (uint i = 0; i < 6; i++) s = Combat.StepPlayer(s, new PlayerInput(i, 0, 0, 0, i == 0 ? InputButtons.Melee : InputButtons.None, MeleeTarget: 2), Config, out _, null, view);
        Assert.Equal(0f, s.Yaw, 4);
        Assert.Equal(0f, s.Position.Y, 3);
        // The charge: its start takes the view's cone and 7 m, under way its heading's cone and the leash.
        Assert.Null(MeleeLock.Target(self, press, 2, view, Config, null, MeleeLock.Phase.RushStart));
        Assert.NotNull(MeleeLock.Target(self, press, 3, view, Config, null, MeleeLock.Phase.Rush));
        Assert.Null(MeleeLock.Target(self, press, 3, view, Config, null, MeleeLock.Phase.RushStart));
    }

    [Fact]
    public void An_attack_started_toward_the_stick_faces_it_and_keeps_its_lock()
    {
        // The target stands to the left; the player pushes the stick left and attacks without turning the view.
        var target = Player(2, At(3f, 90f));
        var self = Player(1, Vector3.Zero);
        var view = View(target);
        var states = new List<PlayerState>();
        int locked = 0;
        for (uint i = 0; i < 8; i++)
        {
            var input = new PlayerInput(i, i == 0 ? -1f : 0f, 0, 0, i == 0 ? InputButtons.Melee : InputButtons.None);   // the stick is let go after the press
            input = input with { MeleeTarget = MeleeLock.Choose(self, input, view, Config, null, locked) };
            locked = input.MeleeTarget;
            self = Combat.StepPlayer(self, input, Config, out _, null, view);
            states.Add(self);
        }
        Assert.Equal(2, locked);
        Assert.Equal(MathF.PI / 2f, states[0].Yaw, 3);   // the press faced the stick's direction
        Assert.True(states[7].Position.X < -0.5f, $"lunged to the left: {states[7].Position}");
    }

    // ---------------------------------------------------------------- alignment and lunge

    /// <summary>Steps player 1 from a press over <paramref name="ticks"/> ticks with the target's view fixed (or moved by <paramref name="move"/>), returning the states.</summary>
    static List<PlayerState> Attack(PlayerState self, PlayerState target, int ticks, float viewYaw = 0f, Func<int, PlayerState, PlayerState>? move = null, bool lockOn = true,
        SimConfig? config = null)
    {
        var c = config ?? Config;
        var states = new List<PlayerState>();
        for (int i = 0; i < ticks; i++)
        {
            target = move?.Invoke(i, target) ?? target;
            var view = View(target);
            var input = new PlayerInput((uint)i, 0, 0, viewYaw, i == 0 ? InputButtons.Melee : InputButtons.None);
            input = input with { MeleeTarget = lockOn ? MeleeLock.Choose(self, input, view, c, null, states.Count > 0 ? 2 : 0) : 0 };
            self = Combat.StepPlayer(self, input, c, out _, null, view);
            states.Add(self);
        }
        return states;
    }

    [Fact]
    public void Alignment_turns_to_the_target_inside_its_window_and_holds_after()
    {
        var target = Player(2, At(3f, 40f));
        var states = Attack(Player(1, Vector3.Zero), target, 12);
        float wanted = MeleeLock.YawOf(MeleeLock.Offset(states[3], target));
        // Ticks 1..3 cover the window (0, 0.1]: by its end the heading faces the target.
        Assert.True(MathF.Abs(MeleeLock.Delta(states[3].Yaw, wanted)) < 0.02f, $"heading {states[3].Yaw}, wanted {wanted}");
        Assert.True(states[1].Yaw > 0.05f && states[1].Yaw < states[2].Yaw && states[2].Yaw < states[3].Yaw);
        // After the window the view's yaw (0) does not turn the attack back.
        Assert.All(states.Skip(4).Take(5), s => Assert.Equal(states[3].Yaw, s.Yaw, 4));
    }

    [Fact]
    public void Alignment_turn_is_capped_by_the_max_rate()
    {
        var c = Config with { MeleeLock = Config.MeleeLock with { MaxAlignRate = 300f } };   // 10° a tick
        var states = Attack(Player(1, Vector3.Zero), Player(2, At(3f, 40f)), 5, config: c);
        float perTick = 300f / 30f * MathF.PI / 180f;
        Assert.Equal(perTick, states[1].Yaw, 3);
        Assert.Equal(3 * perTick, states[3].Yaw, 3);   // 30° by the window's end: not all the way to 40°
    }

    [Fact]
    public void Without_a_target_the_view_turns_the_attack_only_in_its_window()
    {
        var states = Attack(Player(1, Vector3.Zero), Player(2, At(30f, 0f)), 8, viewYaw: 0.5f, lockOn: false);
        Assert.Equal(0.5f, states[0].Yaw, 4);     // the press tick: the view's yaw (the attack starts facing it)
        Assert.Equal(0.5f, states[2].Yaw, 4);     // inside the window: the view
        var held = Attack(Player(1, Vector3.Zero), Player(2, At(30f, 0f)), 8, viewYaw: 0f, lockOn: false);
        var late = new List<PlayerState>();
        var s = held[4];
        for (int i = 0; i < 3; i++) { s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 1.0f, InputButtons.None), Config, out _, null, null); late.Add(s); }
        Assert.All(late, x => Assert.Equal(0f, x.Yaw, 4));   // after the window the view does not turn it
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(3f)]
    [InlineData(6.5f)]
    public void Lunge_ends_at_the_strike_distance_at_the_hit(float distance)
    {
        var target = Player(2, At(distance, 0f));
        var states = Attack(Player(1, Vector3.Zero), target, 9);
        float atHit = Vector2.Distance(new(states[7].Position.X, states[7].Position.Y), new(target.Position.X, target.Position.Y));
        // The clips blended at the target's distance end where they were made to strike from (1.5 m for the 3.5 and 7 m clips; nearer between 1 and 3.5).
        float expected = distance - Step().Reach!.ForwardAt(T(7f), T(30.5f), distance);
        output.WriteLine($"{distance} m: at the hit {atHit:F3} m (the blend: {expected:F3}); moved {states[7].Position.Y:F3}");
        Assert.InRange(atHit, expected - 0.03f, expected + 0.03f);
        if (distance >= 3.5f) Assert.InRange(atHit, Strike - 0.03f, Strike + 0.03f);
    }

    [Fact]
    public void Lunge_never_closes_nearer_than_the_separation()
    {
        var target = Player(2, At(1.2f, 0f));
        var states = Attack(Player(1, Vector3.Zero), target, 31);
        Assert.All(states, s => Assert.True(Vector2.Distance(new(s.Position.X, s.Position.Y), new(target.Position.X, target.Position.Y)) >= Config.MeleeLock.Separation - 1e-3f
            || s.Position.Y <= 0.2f + 1e-3f));
        float min = states.Min(s => Vector2.Distance(new(s.Position.X, s.Position.Y), new(target.Position.X, target.Position.Y)));
        Assert.True(min >= Config.MeleeLock.Separation - 1e-3f, $"closest {min}");
    }

    [Fact]
    public void Without_a_target_every_swing_steps_forward_by_the_start_blend()
    {
        var states = Attack(Player(1, Vector3.Zero), Player(2, At(30f, 0f)), 22, lockOn: false);
        // The 1 m variant: no lunge before the hit, the 0.6 m follow-through after it (until MoveAt, tick 22).
        Assert.Equal(0f, states[6].Position.Y, 3);
        float expected = 0.6f * (T(20f) - T(6.5f)) / (T(30.5f) - T(6.5f));
        Assert.Equal(expected, states[20].Position.Y, 2);
        Assert.True(states[20].Position.Y > 0.3f);
    }

    [Fact]
    public void A_target_that_dodges_sideways_after_the_window_is_not_followed()
    {
        var target = Player(2, At(5f, 0f));
        // From tick 4 (after the alignment window) the target rolls 3 m to the side.
        var states = Attack(Player(1, Vector3.Zero), target, 9, move: (i, t) => i == 4 ? t with { Position = t.Position + new Vector3(3f, 0f, 0f) } : t);
        Assert.Equal(0f, states[8].Yaw, 4);           // the heading stayed
        Assert.True(MathF.Abs(states[8].Position.X) < 1e-3f);
    }

    [Fact]
    public void Fire_before_RangedAt_does_not_stop_the_lunge_and_after_it_cancels_it()
    {
        var target = Player(2, At(6f, 0f));
        var states = Attack(Player(1, Vector3.Zero), target, 3);
        var s = states[^1];
        float y = s.Position.Y;
        Assert.True(y > 0.1f);   // lunging
        // Fire is ignored before RangedAt (tick 16): the lunge goes on.
        var next = Combat.StepPlayer(s, new PlayerInput(3, 0, 0, 0, InputButtons.Fire, MeleeTarget: 2), Config, out _, null, View(target));
        Assert.True(next.Position.Y > y);
        Assert.Equal(1, next.MeleeStep);
        // From RangedAt on, Fire ends the attack and its root motion: the body only slows down (the walk's deceleration), no clip motion.
        var late = Attack(Player(1, Vector3.Zero), Player(2, At(30f, 0f)), 17, lockOn: false)[^1];   // tick 17: past RangedAt, the follow-through running
        Assert.Equal(1, late.MeleeStep);
        var cancelled = Combat.StepPlayer(late, new PlayerInput(17, 0, 0, 0, InputButtons.Fire), Config, out _, null, null);
        Assert.Equal(0, cancelled.MeleeStep);
        float rootStep = Step().Reach!.ForwardAt(T(18f), T(30.5f), 1f) - Step().Reach!.ForwardAt(T(17f), T(30.5f), 1f);
        Assert.True(cancelled.Position.Y - late.Position.Y < rootStep - 1e-4f || rootStep < 1e-4f, "the cancelled attack still moved by its clip");
    }

    [Fact]
    public void A_reaction_stops_the_lunge()
    {
        var target = Player(2, At(6f, 0f));
        var s = Attack(Player(1, Vector3.Zero), target, 3)[^1];
        // A stun lands (the world's TryReact puts the player in it; the reaction clears the attack).
        var stunned = Reaction.Start(s, Config, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.Zero);
        Assert.Equal(0, stunned.MeleeStep);
        var next = Combat.StepPlayer(stunned, new PlayerInput(3, 0, 0, 0, InputButtons.None, MeleeTarget: 2), Config, out _, null, View(target));
        Assert.True(MathF.Abs(next.Position.Y - stunned.Position.Y) < 1e-3f, $"moved {next.Position.Y - stunned.Position.Y} m while stunned");
    }

    [Fact]
    public void Jump_during_an_attack_starts_no_evade_and_the_lunge_goes_on()
    {
        // A class that rolls (the evade tests' roll) with this file's attacks.
        var c = Config with { Weapons = EvadeTests.Config.Weapons, Evades = EvadeTests.Config.Evades, Classes = [new ClassLoadout("tactical", 1, 0, Config.MeleeSteps) { Evade = 0 }] };
        var target = Player(2, At(6f, 0f));
        var rest = Player(1, Vector3.Zero);
        Assert.True(Combat.StepPlayer(rest, new PlayerInput(0, 0, 0, 0, InputButtons.Jump), c, out _, null, null).EvadeTicks > 0, "the class rolls from rest");
        var s = Attack(rest, target, 3, config: c)[^1];
        var next = Combat.StepPlayer(s, new PlayerInput(3, 0, 0, 0, InputButtons.Jump, MeleeTarget: 2), c, out _, null, View(target));
        Assert.Equal(0, next.EvadeTicks);
        Assert.True(next.Position.Y > s.Position.Y);
    }

    // ---------------------------------------------------------------- the world

    static CollisionWorld Level(bool wall)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); }
        Quad(new(-20, -20, 0), new(20, -20, 0), new(20, 20, 0), new(-20, 20, 0));
        if (wall) Quad(new(-5, 1, -1), new(5, 1, -1), new(5, 1, 4), new(-5, 1, 4));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    [Fact]
    public void World_lock_lunges_and_hits_a_target_out_of_plain_reach()
    {
        // 5.5 m away and 35° to the side: without the lock the swing is out of range (4 m) and off the view.
        var spot = At(5.5f, 35f);
        foreach (bool lockOn in new[] { false, true })
        {
            var world = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(spot, 0f)]);
            world.Spawn(1); world.Spawn(2);
            var hits = new List<HitEvent>();
            for (uint i = 0; i < 12; i++)
            {
                world.Step(id => id == 1 ? new PlayerInput(i, 0, 0, 0f, i == 0 ? InputButtons.Melee : InputButtons.None, 0, world.Tick, MeleeTarget: lockOn ? 2 : 0)
                    : new PlayerInput(i, 0, 0, 0, InputButtons.None));
                hits.AddRange(world.LastHits);
            }
            output.WriteLine($"lock {lockOn}: {hits.Count} hits; attacker at {world.Players[1].Position}, yaw {world.Players[1].Yaw:F3}");
            Assert.Equal(lockOn ? 1 : 0, hits.Count);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void World_refuses_a_lock_through_a_wall(bool wall)
    {
        // The target stands 5 m ahead; a wall at y = 1 hides it. A client naming it anyway gets no lunge (the 1 m variant does not move before the hit).
        var world = new World(Config, Level(wall), [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(At(5f, 0f), 0f)]);
        world.Spawn(1); world.Spawn(2);
        float before = world.Players[1].Position.Y;
        for (uint i = 0; i < 8; i++)
            world.Step(id => id == 1 ? new PlayerInput(i, 0, 0, 0f, i == 0 ? InputButtons.Melee : InputButtons.None, 0, world.Tick, MeleeTarget: 2) : new PlayerInput(i, 0, 0, 0, InputButtons.None));
        float moved = world.Players[1].Position.Y - before;
        output.WriteLine($"wall {wall}: moved {moved:F3}");
        if (wall) Assert.True(moved < 0.05f, $"moved {moved} although the target is behind a wall");
        else Assert.InRange(moved, 5f - Strike - 0.1f, 5f - Strike + 0.1f);
    }

    [Fact]
    public void World_refuses_a_lock_on_an_ally_or_beyond_the_leash()
    {
        foreach (var (team, distance, lunges) in new[] { (Team.Chaos, 5f, true), (Team.SpaceMarines, 5f, false), (Team.Chaos, 8.5f, false) })
        {
            var world = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(At(distance, 0f), 0f)]) { Rules = new MatchRules { TimeLimit = 0f } };
            world.Spawn(1, team: Team.SpaceMarines); world.Spawn(2, team: team);
            float before = world.Players[1].Position.Y;
            for (uint i = 0; i < 8; i++)
                world.Step(id => id == 1 ? new PlayerInput(i, 0, 0, 0f, i == 0 ? InputButtons.Melee : InputButtons.None, 0, world.Tick, MeleeTarget: 2) : new PlayerInput(i, 0, 0, 0, InputButtons.None));
            float moved = world.Players[1].Position.Y - before;
            output.WriteLine($"target {team} at {distance} m: moved {moved:F3}");
            Assert.Equal(lunges, moved > 1f);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_lock_through_the_enemy_spawn_blocker_is_refused(bool blocker)
    {
        // A Space Marine attacks a Chaos player standing in its spawn room: the Chaos blocker (solid for Space Marines) stands between them.
        var v = new List<Vector3>(); var idx = new List<int>();
        v.AddRange([new(-20, -20, 0), new(20, -20, 0), new(20, 20, 0), new(-20, 20, 0)]); idx.AddRange([0, 1, 2, 0, 2, 3]);
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var level = new CollisionWorld(mesh, mesh, blockers: blocker ? [new SpawnBlocker(Team.Chaos, new Vector3(0, 3f, 1f), new Vector3(6f, 0.5f, 3f), 0f)] : null);
        var self = Player(1, Vector3.Zero, team: Team.SpaceMarines);
        var target = Player(2, At(5f, 0f), team: Team.Chaos);
        Assert.Equal(blocker ? 0 : 2, MeleeLock.Choose(self, Press(), View(target), Config, level, 0));
        var s = self;
        for (uint i = 0; i < 8; i++) s = Combat.StepPlayer(s, new PlayerInput(i, 0, 0, 0, i == 0 ? InputButtons.Melee : InputButtons.None, MeleeTarget: 2), Config, out _, level, View(target));
        output.WriteLine($"blocker {blocker}: at {s.Position}");
        if (blocker) Assert.True(s.Position.Y < 0.1f, "lunged toward a target behind the blocker");
        else Assert.True(s.Position.Y > 3f);
    }

    [Fact]
    public void A_lunge_off_a_ledge_drops_and_lands()
    {
        // A 1 m platform for y < 2 (its face a wall), the floor below; the target stands on the floor 4 m ahead, 1 m lower.
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); }
        Quad(new(-20, -20, 0), new(20, -20, 0), new(20, 20, 0), new(-20, 20, 0));
        Quad(new(-20, -20, 1), new(20, -20, 1), new(20, 2, 1), new(-20, 2, 1));
        Quad(new(-20, 2, 0), new(20, 2, 0), new(20, 2, 1), new(-20, 2, 1));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var level = new CollisionWorld(mesh, mesh);
        var self = Player(1, new Vector3(0, 0, 1));
        var target = Player(2, new Vector3(0, 4.5f, 0));
        var s = self;
        for (uint i = 0; i < 40; i++)
        {
            var input = new PlayerInput(i, 0, 0, 0, i == 0 ? InputButtons.Melee : InputButtons.None);
            input = input with { MeleeTarget = MeleeLock.Choose(s, input, View(target), Config, level, 2) };
            s = Combat.StepPlayer(s, input, Config, out _, level, View(target));
            Assert.True(float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y) && float.IsFinite(s.Position.Z));
        }
        output.WriteLine($"after the lunge off the ledge: {s.Position}, grounded {s.Grounded}");
        Assert.True(s.Position.Y > 2f && s.Position.Y < target.Position.Y && s.Position.Z < 0.05f && s.Grounded, $"{s.Position}");   // off the ledge, short of the target
        Assert.True(Vector2.Distance(new(s.Position.X, s.Position.Y), new(target.Position.X, target.Position.Y)) >= Config.MeleeLock.Separation - 0.05f);
    }

    [Fact]
    public void Charge_steers_to_its_locked_target_within_the_cone()
    {
        var rush = new RushSpec("ability_bullrush_01",
            new RushMove("rush", Enumerable.Range(0, 21).Select(i => new Vector2(0f, i * 0.5f)).ToArray(), 0.667f, 0.667f, 0.667f, 0.667f, 0.667f),
            new RushMove("stop", [Vector2.Zero, Vector2.Zero], 0.3f, 0.3f, 0.3f, 0.3f, 0.3f),
            new DamageSpec(75f, 0f, 1f, 1f, 1f, 1f, false), default, new DamageSpec(30f, 0f, 1f, 1f, 1f, 1f, false), default, 4f, 1f);
        var c = Config with { Classes = [new ClassLoadout("c", 0, -1, Config.MeleeSteps) { MeleeRush = rush }] };
        var start = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c) with { Rush = RushPhase.Rush, RushTicks = 1 };
        foreach (var (degrees, steers) in new[] { (30f, true), (60f, false) })
        {
            var target = Combat.Respawned(new PlayerState(2, At(6f, degrees), Vector3.Zero, 0f, true), c);
            var s = start;
            for (uint i = 0; i < 4; i++) s = Combat.StepPlayer(s, new PlayerInput(i, 0, 0, 0, InputButtons.None, MeleeTarget: 2), c, out _, null, View(target));
            output.WriteLine($"{degrees}°: yaw {s.Yaw * 180f / MathF.PI:F1}°, at {s.Position}");
            if (steers) Assert.InRange(s.Yaw * 180f / MathF.PI, 17f, 30.5f);   // 6° a tick at 180°/s
            else Assert.Equal(0f, s.Yaw, 4);
        }
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void Input_carries_the_melee_target()
    {
        var input = new PlayerInput(7, 0.5f, 1f, 0.25f, InputButtons.Melee, 0.1f, 5, MeleeTarget: 4321);
        var bytes = Protocol.Write(new InputBatch([input], 9, 3));
        Assert.Equal(1 + 4 + 4 + 1 + 40, bytes.Length);   // protocol 37: + the view fraction; 43: + the body lag
        Assert.Equal(40, Protocol.InputBytes);
        var read = Assert.IsType<InputBatch>(Protocol.Read(bytes));
        Assert.Equal(4321, read.Inputs[0].MeleeTarget);
        Assert.Equal(0, new PlayerInput(0, 0, 0, 0, InputButtons.None, MeleeTarget: -3).Sanitized().MeleeTarget);
        Assert.Equal(0, new PlayerInput(0, 0, 0, 0, InputButtons.None, MeleeTarget: 70000).Sanitized().MeleeTarget);
        Assert.Equal(1200, Protocol.MaxSnapshotBytes);   // snapshots unchanged
    }

    [Fact]
    public void Config_hash_covers_the_reach_and_the_lock_spec_but_not_when_absent()
    {
        var plain = new SimConfig();
        Assert.Equal(plain.Hash(), (plain with { MeleeLock = MeleeLockSpec.Default with { } }).Hash());
        Assert.NotEqual(plain.Hash(), (plain with { MeleeLock = MeleeLockSpec.Default with { Leash = 2f } }).Hash());
        var withReach = Config.Hash();
        var other = Config with { MeleeSteps = [Step() with { Reach = Step().Reach! with { AlignTo = T(4f) } }, Step(), Step()] };
        Assert.NotEqual(withReach, other.Hash());
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(51)]
    [InlineData(52)]
    public void Prediction_is_exact_with_locks_on_a_level_with_walls_and_a_crate_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // A 20 m room, a crate (1 x 1 x 1.2 m) and a 3 m wall with a gap between the two players: locks come and go with the line of sight, lunges
        // stop at the crate and the walls. Every lock decision is taken in the snapshot the client holds at its view tick: the prediction is exact.
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int i = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]); }
        void Wall(Vector2 a, Vector2 b, float h) => Quad(new(a.X, a.Y, -1f), new(b.X, b.Y, -1f), new(b.X, b.Y, h), new(a.X, a.Y, h));
        Quad(new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0));
        Wall(new(-10, -10), new(10, -10), 4); Wall(new(10, -10), new(10, 10), 4); Wall(new(10, 10), new(-10, 10), 4); Wall(new(-10, 10), new(-10, -10), 4);
        Wall(new(-6, 3), new(-1, 3), 3); Wall(new(1, 3), new(6, 3), 3);   // a wall with a 2 m gap
        Wall(new(2, -2), new(3, -2), 1.2f); Wall(new(3, -2), new(3, -1), 1.2f); Wall(new(3, -1), new(2, -1), 1.2f); Wall(new(2, -1), new(2, -2), 1.2f);
        Quad(new(2, -2, 1.2f), new(3, -2, 1.2f), new(3, -1, 1.2f), new(2, -1, 1.2f));   // the crate's top
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        var level = new CollisionWorld(mesh, mesh);
        var map = new GameMap("lock_test", level, [new SpawnPoint(new Vector3(-2, 0, 0), 0f), new SpawnPoint(new Vector3(2, 6, 0), MathF.PI)], 91);
        var config = Config with
        {
            MeleeSteps = [Step(0f), Step(0f), Step(0f)],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(500 + i), options: new ClientOptions { LoadMap = _ => new LoadedMap(level, 91) });
        var (truth, predicted, locks) = RunMelee(h, 25);
        for (int i = 0; i < 2; i++)
        {
            var (compared, mismatches, attacks, lunged) = Compare(h, i, truth, predicted[i]);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {attacks} attacks, {lunged:F1} m moved while attacking, lock ticks {locks[i]}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300 && attacks > 5 && locks[i] > 30, $"client {i}: too little melee ({compared}, {attacks}, {locks[i]})");
            Assert.Empty(mismatches);
        }
    }

    /// <summary>Two players chase each other and melee in bursts for <paramref name="seconds"/> s; the server's states per tick, each client's predictions, lock ticks.</summary>
    static (SortedDictionary<uint, Dictionary<int, PlayerState>> Truth, Dictionary<uint, PlayerState>[] Predicted, int[] Locks) RunMelee(Harness h, int seconds)
    {
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        var locks = new int[2];
        PlayerInput Input(int i, uint t)
        {
            var c = h.Clients[i];
            var me = c.Predicted;
            var other = c.RemotePlayers().Players.FirstOrDefault();
            var to = new Vector2(other.Position.X - me.Position.X, other.Position.Y - me.Position.Y);
            float yaw = to.LengthSquared() > 1e-4f ? MeleeLock.YawOf(to) + 0.3f * MathF.Sin(t * 0.07f + i) : 0f;
            float distance = to.Length();
            float moveY = distance > 6f ? 1f : distance < 2f ? -1f : 0f;
            float moveX = MathF.Sin(t * 0.05f + i * 2f) * 0.8f;
            uint phase = (t + (uint)i * 5) % 100;
            var buttons = phase < 70 && phase % 12 < 2 ? InputButtons.Melee : InputButtons.None;
            return new PlayerInput(t, moveX, moveY, yaw, buttons, 0.1f);
        }
        h.Run(TimeSpan.FromSeconds(seconds), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected)
                {
                    predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
                    if (h.Clients[i].MeleeLockTarget != 0) locks[i]++;
                }
        });
        return (truth, predicted, locks);
    }

    static (int Compared, List<string> Mismatches, int Attacks, float Lunged) Compare(Harness h, int i, SortedDictionary<uint, Dictionary<int, PlayerState>> truth, Dictionary<uint, PlayerState> predicted)
    {
        int id = h.Clients[i].PlayerId, compared = 0;
        var mismatches = new List<string>();
        uint from = truth.Keys.First() + 90;
        foreach (var (tick, pred) in predicted)
        {
            if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
            compared++;
            if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
        }
        var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
        int attacks = mine.Zip(mine.Skip(1)).Count(p => p.Second.MeleeStep == 1 && p.First.MeleeStep != 1);
        float lunged = mine.Zip(mine.Skip(1)).Where(p => p.First.MeleeStep > 0 && p.Second.MeleeStep > 0)
            .Sum(p => Vector2.Distance(new(p.First.Position.X, p.First.Position.Y), new(p.Second.Position.X, p.Second.Position.Y)));
        return (compared, mismatches, attacks, lunged);
    }

    [Theory]
    [InlineData(41)]
    [InlineData(43)]
    public void Prediction_follows_the_server_while_two_players_lock_and_lunge_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // No damage, so neither dies (damage is not predicted); the lunge, the alignment and the lock are.
        var config = Config with
        {
            MeleeSteps = [Step(0f), Step(0f), Step(0f)],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 2, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        var locks = new int[2];
        PlayerInput Input(int i, uint t)
        {
            var c = h.Clients[i];
            var me = c.Predicted;
            var other = c.RemotePlayers().Players.FirstOrDefault();
            var to = new Vector2(other.Position.X - me.Position.X, other.Position.Y - me.Position.Y);
            float yaw = to.LengthSquared() > 1e-4f ? MeleeLock.YawOf(to) + 0.3f * MathF.Sin(t * 0.07f + i) : 0f;   // roughly at the other, wandering ±17°
            float distance = to.Length();
            // Close in while farther than 6 m, back off when nearer than 2 m; a melee press every 12 ticks in bursts.
            float moveY = distance > 6f ? 1f : distance < 2f ? -1f : 0f;
            float moveX = MathF.Sin(t * 0.05f + i * 2f) * 0.8f;
            uint phase = (t + (uint)i * 5) % 100;
            var buttons = phase < 70 && phase % 12 < 2 ? InputButtons.Melee : InputButtons.None;
            return new PlayerInput(t, moveX, moveY, yaw, buttons, 0.1f);
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected)
                {
                    predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
                    if (h.Clients[i].MeleeLockTarget != 0) locks[i]++;
                }
        });
        for (int i = 0; i < 2; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0, exact = 0;
            var errors = new List<float>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred == real) exact++;
                errors.Add(Vector3.Distance(pred.Position, real.Position));
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int attacks = mine.Zip(mine.Skip(1)).Count(p => p.Second.MeleeStep == 1 && p.First.MeleeStep != 1);
            float lunged = mine.Zip(mine.Skip(1)).Where(p => p.First.MeleeStep > 0 && p.Second.MeleeStep > 0)
                .Sum(p => Vector2.Distance(new(p.First.Position.X, p.First.Position.Y), new(p.Second.Position.X, p.Second.Position.Y)));
            output.WriteLine($"client {i}: compared {compared}, exact {exact}; position error p50 {Harness.Percentile(errors, 0.5):F4} p95 {Harness.Percentile(errors, 0.95):F4} " +
                $"p99 {Harness.Percentile(errors, 0.99):F4} max {errors.DefaultIfEmpty().Max():F3} m; {attacks} attacks, {lunged:F1} m moved while attacking, lock ticks {locks[i]}; corrections p99 {Harness.Percentile(c.CorrectionErrors, 0.99):F4}");
            Assert.True(compared > 300 && attacks > 5 && locks[i] > 50, $"client {i}: too little melee ({compared}, {attacks}, {locks[i]})");
            Assert.True(lunged > 5f, $"client {i}: no lunge ({lunged} m)");
            Assert.Equal(compared, exact);   // exact: every lock tick views a snapshot the client holds, the server's history at that tick
            Assert.True(Harness.Percentile(errors, 0.99) < 0.1f, "p99 position error");
            Assert.True(errors.Max() < 0.5f, "max position error");
        }
    }
}
