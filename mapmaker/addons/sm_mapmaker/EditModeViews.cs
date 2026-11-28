using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// What Edit mode draws on a piece (docs/mapmaker.md "Edit mode (editor)", owner-approved brief 2026-11-25 "Blender-style controls"): the shape's edges as a wire, its vertices as
/// dots in Vertex mode, and the selection in orange (selected edges and dots drawn over everything, fully selected faces filled, both triangles of a quad). Built in the piece's unit
/// space under a node with the content's basis, so it hangs under the object's node and its scale sizes it like the drawn shape. Never saved (no owner).
/// </summary>
public static class EditModeViews
{
    public const string NodeName = "EditOverlay";
    public static readonly Color Selected = new(1f, 0.55f, 0.1f), Wire = new(0.06f, 0.06f, 0.08f), Fill = new(1f, 0.55f, 0.1f, 0.38f), ActiveFill = new(1f, 0.7f, 0.25f, 0.45f);
    /// <summary>
    /// A picked edge (Edge mode, owner's recording 2026-11-28: the orange edge could not be told from Godot's orange selection box on the same edge): a near-white stroke
    /// with a dark border, drawn in view pixels so it keeps its width at any distance. Ours.
    /// </summary>
    public static readonly Color PickedEdge = new(1f, 0.97f, 0.78f), PickedEdgeBorder = new(0.05f, 0.05f, 0.07f, 0.9f);
    public const float PickedEdgeWidth = 4f, PickedEdgeBorderWidth = 7f;

    public static void DrawPickedEdges(Control c, IReadOnlyList<(Vector2 A, Vector2 B)> segments, float scale)
    {
        foreach (var (a, b) in segments) c.DrawLine(a, b, PickedEdgeBorder, PickedEdgeBorderWidth * scale, true);
        foreach (var (a, b) in segments) c.DrawLine(a, b, PickedEdge, PickedEdgeWidth * scale, true);
    }

    /// <summary>The faces whose every vertex the selection holds (Blender fills those in any mode): in Face mode exactly the selected faces.</summary>
    public static int[] FilledFaces(EditMesh mesh, ComponentMode mode, IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return [];
        if (mode == ComponentMode.Face) return ids.Where(f => f >= 0 && f < mesh.Faces.Count).Distinct().OrderBy(f => f).ToArray();
        var set = VertexSet(mesh, mode, ids);
        return Enumerable.Range(0, mesh.Faces.Count).Where(f => mesh.Faces[f].All(set.Contains)).ToArray();
    }

    public static HashSet<int> VertexSet(EditMesh mesh, ComponentMode mode, IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return [];
        try { return new ComponentSelection(mode, ids.ToArray()).VertexSet(mesh).ToHashSet(); }
        catch (MapException) { return []; }
    }

