using System.Numerics;

namespace SmMapmaker;

/// <summary>
/// A piece's shape ready to draw (editor preview and game alike, docs/mapmaker.md "Materials and images"): flat-shaded triangles, three corners each, in the document frame (Z up) after the
/// piece's world matrix, with a normal, a tangent (xyz + handedness w, for normal maps) and a UV per corner.
/// <para><b>UVs</b> are planar per face in sized space (metres along the object's own axes, scale applied): a face whose normal is mostly X is mapped by (Y, Z), mostly Y by (X, Z), mostly Z
/// (ties go to Z, then X) by (X, Y), oriented so the picture is not mirrored seen from outside and its top is toward +Z on walls. One UV unit is one metre, so pulling a face adds texture instead of
/// stretching it, and a material's mapping scale is repeats per metre (ours, provisional).</para>
/// <para>Winding: the triangles are those of <see cref="EditMesh.Triangulate"/> (counter-clockwise from outside in the document frame). A renderer whose frame flips handedness swaps two corners,
/// as the client's custom-map view already does.</para>
/// </summary>
public sealed record RenderMesh(Vector3[] Positions, Vector3[] Normals, Vector4[] Tangents, Vector2[] Uvs, int[] TriangleFace)
{
    /// <summary>The shape of a piece, sized by <paramref name="scale"/> and placed by <paramref name="world"/> (which includes that scale, as <see cref="DrawPiece.World"/> does).</summary>
    public static RenderMesh Build(EditMesh mesh, Vector3 scale, Matrix4x4 world)
    {
        var (verts, idx, tf) = mesh.Triangulate();
        int n = idx.Length;
        var pos = new Vector3[n]; var nor = new Vector3[n]; var tan = new Vector4[n]; var uv = new Vector2[n];
        var faceAxis = new (int U, int V, float Su, float Sv)[mesh.Faces.Count];
        for (int f = 0; f < mesh.Faces.Count; f++) faceAxis[f] = Projection(mesh, f, scale);
        for (int t = 0; t < n / 3; t++)
        {
            var (ua, va, su, sv) = faceAxis[tf[t]];
            for (int k = 0; k < 3; k++)
            {
                var v = verts[idx[t * 3 + k]];
                var sized = v * scale;
                pos[t * 3 + k] = Vector3.Transform(v, world);
                uv[t * 3 + k] = new Vector2(su * Get(sized, ua), -sv * Get(sized, va));
            }
            var a = pos[t * 3]; var b = pos[t * 3 + 1]; var c = pos[t * 3 + 2];
            var normal = Vector3.Cross(b - a, c - a);
            normal = normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitZ;
            // tangent from the UV derivatives (world space), Gram-Schmidt against the normal; w is the bitangent's handedness
            var e1 = b - a; var e2 = c - a;
            var d1 = uv[t * 3 + 1] - uv[t * 3]; var d2 = uv[t * 3 + 2] - uv[t * 3];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            Vector3 tg, bt;
            if (MathF.Abs(det) > 1e-12f)
            {
                float r = 1f / det;
                tg = (e1 * d2.Y - e2 * d1.Y) * r;
                bt = (e2 * d1.X - e1 * d2.X) * r;
            }
            else { tg = Vector3.Cross(MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, normal); bt = Vector3.Cross(normal, tg); }
            tg -= normal * Vector3.Dot(normal, tg);
            tg = tg.LengthSquared() > 0 ? Vector3.Normalize(tg) : Vector3.UnitX;
            float w = Vector3.Dot(Vector3.Cross(normal, tg), bt) < 0 ? -1f : 1f;
            for (int k = 0; k < 3; k++) { nor[t * 3 + k] = normal; tan[t * 3 + k] = new Vector4(tg, w); }
        }
        return new RenderMesh(pos, nor, tan, uv, tf);
    }

    static float Get(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    /// <summary>Which sized-space axes map to U and V for face <paramref name="f"/>, and their signs.</summary>
    static (int, int, float, float) Projection(EditMesh mesh, int f, Vector3 scale)
    {
        // Newell normal of the polygon in sized space (robust for a bent quad)
        var face = mesh.Faces[f];
        Vector3 nrm = Vector3.Zero;
        for (int k = 0; k < face.Length; k++)
        {
            var p = mesh.Vertices[face[k]] * scale; var q = mesh.Vertices[face[(k + 1) % face.Length]] * scale;
            nrm += new Vector3((p.Y - q.Y) * (p.Z + q.Z), (p.Z - q.Z) * (p.X + q.X), (p.X - q.X) * (p.Y + q.Y));
        }
        if (scale.X * scale.Y * scale.Z < 0) nrm = -nrm;   // a mirrored object turns its faces inside out in sized space
        float ax = MathF.Abs(nrm.X), ay = MathF.Abs(nrm.Y), az = MathF.Abs(nrm.Z);
        if (az >= ax && az >= ay) return (0, 1, nrm.Z >= 0 ? 1f : -1f, -1f);   // floor/ceiling: (x, y); v negated back below so +Y is up in the picture
        if (ax >= ay) return (1, 2, nrm.X >= 0 ? 1f : -1f, 1f);             // +X face: u along +Y, -X face: along -Y; v up +Z
        return (0, 2, nrm.Y >= 0 ? -1f : 1f, 1f);                           // +Y face: u along -X, -Y face: along +X
    }
}
