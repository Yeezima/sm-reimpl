using System.Numerics;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>One burning spot of the storm look (visual only, no collision, no gameplay): where, how big (1 = a small fire), whether it carries a tall smoke plume, and the way out from the ruin it burns by (unit, game frame).</summary>
public readonly record struct AshfallFireSpot(Vector3 Position, float Size, bool Plume, Vector2 Out);

/// <summary>
/// Draft 7's map-wide look, the data side (owner, 2026-11-06: "fires around the map, ashes flying around the sky, dark clouds in the skybox, light poking through"; ours, placeholder demonstration; docs/musou-ashfall-graybox.md
/// "Draft 7"). The client draws it (<c>AshfallStorm</c>); here only the deterministic places of the fires, so every machine burns the same ruins. Nothing here collides or touches the simulation.
/// </summary>
public static class AshfallAtmosphere
{
    /// <summary>Small fires at the feet of ruins, and tall smoke plumes out of the bigger ruins and towers (ours).</summary>
    public const int SmallFires = 34, Plumes = 9;

    /// <summary>Least distance between two fires, and between two plumes (ours: spread over the map).</summary>
    public const float FireSpacing = 16f, PlumeSpacing = 55f;

    /// <summary>A fire keeps this far from a capture zone's edge (ours: the objectives stay readable).</summary>
    public const float ZoneClear = 4f;

    static readonly HashSet<string> Ruins = ["AC-001", "AC-003", "AC-004", "AC-005", "AC-006", "AC-030", "AC-041", "AC-051", "AC-052", "AC-044", "AC-019", "AC-021", "AC-023", "AC-053", "AC-061"];
    static readonly HashSet<string> PlumeRuins = ["AC-003", "AC-005", "AC-030", "AC-044", "AC-051", "AC-052"];

    /// <summary>The fire spots of a level and its dressing: plumes first, then the small fires; the same list on every machine (integer hashes, total-order sorts).</summary>
    public static IReadOnlyList<AshfallFireSpot> FireSpots(AshfallLevel level, AshfallDressing dressing)
    {
        var boxes = AshfallOverlap.Boxes(level, dressing);
        var byItem = boxes.GroupBy(b => b.Item).ToDictionary(g => g.Key, g => g.Select(x => x.Box).ToList());
        uint seed = AshfallBuilder.Mix(AshfallMap.VariationSeed, 0xF12Eu);
        var plumeCands = new List<(uint Key, Vector3 At, float Size, Vector2 Out)>();
        var fireCands = new List<(uint Key, Vector3 At, float Size, Vector2 Out)>();
        for (int i = 0; i < dressing.Items.Count; i++)
        {
            var it = dressing.Items[i];
            if (it.Role != AshfallDressRole.Prop || !Ruins.Contains(it.Asset) || !byItem.TryGetValue(i, out var mine)) continue;
            var centre = new Vector2(it.Position.X, it.Position.Y);
            if (PlumeRuins.Contains(it.Asset) && MathF.Min(it.Size.X, it.Size.Y) >= 9f)
            {
                float top = mine.Where(b => Inside(b, centre, 0f)).Select(b => b.Z1).DefaultIfEmpty(AshfallMap.SurfaceAt(centre.X, centre.Y)).Max();
                plumeCands.Add((AshfallBuilder.Mix(seed, (uint)i * 16u), new Vector3(centre.X, centre.Y, top), 1.6f + 0.4f * (AshfallBuilder.Mix(seed, (uint)i * 16u + 1u) % 100u) / 100f, centre.LengthSquared() > 1f ? -Vector2.Normalize(centre) : -Vector2.UnitY));
            }
            float s = MathF.Sin(it.Yaw), c = MathF.Cos(it.Yaw);
            var ax = new Vector2(c, s); var ay = new Vector2(-s, c);
            for (uint k = 0; k < 4; k++)
            {
                uint h = AshfallBuilder.Mix(seed, (uint)i * 16u + 2u + k);
                float along = ((h % 1000u) / 1000f - 0.5f) * 0.8f;   // along the side, within its middle 80 %
                var o = k switch { 0 => -ay, 1 => -ax, 2 => ay, _ => ax };
                var p = (k % 2 == 0)
                    ? centre + ay * ((k == 0 ? -1f : 1f) * (it.Size.Y / 2f + 0.9f)) + ax * (along * it.Size.X)
                    : centre + ax * ((k == 1 ? -1f : 1f) * (it.Size.X / 2f + 0.9f)) + ay * (along * it.Size.Y);
                fireCands.Add((h, new Vector3(p.X, p.Y, AshfallMap.SurfaceAt(p.X, p.Y)), 1.2f + 0.7f * ((h >> 10) % 100u) / 100f, o));
            }
        }
        var spots = new List<AshfallFireSpot>();
        foreach (var (_, at, size, o) in plumeCands.OrderBy(x => x.Key).ThenBy(x => x.At.X).ThenBy(x => x.At.Y))
        {
            if (spots.Count >= Plumes) break;
            if (!Clear(level, at) || spots.Any(q => Vector2.Distance(new(q.Position.X, q.Position.Y), new(at.X, at.Y)) < PlumeSpacing)) continue;
            spots.Add(new AshfallFireSpot(at, size, true, o));
        }
        int fires = 0;
        foreach (var (_, at, size, o) in fireCands.OrderBy(x => x.Key).ThenBy(x => x.At.X).ThenBy(x => x.At.Y))
        {
            if (fires >= SmallFires) break;
            if (!Clear(level, at) || boxes.Any(b => Inside(b.Box, new(at.X, at.Y), 0.3f) && b.Box.Z0 < at.Z + 1.5f && b.Box.Z1 > at.Z + 0.1f)) continue;
            if (spots.Any(q => Vector2.Distance(new(q.Position.X, q.Position.Y), new(at.X, at.Y)) < FireSpacing)) continue;
            spots.Add(new AshfallFireSpot(at, size, false, o));
            fires++;
        }
        return spots;
    }

    /// <summary>Whether a point lies within a box's footprint grown by <paramref name="grow"/> metres.</summary>
    public static bool Inside(AshfallBox b, Vector2 p, float grow)
    {
        var d = p - new Vector2(b.Cx, b.Cy);
        return MathF.Abs(Vector2.Dot(d, b.AxisX)) <= b.Length / 2f + grow && MathF.Abs(Vector2.Dot(d, b.AxisY)) <= b.Depth / 2f + grow;
    }

    /// <summary>Outside the capture zones (plus <see cref="ZoneClear"/>), the start areas, the lanes and the paved road, and 8 m inside the boundary.</summary>
    public static bool Clear(AshfallLevel level, Vector3 at)
    {
        if (MathF.Abs(at.X) > AshfallMap.Half - 8f || MathF.Abs(at.Y) > AshfallMap.Half - 8f) return false;
        foreach (var p in level.Points)
            if (Vector2.Distance(new(p.Center.X, p.Center.Y), new(at.X, at.Y)) < p.Radius + ZoneClear) return false;
        foreach (var a in level.Areas)
            if (MathF.Abs(at.X - a.Center.X) < a.Size.X / 2f && MathF.Abs(at.Y - a.Center.Y) < a.Size.Y / 2f) return false;
        const float lane = 4.2f;
        foreach (float v in new[] { -92f, 92f, -232f, 232f })
            if (MathF.Abs(at.X - v) < lane || MathF.Abs(at.Y - v) < lane) return false;
        if (MathF.Abs(at.X) < 6f && at.Y > -160f && at.Y < 80f) return false;
        if (MathF.Abs(at.Y) < 6f && MathF.Abs(at.X) < 80f) return false;
        return true;
    }
}
