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

/// <summary>
/// What an object or prefab part draws: an SM1 object definition (by map and name; streamed from the install) or a custom library asset.
/// <paramref name="Part"/>: one part of an SM1 definition (index into its nearest-LOD parts), for composite placements such as Manufactorum's
/// tarmacgroundplane, which holds the floor and many buildings and gates (owner's report 2026-11-05); null draws the whole definition.
/// </summary>
public sealed record AssetRef(string Source, string? Map, string? Definition, string? Id, int? Part = null)
{
    public const string Sm1Source = "sm1", CustomSource = "custom";
    public static AssetRef Sm1(string map, string definition, int? part = null) => new(Sm1Source, map, definition, null, part);
    public static AssetRef Custom(string id) => new(CustomSource, null, null, id);
    public bool IsSm1 => Source == Sm1Source;

    public string Label => IsSm1 ? (Definition ?? "") + (Part is { } p ? $" part {p}" : "") : Id ?? "";

    public JsonObject ToNode()
    {
        if (!IsSm1) return new JsonObject { ["source"] = Source, ["id"] = Id };
        var o = new JsonObject { ["source"] = Source, ["map"] = Map, ["definition"] = Definition };
        if (Part is { } p) o["part"] = p;
        return o;
    }

    public static AssetRef FromNode(JsonNode? n, string what, string code)
    {
        var o = Json.Obj(n, what, code);
        switch (Json.ReqStr(o, "source", what, code))
        {
            case Sm1Source:
                var map = Json.ReqStr(o, "map", what, code);
                var def = Json.ReqStr(o, "definition", what, code);
                if (map.Length == 0 || def.Length == 0) throw new MapException(code, $"{what}: map and definition must not be empty.");
                int? part = o["part"] is { } pn ? Json.Int(pn, what + ".part", code) : null;
                if (part < 0) throw new MapException(code, $"{what}.part must be 0 or more.");
                return Sm1(map, def, part);
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
    IReadOnlyList<PartOverride> Overrides, PieceSpec? Piece = null)
{
    public JsonObject ToNode()
    {
        var o = new JsonObject
        {
            ["id"] = Id, ["name"] = Name, ["parent"] = Parent, ["layer"] = Layer, ["visible"] = Visible, ["transform"] = Transform.ToNode(),
        };
        if (Asset is not null) o["asset"] = Asset.ToNode();
        if (Piece is not null) o["piece"] = Piece.ToNode();
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
        var piece = o["piece"] is { } pieceNode ? PieceSpec.FromNode(pieceNode, what + ".piece", "bad-file") : null;
        if (piece is not null && (asset is not null || prefab is not null)) throw new MapException("bad-file", $"{what}: a piece has no asset or prefab.");
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
            MapTransform.FromNode(o["transform"], MapTransform.Identity, what + ".transform"), asset, prefab, overrides, piece);
    }
}

/// <summary>
/// A named set of objects and smaller groups, shown in the Groups list (docs/mapmaker.md "Groups"). Editing aid only: it has no parent/child
/// transform and the map compiler ignores it. Each object or group belongs to at most one group directly, so groups form a tree.
/// Members are object ids (<c>o&lt;N&gt;</c>) and group ids (<c>g&lt;N&gt;</c>).
/// </summary>
public sealed record MapGroup(string Id, string Name, IReadOnlyList<string> Members)
{
    public JsonObject ToNode() => new() { ["id"] = Id, ["name"] = Name, ["members"] = new JsonArray(Members.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()) };

    public static bool TryIdNumber(string id, out int n)
    {
        n = 0;
        return id.Length > 1 && id[0] == 'g' && id.Skip(1).All(char.IsAsciiDigit) && id.Length <= 10 && int.TryParse(id.AsSpan(1), out n) && n >= 1 && id == "g" + n;
    }

