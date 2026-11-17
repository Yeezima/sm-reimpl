using System.Numerics;

namespace SmSim;

/// <summary>
/// An invisible movement wall of the map (classic-maps-fidelity gameplay, 2026-11-18): a <c>base/MissionMovementBlocker</c>
/// of the map's <c>d_blockers</c> / <c>d_mission</c> layers with <c>BlockGroup</c> 2 (read as ALL) that no script names
/// (<c>SmContent.MapContent</c> reads them; spec/behavior.md "World movement blockers"). Solid for **every** player
/// in the shared movement step (walk, sprint, jump, jump-pack flight, falling), like a spawn-room blocker for the other
/// side but for both sides and not raised (<see cref="CollisionWorld.CharacterFor"/>); shots, thrown items and blasts pass
/// through (they test the weapon mesh, which has none). The shape is kept as world-space triangles: a box or a cylinder
/// (a prism) turned by the object's full rotation.
/// </summary>
public sealed class WorldBlocker
{
    /// <summary>Sides of the prism that stands for a cylinder; it circumscribes the true cylinder (never smaller than it).</summary>
    public const int CylinderSides = 16;

    public WorldBlocker(string name, Vector3[] vertices, int[] indices)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (vertices.Length < 4 || indices.Length < 12 || indices.Length % 3 != 0) throw new ArgumentException("A world blocker needs a solid: at least 4 vertices and 4 triangles.", nameof(indices));
        foreach (var v in vertices) if (!Geometry.Finite(v)) throw new ArgumentException("A world blocker has a non-finite vertex.", nameof(vertices));
        foreach (int i in indices) if ((uint)i >= (uint)vertices.Length) throw new ArgumentException($"Index {i} outside the {vertices.Length} vertices.", nameof(indices));
        Name = name; Vertices = vertices; Indices = indices;
        Min = vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        Max = vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max);
    }

    public string Name { get; }
    /// <summary>World-space vertices.</summary>
    public Vector3[] Vertices { get; }
    /// <summary>Triangles over <see cref="Vertices"/> (three indices each; triangles are two-sided in <see cref="StaticMesh"/>).</summary>
    public int[] Indices { get; }
    /// <summary>Bounds of the vertices.</summary>
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    /// <summary>The unit box (−0.5 .. 0.5 on each axis) carried to the world by <paramref name="world"/> (scale, rotation, translation).</summary>
    public static WorldBlocker Box(string name, Matrix4x4 world)
    {
        var vertices = new Vector3[8];
        for (int k = 0; k < 8; k++)
        {
            // Same corner order as SpawnBlocker.Corners: bottom (−Z) then top, each (−X,−Y), (+X,−Y), (+X,+Y), (−X,+Y).
            (float x, float y) = (k % 4) switch { 0 => (-0.5f, -0.5f), 1 => (0.5f, -0.5f), 2 => (0.5f, 0.5f), _ => (-0.5f, 0.5f) };
            vertices[k] = Vector3.Transform(new Vector3(x, y, k < 4 ? -0.5f : 0.5f), world);
        }
        return new WorldBlocker(name, vertices, [.. SpawnBlocker.BoxIndices]);
    }

    /// <summary>
    /// The unit cylinder (radius 0.5 about Z, −0.5 .. 0.5 high; the marker's "X and Y are the diameter, Z the height") carried to the
    /// world by <paramref name="world"/> as a <see cref="CylinderSides"/>-sided prism whose corners lie on a circle of radius
    /// 0.5 / cos(π / sides) so its sides touch the true cylinder (the prism contains it).
    /// </summary>
    public static WorldBlocker Cylinder(string name, Matrix4x4 world)
    {
        int n = CylinderSides;
        float r = 0.5f / MathF.Cos(MathF.PI / n);
        var vertices = new Vector3[2 * n];
        for (int i = 0; i < n; i++)
        {
            var (s, c) = MathF.SinCos(2f * MathF.PI * i / n);
            vertices[i] = Vector3.Transform(new Vector3(r * c, r * s, -0.5f), world);
            vertices[n + i] = Vector3.Transform(new Vector3(r * c, r * s, 0.5f), world);
        }
        var indices = new List<int>();
        for (int i = 1; i + 1 < n; i++) indices.AddRange([0, i + 1, i, n, n + i, n + i + 1]);   // the bottom and top fans
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            indices.AddRange([i, j, n + j, i, n + j, n + i]);
        }
        return new WorldBlocker(name, vertices, [.. indices]);
    }

    /// <summary>The list, copied, after checking that no entry is null.</summary>
    internal static WorldBlocker[] Checked(IEnumerable<WorldBlocker>? walls)
    {
        var list = walls?.ToArray() ?? [];
        foreach (var w in list) ArgumentNullException.ThrowIfNull(w);
        return list;
    }

    /// <summary>FNV-1a 64 of <paramref name="h"/> and each wall's vertex and index counts, vertex floats and indices; none: <paramref name="h"/>.</summary>
    internal static ulong Hash(ulong h, IReadOnlyList<WorldBlocker> walls)
    {
        foreach (var w in walls)
        {
            h = Geometry.Fnv(h, (uint)w.Vertices.Length);
            h = Geometry.Fnv(h, (uint)w.Indices.Length);
            foreach (var v in w.Vertices)
                foreach (float f in new[] { v.X, v.Y, v.Z }) h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
            foreach (int i in w.Indices) h = Geometry.Fnv(h, (uint)i);
        }
        return h;
    }
}
