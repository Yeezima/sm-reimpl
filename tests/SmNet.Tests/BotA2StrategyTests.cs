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