    public static bool IsGroupId(string id) => TryIdNumber(id, out _);
}

/// <summary>
/// A <c>.smmap</c> document (formatVersion 2; version 1 files are read through <see cref="MapUpgrade"/>; the optional <c>groups</c> list needs no version step). Mutable; sessions edit a clone and swap it in when a batch succeeds.
/// </summary>
public sealed class MapDocument
{
    public const string FormatName = "smmap";
    public const int FormatVersion = 2;

    /// <summary>The format version the file declared when it was parsed (older than <see cref="FormatVersion"/>: the editor backs the file up before it writes the upgrade).</summary>
    public int LoadedVersion { get; private set; } = FormatVersion;
    /// <summary>
    /// The definitions of the prefabs the objects use, saved in the file (version 2): a map sent to another PC brings its prefabs, which otherwise live only in the maker's library
    /// (owner's pass 3: a shared map showed "unknown prefab" for every instance). Keyed by prefab id; a library prefab of the same id is never overwritten by one from a file.
    /// </summary>
    public List<Prefab> EmbeddedPrefabs { get; set; } = [];

    public string Name { get; set; } = "Untitled";
    public int NextId { get; set; } = 1;
    /// <summary>The game modes this map is made for (any combination; unknown names are kept for future modes). Written only when it is not the default.</summary>
    public List<string> Modes { get; set; } = [..DefaultModes];
    public static readonly string[] DefaultModes = ["annihilation", "seize"];
    public static readonly string[] KnownModes = ["annihilation", "seize"];
    public List<MapObject> Objects { get; } = [];
    /// <summary>Editing groups (no effect in the game); written only when there are any.</summary>
    public List<MapGroup> Groups { get; } = [];

    public MapGroup? FindGroup(string id) => Groups.FirstOrDefault(g => g.Id == id);

    public MapGroup GetGroup(string id) => FindGroup(id) ?? throw new MapException("unknown-group", $"No group '{id}'.", id);

    /// <summary>The group that directly holds this object or group, if any.</summary>
    public MapGroup? GroupOf(string memberId) => Groups.FirstOrDefault(g => g.Members.Contains(memberId));

