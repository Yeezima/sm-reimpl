using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Squad;

/// <summary>W9 second pass (designer review 1): campaign keys, late landing, the puppet damage share, the walking graph and the shared path.</summary>
public class SquadSecondPassTests
{
    const int Rate = 30;
    static uint _t;

    static SquadHuman Owner(Vector3 at, int id = 1) => new(id, at, true);
    static void Run(WarfrontSquadSim sim, int ticks, params SquadHuman[] humans) { for (int k = 0; k < ticks; k++) sim.Step(++_t, humans); }
    static void Down(WarfrontSquadSim sim, int index) => Assert.True(sim.Hit(SquadRules.TargetId(index), 100000f, Vector3.Zero));

    [Fact]
    public void A_roster_survives_a_change_of_its_owners_player_id()
    {
        var r = new SquadRosters();
        r.AddOwner(77UL); r.Recruit(77UL); r.Rename(77UL, 1, "Keeper");
        var sim = new WarfrontSquadSim(r, new FakeArena());
        Assert.Equal(2, sim.BeginBattle([(77UL, 3, Vector3.Zero)]));
        Assert.True(sim.Order(3, new TfwCommandRecord(1, TfwCommand.Hold, 0)));
        r.Marine(77UL, 0)!.Record.Kills = 9;
        sim.EndBattle();
        // the player reconnects with another id: same key, new binding
        Assert.Equal(2, sim.BeginBattle([(77UL, 41, Vector3.Zero)]));
        Assert.False(sim.Order(3, new TfwCommandRecord(2, TfwCommand.Hold, 0)));   // the old id commands nothing
        Assert.True(sim.Order(41, new TfwCommandRecord(2, TfwCommand.Hold, 0)));
        Assert.Equal("Keeper", r.Marine(77UL, 1)!.Name);
        Assert.Equal(9, r.Marine(77UL, 0)!.Record.Kills);
        Assert.Equal(2, sim.StandingOf(41));
        Assert.Equal(0, sim.StandingOf(3));
        var back = new SquadRosters(); back.Import(r.Export());   // a save is keyed by the campaign key too
        Assert.Equal("Keeper", back.Marine(77UL, 1)!.Name);
    }

    [Fact]
    public void At_most_four_squads_fight_at_once_and_a_late_landing_leaves_the_others_alone()
    {
        var r = new SquadRosters();
        for (ulong k = 1; k <= 5; k++) { r.AddOwner(k); r.Recruit(k); }
        var sim = new WarfrontSquadSim(r, new FakeArena());
        Assert.Equal(2, sim.Land(1UL, 1, new Vector3(5, 0, 0)));
        Assert.Equal(2, sim.Land(2UL, 2, new Vector3(-5, 0, 0)));
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        Run(sim, 5, Owner(new Vector3(5, 0, 0)));
        Down(sim, 0);
        var before = sim.View(1)!.Value.Position;
        Assert.Equal(2, sim.Land(3UL, 3, new Vector3(40, 0, 0)));   // late
        Assert.Equal(SquadState.Downed, sim.StateOf(0));            // nothing of owner 1's reset
        Assert.Equal(before, sim.View(1)!.Value.Position);
        Assert.Equal(SquadOrder.Hold, sim.OrderOf(1));
        Assert.Equal(2, sim.Land(4UL, 4, Vector3.Zero));
        Assert.Equal(0, sim.Land(5UL, 5, Vector3.Zero));            // a fifth does not fit
        Assert.Equal(2, sim.Land(3UL, 33, new Vector3(40, 0, 0)));  // a returning player's squad lands again, re-bound
        Assert.Equal(2, sim.StandingOf(33));
        Assert.Equal(SquadState.Downed, sim.StateOf(0));
    }

    [Fact]
    public void A_Marine_takes_the_puppet_Marines_tenth_of_an_Ork_hit()
    {
        var r = new SquadRosters(); r.AddOwner(1UL);
        r.Mirror(1UL, new SquadLoadout { HealthMax = 100f, ArmourMax = 0f });
        var sim = new WarfrontSquadSim(r, new FakeArena());
        sim.Land(1UL, 1, Vector3.Zero);
        Assert.Equal(0.10f, SquadRules.DamageTaken);
        sim.Hit(SquadRules.TargetId(0), 50f, Vector3.Zero);
        Assert.Equal(0.95f, sim.View(0)!.Value.HealthShare, 3);
    }

    [Fact]
    public void A_squad_kill_pays_its_owners_xp_exactly_once()
    {
        var r = new SquadRosters(); r.AddOwner(1UL);
        r.Mirror(1UL, new SquadLoadout { Weapon = new SquadWeapon(30f, 500f, 0.1f, 1f) });
        var arena = new FakeArena();
        var sim = new WarfrontSquadSim(r, arena);
        var campaign = new WarfrontCampaign(1UL);
        campaign.Join(1UL, "A", out _);
        var ledger = new WarfrontXpLedger(campaign);
        sim.Land(1UL, 1, Vector3.Zero);
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        arena.Add(new Vector3(10, 0, 0), 100f);
        int paid = 0;
        for (int k = 0; k < Rate * 3; k++)
        {
            sim.Step(++_t, [Owner(Vector3.Zero)]);
            foreach (var kill in sim.Kills) paid += ledger.Kill(kill.Key, 10, WarfrontKillClass.Ordinary, kill.Enemy).Count;   // the squad's own list only
        }
        Assert.Equal(1, paid);
        Assert.Equal(10, campaign.Player(1UL)!.Xp);
    }

    // ---- the walking graph: a real NavGraph with buildings

