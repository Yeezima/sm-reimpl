using System.Numerics;
using SmBots;
using SmBots.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmServer.Extras.Warfront.Retainers;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;
using Xunit.Abstractions;
using Vector2 = System.Numerics.Vector2;
using Rig = SmNet.Tests.Extras.Warfront.Wiring.WarfrontSquadWiringTests.Rig;

namespace SmNet.Tests.Extras.Warfront.Retainers;

/// <summary>
/// The independent review of Phase B T4 (2026-11-25, FIX FIRST): determinism with a walking graph (F1), a brain's fault (F2), the jump-pound's end (F3), the brain's graph seam (F4), the leash grace
/// when the owner walks away mid-swing (F6), fresh brains per battle and life (L1) and the follow heading (L4).
/// </summary>
public class RetainerBrainReviewTests(ITestOutputHelper output)
{
    /// <summary>The test arena's walking graph round the landing zone (built once; synthetic geometry).</summary>
    static readonly Lazy<NavGraph> ArenaGraph = new(() =>
    {
        var world = TfwTestArena.Level();
        var area = NavArea.Around([TfwTestArena.LandingZone, TfwTestArena.Position1], 45f);
        return NavGraph.Build(world.CharacterFor(Team.SpaceMarines), world.KillVolumes, NavSettings.From(Wiring.WarfrontSquadWiringTests.Cfg()), area);
    });

    static (Rig Rig, WarfrontRetainerBrains Brains, List<string> Log) Driven(bool parallel = true, bool graph = true, int humans = 1)
    {
        var r = Rig.Make(humans: humans, marines: 2);
        var log = new List<string>();
        var nav = graph ? ArenaGraph.Value : null;
        var brains = new WarfrontRetainerBrains(r.W, r.Ret, () => nav, () => "wf test arena", log.Add) { JumpLinks = false, Parallel = parallel }.Attach();
        return (r, brains, log);
    }

    // ---- F1: determinism on a map with a graph ------------------------------------------------------------------------------------------------

    static List<(Vector3, float)> Play(Rig r)
    {
        // the owner walks off 25 m (the retainers follow along the graph), Boyz close in and fight them (low health: the retreat's cover search; the melee push's approach)
        TfwRig.Teleport(r.W, r.Where(1) + new Vector3(14f, 10f, 0f));
        for (int k = 0; k < 4; k++) r.Ork(MusouEnemyType.Boy, r.Where(1) + new Vector3(-12f + 6f * k, -8f, 0f), 600f);
        var trace = new List<(Vector3, float)>();
        for (int t = 0; t < 30 * 15; t++)
        {
            r.Stand(1);
            for (int s = 0; s < 2; s++) if (r.Ret.Of(1, s) is { WorldId: > 0 } x && r.W.Players.TryGetValue(x.WorldId, out var p)) trace.Add((p.Position, p.Yaw));
        }
        return trace;
    }

    [Fact]
    public void With_a_walking_graph_the_inputs_depend_on_neither_the_threads_nor_the_run()
    {
        var (a, ba, _) = Driven(parallel: true);
        var (b, bb, _) = Driven(parallel: false);
        var (c, bc, _) = Driven(parallel: true);
        var ta = Play(a);
        var tb = Play(b);
        var tc = Play(c);
        Assert.NotNull(ba.BrainOf(a.IdOf(1, 0))?.Graph);
        Assert.Equal(ta.Count, tb.Count);
        Assert.Equal(ta.Count, tc.Count);
        for (int i = 0; i < ta.Count; i++) { Assert.Equal(ta[i], tb[i]); Assert.Equal(ta[i], tc[i]); }
        int plans = 0;
        for (int s = 0; s < 2; s++) plans += ba.BrainOf(a.IdOf(1, s))?.NavAgent?.Plans ?? 0;
        output.WriteLine($"{ta.Count} samples identical (parallel, serial, a second parallel run); path plans {plans}; brain step {ba.LastMilliseconds:0.00} ms");
        Assert.True(plans > 0, "the retainers planned paths on the graph");
    }

