using System.Numerics;
using SmBots;
using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Bots A2 re-review (2026-11-25; test aids, ours): the strategy's gate (<see cref="ClassicBotBrain.RoutesAllowed"/>: not on Manufactorum, not in Seize Ground, not with
/// <c>--bot-old-routes</c>), a stalled route leg dropping the route for the life, and a new plan (another class's graph) starting the life's choices again. Engine-free: a brain on
/// a fake world (no collision, so it walks straight) over a synthetic yard's plan.
/// </summary>
public class BotA2StrategyTests
{
    static readonly SimConfig Config = new();
    static readonly Vector3 MarinesStart = new(70, 0, 0), ChaosStart = new(-70, 0, 0), Centre = new(0, 0, 0);

    static readonly Lazy<NavGraph> Yard = new(() =>
    {
        var level = new NavigationTests.Level().Floor(-80, -40, 80, 40).Wall(-30, -14, 30, -14).Wall(-30, 14, 30, 14);
        var mesh = level.Mesh();
        return NavGraph.Build(new CollisionWorld(mesh, mesh, null, []).CharacterFor(Team.SpaceMarines), null, NavSettings.From(Config));
    });

    static BotMapPlan Plan() => BotMapPlan.Build(Yard.Value, MarinesStart, ChaosStart, null, Centre)!;

    sealed class StillWorld : IBotWorldView
    {
        public PlayerState Me = Combat.Respawned(new PlayerState(1, MarinesStart, Vector3.Zero, 0f, true), new SimConfig()) with { Team = Team.SpaceMarines };
        public double Now;
        public int PlayerId => 1;
        public PlayerState Predicted => Me;
        public IReadOnlyList<PlayerState> Others => [];
        public double ConnectedFor => Now;
        public SimConfig? Config { get; } = new SimConfig();
        public CollisionWorld? Collision => null;
        public MatchView Match => new(MatchState.Playing, Team.None, 1000, 0, 0);
        public IReadOnlyList<ControlPointView> Points => [];
        public SeizeGroundInfo? SeizeLayout => null;
        public bool SeizeGround => false;
        public string MapName => "mp_tdm_01";
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

    [Fact]
    public void A_route_that_stalled_twice_is_avoided_in_later_lives()
    {
        var world = new StillWorld();
        var brain = new ClassicBotBrain(world, BotArgs.None, new ClassicBotOptions(), BotLog.None);
        var plan = Plan();
        void NewLife() { var alive = world.Me; world.Me = alive with { Alive = false }; brain.ClassicBegin(world.Me); world.Me = alive; brain.ClassicBegin(world.Me); }
        uint tick = 1;
        int StallOnce()
        {
            brain.BotRouteLife(world.Me, plan);
            int r = brain.RouteOfLife;
            for (int k = 0; k < 60 && !brain.RouteLegDone; k++) { world.Now += 0.1; brain.BotRouteLegInput(world.Me, tick++, null, plan); }
            Assert.True(brain.RouteLegDone);
            return r;
        }
        brain.ClassicBegin(world.Me);   // life 0
        var stalledOn = new List<int>();
        for (int life = 0; life < 2 * plan.Routes.Count + 2; life++)
        {
            stalledOn.Add(StallOnce());
            NewLife();
        }
        // Every route was taken at most RouteAvoidAfterStalls times; then no route leg at all (all avoided).
        foreach (var g in stalledOn.Where(r => r >= 0).GroupBy(r => r)) Assert.True(g.Count() <= ClassicBotBrain.RouteAvoidAfterStalls, $"route {g.Key} taken {g.Count()} times");
        Assert.All(Enumerable.Range(0, plan.Routes.Count), r => Assert.Equal(ClassicBotBrain.RouteAvoidAfterStalls, brain.RouteStalls[r]));
        brain.BotRouteLife(world.Me, plan);
        Assert.Equal(-1, brain.RouteOfLife);
        Assert.True(brain.RouteLegDone);
    }

    [Fact]
    public void The_stall_watch_does_not_count_a_wait_at_a_jump_take_off()
    {
        var w = new RouteStallWatch();
        var goal = new Vector3(0, 20, -9); var here = new Vector3(0, 19, 0);   // a point on the floor below, the bot not moving
        double t = 0;
        // Waiting for jump power (paused) for longer than the stall time: never a stall.
        for (; t < 2 * ClassicBotBrain.RouteStallSeconds; t += 0.1) Assert.False(w.Step(t, goal, here, paused: true));
        // Then standing still unpaused: a stall after RouteStallSeconds, not at once.
        double from = t; bool stalled = false;
        for (; t < from + ClassicBotBrain.RouteStallSeconds + 0.5 && !stalled; t += 0.1) stalled = w.Step(t, goal, here);
        Assert.True(stalled);
        Assert.InRange(t - from, ClassicBotBrain.RouteStallSeconds, ClassicBotBrain.RouteStallSeconds + 0.25);
        // Short unpaused stretches between pauses add up (the pause freezes the watch, it does not restart it): 1 s walking, 1 s waiting, and so on.
        var frozen = new RouteStallWatch(); bool hit = false; double walked = 0, u0 = 0;
        for (double u = u0; u < 20 && !hit; u += 0.1)
        {
            bool waiting = ((int)Math.Floor(u)) % 2 == 1;
            hit = frozen.Step(u, goal, here, paused: waiting);
            if (!waiting) walked += 0.1;
        }
        Assert.True(hit, "the unpaused seconds add up to a stall");
        Assert.InRange(walked, ClassicBotBrain.RouteStallSeconds - 0.25, ClassicBotBrain.RouteStallSeconds + 0.35);
        // Progress (1 m closer) keeps it going.
        var moving = new RouteStallWatch(); var p = new Vector3(0, 0, 0);
        for (double u = 0; u < 10; u += 0.1) { p.Y += 0.05f; Assert.False(moving.Step(u, goal, p)); }
    }

