using System.Numerics;
using SmBots;
using SmContent;
using SmSim;
using BotVector2 = SmBots.Vector2;

namespace SmNet.Tests;

/// <summary>
/// T1 (Phase B, 2026-11-25): the engine-free Classic bot brain (src/SmBots) and its seams. Godot's Vector2 formulas bit for bit (values measured with GodotSharp
/// 4.7.2), the command-line rules of the client, the tick-stable background work, and a brain on the flat ground with a fake world: the strategy, aim-height and
/// goal-clamp seams, and two brains fed the same inputs giving the same input every tick (what the client's <c>--bot-shadow</c> checks in a real match).
/// </summary>
public class BotBrainSeamTests
{
    static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    [Fact]
    public void Vector2_matches_Godot_bit_for_bit()
    {
        // Measured with GodotSharp 4.7.2 (Godot.Vector2): (0.6, -0.8).Rotated(DegToRad(135)) and (3.3, -7.1).Normalized().
        var r = new BotVector2(0.6f, -0.8f).Rotated(SmBots.Mathf.DegToRad(135f));
        Assert.Equal(0x3E10D0C4, Bits(r.X));
        Assert.Equal(0x3F7D6D55, Bits(r.Y));
        var n = new BotVector2(3.3f, -7.1f).Normalized();
        Assert.Equal(0x3ED7CD15, Bits(n.X));
        Assert.Equal(unchecked((int)0xBF682651), Bits(n.Y));
        Assert.Equal(BotVector2.Zero, BotVector2.Zero.Normalized());   // Godot: the zero vector stays zero
        Assert.Equal(MathF.Sqrt(3f * 3f + 4f * 4f), new BotVector2(0f, 0f).DistanceTo(new BotVector2(3f, 4f)));
        Assert.Equal(11f, new BotVector2(1f, 2f).Dot(new BotVector2(3f, 4f)));
        Assert.Equal(90f * (3.1415927f / 180f), SmBots.Mathf.DegToRad(90f));
    }

    [Fact]
    public void BotArgs_reads_flags_like_the_client()
    {
        var a = new BotArgs(["--bot", "--bot-accuracy", "0.5", "--bot-route", "A", "B", "--bot-preset", "--x", "--bot-range", "nope"]);
        Assert.True(a.Flag("bot"));
        Assert.False(a.Flag("bot-door-wait"));
        Assert.Equal("0.5", a.Arg("bot-accuracy"));
        Assert.Null(a.Arg("bot-preset"));                 // followed by another flag
        Assert.Equal(["A", "B"], a.Args("bot-route", 5));
        Assert.Empty(a.Args("missing", 1));
        Assert.Equal(0.5f, a.Float("bot-accuracy"));
        Assert.True(float.IsNaN(a.Float("bot-range")));
        Assert.False(BotArgs.None.Flag("bot"));
    }

    [Fact]
    public void BotWork_hands_results_on_at_a_poll_and_shares_a_key()
    {
        var work = new BotWork();
        using var done = new ManualResetEventSlim();
        var task = work.Run("lane|1|nav", () => { done.Set(); return 42; }, CancellationToken.None);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        SpinWait.SpinUntil(() => false, 50);
        Assert.False(task.IsCompleted);                    // done, but not handed on before the poll
        Assert.Same(task, work.Run("lane|1|nav", () => 7, CancellationToken.None));   // the same key: the same job (a shadow brain shares it)
        for (int i = 0; i < 200 && !task.IsCompleted; i++) { work.Poll(); if (!task.IsCompleted) Thread.Sleep(10); }
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(42, task.Result);
        work.Clear();
        Assert.NotSame(task, work.Run("lane|1|nav", () => 7, CancellationToken.None));   // a new map forgets the keys
    }

