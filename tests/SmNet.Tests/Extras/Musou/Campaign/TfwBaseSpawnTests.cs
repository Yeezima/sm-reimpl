using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Hotfix (the owner, 2026-11-08: "you should always spawn in at the imperial base in the south center of the ashfall map in musou mode"): on Ashfall every battle lands at the Space Marine
/// start area (<see cref="TfwBattlefield.BaseLanding"/>), so each way a human enters the battle (the landing, a redeploy after death, a late join, every later battle) puts it there, facing the
/// field. Before, two battles in three landed on drawn open ground anywhere on the map. The synthetic arena stands in for Ashfall with a real walking graph (the draw needs one); the
/// Manufactorum composition keeps its drawn landing.
/// </summary>
public class TfwBaseSpawnTests
{
    const int Human = TfwRig.Human;
    static readonly Vector3 Base = TfwTestArena.LandingZone;   // the arena's Space Marine start area

    static TfwMapData Data(bool manufactorum) => TfwMap.Compose(TfwTestArena.Level(), TfwTestArena.Points, TfwTestArena.Areas, [TfwTestArena.LandingZone, TfwTestArena.Hq, .. TfwTestArena.Sources], manufactorum);

    static readonly Lazy<TfwMapData> Ashfallish = new(() => Data(manufactorum: false));
    static readonly Lazy<TfwMapData> Manufactorumish = new(() => Data(manufactorum: true));
    static readonly Lazy<NavGraph> Graph = new(() => NavGraph.Build(Ashfallish.Value.Collision.Character, Ashfallish.Value.Collision.KillVolumes, NavSettings.From(TfwRig.Config), Ashfallish.Value.Field.WalkArea()));

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>The yaw a landing faces: from the base towards the commander, across the field.</summary>
    static float FieldYaw(TfwLayout layout) => MathF.Atan2(-(layout.Hq.X - layout.LandingZone.X), layout.Hq.Y - layout.LandingZone.Y);

    static World War()
    {
        var data = Ashfallish.Value;
        var w = new World(TfwRig.Config, data.Collision, data.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = data.StartAreas };
        Assert.True(w.StartTfw(Graph.Value, data.Field));
        // retainers are gone (hotfix/musou-allies-two): the two allied Marines do not affect where a human lands
        w.Tfw!.Guardsmen = true;
        w.Tfw.Patrols = false;
        w.TfwMeksOn = false;
        w.Spawn(Human, w.AssignClass(0));
        TfwRig.Steps(w, 30);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    static void AtBase(World w, int id, TfwBattle battle)
    {
        var p = w.Players[id];
        Assert.True(p.Alive, $"player {id} is down");
        Assert.True(Flat(battle.Layout.LandingZone, Base) < 1f, $"the landing zone {battle.Layout.LandingZone} is not the base {Base}");
        Assert.True(Flat(p.Position, Base) < 6f, $"player {id} at {p.Position}, not at the base {Base}");
        Assert.False(TfwTestArena.Hub.Contains(p.Position));
        Assert.Equal(FieldYaw(battle.Layout), p.Yaw, 3);
    }

    [Fact]
    public void Ashfall_always_lands_at_its_base_and_manufactorum_still_draws()
    {
        Assert.True(Ashfallish.Value.Field.BaseLanding);
        Assert.False(Manufactorumish.Value.Field.BaseLanding);
        var ashfall = new TfwSites(Ashfallish.Value.Field, Graph.Value, Ashfallish.Value.Collision);
        var manufactorum = new TfwSites(Manufactorumish.Value.Field, Graph.Value, Manufactorumish.Value.Collision);
        int away = 0;
        for (ulong seed = 0; seed < 30; seed++)
            for (int node = 1; node <= 4; node++)
            {
                Assert.True(Flat(ashfall.Layout((TfwLane)(seed % 3), node, seed).LandingZone, Base) < 1f, $"seed {seed} node {node}");
                if (Flat(manufactorum.Layout((TfwLane)(seed % 3), node, seed).LandingZone, Base) > 1f) away++;
            }
        Assert.True(away > 0, "the Manufactorum composition no longer draws its landing zone");   // the bug's draw, kept off Ashfall only
    }

    [Fact]
    public void The_real_Ashfall_lands_at_the_imperial_base_in_the_south_centre()
    {
        var data = TfwWorld.Load("", out string? problem);
        Assert.Null(problem);
        var field = data!.Field;
        Assert.True(field.BaseLanding);
        Assert.True(MathF.Abs(field.MarineStart.X) < 10f && field.MarineStart.Y < -150f, $"{field.MarineStart}");   // the spawn compound, south centre (AshfallMap.SpawnCompound)
        Assert.True(field.Commander.Y > field.MarineStart.Y + 200f);                                                   // facing north across the field
    }

    /// <summary>
    /// <paramref name="battles"/> battles one after another (each drawn from another tick: before the hotfix, two in three landed away from the base), <paramref name="during"/> run in each,
    /// then back to the Stormbird.
    /// </summary>
    static void Battles(World w, int battles, Action<int, TfwBattle> during)
    {
        var run = w.Tfw!;
        for (int k = 0; k < battles; k++)
        {
            var battle = TfwRig.Deploy(w, (TfwLane)(k % 3));
            during(k, battle);
            run.PickupForTests(w);
            TfwRig.Steps(w, 7 + k * 13);   // another tick, another seed of the sites
            Assert.Equal(TfwRunPhase.Hub, run.Phase);
        }
    }

    [Fact]
    public void The_landing_puts_every_human_at_the_base_in_every_battle()
    {
        var w = War();
        w.Spawn(2, w.AssignClass(0));
        TfwRig.Steps(w, 2);
        Battles(w, 4, (_, battle) =>
        {
            Assert.Equal(2, w.Tfw!.InField.Count);
            foreach (int id in w.Tfw!.InField) AtBase(w, id, battle);
        });
    }

    [Fact]
    public void A_redeploy_after_death_comes_back_at_the_base()
    {
        var w = War();
        var run = w.Tfw!;
        Battles(w, TfwBattleRules.StartSupplies / TfwBattleRules.RedeployCost, (k, battle) =>
        {
            Assert.True(w.TfwRespawnAt(Human, battle.Layout.Positions[2], 0f));   // fights far from the base
            TfwRig.Steps(w, 2);
            Assert.True(w.TfwKillForTests(Human));
            TfwRig.Steps(w, 2);
            Assert.True(run.Enqueue(Human, new TfwCommandRecord((ushort)(k + 1), TfwCommand.Redeploy, 0)));
            TfwRig.Steps(w, 2);
            Assert.Equal(k + 1, run.Redeploys);
            AtBase(w, Human, battle);
        });
    }

    [Fact]
    public void A_human_who_joins_mid_battle_is_flown_in_at_the_base()
    {
        var w = War();
        var run = w.Tfw!;
        Battles(w, TfwCoop.MaxHumans - 1, (k, battle) =>
        {
            int id = k + 2;
            w.Spawn(id, w.AssignClass(0));   // the core spawns it in the Stormbird; the run flies it in
            TfwRig.Steps(w, 2);
            Assert.Equal(k + 1, run.LateJoins);
            AtBase(w, id, battle);
        });
    }

    [Fact]
    public void Every_later_battle_lands_at_the_base_too()
    {
        var w = War();
        Battles(w, 4, (_, battle) => AtBase(w, Human, battle));
    }
}
