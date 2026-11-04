using System.Numerics;
using System.Text.Json.Nodes;
using SmContent;

namespace SmMapmaker;

/// <summary>
/// A transform stored as components (docs/mapmaker.md "No transform drift"): position in metres, rotation as Euler degrees about X, then Y, then Z
/// (fixed axes, <see cref="MapScene.Rotation"/>), scale. Composed to a matrix only for views and for parent/world maths.
/// </summary>
public readonly record struct MapTransform(Vector3 Position, Vector3 Rotation, Vector3 Scale)
{
    public static readonly MapTransform Identity = new(Vector3.Zero, Vector3.Zero, Vector3.One);

    /// <summary>Same as <see cref="MapScene.Transform"/>: scale, then rotate, then translate (row vectors).</summary>
    public Matrix4x4 ToMatrix() => MapScene.Transform(Position, Rotation, Scale);

    /// <summary>Position, rotation and scale all finite and no scale component zero; otherwise <c>bad-transform</c>.</summary>
    public MapTransform Checked(string what = "transform")
    {
        foreach (var (v, n) in new[] { (Position, "position"), (Rotation, "rotation"), (Scale, "scale") })
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new MapException("bad-transform", $"{what}: {n} is not finite.");
        if (Scale.X == 0 || Scale.Y == 0 || Scale.Z == 0) throw new MapException("bad-transform", $"{what}: scale has a zero component.");
        return this;
    }

    public JsonObject ToNode() => new() { ["position"] = Json.Vec(Position), ["rotation"] = Json.Vec(Rotation), ["scale"] = Json.Vec(Scale) };

    /// <summary>Reads any of position, rotation, scale (a number for a uniform scale); a missing one keeps <paramref name="defaults"/>.</summary>
    public static MapTransform FromNode(JsonNode? node, MapTransform defaults, string what = "transform")
    {
        if (node is null) return defaults;
        var o = Json.Obj(node, what, "bad-transform");
        var p = o["position"] is { } pn ? Json.Vec(pn, what + ".position") : defaults.Position;
        var r = o["rotation"] is { } rn ? Json.Vec(rn, what + ".rotation") : defaults.Rotation;
        var s = o["scale"] is { } sn ? ReadScale(sn, what + ".scale") : defaults.Scale;
        return new MapTransform(p, r, s).Checked(what);
    }

    internal static Vector3 ReadScale(JsonNode n, string what) =>
        n is JsonArray ? Json.Vec(n, what) : new Vector3(Json.Num(n, what));

    /// <summary>Components of a matrix made by <see cref="MapScene.Transform"/> (or any product of such matrices without shear), rounded to 5 decimals.</summary>
    public static MapTransform FromMatrix(Matrix4x4 m) => TransformMath.Decompose(m);
}

/// <summary>An axis-aligned box in the document frame (Z up).</summary>
public readonly record struct Bounds(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;

    public Bounds Union(Bounds o) => new(Vector3.Min(Min, o.Min), Vector3.Max(Max, o.Max));

    /// <summary>The box of the eight corners moved by <paramref name="m"/> (row-vector matrix).</summary>
    public Bounds Transformed(Matrix4x4 m)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var c = Vector3.Transform(new Vector3((i & 1) == 0 ? Min.X : Max.X, (i & 2) == 0 ? Min.Y : Max.Y, (i & 4) == 0 ? Min.Z : Max.Z), m);
            lo = Vector3.Min(lo, c);
            hi = Vector3.Max(hi, c);
        }
        return new Bounds(lo, hi);
    }

    public JsonObject ToNode() => new() { ["min"] = Json.Vec(Min), ["max"] = Json.Vec(Max) };

    public static Bounds FromNode(JsonNode? n, string what)
    {
        var o = Json.Obj(n, what);
        return new Bounds(Json.Vec(o["min"], what + ".min", "bad-file"), Json.Vec(o["max"], what + ".max", "bad-file"));
    }
}

/// <summary>What an object or prefab part draws: an SM1 object definition (by map and name; streamed from the install) or a custom library asset.</summary>
public sealed record AssetRef(string Source, string? Map, string? Definition, string? Id)
{
    public const string Sm1Source = "sm1", CustomSource = "custom";
    public static AssetRef Sm1(string map, string definition) => new(Sm1Source, map, definition, null);
    public static AssetRef Custom(string id) => new(CustomSource, null, null, id);
    public bool IsSm1 => Source == Sm1Source;

    public string Label => IsSm1 ? Definition ?? "" : Id ?? "";

    public JsonObject ToNode() => IsSm1
        ? new JsonObject { ["source"] = Source, ["map"] = Map, ["definition"] = Definition }
        : new JsonObject { ["source"] = Source, ["id"] = Id };