    // ---- F4: the graph seam -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_host_hands_its_graph_to_the_brain_and_a_retainer_walks_a_path_on_it()
    {
        var (r, brains, _) = Driven();
        r.Stand(2);
        var brain = brains.BrainOf(r.IdOf(1, 0))!;
        Assert.Same(ArenaGraph.Value, brain.Graph);
        Assert.NotNull(brain.NavAgent);
        var start = r.Body(1, 0).Position;
        var owner = r.Where(1) + new Vector3(0f, 25f, 0f);
        TfwRig.Teleport(r.W, owner);
        for (int t = 0; t < 30 * 10; t++) { r.Clear(); r.Stand(1); }   // no Orks: the follow alone
        var end = r.Body(1, 0).Position;
        float d = new Vector2(end.X - owner.X, end.Y - owner.Y).Length();
        output.WriteLine($"from {Vector2.Distance(new(start.X, start.Y), new(owner.X, owner.Y)):0.0} m to {d:0.0} m of the owner; plans {brain.NavAgent!.Plans}");
        Assert.True(brain.NavAgent.Plans > 0);
        Assert.True(d < 8f, $"followed along the graph ({d:0.0} m)");
    }

    // ---- F2: a brain's fault ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_retainer_whose_brain_throws_stands_that_tick_the_other_steps_and_it_is_logged_once()
    {
        var (r, brains, log) = Driven(graph: false);
        r.Stand(2);
        int bad = r.IdOf(1, 0), good = r.IdOf(1, 1);
        brains.FaultForTests = id => { if (id == bad) throw new InvalidOperationException("test fault"); };
        var owner = r.Where(1) + new Vector3(0f, 20f, 0f);
        TfwRig.Teleport(r.W, owner);
        var badAt = r.Body(1, 0).Position;
        for (int t = 0; t < 30 * 6; t++) { r.Clear(); r.Stand(1); }
        Assert.True(Vector2.Distance(new Vector2(badAt.X, badAt.Y), new Vector2(r.Body(1, 0).Position.X, r.Body(1, 0).Position.Y)) < 0.5f, "it stood (no input; at most a body's push)");
        var g = r.Body(1, 1).Position;
        Assert.True(new Vector2(g.X - owner.X, g.Y - owner.Y).Length() < 8f, "the other retainer followed");
        Assert.Single(log, l => l.Contains($"P{bad}: brain failed") && l.Contains("test fault"));
        Assert.DoesNotContain(log, l => l.Contains($"P{good}: brain failed"));
    }

    // ---- F3: the jump-pound's end -------------------------------------------------------------------------------------------------------------

    static WarfrontRetainer Marked(Rig r)
    {
        var x = r.Of(1, 0);
        typeof(WarfrontRetainer).GetProperty(nameof(WarfrontRetainer.PoundAt))!.SetValue(x, (Vector3?)(r.Where(1) + new Vector3(5f, 0f, 0f)));
        typeof(WarfrontRetainer).GetProperty(nameof(WarfrontRetainer.PoundTick))!.SetValue(x, r.W.Tick);
        return x;
    }

    [Fact]
    public void A_jump_pound_mark_ends_on_a_new_order_on_a_fall_and_after_its_timeout()
    {
        var r = Rig.Make(marines: 2);
        var x = Marked(r);
        Assert.True(r.Ret.ServerOrder(1, MarineOrder.Follow));
        Assert.Null(x.PoundAt);

        x = Marked(r);
        r.DownMarine(1, 0);
        Assert.Null(x.PoundAt);

        var y = r.Of(1, 1);
        typeof(WarfrontRetainer).GetProperty(nameof(WarfrontRetainer.PoundAt))!.SetValue(y, (Vector3?)r.Where(1));
        typeof(WarfrontRetainer).GetProperty(nameof(WarfrontRetainer.PoundTick))!.SetValue(y, r.W.Tick);
        r.Stand((int)(WarfrontRetainers.PoundTimeoutSeconds * 30f) - 5);
        Assert.NotNull(y.PoundAt);
        r.Stand(10);
        Assert.Null(y.PoundAt);
    }

    // ---- F6: the grace when the owner walks away mid-swing ------------------------------------------------------------------------------------

