using System;
using Godot;

namespace SmClient;

/// <summary>
/// The ability cells' icons (owner 2026-11-12: the artillery strike's matches his black explosion-burst reference exactly, the drop pod's is drawn from his top-down pod reference in the same style, the Librarian's match them).
/// One style: solid shapes in the cell's gold, cut by thin gaps. The burst and the pod are traced by hand into polygons below (ours: nothing of the pictures is embedded; the burst is a zig-zag ribbon with its tail
/// tips down, the tall central spike, the side spikes and four flying shards). Each icon is one triangulated mesh in cell-local units, centred on (0, 0) and <see cref="IconHeight"/> tall, built once on the first draw,
/// with a thin antialiased outline along its edges; drawing allocates nothing.
/// </summary>
public sealed partial class MusouHud
{
    // The burst, in the reference's pixels about its centre: the ribbon, then the four shards.
    static readonly Vector2[][] ArtilleryPolys =
    [
        [new(-157, 274), new(-250, 202), new(-150, 187), new(-232, 50), new(-269, 21), new(-108, 86), new(-103, -22), new(-64, 30), new(-45, -228), new(-6, 39), new(38, -87), new(-2, 91), new(197, -90), new(80, 154), new(242, 139), new(118, 274),
         new(310, 109), new(136, 114), new(239, -146), new(34, 25), new(48, -125), new(2, -27), new(-47, -280), new(-80, -30), new(-121, -82), new(-136, 42), new(-323, -17), new(-260, 70), new(-202, 165), new(-310, 190)],
        [new(-206, -238), new(-178, -245), new(-134, -106), new(-152, -130)],
        [new(72, -214), new(94, -221), new(100, -200), new(64, -156)],
        [new(314, -75), new(335, -73), new(334, -52), new(262, -3)],
        [new(-335, 114), new(-312, 111), new(-286, 152), new(-306, 142), new(-334, 130)],
    ];

    // The drop pod from above, in its own units (x 0..100 across, y 0..127 from the nose down): the reference's tapering hull, wide at the tail, and three fins at the top with slits between them.
    static readonly Vector2[][] PodPolys =
    [
        [new(27, 3), new(36, 3), new(41, 39), new(15, 39)],      // left fin
        [new(47, 0), new(53, 0), new(53, 39), new(47, 39)],      // centre spine
        [new(64, 3), new(73, 3), new(85, 39), new(59, 39)],      // right fin
        [new(15, 43), new(85, 43), new(97, 80), new(100, 100), new(100, 115), new(92, 127), new(8, 127), new(0, 115), new(0, 100), new(3, 80)],   // the hull
    ];

    // The hull's panel lines, pairs of points cut into it in the panel's black: the centre seam, the two shoulder seams, the chevron.
    static readonly Vector2[] PodLines =
    [
        new(50, 43), new(50, 100), new(34, 48), new(26, 100), new(66, 48), new(74, 100), new(30, 118), new(70, 118),
        new(43, 115), new(50, 107), new(50, 107), new(57, 115),
    ];

    static Vector2[] _podLines;
    static readonly ArrayMesh[] IconMesh = new ArrayMesh[4];
    static readonly Vector2[][][] IconEdges = new Vector2[4][][];

    /// <summary>A band of a ring from angle <paramref name="a0"/> to <paramref name="a1"/> (radians) between two radii, as a polygon.</summary>
    static Vector2[] ArcBand(float r0, float r1, float a0, float a1, int steps)
    {
        var p = new Vector2[(steps + 1) * 2];
        for (int i = 0; i <= steps; i++)
        {
            float a = a0 + (a1 - a0) * i / steps;
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            p[i] = d * r1;
            p[p.Length - 1 - i] = d * r0;
        }
        return p;
    }

    static Vector2[] Fit(Vector2[] src, float scale, Vector2 centre)
    {
        var d = new Vector2[src.Length];
        for (int i = 0; i < src.Length; i++) d[i] = (src[i] - centre) * scale;
        return d;
    }

