using System.Numerics;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// The front pilot's levels (docs/musou-ashfall-graybox.md "Front pilot"): each band built by the kit pass on its own 480 m square (band frame), then the three composed into one world side by side at
/// <see cref="AshfallFront.OffsetOf"/> (the server cannot change maps: one run, one world; the run switches the active band per node). Also the empty frame gate A measures.
/// </summary>
public static partial class AshfallMap
{
    static readonly Lazy<AshfallDraft5>[] FrontBandCache = [.. Enumerable.Range(0, AshfallFront.BandCount).Select(b => new Lazy<AshfallDraft5>(() => AshfallDraft5Pass.Run(AshfallFront.Default(b)), LazyThreadSafetyMode.ExecutionAndPublication))];
    static readonly Lazy<AshfallDraft5> FrontCache = new(() => ComposeFront([.. FrontBandCache.Select(c => c.Value)]), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The front pilot: band <paramref name="band"/>'s level with the default seed (band frame, built once).</summary>
    public static AshfallDraft5 BuildFrontBand(int band) => FrontBandCache[band].Value;

    /// <summary>An uncached run of band <paramref name="band"/> with <paramref name="seed"/> (the seed checks).</summary>
    public static AshfallDraft5 RebuildFront(int band, uint seed) => AshfallDraft5Pass.Run(AshfallFront.Plan(band, seed));

    /// <summary>An uncached run of a front band's plan (the tool's, which keeps the plan for its overlays).</summary>
    public static AshfallDraft5 BuildFrontFrom(AshfallFieldPlan plan) => AshfallDraft5Pass.Run(plan);

    /// <summary>
    /// The empty frame of a band's plan as a level (gate A measures it): the ground, the boundary and its backdrop, and the authored pieces as the through variants keep them: draft 10's stronghold, twin towers and compound walls without
    /// what meets the avenue's strip, and the through pieces (the compound's walls with their gates, the north gate jamb, the keep's wings) as plain boxes at their final size. No fabric, no platforms, no rubble.
    /// </summary>
    public static AshfallLevel BuildFrontFrame(AshfallFieldPlan frame)
    {
        var b = new AshfallBuilder();
        b.Box(Ground, -Half, -Half, -2f, Half, Half, BlockTop);
        BoundaryLayout(b);
        foreach (var s in AshfallFront.ThroughPieces()) b.Oriented(s.Family == "keepwing" ? Stronghold : Wall, s.Centre.X, s.Centre.Y, BlockTop, BlockTop + s.Size.Z, s.Size.X, s.Size.Y, s.YawStep);
        var parts = new List<AshfallPart>(b.Parts);
        var d10 = BuildDraft5();
        var ranges = d10.Dressing.ItemParts;
        foreach (var p in d10.Placements)
        {
            if (p.Item < 0 || !AshfallDraft5Pass.IsTransplanted(p.Group)) continue;
            var (first, count) = ranges[p.Item];
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (int k = first; k < first + count; k++) foreach (var v in d10.Level.Parts[k].Corners) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            if (AshfallFront.InThroughRoad(min, max)) continue;
            for (int k = first; k < first + count; k++) parts.Add(d10.Level.Parts[k]);
        }
        return new AshfallLevel(parts, frame.Points, frame.Areas, frame.Spawns, HashOf(parts, frame.HashVersion)) { Layout = frame.Layout };
    }

    /// <summary>
    /// The composed world (<see cref="AshfallLayout.Front"/>): the bands' parts, points (A, B, C of each band in band order), start areas (each band's two), spawns and dressing, band <c>k</c> moved <see cref="AshfallFront.OffsetOf"/>(k)
    /// along x. The hash is over the composed solid corners, salted with <see cref="AshfallFront.ComposedHashVersion"/>.
    /// </summary>
    public static AshfallDraft5 ComposeFront(IReadOnlyList<AshfallDraft5> bands)
    {
        var parts = new List<AshfallPart>(); var points = new List<ControlPoint>(); var areas = new List<StartArea>(); var spawns = new List<Vector3>();
        var items = new List<AshfallDressItem>(); var itemParts = new List<(int, int)>(); var hidden = new List<int>(); var structures = new List<AshfallStructure>(); var flags = new List<string>();
        var placements = new List<AshfallPlacement>();
        for (int k = 0; k < bands.Count; k++)
        {
            var d = bands[k];
            var off = new Vector3(AshfallFront.OffsetOf(k), 0f, 0f);
            int partBase = parts.Count, itemBase = items.Count;
            foreach (var part in d.Level.Parts) parts.Add(k == 0 ? part : part with { Corners = [.. part.Corners.Select(v => v + off)] });
            points.AddRange(d.Level.Points.Select(p => p with { Center = p.Center + off }));
            areas.AddRange(d.Level.Areas.Select(a => a with { Center = a.Center + off }));
            spawns.AddRange(d.Level.Spawns.Select(s => s + off));
            items.AddRange(d.Dressing.Items.Select(i => i with { Position = i.Position + off }));   // Part (the graybox structure a skin stood for) stays the band's own
            itemParts.AddRange(d.Dressing.ItemParts.Select(r => r.Count == 0 ? (r.First, 0) : (r.First + partBase, r.Count)));
            hidden.AddRange(d.Dressing.HiddenParts.Select(h => h + partBase));
            structures.AddRange((d.Dressing.Structures ?? []).Select(s => new AshfallStructure([.. s.Parts.Select(q => q + partBase)], s.Min + off, s.Max + off)));
            flags.AddRange(d.Dressing.Flags.Select(f => $"band {k}: {f}"));
            placements.AddRange(d.Placements.Select(p => p.Item >= 0 ? p with { Item = p.Item + itemBase } : p));
        }
        var level = new AshfallLevel(parts, points, areas, spawns, HashOf(parts, AshfallFront.ComposedHashVersion)) { Layout = AshfallLayout.Front };
        var dressing = new AshfallDressing(items, hidden, DressVersion, structures) { ItemParts = itemParts, Flags = flags };
        return new AshfallDraft5(level, dressing, bands[0].Graybox, placements);
    }

    /// <summary>
    /// The composed world back into its bands, each in its band frame (the client draws one band at a time at its offset): parts, points, areas, spawns and dressing of band <c>k</c>, moved back by its offset. A part belongs to the band
    /// whose square its first corner stands in; the composition keeps them in band order. The bands' levels carry their front layouts (their plans answer <see cref="AshfallLayouts.PlanOf"/>); their hashes are not the band builds' (a float
    /// moved there and back may differ in its last bit; drawing only).
    /// </summary>
    public static IReadOnlyList<(AshfallLevel Level, AshfallDressing Dressing)> SplitFront(AshfallLevel level, AshfallDressing dressing)
    {
        int n = AshfallFront.BandCount;
        var start = new int[n + 1];
        int band = 0;
        for (int i = 0; i < level.Parts.Count && band < n; i++)
            while (band < n - 1 && AshfallFront.BandAt(level.Parts[i].Corners[0].X) > band) start[++band] = i;
        for (int b = band + 1; b <= n; b++) start[b] = level.Parts.Count;
        var result = new List<(AshfallLevel, AshfallDressing)>();
        for (int k = 0; k < n; k++)
        {
            var off = new Vector3(AshfallFront.OffsetOf(k), 0f, 0f);
            int p0 = start[k], p1 = start[k + 1];
            var parts = new List<AshfallPart>(p1 - p0);
            for (int i = p0; i < p1; i++) parts.Add(k == 0 ? level.Parts[i] : level.Parts[i] with { Corners = [.. level.Parts[i].Corners.Select(v => v - off)] });
            var points = level.Points.Skip(3 * k).Take(3).Select(p => p with { Center = p.Center - off }).ToList();
            var areas = level.Areas.Skip(2 * k).Take(2).Select(a => a with { Center = a.Center - off }).ToList();
            var spawns = level.Spawns.Where(s => AshfallFront.BandAt(s.X) == k).Select(s => s - off).ToList();
            var items = new List<AshfallDressItem>(); var ranges = new List<(int, int)>();
            for (int i = 0; i < dressing.Items.Count; i++)
            {
                var it = dressing.Items[i];
                if (AshfallFront.BandAt(it.Position.X) != k) continue;
                var (f, c) = i < dressing.ItemParts.Count ? dressing.ItemParts[i] : (0, 0);
                items.Add(it with { Position = it.Position - off });
                ranges.Add(c == 0 ? (f, 0) : (f - p0, c));
            }
            var hidden = dressing.HiddenParts.Where(h => h >= p0 && h < p1).Select(h => h - p0).ToList();
            var structures = (dressing.Structures ?? []).Where(s => s.Parts.Count > 0 && s.Parts[0] >= p0 && s.Parts[0] < p1).Select(s => new AshfallStructure([.. s.Parts.Select(q => q - p0)], s.Min - off, s.Max - off)).ToList();
            var bandLevel = new AshfallLevel(parts, points, areas, spawns, HashOf(parts, AshfallFront.HashVersionOf(k))) { Layout = AshfallFront.LayoutOf(k) };
            result.Add((bandLevel, new AshfallDressing(items, hidden, dressing.Version, structures) { ItemParts = ranges, Flags = [] }));
        }
        return result;
    }

    /// <summary>Half the opening drawn in the boundary wall where the main avenue leaves a band (m: the avenue's 24 m).</summary>
    public const float FrontOpeningHalf = 12f;

    /// <summary>
    /// The owner, 2026-11-16: where the avenue leaves a band, a visual opening in the 64 m boundary wall. A copy of the band's level for drawing only (the client's): the north and south boundary walls' parts (one box each) become two
    /// boxes either side of x = +-<see cref="FrontOpeningHalf"/>, at the same part index (the dressing's hidden parts stay right); every other part, the points, areas, spawns and the hash are the level's. The collision is the composed
    /// level's and stays closed: nobody walks through.
    /// </summary>
    public static AshfallLevel OpenFrontEdges(AshfallLevel level)
    {
        var parts = level.Parts.ToArray();
        int opened = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Kind != AshfallKind.Boundary || p.Corners.Length != 36) continue;
            Vector3 lo = p.Corners[0], hi = p.Corners[0];
            foreach (var v in p.Corners) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            bool edge = lo.X < -FrontOpeningHalf && hi.X > FrontOpeningHalf && (lo.Y >= Half - 0.01f || hi.Y <= -Half + 0.01f);
            if (!edge) continue;
            parts[i] = p with { Corners = [.. BoxCorners(lo, new Vector3(-FrontOpeningHalf, hi.Y, hi.Z)), .. BoxCorners(new Vector3(FrontOpeningHalf, lo.Y, lo.Z), hi)] };
            opened++;
        }
        return opened == 0 ? level : level with { Parts = parts };
    }

    static Vector3[] BoxCorners(Vector3 l, Vector3 h)
    {
        Vector3[] q = [new(l.X, l.Y, l.Z), new(h.X, l.Y, l.Z), new(h.X, h.Y, l.Z), new(l.X, h.Y, l.Z), new(l.X, l.Y, h.Z), new(h.X, l.Y, h.Z), new(h.X, h.Y, h.Z), new(l.X, h.Y, h.Z)];
        int[][] faces = [[3, 2, 1, 0], [4, 5, 6, 7], [0, 1, 5, 4], [2, 3, 7, 6], [3, 0, 4, 7], [1, 2, 6, 5]];
        var c = new List<Vector3>(36);
        foreach (var f in faces) { c.Add(q[f[0]]); c.Add(q[f[1]]); c.Add(q[f[2]]); c.Add(q[f[0]]); c.Add(q[f[2]]); c.Add(q[f[3]]); }
        return [.. c];
    }
}
