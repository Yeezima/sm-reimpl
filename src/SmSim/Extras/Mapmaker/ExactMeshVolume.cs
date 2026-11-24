using System.Numerics;

namespace SmSim.Extras.Mapmaker;

/// <summary>
/// A Mapmaker edited shape as the simulation's <see cref="IExactVolume"/> (added content, docs/mapmaker.md "Edit mode in play", plan M2): a capture point, start area, spawn door or
/// kill zone whose shape the author edited acts by this shape instead of its box. <see cref="Contains"/> is <see cref="MeshVolume.Contains"/>; <see cref="Meets"/> adds an exact
/// segment-triangle test in double with IEEE + - * / only; <see cref="StretchedTo"/> scales the heights above the bottom (float + - * / only). Every answer is the same on every OS.
/// </summary>
public sealed class ExactMeshVolume : IExactVolume
{
    public MeshVolume Mesh { get; }

    public ExactMeshVolume(MeshVolume mesh) { ArgumentNullException.ThrowIfNull(mesh); Mesh = mesh; }

    public Vector3 Min => Mesh.Min;
    public Vector3 Max => Mesh.Max;
    public IReadOnlyList<Vector3> Vertices => Mesh.Vertices;
    public IReadOnlyList<int> Indices => Mesh.Indices;

    public bool Contains(Vector3 p) => Mesh.Contains(p);

    public bool Meets(Vector3 a, Vector3 b)
    {
        if (Mesh.Contains(a) || Mesh.Contains(b)) return true;
        // the segment's box against the shape's
        if (MathF.Max(a.X, b.X) < Min.X || MathF.Min(a.X, b.X) > Max.X || MathF.Max(a.Y, b.Y) < Min.Y || MathF.Min(a.Y, b.Y) > Max.Y || MathF.Max(a.Z, b.Z) < Min.Z || MathF.Min(a.Z, b.Z) > Max.Z) return false;
        double ox = a.X, oy = a.Y, oz = a.Z, dx = (double)b.X - a.X, dy = (double)b.Y - a.Y, dz = (double)b.Z - a.Z;
        var v = Mesh.Vertices; var idx = Mesh.Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3)
            if (SegmentHits(ox, oy, oz, dx, dy, dz, v[idx[t]], v[idx[t + 1]], v[idx[t + 2]])) return true;
        return false;
    }

    /// <summary>Möller-Trumbore in double, closed (edges and both ends count); a segment in the triangle's plane does not hit (its ends are tested by <see cref="Contains"/>).</summary>
    static bool SegmentHits(double ox, double oy, double oz, double dx, double dy, double dz, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        double e1x = (double)p1.X - p0.X, e1y = (double)p1.Y - p0.Y, e1z = (double)p1.Z - p0.Z;
        double e2x = (double)p2.X - p0.X, e2y = (double)p2.Y - p0.Y, e2z = (double)p2.Z - p0.Z;
        double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
        double det = e1x * px + e1y * py + e1z * pz;
        if (det == 0) return false;
        double sx = ox - p0.X, sy = oy - p0.Y, sz = oz - p0.Z;
        double u = (sx * px + sy * py + sz * pz) / det;
        if (u < 0 || u > 1) return false;
        double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
        double w = (dx * qx + dy * qy + dz * qz) / det;
        if (w < 0 || u + w > 1) return false;
        double t = (e2x * qx + e2y * qy + e2z * qz) / det;
        return t >= 0 && t <= 1;
    }

    public IExactVolume StretchedTo(float top)
    {
        if (!float.IsFinite(top) || top <= Max.Z || !(Max.Z > Min.Z)) return this;
        float bottom = Min.Z, k = (top - bottom) / (Max.Z - bottom);
        var stretched = new Vector3[Mesh.Vertices.Length];
        for (int i = 0; i < stretched.Length; i++)
        {
            var p = Mesh.Vertices[i];
            stretched[i] = new Vector3(p.X, p.Y, p.Z == Max.Z ? top : bottom + (p.Z - bottom) * k);
        }
        return new ExactMeshVolume(new MeshVolume(Mesh.Name, stretched, Mesh.Indices));
    }
}
