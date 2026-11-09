using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// Ashfall draft 10 (owner, 2026-11-09; docs/musou-ashfall-graybox.md "Draft 10 lanes and stairs"): clear lanes from the stronghold to every point and between the points, a stronger clear zone at the foot of every
/// stair, and the stairs that could not be walked up made walkable. From the generator alone (no asset bytes, no install); the walks use the game's own movement (<see cref="Movement.Step"/>, the player's capsule) on the
/// whole level's collision.
/// </summary>
public class AshfallDraft10Tests(ITestOutputHelper output)
{
    static readonly AshfallDraft5 D = AshfallMap.BuildDraft5();
    static readonly Lazy<CollisionWorld> World = new(() =>
    {
        var c = D.Level.SolidCorners(); var w = D.Level.WeaponCorners();
        return new CollisionWorld(new StaticMesh(c, Enumerable.Range(0, c.Length).ToArray()), new StaticMesh(w, Enumerable.Range(0, w.Length).ToArray()));
    });

    string Name(int item) => D.Placements.FirstOrDefault(p => p.Item == item) is { } p ? $"{p.Group} ({p.Asset})" : $"item {item}";

    // ---------------------------------------------------------------- lanes

    /// <summary>Walks a capsule along waypoints at run speed; null when it arrives, else where it stalled.</summary>
    static string? WalkRoute(Vector2[] path, out int ticks)
    {
        var cfg = new SimConfig();
        float speed = cfg.RunSpeed / cfg.TickRate;
        var s = new PlayerState(1, new Vector3(path[0].X, path[0].Y, AshfallMap.SurfaceAt(path[0].X, path[0].Y) + 0.05f), Vector3.Zero, 0f, true);
        float length = 0f;
        for (int i = 1; i < path.Length; i++) length += Vector2.Distance(path[i - 1], path[i]);
        int limit = (int)(length / speed * 1.5f) + 200, next = 1;
        var mark = path[0]; uint markTick = 0;
        for (uint t = 0; t < limit; t++)
        {
            var p = new Vector2(s.Position.X, s.Position.Y);
            while (next < path.Length && Vector2.Distance(p, path[next]) < (next == path.Length - 1 ? 1.5f : 2.5f)) next++;
            if (next >= path.Length) { ticks = (int)t; return null; }
            var d = path[next] - p;
            float yaw = MathF.Atan2(-d.X, d.Y);
            s = Movement.Step(s, new PlayerInput(t, 0f, 1f, yaw, InputButtons.None), cfg, World.Value);
            // stalled: under 2 m of progress over 2 s (the run covers about 12 m)
            if (t - markTick >= (uint)(2 * cfg.TickRate))
            {
                var q = new Vector2(s.Position.X, s.Position.Y);
                if (Vector2.Distance(q, mark) < 2f) { ticks = (int)t; return $"stalls at ({q.X:0.0}, {q.Y:0.0}, {s.Position.Z:0.0}) on the way to ({path[next].X:0}, {path[next].Y:0})"; }
                mark = q; markTick = t;
            }
        }
        ticks = limit;
        return $"does not arrive (at ({s.Position.X:0.0}, {s.Position.Y:0.0}))";
    }

    [Fact]
    public void Every_lane_walks_end_to_end_both_ways_with_the_players_capsule()
    {
        var bad = new List<string>();
        foreach (var (name, path) in AshfallLanes.Routes)
            foreach (bool back in new[] { false, true })
            {
                var p = back ? path.Reverse().ToArray() : path;
                var why = WalkRoute(p, out int ticks);
                output.WriteLine($"{name}{(back ? " (back)" : "")}: {(why ?? "walked")} in {ticks} ticks");
                if (why is not null) bad.Add($"{name}{(back ? " (back)" : "")}: {why}");
            }
        Assert.Empty(bad);
    }

    [Fact]
    public void The_routes_join_the_stronghold_the_base_and_every_point_and_every_pair_of_points()
    {
        var names = AshfallLanes.Routes.Select(r => r.Name).ToHashSet();
        foreach (var p in D.Level.Points) Assert.Contains($"stronghold to {p.Name}", names);
        foreach (var p in D.Level.Points) Assert.Contains($"base to {p.Name}", names);
        Assert.Contains("A to B", names); Assert.Contains("B to C", names); Assert.Contains("A to C", names);
        // each route ends at its point's centre and starts at the stronghold's courtyard, the base's centre or a point
        foreach (var (name, path) in AshfallLanes.Routes)
        {
            var end = name.Split(' ')[^1];
            var pt = D.Level.Points.Single(q => q.Name == end);
            Assert.True(Vector2.Distance(path[^1], new(pt.Center.X, pt.Center.Y)) < 0.5f, name);
        }
    }

