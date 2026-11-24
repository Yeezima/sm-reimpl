using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>What Edit mode selects (Blender's 1 / 2 / 3).</summary>
public enum ComponentMode { Vertex, Edge, Face }

/// <summary>A component selection on one object: vertex, edge or face ids of its <see cref="EditMesh"/>.</summary>
public sealed record ComponentSelection(ComponentMode Mode, IReadOnlyList<int> Ids)
{
    /// <summary>The distinct vertices the selection moves (a vertex shared by two selected faces moves once), ascending. Unknown ids: <c>unknown-component</c>.</summary>
    public int[] VertexSet(EditMesh mesh)
    {
        if (Ids.Count == 0) throw new MapException("empty-selection", "No vertex, edge or face is selected.");
        var set = new SortedSet<int>();
        foreach (var id in Ids)
        {
            switch (Mode)
            {
                case ComponentMode.Vertex:
                    if (id < 0 || id >= mesh.Vertices.Count) throw new MapException("unknown-component", $"No vertex {id} (the shape has {mesh.Vertices.Count}).");
                    set.Add(id); break;
                case ComponentMode.Edge:
                    if (id < 0 || id >= mesh.Edges.Count) throw new MapException("unknown-component", $"No edge {id} (the shape has {mesh.Edges.Count}).");
                    set.Add(mesh.Edges[id].A); set.Add(mesh.Edges[id].B); break;
                default:
                    if (id < 0 || id >= mesh.Faces.Count) throw new MapException("unknown-component", $"No face {id} (the shape has {mesh.Faces.Count}).");
                    foreach (var v in mesh.Faces[id]) set.Add(v);
                    break;
            }
        }
        return [.. set];
    }

    public static ComponentMode ParseMode(string? s, string code = "bad-op") => s switch
    {
        "vertex" or "v" => ComponentMode.Vertex,
        "edge" or "e" => ComponentMode.Edge,
        "face" or "f" => ComponentMode.Face,
        _ => throw new MapException(code, $"mode must be \"vertex\", \"edge\" or \"face\", not '{s}'."),
    };

    public static string ModeName(ComponentMode m) => m switch { ComponentMode.Vertex => "vertex", ComponentMode.Edge => "edge", _ => "face" };
}

public enum MeshEditKind { Move, Rotate, Scale }

/// <summary>Where a rotate or scale turns or grows about (Blender's pivot point). Move ignores it.</summary>
public enum PivotMode
{
    /// <summary>The centre of the selection's bounding box (default; a face's centre, an edge's midpoint): scaling a column's top face tapers it while its base stays planted.</summary>
    Selection,
    /// <summary>The mean of the selected vertices.</summary>
    Median,
    /// <summary>The object's origin (bottom centre of the footprint).</summary>
    Origin,
    /// <summary>An explicit point in sized space (<see cref="MeshEdit.PivotPoint"/>).</summary>
    Point,
}

/// <summary>
/// One component edit in the object's own axes (sized space: metres, scale applied). Only the selected vertices change; every other vertex keeps its stored bits (so pulling one face of a
/// wall keeps the opposite face where it was). <see cref="Axis"/> constrains to one axis (0 X, 1 Y, 2 Z): a move keeps only that component, a scale grows only that axis, a rotation needs it.
/// </summary>
public sealed record MeshEdit(MeshEditKind Kind, Vector3 Amount, int? Axis = null, PivotMode Pivot = PivotMode.Selection, Vector3 PivotPoint = default, float Degrees = 0)
{
    public static MeshEdit Move(Vector3 delta, int? axis = null) => new(MeshEditKind.Move, delta, axis);
    public static MeshEdit Rotate(int axis, float degrees, PivotMode pivot = PivotMode.Selection) => new(MeshEditKind.Rotate, Vector3.Zero, axis, pivot, default, degrees);
    public static MeshEdit Scale(Vector3 factor, int? axis = null, PivotMode pivot = PivotMode.Selection) => new(MeshEditKind.Scale, factor, axis, pivot);
    public static MeshEdit Scale(float factor, int? axis = null, PivotMode pivot = PivotMode.Selection) => Scale(new Vector3(factor), axis, pivot);

    /// <summary>The same edit with its amount (move delta, scale factor, rotation degrees) replaced: a drag or a typed value updates this and re-previews from the start shape.</summary>
    public MeshEdit WithAmount(Vector3 amount, float degrees = 0) => this with { Amount = amount, Degrees = degrees };

    public static int? ParseAxis(JsonNode? n, string code = "bad-op") => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s switch { "x" or "X" => 0, "y" or "Y" => 1, "z" or "Z" => 2, _ => throw new MapException(code, $"axis must be \"x\", \"y\" or \"z\", not '{s}'.") },
        _ => throw new MapException(code, "axis must be \"x\", \"y\" or \"z\"."),
    };

    public static string AxisName(int axis) => axis switch { 0 => "x", 1 => "y", _ => "z" };

    /// <summary>The op fields of this edit (<c>move</c> / <c>rotate</c> / <c>scale</c>, <c>axis</c>, <c>pivot</c>) for an <c>edit-mesh</c> operation.</summary>
    public void WriteTo(JsonObject op)
    {
        switch (Kind)
        {
            case MeshEditKind.Move: op["move"] = Json.Vec(Amount); break;
            case MeshEditKind.Scale: op["scale"] = Json.Vec(Amount); break;
            default: op["rotate"] = Degrees; break;
        }
        if (Axis is { } a) op["axis"] = AxisName(a);
        if (Kind != MeshEditKind.Move)
            op["pivot"] = Pivot switch { PivotMode.Median => "median", PivotMode.Origin => "origin", PivotMode.Point => Json.Vec(PivotPoint), _ => "selection" };
    }

    /// <summary>Reads the edit fields of an <c>edit-mesh</c> operation (exactly one of move, rotate, scale).</summary>
    public static MeshEdit FromOp(JsonObject op)
    {
        int kinds = (op["move"] is null ? 0 : 1) + (op["rotate"] is null ? 0 : 1) + (op["scale"] is null ? 0 : 1);
        if (kinds != 1) throw new MapException("bad-op", "edit-mesh needs exactly one of move, rotate or scale.");
        var axis = ParseAxis(op["axis"]);
        PivotMode pivot = PivotMode.Selection; Vector3 point = default;
        switch (op["pivot"])
        {
            case null: break;
            case JsonArray: pivot = PivotMode.Point; point = Json.Vec(op["pivot"], "pivot", "bad-op"); break;
            case JsonValue pv when pv.TryGetValue<string>(out var ps):
                pivot = ps switch { "selection" => PivotMode.Selection, "median" => PivotMode.Median, "origin" => PivotMode.Origin, _ => throw new MapException("bad-op", $"pivot must be \"selection\", \"median\", \"origin\" or [x, y, z], not '{ps}'.") };
                break;
            default: throw new MapException("bad-op", "pivot must be \"selection\", \"median\", \"origin\" or [x, y, z].");
        }
        if (op["move"] is { } m) return new MeshEdit(MeshEditKind.Move, Json.Vec(m, "move", "bad-op"), axis);
        if (op["scale"] is { } s) return new MeshEdit(MeshEditKind.Scale, MapTransform.ReadScale(s, "scale"), axis, pivot, point);
        if (axis is null) throw new MapException("bad-op", "rotate needs an axis (\"x\", \"y\" or \"z\").");
        return new MeshEdit(MeshEditKind.Rotate, Vector3.Zero, axis, pivot, point, Json.Num(op["rotate"], "rotate", "bad-op"));
    }
}

