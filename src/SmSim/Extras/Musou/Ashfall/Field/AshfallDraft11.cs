using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Draft 11 (the battlefield layout; docs/musou-ashfall-graybox.md "Draft 11 battlefield layout"): what the kit pass does differently on the field layout's graybox. Every standing rule of drafts 7 to 10 runs as before
/// (no overlaps, no stacked models, roadways clear, stair feet and landings clear, walkable stairs only, the stair-access rule, bridges on supports, bare platform edges); the corridors are the roadways and the protected
/// places come from the plan.
/// </summary>
internal static partial class AshfallDraft5Pass
{
    /// <summary>The field's protected places: the start areas and assembly areas (rectangles), the capture zones with their open rings, the spawn markers (discs), the stronghold's breaches (as draft 10).</summary>
    static void BuildFieldBanned(Ctx c, AshfallLevel g5, AshfallFieldPlan f)
    {
        foreach (var a in g5.Areas) c.BannedRects.Add(new(a.Center.X - a.Size.X / 2f, a.Center.Y - a.Size.Y / 2f, a.Center.X + a.Size.X / 2f, a.Center.Y + a.Size.Y / 2f));
        foreach (var (_, r) in f.Assembly) c.BannedRects.Add(r);
        foreach (var p in g5.Points) c.BannedDiscs.Add(new(p.Center.X, p.Center.Y, p.Radius + f.Params.ObjectiveClear));
        foreach (var s in g5.Spawns) c.BannedDiscs.Add(new(s.X, s.Y, 4f));
        uint seed = 7000;
        foreach (var (_, alongX, t0, t1, lo, hi, _, _) in Legs)
            foreach (var (a, b) in LegLayout(lo, hi, seed += 101).Breaches)
                c.BreachRects.Add(alongX ? new(a, t0, b, t1) : new(t0, a, t1, b));
    }

    /// <summary>
    /// The authored pieces draft 11 takes from draft 10 as they stand there (owner: keep the stronghold's identity and the Imperial base; ask before changing the fortress): the stronghold's ring with its breaches, the keep
    /// (AC-044, three dead flights, as authored), its tower, the stair towers, the keep bridge, the terraces and outer walls, the twin towers and their bridge onto the west rampart, and the compound's five walls.
    /// </summary>
    internal static bool IsTransplanted(string group) =>
        group.StartsWith("ring ", StringComparison.Ordinal) || group.StartsWith("terrace", StringComparison.Ordinal) || group.StartsWith("east outer wall", StringComparison.Ordinal) || group.StartsWith("compound ", StringComparison.Ordinal)
        || group is "keep" or "Keep tower" or "Stair tower SW" or "Stair tower SE" or "keep bridge" or "Twin tower west" or "Twin tower east" or "twin bridge";

