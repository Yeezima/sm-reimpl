using SmContent.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmNet.Tests.Extras.Warfront.Wiring;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Chaos.Finale;
using SmSim.Extras.Warfront.Faction;
using SmSim.Extras.Warfront.Fortress;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>
/// The duel's bench with the real squad wiring (series-37 integration; Fable review of 8f885525): Phase B T2's retainers are World players, so when the last Chosen falls the relay's isolation
/// dismisses them with the other puppets and the bench (<c>GameServer.WarfrontChaosDuel.cs</c>: <c>WarfrontRetainers.EndBattle</c>) clears their battle records; none comes back through a handoff
/// (the squads' step reads <see cref="World.WarfrontDuelHoldsSquads"/>, <c>WarfrontSquadWiring.OnSquadStep</c>), and they land again at the next battle. A squad that is all downed is benched too.
/// </summary>
public class DuelBenchWiringTests
{
    /// <summary>A Chaos fortress of <paramref name="humans"/> humans with two retainers each, the real wiring and the server's bench, landed and stepped.</summary>
    static WarfrontSquadWiringTests.Rig ChaosFortress(int humans)
    {
        var r = WarfrontSquadWiringTests.Rig.Make(humans: humans, marines: 2, deploy: false, config: WarfrontChaosTuning.Apply(WarfrontSquadWiringTests.Cfg(), WarfrontFaction.Chaos));
        r.W.WarfrontDuelBenchRetainers = () => r.Wi.Retainers.EndBattle();   // as GameServer.WarfrontChaosDuel.cs wires it
        Land(r);
        return r;
    }

    static void Land(WarfrontSquadWiringTests.Rig r)
    {
        r.W.Tfw!.Warfront!.Fortress = WarfrontFortressSetup.For(new WarfrontFortressBattleTests.StubArena());
        r.W.Tfw.War.SetFrontsForTests(WarRules.FrontStronghold);
        r.Battle = TfwRig.Deploy(r.W);
        r.Stand(3);   // the humans land and the squads with them (no Clear: the Chosen and the Lord stay)
        Assert.True(r.Battle.ChaosFortress);
    }

    static int Puppets(World w) => w.Players.Keys.Count(TfwContracts.IsPuppet);

    [Fact]
    public void The_last_Chosen_benches_every_retainer_no_puppet_survives_a_handoff_and_they_land_at_the_next_battle()
    {
        var r = ChaosFortress(humans: 2);
        Assert.Equal(4, r.Ret.All.Count);
        Assert.Equal(4, Puppets(r.W));
        FinaleRig.KillChosen(r.W, r.Battle);
        var duel = Assert.IsType<WarfrontDuel>(r.W.WarfrontChaosDuel);
        Assert.Equal(WarfrontDuelPhase.Duel, duel.Phase);
        Assert.True(r.W.WarfrontDuelHoldsSquads);
        Assert.Equal(0, Puppets(r.W));
        Assert.Empty(r.Ret.All);

        // the first challenger falls: the handoff and the next duel bring no retainer back
        int first = r.W.WarfrontDuelist;
        Assert.True(r.W.TfwKillForTests(first));
        r.Stand(1);
        Assert.Equal(WarfrontDuelPhase.Handoff, duel.Phase);
        r.Stand(30 * 3 + 2);
        Assert.Equal(WarfrontDuelPhase.Duel, duel.Phase);
        Assert.NotEqual(first, r.W.WarfrontDuelist);
        Assert.Equal(0, Puppets(r.W));
        Assert.Empty(r.Ret.All);

        // the battle is left; the next one lands the squads again
        r.W.Tfw!.PickupForTests(r.W);
        Assert.Equal(0, Puppets(r.W));
        Land(r);
        Assert.Equal(4, r.Ret.All.Count);
        Assert.Equal(4, Puppets(r.W));
    }

    [Fact]
    public void A_squad_that_is_all_downed_is_benched_too_so_no_downed_record_outlives_its_World_player()
    {
        var r = ChaosFortress(humans: 1);
        r.DownMarine(1, 0);
        r.DownMarine(1, 1);
        Assert.False(r.Ret.AnyStanding);
        Assert.Equal(2, r.Ret.All.Count);
        FinaleRig.KillChosen(r.W, r.Battle);
        Assert.True(r.W.WarfrontDuelHoldsSquads);
        Assert.Equal(0, Puppets(r.W));
        Assert.Empty(r.Ret.All);   // before the review fix the bench waited for a standing retainer: the two Downed records stayed with dead World ids
    }
}
