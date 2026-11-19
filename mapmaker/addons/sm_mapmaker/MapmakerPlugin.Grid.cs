using System;
using System.Linq;
using System.Text.Json.Nodes;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// The Grid section's logic (docs/mapmaker.md "Grid"): the map's <see cref="MapGrid"/> drawn by <see cref="EditorGrid"/> under the scene root, edits as the
/// <c>set-grid</c> operation (one undo step each, like every other Map tab edit that changes the document), and Godot's own subdividing grid switched off while a Mapmaker scene is open.
/// </summary>
public partial class MapmakerPlugin
{
    /// <summary>The settings in force: the open map's, or the defaults while no map is open (the grid still shows in an empty Mapmaker scene).</summary>
    public MapGrid CurrentGrid => Session?.Document.Grid ?? MapGrid.Default;

    /// <summary>Draws the grid for the current document (or a preview of <paramref name="preview"/>, not stored) and keeps Godot's own grid off while a Mapmaker scene is open.</summary>
    public void ApplyGrid(MapGrid? preview = null, bool hold = false)
    {
        if (MapHost.Root is { } root && GodotObject.IsInstanceValid(root))
        {
            EditorGrid.Apply(root, preview ?? CurrentGrid);
            if (hold || !EditorGrid.BuiltInHeld) EditorGrid.HideBuiltIn();   // after the first time, only when a scene opens: a user who switches Godot's grid on again is left alone
        }
        else EditorGrid.RestoreBuiltIn();
    }

    int _holdRun;

    /// <summary>
    /// A Mapmaker scene became the edited one: keeps Godot's own grid off for the next second and a half. Godot applies a scene's own grid state a moment after the scene
    /// opens (and again after a tab switch), which would switch its grid back on right after ours was hidden.
    /// </summary>
    void HoldEditorGrid()
    {
        int run = ++_holdRun;
        ApplyGrid(hold: true);
        void Again(int left)
        {
            if (run != _holdRun || left <= 0 || !IsInsideTree() || MapHost.Root is null) return;
            GetTree().CreateTimer(0.15).Timeout += () => { if (run == _holdRun) { ApplyGrid(hold: true); Again(left - 1); } };
        }
        Again(10);
    }

    /// <summary>Changes the named grid settings as one undo step; nothing happens when they already hold. Returns the result (a failed one is also in the status line).</summary>
    public OpResult SetGrid(string label, params (string Key, JsonNode? Value)[] fields)
    {
        if (Session is null) return OpResult.Fail("no-document", "No map is open: use New or Open first.");
        // numbers go in as doubles (what a file holds), whatever the caller's type
        var op = Op("set-grid", fields.Select(f => (f.Key, f.Value is JsonValue v && v.TryGetValue<float>(out var x) ? JsonValue.Create((double)x) : f.Value)).ToArray());
        try
        {
            if (Session.Document.Grid.Apply(op) == Session.Document.Grid) { ApplyGrid(); return new OpResult(true, [], [], []); }
        }
        catch (MapException e) { SetStatus($"{label}: {e.Message}"); ApplyGrid(); return OpResult.Fail(e.Code, e.Message, "set-grid"); }
        return Do(label, op);
    }

    /// <summary>
    /// The lowest point of the selected objects' drawn boxes, as an SM1 Z in metres (the grid's "base" of a selection: where it stands), or null when nothing with a box is selected.
    /// Measured from each node's content box in the scene root's frame, so parents, rotation and scale are included.
    /// </summary>
    public float? SelectionBaseHeight()
    {
        if (MapHost.Root is not { } root) return null;
        var toRoot = root.GlobalTransform.AffineInverse();
        float? low = null;
        foreach (var id in SelectedIds())
        {
            if (!_nodes.TryGetValue(id, out var n) || !GodotObject.IsInstanceValid(n) || !n.IsInsideTree()) continue;
            var t = toRoot * n.GlobalTransform;
            var b = n.LocalBounds;
            for (int i = 0; i < 8; i++)
            {
                var c = b.Position + new Vector3((i & 1) * b.Size.X, ((i >> 1) & 1) * b.Size.Y, ((i >> 2) & 1) * b.Size.Z);
                float z = (t * c).Y;   // Godot Y is SM1 Z
                if (low is null || z < low) low = z;
            }
        }
        return low is { } l ? MathF.Round(l, 3) : null;
    }
}
