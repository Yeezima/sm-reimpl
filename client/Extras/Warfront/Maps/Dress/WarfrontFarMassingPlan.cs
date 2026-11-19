using System;
using System.Collections.Generic;
using System.Numerics;

namespace SmClient.Extras.Warfront.Dress;

/// <summary>
/// The far massing of a lane node (slice "dress", part 1; every number ours, an estimate): low blocks for the bands still to cross, between the neighbouring band's copy (520 m north of a node-1 or node-2 band) and the far spire, in the band's own frame
/// (X east, Y north, the avenue along Y at x +-12). The outskirts first, as low sheds, then the core, as bigger and taller blocks: rows every <see cref="RowSpacing"/> m, the number of blocks and their height growing toward the spire (<see cref="Density"/>),
/// the avenue kept clear, a line of taller blocks along the avenue's sides in the last 300 m (the processional way to the spire). Client scenery only: no collision, in no hash.
/// The numbers that fix the layout are passed in (the front's constants live in Musou's scenery file, which this slice does not edit), so the plan is Godot-free and testable.
/// </summary>
public static class WarfrontFarMassingPlan
{
    /// <summary>Distance between rows of blocks (m), the avenue's clear half-width (m), the blocks' side spread from the avenue (m) and the clearance kept short of the spire (m); ours.</summary>
    public const float RowSpacing = 34f, AvenueClear = 20f, Spread = 235f, SpireClearance = 140f;

    /// <summary>One far block: centre of its bottom (x, y), footprint, height and yaw.</summary>
    public readonly record struct Block(float X, float Y, float SizeX, float SizeY, float Height, float Yaw);

    /// <summary>The blocks' density in 0..1 at fraction <paramref name="t"/> of the way to the spire (0 at the first row, 1 at the last): how crowded the ground is (blocks a side a row, 2 to 8).</summary>
    public static int Density(float t) => 2 + (int)MathF.Round(6f * Math.Clamp(t, 0f, 1f));

    /// <summary>The blocks between <paramref name="y0"/> and <paramref name="y1"/> (band frame), seeded by <paramref name="seed"/>.</summary>
    public static IReadOnlyList<Block> Blocks(float y0, float y1, uint seed)
    {
        var list = new List<Block>();
        if (y1 <= y0) return list;
        int rows = (int)((y1 - y0) / RowSpacing);
        for (int r = 0; r < rows; r++)
        {
            float t = rows <= 1 ? 1f : r / (float)(rows - 1);
            float y = y0 + r * RowSpacing;
            foreach (int side in new[] { -1, 1 })
            {
                int n = Density(t);
                for (int i = 0; i < n; i++)
                {
                    uint h = DressHash.Mix(seed + (uint)(r * 977), (uint)(i * 2 + (side > 0 ? 1 : 0)));
                    float sx = 16f + (h & 15u) * 1.8f + t * 20f;                // 16..63 m, wider toward the core
                    float sy = 16f + ((h >> 4) & 15u) * 1.2f + t * 10f;
                    float height = (10f + 0.05f * y) * (0.6f + ((h >> 8) & 15u) / 15f * 0.9f) + (((h >> 12) & 15u) == 0u ? 60f * t : 0f);   // tall enough to stand above the nearer copy's props from the ground (about 3 degrees: 0.05 of the distance): low hive blocks in the outskirts, towers toward the core
                    float slot = AvenueClear + sx / 2f + (i + ((h >> 16) & 7u) / 10f) * (Spread / n);
                    float x = side * slot;
                    float jy = (((h >> 20) & 15u) - 8f) * 1.5f;
                    list.Add(new Block(x, y + jy, sx, sy, height, (((h >> 24) & 7u) == 0u) ? 0.3f : 0f));
                }
            }
            if (t > 0.6f)   // the processional way: a taller line along the avenue's sides
                foreach (int side in new[] { -1, 1 })
                {
                    uint h = DressHash.Mix(seed ^ 0xA5A5u, (uint)(r * 2 + (side > 0 ? 1 : 0)));
                    list.Add(new Block(side * (AvenueClear + 13f), y + 6f, 22f, 28f, 60f + 140f * t + (h & 7u) * 8f, 0f));
                }
        }
        return list;
    }

    /// <summary>The first and last row's y for a band's node: past the neighbouring band's copy (or just past the band's own edge when there is none) up to <paramref name="spireDistance"/> minus the clearance.</summary>
    public static (float Y0, float Y1) Range(bool hasNorthNeighbour, float neighbourOffset, float lineHalf, float spireDistance)
        => ((hasNorthNeighbour ? neighbourOffset + lineHalf + 30f : lineHalf + 60f), spireDistance - SpireClearance);
}
