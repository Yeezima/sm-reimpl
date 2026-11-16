using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The Musou front pilot's run through a World (review of 2026-11-16): three copies of the synthetic arena side by side at the front's offsets, composed as the front composes its bands (<see cref="TfwMap.ComposeFront"/>). Each battle
/// lands on the band of its node (lane B: node 1, 2, 3, then the final assault on the last), a redeploy and a late join come back on the battle's band, and a new run starts on the first band again.
/// </summary>
public class TfwFrontRunTests
{
    const int Human = TfwRig.Human;

    static Vector3 Off(int k) => new(AshfallFront.OffsetOf(k), 0f, 0f);

    static TfwMapData Front()
    {
        var corners = Enumerable.Range(0, 3).SelectMany(k => TfwTestArena.Solids.Select(s => new TfwSolid(s.Min + Off(k), s.Max + Off(k)))).SelectMany(TfwMap.Triangles).ToArray();
        var mesh = new StaticMesh(corners, Enumerable.Range(0, corners.Length).ToArray());
        var level = new CollisionWorld(mesh, mesh);
        var points = Enumerable.Range(0, 3).SelectMany(k => TfwTestArena.Points.Select(p => p with { Center = p.Center + Off(k) })).ToArray();
        var areas = Enumerable.Range(0, 3).SelectMany(k => TfwTestArena.Areas.Select(a => a with { Center = a.Center + Off(k) })).ToArray();
        var spawns = Enumerable.Range(0, 3).SelectMany(k => new[] { TfwTestArena.LandingZone, TfwTestArena.Hq }.Concat(TfwTestArena.Sources).Select(s => s + Off(k))).ToArray();
        return TfwMap.ComposeFront(level, points, areas, spawns, 3, AshfallFront.BandAt);
    }

    static readonly Lazy<TfwMapData> Data = new(Front);

    static World War()
    {
        var data = Data.Value;
        var w = new World(TfwRig.Config, data.Collision, data.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = data.StartAreas };
        Assert.True(w.StartTfw(null, data.Field));
        w.Tfw!.AllyUp = _ => false;
        w.Tfw.Guardsmen = false;
        w.Tfw.Patrols = false;
        w.TfwMeksOn = false;
        w.Spawn(Human, w.AssignClass(0));
        TfwRig.Steps(w, 30);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    static void OnBand(World w, TfwBattle battle, int band, params int[] ids)
    {
        var run = w.Tfw!;
        Assert.Equal(band, run.ActiveBand);
        Assert.Same(Data.Value.Field.Bands![band], run.Field);
        Assert.Equal(band, AshfallFront.BandAt(battle.Layout.LandingZone.X));
        Assert.All(battle.Layout.Positions, p => Assert.Equal(band, AshfallFront.BandAt(p.X)));
        Assert.Equal(band, AshfallFront.BandAt(battle.Layout.Hq.X));
        foreach (int id in ids)
        {
            Assert.True(w.Players[id].Alive, $"player {id} is down");
            Assert.Equal(band, AshfallFront.BandAt(w.Players[id].Position.X));
            Assert.False(Data.Value.Field.Hub.Contains(w.Players[id].Position));
        }
    }

    /// <summary>Back to the Stormbird and the node won (the war's front moves on, as a won battle moves it).</summary>
    static void WinAndReturn(World w, TfwLane lane)
    {
        w.Tfw!.PickupForTests(w);
        w.Tfw.War.PlayerWon(lane);
        TfwRig.Steps(w, 5);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
    }

    [Fact]
    public void Lane_B_fights_node_1_2_and_3_on_the_three_bands_and_the_final_assault_on_the_last()
    {
        var w = War();
        var run = w.Tfw!;
        Assert.Equal(0, run.ActiveBand);
        Assert.Same(Data.Value.Field, run.WholeField);
        for (int node = 1; node <= 3; node++)
        {
            Assert.Equal(node - 1, run.War.LaneState(TfwLane.B).Front);
            var battle = TfwRig.Deploy(w, TfwLane.B);
            Assert.Equal(TfwRunPhase.Battle, run.Phase);
            OnBand(w, battle, node - 1, Human);
            WinAndReturn(w, TfwLane.B);
        }
        Assert.Equal(WarRules.FrontStronghold, run.War.LaneState(TfwLane.B).Front);
        var final = TfwRig.Deploy(w, TfwLane.B);
        Assert.Equal(TfwRunPhase.FinalAssault, run.Phase);
        OnBand(w, final, 2, Human);
    }

    [Fact]
    public void Lanes_A_and_C_start_on_the_outskirts()
    {
        var w = War();
        OnBand(w, TfwRig.Deploy(w, TfwLane.A), 1, Human);
        WinAndReturn(w, TfwLane.A);
        OnBand(w, TfwRig.Deploy(w, TfwLane.A), 2, Human);
        w.Tfw!.PickupForTests(w);
        TfwRig.Steps(w, 5);
        OnBand(w, TfwRig.Deploy(w, TfwLane.C), 1, Human);
    }

    [Fact]
    public void A_redeploy_and_a_late_join_come_back_on_the_battles_band()
    {
        var w = War();
        var run = w.Tfw!;
        WinAndReturn(w, TfwLane.B);   // lane B's front at 1: the next battle is node 2, the outskirts
        TfwRig.Deploy(w, TfwLane.B);
        var battle = run.Battle!;
        OnBand(w, battle, 1, Human);
        Assert.True(w.TfwRespawnAt(Human, battle.Layout.Positions[2], 0f));
        TfwRig.Steps(w, 2);
        Assert.True(w.TfwKillForTests(Human));
        TfwRig.Steps(w, 2);
        Assert.True(run.Enqueue(Human, new TfwCommandRecord(9, TfwCommand.Redeploy, 0)));
        TfwRig.Steps(w, 2);
        Assert.Equal(1, run.Redeploys);
        OnBand(w, battle, 1, Human);
        w.Spawn(2, w.AssignClass(0));   // the core spawns it in the Stormbird; the run flies it in
        TfwRig.Steps(w, 2);
        Assert.Equal(1, run.LateJoins);
        OnBand(w, battle, 1, Human, 2);
    }

    [Fact]
    public void A_new_run_starts_on_the_first_band_again()
    {
        var w = War();
        var run = w.Tfw!;
        WinAndReturn(w, TfwLane.B);
        WinAndReturn(w, TfwLane.B);
        TfwRig.Deploy(w, TfwLane.B);
        Assert.Equal(2, run.ActiveBand);
        // every human gone: the match goes back to warm-up with a new run
        w.Remove(Human);
        TfwRig.Steps(w, 3);
        Assert.Equal(0, run.ActiveBand);
        Assert.Same(Data.Value.Field.Bands![0], run.Field);
        Assert.Equal(WarRules.StartFrontB, run.War.LaneState(TfwLane.B).Front);
    }
}
