using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>The G0 harness itself (Guardsmen baseline, 2026-11-26): the five scenarios run on the flat test arena, are seeded and repeatable, and do what their names say. Not a balance test: the numbers are the baseline's job.</summary>
public class WarfrontLineTrialTests
{
    static World Ready()
    {
        var w = TfwRig.War(config: WarfrontBattleTests.WarfrontConfig);
        TfwRig.Deploy(w);
        WarfrontLineTrial.Prepare(w, 8);
        return w;
    }

    [Fact]
    public void Prepare_secures_every_point_and_leaves_a_line_standing()
    {
        var w = Ready();
        var b = w.Tfw!.Battle!;
        Assert.All(b.Objectives.Take(3), o => Assert.Equal(TfwPositionState.Secured, o.State));
        Assert.True(w.TfwGuards!.Alive > 0);
        Assert.True(w.TfwGuards.SquadsAlive > 0);
    }

    [Theory]
    [InlineData("routine")] [InlineData("elite")] [InlineData("flank")] [InlineData("collapse")] [InlineData("break")]
    public void Every_scenario_runs_and_reports_a_consistent_result(string scenario)
    {
        var r = WarfrontLineTrial.Run(Ready(), scenario, 1, 0, 40);
        Assert.Equal(scenario, r.Scenario);
        Assert.InRange(r.Seconds, 1, 40);
        Assert.Contains(r.Outcome, new[] { "HELD", "POINT-LOST", "LOST-AND-RETAKEN", "WIPED" });
        Assert.InRange(r.PointsLost, 0, 3);
        Assert.InRange(r.PointsSecured, 0, 3);
        Assert.True(r.GuardsmenStart > 0);
        Assert.True(r.GuardsmenMin <= r.GuardsmenStart);
        Assert.True(r.OrksPeak > 0, "the wave made Orks");
        Assert.Equal(16, r.Hash.Length);
    }

    [Fact]
    public void The_same_seed_is_the_same_run_and_another_seed_moves_the_wave()
    {
        var a = WarfrontLineTrial.Run(Ready(), "routine", 5, 0, 20);
        var b = WarfrontLineTrial.Run(Ready(), "routine", 5, 0, 20);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.WaveSize, b.WaveSize);
        Assert.Equal(a.GuardsmenEnd, b.GuardsmenEnd);
        var sizes = Enumerable.Range(1, 6).Select(s => WarfrontLineTrial.Run(Ready(), "routine", s, 0, 10).WaveSize).Distinct().Count();
        Assert.True(sizes >= 3, "the seed moves the wave's size");
    }

    [Fact]
    public void Elite_and_collapse_waves_carry_more_elites_than_the_routine_one()
    {
        var routine = WarfrontLineTrial.Run(Ready(), "routine", 2, 0, 10);
        var elite = WarfrontLineTrial.Run(Ready(), "elite", 2, 0, 10);
        var collapse = WarfrontLineTrial.Run(Ready(), "collapse", 2, 0, 10);
        Assert.True(elite.WaveElites > routine.WaveElites * 3);
        Assert.True(collapse.WaveSize > elite.WaveSize * 1.5);
    }

    [Fact]
    public void The_flank_scenario_adds_a_second_ork_army_from_another_bearing()
    {
        var w = Ready();
        var b = w.Tfw!.Battle!;
        int before = b.ArmyList.Count;
        WarfrontLineTrial.Run(w, "flank", 3, 0, 2);
        Assert.True(b.ArmyList.Count(a => a.Side == TfwArmySide.Ork) >= 2, "two Ork armies in the flank scenario");
        Assert.True(b.ArmyList.Count >= before + 2);
        var w2 = Ready();
        WarfrontLineTrial.Run(w2, "routine", 3, 0, 2);
        Assert.Equal(1, w2.Tfw!.Battle!.ArmyList.Count(a => a.Side == TfwArmySide.Ork && a.Goal == 0));
    }

    [Fact]
    public void An_unknown_scenario_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WarfrontLineTrial.Run(Ready(), "bogus", 1));
    }
}
