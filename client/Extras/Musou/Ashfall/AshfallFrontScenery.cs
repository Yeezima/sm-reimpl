using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;
using V3 = System.Numerics.Vector3;

namespace SmClient;

/// <summary>
/// The Musou front pilot's scenery outside a band's playable square (docs/musou-ashfall-graybox.md "Front pilot", F3): client only, no collision, never in any hash. In the band's own frame (game space, Z up), under the band's node:
/// the ground beyond the boundary out to <see cref="Reach"/> m, flat along the front's line (the band's square and its neighbours north and south) and rising at <see cref="SlopeDegrees"/> to the east and west so distant things stand
/// above the near ground (the owner will later fill the slopes with distant battles); the main avenue running on out of both edges; low-detail massing copies of the neighbouring bands (one box per kit item) where the next and the last
/// map line up; manufactorum stacks along the slopes; the cathedral spire far to the north, the front's goal. Every size and colour is ours (placeholder demonstration, refine register).
/// </summary>
public static class AshfallFrontScenery
{
    /// <summary>How far the outside ground reaches from the band's centre (m) and the angle it rises at beyond the front's line (degrees; owner's plan: about 10 degrees out to about 1.2 km).</summary>
    public const float Reach = 1200f, SlopeDegrees = 10f;
    /// <summary>Half the width of the flat front line the bands stand on (m: the square's 240 and its 4 m boundary).</summary>
    public const float LineHalf = 244f;
    /// <summary>Where a neighbouring band's copy stands (its centre, m north or south of this band's): the next map lines up beyond the boundary, its avenue on this one's.</summary>
    public const float NeighbourOffset = 520f;
    /// <summary>The cathedral spire: its place north of the band's centre (m) and its height (m; tall enough to show over the 64 m boundary from most of the band).</summary>
    public const float SpireNorth = 1150f, SpireHeight = 380f;

    /// <summary>The outside ground's height at band-frame x (m).</summary>
    public static float Height(float x) => AshfallMap.BlockTop - 0.03f + MathF.Tan(SlopeDegrees * MathF.PI / 180f) * MathF.Max(0f, MathF.Abs(x) - LineHalf);

    /// <summary>Triangles drawn by the last <see cref="Build"/> (for the report).</summary>
    public static int LastTriangles { get; private set; }