    static (NavGraph Nav, MusouSim Sim, MusouSquadArena Arena) World(Action<NavigationTests.Level> build)
    {
        var level = new NavigationTests.Level().Floor(-50f, -50f, 50f, 50f);
        build(level);
        var mesh = level.Mesh();
        var nav = NavGraph.Build(mesh, null, new NavSettings());
        var sim = new MusouSim(nav);
        return (nav, sim, new MusouSquadArena(sim, MusouSquadArena.SightOf(new CollisionWorld(mesh, mesh))));
    }

    static bool InBox(Vector3 p, float x0, float y0, float x1, float y1, float margin) => p.X > x0 - margin && p.X < x1 + margin && p.Y > y0 - margin && p.Y < y1 + margin;

    [Fact]
    public void A_Marine_never_ends_a_tick_off_the_walking_graph_among_buildings()
    {
        var (nav, enemies, arena) = World(l => { l.Box(new Vector3(-12, -12, 0), new Vector3(-4, -4, 6)); l.Box(new Vector3(4, -3, 0), new Vector3(14, 9, 6)); l.Box(new Vector3(-3, 10, 0), new Vector3(3, 22, 6)); });
        var rosters = new SquadRosters(); rosters.AddOwner(1UL); for (int i = 0; i < 8; i++) rosters.Recruit(1UL);
        var squad = new WarfrontSquadSim(rosters, arena);
        squad.BeginBattle([(1UL, 1, new Vector3(-20, 0, 0))]);
        var rng = new Random(4);
        for (int i = 0; i < 20; i++) enemies.Spawn(MusouEnemyType.Boy, new Vector3(rng.Next(-40, 40), rng.Next(-40, 40), 0), 0f, 85f);
        var owner = new Vector3(-20, 0, 0);
        var route = new[] { new Vector3(0, 0, 0), new Vector3(0, 30, 0), new Vector3(20, 30, 0), new Vector3(20, -20, 0), new Vector3(-20, -20, 0) };
        int leg = 0; uint t = 0;
        for (int k = 0; k < Rate * 60; k++)
        {
            var d = route[leg] - owner;
            if (d.Length() < 1f) leg = (leg + 1) % route.Length; else owner += Vector3.Normalize(d) * (4f / Rate);
            squad.Step(++t, [new SquadHuman(1, owner, true)]);
            for (int i = 0; i < 9; i++)
            {
                var v = squad.View(i)!.Value;
                if (v.State != SquadState.Standing) continue;
                Assert.True(nav.Nearest(v.Position, 0.5f, 1f) >= 0, $"tick {t}: Marine {i} off the graph at {v.Position}");
                Assert.False(InBox(v.Position, -12, -12, -4, -4, 0.2f) || InBox(v.Position, 4, -3, 14, 9, 0.2f) || InBox(v.Position, -3, 10, 3, 22, 0.2f), $"tick {t}: Marine {i} inside a building at {v.Position}");
            }
        }
    }

    [Fact]
    public void Follow_round_a_corner_takes_the_walkable_way_on_one_shared_path()
    {
        var (_, _, arena) = World(l => l.Box(new Vector3(-1, -30, 0), new Vector3(1, 10, 6)));   // a wall between them, open past y = 10
        var rosters = new SquadRosters(); rosters.AddOwner(1UL); for (int i = 0; i < 3; i++) rosters.Recruit(1UL);
        var squad = new WarfrontSquadSim(rosters, arena);
        squad.BeginBattle([(1UL, 1, new Vector3(-10, 0, 0))]);
        var owner = new Vector3(10, 0, 0);
        uint t = 0;
        for (int k = 0; k < Rate * 40; k++)
        {
            squad.Step(++t, [new SquadHuman(1, owner, true)]);
            for (int i = 0; i < 4; i++) Assert.False(InBox(squad.View(i)!.Value.Position, -1, -30, 1, 10, 0.1f), $"Marine {i} in the wall");
        }
        for (int i = 0; i < 4; i++) Assert.True(Vector3.Distance(squad.View(i)!.Value.Position, owner) <= MarineRules.FollowMax + 1.5f, $"Marine {i} at {squad.View(i)!.Value.Position}, paths {arena.PathsBuilt}, path {string.Join(" ", arena.FindPath(squad.View(i)!.Value.Position, owner)!.Select(q => $"({q.X:F0},{q.Y:F0})"))}");
        Assert.InRange(arena.PathsBuilt, 1, 2 * 40 + 2);   // one path per squad, at most twice a second
        Assert.Equal(0, arena.FlowGoals);                    // Follow never builds a flow field
    }

    [Fact]
    public void A_fixed_point_order_uses_the_squads_own_flow_field_round_the_wall()
    {
        var (_, _, arena) = World(l => l.Box(new Vector3(-1, -30, 0), new Vector3(1, 10, 6)));
        var rosters = new SquadRosters(); rosters.AddOwner(1UL); for (int i = 0; i < 3; i++) rosters.Recruit(1UL);
        var squad = new WarfrontSquadSim(rosters, arena);
        squad.BeginBattle([(1UL, 1, new Vector3(-10, 0, 0))]);
        var point = new Vector3(20, 0, 0);
        Assert.True(squad.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0, point)));
        uint t = 0;
        for (int k = 0; k < Rate * 40; k++)
        {
            squad.Step(++t, [new SquadHuman(1, new Vector3(-10, 0, 0), true)]);
            for (int i = 0; i < 4; i++) Assert.False(InBox(squad.View(i)!.Value.Position, -1, -30, 1, 10, 0.1f), $"Marine {i} in the wall");
        }
        for (int i = 0; i < 4; i++) Assert.InRange(Vector3.Distance(squad.View(i)!.Value.Position, point), 0f, 4f);
        Assert.Equal(0, arena.PathsBuilt);
        Assert.InRange(arena.FlowGoals, 1, 4);
    }
}
