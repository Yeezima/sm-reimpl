using System.Numerics;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Maps;

/// <summary>What the D site check found (<see cref="WarfrontSiteCheck.Measure"/>).</summary>
public sealed record WarfrontSiteReport(
    Vector3 Site, bool OnGraph, float GraphDistance, bool ReachableFromLanding, bool ReachableFromBase, float PathLength,
    int Approaches, IReadOnlyList<float> ApproachDegrees, int CapableDirections, float ArmyWidth, float RingRadius)
{
    /// <summary>The slice's rule: on the graph, reachable from the landing zone and the base, and at least <see cref="WarfrontSiteCheck.RequiredApproaches"/> army-width approaches.</summary>
    public bool Passed => OnGraph && ReachableFromLanding && ReachableFromBase && Approaches >= WarfrontSiteCheck.RequiredApproaches;
}

/// <summary>
/// The Warfront objective D check (plan section 2): D is <c>TfwLayout.Hq</c>, the stronghold's centre on the through road between the keep's wings. It must stand on the walking graph, be reachable on foot from the landing zone (and from the
/// Imperial base), and be approachable by at least two army-width ways: from a ring round D (its capture radius plus a margin), the directions whose ground keeps half the army's width clear and connects, through ground of that clearance
/// outside D's own disc, to the base's assembly area; two such directions at least the pilot's approach separation apart count as two approaches (the points' rule of gate A and B, applied to D).
/// </summary>
public static class WarfrontSiteCheck
{
    /// <summary>D needs this many army-width approaches (plan section 2; ours).</summary>
    public const int RequiredApproaches = 2;

    /// <summary>The ring the approaches are read on: D's capture radius plus this (m; ours, the points' rule uses their open zone plus 6 m).</summary>
    public const float RingMargin = 6f;

    /// <summary>The disc round D the connecting ground may not cross (m; the capture ring, <see cref="TfwMap.HqRadius"/>): an approach comes in from outside it.</summary>
    public const float DiscRadius = TfwMap.HqRadius;

    /// <summary>Measures D on a built slot: <paramref name="level"/> and <paramref name="plan"/> are the band's, <paramref name="nav"/> the world's walking graph, <paramref name="hq"/> the layout's Hq, <paramref name="landing"/> its landing zone.</summary>
    public static WarfrontSiteReport Measure(AshfallLevel level, AshfallFieldPlan plan, NavGraph nav, Vector3 hq, Vector3 landing, Vector3 marineStart)
    {
        int node = nav.Nearest(hq, 6f, 8f);
        float graphDistance = node >= 0 ? Vector3.Distance(nav.Position(node), hq) : float.PositiveInfinity;
        bool fromLanding = false, fromBase = false; float length = 0f;
        if (node >= 0)
        {
            var path = nav.FindPath(landing, hq);
            fromLanding = path is { Partial: false };
            length = path is { Partial: false } p ? p.Length : 0f;
            fromBase = nav.FindPath(marineStart, hq) is { Partial: false };
        }
        float army = plan.Params.MinArmyWidth, half = army / 2f, ring = TfwMap.HqRadius + RingMargin;
        var grid = new AshfallGrid(level);
        var c = new Vector2(hq.X, hq.Y);
        int n = grid.N;
        var comp = new bool[n * n];
        var rect = plan.Assembly[0].Rect;
        int start = grid.Snap(new Vector2((rect.X + rect.Z) / 2f, (rect.Y + rect.W) / 2f), half);
        if (start >= 0) Fill(grid, comp, start, half, c, DiscRadius);
        var capable = new List<float>();
        for (int deg = 0; deg < 360; deg += 3)
        {
            float rad = deg * MathF.PI / 180f;
            var q = c + new Vector2(MathF.Cos(rad), MathF.Sin(rad)) * ring;
            if (MathF.Abs(q.X) >= AshfallMap.Half - 1f || MathF.Abs(q.Y) >= AshfallMap.Half - 1f) continue;
            int k = grid.Index(q);
            if (grid.Clearance[k] >= half && comp[k]) capable.Add(deg);
        }
        float sep = plan.Params.ApproachSeparation;
        var picked = new List<float>();
        foreach (float s in capable)
        {
            var set = new List<float> { s };
            foreach (float d in capable.Select(x => (x - s + 360f) % 360f).OrderBy(x => x).Select(x => (x + s) % 360f))
                if (set.All(t => Sep(t, d) >= sep)) set.Add(d);
            if (set.Count > picked.Count) picked = set;
        }
        return new WarfrontSiteReport(hq, node >= 0, graphDistance, fromLanding, fromBase, length, picked.Count, [.. picked.OrderBy(x => x)], capable.Count, army, ring);
    }

    static float Sep(float a, float b) { float d = MathF.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }

    /// <summary>Flood fill (8-connected) over the cells with at least <paramref name="half"/> clearance outside the disc round <paramref name="c"/>; reached cells are marked.</summary>
    static void Fill(AshfallGrid g, bool[] seen, int start, float half, Vector2 c, float disc)
    {
        int n = g.N;
        float d2 = disc * disc;
        var stack = new Stack<int>();
        seen[start] = true; stack.Push(start);
        while (stack.TryPop(out int k))
        {
            int x = k % n, y = k / n;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                    int m = ny * n + nx;
                    if (seen[m] || g.Clearance[m] < half || Vector2.DistanceSquared(g.At(m), c) < d2) continue;
                    seen[m] = true; stack.Push(m);
                }
        }
    }
}