    public static AssetRef FromNode(JsonNode? n, string what, string code)
    {
        var o = Json.Obj(n, what, code);
        switch (Json.ReqStr(o, "source", what, code))
        {
            case Sm1Source:
                var map = Json.ReqStr(o, "map", what, code);
                var def = Json.ReqStr(o, "definition", what, code);
                if (map.Length == 0 || def.Length == 0) throw new MapException(code, $"{what}: map and definition must not be empty.");
                return Sm1(map, def);
            case CustomSource:
                var id = Json.ReqStr(o, "id", what, code);
                if (!Json.IsSlug(id)) throw new MapException(code, $"{what}: '{id}' is not an asset id.");
                return Custom(id);
            default: throw new MapException(code, $"{what}.source must be \"sm1\" or \"custom\".");
        }
    }
}

/// <summary>A per-part change a prefab instance keeps (Phase 1: visibility and transform).</summary>
public sealed record PartOverride(string Part, bool? Visible, MapTransform? Transform)
{
    public JsonObject ToNode()
    {
        var o = new JsonObject { ["part"] = Part };
        if (Visible is { } v) o["visible"] = v;
        if (Transform is { } t) o["transform"] = t.ToNode();
        return o;
    }
}

/// <summary>One placed object: a plain asset, a linked prefab instance, or an empty group; with a parent when nested (transform local to it).</summary>
public sealed record MapObject(
    string Id, string Name, string? Parent, string Layer, bool Visible, MapTransform Transform, AssetRef? Asset, string? Prefab,
    IReadOnlyList<PartOverride> Overrides)
{
    public JsonObject ToNode()
    {
        var o = new JsonObject
        {
            ["id"] = Id, ["name"] = Name, ["parent"] = Parent, ["layer"] = Layer, ["visible"] = Visible, ["transform"] = Transform.ToNode(),
        };
        if (Asset is not null) o["asset"] = Asset.ToNode();
        if (Prefab is not null) { o["prefab"] = Prefab; o["overrides"] = new JsonArray(Overrides.Select(x => (JsonNode)x.ToNode()).ToArray()); }
        return o;
    }

    public static MapObject FromNode(JsonNode? n, int index)
    {
        string what = $"objects[{index}]";
        var o = Json.Obj(n, what);
        var id = Json.ReqStr(o, "id", what);
        if (!MapDocument.TryIdNumber(id, out _)) throw new MapException("bad-file", $"{what}: '{id}' is not an object id (o<N>).");
        var asset = o["asset"] is { } an ? AssetRef.FromNode(an, what + ".asset", "bad-file") : null;
        var prefab = Json.Str(o["prefab"], what + ".prefab", nullable: true);
        if (asset is not null && prefab is not null) throw new MapException("bad-file", $"{what}: has both asset and prefab.");
        var overrides = new List<PartOverride>();
        if (o["overrides"] is { } on)
            foreach (var (item, i) in Json.Arr(on, what + ".overrides").Select((x, i) => (x, i)))
            {
                var io = Json.Obj(item, $"{what}.overrides[{i}]");
                bool? vis = io["visible"] is { } vn ? Json.Bool(vn, "visible", "bad-file", true) : null;
                MapTransform? tr = io["transform"] is { } tn ? MapTransform.FromNode(tn, MapTransform.Identity, $"{what}.overrides[{i}].transform") : null;
                overrides.Add(new PartOverride(Json.ReqStr(io, "part", $"{what}.overrides[{i}]"), vis, tr));
            }
        if (prefab is null && overrides.Count > 0) throw new MapException("bad-file", $"{what}: overrides without a prefab.");
        return new MapObject(id, Json.Str(o["name"], what + ".name") ?? "", Json.Str(o["parent"], what + ".parent", nullable: true),
            Json.Str(o["layer"], what + ".layer", nullable: true) ?? "default", Json.Bool(o["visible"], what + ".visible", "bad-file", true),
            MapTransform.FromNode(o["transform"], MapTransform.Identity, what + ".transform"), asset, prefab, overrides);
    }
}

/// <summary>A <c>.smmap</c> document (formatVersion 1). Mutable; sessions edit a clone and swap it in when a batch succeeds.</summary>
public sealed class MapDocument
{
    public const string FormatName = "smmap";
    public const int FormatVersion = 1;

    public string Name { get; set; } = "Untitled";
    public int NextId { get; set; } = 1;
    public List<MapObject> Objects { get; } = [];

    public static MapDocument Create(string name) => new() { Name = name };

    public static bool TryIdNumber(string id, out int n)
    {
        n = 0;
        return id.Length > 1 && id[0] == 'o' && id.Skip(1).All(char.IsAsciiDigit) && id.Length <= 10 && int.TryParse(id.AsSpan(1), out n) && n >= 1 && id == "o" + n;
    }

    public MapObject? Find(string id) => Objects.FirstOrDefault(o => o.Id == id);

    public MapObject Get(string id) => Find(id) ?? throw new MapException("unknown-id", $"No object '{id}'.", id);

    public int IndexOf(string id) => Objects.FindIndex(o => o.Id == id);

    public IEnumerable<MapObject> ChildrenOf(string? parent) => Objects.Where(o => o.Parent == parent);

    /// <summary>The object and everything below it, depth first in document order.</summary>
    public List<MapObject> Subtree(string id)
    {
        var result = new List<MapObject>();
        void Walk(MapObject o, int depth)
        {
            if (depth > 1000) throw new MapException("parent-cycle", "The parent chain is too deep or cyclic.", o.Id);
            result.Add(o);
            foreach (var c in ChildrenOf(o.Id)) Walk(c, depth + 1);
        }
        Walk(Get(id), 0);
        return result;
    }

