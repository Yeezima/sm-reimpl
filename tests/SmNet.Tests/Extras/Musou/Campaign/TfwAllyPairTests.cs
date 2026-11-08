using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The owner's allied pair in a battle (2026-11-08: "reduce their total count to 2, and they roam the map together attacking and defending"): two whatever the number of players, landed at the
/// landing zone, no retainer, lord or Defender beside them; they go to a contested position, defend a secured one under attack, keep together, and are back at the landing zone 30 s after a death.
/// </summary>
public class TfwAllyPairTests
{
    const int Human = TfwRig.Human;

    static int Puppets(World w) => w.Players.Keys.Count(TfwContracts.IsPuppet);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_battle_lands_two_allied_marines_whatever_the_number_of_players(int humans)
    {
        var w = TfwRig.War();
        for (int id = 2; id <= humans; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Steps(w, 2);
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        Assert.Equal(humans, run.InField.Count);
        Assert.Equal(AllyRules.Count, run.Marines.Count);
        Assert.Equal(AllyRules.Count, Puppets(w));                              // nothing else without a peer: no retainer, lord or Defender
        foreach (var m in run.Marines)
            Assert.True(MarineBrain.Flat(w.Players[m.Id].Position, battle.Layout.LandingZone) < 8f, $"{m.Name} landed at {w.Players[m.Id].Position}");
        Assert.Equal(1, run.Marines.Count(m => m.Leads));
        TfwRig.Seconds(w, 5f);
        Assert.Equal(AllyRules.Count, Puppets(w));
        w.Spawn(humans + 1, w.AssignClass(0));                                  // a late joiner brings none
        TfwRig.Seconds(w, 1f);
        Assert.Equal(AllyRules.Count, Puppets(w));
    }

    [Fact]
    public void The_pair_goes_together_to_a_contested_position()
    {
        var w = TfwRig.War();
        w.Tfw!.Guardsmen = false;   // the pair alone with the Orks
        var battle = TfwRig.Deploy(w);
        var run = w.Tfw;
        TfwRig.Steps(w, 2);
        Assert.InRange(run.AllyGoalIndex, 0, 2);                                // a position not secured: the attack
        Assert.NotEqual(TfwPositionState.Secured, battle.Objectives[run.AllyGoalIndex].State);
        bool reached = false;
        int together = 0, samples = 0;
        for (int t = 0; t < 90 * 30 && !reached; t++)
        {
            TfwRig.KillAll(w);   // a clear field: this test is of the way and the keeping together (with nobody fighting beside them the test arena's crowds swamp the pair on the way)
            w.Step(_ => default);
            var alive = run.Marines.Where(m => w.Players[m.Id].Alive).ToList();
            if (run.Marines.FirstOrDefault(m => m.Leads) is { } lead && run.AllyGoalIndex is >= 0 and < 3
                && MarineBrain.Flat(w.Players[lead.Id].Position, battle.Layout.Positions[run.AllyGoalIndex]) <= MarineRules.ArriveRadius + 2f) reached = true;
            if (alive.Count == 2 && t > 5 * 30)
            {
                samples++;
                if (MarineBrain.Flat(w.Players[alive[0].Id].Position, w.Players[alive[1].Id].Position) <= AllyRules.LeaderWait + 4f) together++;
            }
        }
        Assert.True(reached, "the pair's leader reached the contested position: " + string.Join("; ", run.Marines.Select(m => $"{m.Name} lead {m.Leads} alive {w.Players[m.Id].Alive} at {w.Players[m.Id].Position} deaths {w.Scores.GetValueOrDefault(m.Id).Deaths}")) + $"; goal {run.AllyGoalIndex} {run.AllyGoal}");
        Assert.True(samples == 0 || together >= samples * 0.9f, $"together {together} of {samples} samples");
    }

    [Fact]
    public void The_pair_defends_a_secured_position_under_attack_and_goes_back_to_the_attack_after()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        TfwRig.Secure(w, battle, 0);
        TfwRig.KillAll(w);
        var from = battle.Layout.LandingZone;
        int attack = battle.AllyGoal(from, -2);
        Assert.InRange(attack, 1, 2);                                           // nobody presses the secured one: the attack goes on
        for (int k = 0; k < 10; k++) w.Musou!.Spawn(MusouEnemyType.Boy, battle.Layout.Positions[0] + new Vector3(8f + k, 6f, 0f), 0f, 85f);
        Assert.Equal(0, battle.AllyGoal(from, attack));                         // ten Orks on it: they go and defend it
        TfwRig.KillAll(w);
        for (int k = 0; k < 5; k++) w.Musou!.Spawn(MusouEnemyType.Boy, battle.Layout.Positions[0] + new Vector3(8f + k, 6f, 0f), 0f, 85f);
        Assert.Equal(0, battle.AllyGoal(from, 0));                              // half the pressure keeps them there
        TfwRig.KillAll(w);
        Assert.InRange(battle.AllyGoal(from, 0), 1, 2);                         // none: back to the attack
        Assert.Equal(attack, battle.AllyGoal(from, attack));                    // the contested one under way is kept
    }

    [Fact]
    public void A_dead_marine_of_the_pair_is_back_at_the_landing_zone_30_s_later()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        var run = w.Tfw!;
        var one = run.Marines[0];
        Assert.True(w.TfwKillForTests(one.Id));
        TfwRig.Seconds(w, AllyRules.RespawnSeconds - 0.5f);
        Assert.False(w.Players[one.Id].Alive);
        TfwRig.Seconds(w, 1f);
        Assert.True(w.Players[one.Id].Alive);
        Assert.True(MarineBrain.Flat(w.Players[one.Id].Position, battle.Layout.LandingZone) < 8f, $"{w.Players[one.Id].Position}");   // back at the landing zone (half a second on his way)
        Assert.Equal(AllyRules.Count, run.Marines.Count);
    }
}