    [Fact]
    public void Every_lane_is_paved_road_and_no_object_stands_in_it()
    {
        // each connector is a Road part of the level (a roadway for the draft-8 rule) and its length is tiled with road slabs
        var roads = D.Level.Parts.Where(p => p.Kind == AshfallKind.Road).Select(AshfallBox.Of).Where(b => b is not null).Select(b => b!.Value).ToList();
        var tiles = D.Dressing.Items.Where(i => i.Role == AshfallDressRole.Surface).Select(i => new Vector2(i.Position.X, i.Position.Y)).ToList();
        foreach (var (name, r) in AshfallLanes.Connectors)
        {
            var c = new Vector2((r.X + r.Z) / 2f, (r.Y + r.W) / 2f);
            Assert.True(roads.Any(b => MathF.Abs(b.Cx - c.X) < 0.01f && MathF.Abs(b.Cy - c.Y) < 0.01f), $"{name}: a Road part");
            Assert.True(MathF.Min(r.Z - r.X, r.W - r.Y) >= AshfallLanes.Width - 0.01f || name == "stronghold gate", $"{name}: {AshfallLanes.Width} m wide");
            int on = tiles.Count(t => t.X >= r.X && t.X <= r.Z && t.Y >= r.Y && t.Y <= r.W);
            Assert.True(on >= 1, $"{name}: road tiles on it");
        }
        // no placed item's collision box meets a roadway (the lanes included) plus its margin
        var hits = AshfallRoadway.Find(D.Level, D.Dressing);
        foreach (var h in hits.Take(20)) output.WriteLine($"{Name(h.Item)} {h.Depth:0.00} m at ({h.At.X:0.0}, {h.At.Y:0.0})");
        Assert.Empty(hits);
    }

    // ---------------------------------------------------------------- stairs

    [Fact]
    public void Nothing_of_another_item_stands_in_the_three_metre_zone_at_any_stairs_foot()
    {
        var owner = new int[D.Level.Parts.Count];
        Array.Fill(owner, -1);
        for (int i = 0; i < D.Dressing.ItemParts.Count; i++) { var (f, n) = D.Dressing.ItemParts[i]; for (int p = f; p < f + n; p++) owner[p] = i; }
        var boxes = new List<(int Item, AshfallBox Box)>();
        for (int p = 0; p < D.Level.Parts.Count; p++)
        {
            var part = D.Level.Parts[p];
            if (!part.Solid || part.Kind is AshfallKind.Stair or AshfallKind.Ground or AshfallKind.Road or AshfallKind.Paving or AshfallKind.Curb or AshfallKind.Boundary) continue;
            if (AshfallBox.Of(part) is { } b) boxes.Add((owner[p], b));
        }
        var ramps = AshfallStairs.All(D.Level, D.Dressing);
        var bad = new List<string>();
        foreach (var r in ramps)
        {
            // the zone: FootApproach in front of the foot, the stair's width plus SideMargin a side, from a step over the foot to head height
            float depth = AshfallStairs.FootApproach, half = r.Width / 2f + AshfallStairs.SideMargin - 0.05f;
            for (float a = 0.1f; a <= depth - 0.05f; a += 0.25f)
                for (float t = -half; t <= half + 1e-3f; t += 0.25f)
                {
                    var p = r.Low - r.Dir * a + r.Across * t;
                    foreach (var (item, b) in boxes)
                    {
                        if (item == r.Item || b.Z0 >= r.LowZ + AshfallStairs.Headroom || b.Z1 <= r.LowZ + AshfallStairs.Step + 0.05f) continue;
                        if (MathF.Abs(b.Cx - p.X) > 40f || MathF.Abs(b.Cy - p.Y) > 40f || !AshfallAtmosphere.Inside(b, p, -0.03f)) continue;
                        bad.Add($"{Name(r.Item)} foot at ({r.Low.X:0.0}, {r.Low.Y:0.0}): {Name(item)} at {a:0.0} m");
                        goto next;
                    }
                }
            next:;
        }
        foreach (var b in bad.Take(30)) output.WriteLine(b);
        output.WriteLine($"{ramps.Count} stairs, {bad.Count} feet entered by another item");
        Assert.Empty(bad);
    }

    [Fact]
    public void The_draft_10_stair_rules_keep_three_times_the_draft_9_stairs_and_drop_only_steps_that_are_not_flights()
    {
        var ramps = AshfallStairs.All(D.Level, D.Dressing);
        output.WriteLine($"{ramps.Count} stairs");
        Assert.True(ramps.Count >= 135, $"{ramps.Count} stairs (draft 9: 45)");
        // narrow flights are widened to the walking width: every ramp at least 1.2 m (or the found width when wider)
        Assert.All(ramps, r => Assert.True(r.Width >= 1.19f, $"{Name(r.Item)}: {r.Width:0.00} m"));
        Assert.True(AshfallStairs.FootApproach >= 2f * AshfallStairs.Approach && AshfallStairs.SideMargin >= 0.5f);
        var flag = D.Dressing.Flags.Single(f => f.StartsWith("draft 9 stairs"));
        output.WriteLine(flag);
        Assert.Contains("draft 10", flag);
        // what is still dropped stays few: a stall on the walk at most a handful (the rest: steps under 0.8 m rise or 0.4 m wide, not rising after the scale, roof steps without a landing)
        int stalls = flag.Split("; ").Where(s => s.Contains("stalls")).Sum(s => int.Parse(s[(s.LastIndexOf(" x", StringComparison.Ordinal) + 2)..]));
        Assert.True(stalls <= 3, $"{stalls} flights still stall");
    }

    [Fact]
    public void The_level_version_is_raised()
    {
        Assert.True(AshfallMap.Version >= 17);
        Assert.True(AshfallMap.DressVersion >= 13);
    }
}
