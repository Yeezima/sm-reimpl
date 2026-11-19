using System.Linq;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// What a gameplay piece looks like in the editor (docs/mapmaker.md "Pieces"): solid pieces as grey shapes, markers as tinted see-through boxes or cylinders with a green arrow on the
/// side they face (+Y). Built in the document frame (Z up, the unit shape; the object's own scale makes the size) under a node whose basis converts to Y up, like every content node.
/// Nothing here is ever saved.
/// </summary>
public static class PieceViews
{
    public static readonly Color Marines = new(0.25f, 0.5f, 1f), Chaos = new(1f, 0.3f, 0.25f), Point = new(1f, 0.85f, 0.2f), Kill = new(0.9f, 0.15f, 0.55f), Wall = new(0.15f, 0.85f, 0.8f), Cover = new(1f, 0.6f, 0.15f), Solid = new(0.62f, 0.64f, 0.68f);

    public static Color ColourOf(PieceSpec p) => p.Kind switch
    {
        PieceKind.SpawnArea or PieceKind.Blocker => p.Side == PieceSpec.Marines ? Marines : Chaos,
        PieceKind.CapturePoint => Point,
        PieceKind.KillZone => Kill,
        PieceKind.Ramp => new Color(0.55f, 0.7f, 0.85f),
        PieceKind.CollisionBox => new Color(0.8f, 0.8f, 0.8f),
        PieceKind.MoveWall => Wall,   // teal, see-through: the players-only wall (distinct from the grey Invisible wall)
        PieceKind.Cover => Cover,     // orange, see-through: the shoot-through cover
        _ => Solid,
    };

    public static Node3D Build(PieceSpec piece, out Aabb bounds)
    {
        bounds = ContentFactory.ToGodot(new Bounds(new System.Numerics.Vector3(-0.5f, -0.5f, 0f), new System.Numerics.Vector3(0.5f, 0.5f, 1f)));
        var root = new Node3D { Basis = Frame.C };
        var colour = ColourOf(piece);
        var info = piece.Info;
        (System.Numerics.Vector3[] V, int[] I) shape = piece.Kind == PieceKind.CapturePoint || piece is { Kind: PieceKind.MoveWall, Shape: PieceSpec.CylinderShape } ? PieceMesh.Cylinder(40)
            : PieceMesh.Unit(piece.Kind) ?? PieceMesh.MarkerBox();
        var mesh = Flat(shape.V, shape.I);
        StandardMaterial3D material;
        if (info.Solid && piece.Kind != PieceKind.CollisionBox)
            material = new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        else
            material = new StandardMaterial3D
            {
                AlbedoColor = new Color(colour, piece.Kind == PieceKind.CollisionBox ? 0.18f : piece.Kind is PieceKind.MoveWall or PieceKind.Cover ? 0.24f : 0.3f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
        root.AddChild(new MeshInstance3D { Name = "Shape", Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        // Markers and ramps show which way they face (+Y) with a green arrow; the outline of a marker is a wire box in its colour.
        bool arrow = piece.Kind is PieceKind.SpawnArea or PieceKind.Blocker or PieceKind.Ramp;
        bool wire = !info.Solid;
        if (arrow || wire)
        {
            var lines = new ImmediateMesh();
            if (wire && piece.IsCylinder)
            {
                lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
                const int n = 24;
                for (int i = 0; i < n; i++)
                {
                    float a0 = i * Mathf.Tau / n, a1 = (i + 1) * Mathf.Tau / n;
                    foreach (float z in new[] { 0f, 1f })
                    {
                        lines.SurfaceAddVertex(new Vector3(0.5f * Mathf.Cos(a0), 0.5f * Mathf.Sin(a0), z));
                        lines.SurfaceAddVertex(new Vector3(0.5f * Mathf.Cos(a1), 0.5f * Mathf.Sin(a1), z));
                    }
                    if (i % 6 == 0) { lines.SurfaceAddVertex(new Vector3(0.5f * Mathf.Cos(a0), 0.5f * Mathf.Sin(a0), 0)); lines.SurfaceAddVertex(new Vector3(0.5f * Mathf.Cos(a0), 0.5f * Mathf.Sin(a0), 1)); }
                }
                lines.SurfaceEnd();
            }
            else if (wire)
            {
                lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
                foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
                {
                    lines.SurfaceAddVertex(Corner(a)); lines.SurfaceAddVertex(Corner(b));
                }
                lines.SurfaceEnd();
            }
            root.AddChild(new MeshInstance3D { Name = "Outline", Mesh = lines, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = colour.Lightened(0.2f) } });
            if (arrow)
            {
                var a = new ImmediateMesh();
                a.SurfaceBegin(Mesh.PrimitiveType.Lines);
                float z = piece.Kind == PieceKind.Ramp ? 0.7f : 0.5f;
                float y0 = piece.Kind == PieceKind.Ramp ? -0.3f : -0.15f, y1 = 0.42f;
                float z0 = piece.Kind == PieceKind.Ramp ? 0.15f : z, z1 = piece.Kind == PieceKind.Ramp ? 0.85f : z;
                void L(Vector3 p, Vector3 q) { a.SurfaceAddVertex(p); a.SurfaceAddVertex(q); }
                L(new Vector3(0, y0, z0), new Vector3(0, y1, z1));
                L(new Vector3(0, y1, z1), new Vector3(-0.12f, y1 - 0.15f, z1 - (piece.Kind == PieceKind.Ramp ? 0.1f : 0f)));
                L(new Vector3(0, y1, z1), new Vector3(0.12f, y1 - 0.15f, z1 - (piece.Kind == PieceKind.Ramp ? 0.1f : 0f)));
                a.SurfaceEnd();
                root.AddChild(new MeshInstance3D { Name = "Facing", Mesh = a, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.3f, 1f, 0.35f), NoDepthTest = true } });
            }
        }
        return root;
    }

    static Vector3 Corner(int i) => new((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? 0f : 1f);

    static ArrayMesh Flat(System.Numerics.Vector3[] v, int[] tri)
    {
        var vertices = new Vector3[tri.Length];
        var normals = new Vector3[tri.Length];
        for (int t = 0; t + 2 < tri.Length; t += 3)
        {
            var a = v[tri[t]]; var b = v[tri[t + 1]]; var c = v[tri[t + 2]];
            var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(b - a, c - a));
            if (!float.IsFinite(n.X)) n = System.Numerics.Vector3.UnitZ;
            vertices[t] = new(a.X, a.Y, a.Z); vertices[t + 1] = new(c.X, c.Y, c.Z); vertices[t + 2] = new(b.X, b.Y, b.Z);
            normals[t] = normals[t + 1] = normals[t + 2] = new(n.X, n.Y, n.Z);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