/// <summary>
/// Pure component edits on an <see cref="EditMesh"/> (no document, no undo): the editor previews a drag with these and the <c>edit-mesh</c> operation commits the same call, so the preview and
/// the saved shape are the same bits. Maths in double; the result is rounded to float once per vertex.
/// </summary>
public static class MeshOps
{
    /// <summary>Smallest size (metres, sized space) an edited shape may have along any of its own axes (<c>bad-mesh</c> below it: a flat shape has no inside).</summary>
    public const float MinExtent = 0.01f;

    /// <summary>
    /// Applies <paramref name="edit"/> to the selected vertices of <paramref name="mesh"/> on an object of scale <paramref name="scale"/>. Unselected vertices are copied bit for bit.
    /// <exception cref="MapException"><c>unknown-component</c>, <c>empty-selection</c>, <c>bad-mesh</c> (the result is flatter than <see cref="MinExtent"/> on an axis, or not finite).</exception>
    /// </summary>
    public static EditMesh Apply(EditMesh mesh, Vector3 scale, ComponentSelection selection, MeshEdit edit)
    {
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0) throw new MapException("bad-transform", "The object's scale has a zero component.");
        var picked = selection.VertexSet(mesh);
        var sized = mesh.Vertices.Select(v => ToSized(v, scale)).ToArray();
        var pivot = PivotOf(edit, picked.Select(i => sized[i]).ToArray());
        var result = mesh.Vertices.ToArray();
        foreach (var i in picked)
        {
            var p = sized[i];
            (double x, double y, double z) q = edit.Kind switch
            {
                MeshEditKind.Move => Moved(p, edit),
                MeshEditKind.Scale => Scaled(p, pivot, edit),
                _ => Rotated(p, pivot, edit),
            };
            result[i] = new Vector3((float)(q.x / scale.X), (float)(q.y / scale.Y), (float)(q.z / scale.Z));
        }
        var edited = mesh.WithVertices(result);   // validates finiteness and the unit-space limit
        CheckExtent(edited, scale);
        return edited;
    }

    /// <summary>Unit space to sized space (metres along the object's own axes).</summary>
    public static (double X, double Y, double Z) ToSized(Vector3 v, Vector3 scale) => ((double)v.X * scale.X, (double)v.Y * scale.Y, (double)v.Z * scale.Z);

    static (double, double, double) PivotOf(MeshEdit edit, (double X, double Y, double Z)[] pts)
    {
        switch (edit.Pivot)
        {
            case PivotMode.Origin: return (0, 0, 0);
            case PivotMode.Point: return (edit.PivotPoint.X, edit.PivotPoint.Y, edit.PivotPoint.Z);
            case PivotMode.Median:
            {
                double x = 0, y = 0, z = 0;
                foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
                return (x / pts.Length, y / pts.Length, z / pts.Length);
            }
            default:
            {
                double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue, hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
                foreach (var p in pts) { lx = Math.Min(lx, p.X); ly = Math.Min(ly, p.Y); lz = Math.Min(lz, p.Z); hx = Math.Max(hx, p.X); hy = Math.Max(hy, p.Y); hz = Math.Max(hz, p.Z); }
                return ((lx + hx) / 2, (ly + hy) / 2, (lz + hz) / 2);
            }
        }
    }

    static (double, double, double) Moved((double X, double Y, double Z) p, MeshEdit e)
    {
        var d = e.Amount;
        if (e.Axis is { } a) d = ComponentEdit.WithAxis(Vector3.Zero, a, ComponentEdit.Get(d, a));
        return (p.X + d.X, p.Y + d.Y, p.Z + d.Z);
    }

    static (double, double, double) Scaled((double X, double Y, double Z) p, (double X, double Y, double Z) c, MeshEdit e)
    {
        var f = e.Amount;
        if (e.Axis is { } a) f = ComponentEdit.WithAxis(Vector3.One, a, ComponentEdit.Get(f, a));
        return (c.X + (p.X - c.X) * f.X, c.Y + (p.Y - c.Y) * f.Y, c.Z + (p.Z - c.Z) * f.Z);
    }

    static (double, double, double) Rotated((double X, double Y, double Z) p, (double X, double Y, double Z) c, MeshEdit e)
    {
        var (s, co) = DetMath.SinCosDegrees(e.Degrees);   // deterministic sin/cos, angle snapped to 1/1000 degree
        double x = p.X - c.X, y = p.Y - c.Y, z = p.Z - c.Z;
        // counter-clockwise looking down the positive axis (right hand), as the object rotation fields turn
        (x, y, z) = e.Axis switch
        {
            0 => (x, y * co - z * s, y * s + z * co),
            1 => (x * co + z * s, y, -x * s + z * co),
            _ => (x * co - y * s, x * s + y * co, z),
        };
        return (c.X + x, c.Y + y, c.Z + z);
    }

    static void CheckExtent(EditMesh mesh, Vector3 scale)
    {
        var d = MeshDims.Of(mesh, scale);
        if (d.X < MinExtent || d.Y < MinExtent || d.Z < MinExtent)
            throw new MapException("bad-mesh", $"The edit would make the shape flatter than {MinExtent * 100:0} cm along one of its axes ({d.X:0.###} x {d.Y:0.###} x {d.Z:0.###} m).");
    }
}