    /// <summary>Builds the scenery of band <paramref name="band"/>; <paramref name="bands"/>: every band's level and dressing (band frame), for the neighbours' copies.</summary>
    public static Node3D Build(int band, IReadOnlyList<(AshfallLevel Level, AshfallDressing Dressing)> bands)
    {
        var root = new Node3D { Name = "FrontScenery" };
        int tris = 0;
        void Add(string name, List<V3> corners, Color colour, bool shadow = false)
        {
            if (corners.Count == 0) return;
            tris += corners.Count / 3;
            root.AddChild(new MeshInstance3D
            {
                Name = name, Mesh = AshfallView.MeshOf([.. corners]),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = colour, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        // 1. The outside ground (every cell of the grid outside the square).
        float[] xs = [-Reach, -950f, -750f, -580f, -440f, -340f, -280f, -LineHalf, LineHalf, 280f, 340f, 440f, 580f, 750f, 950f, Reach];
        float[] ys = [-Reach, -1040f, -760f, -520f, -280f, -LineHalf, LineHalf, 280f, 520f, 760f, 1040f, Reach];
        var ground = new List<V3>();
        for (int i = 0; i + 1 < xs.Length; i++)
            for (int j = 0; j + 1 < ys.Length; j++)
            {
                float x0 = xs[i], x1 = xs[i + 1], y0 = ys[j], y1 = ys[j + 1];
                if (x0 >= -LineHalf && x1 <= LineHalf && y0 >= -LineHalf && y1 <= LineHalf) continue;   // the band's own square (its ground and boundary are the level's)
                Quad(ground, new(x0, y0, Height(x0)), new(x1, y0, Height(x1)), new(x1, y1, Height(x1)), new(x0, y1, Height(x0)));
            }
        Add("OutsideGround", ground, new Color(0.47f, 0.44f, 0.41f));

        // 2. The main avenue on out of both edges (24 m, a hand's breadth over the ground).
        var road = new List<V3>();
        foreach (var (a, b) in new[] { (LineHalf, Reach), (-Reach, -LineHalf) })
            Quad(road, new(-12f, a, AshfallMap.BlockTop + 0.02f), new(12f, a, AshfallMap.BlockTop + 0.02f), new(12f, b, AshfallMap.BlockTop + 0.02f), new(-12f, b, AshfallMap.BlockTop + 0.02f));
        Add("AvenueOnward", road, new Color(0.33f, 0.31f, 0.29f));

        // 3. The neighbouring bands' massing copies: one box per kit item (props only), where the last map lies south and the next north.
        foreach (var (k, dy) in new[] { (band - 1, -NeighbourOffset), (band + 1, NeighbourOffset) })
        {
            if (k < 0 || k >= bands.Count) continue;
            var boxes = new List<V3>();
            foreach (var it in bands[k].Dressing.Items)
                if (it.Role == AshfallDressRole.Prop && it.Size.Z > 0.8f) Box(boxes, new V3(it.Position.X, it.Position.Y + dy, it.Position.Z), it.Size, it.Yaw);
            Add($"Neighbour{k}", boxes, new Color(0.40f, 0.38f, 0.37f), shadow: true);
        }

        // 4. Manufactorum stacks along the slopes, east and west (seeded by the band, fixed).
        var stacks = new List<V3>();
        for (int side = -1; side <= 1; side += 2)
            for (int i = 0; i < 10; i++)
            {
                uint h = Mix(0x57AC0u + (uint)band * 31u, (uint)(i * 2 + (side > 0 ? 1 : 0)));
                float x = side * (330f + 60f * i + (h % 40u)), y = -1000f + 210f * i + ((h >> 8) % 60u) - 30f;
                float r = 4f + (h >> 16) % 5u, top = 70f + (h >> 20) % 60u;
                Prism(stacks, x, y, Height(x) - 2f, Height(x) + top, r, 10);
                Prism(stacks, x, y, Height(x) + top, Height(x) + top + 3f, r + 1.2f, 10);   // the stack's crown
            }
        Add("ManufactorumStacks", stacks, new Color(0.24f, 0.22f, 0.21f));

        // 5. The cathedral spire, the front's far goal (a nave block, a tower, a spire).
        var spire = new List<V3>();
        Box(spire, new V3(0f, SpireNorth, Height(0f)), new V3(70f, 120f, 90f), 0f);
        Box(spire, new V3(0f, SpireNorth - 30f, Height(0f)), new V3(36f, 36f, 220f), 0f);
        Pyramid(spire, 0f, SpireNorth - 30f, Height(0f) + 220f, Height(0f) + SpireHeight, 18f);
        Add("CathedralSpire", spire, new Color(0.30f, 0.29f, 0.30f));

        LastTriangles = tris;
        return root;
    }

    /// <summary>A fixed integer hash (the scenery's places; ours).</summary>
    static uint Mix(uint seed, uint i)
    {
        uint h = seed * 0x9E3779B1u ^ (i + 0x7F4A7C15u) * 0x85EBCA6Bu;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return h;
    }

    static void Quad(List<V3> c, V3 a, V3 b, V3 cc, V3 d) { c.Add(a); c.Add(b); c.Add(cc); c.Add(a); c.Add(cc); c.Add(d); }

    /// <summary>A box standing on <paramref name="bottom"/> (centre of its bottom face), turned by <paramref name="yaw"/> (radians, X toward Y).</summary>
    static void Box(List<V3> c, V3 bottom, V3 size, float yaw)
    {
        float s = MathF.Sin(yaw), co = MathF.Cos(yaw), hx = size.X / 2f, hy = size.Y / 2f;
        V3 P(float lx, float ly, float z) => new(bottom.X + co * lx - s * ly, bottom.Y + s * lx + co * ly, z);
        float z0 = bottom.Z, z1 = bottom.Z + size.Z;
        V3[] lo = [P(-hx, -hy, z0), P(hx, -hy, z0), P(hx, hy, z0), P(-hx, hy, z0)], hi = [P(-hx, -hy, z1), P(hx, -hy, z1), P(hx, hy, z1), P(-hx, hy, z1)];
        Quad(c, hi[0], hi[1], hi[2], hi[3]);
        for (int k = 0; k < 4; k++) Quad(c, lo[k], lo[(k + 1) % 4], hi[(k + 1) % 4], hi[k]);
    }

    static void Prism(List<V3> c, float x, float y, float z0, float z1, float r, int sides)
    {
        for (int k = 0; k < sides; k++)
        {
            float a0 = k * MathF.Tau / sides, a1 = (k + 1) * MathF.Tau / sides;
            var p0 = new V3(x + r * MathF.Cos(a0), y + r * MathF.Sin(a0), 0f); var p1 = new V3(x + r * MathF.Cos(a1), y + r * MathF.Sin(a1), 0f);
            Quad(c, p0 with { Z = z0 }, p1 with { Z = z0 }, p1 with { Z = z1 }, p0 with { Z = z1 });
            c.Add(new V3(x, y, z1)); c.Add(p0 with { Z = z1 }); c.Add(p1 with { Z = z1 });
        }
    }

    static void Pyramid(List<V3> c, float x, float y, float z0, float z1, float half)
    {
        V3[] b = [new(x - half, y - half, z0), new(x + half, y - half, z0), new(x + half, y + half, z0), new(x - half, y + half, z0)];
        var tip = new V3(x, y, z1);
        for (int k = 0; k < 4; k++) { c.Add(b[k]); c.Add(b[(k + 1) % 4]); c.Add(tip); }
    }
}