    /// <summary>The overlay of one object. <paramref name="fillTriangles"/>: how many triangles the face highlight has (a selected wall face: 2).</summary>
    public static Node3D Build(EditMesh mesh, ComponentMode mode, IReadOnlyCollection<int> ids, bool active, out int fillTriangles)
    {
        var root = new Node3D { Name = NodeName, Basis = Frame.C };
        var selV = VertexSet(mesh, mode, ids);
        var im = new ImmediateMesh();
        static Vector3 G(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
        StandardMaterial3D Mat(Color c, bool onTop, float point = 0f, bool alpha = false)
        {
            var m = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, NoDepthTest = onTop, CullMode = BaseMaterial3D.CullModeEnum.Disabled, RenderPriority = onTop ? 3 : 1 };
            if (point > 0) { m.UsePointSize = true; m.PointSize = point; }
            if (alpha) { m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.RenderPriority = 2; }
            return m;
        }
        // face fill: slightly off the face along its normal (no depth fight with the drawn face)
        fillTriangles = 0;
        var filled = FilledFaces(mesh, mode, ids);
        if (filled.Length > 0)
        {
            im.SurfaceBegin(Mesh.PrimitiveType.Triangles, Mat(active ? ActiveFill : Fill, false, alpha: true));
            foreach (var f in filled)
            {
                var face = mesh.Faces[f];
                var n = System.Numerics.Vector3.Zero;   // Newell normal of the polygon
                for (int k = 0; k < face.Length; k++) { var a = mesh.Vertices[face[k]]; var b = mesh.Vertices[face[(k + 1) % face.Length]]; n += new System.Numerics.Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
                n = n.LengthSquared() > 1e-12f ? System.Numerics.Vector3.Normalize(n) * 0.004f : System.Numerics.Vector3.Zero;
                for (int k = 1; k + 1 < face.Length; k++)
                {
                    im.SurfaceAddVertex(G(mesh.Vertices[face[0]] + n)); im.SurfaceAddVertex(G(mesh.Vertices[face[k + 1]] + n)); im.SurfaceAddVertex(G(mesh.Vertices[face[k]] + n));
                    fillTriangles++;
                }
            }
            im.SurfaceEnd();
        }
        var plain = mesh.Edges.Where(e => !(selV.Contains(e.A) && selV.Contains(e.B))).ToList();
        var picked = mesh.Edges.Where(e => selV.Contains(e.A) && selV.Contains(e.B)).ToList();
        if (plain.Count > 0)
        {
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, Mat(Wire, false));
            foreach (var (a, b) in plain) { im.SurfaceAddVertex(G(mesh.Vertices[a])); im.SurfaceAddVertex(G(mesh.Vertices[b])); }
            im.SurfaceEnd();
        }
        if (picked.Count > 0)
        {
            im.SurfaceBegin(Mesh.PrimitiveType.Lines, Mat(Selected, true));
            foreach (var (a, b) in picked) { im.SurfaceAddVertex(G(mesh.Vertices[a])); im.SurfaceAddVertex(G(mesh.Vertices[b])); }
            im.SurfaceEnd();
        }
        if (mode == ComponentMode.Vertex)
        {
            var off = Enumerable.Range(0, mesh.Vertices.Count).Where(i => !selV.Contains(i)).ToList();
            if (off.Count > 0)
            {
                im.SurfaceBegin(Mesh.PrimitiveType.Points, Mat(Wire, false, 7f));
                foreach (var i in off) im.SurfaceAddVertex(G(mesh.Vertices[i]));
                im.SurfaceEnd();
            }
            if (selV.Count > 0)
            {
                im.SurfaceBegin(Mesh.PrimitiveType.Points, Mat(Selected, true, 9f));
                foreach (var i in selV.OrderBy(i => i)) im.SurfaceAddVertex(G(mesh.Vertices[i]));
                im.SurfaceEnd();
            }
        }
        root.AddChild(new MeshInstance3D { Name = "Components", Mesh = im, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        return root;
    }

    // ---- the 2D layer over the view: component gizmo and the live dimensions ----------------------------------------------------------------------------------

    public static readonly Color[] AxisColours = [new(0.95f, 0.3f, 0.3f), new(0.45f, 0.9f, 0.3f), new(0.35f, 0.55f, 1f)];

    /// <summary>Where the component gizmo's handles are on screen (viewport pixels): the pivot, and per axis its unit screen direction (zero when it points at the camera).</summary>
    public readonly record struct GizmoLayout(Vector2 Pivot, Vector2[] Axes, float Length)
    {
        public Vector2 MoveTip(int a) => Pivot + Axes[a] * Length;
        public Vector2 ScaleHandle(int a) => Pivot + Axes[a] * (Length + 18f);
    }

    /// <summary>Which handle a press hits: (Move or Scale, axis), (Move, null) for the centre ring (free move), or null.</summary>
    public static (MeshEditKind Kind, int? Axis)? HitGizmo(GizmoLayout g, Vector2 p, float scale)
    {
        float reach = 8f * scale;
        for (int a = 0; a < 3; a++)
        {
            if (g.Axes[a].LengthSquared() < 1e-4f) continue;
            if ((p - g.ScaleHandle(a)).Length() < reach + 3f) return (MeshEditKind.Scale, a);
        }
        int best = -1; float bestD = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            if (g.Axes[a].LengthSquared() < 1e-4f) continue;
            var end = g.MoveTip(a) - g.Pivot;
            float t = Mathf.Clamp((p - g.Pivot).Dot(end) / end.LengthSquared(), 0f, 1f);
            float d = (p - (g.Pivot + end * t)).Length();
            if (t > 0.25f && d < reach && d < bestD) { best = a; bestD = d; }
        }
        if (best >= 0) return (MeshEditKind.Move, best);
        if ((p - g.Pivot).Length() < 11f * scale) return (MeshEditKind.Move, null);
        return null;
    }

    public static void DrawGizmo(Control c, GizmoLayout g, (MeshEditKind Kind, int? Axis)? active, float scale)
    {
        for (int a = 0; a < 3; a++)
        {
            if (g.Axes[a].LengthSquared() < 1e-4f) continue;
            bool hot = active is { } h && h.Axis == a;
            var col = hot ? Selected : AxisColours[a];
            var tip = g.MoveTip(a);
            c.DrawLine(g.Pivot, tip, col, 2.5f * scale, true);
            var side = new Vector2(-g.Axes[a].Y, g.Axes[a].X) * 6f * scale;
            c.DrawColoredPolygon([tip + g.Axes[a] * 12f * scale, tip + side, tip - side], col);
            var s = g.ScaleHandle(a);
            c.DrawRect(new Rect2(s - new Vector2(5, 5) * scale, new Vector2(10, 10) * scale), col, true);
        }
        c.DrawArc(g.Pivot, 9f * scale, 0, Mathf.Tau, 24, active is { Axis: null } ? Selected : new Color(1, 1, 1, 0.85f), 2f * scale, true);
    }

    /// <summary>The readout box: a title line ("Wall · X 5.00 m   Y 2.00 m   Z 1.00 m") and an optional second line (the edit in progress), top left of the view.</summary>
    public static void DrawReadout(Control c, string title, string? detail, float scale)
    {
        var font = ThemeDB.FallbackFont;
        int size = (int)(16 * scale), small = (int)(13 * scale);
        var w1 = font.GetStringSize(title, HorizontalAlignment.Left, -1, size).X;
        var w2 = detail is null ? 0 : font.GetStringSize(detail, HorizontalAlignment.Left, -1, small).X;
        var origin = new Vector2(14, 72) * scale;   // below the view's own Perspective menu
        float h = (detail is null ? 28 : 48) * scale;
        c.DrawRect(new Rect2(origin - new Vector2(8, 20) * scale, new Vector2(Math.Max(w1, w2) + 16 * scale, h)), new Color(0.05f, 0.06f, 0.08f, 0.78f), true);
        c.DrawString(font, origin, title, HorizontalAlignment.Left, -1, size, new Color(1, 1, 1));
        if (detail is not null) c.DrawString(font, origin + new Vector2(0, 20 * scale), detail, HorizontalAlignment.Left, -1, small, Selected.Lightened(0.25f));
    }
}
