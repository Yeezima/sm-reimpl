using System.Numerics;

namespace SmSim;

/// <summary>
/// A closed triangle mesh used as a gameplay volume (Mapmaker added content, docs/mapmaker.md "Edit mode contract": an edited capture point, kill zone, spawn area or spawn door is the shape
/// the author made, not its bounding box). <see cref="Contains"/> is an exact point-in-mesh test by ray parity along +Z, in double with IEEE + - * / only and a fixed tie rule, so the server
/// and every client give the same answer for the same point on every OS. Unused by Classic maps; nothing in the simulation reads it until the runtime wires it in (plan M2).
/// <para>Tie rule: the point is projected onto XY; a triangle (made counter-clockwise in XY) counts when the point is strictly inside its projection, or on an edge that is a "top" edge
/// (horizontal, pointing -X) or a "left" edge (pointing -Y): the rasterisers' top-left rule, so a point on an edge shared by two triangles is counted by exactly one. A triangle whose
/// projection has no area (a vertical wall) never counts. It is crossed when its plane at the point's XY is at or above the point's Z.</para>
/// </summary>
public sealed class MeshVolume
{
    public string Name { get; }
    public Vector3[] Vertices { get; }
    public int[] Indices { get; }
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    public MeshVolume(string name, Vector3[] vertices, int[] indices)
    {
        if (vertices.Length == 0 || indices.Length % 3 != 0 || indices.Any(i => i < 0 || i >= vertices.Length)) throw new ArgumentException("A volume needs vertices and whole triangles over them.");
        Name = name; Vertices = vertices; Indices = indices;
        Min = vertices.Aggregate(Vector3.Min); Max = vertices.Aggregate(Vector3.Max);
    }

    /// <summary>The centre of the bounding box (where the sim's box-shaped fallbacks put the marker).</summary>
    public Vector3 BoundsCenter => (Min + Max) * 0.5f;

    /// <summary>True when <paramref name="p"/> is inside the closed mesh (on its surface counts by the tie rule).</summary>
    public bool Contains(Vector3 p)
    {
        if (p.X < Min.X || p.Y < Min.Y || p.Z < Min.Z || p.X > Max.X || p.Y > Max.Y || p.Z > Max.Z) return false;
        double px = p.X, py = p.Y, pz = p.Z;
        int crossings = 0;
        for (int t = 0; t + 2 < Indices.Length; t += 3)
        {
            var a = Vertices[Indices[t]]; var b = Vertices[Indices[t + 1]]; var c = Vertices[Indices[t + 2]];
            double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cx = c.X, cy = c.Y;
            double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (area == 0) continue;
            double az = a.Z, bz = b.Z, cz = c.Z;
            if (area < 0) { (bx, cx) = (cx, bx); (by, cy) = (cy, by); (bz, cz) = (cz, bz); area = -area; }
            double w0 = Edge(bx, by, cx, cy, px, py), w1 = Edge(cx, cy, ax, ay, px, py), w2 = Edge(ax, ay, bx, by, px, py);
            if (!Inside(w0, bx, by, cx, cy) || !Inside(w1, cx, cy, ax, ay) || !Inside(w2, ax, ay, bx, by)) continue;
            double z = (w0 * az + w1 * bz + w2 * cz) / area;
            if (z >= pz) crossings++;
        }
        return (crossings & 1) == 1;
    }

    static double Edge(double x0, double y0, double x1, double y1, double px, double py) => (x1 - x0) * (py - y0) - (y1 - y0) * (px - x0);

    /// <summary>Strictly inside the edge, or on it when it is a top or left edge of a counter-clockwise triangle.</summary>
    static bool Inside(double w, double x0, double y0, double x1, double y1)
    {
        if (w > 0) return true;
        if (w < 0) return false;
        double dx = x1 - x0, dy = y1 - y0;
        return (dy == 0 && dx < 0) || dy < 0;
    }
}
