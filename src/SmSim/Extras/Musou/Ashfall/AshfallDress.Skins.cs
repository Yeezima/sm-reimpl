using System.Numerics;
using static SmSim.Extras.Musou.Ashfall.AshfallKind;

namespace SmSim.Extras.Musou.Ashfall;

// Skins (kit dressing, draft 1; all ours): kit models drawn exactly over graybox boxes. The box stays the collision; the client hides it (HiddenParts) in the kit look.
internal static partial class AshfallDressPass
{
    /// <summary>Whether the model's long raw axis is glTF Z (the item then swaps X and Z so the long side runs along the box's length).</summary>
    public static bool Swap(AshfallKitModel m) => m.RawSize.Z > m.RawSize.X * 1.2f;

    readonly record struct TileFit(int N, int Rows, float Ratio);

    /// <summary>
    /// How to tile a box of L x D x H (length, depth, height) with copies of the model: N copies along the length, Rows stacked, the fewest that keep the per-axis scales within 1:2.2 of each other
    /// (else within 1:3). Null when no tiling does.
    /// </summary>
    static TileFit? Fit(AshfallKitModel m, float L, float D, float H)
    {
        var r = m.RawSize;
        bool swap = Swap(m);
        float rl = swap ? r.Z : r.X, rd = swap ? r.X : r.Z, rh = r.Y;
        foreach (float cap in new[] { 2.2f, 3.0f })
        {
            TileFit? best = null;
            for (int rows = 1; rows <= 12; rows++)
                for (int n = 1; n <= 64; n++)
                {
                    float sx = L / n / rl, sy = D / rd, sz = H / rows / rh;
                    float ratio = MathF.Max(sx, MathF.Max(sy, sz)) / MathF.Min(sx, MathF.Min(sy, sz));
                    if (ratio > cap) continue;
                    if (best is not { } b || n * rows < b.N * b.Rows || (n * rows == b.N * b.Rows && ratio < b.Ratio)) best = new TileFit(n, rows, ratio);
                }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>
    /// Tiles one wall-like box (centre, yaw step, length x depth x height from <paramref name="z0"/>) with a candidate model that fits at no more than twice the lowest triangle cost (items x the model's triangles; the hash picks among those);
    /// nothing is added when none fits.
    /// </summary>
    static bool TryWall(List<AshfallDressItem> into, string[] candidates, uint hash, Vector2 centre, int yawStep, float L, float D, float z0, float H, int part, Vector2? front = null)
    {
        if (L < 0.4f || D < 0.2f || H < 0.3f) return false;
        var fits = new List<(AshfallKitModel M, TileFit Fit, long Cost)>();
        foreach (string id in candidates)
        {
            var m = AshfallKit.Get(id);
            if (Fit(m, L, D, H) is { } fit) fits.Add((m, fit, (long)fit.N * fit.Rows * m.Triangles));
        }
        if (fits.Count == 0) return false;
        long cheapest = fits.Min(f => f.Cost);
        var near = fits.Where(f => f.Cost <= 2 * cheapest).ToList();   // variety within twice the cheapest triangle cost
        var (best, bestFit, _) = near[(int)(hash % (uint)near.Count)];
        var (s, co) = AshfallBuilder.Trig(yawStep);
        var ax = new Vector2(co, s);
        // The model's front (glTF +Z) faces local -Y at yaw 0 (the fit is a rotation, not a mirror): turn the items half a turn when the wanted front direction lies on the local +Y side.
        int itemYaw = front is { } f && Vector2.Dot(new Vector2(-s, co), f) > 0f ? yawStep + 12 : yawStep;
        float lp = L / bestFit.N, rh = H / bestFit.Rows;
        for (int row = 0; row < bestFit.Rows; row++)
            for (int i = 0; i < bestFit.N; i++)
            {
                var p = centre + ax * (-L / 2f + (i + 0.5f) * lp);
                into.Add(new AshfallDressItem(best.Id, new Vector3(p.X, p.Y, z0 + row * rh), Yaw(itemYaw), new Vector3(lp, D, rh), Swap(best), AshfallDressRole.Skin, part));
            }
        return true;
    }

    static bool TryBox(List<AshfallDressItem> into, string[] candidates, uint hash, AshfallBox box, int part, Vector2? front = null) =>
        TryWall(into, candidates, hash, new Vector2(box.Cx, box.Cy), box.YawStep, box.Length, box.Depth, box.Z0, box.Height, part, front ?? StreetSide(box.Cx, box.Cy));

    /// <summary>The direction from (x, y) to the nearest road centre line (the avenues at +-92 and the perimeter at +-232, both ways): wall fronts face the street (ours).</summary>
    static Vector2 StreetSide(float x, float y)
    {
        float bx = 1e9f, by = 1e9f;
        foreach (float c in new[] { -232f, -92f, 92f, 232f })
        {
            if (MathF.Abs(c - x) < MathF.Abs(bx)) bx = c - x;
            if (MathF.Abs(c - y) < MathF.Abs(by)) by = c - y;
        }
        return MathF.Abs(bx) <= MathF.Abs(by) ? new Vector2(bx >= 0f ? 1f : -1f, 0f) : new Vector2(0f, by >= 0f ? 1f : -1f);
    }

    static readonly string[] WallTall = ["AC-005", "AC-004", "AC-006", "AC-002", "AC-001"], WallMid = ["AC-004", "AC-006", "AC-001", "AC-005", "AC-002"],
        WallLow = ["AC-001", "AC-006", "AC-004", "AC-003"], WallSill = ["AC-001", "AC-017", "AC-014", "AC-011"], WallThick = ["AC-003", "AC-001", "AC-006"],
        CoverThin = ["AC-011", "AC-014", "AC-017"], CoverThick = ["AC-017", "AC-014", "AC-018"], CoverCrater = ["AC-011", "AC-014", "AC-017"],
        RubbleLow = ["AC-020", "AC-024", "AC-025"], RubbleTall = ["AC-019", "AC-021", "AC-023"], Obelisk = ["AC-010", "AC-030"], Plinth = ["AC-028"],
        Keep = ["AC-006", "AC-004", "AC-001"];

    static void Skins(IReadOnlyList<AshfallPart> parts, int count, List<AshfallDressItem> items, List<int> hidden)
    {
        for (int i = 0; i < count; i++)
        {
            var part = parts[i];
            if (!part.Solid || AshfallBox.Of(part) is not { } box) continue;
            uint h = AshfallBuilder.Mix(0xA5F1u, (uint)i);
            bool done = false;
            switch (part.Kind)
            {
                case Wall when box.Depth <= 4.1f && box.Length >= 2f && box.Length * box.Height >= 40f:
                    // Small or narrow pieces (face under 40 square metres, or under 2 m along the face) get no skin and stay graybox, drawn by the client as stone.
                    done = TryBox(items, box.Height >= 10f ? WallTall : box.Height >= 5f ? WallMid : box.Height >= 2.2f ? (box.Depth >= 2f && box.Length <= 6f ? WallThick : WallLow) : WallSill, h, box, i);
                    break;
                case Cover when box.Depth <= 3f && box.Height >= 0.45f:
                    done = TryBox(items, box.Cx < -104f && MathF.Abs(box.Cy) < 80f ? CoverCrater : box.Depth >= 1.8f ? CoverThick : CoverThin, h, box, i);
                    break;
                case Rubble:
                    done = TryBox(items, box.Height <= 0.4f ? RubbleLow : RubbleTall, h, box, i);
                    break;
                case Perch when box.Length <= 4.5f && box.Height >= 8f:
                    done = TryBox(items, Obelisk, h, box, i);   // the broken obelisks (A's and the small monument's), jump-only perches
                    break;
                case Floor when box.Z0 == 0f && box.Depth >= 8f && box.Length <= 17f && box.Height <= 3.01f:
                    done = TryBox(items, Plinth, h, box, i);    // A's plinth, the small monument's plinth, the gun platform
                    break;
                case Boundary:
                    done = BoundaryTiers(items, h, box, i);
                    break;
                case Stronghold:
                    done = Ring(items, Keep, h, box, i);
                    break;
            }
            if (done && part.Kind is Cover or Rubble) hidden.Add(i);   // only Cover and Rubble skins hide their box; the client draws the others' boxes as a stone backing (draft 2)
        }
    }

    static readonly (string Id, float Tier)[] BoundaryModels = [("AC-001", 10f), ("AC-004", 12f), ("AC-005", 14f)];

    /// <summary>
    /// The boundary wall tiled along its length in pieces of about 12 m (inner face towards the play area), each piece stacked in tiers up to the wall's top, the model of every tier picked by the hash
    /// (AC-001 10 m, AC-004 12 m and AC-005 14 m tiers; a last tier shorter than 4 m is shared with the one below). Every tier keeps its scales within 1:3.
    /// </summary>
    static bool BoundaryTiers(List<AshfallDressItem> into, uint hash, AshfallBox box, int part)
    {
        int n = Math.Max(1, (int)MathF.Round(box.Length / 12f));
        float lp = box.Length / n;
        var toward = -Vector2.Normalize(new Vector2(box.Cx, box.Cy));
        var (s, co) = AshfallBuilder.Trig(box.YawStep);
        var ax = new Vector2(co, s);
        int step = Vector2.Dot(new Vector2(-s, co), toward) > 0f ? (box.YawStep + 12) % 24 : box.YawStep;   // the front (glTF +Z) faces local -Y
        var added = new List<AshfallDressItem>();
        for (int i = 0; i < n; i++)
        {
            var centre = new Vector2(box.Cx, box.Cy) + ax * (-box.Length / 2f + (i + 0.5f) * lp);
            uint h = AshfallBuilder.Mix(hash, (uint)i);
            float z = box.Z0, left = box.Height;
            for (uint t = 0; left > 0.01f; t++)
            {
                uint ht = AshfallBuilder.Mix(h, t + 1u);
                bool placed = false;
                for (int c = 0; c < BoundaryModels.Length && !placed; c++)
                {
                    var (id, tier) = BoundaryModels[(int)((ht + (uint)c) % (uint)BoundaryModels.Length)];
                    float th = left <= tier ? left : left - tier < 4f ? left / 2f : tier;
                    var m = AshfallKit.Get(id);
                    var r = m.RawSize;
                    float sx = lp / r.X, sy = box.Depth / r.Z, sz = th / r.Y;
                    if (MathF.Max(sx, MathF.Max(sy, sz)) / MathF.Min(sx, MathF.Min(sy, sz)) > 3f) continue;
                    added.Add(new AshfallDressItem(id, new Vector3(centre.X, centre.Y, z), Yaw(step), new Vector3(lp, box.Depth, th), false, AshfallDressRole.Skin, part));
                    z += th; left -= th; placed = true;
                }
                if (!placed) return false;
            }
        }
        into.AddRange(added);
        return true;
    }

    /// <summary>A thick box tiled along its four vertical faces (a shell 3 m deep; the top is covered by another part or out of sight): all four or none.</summary>
    static bool Ring(List<AshfallDressItem> into, string[] candidates, uint hash, AshfallBox box, int part)
    {
        float d = MathF.Min(3f, MathF.Min(box.Length, box.Depth) / 2f);
        var c = new Vector2(box.Cx, box.Cy);
        var ax = box.AxisX; var ay = box.AxisY;
        var added = new List<AshfallDressItem>();
        bool ok = TryWall(added, candidates, hash, c + ay * (box.Depth / 2f - d / 2f), box.YawStep, box.Length, d, box.Z0, box.Height, part, ay)
            && TryWall(added, candidates, hash, c - ay * (box.Depth / 2f - d / 2f), box.YawStep, box.Length, d, box.Z0, box.Height, part, -ay);
        float inner = box.Depth - 2f * d;
        if (ok && inner > 0.4f)
            ok = TryWall(added, candidates, hash, c + ax * (box.Length / 2f - d / 2f), (box.YawStep + 6) % 24, inner, d, box.Z0, box.Height, part, ax)
                && TryWall(added, candidates, hash, c - ax * (box.Length / 2f - d / 2f), (box.YawStep + 6) % 24, inner, d, box.Z0, box.Height, part, -ax);
        if (ok) into.AddRange(added);
        return ok;
    }
}