    [Fact]
    public void A_path_ending_in_a_drop_is_not_reached_from_the_ledge_above_its_landing()
    {
        var level = new NavigationTests.Level().Floor(-20, -10, 30, 10).Box(new Vector3(-20, -10, 0), new Vector3(0, 10, 5));   // a 5 m block, an open edge at x = 0
        var g = NavGraph.Build(level.Mesh(), null, NavSettings.From(Config));
        // The shortest drop link off the edge (take-off on the block, landing on the ground 5 m down); the arrival radius is set just past its flat length, so the landing is
        // "within reach" by flat distance from the ledge (as the Hab Center route points were).
        Vector3 ledge = default, below = default; float shortest = float.MaxValue;
        for (int n = 0; n < g.NodeCount; n++)
        {
            var p = g.Position(n);
            if (p.Z < 4f || MathF.Abs(p.Y) > 3f) continue;
            foreach (var (to, _, kind) in g.Links(n))
            {
                var q = g.Position(to);
                float flat = new System.Numerics.Vector2(q.X - p.X, q.Y - p.Y).Length();
                if (kind != NavLinkKind.Drop || q.Z > 1f || flat >= shortest) continue;
                ledge = p; below = q; shortest = flat;
            }
        }
        Assert.True(shortest < 3f, "the 5 m edge has drop links");
        float radius = shortest + 0.3f;
        NavSteer Last(NavAgent a) { NavSteer s = default; for (int k = 0; k < 30; k++) s = a.Steer(ledge, true, below, k * 0.033); return s; }
        Assert.True(Last(new NavAgent(g) { ArriveRadius = radius }).Arrived);   // the old flat arrival: "there" while still on the ledge
        var steer = Last(new NavAgent(g) { ArriveRadius = radius, ArriveMaxDz = ClassicBotBrain.ArriveMaxDz });
        Assert.False(steer.Arrived);                  // A2: walk on, off the edge
        Assert.True(steer.Direction.X > 0.5f, $"walks toward the edge: {steer.Direction}");
    }

    [Fact]
    public void The_A2_strategy_runs_only_in_Annihilation_on_the_imported_Classic_maps()
    {
        Assert.True(ClassicBotBrain.RoutesAllowed("mp_tdm_01", seizeGround: false, oldRoutes: false, routes: 4));
        Assert.True(ClassicBotBrain.RoutesAllowed("mp_dlc2_map4", false, false, 1));
        Assert.False(ClassicBotBrain.RoutesAllowed("mp_tdm_09", false, false, 4));    // Manufactorum: the owner's "on maps that aren't manufactorum"
        Assert.False(ClassicBotBrain.RoutesAllowed("mp_tdm_01", true, false, 4));     // Seize Ground keeps its point logic
        Assert.False(ClassicBotBrain.RoutesAllowed("mp_tdm_01", false, true, 4));     // --bot-old-routes
        Assert.False(ClassicBotBrain.RoutesAllowed("mp_tdm_01", false, false, 0));    // no routes
        Assert.False(ClassicBotBrain.RoutesAllowed("tfw_war", false, false, 4));      // never Musou Mode's maps
        Assert.False(ClassicBotBrain.RoutesAllowed("", false, false, 4));             // no map loaded yet
    }

    [Fact]
    public void A_route_leg_without_progress_drops_the_route_for_the_life_and_a_new_plan_starts_the_choices_again()
    {
        var world = new StillWorld();
        var brain = new ClassicBotBrain(world, BotArgs.None, new ClassicBotOptions(), BotLog.None);
        var plan = Plan();
        brain.BotRouteLife(world.Me, plan);
        int route = brain.RouteOfLife;
        Assert.InRange(route, 0, plan.Routes.Count - 1);
        Assert.False(brain.RouteLegDone);
        Assert.NotNull(brain.BotRouteLegInput(world.Me, 1, null, plan));   // walking the leg
        // The bot never moves (a point it cannot reach): after ClassicBotBrain.RouteStallSeconds the leg ends and the route is dropped for this life.
        PlayerInput? last = null;
        for (int k = 1; k <= 45 && !brain.RouteLegDone; k++)
        {
            world.Now = k * 0.1;
            last = brain.BotRouteLegInput(world.Me, (uint)(k + 1), null, plan);
        }
        Assert.True(brain.RouteLegDone);
        Assert.Null(last);
        Assert.Contains(route, brain.RoutesDropped);
        Assert.InRange(world.Now, ClassicBotBrain.RouteStallSeconds, ClassicBotBrain.RouteStallSeconds + 0.3);
        // Same life, same plan: nothing restarts.
        brain.BotRouteLife(world.Me, plan);
        Assert.True(brain.RouteLegDone);
        // A new plan (another class's graph): the life's choices start again, nothing dropped.
        var other = Plan();
        brain.BotRouteLife(world.Me, other);
        Assert.False(brain.RouteLegDone);
        Assert.Empty(brain.RoutesDropped);
        Assert.InRange(brain.RouteOfLife, 0, other.Routes.Count - 1);
    }
}
