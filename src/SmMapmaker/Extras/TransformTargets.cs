namespace SmMapmaker;

/// <summary>
/// Which selected objects take a typed Position / Rotation / Scale entry in the dock's Object tab (owner, 2026-11-18: "a selected child rotates on its own,
/// unless the all of the children and the parent are selected. basically whatever is selected is rotated, regardless of parent/children relationship").
/// Exactly the selected objects are edited, nothing unselected is ever touched, and a selected object whose ancestor is also selected is not edited a second
/// time: its selected ancestor's entry already carries it (the same as Godot's own multi-select rotate, where the child keeps its relation to the parent).
/// A selected child with its parent unselected is a target by itself and turns alone.
/// </summary>
public static class TransformTargets
{
    /// <summary>The selected ids that get the entry directly: those with no selected ancestor, in selection order.</summary>
    public static List<string> Roots(MapDocument doc, IReadOnlyList<string> selected) =>
        selected.Distinct().Where(id => !selected.Any(other => other != id && doc.IsDescendantOf(id, other))).ToList();
}
