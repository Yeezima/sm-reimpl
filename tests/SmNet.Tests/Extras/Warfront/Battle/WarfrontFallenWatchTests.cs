using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Fortress;
using SmNet.Tests.Extras.Warfront.Hq;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Coop;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Battle;

/// <summary>
/// Fallen-watch fix, second pass: a fallen human's Redeploy is refused and costs nothing (the death texts "KILLED" and "ENTER: DEPLOY ... (1 SUPPLY)" are hidden on the client); the default watch of a fallen player in the HQ defence and in the fortress
/// (the leader or a living teammate, and none when every candidate is down: the client then shows "WATCHING THE SQUAD" on the own body).
/// </summary>
public class WarfrontFallenWatchTests
{
    const int Window = 750, Spectate = 1800;   // ticks at 30 Hz: 25 s and 60 s
    const ulong A = 1, B = 2;

    sealed class Host : IWarfrontCoopHost
    {
        public bool HasRoster(ulong key) => true;
        public void AddStartingRetainer(ulong key) { }
        public void HoldSquad(ulong key) { }
        public void RebindSquad(ulong key, int player) { }
        public void Save() { }
    }

    static void Stand(World w, int ticks) { for (int k = 0; k < ticks; k++) w.Step(_ => default); }

    [Fact]
    public void A_fallen_humans_redeploy_is_refused_in_the_window_and_while_spectating_and_spends_no_supply()
    {
        var w = WarfrontBattleTests.War(2);
        w.Tfw!.Warfront!.AnyRetainerStanding = () => true;
        TfwRig.Deploy(w);
        w.Musou!.Frozen = true;
        var run = w.Tfw;
        int supplies = run.Supplies, transport = run.Transport;
        Assert.True(w.TfwKillForTests(1));
        ushort seq = 50;
        foreach (int ticks in new[] { 60, Window })   // inside the window, then (after it) as a spectator
        {
            Stand(w, ticks);
            Assert.True(run.Enqueue(1, new TfwCommandRecord(seq++, TfwCommand.Redeploy, 0)));
            Stand(w, 30);
            Assert.False(w.Players[1].Alive);
            Assert.Equal(supplies, run.Supplies);
            Assert.Equal(transport, run.Transport);
        }
        Assert.Equal(WarfrontDownPhase.Spectator, run.WarfrontDownState(1).Phase);
    }

    /// <summary>The leader (A, player 1) falls and spectates; B (player 2) stands, then falls: the watch goes from B to none.</summary>
    static void WatchRun(World w)
    {
        w.Tfw!.Warfront!.AnyRetainerStanding = () => true;   // keeps the battle from ending as a wipe while both are down
        var campaign = new WarfrontCampaign(7);
        var coop = new WarfrontCoop(campaign, new Host());
        coop.Connect(A, "A", 1);
        coop.Connect(B, "B", 2);
        Assert.True(w.TfwKillForTests(1));
        Stand(w, Window + 30);
        Assert.Equal(WarfrontDownPhase.Spectator, w.Tfw.WarfrontDownState(1).Phase);
        IEnumerable<int> Living() => new[] { 1, 2 }.Where(i => w.Players[i].Alive);
        // B stands: the fallen leader watches the other human (no Marine stands in this double)
        Assert.True(coop.DefaultWatch(A, true, WarfrontCoop.Candidates(Living(), [])));
        Assert.Equal(new WarfrontWatch(WarfrontWatchKind.Player, 2), coop.WatchOf(A));
        Assert.Equal(new WarfrontWatch(WarfrontWatchKind.Player, 2), coop.ViewFor(A).Watch);
        // a Marine stands too: the human is still first
        Assert.False(coop.DefaultWatch(A, true, WarfrontCoop.Candidates(Living(), [3])));
        // B falls: only the Marine is left, then nobody
        Assert.True(w.TfwKillForTests(2));
        Stand(w, 5);
        Assert.True(coop.DefaultWatch(A, true, WarfrontCoop.Candidates(Living(), [3])));
        Assert.Equal(new WarfrontWatch(WarfrontWatchKind.Marine, 3), coop.WatchOf(A));
        Assert.True(coop.DefaultWatch(A, true, WarfrontCoop.Candidates(Living(), [])));
        Assert.Equal(default, coop.WatchOf(A));
        Assert.Equal(WarfrontWatchKind.None, coop.ViewFor(A).Watch.Kind);   // the client keeps the own body: "WATCHING THE SQUAD", no error
        Assert.Equal(WarfrontDownPhase.Spectator, w.Tfw.WarfrontDownState(1).Phase);
        Assert.Equal(WarfrontDownPhase.Downed, w.Tfw.WarfrontDownState(2).Phase);
        // the respawn ends the spectating: a stale target would not outlive it
        Assert.False(coop.DefaultWatch(A, false, WarfrontCoop.Candidates(Living(), [])));
    }

    [Fact]
    public void A_fallen_player_in_the_hq_defence_watches_a_standing_human_and_none_when_every_candidate_is_down()
    {
        var w = WarfrontHqBattleTests.Hq(out _, humans: 2);
        WatchRun(w);
    }

    [Fact]
    public void A_fallen_player_in_the_fortress_watches_a_standing_human_and_none_when_every_candidate_is_down()
    {
        var (w, battle, _) = WarfrontFortressBattleTests.Fortress(humans: 2);
        Assert.True(battle.IsWarfrontFortress);
        w.Musou!.Frozen = true;
        WatchRun(w);
    }
}
