using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient;

namespace SmMapmakerEditor;

/// <summary>
/// The scene root of a Mapmaker scene: a view of the open document (<see cref="MapHost"/>) and, optionally, the SM1 map as a backdrop.
/// Everything it draws is a runtime child without an owner, so the scene file never holds an SM1 mesh (docs/mapmaker.md, "Original assets
/// never touch disk"). Owned children exist only as selection targets (<see cref="MapmakerObject"/>: an id and a transform, no geometry).
/// </summary>
[Tool, GlobalClass]
public partial class MapmakerRoot : Node3D
{
    MapView? _backdrop;
    Node3D? _highlights;

    public MapView? Backdrop => _backdrop;

    /// <summary>The SM1 map the backdrop draws (null when none is shown).</summary>
    public Sm1Source? BackdropSource { get; private set; }

    public override void _EnterTree()
    {
        // A saved scene may carry owned object nodes from an earlier session; the document is the truth, so they are rebuilt from it.
        foreach (var child in GetChildren().OfType<MapmakerObject>()) { RemoveChild(child); child.QueueFree(); }
        MapHost.AttachRoot(this);
    }

    public override void _ExitTree() => MapHost.DetachRoot(this);

    /// <summary>Shows (or hides) the SM1 map's meshes as a reference; replaces an earlier backdrop.</summary>
    public void ShowBackdrop(Sm1Source? source, List<string> notes)
    {
        if (_backdropSpace is not null) { RemoveChild(_backdropSpace); _backdropSpace.QueueFree(); _backdropSpace = null; _backdrop = null; }
        BackdropSource = source;
        if (source is null) return;
        _backdrop = source.BuildBackdrop(notes);
        // MapView lives in game space (Z up); the parent converts to Y up. No owner is set on purpose.
        var space = new Node3D { Name = "Sm1Backdrop", Basis = Frame.C };
        space.AddChild(_backdrop);
        AddChild(space);
        _backdropSpace = space;
    }

    Node3D? _backdropSpace;

    public bool BackdropVisible
    {
        get => _backdropSpace?.Visible ?? false;
        set { if (_backdropSpace is not null) _backdropSpace.Visible = value; }
    }

    /// <summary>Wireframe boxes around SM1 placements picked for extraction (game frame boxes drawn in Y up).</summary>
    public void ShowHighlights(IEnumerable<(Vector3 Min, Vector3 Max)> boxes)
    {
        if (_highlights is not null) { RemoveChild(_highlights); _highlights.QueueFree(); _highlights = null; }
        var list = boxes.ToList();
        if (list.Count == 0) return;
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var (min, max) in list)
        {
            Vector3 Corner(int i) => Frame.ToGodot(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
            foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
            { mesh.SurfaceAddVertex(Corner(a)); mesh.SurfaceAddVertex(Corner(b)); }
        }
        mesh.SurfaceEnd();
        _highlights = new Node3D { Name = "Sm1Picked" };
        _highlights.AddChild(new MeshInstance3D
        {
            Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.75f, 0.1f), NoDepthTest = true },
        });
        AddChild(_highlights);
    }
}