    public string NewGroupId() => "g" + (Groups.Select(g => MapGroup.TryIdNumber(g.Id, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1);

    /// <summary>Every object in the group and its smaller groups, in member order, without repeats.</summary>
    public List<string> GroupObjects(string groupId)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        void Walk(string gid, int depth)
        {
            if (depth > 1000 || !seen.Add(gid) || FindGroup(gid) is not { } g) return;
            foreach (var m in g.Members)
                if (MapGroup.IsGroupId(m)) Walk(m, depth + 1);
                else if (!result.Contains(m)) result.Add(m);
        }
        Walk(groupId, 0);
        return result;
    }

    /// <summary>True when <paramref name="id"/> is the group itself or lies anywhere inside it.</summary>
    public bool GroupContains(string groupId, string id)
    {
        if (id == groupId) return true;
        int guard = 0;
        for (var cur = GroupOf(id); cur is not null; cur = GroupOf(cur.Id))
        {
            if (cur.Id == groupId) return true;
            if (++guard > 1000) break;
        }
        return false;
    }

    /// <summary>Takes the ids out of whatever group holds them now (used by operations that move members, and when objects are deleted).</summary>
    public void DetachFromGroups(IReadOnlyCollection<string> ids)
    {
        for (int i = 0; i < Groups.Count; i++)
            if (Groups[i].Members.Any(ids.Contains)) Groups[i] = Groups[i] with { Members = Groups[i].Members.Where(m => !ids.Contains(m)).ToList() };
    }

    /// <summary>Drops group members whose object is gone (a safety net after any batch: operations that remove objects need not know about groups).</summary>
    public void PruneGroups()
    {
        if (Groups.Count == 0) return;
        var objects = Objects.Select(o => o.Id).ToHashSet();
        var groups = Groups.Select(g => g.Id).ToHashSet();
        for (int i = 0; i < Groups.Count; i++)
            if (Groups[i].Members.Any(m => MapGroup.IsGroupId(m) ? !groups.Contains(m) : !objects.Contains(m)))
                Groups[i] = Groups[i] with { Members = Groups[i].Members.Where(m => MapGroup.IsGroupId(m) ? groups.Contains(m) : objects.Contains(m)).ToList() };
    }

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

    public JsonObject ToNode()
    {
        var o = new JsonObject { ["format"] = FormatName, ["formatVersion"] = FormatVersion, ["name"] = Name, ["nextId"] = NextId };
        if (!Modes.SequenceEqual(DefaultModes)) o["modes"] = new JsonArray(Modes.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        o["objects"] = new JsonArray(Objects.Select(x => (JsonNode)x.ToNode()).ToArray());
        if (EmbeddedPrefabs.Count > 0) o["prefabs"] = new JsonArray(EmbeddedPrefabs.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => (JsonNode)p.ToNode()).ToArray());
        if (Groups.Count > 0) o["groups"] = new JsonArray(Groups.Select(x => (JsonNode)x.ToNode()).ToArray());
        return o;
    }

    public string ToJson() => CanonicalJson.Write(ToNode());

    public MapDocument Clone()
    {
        var d = new MapDocument { Name = Name, NextId = NextId, Modes = [..Modes], EmbeddedPrefabs = [..EmbeddedPrefabs] };
        d.Objects.AddRange(Objects);
        d.Groups.AddRange(Groups);
        return d;
    }

    /// <summary>Limits on a document read from a file someone else made (shared maps): total text, objects, pieces, and the size of any position or scale.</summary>
    public const int MaxChars = 8_000_000, MaxObjects = 20_000, MaxPieces = 5_000, MaxNameLength = 200, MaxGroups = 5_000;
    public const float MaxPosition = 100_000f, MaxScale = 10_000f;

    public static MapDocument Parse(string json)
    {
        if (json.Length > MaxChars) throw new MapException("too-big", $"The map file is larger than {MaxChars / 1_000_000} MB.");
        var o = Json.Obj(Json.ParseText(json, "The map"), "The map");
        if (Json.Str(o["format"], "format", nullable: true) != FormatName) throw new MapException("bad-file", "Not a .smmap document (format must be \"smmap\").");
        if (o["formatVersion"] is null) throw new MapException("bad-file", "The map has no formatVersion.");
        int declared = Json.Int(o["formatVersion"], "formatVersion", "bad-file");
        o = MapUpgrade.ToCurrent(o, declared, FormatVersion);   // older files are upgraded in memory, the same way for everyone who reads the file
        var d = new MapDocument { Name = Json.Str(o["name"], "name") ?? "", NextId = Json.Int(o["nextId"], "nextId", "bad-file"), LoadedVersion = declared };
        if (o["prefabs"] is { } prefabNodes)
            foreach (var (pn, pi) in Json.Arr(prefabNodes, "prefabs").Select((x, i) => (x, i)))
            {
                if (pn is not JsonObject) throw new MapException("bad-file", $"prefabs[{pi}] is not an object.");
                var embedded = Prefab.Parse(pn.ToJsonString());
                if (d.EmbeddedPrefabs.Any(p => p.Id == embedded.Id)) throw new MapException("bad-file", $"Duplicate embedded prefab '{embedded.Id}'.");
                d.EmbeddedPrefabs.Add(embedded);
            }
        // A hostile file: prefabs that contain themselves (directly or through others) are refused when the file is read, so nothing downstream has to unfold them.
        if (d.EmbeddedPrefabs.Count > 5000) throw new MapException("too-big", "The map file carries more than 5000 prefab definitions.");
        var embeddedById = d.EmbeddedPrefabs.ToDictionary(p => p.Id, StringComparer.Ordinal);   // built once; the walk is iterative and shares one state across all prefabs
        if (PrefabGraph.FindCycle(d.EmbeddedPrefabs.Select(p => p.Id), id => embeddedById.GetValueOrDefault(id)) is { } cycle)
            throw new MapException("prefab-cycle", $"A prefab in the map file contains itself: {cycle}.");
        if (d.Name.Length > MaxNameLength) throw new MapException("too-big", $"The map name is longer than {MaxNameLength} characters.");
        if (o["modes"] is { } modesNode) d.Modes = Json.Arr(modesNode, "modes").Select((m, i) => Json.Str(m, $"modes[{i}]")!).Where(m => m.Length > 0).Distinct().ToList();
        var seen = new HashSet<string>();
        var objectNodes = Json.Arr(o["objects"], "objects");
        if (objectNodes.Count > MaxObjects) throw new MapException("too-big", $"The map has more than {MaxObjects} objects.");
        int pieces = 0;
        foreach (var (n, i) in objectNodes.Select((x, i) => (x, i)))
        {
            var obj = MapObject.FromNode(n, i);
            if (obj.Piece is not null && ++pieces > MaxPieces) throw new MapException("too-big", $"The map has more than {MaxPieces} pieces.");
            var tr = obj.Transform;
            if (MathF.Max(MathF.Abs(tr.Position.X), MathF.Max(MathF.Abs(tr.Position.Y), MathF.Abs(tr.Position.Z))) > MaxPosition
                || MathF.Max(MathF.Abs(tr.Scale.X), MathF.Max(MathF.Abs(tr.Scale.Y), MathF.Abs(tr.Scale.Z))) > MaxScale)
                throw new MapException("too-big", $"Object {obj.Id} is placed or sized beyond {MaxPosition:0} m / {MaxScale:0} m.", obj.Id);
            if (!seen.Add(obj.Id)) throw new MapException("bad-file", $"Duplicate object id '{obj.Id}'.", obj.Id);
            d.Objects.Add(obj);
        }
        if (o["groups"] is { } groupsNode) ParseGroups(d, Json.Arr(groupsNode, "groups"));
        return d;
    }

    /// <summary>
    /// Reads the optional groups. Groups are an editing aid, so a file that lists a member that no longer exists, or lists a member twice, is repaired
    /// (the stray entries are dropped) rather than refused; a malformed entry or a duplicate group id is refused.
    /// </summary>
    static void ParseGroups(MapDocument d, JsonArray nodes)
    {
        if (nodes.Count > MaxGroups) throw new MapException("too-big", $"The map has more than {MaxGroups} groups.");
        var raw = new List<(string Id, string Name, List<string> Members)>();
        var ids = new HashSet<string>();
        foreach (var (n, i) in nodes.Select((x, i) => (x, i)))
        {
            string what = $"groups[{i}]";
            var g = Json.Obj(n, what);
            var id = Json.ReqStr(g, "id", what);
            if (!MapGroup.IsGroupId(id)) throw new MapException("bad-file", $"{what}: '{id}' is not a group id (g<N>).");
            if (!ids.Add(id)) throw new MapException("bad-file", $"Duplicate group id '{id}'.", id);
            var name = Json.Str(g["name"], what + ".name") ?? "";
            if (name.Length > MaxNameLength) throw new MapException("too-big", $"A group name is longer than {MaxNameLength} characters.");
            raw.Add((id, name, Json.Arr(g["members"], what + ".members").Select((m, k) => Json.Str(m, $"{what}.members[{k}]")!).ToList()));
        }
        var used = new HashSet<string>();
        foreach (var (id, name, members) in raw)
        {
            var kept = new List<string>();
            foreach (var m in members)
            {
                bool exists = MapGroup.IsGroupId(m) ? ids.Contains(m) && m != id : d.Find(m) is not null;
                if (exists && used.Add(m)) kept.Add(m);
            }
            d.Groups.Add(new MapGroup(id, name, kept));
        }
        // A group inside itself (through others) cannot be drawn as a tree: that member is dropped.
        foreach (var g in d.Groups.ToList())
            foreach (var m in d.GetGroup(g.Id).Members.Where(MapGroup.IsGroupId).ToList())
                if (d.GroupContains(m, g.Id))
                    d.Groups[d.Groups.FindIndex(x => x.Id == g.Id)] = d.GetGroup(g.Id) with { Members = d.GetGroup(g.Id).Members.Where(x => x != m).ToList() };
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

