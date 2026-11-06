using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The late batch's run flow (the owner, 2026-11-06, stream D; <c>TfwRun.Flow.cs</c>, <c>TfwBattle.Victory.cs</c>): a fallen human deploys again at the landing zone for a supply, the host calls
/// the Stormbird back (survive 20 s, then the hub), and a giant Ork wave comes from every side after the Warboss of a battle falls.
/// </summary>
public class TfwFlowTests
{
    const int Human = TfwRig.Human, Second = 2;

    static void Command(World w, int player, TfwCommand command, ushort sequence = 1) => Assert.True(w.Tfw!.Enqueue(player, new TfwCommandRecord(sequence, command, 0)));

    // ---------------------------------------------------------------- the deploy after death

    [Fact]
    public void A_fallen_human_deploys_again_at_its_landing_place_for_one_supply()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        var battle = TfwRig.Deploy(w);
        var landed = w.Players[Human].Position;
        TfwRig.Teleport(w, battle.Layout.Positions[1]);
        TfwRig.Steps(w, 2);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.False(w.Players[Human].Alive);
        Assert.True(run.ExtractionTicks > 0);
        Command(w, Human, TfwCommand.Redeploy);
        TfwRig.Steps(w, 2);
        var p = w.Players[Human];
        Assert.True(p.Alive);
        Assert.True(Vector3.Distance(p.Position, landed) < 0.5f, $"{p.Position} against {landed}");
        Assert.Equal(TfwBattleRules.StartSupplies - TfwBattleRules.RedeployCost, run.Supplies);
        Assert.Equal((TfwRunPhase.Battle, 0, 1), (run.Phase, run.ExtractionTicks, run.Redeploys));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 1f);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);                           // no extraction came: the player fights on
    }

    [Fact]
    public void A_redeploy_is_refused_alive_in_the_stormbird_and_without_supplies()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        Command(w, Human, TfwCommand.Redeploy);                                 // in the hub
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwBattleRules.StartSupplies, run.Supplies);
        TfwRig.Deploy(w);
        Command(w, Human, TfwCommand.Redeploy, 2);                              // alive
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwBattleRules.StartSupplies, 0), (run.Supplies, run.Redeploys));
        typeof(TfwRun).GetProperty(nameof(TfwRun.Supplies))!.SetValue(run, 0);
        Assert.True(w.TfwKillForTests(Human));
        Command(w, Human, TfwCommand.Redeploy, 3);
        TfwRig.Steps(w, 2);
        Assert.False(w.Players[Human].Alive);
        Assert.Equal(0, run.Redeploys);
    }

    [Fact]
    public void The_hud_offers_the_deploy_only_to_the_fallen_in_the_battle()
    {
        var own = new TfwOwnView(0, 0, 0, 3, 0, false, TfwRetainerState.None, MarineOrder.Follow, 0, 0, 0, 0, 0, Deployed: true);
        var war = new TfwWarView(TfwRunPhase.Battle, TfwLane.A, 0f, 1f, 4, 2, 0, default, default, default, own);
        Assert.Equal("ENTER: DEPLOY AT THE LANDING ZONE (1 SUPPLY)", TfwHudModel.RedeployText(war, alive: false));
        Assert.Equal("A: DEPLOY AT THE LANDING ZONE (1 SUPPLY)", TfwHudModel.RedeployText(war, alive: false, pad: true));
        Assert.Equal("", TfwHudModel.RedeployText(war, alive: true));
        Assert.Equal("NO SUPPLIES TO DEPLOY AGAIN", TfwHudModel.RedeployText(war with { Supplies = 0 }, alive: false));
        Assert.False(TfwHudModel.CanRedeploy(war with { Supplies = 0 }, alive: false));
        Assert.Equal("", TfwHudModel.RedeployText(war with { Phase = TfwRunPhase.Hub }, alive: false));
        Assert.Equal("", TfwHudModel.RedeployText(war with { Own = own with { Deployed = false } }, alive: false));
    }

    // ---------------------------------------------------------------- the host's recall

    [Fact]
    public void The_host_is_the_lowest_human_id()
    {
        Assert.Equal(1, TfwRun.HostOf([TfwContracts.PuppetIdFirst, 3, 1, 2]));
        Assert.Equal(-1, TfwRun.HostOf([TfwContracts.PuppetIdFirst]));
        Assert.True(TfwHudModel.IsHost(2, [5, 2, TfwContracts.PuppetIdLast]));
        Assert.False(TfwHudModel.IsHost(5, [5, 2]));
    }

    static World TwoHumans(out TfwBattle battle)
    {
        var w = TfwRig.War();
        w.Spawn(Second, w.AssignClass(0));
        TfwRig.Steps(w, 30);
        battle = TfwRig.Deploy(w);
        Assert.Equal([Human, Second], w.Tfw!.InField);
        return w;
    }

    [Fact]
    public void The_hosts_recall_takes_20_s_is_free_survives_a_death_and_brings_everyone_home()
    {
        var w = TwoHumans(out var battle);
        var run = w.Tfw!;
        int transport = run.Transport;
        Command(w, Second, TfwCommand.ReturnToShip);                            // not the host
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Command(w, Human, TfwCommand.ReturnToShip, 2);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        Assert.True(run.Recalling);
        Assert.True(w.TfwKillForTests(Second));                                 // a death does not call it off
        Command(w, Second, TfwCommand.CancelPickup, 3);                         // nor does anyone but the host
        TfwRig.Seconds(w, 1f);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        TfwRig.Seconds(w, TfwBattleRules.RecallSeconds - 1.5f);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        TfwRig.Seconds(w, 0.7f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.Equal(transport, run.Transport);                                 // free
        Assert.All([Human, Second], id => Assert.True(w.Players[id].Alive && TfwRig.InStormbird(w.Players[id].Position)));
        Assert.Equal((float)WarRules.LeftProgress(battle.Secured), run.War.LaneState(TfwLane.A).Progress, 4);   // a battle not won counts as left
        Assert.False(run.Recalling);
    }

    [Fact]
    public void The_host_can_call_the_recall_off_and_a_pickup_then_runs_as_ever()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        Command(w, Human, TfwCommand.ReturnToShip);
        TfwRig.Steps(w, 2);
        Assert.True(run.Recalling);
        Command(w, Human, TfwCommand.CancelPickup, 2);
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Battle, false), (run.Phase, run.Recalling));
        Command(w, Human, TfwCommand.RequestPickup, 3);                         // a plain pickup: a death cancels it again
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
    }

    [Fact]
    public void The_recall_is_refused_in_the_stormbird()
    {
        var w = TfwRig.War();
        Command(w, Human, TfwCommand.ReturnToShip);
        TfwRig.Steps(w, 2);
        Assert.Equal((TfwRunPhase.Hub, false), (w.Tfw!.Phase, w.Tfw.Recalling));
    }

    // ---------------------------------------------------------------- the wave after the Warboss

    [Fact]
    public void The_warbosss_fall_brings_a_giant_wave_from_every_side()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        TfwRig.Teleport(w, battle.Layout.Hq);
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 2);
        Assert.True(battle.Won);
        Assert.Equal(VictoryWaveRules.Size - VictoryWaveRules.PerTick, battle.VictoryWaveLeft);   // the tick after the win sent its first
        var living = w.Players.Values.Where(p => p.Alive).Select(p => p.Position).ToArray();
        var centre = living.Aggregate(Vector3.Zero, (a, b) => a + b) / living.Length;   // the living players' centre, the retainer too
        var before = Ids(w);
        TfwRig.Seconds(w, 0.2f);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        var sectors = new int[8];
        foreach (var e in all.Take(n).Where(e => e.Alive && !before.Contains(e.Id)))
        {
            var d = e.Position - centre;
            Assert.InRange(new Vector2(d.X, d.Y).Length(), VictoryWaveRules.Radius - 8f, VictoryWaveRules.Radius + 1f);   // spawned on the ring (they have walked a little)
            float bearing = MathF.Atan2(d.Y, d.X);
            sectors[(int)((bearing + MathF.PI) / MathF.Tau * 8f) % 8]++;
        }
        Assert.All(sectors, c => Assert.True(c > 0, string.Join(",", sectors)));   // every side
        TfwRig.Seconds(w, 2f);
        Assert.Equal((0, VictoryWaveRules.Size), (battle.VictoryWaveLeft, battle.VictoryWaveSent));
    }

    static HashSet<ushort> Ids(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        return all.Take(n).Select(e => e.Id).ToHashSet();
    }

    [Fact]
    public void The_final_assaults_warboss_brings_no_wave_and_the_switch_holds_it_back()
    {
        var w = TfwRig.War();
        var final = new TfwBattle(w.Musou!, w.TfwGuards!, TfwTestArena.Layout(), 100.0, 1, final: true);
        final.Start();
        Assert.NotEqual(0, final.BossId);
        TfwRig.KillAll(w);
        final.Step([TfwTestArena.Hq]);
        final.Step([TfwTestArena.Hq]);
        Assert.True(final.Won);
        Assert.Equal((0, 0), (final.VictoryWaveLeft, final.VictoryWaveSent));

        w.Tfw!.VictoryWave = false;
        var battle = TfwRig.Deploy(w);
        for (int k = 0; k < 3; k++) TfwRig.Secure(w, battle, k);
        TfwRig.KillAll(w);
        TfwRig.Steps(w, 3);
        Assert.True(battle.Won);
        Assert.Equal(0, battle.VictoryWaveSent);
    }
}
