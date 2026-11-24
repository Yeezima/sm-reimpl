using System.Numerics;

namespace SmSim;

/// <summary>
/// A closed triangle mesh used as a gameplay volume (docs/mapmaker.md "Edit mode contract": an edited capture point, kill zone, spawn area or spawn door is the shape its author made, not its
/// bounding box). A neutral geometry type in the core simulation (no Classic map makes one; nothing reads it until the Mapmaker's runtime wires it in), so core files may use it in a build
/// without added content. Every test is in double with IEEE + - * / only and fixed tie rules, so the server and every client give the same answer for the same input on every OS.
/// <para><b>Inside</b> (<see cref="Contains"/>): the non-zero winding number of a ray from the point straight up (+Z). The point is projected onto XY; a triangle counts when the point lies in its
/// projection by the rasterisers' top-left rule (strictly inside, or on a "top" edge (horizontal, pointing -X once the triangle is made counter-clockwise in XY) or a "left" edge (pointing -Y)), so a
/// point on an edge two triangles share is counted by exactly one; a triangle whose projection has no area (a vertical wall) never counts. It is crossed when its plane at the point is strictly above
/// the point; an upward-facing triangle adds 1, a downward-facing one subtracts 1. Consequences: a self-overlapping shape is inside wherever any part covers the point; the bottom surface is inside
/// and the top surface outside; on a box, the -X and +Y side faces are inside and the +X and -Y side faces outside (half-open, so two volumes that touch never both hold a boundary point).</para>
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
        if (vertices.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0 || indices.Any(i => i < 0 || i >= vertices.Length)) throw new ArgumentException("A volume needs vertices and whole triangles over them.");
        if (vertices.Any(v => !Geometry.Finite(v))) throw new ArgumentException("A volume's vertices must be finite.");
        Name = name; Vertices = vertices; Indices = indices;
        Min = vertices.Aggregate(Vector3.Min); Max = vertices.Aggregate(Vector3.Max);
    }

    /// <summary>The centre of the bounding box (may lie outside a non-convex shape: use <see cref="InsidePoint"/> for a point that is inside).</summary>
    public Vector3 BoundsCenter => (Min + Max) * 0.5f;

    /// <summary>True when <paramref name="p"/> is inside the closed mesh (rules in the class summary). A non-finite point is never inside.</summary>
    public bool Contains(Vector3 p)
    {
        if (!Geometry.Finite(p)) return false;
        if (p.X < Min.X || p.Y < Min.Y || p.Z < Min.Z || p.X > Max.X || p.Y > Max.Y || p.Z > Max.Z) return false;
        int winding = 0;
        for (int t = 0; t + 2 < Indices.Length; t += 3)
            if (CrossingAt(t, p.X, p.Y, out double z, out int sign) && z > p.Z) winding += sign;
        return winding != 0;
    }

    /// <summary>Whether triangle <paramref name="t"/>'s projection holds (x, y) by the tie rule; its height there and whether it faces up (+1) or down (-1).</summary>
    bool CrossingAt(int t, double px, double py, out double z, out int sign)
    {
        z = 0; sign = 0;
        var a = Vertices[Indices[t]]; var b = Vertices[Indices[t + 1]]; var c = Vertices[Indices[t + 2]];
        double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cx = c.X, cy = c.Y, az = a.Z, bz = b.Z, cz = c.Z;
        double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (area == 0) return false;
        sign = area > 0 ? 1 : -1;
        if (area < 0) { (bx, cx) = (cx, bx); (by, cy) = (cy, by); (bz, cz) = (cz, bz); area = -area; }
        double w0 = Edge(bx, by, cx, cy, px, py), w1 = Edge(cx, cy, ax, ay, px, py), w2 = Edge(ax, ay, bx, by, px, py);
        if (!Inside(w0, bx, by, cx, cy) || !Inside(w1, cx, cy, ax, ay) || !Inside(w2, ax, ay, bx, by)) return false;
        z = (w0 * az + w1 * bz + w2 * cz) / area;
        return true;
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

    /// <summary>
    /// Whether the segment from <paramref name="a"/> to <paramref name="b"/> meets the volume (the mesh equivalent of <see cref="KillVolume.Crosses"/>, so a fast mover cannot pass a thin volume
    /// between ticks): an end inside, or the segment touching any triangle (faces and edges included). A non-finite end never crosses.
    /// </summary>
    public bool Crosses(Vector3 a, Vector3 b)
    {
        if (!Geometry.Finite(a) || !Geometry.Finite(b)) return false;
        if (Math.Max(a.X, b.X) < Min.X || Math.Max(a.Y, b.Y) < Min.Y || Math.Max(a.Z, b.Z) < Min.Z || Math.Min(a.X, b.X) > Max.X || Math.Min(a.Y, b.Y) > Max.Y || Math.Min(a.Z, b.Z) > Max.Z) return false;
        if (Contains(a) || Contains(b)) return true;
        double ox = a.X, oy = a.Y, oz = a.Z, dx = (double)b.X - a.X, dy = (double)b.Y - a.Y, dz = (double)b.Z - a.Z;
        for (int t = 0; t + 2 < Indices.Length; t += 3)
        {
            var v0 = Vertices[Indices[t]]; var v1 = Vertices[Indices[t + 1]]; var v2 = Vertices[Indices[t + 2]];
            double e1x = (double)v1.X - v0.X, e1y = (double)v1.Y - v0.Y, e1z = (double)v1.Z - v0.Z, e2x = (double)v2.X - v0.X, e2y = (double)v2.Y - v0.Y, e2z = (double)v2.Z - v0.Z;
            double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            double det = e1x * px + e1y * py + e1z * pz;
            if (det == 0) continue;   // the segment runs parallel to this triangle; an end inside or another face catches it
            double tx = ox - v0.X, ty = oy - v0.Y, tz = oz - v0.Z;
            double u = (tx * px + ty * py + tz * pz) / det;
            if (u < 0 || u > 1) continue;
            double qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
            double v = (dx * qx + dy * qy + dz * qz) / det;
            if (v < 0 || u + v > 1) continue;
            double s = (e2x * qx + e2y * qy + e2z * qz) / det;
            if (s >= 0 && s <= 1) return true;
        }
        return false;
    }

    /// <summary>
    /// A point inside the volume (a spawn or label point: the box centre may be outside a sheared or hollow shape), or null when none is found (a degenerate mesh). The bounding box's centre column
    /// is tried first, then a 7 x 7 grid of columns over the box; in each, the first stretch of the vertical line that is inside gives its middle.
    /// </summary>
    public Vector3? InsidePoint()
    {
        var columns = new List<(double X, double Y)> { ((Min.X + (double)Max.X) / 2, (Min.Y + (double)Max.Y) / 2) };
        for (int i = 0; i < 7; i++)
            for (int j = 0; j < 7; j++)
                columns.Add((Min.X + (Max.X - (double)Min.X) * (i + 0.5) / 7, Min.Y + (Max.Y - (double)Min.Y) * (j + 0.5) / 7));
        foreach (var (x, y) in columns)
        {
            var hits = new List<(double Z, int Sign)>();
            for (int t = 0; t + 2 < Indices.Length; t += 3)
                if (CrossingAt(t, x, y, out double z, out int sign)) hits.Add((z, sign));
            hits.Sort((p, q) => p.Z != q.Z ? p.Z.CompareTo(q.Z) : p.Sign.CompareTo(q.Sign));
            // walking up, the winding below a crossing is minus the sum of the signs above it; start with every sign above (the ray from below the shape)
            int winding = 0;
            foreach (var h in hits) winding += h.Sign;
            for (int k = 0; k + 1 < hits.Count; k++)
            {
                winding -= hits[k].Sign;
                if (winding != 0 && hits[k + 1].Z > hits[k].Z)
                {
                    var mid = new Vector3((float)x, (float)y, (float)((hits[k].Z + hits[k + 1].Z) / 2));
                    if (Contains(mid)) return mid;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The volume stretched up so its top reaches at least <paramref name="top"/>, its lowest point staying (the mesh equivalent of <see cref="SpawnBlocker.ReachingAbove"/>): every vertex keeps its
    /// X and Y and its height above the bottom is scaled by the same factor. Unchanged when the top is already that high or <paramref name="top"/> is not finite.
    /// </summary>
    public MeshVolume ReachingAbove(float top)
    {
        if (!float.IsFinite(top) || top <= Max.Z || Max.Z <= Min.Z) return this;
        double bottom = Min.Z, f = (top - bottom) / ((double)Max.Z - bottom);
        return new MeshVolume(Name, Vertices.Select(v => v with { Z = v.Z == Max.Z ? top : (float)(bottom + (v.Z - bottom) * f) }).ToArray(), Indices);
    }

    /// <summary>
    /// A capsule (feet <paramref name="feet"/>, <paramref name="radius"/>, <paramref name="height"/>) whose axis meets the volume is moved out horizontally (the mesh equivalent of
    /// <see cref="SpawnBlocker.PushOut"/>): across the nearest edge of the shape seen from above, to <paramref name="radius"/> + 1 cm beyond it, the first such place (nearest first, ties in triangle
    /// order) where the axis no longer meets the volume; if none clears, out of the bounding box across its nearest side (ties: X sides, then the positive side). Otherwise <paramref name="feet"/>
    /// is returned unchanged. Provisional approximation (ours).
    /// </summary>
    public Vector3 PushOut(Vector3 feet, float radius, float height)
    {
        if (!Geometry.Finite(feet) || !(feet.Z < Max.Z && feet.Z + height > Min.Z)) return feet;
        var up = new Vector3(0, 0, MathF.Max(height, 0f));
        if (!Crosses(feet, feet + up)) return feet;
        double px = feet.X, py = feet.Y, beyond = Math.Max(radius, 0f) + 0.01;
        var candidates = new List<(double D, int Order, double Qx, double Qy)>();
        int order = 0;
        for (int t = 0; t + 2 < Indices.Length; t += 3)
            for (int k = 0; k < 3; k++, order++)
            {
                var a = Vertices[Indices[t + k]]; var b = Vertices[Indices[t + (k + 1) % 3]];
                double ex = (double)b.X - a.X, ey = (double)b.Y - a.Y, len2 = ex * ex + ey * ey;
                double s = len2 == 0 ? 0 : Math.Clamp(((px - a.X) * ex + (py - a.Y) * ey) / len2, 0, 1);
                double qx = a.X + ex * s, qy = a.Y + ey * s, ddx = qx - px, ddy = qy - py;
                candidates.Add((ddx * ddx + ddy * ddy, order, qx, qy));
            }
        candidates.Sort((p, q) => p.D != q.D ? p.D.CompareTo(q.D) : p.Order.CompareTo(q.Order));
        foreach (var (d, _, qx, qy) in candidates)
        {
            if (d == 0) continue;   // on the edge itself: no direction
            double n = Math.Sqrt(d), ox = qx + (qx - px) / n * beyond, oy = qy + (qy - py) / n * beyond;
            var to = new Vector3((float)ox, (float)oy, feet.Z);
            if (!Crosses(to, to + up)) return to;
        }
        double dxMin = px - Min.X, dxMax = Max.X - px, dyMin = py - Min.Y, dyMax = Max.Y - py;
        double best = Math.Min(Math.Min(dxMin, dxMax), Math.Min(dyMin, dyMax));
        if (best == dxMax) return feet with { X = (float)(Max.X + beyond) };
        if (best == dxMin) return feet with { X = (float)(Min.X - beyond) };
        if (best == dyMax) return feet with { Y = (float)(Max.Y + beyond) };
        return feet with { Y = (float)(Min.Y - beyond) };
    }

    /// <summary>
    /// The shape's outline for drawing (a door's shield, a point's ring): every edge whose two triangles are not coplanar, or that has only one triangle, as vertex index pairs (lower first), in the
    /// order the triangles first walk them. Diagonals inside flat faces are left out.
    /// </summary>
    public IReadOnlyList<(int A, int B)> FeatureEdges()
    {
        var normals = new (double X, double Y, double Z)[Indices.Length / 3];
        for (int t = 0; t < normals.Length; t++)
        {
            var a = Vertices[Indices[3 * t]]; var b = Vertices[Indices[3 * t + 1]]; var c = Vertices[Indices[3 * t + 2]];
            double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z, vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
            normals[t] = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
        }
        var faces = new Dictionary<(int, int), List<int>>();
        var order = new List<(int, int)>();
        for (int t = 0; t < normals.Length; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = Indices[3 * t + k], b = Indices[3 * t + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!faces.TryGetValue(key, out var list)) { faces[key] = list = []; order.Add(key); }
                list.Add(t);
            }
        var result = new List<(int, int)>();
        foreach (var key in order)
        {
            var list = faces[key];
            if (list.Count != 2) { result.Add(key); continue; }
            var (n1, n2) = (normals[list[0]], normals[list[1]]);
            double cx = n1.Y * n2.Z - n1.Z * n2.Y, cy = n1.Z * n2.X - n1.X * n2.Z, cz = n1.X * n2.Y - n1.Y * n2.X;
            double l1 = n1.X * n1.X + n1.Y * n1.Y + n1.Z * n1.Z, l2 = n2.X * n2.X + n2.Y * n2.Y + n2.Z * n2.Z, dot = n1.X * n2.X + n1.Y * n2.Y + n1.Z * n2.Z;
            if (dot <= 0 || cx * cx + cy * cy + cz * cz > 1e-12 * l1 * l2) result.Add(key);
        }
        return result;
    }

    /// <summary>FNV-1a 64 of <paramref name="h"/> and the volume's vertices (float bits) and indices: for a world's content hash.</summary>
    public ulong Hash(ulong h)
    {
        foreach (var v in Vertices)
        {
            h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.X)); h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Y)); h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Z));
        }
        foreach (var i in Indices) h = Geometry.Fnv(h, (uint)i);
        return h;
    }
}