    static Vector2[][] BuildPolys(AbilityKind kind)
    {
        var list = new System.Collections.Generic.List<Vector2[]>();
        switch (kind)
        {
            case AbilityKind.Artillery:
                foreach (var p in ArtilleryPolys) list.Add(Fit(p, IconHeight / 549f, Vector2.Zero));
                break;
            case AbilityKind.DropPod:
                foreach (var p in PodPolys) list.Add(Fit(p, IconHeight / 127f, new Vector2(50f, 63.5f)));
                break;
            case AbilityKind.Shockwave:
            {
                // a core, a ring in two halves and an outer ring in four arcs: the blast spreading
                var core = new Vector2[20];
                for (int i = 0; i < core.Length; i++) core[i] = new Vector2(MathF.Cos(i * MathF.Tau / core.Length), MathF.Sin(i * MathF.Tau / core.Length)) * 5.5f;
                list.Add(core);
                const float gap = 0.2f;
                list.Add(ArcBand(10f, 14f, gap, MathF.PI - gap, 14));
                list.Add(ArcBand(10f, 14f, MathF.PI + gap, MathF.Tau - gap, 14));
                for (int k = 0; k < 4; k++)
                {
                    float a = MathF.PI / 4f + k * MathF.PI / 2f;
                    list.Add(ArcBand(18.5f, 23f, a - MathF.PI / 4f + 0.12f, a + MathF.PI / 4f - 0.12f, 10));
                }
                break;
            }
            default:
            {
                // two arrows drawn in to a centre bar: the Pull
                Vector2[] arrow = [new(5, 0), new(17, -14), new(17, -6), new(26, -6), new(26, 6), new(17, 6), new(17, 14)];
                var left = new Vector2[arrow.Length];
                for (int i = 0; i < arrow.Length; i++) left[arrow.Length - 1 - i] = new Vector2(-arrow[i].X, arrow[i].Y);
                list.Add(Fit(arrow, 1.35f, Vector2.Zero)); list.Add(Fit(left, 1.35f, Vector2.Zero));
                list.Add(Fit([new(-2, -9), new(2, -9), new(2, 9), new(-2, 9)], 1.35f, Vector2.Zero));
                break;
            }
        }
        return list.ToArray();
    }

    static void BuildIcon(AbilityKind kind)
    {
        int k = (int)kind;
        var polys = BuildPolys(kind);
        var verts = new System.Collections.Generic.List<Vector2>();
        var idx = new System.Collections.Generic.List<int>();
        var edges = new Vector2[polys.Length][];
        for (int i = 0; i < polys.Length; i++)
        {
            var p = polys[i];
            var tri = Geometry2D.TriangulatePolygon(p);
            if (tri.Length == 0) GD.PrintErr($"MusouHud cell icon {kind}: polygon {i} did not triangulate");
            int o = verts.Count;
            verts.AddRange(p);
            foreach (int t in tri) idx.Add(o + t);
            edges[i] = new Vector2[p.Length + 1];
            Array.Copy(p, edges[i], p.Length);
            edges[i][p.Length] = p[0];
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        IconMesh[k] = mesh;
        IconEdges[k] = edges;
        if (kind == AbilityKind.DropPod)
        {
            _podLines = new Vector2[PodLines.Length];
            for (int i = 0; i < PodLines.Length; i++) _podLines[i] = (PodLines[i] - new Vector2(50f, 63.5f)) * (IconHeight / 127f);
        }
    }

    /// <summary>One icon at <paramref name="at"/> (the cell's local units): the artillery burst, the drop pod, the Shockwave's rings or the Pull's arrows.</summary>
    void DrawIcon(AbilityKind kind, Vector2 at, Color col)
    {
        int k = (int)kind;
        if (IconMesh[k] == null) BuildIcon(kind);
        DrawSetTransformMatrix(_cellXf * new Transform2D(0f, at));
        DrawMesh(IconMesh[k], null, null, col);
        foreach (var e in IconEdges[k]) DrawPolyline(e, col, 1f, true);
        if (kind == AbilityKind.DropPod) DrawMultiline(_podLines, new Color(0f, 0f, 0f, 0.95f), 1.5f, true);
        DrawSetTransformMatrix(_cellXf);
    }
}