    public bool IsDescendantOf(string id, string ancestor)
    {
        int guard = 0;
        for (var cur = Find(id); cur?.Parent is { } p; cur = Find(p))
        {
            if (p == ancestor) return true;
            if (++guard > 1000) break;
        }
        return false;
    }

    /// <summary>The object's world matrix: its local matrix times its parent chain's (row vectors).</summary>
    public Matrix4x4 World(string id)
    {
        var m = Matrix4x4.Identity;
        int guard = 0;
        for (var cur = Get(id); ; cur = Get(cur.Parent!))
        {
            m *= cur.Transform.ToMatrix();
            if (cur.Parent is null) break;
            if (++guard > 1000) throw new MapException("parent-cycle", "The parent chain is cyclic.", id);
        }
        return m;
    }

    public JsonObject ToNode() => new()
    {
        ["format"] = FormatName, ["formatVersion"] = FormatVersion, ["name"] = Name, ["nextId"] = NextId,
        ["objects"] = new JsonArray(Objects.Select(o => (JsonNode)o.ToNode()).ToArray()),
    };

    public string ToJson() => CanonicalJson.Write(ToNode());

    public MapDocument Clone()
    {
        var d = new MapDocument { Name = Name, NextId = NextId };
        d.Objects.AddRange(Objects);
        return d;
    }

    public static MapDocument Parse(string json)
    {
        var o = Json.Obj(Json.ParseText(json, "The map"), "The map");
        if (Json.Str(o["format"], "format", nullable: true) != FormatName) throw new MapException("bad-file", "Not a .smmap document (format must be \"smmap\").");
        if (o["formatVersion"] is null || Json.Int(o["formatVersion"], "formatVersion", "bad-file") != FormatVersion)
            throw new MapException("bad-file", $"Unsupported formatVersion (this build reads {FormatVersion}).");
        var d = new MapDocument { Name = Json.Str(o["name"], "name") ?? "", NextId = Json.Int(o["nextId"], "nextId", "bad-file") };
        var seen = new HashSet<string>();
        foreach (var (n, i) in Json.Arr(o["objects"], "objects").Select((x, i) => (x, i)))
        {
            var obj = MapObject.FromNode(n, i);
            if (!seen.Add(obj.Id)) throw new MapException("bad-file", $"Duplicate object id '{obj.Id}'.", obj.Id);
            d.Objects.Add(obj);
        }
        return d;
    }

    /// <summary>
    /// Everything a stored document can get wrong: dangling or cyclic parents, a stale <c>nextId</c>, assets and prefabs missing from the library,
    /// prefab cycles (errors), and overrides of parts the prefab no longer has (warnings).
    /// </summary>
    public OpResult Validate(MapLibrary lib)
    {
        var errors = new List<OpError>();
        var warnings = new List<string>();
        int max = Objects.Select(o => TryIdNumber(o.Id, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        if (NextId <= max) errors.Add(new OpError("bad-file", $"nextId {NextId} is not above the highest object id (o{max}).", "validate"));
        foreach (var o in Objects)
        {
            if (o.Parent is not null && Find(o.Parent) is null) errors.Add(new OpError("unknown-id", $"'{o.Id}' has unknown parent '{o.Parent}'.", "validate", o.Id));
            else
            {
                int guard = 0;
                for (var cur = o; cur.Parent is not null && Find(cur.Parent) is { } p; cur = p)
                    if (++guard > Objects.Count) { errors.Add(new OpError("parent-cycle", $"'{o.Id}' is in a parent cycle.", "validate", o.Id)); break; }
            }
            try { o.Transform.Checked($"'{o.Id}' transform"); } catch (MapException e) { errors.Add(new OpError(e.Code, e.Message, "validate", o.Id)); }
            if (o.Asset is { IsSm1: false } a && lib.GetAsset(a.Id!) is null) errors.Add(new OpError("unknown-asset", $"'{o.Id}' uses unknown asset '{a.Id}'.", "validate", o.Id));
            if (o.Prefab is { } pid)
            {
                Prefab? p;
                try { p = lib.GetPrefab(pid); }
                catch (MapException e) { errors.Add(new OpError(e.Code, e.Message, "validate", o.Id)); continue; }
                if (p is null) { errors.Add(new OpError("unknown-prefab", $"'{o.Id}' uses unknown prefab '{pid}'.", "validate", o.Id)); continue; }
                if (PrefabGraph.FindCycle(pid, lib) is { } cyc) errors.Add(new OpError("prefab-cycle", $"Prefab '{pid}' is in a cycle: {cyc}.", "validate", o.Id));
                foreach (var ov in o.Overrides)
                    if (p.Parts.All(x => x.Id != ov.Part)) warnings.Add($"'{o.Id}' overrides part '{ov.Part}', which prefab '{pid}' no longer has.");
            }
        }
        return new OpResult(errors.Count == 0, [], warnings, errors);
    }
}
