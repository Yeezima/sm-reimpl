using System.Numerics;
using SmContent;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>Musou Mode co-op (the owner, 2026-11-06: 4 players on a locally hosted server): the cap of 4 and the team's rules, through the real server and World.</summary>
public class TfwCoopTests
{
    const int Human = TfwRig.Human;
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0);

    [Fact]
    public void Musou_caps_a_match_at_4_players_and_every_other_profile_keeps_the_server_s_cap()
    {
        Assert.Equal(4, TfwCoop.MaxHumans);
        Assert.Equal(4, TfwCoop.PlayerCap(ContentExtras.Musou, Protocol.MaxPlayers));
        Assert.Equal(2, TfwCoop.PlayerCap(ContentExtras.Musou, 2));                     // a lower cap of the host stands
        Assert.Equal(Protocol.MaxPlayers, TfwCoop.PlayerCap(ContentExtras.None, Protocol.MaxPlayers));
        Assert.Equal(Protocol.MaxPlayers, TfwCoop.PlayerCap(ContentExtras.All, Protocol.MaxPlayers));
        Assert.Equal(TfwCoop.MaxHumans, MusouLocal.MaxPlayers);                          // the front end's texts say the server's number
        Assert.Contains($"{MusouLocal.MaxPlayers} players", MusouLocal.CoopNote);
        Assert.True(TfwShip.Spawns.Length >= TfwCoop.MaxHumans, "the Stormbird has a spawn place for each human");
        Assert.Equal(TfwCoop.MaxHumans, Enumerable.Range(0, TfwCoop.MaxHumans).Select(TfwCoop.LandingOffset).Distinct().Count());
    }

    [Fact]
    public void A_Musou_server_admits_4_players_and_refuses_the_fifth_with_the_cap_named()
    {
        var config = new SimConfig { Extras = ContentExtras.Musou };
        var h = new Harness(Link, seed: 1, clients: 4, config: config);
        Assert.Equal(4, h.Server.PlayerCap);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        var fifth = h.AddClient(nonce: 555);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Rejected, fifth.Status);
        Assert.Equal(RejectReason.ServerFull, fifth.RejectedBecause);
        Assert.Contains("4 players", fifth.RejectDetail);
        Assert.Equal(4, h.Server.Players.Count);
        // A place freed is a place taken: one leaves, another joins.
        h.Clients[0].Leave();
        h.Run(TimeSpan.FromSeconds(1));
        var next = h.AddClient(nonce: 556);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, next.Status);
        Assert.Equal(4, h.Server.Players.Count);
    }

    [Fact]
    public void A_classic_server_keeps_its_cap_of_16()
    {
        var h = new Harness(Link, seed: 1, clients: 5);
        Assert.Equal(Protocol.MaxPlayers, h.Server.PlayerCap);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.Equal(5, h.Server.Players.Count);
    }

    static World FourHumans()
    {
        var w = TfwRig.War();
        for (int id = 2; id <= 4; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Steps(w, 2);
        return w;
    }

    [Fact]
    public void One_deploy_lands_all_4_humans_apart_by_the_landing_zone_with_two_allied_marines_in_all()
    {
        var w = FourHumans();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        Assert.Equal([1, 2, 3, 4], run.InField.ToArray());
        var places = run.InField.Select(id => w.Players[id].Position).ToList();
        foreach (var p in places) Assert.True(Vector3.Distance(p with { Z = 0f }, TfwTestArena.LandingZone) < 6f, $"{p}");
        for (int a = 0; a < places.Count; a++)
            for (int b = a + 1; b < places.Count; b++)
                Assert.True(Vector3.Distance(places[a], places[b]) > 2f, $"humans {a + 1} and {b + 1} stand on each other");
        foreach (int id in run.InField)
        {
            Assert.Equal(Team.SpaceMarines, w.Players[id].Team);
            Assert.True(w.TfwOwn(id).Deployed);
        }
        Assert.Equal(AllyRules.Count, run.Marines.Count);                    // the owner, 2026-11-08: two, whatever the number of players
        Assert.Equal(0, run.LateJoins);
    }

    [Fact]
    public void A_human_who_joins_during_a_battle_is_flown_in_at_the_landing_zone_and_brings_no_marine()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        Assert.Equal([Human], run.InField.ToArray());
        w.Spawn(2, w.AssignClass(0));                                       // joins mid-battle: the core spawns it in the Stormbird
        TfwRig.Steps(w, 2);
        Assert.Contains(2, run.InField);
        Assert.Equal(1, run.LateJoins);
        Assert.True(w.TfwOwn(2).Deployed);
        Assert.True(Vector3.Distance(w.Players[2].Position with { Z = 0f }, TfwTestArena.LandingZone) < 6f, $"{w.Players[2].Position}");
        Assert.False(TfwTestArena.Hub.Contains(w.Players[2].Position));
        Assert.Equal(AllyRules.Count, run.Marines.Count);
        TfwRig.Steps(w, 30);
        Assert.Equal(1, run.LateJoins);                                     // once
    }

    [Fact]
    public void A_human_who_joins_while_the_team_is_picked_up_waits_in_the_Stormbird()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        TfwRig.Command(w, TfwCommand.RequestPickup);
        TfwRig.Steps(w, 2);
        Assert.Equal(TfwRunPhase.Pickup, run.Phase);
        w.Spawn(2, w.AssignClass(0));
        TfwRig.Steps(w, 2);
        Assert.DoesNotContain(2, run.InField);
        Assert.Equal(0, run.LateJoins);
    }

    [Fact]
    public void While_one_human_still_fights_a_fallen_one_waits_and_no_extraction_runs()
    {
        var w = FourHumans();
        var run = w.Tfw!;
        TfwRig.Deploy(w);
        foreach (int id in new[] { 1, 2, 3 }) Assert.True(w.TfwKillForTests(id));
        TfwRig.Seconds(w, TfwBattleRules.ExtractionPromptSeconds + 1f);
        Assert.Equal(TfwRunPhase.Battle, run.Phase);
        Assert.Equal(0, run.ExtractionTicks);
        foreach (int id in new[] { 1, 2, 3 }) Assert.False(w.Players[id].Alive);   // held: the run brings them back, not the core's timer
        Assert.True(w.TfwKillForTests(4));                                  // the last one falls: now the extraction's prompt runs
        TfwRig.Steps(w, 2);
        Assert.True(run.ExtractionTicks > 0);
    }
}
