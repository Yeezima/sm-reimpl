using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode's packs and the third doubling (the owner, 2026-11-06: "make the orks stop running in straight lines in musou mode. they should pool up at their spawn point before fanning out, swarming their target in a
/// rough blobby formation. double the amount of orks on the map again"): the trickle's muster, a released group as one pack, its blob shape on the way (no file), the surround at the target, determinism, the counts.
/// </summary>
public class TfwPackTests
{
    static MusouSim Field(MusouSwarmRules? rules = null, bool wall = false)
    {
        var level = new NavigationTests.Level().Floor(0f, 0f, 240f, 160f);
        if (wall) level = level.Wall(70f, 0f, 70f, 68f).Wall(70f, 92f, 70f, 160f);   // a 24 m gap at y 68 to 92
        return new MusouSim(NavGraph.Build(level.Mesh(), null, NavSettings.From(new SimConfig()))) { Swarm = rules ?? MusouSwarmRules.Default };
    }

    static void Run(MusouSim sim, MusouTarget[] targets, int ticks, ref uint tick, List<MusouAttack>? attacks = null)
    {
        attacks ??= new List<MusouAttack>();
        for (int k = 0; k < ticks; k++) { sim.BeginTick(++tick); sim.Step(targets, null, false, attacks); }
    }