/// <summary>Dimensions of a piece's resulting geometry (the brief's live X / Y / Z readout): the extents of its shape along the object's own axes, in metres, with its scale applied.</summary>
public static class MeshDims
{
    /// <summary>X, Y, Z extents in metres (sized space; positive also for a mirrored scale).</summary>
    public static Vector3 Of(EditMesh mesh, Vector3 scale)
    {
        double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue, hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
        foreach (var v in mesh.Vertices)
        {
            var (x, y, z) = MeshOps.ToSized(v, scale);
            lx = Math.Min(lx, x); ly = Math.Min(ly, y); lz = Math.Min(lz, z); hx = Math.Max(hx, x); hy = Math.Max(hy, y); hz = Math.Max(hz, z);
        }
        return new Vector3((float)(hx - lx), (float)(hy - ly), (float)(hz - lz));
    }

    /// <summary>
    /// The dimensions of a piece object: its stored mesh's, or its stock shape's (then exactly |scale|), except an unedited capture point, which plays as a circle of its X size and so reads
    /// (|X|, |X|, |Z|). Its first edit starts from the 16-gon of its X and Y sizes (docs/mapmaker.md "Edit mode contract", first edit of a round piece).
    /// </summary>
    public static Vector3 Of(MapObject o)
    {
        if (o.Mesh is null && o.Piece?.Kind == PieceKind.CapturePoint)
        {
            var s = o.Transform.Scale;
            return new Vector3(MathF.Abs(s.X), MathF.Abs(s.X), MathF.Abs(s.Z));
        }
        return Of(EditMesh.Of(o), o.Transform.Scale);
    }

    /// <summary>"X 5.00 m   Y 2.00 m   Z 1.00 m" (the brief's readout; two decimals, invariant culture).</summary>
    public static string Label(Vector3 d) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"X {d.X:0.00} m   Y {d.Y:0.00} m   Z {d.Z:0.00} m");
}