    /// <summary>
    /// Draft 11: copies the fortress and the compound from the draft-10 level, item for item and collision part for collision part (the same bits), and registers them for every rule of the items after them (overlap,
    /// drawn boxes, stair zones, narrow gaps), so the field's added platforms, rubble props and bridges treat them as draft 10's later items did. Re-placing them on the field would settle them differently: in draft 10 the
    /// street grid's roads and neighbours pushed the gatehouse, the terraces, the outer walls and the ring's ends, and the seeded pick runs on a counter shared with every placement.
    /// </summary>
    static void Transplant(Ctx c, AshfallDraft5 d10)
    {
        var items = d10.Dressing.Items; var ranges = d10.Dressing.ItemParts!;
        int moved = 0, stairs = 0;
        foreach (var p in d10.Placements)
        {
            if (p.Item < 0 || !IsTransplanted(p.Group)) continue;
            var item = items[p.Item];
            var (first, count) = ranges[p.Item];
            int index = c.Items.Count, start = c.Parts.Count;
            c.Items.Add(item);
            int yaw = ((int)MathF.Round(item.Yaw / (MathF.PI / 12f)) % 24 + 24) % 24;
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (int k = first; k < first + count; k++)
            {
                var part = d10.Level.Parts[k];
                c.Parts.Add(part);
                foreach (var v in part.Corners) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                WBox b;
                if (AshfallBox.Of(part) is { } ab) b = new WBox(new Vector3(ab.Cx, ab.Cy, (ab.Z0 + ab.Z1) / 2f), new Vector3(ab.Length / 2f, ab.Depth / 2f, (ab.Z1 - ab.Z0) / 2f), ab.YawStep);
                else if (part.Kind == AshfallKind.Stair) { b = RampBox(item, yaw, part.Corners); stairs++; }
                else continue;
                c.AllBoxes.Add((index, b));
                AddLow(c, index, b);
                if (!BridgeIds.Contains(item.Asset)) AddSolid(c, index, b);
            }
            c.ItemParts.Add((start, count));
            c.Uses[item.Asset] = c.Uses.GetValueOrDefault(item.Asset) + 1;
            c.Placed.Add((item.Asset, new Vector2(item.Position.X, item.Position.Y)));
            var spec = new Spec(p.Group, p.Family, min, max, null, []);
            var zones = AshfallKitCollision.Stairs(item.Asset).Count > 0 ? Stairs(item, yaw, item.Asset, Boxes(item, yaw, item.Asset)).Zones : [];
            AddDraft9(c, spec, index, item, yaw, item.Asset, zones);
            c.Structures.Add(new AshfallStructure([.. Enumerable.Range(start, count)], min, max));
            c.Log.Add(new(p.Group, p.Family, item.Asset, index, $"transplanted from draft 10 ({p.Outcome})"));
            moved++;
        }
        c.Flags.Add($"draft 11: {moved} authored pieces (the stronghold, its twin towers and bridges, the Imperial compound's walls) taken from draft 10 as they stand there, the same collision bits ({stairs} stair ramps among them)");
    }

    /// <summary>The way from a point to the nearest corridor's centreline, on the plan's axes (a building's front faces the route it stands beside).</summary>
    static Vector2 FieldStreetSide(AshfallFieldPlan f, Vector2 p)
    {
        Vector2 best = Vector2.UnitY; float dist = float.MaxValue;
        foreach (var k in f.Corridors)
        {
            var ab = k.B - k.A; float t = Math.Clamp(Vector2.Dot(p - k.A, ab) / ab.LengthSquared(), 0f, 1f);
            var q = k.A + ab * t; float d = Vector2.Distance(p, q);
            if (d >= dist || d < 1e-3f) continue;
            dist = d;
            var v = q - p;
            best = MathF.Abs(v.X) >= MathF.Abs(v.Y) ? new Vector2(MathF.Sign(v.X), 0f) : new Vector2(0f, MathF.Sign(v.Y));
        }
        return best;
    }
}

internal static partial class AshfallDressPass
{
    /// <summary>
    /// Draft 11's surface dressing (visual only, from <see cref="AshfallFieldParams.SurfaceSeed"/> alone): worn ash slabs (AC-062..067, 16 m) along the army corridors every 40 m and at their junctions, so the routes read on
    /// the open ground; no slab meets another (no shared plane to flicker). Changing the surface seed changes only these tiles' variants and turns, never collision or routes.
    /// </summary>
    public static void FieldSurfaceItems(AshfallFieldPlan f, List<AshfallDressItem> items)
    {
        uint seed = AshfallBuilder.Mix(f.Params.SurfaceSeed, 0x5F1E1Du);
        const float size = 16f, every = 40f;
        var placed = new List<Vector4>();
        int n = 0;
        foreach (var k in f.Corridors)
        {
            var dir = Vector2.Normalize(k.B - k.A);
            int yaw = k.YawStep;
            for (float t = 0f; t <= k.Length + 1e-3f; t += every)
            {
                var at = k.A + dir * t;
                var r = AshfallField.Footprint(at, new Vector3(size, size, 0f), yaw);
                if (placed.Any(o => o.X < r.Z + 1f && r.X < o.Z + 1f && o.Y < r.W + 1f && r.Y < o.W + 1f)) continue;
                if (f.Points.Any(p => Vector2.Distance(new Vector2(p.Center.X, p.Center.Y), at) < p.Radius + size)) continue;   // the capture rings keep their own look
                uint h = AshfallBuilder.Mix(seed, (uint)n++);
                items.Add(Slab(AshSlabs[(int)(h % (uint)AshSlabs.Length)], at.X, at.Y, AshfallMap.BlockTop, yaw + 6 * (int)((h >> 8) % 4u), size, size));
                placed.Add(r);
            }
        }
    }
}
