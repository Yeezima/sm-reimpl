using Godot;

namespace SmMapmakerEditor;

/// <summary>
/// One document object in the scene tree. It owns only a document id and a transform (so the editor can select and move it); what it draws is a
/// runtime child with no owner (see <see cref="ContentFactory"/>). Its transform is the Godot-frame image of the document transform (Frame.cs).
/// </summary>
[Tool, GlobalClass]
public partial class MapmakerObject : Node3D
{
    [Export] public string ObjectId { get; set; } = "";

    /// <summary>Box of the drawn content in this node's local (Godot, Y up) frame; used by the gizmo for the outline and for click picking.</summary>
    public Aabb LocalBounds { get; set; } = new(new Vector3(-0.25f, 0f, -0.25f), new Vector3(0.5f, 0.5f, 0.5f));

    Node3D? _content;

    /// <summary>Counts <see cref="SetContent"/> calls: a view cache keyed on it knows when the drawing was replaced.</summary>
    public int ContentVersion { get; private set; }

    public void SetContent(Node3D? content, Aabb bounds)
    {
        ContentVersion++;
        if (_content is not null) { RemoveChild(_content); _content.QueueFree(); }
        _content = content;
        if (content is not null) { content.Name = "Content"; AddChild(content); }   // no owner: never serialised
        LocalBounds = bounds.Size.LengthSquared() < 1e-6f ? new Aabb(new Vector3(-0.25f, 0f, -0.25f), new Vector3(0.5f, 0.5f, 0.5f)) : bounds;
        UpdateGizmos();
    }

    public void SetShown(bool visible) => Visible = visible;
}