    [Fact]
    public void The_owner_walking_away_mid_swing_gives_the_grace_and_the_retainers_own_charge_out_is_an_overshoot()
    {
        var l = new WarfrontLeash();
        var self = Vector3.Zero;
        l.Step(0, RetainerOrder.Follow, new Vector3(0f, 38f, 0f), self, false, 0);   // inside, fighting
        // the owner walks 4 m away while the retainer is in a swing (committed), standing where it was
        Assert.Equal(LeashState.Grace, l.Step(0.5, RetainerOrder.Follow, new Vector3(0f, 42f, 0f), self, true, 0.5));
        Assert.Equal(LeashState.Grace, l.Step(2.0, RetainerOrder.Follow, new Vector3(0f, 44f, 0f), self, false, 2.0));
        Assert.Equal(LeashState.Return, l.Step(3.6, RetainerOrder.Follow, new Vector3(0f, 44f, 0f), self, false, 3.6));

        var m = new WarfrontLeash();
        var owner = new Vector3(0f, 38f, 0f);
        m.Step(0, RetainerOrder.Follow, owner, Vector3.Zero, false, 0);
        // the retainer's own charge carries it 6 m out, the owner standing
        Assert.Equal(LeashState.Overshoot, m.Step(0.3, RetainerOrder.Follow, owner, new Vector3(0f, -6f, 0f), true, 0.3));
        Assert.Equal(LeashState.Return, m.Step(0.6, RetainerOrder.Follow, owner, new Vector3(0f, -6f, 0f), false, 0.6));
    }

    // ---- L4: the follow heading ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Panning_the_owners_camera_does_not_move_the_follow_spot_and_walking_turns_it()
    {
        var s = new WarfrontRetainerStrategy();
        var world = new MinimalWorld();
        var brain = new ClassicBotBrain(world, BotArgs.None, new ClassicBotOptions { SyncPlans = true }, BotLog.None, IBotHooks.None, s);
        s.Situation.Owner = new Vector3(0f, 2f, 0f);
        s.Situation.OwnerYaw = 0f;
        brain.Step(1);
        float first = s.OwnerHeading;
        s.Situation.OwnerYaw = 2.5f;   // the camera pans; he stands
        world.Now = 0.1; brain.Step(2);
        Assert.Equal(first, s.OwnerHeading);
        s.Situation.Owner = new Vector3(5f, 2f, 0f);   // he walks 5 m east
        world.Now = 0.2; brain.Step(3);
        Assert.Equal(MathF.Atan2(-5f, 0f), s.OwnerHeading, 4);
    }

    sealed class MinimalWorld : IBotWorldView
    {
        public double Now;
        public PlayerState Me = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), Wiring.WarfrontSquadWiringTests.Cfg()) with { Team = Team.SpaceMarines };
        public int PlayerId => 1;
        public PlayerState Predicted => Me;
        public IReadOnlyList<PlayerState> Others => [];
        public double ConnectedFor => Now;
        public SimConfig? Config { get; } = Wiring.WarfrontSquadWiringTests.Cfg();
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
        public BotWork Work { get; } = new();
    }

    // ---- L1: fresh brains per battle and per life ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_revived_retainer_gets_a_fresh_brain()
    {
        var (r, brains, _) = Driven(graph: false);
        r.Stand(2);
        int id = r.IdOf(1, 0);
        var first = brains.BrainOf(id);
        Assert.NotNull(first);
        r.DownMarine(1, 0);
        r.Stand(3);
        Assert.Same(first, brains.BrainOf(id));   // downed: the same brain (it notes the death)
        Assert.True(r.Ret.Revive(r.Of(1, 0)));
        r.Stand(3);
        var second = brains.BrainOf(id);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    /// <summary>Review 2026-11-25 (4): with every retainer down nothing asks for an input, so the falls go unseen; a revived one still gets a fresh brain (renewed when not stepped on the last tick).</summary>
    [Fact]
    public void A_retainer_revived_after_every_retainer_was_down_gets_a_fresh_brain()
    {
        var (r, brains, _) = Driven(graph: false);
        r.Stand(2);
        int id = r.IdOf(1, 0);
        var first = brains.BrainOf(id);
        Assert.NotNull(first);
        r.DownMarine(1, 0);
        r.DownMarine(1, 1);
        r.Stand(30);
        Assert.True(r.Ret.Revive(r.Of(1, 0)));
        r.Stand(3);
        Assert.NotNull(brains.BrainOf(id));
        Assert.NotSame(first, brains.BrainOf(id));
    }
}
