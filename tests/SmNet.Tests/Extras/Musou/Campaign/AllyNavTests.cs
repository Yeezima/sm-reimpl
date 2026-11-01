using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>Allied navigation and line of sight (pass 2's M2): the Guardsmen and the AI Marines go round walls by the walking graph, and a shot needs a sight. Synthetic levels only.</summary>
public class AllyNavTests
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou,
    };

    /// <summary>A 60 m square floor cut by a wall at x = 30, open only at the north end, y above 52: from (10, 10) to (50, 10) is 40 m straight and about 90 m on foot.</summary>
    static StaticMesh Maze() => new NavigationTests.Level().Floor(0f, 0f, 60f, 60f).Wall(30f, 0f, 30f, 52f).Mesh();

    static NavGraph Graph(StaticMesh mesh) => NavGraph.Build(mesh, null, NavSettings.From(Config));

    [Fact]
    public void A_squad_goes_round_a_wall_it_could_not_walk_through()
    {
        var sim = new MusouSim(Graph(Maze()));
        var guards = new GuardSim(sim);
        int s = guards.SpawnSquad(new Vector3(10f, 10f, 0f));
        var to = new Vector3(50f, 10f, 0f);
        Assert.True(guards.Order(s, to));
        uint k = 1;
        for (; k < 60 * 30 && !guards.Arrived(s); k++) { guards.BeginTick(k); guards.Step(false); }
        Assert.True(guards.Arrived(s), $"still walking at {guards.Centre(s)}");
        for (int t = 0; t < 10 * 30; t++) { guards.BeginTick(k + (uint)t); guards.Step(false); }
        Assert.True(guards.CountInside(to, 8f) == GuardSim.SquadSize, $"{guards.CountInside(to, 8f)} of the men came, centre {guards.Centre(s)}");
    }

    [Fact]
    public void Squads_bound_for_one_point_share_one_field()
    {
        var sim = new MusouSim(Graph(Maze()));
        var guards = new GuardSim(sim);
        var point = new Vector3(50f, 10f, 0f);
        int a = guards.SpawnSquad(new Vector3(10f, 10f, 0f)), b = guards.SpawnSquad(new Vector3(10f, 30f, 0f));
        guards.Order(a, point + new Vector3(4f, 0f, 0f), point);
        guards.Order(b, point + new Vector3(-4f, 0f, 0f), point);
        uint k = 1;
        for (; k < 60 * 30 && !(guards.Arrived(a) && guards.Arrived(b)); k++) { guards.BeginTick(k); guards.Step(false); }
        Assert.True(guards.Arrived(a) && guards.Arrived(b));
        Assert.True(guards.Centre(a).X > 30f && guards.Centre(b).X > 30f, $"{guards.Centre(a)} {guards.Centre(b)}");
    }

    [Fact]
    public void A_field_being_worked_on_is_asked_for_again_and_the_oldest_goes()
    {
        var walk = new MusouNav(Graph(Maze()));
        var ways = new AllyFlow(walk);
        int goal = ways.GoalNode(new Vector3(50f, 10f, 0f));
        Assert.True(goal >= 0);
        Assert.Null(ways.Ready(goal));
        for (int k = 0; k < 20 && ways.Ready(goal) is null; k++) ways.Step();
        var field = ways.Ready(goal);
        Assert.NotNull(field);
        Assert.Equal(-1, ways.GoalNode(new Vector3(500f, 500f, 0f)));
        Assert.Null(ways.Ready(-1));
        for (int k = 0; k < AllyFlow.Slots; k++) Assert.Null(ways.Ready(ways.GoalNode(new Vector3(35f + k * 2f, 5f, 0f))));
        Assert.Null(ways.Ready(goal));   // pushed out by eight newer goals: it is worked on again
    }

    [Fact]
    public void The_carrot_leads_along_the_way_not_at_the_wall()
    {
        var walk = new MusouNav(Graph(Maze()));
        var ways = new AllyFlow(walk);
        int goal = ways.GoalNode(new Vector3(50f, 10f, 0f));
        Assert.Null(ways.Carrot(new Vector3(28f, 10f, 0f), goal));
        for (int k = 0; k < 20; k++) ways.Step();
        var carrot = ways.Carrot(new Vector3(28f, 10f, 0f), goal);
        Assert.NotNull(carrot);
        Assert.True(carrot.Value.Y > 11f && carrot.Value.X < 30f, $"{carrot}");   // north, along the wall
    }

    static (MusouSim Sim, GuardSim Guards, ushort Enemy) Shooters(StaticMesh mesh)
    {
        var sim = new MusouSim(Graph(mesh)) { Frozen = true };
        var guards = new GuardSim(sim) { Sight = new CollisionWorld(mesh, mesh) };
        guards.SpawnSquad(new Vector3(20f, 10f, 0f));
        ushort id = sim.Spawn(MusouEnemyType.Warboss, new Vector3(40f, 10f, 0f), 0f, 1e7f);
        for (uint k = 1; k <= 300; k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(true); }
        return (sim, guards, id);
    }

    [Fact]
    public void A_wall_stops_a_guardsmans_shot_and_open_ground_does_not()
    {
        var (_, blocked, _) = Shooters(Maze());
        Assert.Equal(0, blocked.Shots);
        var (_, open, _) = Shooters(new NavigationTests.Level().Floor(0f, 0f, 60f, 60f).Mesh());
        Assert.InRange(open.Shots, 80, 120);   // 10 men, 1 shot/s, 10 s
    }

    [Fact]
    public void A_squad_stands_and_fights_an_enemy_on_it_and_walks_on_when_it_is_gone()
    {
        var mesh = new NavigationTests.Level().Floor(0f, 0f, 100f, 60f).Mesh();
        var sim = new MusouSim(Graph(mesh)) { Frozen = true };
        var guards = new GuardSim(sim);
        int s = guards.SpawnSquad(new Vector3(10f, 30f, 0f));
        guards.Order(s, new Vector3(90f, 30f, 0f));
        ushort enemy = sim.Spawn(MusouEnemyType.Warboss, new Vector3(15f, 30f, 0f), 0f, 1e7f);
        uint k = 1;
        for (; k <= 90; k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(true); }
        float held = guards.Centre(s).X;
        Assert.True(held < 14f, $"{held}");
        sim.Remove(enemy);
        for (; k <= 300; k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(true); }
        Assert.True(guards.Centre(s).X > held + 20f, $"{guards.Centre(s)}");
    }

    [Fact]
    public void An_ai_marine_goes_round_the_wall_to_the_point_it_is_sent_to()
    {
        var mesh = Maze();
        var w = new World(Config, new CollisionWorld(mesh, mesh), [new SpawnPoint(new Vector3(10f, 40f, 0f), 0f)]) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(Graph(mesh), TfwTestArena.Field));
        w.Spawn(1, w.AssignClass(0));
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) w.Step(_ => default);
        int marine = w.TfwSpawnRetainer(1, new Vector3(10f, 10f, 0f))!.Value;
        var to = new Vector3(50f, 10f, 0f);
        Assert.True(w.TfwOrderMarines(1, new TfwCommandRecord(1, TfwCommand.Attack, 0, to)));
        for (int k = 0; k < 40 * 30 && MarineBrain.Flat(w.Players[marine].Position, to) > MarineRules.ArriveRadius; k++) w.Step(_ => default);
        Assert.True(MarineBrain.Flat(w.Players[marine].Position, to) <= MarineRules.ArriveRadius + 1f, $"stopped at {w.Players[marine].Position}");
    }

    [Fact]
    public void Decide_walks_towards_the_via_and_stops_by_the_goal()
    {
        var self = new PlayerState { PlayerId = 1, Position = Vector3.Zero, Yaw = 0f, Alive = true };
        var goal = new MarineGoal(new Vector3(20f, 0f, 0f), 0f, 2f, false, new Vector3(0f, 3f, 0f));
        var input = MarineBrain.Decide(self, null, goal, null, float.MaxValue, 1.8f, 1u);
        Assert.Equal(0f, MarineBrain.YawOf(0f, 1f), 3);
        Assert.Equal(0f, input.Yaw, 3);
        Assert.True(input.MoveY > 0.99f, $"{input}");
        var there = MarineBrain.Decide(self with { Position = new Vector3(19f, 0f, 0f) }, null, goal, null, float.MaxValue, 1.8f, 1u);
        Assert.Equal(0f, there.MoveX);
        Assert.Equal(0f, there.MoveY);
    }

    [Fact]
    public void Squads_go_for_the_nearest_position_not_secured_and_a_secured_one_keeps_a_squad()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        var guards = w.TfwGuards!;
        var lz = battle.Layout.LandingZone;
        Assert.Equal(battle.Layout.Positions.OrderBy(p => Vector3.Distance(p, lz)).First(), battle.GoalFrom(lz));
        Assert.All(battle.Objectives.Take(3), o => Assert.Equal(TfwPositionState.Held, o.State));
        TfwRig.Seconds(w, 2f);
        for (int s = 0; s < GuardSim.MaxSquads; s++) if (guards.AliveIn(s) > 0) Assert.False(guards.Arrived(s));   // nobody waits at the landing zone
        TfwRig.Secure(w, battle, 0);
        Assert.Equal(battle.Layout.Positions[1], battle.ActivePosition);
        TfwRig.Teleport(w, lz);
        for (int t = 0; t < 40 * 30; t++) { TfwRig.KillAll(w); w.Step(_ => default); }
        Assert.True(guards.CountInside(battle.Layout.Positions[0], TfwTestArena.CaptureRadius) > 0, "a squad stayed on the secured position");
    }
}
