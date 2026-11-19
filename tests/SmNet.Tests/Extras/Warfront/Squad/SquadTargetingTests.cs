using System.Numerics;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Squad;

/// <summary>Retainer AI slice S1: sticky targets with a hysteresis margin, and threats to the owner or a downed Marine come first.</summary>
public class SquadTargetingTests
{
    [Fact]
    public void The_pure_rule_holds_a_target_a_second_and_then_needs_a_clear_margin()
    {
        Assert.False(SquadTargeting.PreferNew(0.3f, 10f, false, 8f, false));    // a little nearer: kept in the hold
        Assert.True(SquadTargeting.PreferNew(0.3f, 10f, false, 4f, false));     // much nearer: switch even in the hold
        Assert.False(SquadTargeting.PreferNew(2f, 10f, false, 8f, false));      // 80 %: not enough after the hold
        Assert.True(SquadTargeting.PreferNew(2f, 10f, false, 7f, false));       // 70 %: switch
        Assert.True(SquadTargeting.PreferNew(0.1f, 10f, false, 20f, true));     // a threat to the owner wins, however far
        Assert.False(SquadTargeting.PreferNew(5f, 10f, true, 1f, false));       // a held threat is not dropped for a nearer harmless one
    }

    [Fact]
    public void Threats_are_the_Orks_near_the_owner_or_a_downed_Marine()
    {
        Vector3[] guarded = [new(0, 0, 0), new(30, 0, 0)];
        Assert.True(SquadTargeting.Threatens(new Vector3(5, 0, 0), guarded));
        Assert.True(SquadTargeting.Threatens(new Vector3(30, 5, 0), guarded));
        Assert.False(SquadTargeting.Threatens(new Vector3(15, 0, 0), guarded));
        Assert.False(SquadTargeting.Threatens(new Vector3(0, 0, 0), []));
    }

    static (FakeArena Arena, WarfrontSquadSim Sim) Make()
    {
        var rosters = new SquadRosters(); rosters.AddOwner(1);
        var arena = new FakeArena();
        // a slow, weak weapon so nothing dies: the target choice is what is read (the view's Target)
        rosters.Mirror(1, new SquadLoadout { Weapon = new SquadWeapon(40f, 0.001f, 5f, 1f), HealthMax = 1000f, ArmourMax = 0f });
        var sim = new WarfrontSquadSim(rosters, arena);
        sim.BeginBattle([(1UL, 1, Vector3.Zero)]);
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        return (arena, sim);
    }

    static uint _t = 1000;
    static void Run(WarfrontSquadSim sim, int ticks, Vector3 owner) { for (int k = 0; k < ticks; k++) sim.Step(++_t, [new SquadHuman(1, owner, true)]); }
    static int Targeted(WarfrontSquadSim sim) => sim.View(0)!.Value.Target;

    [Fact]
    public void A_target_is_kept_when_another_Ork_is_only_slightly_nearer_and_dropped_for_a_much_nearer_one()
    {
        var (arena, sim) = Make();
        var away = new Vector3(0, -80, 0);   // the owner is far from every Ork: no threat
        var first = arena.Add(new Vector3(20, 0, 0), 1e6f);
        Run(sim, 10, away);
        Assert.Equal((int)first.Id, Targeted(sim));
        arena.Add(new Vector3(18, 0, 0), 1e6f);   // a little nearer: a flip-flopping bot would switch
        Run(sim, 9, away);                          // under a second, three retargets
        Assert.Equal((int)first.Id, Targeted(sim));
        var muchNearer = arena.Add(new Vector3(8, 0, 0), 1e6f);
        Run(sim, 6, away);
        Assert.Equal((int)muchNearer.Id, Targeted(sim));
    }

    [Fact]
    public void An_Ork_on_the_owner_is_preferred_to_a_nearer_one_away_from_him()
    {
        var (arena, sim) = Make();
        var harmless = arena.Add(new Vector3(-10, 0, 0), 1e6f);
        var threat = arena.Add(new Vector3(20, 5, 0), 1e6f);   // within 6 m of the owner at (22, 5)
        Run(sim, 10, new Vector3(22, 5, 0));
        Assert.NotEqual((int)harmless.Id, Targeted(sim));
        Assert.Equal((int)threat.Id, Targeted(sim));
    }

    [Fact]
    public void Without_a_threat_the_nearest_Ork_is_still_chosen()
    {
        var (arena, sim) = Make();
        arena.Add(new Vector3(20, 0, 0), 1e6f);
        var near = arena.Add(new Vector3(-8, 0, 0), 1e6f);
        Run(sim, 10, new Vector3(0, -80, 0));
        Assert.Equal((int)near.Id, Targeted(sim));
    }
}
