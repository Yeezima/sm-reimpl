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

    /// <summary>
    /// The shape as authored (this volume itself, unless it was made by <see cref="ReachingAbove"/>: then the shape it was raised from, whose vertices are the first
    /// <c>Authored.Vertices.Length</c> of this one's, in the same order). <see cref="FeatureEdges"/> and <see cref="PushOut"/>'s outline come from it.
    /// </summary>
    public MeshVolume Authored { get; }

    /// <summary>
    /// A volume of the closed mesh. An inside-out mesh (negative signed volume: a mirrored placement, or a face pulled through the opposite one) is turned the right way out here (every triangle's
    /// winding reversed, in a copy of <paramref name="indices"/>), so the tests do not depend on how the shape was wound.
    /// </summary>
    public MeshVolume(string name, Vector3[] vertices, int[] indices)
    {
        if (vertices.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0 || indices.Any(i => i < 0 || i >= vertices.Length)) throw new ArgumentException("A volume needs vertices and whole triangles over them.");
        if (vertices.Any(v => !Geometry.Finite(v))) throw new ArgumentException("A volume's vertices must be finite.");
        Name = name; Vertices = vertices; Indices = SignedVolume(vertices, indices) < 0 ? Reversed(indices) : indices;
        Min = vertices.Aggregate(Vector3.Min); Max = vertices.Aggregate(Vector3.Max);
        Authored = this;
    }

    MeshVolume(string name, Vector3[] vertices, int[] indices, MeshVolume authored)
    {
        Name = name; Vertices = vertices; Indices = indices;
        Min = vertices.Aggregate(Vector3.Min); Max = vertices.Aggregate(Vector3.Max);
        Authored = authored;
    }

    /// <summary>Six times the signed volume (positive when the triangles are wound counter-clockwise seen from outside), in double.</summary>
    static double SignedVolume(Vector3[] v, int[] idx)
    {
        double sum = 0;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            Vector3 a = v[idx[t]], b = v[idx[t + 1]], c = v[idx[t + 2]];
            sum += a.X * ((double)b.Y * c.Z - (double)b.Z * c.Y) - (double)a.Y * ((double)b.X * c.Z - (double)b.Z * c.X) + (double)a.Z * ((double)b.X * c.Y - (double)b.Y * c.X);
        }
        return sum;
    }

    static int[] Reversed(int[] idx)
    {
        var r = (int[])idx.Clone();
        for (int t = 0; t + 2 < r.Length; t += 3) (r[t + 1], r[t + 2]) = (r[t + 2], r[t + 1]);
        return r;
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
        // the height as the first corner's plus the weighted rises to the other two: exactly az on a flat (horizontal) triangle, so a point on a flat bottom or top is judged by the rule, not by rounding
        z = az + (w1 * (bz - az) + w2 * (cz - az)) / area;
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
    /// between ticks): an end inside, or the segment touching any triangle (faces and edges included). A non-finite end never crosses. One difference from <see cref="KillVolume.Crosses"/>: a
    /// zero-length segment (a = b) is exactly <see cref="Contains"/>, so it follows the half-open rule (a point on the top or a +X / -Y face does not cross), where the box test counts every face.
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
    /// The volume reaching up to at least <paramref name="top"/> (the mesh equivalent of <see cref="SpawnBlocker.ReachingAbove"/>): everything at or above any point of the authored shape, up to the
    /// flat height <paramref name="top"/>, is inside, built as the union of closed vertical prisms, one from every non-vertical triangle of the authored shape (each wound counter-clockwise in plan, so
    /// the result does not depend on the mesh's winding) up to <paramref name="top"/>. The authored shape is inside it unchanged (every point of it lies above its own bottom surface), a leaning or
    /// uneven-topped door blocks all the way up above every part of it and nothing beside it. The raised volume keeps the authored vertices first and <see cref="Authored"/> for the outline. Build it
    /// once (when the world is made) and keep it. Unchanged when the top is already that high or <paramref name="top"/> is not finite; raising a raised volume raises its authored shape again.
    /// </summary>
    public MeshVolume ReachingAbove(float top)
    {
        if (!float.IsFinite(top)) return this;
        if (!ReferenceEquals(Authored, this) && top <= Max.Z) return this;   // already raised at least that high
        var src = Authored;
        // nothing to raise when every part of the shape already reaches the height (all of its upper surface at or above it); a shape whose highest point reaches it but whose other parts are
        // lower is raised too, up to its own highest point where that is above the height (the prisms share one flat top)
        if (top <= src.Min.Z || !src.NeedsRaising(top)) return this;
        top = MathF.Max(top, src.Max.Z);
        var v = new List<Vector3>(src.Vertices);
        var idx = new List<int>();
        for (int t = 0; t + 2 < src.Indices.Length; t += 3)
        {
            Vector3 a = src.Vertices[src.Indices[t]], b = src.Vertices[src.Indices[t + 1]], c = src.Vertices[src.Indices[t + 2]];
            double area = ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
            if (area == 0) continue;   // vertical: its column is covered by its neighbours' prisms
            if (area < 0) (b, c) = (c, b);
            int i = v.Count;
            v.AddRange([a, b, c, a with { Z = top }, b with { Z = top }, c with { Z = top }]);
            // bottom (the triangle, facing down), top (facing up), three sides (outward: the triangle is counter-clockwise in plan)
            idx.AddRange([i, i + 2, i + 1, i + 3, i + 4, i + 5, i, i + 1, i + 4, i, i + 4, i + 3, i + 1, i + 2, i + 5, i + 1, i + 5, i + 4, i + 2, i, i + 3, i + 2, i + 3, i + 5]);
        }
        return idx.Count == 0 ? this : new MeshVolume(Name, [.. v], [.. idx], src);
    }

    /// <summary>Whether some upward-facing triangle has a corner below <paramref name="top"/> (so something straight above the shape is lower than the height).</summary>
    bool NeedsRaising(float top)
    {
        for (int t = 0; t + 2 < Indices.Length; t += 3)
        {
            Vector3 a = Vertices[Indices[t]], b = Vertices[Indices[t + 1]], c = Vertices[Indices[t + 2]];
            double area = ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
            if (area > 0 && (a.Z < top || b.Z < top || c.Z < top)) return true;
        }
        return false;
    }

    /// <summary>
    /// A capsule (feet <paramref name="feet"/>, <paramref name="radius"/>, <paramref name="height"/>) whose axis meets the volume is moved out horizontally (the mesh equivalent of
    /// <see cref="SpawnBlocker.PushOut"/>; call it on the raised volume the world keeps): across the nearest outline edge of the authored shape (<see cref="FeatureEdges"/>) seen from above, to <paramref name="radius"/> + 1 cm beyond it, the first such place (nearest first, ties in triangle
    /// order) where the axis no longer meets the volume there nor 1 cm past the edge point (so the capsule clears the shape by its radius, also beside a leaning face); if none clears, out of the bounding box across its nearest side (ties: X sides, then the positive side). Otherwise <paramref name="feet"/>
    /// is returned unchanged. Provisional approximation (ours).
    /// </summary>
    public Vector3 PushOut(Vector3 feet, float radius, float height)
    {
        if (!float.IsFinite(radius) || radius < 0) radius = 0;   // a NaN or negative radius or height is read as none (Math.Max would carry a NaN through)
        if (!float.IsFinite(height) || height < 0) height = 0;
        if (!Geometry.Finite(feet) || !(feet.Z < Max.Z && feet.Z + height > Min.Z)) return feet;
        var up = new Vector3(0, 0, height);
        if (!Crosses(feet, feet + up)) return feet;
        double px = feet.X, py = feet.Y, beyond = radius + 0.01;
        var candidates = new List<(double D, int Order, double Qx, double Qy)>();
        int order = 0;
        foreach (var (ia, ib) in FeatureEdges())   // the authored outline only (a cap's fan diagonals and a raised volume's prism edges lie inside it and would push too short)
            {
                var a = Vertices[ia]; var b = Vertices[ib]; order++;   // the authored vertices are this volume's first ones
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
            // the column just past the edge point must be clear too: a leaning shape reaches beyond an edge of its foot, and the capsule would end up less than its radius from it
            var past = new Vector3((float)(qx + (qx - px) / n * 0.01), (float)(qy + (qy - py) / n * 0.01), feet.Z);
            if (!Crosses(to, to + up) && !Crosses(past, past + up)) return to;
        }
        double dxMin = px - Min.X, dxMax = Max.X - px, dyMin = py - Min.Y, dyMax = Max.Y - py;
        double best = Math.Min(Math.Min(dxMin, dxMax), Math.Min(dyMin, dyMax));
        if (best == dxMax) return feet with { X = (float)(Max.X + beyond) };
        if (best == dxMin) return feet with { X = (float)(Min.X - beyond) };
        if (best == dyMax) return feet with { Y = (float)(Max.Y + beyond) };
        return feet with { Y = (float)(Min.Y - beyond) };
    }

    /// <summary>
    /// The authored shape's outline for drawing (a door's shield, a point's ring) and for <see cref="PushOut"/>: every edge whose two triangles are not coplanar, or that has only one triangle, as
    /// vertex index pairs (lower first), in the order the triangles first walk them. Two same-facing triangles count as coplanar when each one's far corner lies within
    /// <see cref="CoplanarTolerance"/> of one plane (the tetrahedron of the edge and both far corners is that thin over the larger triangle; the 1/4096 m grid bends a flat face a little), so diagonals inside flat faces are left out. A raised volume returns its authored
    /// shape's outline (its indices are valid in the raised volume too); draw a raised door's shield from that outline up to <see cref="Max"/>.Z.
    /// </summary>
    public IReadOnlyList<(int A, int B)> FeatureEdges() => ReferenceEquals(Authored, this) ? Outline() : Authored.FeatureEdges();

    /// <summary>Metres: two same-facing triangles whose far corners lie this close to each other's plane are one flat face (half a millimetre; the vertex grid is 1/4096 m).</summary>
    public const double CoplanarTolerance = 0.0005;

    IReadOnlyList<(int A, int B)> Outline()
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
            double dot = n1.X * n2.X + n1.Y * n2.Y + n1.Z * n2.Z;
            if (dot <= 0 || Thickness(list[0], list[1], key) > CoplanarTolerance) result.Add(key);
        }
        return result;

        // the corner of triangle t that is not on the edge
        Vector3 Far(int t, (int A, int B) edge)
        {
            for (int k = 0; k < 3; k++) { int i = Indices[3 * t + k]; if (i != edge.A && i != edge.B) return Vertices[i]; }
            return Vertices[Indices[3 * t]];
        }
        // how far the two triangles on an edge are from one plane (metres): the height of the tetrahedron of the edge and both far corners over the larger triangle, 6V / (2 A_max). Measured
        // over the larger triangle, so a thin sliver (a tilted cap's fan triangle, whose own normal grid noise turns) does not make a flat face look bent
        double Thickness(int t1, int t2, (int A, int B) edge)
        {
            Vector3 a = Vertices[edge.A], b = Vertices[edge.B], c = Far(t1, edge), d = Far(t2, edge);
            double ux = (double)b.X - a.X, uy = (double)b.Y - a.Y, uz = (double)b.Z - a.Z, vx = (double)c.X - a.X, vy = (double)c.Y - a.Y, vz = (double)c.Z - a.Z;
            double wx = (double)d.X - a.X, wy = (double)d.Y - a.Y, wz = (double)d.Z - a.Z;
            double sixV = Math.Abs(ux * (vy * wz - vz * wy) - uy * (vx * wz - vz * wx) + uz * (vx * wy - vy * wx));
            var (n1, n2) = (normals[t1], normals[t2]);
            double twoA = Math.Max(Math.Sqrt(n1.X * n1.X + n1.Y * n1.Y + n1.Z * n1.Z), Math.Sqrt(n2.X * n2.X + n2.Y * n2.Y + n2.Z * n2.Z));
            return twoA == 0 ? double.MaxValue : sixV / twoA;
        }
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
