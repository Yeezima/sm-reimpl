using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// What a gameplay piece looks like in the editor (docs/mapmaker.md "Pieces"): solid pieces as grey shapes, markers as tinted see-through boxes or cylinders with a green arrow on the
/// side they face (+Y). Built in the document frame (Z up, the piece's unit-space shape: its edited mesh or its stock shape; the object's own scale makes the size) under a node whose basis converts to Y up, like every content node.
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

    public static Node3D Build(PieceSpec piece, out Aabb bounds) => Build(piece, null, out bounds);

    /// <summary>
    /// The piece drawn from its editable shape (docs/mapmaker.md "Edit mode contract"): <paramref name="mesh"/> (the stored mesh, or a drag's preview), else its stock shape
    /// (<see cref="EditMesh.Stock(PieceSpec)"/>), in unit space; the object's scale still sizes it. The faces are the shape's own polygons, so Edit mode's vertex dots sit on the drawn corners.
    /// </summary>
    /// <para>An unedited round piece (capture point, round players-only wall) keeps its old drawing, a smooth circle, because that is how it plays (contract v2): a capture point is a circle
    /// of its X size (pass <paramref name="scale"/> so its Y is drawn at the X size too; MeshDims reads (X, X, Z)), and its first edit turns it into the 16-gon of its topology.</para>
    public static Node3D Build(PieceSpec piece, EditMesh? mesh, out Aabb bounds, System.Numerics.Vector3? scale = null)
    {
        var shapeMesh = mesh ?? EditMesh.Stock(piece);
        bool circle = mesh is null && (piece.Kind == PieceKind.CapturePoint || piece is { Kind: PieceKind.MoveWall, IsCylinder: true });
        float yFix = circle && piece.Kind == PieceKind.CapturePoint && scale is { } sc && sc.Y != 0 && float.IsFinite(sc.X / sc.Y) ? MathF.Abs(sc.X / sc.Y) : 1f;
        System.Numerics.Vector3[] tv; int[] ti;
        List<(System.Numerics.Vector3, System.Numerics.Vector3)> outline = new();
        if (circle)
        {
            (tv, ti) = PieceMesh.Cylinder(40);
            tv = tv.Select(v => new System.Numerics.Vector3(v.X, v.Y * yFix, v.Z)).ToArray();
            const int n = 24;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.Tau / n, a1 = (i + 1) * Mathf.Tau / n;
                System.Numerics.Vector3 P(float a, float z) => new(0.5f * MathF.Cos(a), 0.5f * MathF.Sin(a) * yFix, z);
                outline.Add((P(a0, 0), P(a1, 0))); outline.Add((P(a0, 1), P(a1, 1)));
                if (i % 6 == 0) outline.Add((P(a0, 0), P(a0, 1)));
            }
            bounds = ContentFactory.ToGodot(new Bounds(new System.Numerics.Vector3(-0.5f, -0.5f * yFix, 0f), new System.Numerics.Vector3(0.5f, 0.5f * yFix, 1f)));
        }
        else
        {
            (tv, ti, _) = shapeMesh.Triangulate();
            foreach (var (a, b) in shapeMesh.Edges) outline.Add((shapeMesh.Vertices[a], shapeMesh.Vertices[b]));
            bounds = ContentFactory.ToGodot(shapeMesh.UnitBounds());
        }
        var root = new Node3D { Basis = Frame.C };
        var colour = ColourOf(piece);
        var info = piece.Info;
        var drawn = Flat(tv, ti);
        var material = DefaultMaterial(piece);
        root.AddChild(new MeshInstance3D { Name = "Shape", Mesh = drawn, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        // Markers and ramps show which way they face (+Y) with a green arrow; the outline of a marker is a wire box in its colour.
        bool arrow = piece.Kind is PieceKind.SpawnArea or PieceKind.Blocker or PieceKind.Ramp;
        bool wire = !info.Solid;
        if (arrow || wire)
        {
            var lines = new ImmediateMesh();
            if (wire)
            {
                // the outline is the shape's own edges (a round piece shows its sides), so an edited marker's wire follows its edited corners
                lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
                foreach (var (p, q) in outline) { lines.SurfaceAddVertex(new Vector3(p.X, p.Y, p.Z)); lines.SurfaceAddVertex(new Vector3(q.X, q.Y, q.Z)); }
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

    /// <summary>The editor's own look of a piece with no map material: grey solid pieces, tinted see-through markers.</summary>
    static StandardMaterial3D DefaultMaterial(PieceSpec piece)
    {
        var colour = ColourOf(piece);
        if (piece.Info.Solid && piece.Kind != PieceKind.CollisionBox)
            return new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        return new StandardMaterial3D
        {
            AlbedoColor = new Color(colour, piece.Kind == PieceKind.CollisionBox ? 0.18f : piece.Kind is PieceKind.MoveWall or PieceKind.Cover ? 0.24f : 0.3f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    /// <summary>
    /// The material hook (docs/mapmaker.md "Materials (editor)"): draws the piece's "Shape" with a map material's surface built by the game's own builder (<see cref="SmClient.MaterialBuild"/>:
    /// the mesh with normals, tangents and UVs in metres from <see cref="RenderMesh"/>, UVs sized by <paramref name="scale"/>, positions left in unit space for the node's scale as the stock
    /// drawing is), or, with <paramref name="surface"/> null, puts the default look back. Preview = saved = in game.
    /// </summary>
    public static void ApplySurface(MeshInstance3D shape, PieceSpec piece, EditMesh? mesh, System.Numerics.Vector3 scale, StandardMaterial3D? surface)
    {
        var m = mesh ?? EditMesh.Stock(piece);
        if (surface is null)
        {
            var (tv, ti, _) = m.Triangulate();
            shape.Mesh = Flat(tv, ti);
            shape.MaterialOverride = DefaultMaterial(piece);
            return;
        }
        shape.Mesh = SmClient.MaterialBuild.Mesh(RenderMesh.Build(m, scale, System.Numerics.Matrix4x4.Identity));
        shape.MaterialOverride = surface;
    }

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
