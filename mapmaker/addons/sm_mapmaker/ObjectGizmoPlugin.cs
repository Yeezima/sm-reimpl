using Godot;

namespace SmMapmakerEditor;

/// <summary>The outline and click target of a <see cref="MapmakerObject"/>: a wire box of its content (none while Edit mode reshapes it), and its faces as the pick surface.</summary>
public partial class ObjectGizmoPlugin : EditorNode3DGizmoPlugin
{
    public ObjectGizmoPlugin()
    {
        CreateMaterial("box", new Color(0.35f, 0.75f, 1f, 0.55f));   // Godot adds the selected variant itself
    }

    public override string _GetGizmoName() => "SM Mapmaker object";
    public override bool _HasGizmo(Node3D forNode3D) => forNode3D is MapmakerObject;

    public override void _Redraw(EditorNode3DGizmo gizmo)
    {
        gizmo.Clear();
        if (gizmo.GetNode3D() is not MapmakerObject o) return;
        var b = o.LocalBounds;
        // Edit mode (docs/mapmaker.md "Edit mode (editor)"): an object whose shape is being edited shows its own edges and the component gizmo instead of the box
        bool editing = MapHost.Plugin?.IsInEditSet(o.ObjectId) == true;
        var lines = new Vector3[24];
        int n = 0;
        Vector3 Corner(int i) => b.Position + new Vector3((i & 1) * b.Size.X, ((i >> 1) & 1) * b.Size.Y, ((i >> 2) & 1) * b.Size.Z);
        foreach (var (p, q) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
        { lines[n++] = Corner(p); lines[n++] = Corner(q); }
        if (!editing) gizmo.AddLines(lines, GetMaterial("box", gizmo));
        var box = new BoxMesh { Size = b.Size };
        var tri = box.GenerateTriangleMesh();
        // BoxMesh is centred on the origin; move it to the box.
        var faces = tri.GetFaces();
        var moved = new Vector3[faces.Length];
        for (int i = 0; i < faces.Length; i++) moved[i] = faces[i] + b.GetCenter();
        var shifted = new TriangleMesh();
        shifted.CreateFromFaces(moved);
        gizmo.AddCollisionTriangles(shifted);
    }
}
