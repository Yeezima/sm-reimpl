using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// musou/spawn-facing (owner, 2026-11-09: "any unit spawning, ork or imperial, player or allied space marine, they always point forwards towards the center of the map when spawning"): in a
/// Musou battle every spawn faces the map's centre (<see cref="TfwLayout.Centre"/>, the middle capture point); an army made real mid-march keeps its march facing; Classic PvP and the
/// 5-wave run (no <see cref="MusouSim.SpawnCentre"/>) keep the yaw the caller gave.
/// </summary>
public class TfwSpawnFacingTests
{
    const float Tolerance = 0.05f;   // rad, a little under 3 degrees

    static float Wrap(float a) { while (a > MathF.PI) a -= MathF.Tau; while (a < -MathF.PI) a += MathF.Tau; return a; }
    static void Faces(TfwLayout layout, Vector3 at, float yaw, string what) => Assert.True(MathF.Abs(Wrap(yaw - layout.FaceCentre(at))) < Tolerance, $"{what} at {at} faces {yaw}, the centre is {layout.FaceCentre(at)}");

    [Fact]
    public void The_centre_is_the_middle_capture_point_whichever_way_the_battle_runs()
    {
        var points = TfwTestArena.Points;
        var sites = new TfwSites(TfwMap.Compose(TfwTestArena.Level(), points, TfwTestArena.Areas, [TfwTestArena.LandingZone, TfwTestArena.Hq]).Field, null, null);
        foreach (int node in new[] { 1, 2 })
            Assert.True(Vector3.Distance(sites.Layout(TfwLane.A, node, 5).Centre, points[1].Center) < 12f, $"node {node}");
    }

    [Fact]
    public void An_enemy_spawns_facing_the_centre_only_while_a_battle_sets_it()
    {
        var sim = new MusouSim(null);
        var at = new Vector3(40f, 10f, 0f);
        ushort a = sim.Spawn(MusouEnemyType.Boy, at, 1f, 100f);
        Assert.Equal(1f, sim.YawOf(a), 3);   // no centre: the caller's yaw (the 5-wave run, the hub)
        sim.SpawnCentre = new Vector3(0f, 0f, 0f);
        float toward = MathF.Atan2(-(0f - at.X), 0f - at.Y);
        ushort b = sim.Spawn(MusouEnemyType.Boy, at, 1f, 100f);
        Assert.Equal(toward, sim.YawOf(b), 3);
        ushort c = sim.SpawnLeashed(MusouEnemyType.Boy, at, 1f, 100f, 1, 20f);
        Assert.Equal(toward, sim.YawOf(c), 3);
        ushort d = sim.Spawn(MusouEnemyType.Boy, at, 1f, 100f, keepYaw: true);   // an army made real mid-march
        Assert.Equal(1f, sim.YawOf(d), 3);
    }

    [Fact]
    public void Players_allies_and_the_first_Orks_face_the_centre_at_the_landing_and_a_redeploy()
    {
        var data = TfwMap.Compose(TfwTestArena.Level(), TfwTestArena.Points, TfwTestArena.Areas, [TfwTestArena.LandingZone, TfwTestArena.Hq, .. TfwTestArena.Sources], manufactorum: false);
        var graph = NavGraph.Build(data.Collision.Character, data.Collision.KillVolumes, NavSettings.From(TfwRig.Config), data.Field.WalkArea());
        var w = new World(TfwRig.Config, data.Collision, data.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = data.StartAreas };
        Assert.True(w.StartTfw(graph, data.Field));
        w.Tfw!.Patrols = false;
        w.TfwMeksOn = false;
        w.Spawn(TfwRig.Human, w.AssignClass(0));
        TfwRig.Steps(w, 30);
        TfwRig.Command(w, TfwCommand.Deploy, TfwLane.A);
        TfwBattle? battle = null;
        for (int t = 0; t < (int)((TfwBattleRules.TravelSeconds + 1f) * 30f) && battle is null; t++)
        {
            w.Step(id => new PlayerInput(0, 0f, 0f, w.Players.TryGetValue(id, out var q) ? q.Yaw : 0f, default));   // the client's input carries the view yaw it took from the server
            battle = w.Tfw!.Battle;
        }
        Assert.NotNull(battle);
        var layout = battle!.Layout;
        Faces(layout, w.Players[TfwRig.Human].Position, w.Players[TfwRig.Human].Yaw, "the landed human");
        foreach (var (id, p) in w.Players) if (TfwContracts.IsPuppet(id)) Faces(layout, p.Position, p.Yaw, $"puppet {id}");
        Assert.True(w.TfwRespawnAt(TfwRig.Human, layout.LandingZone + new Vector3(3f, 0f, 0f), 0f));
        Faces(layout, w.Players[TfwRig.Human].Position, w.Players[TfwRig.Human].Yaw, "a respawn in a battle");
        int checkedOrks = 0;
        for (ushort id = 1; id <= MusouSim.Capacity; id++)
            if (w.Musou.TryGet(id, out var e) && e.State == MusouEnemyState.Spawning) { Faces(layout, e.Position, w.Musou.YawOf(id), $"Ork {id}"); checkedOrks++; }
        Assert.True(checkedOrks > 0, "no Ork was still spawning to check");
    }
}
