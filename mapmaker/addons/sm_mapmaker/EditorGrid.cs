using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>
/// The Mapmaker's own 3D grid (docs/mapmaker.md "Grid"). It is a fixed set of lines in world space: spacing is the map's cell size, the area is
/// <see cref="MapGrid.HalfExtent"/> metres each way from the origin, the lines are the GPU's one pixel wide, and nothing about the camera (zoom, distance, angle)
/// reaches it: no subdivision, no fade. Built as one ArrayMesh of line segments with an unshaded material; the mesh is rebuilt only when the cell size changes.
/// The node is a runtime child of the scene root (no owner), so it never enters the scene file.
/// </summary>
public static class EditorGrid
{
    public const string NodeName = "MapmakerGrid";
    /// <summary>The most lines in one direction (cell size 0.1 m over the fixed 400 m area gives 4001, 16004 vertices in all): the mesh stays small however the cell size is set.</summary>
    public const int MaxLinesPerAxis = 4001;

    /// <summary>Lines each way (so 2n + 1 lines per direction) for a cell size: the whole cells that fit in the half extent.</summary>
    public static int HalfCount(float cell) => Math.Clamp((int)MathF.Floor(MapGrid.HalfExtent / cell + 1e-4f), 1, (MaxLinesPerAxis - 1) / 2);

    /// <summary>Line end points (pairs) in the Godot frame (Y up) at height 0 of the grid plane.</summary>
    public static Vector3[] Lines(float cell)
    {
        int n = HalfCount(cell);
        float ext = n * cell;
        var v = new List<Vector3>((2 * n + 1) * 4);
        for (int i = -n; i <= n; i++)
        {
            float t = i * cell;
            v.Add(new Vector3(t, 0, -ext)); v.Add(new Vector3(t, 0, ext));   // along game Y
            v.Add(new Vector3(-ext, 0, t)); v.Add(new Vector3(ext, 0, t));   // along game X
        }
        return v.ToArray();
    }

    /// <summary>Creates or updates the grid node under <paramref name="root"/> from the settings; returns it.</summary>
    public static MeshInstance3D Apply(Node3D root, MapGrid g)
    {
        var node = root.GetNodeOrNull<MeshInstance3D>(NodeName);
        if (node is null)
        {
            node = new MeshInstance3D { Name = NodeName, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = new StandardMaterial3D() };
            node.SetMeta("cell", 0f);
            root.AddChild(node);   // no owner: never serialised
        }
        if (node.GetMeta("cell").AsSingle() != g.CellSize)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = Lines(g.CellSize);
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
            node.Mesh = mesh;
            node.SetMeta("cell", g.CellSize);
        }
        var (r, gr, b, a) = g.Bytes();
        var mat = (StandardMaterial3D)node.MaterialOverride!;
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        mat.AlbedoColor = new Color(r / 255f, gr / 255f, b / 255f, a / 255f);
        mat.NoDepthTest = g.DrawOver;
        mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        mat.RenderPriority = g.DrawOver ? 100 : 0;
        node.Position = new Vector3(0, g.Height, 0);   // SM1 Z (up) is Godot Y
        node.Visible = g.Show;
        return node;
    }

    // ---- Godot's own editor grid (it subdivides with zoom, so it is switched off while a Mapmaker map is open) ----
    // The 3D editor has a "View Grid" check item in its View menu and one in each viewport's menu. All of them are switched off through their own menus (so the editor's
    // state stays consistent) and each one that was on is switched on again afterwards.

    static readonly Dictionary<ulong, bool> Held = new();   // menu instance id -> was the item checked before we switched it off

    static bool IsViewGrid(PopupMenu pm, int i) => pm.IsItemCheckable(i) && pm.GetItemText(i).Contains("Grid", StringComparison.OrdinalIgnoreCase) && pm.GetItemText(i).Contains("View", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every "View Grid" check item of the editor, as (menu, item index).</summary>
    static List<(PopupMenu Menu, int Index)> Items()
    {
        var list = new List<(PopupMenu, int)>();
        try
        {
            foreach (var n in EditorInterface.Singleton.GetBaseControl().FindChildren("*", "PopupMenu", true, false))
                if (n is PopupMenu pm) for (int i = 0; i < pm.ItemCount; i++) if (IsViewGrid(pm, i)) list.Add((pm, i));
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>Test aid: the checked state of every "View Grid" item (the 3D editor's menu and each viewport's); empty when this Godot has none.</summary>
    public static bool[] ViewGridStates() => Items().Select(x => x.Menu.IsItemChecked(x.Index)).ToArray();

    /// <summary>True while a Mapmaker scene holds Godot's grid off (the user's own setting is restored by <see cref="RestoreBuiltIn"/>).</summary>
    public static bool BuiltInHeld => Held.Count > 0;

    /// <summary>
    /// Switches Godot's grid off, remembering which items were on. Godot applies a scene's own grid state while a scene is being opened, so the plugin calls this again a few times
    /// after a Mapmaker scene opens (<c>MapmakerPlugin.HoldEditorGrid</c>); an item already remembered keeps its first remembered state.
    /// </summary>
    public static void HideBuiltIn()
    {
        foreach (var (m, i) in Items())
        {
            Held.TryAdd(m.GetInstanceId(), m.IsItemChecked(i));
            if (m.IsItemChecked(i)) m.EmitSignal(PopupMenu.SignalName.IdPressed, m.GetItemId(i));
        }
    }

    /// <summary>Gives the user's grid back: every item that was on is on again (Godot may already have re-applied some when it switched scenes).</summary>
    public static void RestoreBuiltIn()
    {
        if (Held.Count == 0) return;
        var was = new Dictionary<ulong, bool>(Held);
        Held.Clear();
        foreach (var (m, i) in Items())
            if (was.TryGetValue(m.GetInstanceId(), out var on) && on && !m.IsItemChecked(i)) m.EmitSignal(PopupMenu.SignalName.IdPressed, m.GetItemId(i));
    }
}
