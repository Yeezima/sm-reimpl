using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Orks retaking a point spread over its capture ring and look round (MusouSim.Hold.cs; the owner, 2026-11-08: "orks pile up all looking at the center of a point when capturing it - they should spread out a bit
/// and look around"). A headless, deterministic trial on a flat synthetic floor: a band of Orks comes at a secured point (a goal) from 40 m and stands there; the same band against a goal without a ring is the old way.
/// </summary>
public class OrkCaptureSpreadTests(ITestOutputHelper output)
{
    const float Ring = 14f, Half = 90f;
    static readonly Vector3 Point = new(20f, 20f, 0f);
    static NavGraph? _nav;
    static StaticMesh? _mesh;

    sealed record Trial(MusouSim Sim, List<ushort> Ids, float FirstInside, float AllInside);

    /// <summary>
    /// <paramref name="orks"/> Orks (Boyz and Shoota Boyz) from a disc 40 m off, the point the only target (with <paramref name="ring"/> 0: the old goal), for <paramref name="seconds"/>;
    /// <paramref name="threat"/>: a player standing there too. Returns when the first and the last stood inside the ring.
    /// </summary>
    static Trial Run(int orks, float ring, float seconds, Vector3? point = null, Vector3? threat = null)
    {
        var at = point ?? Point;
        _mesh ??= new NavigationTests.Level().Floor(-Half, -Half, Half, Half).Mesh();
        var collision = new CollisionWorld(_mesh, _mesh);
        _nav ??= NavGraph.Build(_mesh, null, NavSettings.From(new SimConfig()));
        var sim = new MusouSim(_nav) { TickRate = 30, Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };
        var from = at + new Vector3(-40f, 0f, 0f);
        var ids = new List<ushort>();
        for (int n = 0; n < orks; n++)
        {
            var type = n % 4 == 3 ? MusouEnemyType.ShootaBoy : MusouEnemyType.Boy;
            float a = n * 2.399963f, r = 5f * MathF.Sqrt((n + 0.5f) / orks);
            ushort id = sim.Spawn(type, from + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), -MathF.PI / 2f, sim.Rules.Of(type).Health);
            Assert.NotEqual((ushort)0, id);
            ids.Add(id);
        }
        var targets = new List<MusouTarget> { new(TfwContracts.PointTargetId(0), at, true, 0f, Goal: true, Ring: ring) };
        if (threat is { } player) targets.Add(new MusouTarget(1, player, true));
        var attacks = new List<MusouAttack>();
        float first = -1f, all = -1f;
        for (uint tick = 1; tick <= (uint)(seconds * 30f); tick++)
        {
            sim.BeginTick(tick);
            attacks.Clear();
            sim.Step(targets.ToArray(), collision, false, attacks);
            int inside = Inside(sim, ids, at);
            if (first < 0f && inside > 0) first = tick / 30f;
            if (all < 0f && inside == ids.Count) all = tick / 30f;
        }
        return new Trial(sim, ids, first, all);
    }

    /// <summary>The Orks inside the capture volume the battle counts (flat ring, 2.5 m of height).</summary>
    static int Inside(MusouSim sim, List<ushort> ids, Vector3 at)
    {
        int n = 0;
        foreach (var id in ids)
            if (sim.TryGet(id, out var e) && MathF.Abs(e.Position.Z - at.Z) <= 2.5f && new Vector2(e.Position.X - at.X, e.Position.Y - at.Y).Length() <= Ring) n++;
        return n;
    }

    static (float Min, float MeanNearest) Spacing(MusouSim sim, List<ushort> ids)
    {
        var p = ids.Select(id => { sim.TryGet(id, out var e); return new Vector2(e.Position.X, e.Position.Y); }).ToArray();
        float min = float.MaxValue, sum = 0f;
        for (int a = 0; a < p.Length; a++)
        {
            float nearest = float.MaxValue;
            for (int b = 0; b < p.Length; b++) if (a != b) nearest = MathF.Min(nearest, Vector2.Distance(p[a], p[b]));
            min = MathF.Min(min, nearest); sum += nearest;
        }
        return (min, sum / p.Length);
    }

    static Vector2 Facing(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw));

    [Fact]
    public void Orks_retaking_a_point_spread_across_its_ring_instead_of_piling_on_its_centre()
    {
        var before = Run(30, 0f, 30f);
        var after = Run(30, Ring, 30f);
        var (oldMin, oldMean) = Spacing(before.Sim, before.Ids);
        var (min, mean) = Spacing(after.Sim, after.Ids);
        output.WriteLine($"before: nearest min {oldMin:F2} m, mean {oldMean:F2} m; after: nearest min {min:F2} m, mean {mean:F2} m");
        Assert.True(min >= 1.0f, $"two Orks within {min:F2} m");   // no stacking
        Assert.True(mean >= 2.0f, $"mean nearest {mean:F2} m");
        Assert.True(mean > oldMean * 1.5f, $"after {mean:F2} m vs before {oldMean:F2} m");
        // Across the ring: some near its middle, some out toward its edge, every one inside it.
        var radii = after.Ids.Select(id => { after.Sim.TryGet(id, out var e); return new Vector2(e.Position.X - Point.X, e.Position.Y - Point.Y).Length(); }).ToArray();
        output.WriteLine("radii: " + string.Join(" ", radii.Select(r => r.ToString("F1"))));
        Assert.All(radii, r => Assert.True(r <= Ring, $"an Ork {r:F1} m out, beyond the ring"));
        Assert.Contains(radii, r => r >= Ring * 0.6f);
        Assert.Contains(radii, r => r <= Ring * 0.4f);
        foreach (var id in after.Ids) Assert.NotNull(after.Sim.GoalSpotOf(id));
        // Test aid: SM_ORK_SPREAD_DUMP=<folder> writes both bands (x, y from the centre, yaw) for a top-down picture.
        if (Environment.GetEnvironmentVariable("SM_ORK_SPREAD_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            foreach (var (name, trial) in new[] { ("before", before), ("after", after) })
                File.WriteAllLines(Path.Combine(dump, name + ".csv"), trial.Ids.Select(id =>
                {
                    trial.Sim.TryGet(id, out var e);
                    return FormattableString.Invariant($"{e.Position.X - Point.X:F3},{e.Position.Y - Point.Y:F3},{e.Yaw:F4}");
                }));
        }
    }

    [Fact]
    public void Orks_standing_on_a_point_face_varied_ways_and_look_round_instead_of_at_its_centre()
    {
        var trial = Run(30, Ring, 30f);
        int atCentre = 0;
        var sum = Vector2.Zero;
        var yaws = new Dictionary<ushort, float>();
        foreach (var id in trial.Ids)
        {
            Assert.True(trial.Sim.TryGet(id, out var e));
            var face = Facing(e.Yaw);
            var toCentre = new Vector2(Point.X - e.Position.X, Point.Y - e.Position.Y);
            if (toCentre.Length() > 1f && Vector2.Dot(face, Vector2.Normalize(toCentre)) > MathF.Cos(30f * MathF.PI / 180f)) atCentre++;
            sum += face;
            yaws[id] = e.Yaw;
        }
        float resultant = sum.Length() / trial.Ids.Count;   // 1: all one way; 0: all round
        output.WriteLine($"facing the centre (30 deg): {atCentre} of {trial.Ids.Count}; resultant {resultant:F2}");
        Assert.True(atCentre <= trial.Ids.Count / 4, $"{atCentre} face the centre");
        Assert.True(resultant < 0.5f, $"resultant {resultant:F2}");

        // Ten seconds on, most have glanced round (each every 3 to 8 s).
        var more = Run(30, Ring, 40f);
        int turned = 0;
        foreach (var id in more.Ids)
            if (more.Sim.TryGet(id, out var e) && MathF.Abs(MathF.IEEERemainder(e.Yaw - yaws[id], MathF.Tau)) > 5f * MathF.PI / 180f) turned++;
        output.WriteLine($"turned in 10 s: {turned} of {more.Ids.Count}");
        Assert.True(turned >= more.Ids.Count / 2, $"{turned} turned");
    }

    [Fact]
    public void A_standing_retaker_looks_at_a_threat_in_range()
    {
        // A player 16 m from the centre on the far side: the Orks still going for the point (the point nearer than the player) within range of him look at him.
        var player = Point + new Vector3(16f, 0f, 0f);
        var trial = Run(20, Ring, 30f, threat: player);
        int looking = 0, near = 0;
        foreach (var id in trial.Ids)
        {
            if (!trial.Sim.TryGet(id, out var e) || e.TargetPlayer != TfwContracts.PointTargetId(0) || e.Velocity.Length() > 0.3f) continue;
            var to = new Vector2(player.X - e.Position.X, player.Y - e.Position.Y);
            if (to.Length() > MusouSwarmRules.Default.LookThreatRange) continue;
            near++;
            if (Vector2.Dot(Facing(e.Yaw), Vector2.Normalize(to)) > MathF.Cos(15f * MathF.PI / 180f)) looking++;
        }
        output.WriteLine($"standing retakers in range: {near}, looking at the player: {looking}");
        Assert.True(near > 0);
        Assert.Equal(near, looking);
    }

    [Fact]
    public void The_retake_still_starts_and_fills_the_ring_as_before()
    {
        var before = Run(30, 0f, 30f);
        var after = Run(30, Ring, 30f);
        output.WriteLine($"first inside: before {before.FirstInside:F2} s, after {after.FirstInside:F2} s; all inside: before {before.AllInside:F2} s, after {after.AllInside:F2} s");
        Assert.True(before.FirstInside > 0f && after.FirstInside > 0f);
        Assert.InRange(after.FirstInside - before.FirstInside, -0.5f, 0.5f);   // the point starts falling when it did
        Assert.True(after.AllInside > 0f, "not every Ork reached the ring");
        Assert.Equal(after.Ids.Count, Inside(after.Sim, after.Ids, Point));   // and every one counts on it
    }

    [Fact]
    public void Spots_off_the_floor_are_pulled_in_and_every_Ork_still_stands_inside_the_ring()
    {
        // A point near the floor's edge: part of its ring hangs over nothing.
        var edge = new Vector3(Half - 6f, 0f, 0f);
        var trial = Run(30, Ring, 35f, point: edge);
        foreach (var id in trial.Ids)
        {
            var spot = trial.Sim.GoalSpotOf(id);
            Assert.NotNull(spot);
            Assert.True(spot!.Value.X <= Half, $"spot {spot} off the floor");
        }
        Assert.Equal(trial.Ids.Count, Inside(trial.Sim, trial.Ids, edge));
    }

    [Fact]
    public void Leashed_Orks_at_their_post_look_round()
    {
        _mesh ??= new NavigationTests.Level().Floor(-Half, -Half, Half, Half).Mesh();
        _nav ??= NavGraph.Build(_mesh, null, NavSettings.From(new SimConfig()));
        var sim = new MusouSim(_nav) { TickRate = 30, Swarm = MusouSwarmRules.Default, War = TfwCombatRules.Default };
        var ids = new List<ushort>();
        for (int n = 0; n < 24; n++)
        {
            float a = n * 2.399963f, r = 8f * MathF.Sqrt((n + 0.5f) / 24f);
            ids.Add(sim.SpawnLeashed(MusouEnemyType.Boy, Point + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), 0f, 100f, group: 5, aggro: 30f));
        }
        var attacks = new List<MusouAttack>();
        for (uint tick = 1; tick <= 300; tick++) { sim.BeginTick(tick); attacks.Clear(); sim.Step(ReadOnlySpan<MusouTarget>.Empty, null, false, attacks); }
        var sum = Vector2.Zero;
        int moved = 0;
        foreach (var id in ids)
        {
            Assert.True(sim.TryGet(id, out var e));
            sum += Facing(e.Yaw);
            if (MathF.Abs(MathF.IEEERemainder(e.Yaw, MathF.Tau)) > 10f * MathF.PI / 180f) moved++;
        }
        output.WriteLine($"resultant {sum.Length() / ids.Count:F2}, off their spawn facing: {moved} of {ids.Count}");
        Assert.True(sum.Length() / ids.Count < 0.85f);
        Assert.True(moved >= ids.Count * 2 / 3);
    }
}
