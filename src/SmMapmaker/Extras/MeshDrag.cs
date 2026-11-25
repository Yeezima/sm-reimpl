using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// One component edit in progress (a gizmo drag, or G / R / S then an axis and a typed value): previews from the shapes as they were when it began, never touches the document, and ends in
/// exactly one of <see cref="Commit"/> (one <c>edit-mesh</c> batch = one undo step, the same bits as the last preview) or <see cref="Cancel"/> (nothing changed). Engine-free: the editor feeds it
/// the gizmo's or keyboard's amount each frame and draws <see cref="Preview"/> and <see cref="Dims"/>.
/// </summary>
public sealed class MeshDrag
{
    readonly MapSession _session;
    readonly int _startVersion;
    readonly Dictionary<string, (EditMesh Start, Vector3 Scale)> _start = [];
    readonly Dictionary<string, EditMesh> _preview = [];
    bool _done;

    public IReadOnlyList<(string Id, ComponentSelection Selection)> Targets { get; }
    public MeshEdit Edit { get; private set; }
    /// <summary>Why the last <see cref="Update"/> was refused (the preview keeps the last good shape), or null.</summary>
    public string? Error { get; private set; }
    /// <summary>The shapes as they would be after the edit, by object id.</summary>
    public IReadOnlyDictionary<string, EditMesh> Preview => _preview;
    /// <summary>The active object (the first target): the readout is labelled with its name.</summary>
    public string ActiveId => Targets[0].Id;

    /// <exception cref="MapException"><c>not-editable</c>, <c>unknown-id</c>, <c>unknown-component</c>, <c>empty-selection</c>.</exception>
    public MeshDrag(MapSession session, IReadOnlyList<(string Id, ComponentSelection Selection)> targets, MeshEdit edit)
    {
        if (targets.Count == 0) throw new MapException("empty-selection", "Nothing is selected in Edit mode.");
        _session = session;
        _startVersion = session.Version;
        Targets = targets;
        Edit = edit;
        foreach (var (id, sel) in targets)
        {
            var o = session.Document.Get(id);
            var mesh = EditMesh.Of(o);
            sel.VertexSet(mesh);   // validates the selection now
            _start[id] = (mesh, o.Transform.Scale);
            _preview[id] = mesh;
        }
        Update(edit);
    }

    /// <summary>Re-previews with a new amount (or a new axis): always from the start shapes, so a drag never accumulates rounding. False (and <see cref="Error"/>) when the edit is refused.</summary>
    public bool Update(MeshEdit edit)
    {
        if (_done) throw new InvalidOperationException("The edit has ended.");
        var next = new Dictionary<string, EditMesh>();
        try
        {
            foreach (var (id, sel) in Targets) next[id] = MeshOps.Apply(_start[id].Start, _start[id].Scale, sel, edit);
        }
        catch (MapException e) { Error = e.Message; return false; }
        Edit = edit;
        Error = null;
        foreach (var (k, v) in next) _preview[k] = v;
        return true;
    }

    /// <summary>Live dimensions (metres, the object's own axes, scale applied) of a target's previewed shape.</summary>
    public Vector3 Dims(string id) => MeshDims.Of(_preview[id], _start[id].Scale);

    /// <summary>The <c>edit-mesh</c> operation this edit commits.</summary>
    public JsonObject ToOp()
    {
        var op = new JsonObject
        {
            ["op"] = "edit-mesh",
            ["targets"] = new JsonArray(Targets.Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Id, ["mode"] = ComponentSelection.ModeName(t.Selection.Mode), ["ids"] = new JsonArray(t.Selection.Ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
            }).ToArray()),
        };
        Edit.WriteTo(op);
        return op;
    }

    /// <summary>Commits the edit as one batch (one undo step). Refused when the document changed since the edit began (<c>stale-edit</c>) or the last update was refused.</summary>
    public OpResult Commit()
    {
        if (_done) throw new InvalidOperationException("The edit has ended.");
        _done = true;
        if (_session.Version != _startVersion) return OpResult.Fail("stale-edit", "The map changed while the edit was in progress; nothing was applied.", "edit-mesh");
        if (Error is not null) return OpResult.Fail("bad-mesh", Error, "edit-mesh");
        return _session.Apply(ToOp());
    }

    /// <summary>Ends the edit with nothing changed (Esc or right click).</summary>
    public void Cancel() => _done = true;
}