    /// <summary>A flat-ground world (no collision): the bot (player 1, Space Marines) and the given others; its own view.</summary>
    sealed class FlatWorld : IBotWorldView
    {
        public PlayerState Me = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), new SimConfig()) with { Team = Team.SpaceMarines };
        public List<PlayerState> OthersList = [];
        public double Now;
        public float? AimHeight;
        public int PlayerId => 1;
        public PlayerState Predicted => Me;
        public IReadOnlyList<PlayerState> Others => OthersList;
        public double ConnectedFor => Now;
        public SimConfig? Config { get; } = new SimConfig();
        public CollisionWorld? Collision => null;
        public MatchView Match => new(MatchState.Playing, Team.None, 1000, 0, 0);
        public IReadOnlyList<ControlPointView> Points => [];
        public SeizeGroundInfo? SeizeLayout => null;
        public bool SeizeGround => false;
        public string MapName => "";
        public TimeSpan CurrentInterpolationDelay => TimeSpan.Zero;
        public IEnumerable<ProjectileView> NewestProjectiles() => [];
        public IReadOnlyList<ProjectileView> Projectiles() => [];
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float GrenadesOutFor => 0f;
        public int OwnShotsFired => 0;
        public ContentExtras MatchExtras => ContentExtras.None;
        public StimSpec? StimOf(PlayerState s) => null;
        public float AimHeightOf(PlayerState target) => AimHeight ?? ClassicBotBrain.BotTargetHeight;
        public BotWork Work { get; } = new();
    }

    sealed class RecordingStrategy : IBotStrategy
    {
        public int Begins, Fights, Idles;
        public PlayerInput? IdleInput;
        public void Begin(ClassicBotBrain brain, PlayerState me) => Begins++;
        public PlayerInput? BeforeFight(ClassicBotBrain brain, PlayerState me, uint tick, double phase) => null;
        public void NoteFight(ClassicBotBrain brain, PlayerState me, PlayerState target) => Fights++;
        public PlayerInput? FightMove(ClassicBotBrain brain, PlayerState me, PlayerState target, uint tick, float horizontal) => null;
        public BotPost? Post(ClassicBotBrain brain, PlayerState me) => null;
        public Vector3? PostReturn(ClassicBotBrain brain, PlayerState me) => null;
        public PlayerInput? Idle(ClassicBotBrain brain, PlayerState me, uint tick) { Idles++; return IdleInput; }
        public Vector3 ClampGoal(ClassicBotBrain brain, PlayerState me, Vector3 goal) => new(1f, 1f, 0f);
    }

    static ClassicBotBrain Brain(FlatWorld world, IBotStrategy? strategy = null) =>
        new(world, new BotArgs(["--bot-accuracy", "1"]), new ClassicBotOptions(), BotLog.None, strategy: strategy);

    static PlayerState Enemy(Vector3 at) => Combat.Respawned(new PlayerState(2, at, Vector3.Zero, 0f, true), new SimConfig()) with { Team = Team.Chaos };

    [Fact]
    public void The_strategy_seam_runs_at_its_points()
    {
        var world = new FlatWorld();
        var strategy = new RecordingStrategy { IdleInput = new PlayerInput(0, 0f, 0f, 0.25f, InputButtons.None) };
        var brain = Brain(world, strategy);
        var idle = brain.Step(1);
        Assert.Equal(1, strategy.Begins);
        Assert.Equal(1, strategy.Idles);
        Assert.Equal(0.25f, idle.Yaw);                       // the strategy's idle input is the bot's (no aim error at accuracy 1)
        world.OthersList.Add(Enemy(new Vector3(0f, 10f, 0f)));
        world.Now = 0.1;
        brain.Step(2);
        Assert.Equal(1, strategy.Fights);                    // an enemy on the flat ground is fought (no strategy idle)
        Assert.Equal(1, strategy.Idles);
        Assert.Equal(0f, world.Yaw, 4);                      // facing +y: yaw 0
    }

    [Fact]
    public void The_aim_height_seam_sets_the_pitch()
    {
        float PitchWith(float? height)
        {
            var world = new FlatWorld { AimHeight = height };
            world.OthersList.Add(Enemy(new Vector3(0f, 10f, 0f)));
            Brain(world).Step(1);
            return world.Pitch;
        }
        float chest = PitchWith(null), high = PitchWith(3f);
        var c = new SimConfig();
        Assert.Equal(MathF.Atan2(ClassicBotBrain.BotTargetHeight - c.EyeHeight, 10f), chest, 4);
        Assert.Equal(MathF.Atan2(3f - c.EyeHeight, 10f), high, 4);
    }

    [Fact]
    public void Steering_goes_through_the_goal_clamp()
    {
        var world = new FlatWorld();
        var brain = Brain(world, new RecordingStrategy());
        var (direction, _, _, _) = brain.BotSteer(world.Me, new Vector3(0f, 50f, 0f), "test");
        // No graph: the straight walk, toward the clamped goal (1, 1), not the asked one (0, 50).
        Assert.Equal(MathF.Sqrt(0.5f), direction.X, 4);
        Assert.Equal(MathF.Sqrt(0.5f), direction.Y, 4);
    }

    [Fact]
    public void Two_brains_on_the_same_inputs_give_the_same_input_every_tick()
    {
        // The shadow comparison's property, engine-free: same world, same args, same seeds (by player id), so every tick's input and view agree.
        var a = new FlatWorld(); var b = new FlatWorld();
        var brainA = new ClassicBotBrain(a, BotArgs.None, new ClassicBotOptions(), BotLog.None);
        var brainB = new ClassicBotBrain(b, BotArgs.None, new ClassicBotOptions(), BotLog.None);
        for (uint tick = 1; tick <= 600; tick++)
        {
            var at = new Vector3(8f * MathF.Sin(tick * 0.05f), 6f + 20f * MathF.Cos(tick * 0.013f), 0f);
            foreach (var w in new[] { a, b })
            {
                w.Now = tick / 30.0;
                w.OthersList.Clear();
                if (tick % 200 < 150) w.OthersList.Add(Enemy(at));
            }
            b.Yaw = a.Yaw; b.Pitch = a.Pitch;
            var ia = brainA.Hold(brainA.Step(tick));
            var ib = brainB.Hold(brainB.Step(tick));
            Assert.Equal(ia, ib);
            Assert.Equal(Bits(a.Yaw), Bits(b.Yaw));
            Assert.Equal(Bits(a.Pitch), Bits(b.Pitch));
        }
    }

    [Fact]
    public void A_seeded_preset_draw_repeats_and_a_server_brain_has_no_test_aids()
    {
        var world = new FlatWorld();
        var brain = new ClassicBotBrain(world, BotArgs.None, new ClassicBotOptions { PresetSeed = 5 }, BotLog.None);
        Assert.Null(brain.Preset);
        Assert.False(brain.CommittedMove(world.Me));
        Assert.Equal("", brain.Note);
    }
}