    /// <summary>A group of <paramref name="n"/> Boyz standing leashed on a disc round <paramref name="centre"/>, then let go as one.</summary>
    static ushort[] Release(MusouSim sim, Vector3 centre, int n, int group = 7)
    {
        var ids = new ushort[n];
        for (int k = 0; k < n; k++)
        {
            float a = k * 2.399963f, r = 9f * MathF.Sqrt((k + 0.5f) / n);
            ids[k] = sim.SpawnLeashed(MusouEnemyType.Boy, centre + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), 0f, 85f, group, 5f);
            Assert.NotEqual(0, ids[k]);
        }
        sim.ReleaseGroup(group);
        return ids;
    }

    /// <summary>The shape of the enemies in <paramref name="ids"/> still alive: how far across (the smaller spread, m), along (the larger), and how many times longer than wide.</summary>
    static (float Across, float Along, float Ratio, Vector2 Centre) Shape(MusouSim sim, IEnumerable<ushort> ids)
    {
        var at = ids.Select(id => sim.TryGet(id, out var e) ? new Vector2(e.Position.X, e.Position.Y) : (Vector2?)null).OfType<Vector2>().ToArray();
        var mean = at.Aggregate(Vector2.Zero, (s, p) => s + p) / at.Length;
        float xx = at.Average(p => (p.X - mean.X) * (p.X - mean.X)), yy = at.Average(p => (p.Y - mean.Y) * (p.Y - mean.Y)), xy = at.Average(p => (p.X - mean.X) * (p.Y - mean.Y));
        float half = (xx + yy) / 2f, root = MathF.Sqrt(MathF.Max(0f, half * half - (xx * yy - xy * xy)));
        float big = MathF.Sqrt(half + root), small = MathF.Sqrt(MathF.Max(1e-6f, half - root));
        return (small, big, big / small, mean);
    }

    [Fact]
    public void A_released_group_is_one_pack_and_the_5_wave_run_makes_none()
    {
        var sim = Field();
        var ids = Release(sim, new Vector3(30f, 80f, 0f), 40);
        int pack = sim.PackOf(ids[0]);
        Assert.NotEqual(0, pack);
        Assert.All(ids, id => Assert.Equal(pack, sim.PackOf(id)));
        var other = Release(sim, new Vector3(30f, 30f, 0f), 10, group: 8);
        Assert.NotEqual(pack, sim.PackOf(other[0]));
        uint tick = 0;
        Run(sim, [new MusouTarget(1, new Vector3(200f, 80f, 0f), true)], 2, ref tick);
        Assert.Equal(40, sim.Pack(pack).Members);

        var plain = Field();
        plain.Swarm = null;
        var five = Release(plain, new Vector3(30f, 80f, 0f), 10);
        Assert.All(five, id => Assert.Equal(0, plain.PackOf(id)));
    }

    [Fact]
    public void A_woken_ork_and_the_group_let_go_after_it_share_one_pack()
    {
        var sim = Field();
        var a = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(100f, 80f, 0f), 0f, 85f, 5, 10f);
        var b = sim.SpawnLeashed(MusouEnemyType.Boy, new Vector3(130f, 80f, 0f), 0f, 85f, 5, 10f);
        uint tick = 0;
        Run(sim, [new MusouTarget(1, new Vector3(95f, 80f, 0f), true)], 40, ref tick);   // past the spawn: a is near the target, b is not
        Assert.False(sim.Leashed(a));
        Assert.True(sim.Leashed(b));
        sim.ReleaseGroup(5);
        Assert.NotEqual(0, sim.PackOf(a));
        Assert.Equal(sim.PackOf(a), sim.PackOf(b));
    }

    [Fact]
    public void A_pack_goes_as_a_loose_blob_not_a_file_even_round_a_corner()
    {
        // 120 Boyz from a 9 m disc to a target beyond a 24 m gap in a wall (their way hugs the wall's end), sampled each second for 30 s.
        (float OpenRatio, float WorstRatio, float LeastAcross, string Trace) Walk(MusouSwarmRules rules)
        {
            var sim = Field(rules, wall: true);
            var ids = Release(sim, new Vector3(25f, 30f, 0f), 120);
            uint tick = 0;
            var target = new MusouTarget(1, new Vector3(225f, 140f, 0f), true);
            float open = 0f, worst = 0f, least = float.MaxValue;
            string trace = "";
            for (int s = 0; s < 30; s++)
            {
                Run(sim, [target], 30, ref tick);
                var now = Shape(sim, ids);
                trace += $" {s + 1}s {now.Along:F1}x{now.Across:F1}";
                if (s >= 2 && s < 9) open = MathF.Max(open, now.Ratio);   // the open ground before the gap (11 s before the Orks' 1.2x pace of 2026-11-09)
                worst = MathF.Max(worst, now.Ratio);
                if (s >= 2) least = MathF.Min(least, now.Across);
            }
            return (open, worst, least, trace);
        }
        var packs = Walk(MusouSwarmRules.Default);
        var plain = Walk(MusouSwarmRules.Default with { PackLead = 0f, WanderDegrees = 0f });   // each Ork down the field on its own: the old way
        string why = $"pack:{packs.Trace} | plain:{plain.Trace}";
        Assert.True(packs.OpenRatio < 1.6f, $"in the open the pack is {packs.OpenRatio:F1} times longer than wide; {why}");
        Assert.True(packs.LeastAcross > 2f, $"the pack narrowed to {packs.LeastAcross:F1} m across; {why}");
        Assert.True(plain.LeastAcross < 1.2f && plain.WorstRatio > 10f, $"the old way was no file this time ({plain.LeastAcross:F1} m, {plain.WorstRatio:F1}); {why}");
        Assert.True(packs.WorstRatio < plain.WorstRatio / 3f, $"the pack's worst {packs.WorstRatio:F1} against the old way's {plain.WorstRatio:F1}; {why}");
    }

    [Fact]
    public void A_pack_surrounds_its_target_from_several_sides()
    {
        var sim = Field();
        var target = new MusouTarget(1, new Vector3(150f, 80f, 0f), true);
        var ids = Release(sim, new Vector3(60f, 80f, 0f), 80);   // all from the west
        uint tick = 0;
        Run(sim, [target], 35 * 30, ref tick);
        var sectors = new HashSet<int>();
        int near = 0;
        foreach (var id in ids)
        {
            if (!sim.TryGet(id, out var e)) continue;
            var d = new Vector2(e.Position.X - target.Position.X, e.Position.Y - target.Position.Y);
            if (d.Length() > 12f) continue;
            near++;
            sectors.Add((int)MathF.Floor((MathF.Atan2(d.Y, d.X) + MathF.PI) / (MathF.Tau / 12f)) % 12);
        }
        Assert.True(near >= 40, $"{near} of 80 within 12 m");
        Assert.True(sectors.Count >= 8, $"{sectors.Count} of 12 sectors round the target hold an Ork (they came from the west alone)");
        Assert.True(sectors.Contains(5) || sectors.Contains(6), "the far (east) side holds some");   // sectors 5 and 6 face +x
    }

    [Fact]
    public void Packs_are_deterministic()
    {
        string Run1()
        {
            var sim = Field(wall: true);
            Release(sim, new Vector3(25f, 30f, 0f), 60);
            Release(sim, new Vector3(25f, 130f, 0f), 30, group: 9);
            uint tick = 0;
            Run(sim, [new MusouTarget(1, new Vector3(200f, 80f, 0f), true), new MusouTarget(2, new Vector3(190f, 40f, 0f), true)], 20 * 30, ref tick);
            var all = new MusouEnemy[MusouSim.Capacity];
            int n = sim.CopyTo(all);
            return string.Join(";", all.Take(n).Select(e => $"{e.Id}:{e.Position.X:R},{e.Position.Y:R},{e.State}"));
        }
        Assert.Equal(Run1(), Run1());
    }

    [Fact]
    public void The_trickle_musters_at_its_source_then_goes_as_one_pack()
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default };
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, new GuardSim(sim), layout, 100.0, 1, false, default, TfwBattleRules.EliteBase, false);
        battle.Start();
        int perTrickle = (int)MathF.Round(TfwBattleRules.TrickleSeconds * 30f);
        ushort[] first = [];
        for (int t = 0; t < perTrickle * (TfwBattleRules.MusterSize - 1); t++) { sim.BeginTick((uint)t + 1); battle.Step([]); }
        first = battle.MusterIds(0);
        Assert.Equal(TfwBattleRules.MusterSize - 1, first.Length);
        Assert.All(first, id => Assert.True(sim.Leashed(id)));               // standing at the source
        Assert.All(first, id => Assert.Equal(0, sim.PackOf(id)));
        for (int t = 0; t < perTrickle && battle.MusterIds(0).Length > 0; t++) battle.Step([]);
        Assert.Empty(battle.MusterIds(0));                                   // the 16th came: all of it goes
        Assert.All(first, id => Assert.False(sim.Leashed(id)));
        int pack = sim.PackOf(first[0]);
        Assert.NotEqual(0, pack);
        Assert.All(first, id => Assert.Equal(pack, sim.PackOf(id)));
    }

    [Fact]
    public void A_muster_short_of_its_size_goes_after_its_wait()
    {
        var sim = new MusouSim(null) { Swarm = MusouSwarmRules.Default };
        var battle = new TfwBattle(sim, new GuardSim(sim), TfwTestArena.Layout(), 100.0, 1, false, default, TfwBattleRules.EliteBase, false);
        battle.Start();
        int wait = (int)MathF.Round(TfwBattleRules.MusterSeconds * 30f);
        int perTrickle = (int)MathF.Round(TfwBattleRules.TrickleSeconds * 30f);
        for (int t = 0; t < perTrickle; t++) battle.Step([]);
        var first = battle.MusterIds(0);
        Assert.NotEmpty(first);
        battle.Trickle = false;   // no more come: the muster stays short
        for (int t = 0; t < wait - 2; t++) battle.Step([]);
        Assert.All(first, id => Assert.True(sim.Leashed(id)));
        for (int t = 0; t < 4; t++) battle.Step([]);
        Assert.All(first, id => Assert.False(sim.Leashed(id)));
        Assert.True(wait / perTrickle >= TfwBattleRules.MusterSize, "a source that sends fills its muster before the wait ends it");
    }

    [Fact]
    public void Twice_the_orks_again_and_all_of_it_fits_the_pool()
    {
        Assert.Equal(2048, TfwBattleRules.CrowdPool);   // every spawner's pool; expansion S6 grew MusouSim's for the armies alone
        Assert.True(MusouSim.Capacity >= TfwBattleRules.CrowdPool + TfwArmyRules.NearOrksMost);
        Assert.Equal(96, TfwBattleRules.Garrison);        // 77, 320, 960, 1344 and 1280 on 2026-11-09 (80 %); back to 100 % since 2026-11-10 (OrkArmyAggregateTests)
        Assert.Equal(400, TfwBattleRules.CrowdSize);
        Assert.Equal(1200, TfwBattleRules.AliveCap(1));
        Assert.Equal(1680, TfwBattleRules.AliveCap(4));
        Assert.Equal(1600, TfwBattleRules.TrickleTotalMax);
        Assert.True(3 * TfwBattleRules.Garrison + TfwBattleRules.AliveCapMost + TfwBattleRules.PoolReserve <= TfwBattleRules.CrowdPool);
        Assert.True(TfwBattleRules.TrickleTotalMax <= TfwBattleRules.CrowdPool - 3 * TfwBattleRules.Garrison - TfwBattleRules.PoolReserve);
        // A crowd still fills in about the same time: 400 at one every 4 ticks (320 at 5 on 2026-11-09).
        Assert.InRange(TfwBattleRules.CrowdSize * TfwBattleRules.CrowdFillSeconds, 50f, 56f);
    }
}
