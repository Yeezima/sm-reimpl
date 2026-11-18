using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>An asset staged by import-asset: written to the library only when the batch commits.</summary>
internal sealed record StagedAsset(AssetInfo Info, string SourceDir, string MainFile, IReadOnlyList<string> Companions);

/// <summary>
/// One batch in flight: a working copy of the document plus pending prefabs and assets. Nothing reaches the library or the session until the
/// session commits it, so a failing operation leaves no trace (docs/mapmaker.md "one transaction").
/// </summary>
internal sealed partial class Batch(MapDocument doc, MapLibrary library, ISm1Source? sm1) : MapContext(library, sm1)
{
    public MapDocument Doc { get; } = doc;
    public SortedDictionary<string, Prefab> Prefabs { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, StagedAsset> Assets { get; } = new(StringComparer.Ordinal);
    public List<string> Created { get; } = [];
    public List<string> CreatedPrefabs { get; } = [];
    public SortedSet<string> DeletedPrefabs { get; } = new(StringComparer.Ordinal);
    public List<string> CreatedAssets { get; } = [];
    public SortedSet<string> DeletedAssets { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = [];
    public List<KeyValuePair<string, string>> Mapping { get; } = [];

    public override Prefab? GetPrefab(string id) => Prefabs.TryGetValue(id, out var p) ? p : DeletedPrefabs.Contains(id) ? null : base.GetPrefab(id);
    public override AssetInfo? GetAsset(string id) => DeletedAssets.Contains(id) ? null : Assets.TryGetValue(id, out var a) ? a.Info : base.GetAsset(id);

    record Item(string Name, AssetRef? Asset, string? Prefab, Matrix4x4 World, MapTransform Comp);

    public void Run(JsonObject op, out string name)
    {
        name = Json.ReqStr(op, "op", "operation", "bad-op");
        switch (name)
        {
            case "place": Place(op); break;
            case "set-piece": SetPiece(op); break;
            case "set-modes":
            {
                // set-modes {modes:["annihilation","seize", ...]}: the game modes this map is made for (any combination, at least one; unknown names are kept for future modes).
                var modes = Json.Arr(op["modes"], "modes", "bad-op").Select((m, i) => Json.Str(m, $"modes[{i}]", "bad-op")!).Where(m => m.Length > 0).Distinct().ToList();
                if (modes.Count == 0) throw new MapException("bad-op", "A map needs at least one game mode.");
                Doc.Modes = modes;
                break;
            }
            case "transform": Transform(op); break;
            case "duplicate": Duplicate(op); break;
            case "delete": Delete(op); break;
            case "rename": Rename(op); break;
            case "set-parent": SetParent(op); break;
            case "set-visible": SetVisible(op); break;
            case "group-to-prefab": GroupToPrefab(op); break;
            case "prefab-edit": PrefabEdit(op); break;
            case "delete-prefab": DeletePrefab(op); break;
            case "delete-asset": DeleteAsset(op); break;
            case "instance-override": InstanceOverride(op); break;
            case "import-asset": ImportAsset(op); break;
            case "import-prefab": ImportPrefab(op); break;
            case "extract-sm1": ExtractSm1(op); break;
            case "import-sm1-map": ImportSm1Map(op); break;
            default: throw new MapException("bad-op", $"Unknown operation '{name}'.");
        }
    }

    // ---- helpers ----

    string NewId() => "o" + Doc.NextId++;

    void Replace(MapObject o) => Doc.Objects[Doc.IndexOf(o.Id)] = o;

    static IReadOnlyList<string> Ids(JsonObject op, string key = "ids")
    {
        var list = new List<string>();
        if (op[key] is { } n) foreach (var x in Json.Arr(n, key, "bad-op")) list.Add(Json.Str(x, key + "[]", "bad-op")!);
        else if (op["id"] is { } one) list.Add(Json.Str(one, "id", "bad-op")!);
        if (list.Count == 0) throw new MapException("empty-selection", $"'{key}' must name at least one object.");
        return list.Distinct().ToList();
    }

    static string ReqId(JsonObject op, string key = "id") => Json.ReqStr(op, key, "operation", "bad-op");

    void CheckAsset(AssetRef a)
    {
        if (!a.IsSm1 && GetAsset(a.Id!) is null) throw new MapException("unknown-asset", $"No library asset '{a.Id}'.");
    }

    Prefab RequirePrefab(string id) => GetPrefab(id) ?? throw new MapException("unknown-prefab", $"No prefab '{id}'.");

    static Vector3 OptVec(JsonObject op, string key, Vector3 dflt) => op[key] is { } n ? Json.Vec(n, key) : dflt;

    // ---- object operations ----

    void Place(JsonObject op)
    {
        var asset = op["asset"] is { } an ? AssetRef.FromNode(an, "asset", "bad-op") : null;
        var prefab = Json.Str(op["prefab"], "prefab", "bad-op", nullable: true);
        var piece = op["piece"] is { } pieceNode ? PieceSpec.FromNode(pieceNode, "piece", "bad-op") : null;
        if ((asset is null ? 0 : 1) + (prefab is null ? 0 : 1) + (piece is null ? 0 : 1) != 1) throw new MapException("bad-op", "place needs exactly one of asset, prefab or piece.");
        if (asset is not null) CheckAsset(asset); else if (prefab is not null) RequirePrefab(prefab);
        var parent = Json.Str(op["parent"], "parent", "bad-op", nullable: true);
        if (parent is not null) Doc.Get(parent);
        var transform = MapTransform.FromNode(op["transform"], piece is null ? MapTransform.Identity : MapTransform.Identity with { Scale = piece.Info.DefaultSize });
        var name = Json.Str(op["name"], "name", "bad-op", nullable: true) ?? asset?.Label ?? prefab ?? piece!.DefaultName;
        var layer = Json.Str(op["layer"], "layer", "bad-op", nullable: true) ?? "default";
        var id = NewId();
        Doc.Objects.Add(new MapObject(id, name, parent, layer, true, transform, asset, prefab, [], piece));
        Created.Add(id);
    }

    /// <summary>set-piece {id, side}: turns a spawn area or door to the other side.</summary>
    void SetPiece(JsonObject op)
    {
        var id = ReqId(op);
        var o = Doc.Get(id);
        if (o.Piece is not { } p) throw new MapException("bad-op", $"'{id}' is not a piece.", id);
        var side = Json.ReqStr(op, "side", "set-piece", "bad-op");
        var next = PieceSpec.FromNode(new JsonObject { ["kind"] = p.Kind, ["side"] = side }, "piece", "bad-op");
        Replace(o with { Piece = next, Name = o.Name == p.DefaultName ? next.DefaultName : o.Name });
    }

    void Transform(JsonObject op)
    {
        bool set = op.ContainsKey("position") || op.ContainsKey("rotation") || op.ContainsKey("scale");
        bool delta = op.ContainsKey("move") || op.ContainsKey("rotate") || op.ContainsKey("scaleBy");
        if (set == delta) throw new MapException("bad-op", "transform needs either position/rotation/scale (set) or move/rotate/scaleBy (delta), not both.");
        foreach (var id in Ids(op))
        {
            var o = Doc.Get(id);
            var t = o.Transform;
            if (set)
                t = new MapTransform(
                    op["position"] is { } p ? Json.Vec(p, "position") : t.Position,
                    op["rotation"] is { } r ? Json.Vec(r, "rotation") : t.Rotation,
                    op["scale"] is { } s ? MapTransform.ReadScale(s, "scale") : t.Scale);
            else
                t = new MapTransform(
                    t.Position + (op["move"] is { } m ? Json.Vec(m, "move") : Vector3.Zero),
                    t.Rotation + (op["rotate"] is { } ro ? Json.Vec(ro, "rotate") : Vector3.Zero),
                    t.Scale * (op["scaleBy"] is { } sb ? MapTransform.ReadScale(sb, "scaleBy") : Vector3.One));
            try { Replace(o with { Transform = t.Checked($"'{id}' transform") }); }
            catch (MapException e) { throw new MapException(e.Code, e.Message, id); }
        }
    }

    void Duplicate(JsonObject op)
    {
        var ids = Ids(op);
        var offset = OptVec(op, "offset", Vector3.Zero);
        foreach (var id in ids) Doc.Get(id);
        foreach (var id in ids.Where(i => !ids.Any(other => other != i && Doc.IsDescendantOf(i, other))))
        {
            var src = Doc.Get(id);
            var copy = CopyTree(src, src.Parent, offset);
            Created.Add(copy);
        }
    }

    string CopyTree(MapObject src, string? parent, Vector3 offset)
    {
        var id = NewId();
        var t = src.Transform;
        Doc.Objects.Add(src with { Id = id, Parent = parent, Transform = t with { Position = t.Position + offset } });
        Mapping.Add(new(src.Id, id));
        foreach (var child in Doc.ChildrenOf(src.Id).ToList()) CopyTree(child, id, Vector3.Zero);
        return id;
    }

    void Delete(JsonObject op)
    {
        var ids = Ids(op);
        var gone = new HashSet<string>();
        foreach (var id in ids) foreach (var o in Doc.Subtree(id)) gone.Add(o.Id);
        Doc.Objects.RemoveAll(o => gone.Contains(o.Id));
    }

    void Rename(JsonObject op)
    {
        var id = ReqId(op);
        var name = Json.ReqStr(op, "name", "rename", "bad-op");
        if (name.Length == 0) throw new MapException("bad-op", "A name must not be empty.", id);
        Replace(Doc.Get(id) with { Name = name });
    }

    void SetVisible(JsonObject op)
    {
        var id = ReqId(op);
        if (op["visible"] is not JsonValue v || !v.TryGetValue<bool>(out var vis)) throw new MapException("bad-op", "visible must be true or false.", id);
        Replace(Doc.Get(id) with { Visible = vis });
    }

    void SetParent(JsonObject op)
    {
        var id = ReqId(op);
        if (!op.ContainsKey("parent")) throw new MapException("bad-op", "set-parent needs a parent (an id, or null for none).", id);
        var parent = Json.Str(op["parent"], "parent", "bad-op", nullable: true);
        var o = Doc.Get(id);
        if (parent is not null)
        {
            Doc.Get(parent);
            if (parent == id || Doc.IsDescendantOf(parent, id)) throw new MapException("parent-cycle", $"'{parent}' is inside '{id}'.", id);
        }
        if (o.Parent == parent) return;
        var world = Doc.World(id);
        var parentWorld = parent is null ? Matrix4x4.Identity : Doc.World(parent);
        if (!Matrix4x4.Invert(parentWorld, out var inverse)) throw new MapException("bad-transform", $"'{parent}' has a transform that cannot be inverted.", id);
        try { Replace(o with { Parent = parent, Transform = TransformMath.Decompose(world * inverse) }); }
        catch (MapException e) when (e.Id is null) { throw new MapException(e.Code, e.Message, id); }
    }

    // ---- prefabs ----

    void GroupToPrefab(JsonObject op)
    {
        var ids = Ids(op);
        foreach (var id in ids) Doc.Get(id);
        var roots = ids.Where(i => !ids.Any(other => other != i && Doc.IsDescendantOf(i, other))).ToList();
        var items = new List<Item>();
        foreach (var root in roots)
            foreach (var n in Doc.Subtree(root))
            {
                if (n.Asset is null && n.Prefab is null) continue;
                if (n.Prefab is not null && n.Overrides.Count > 0)
                {
                    // An instance with part overrides is unfolded into its parts, as drawn, so moved parts stay put and hidden parts stay hidden (left out).
                    foreach (var rp in MapResolver.Flatten(Doc, this).Where(x => x.ObjectId == n.Id && x.Visible))
                    {
                        MapTransform pc;
                        try { pc = TransformMath.Decompose(rp.World); }
                        catch (MapException e) { throw new MapException(e.Code, e.Message, n.Id); }
                        items.Add(new Item(rp.Name, rp.Asset, null, rp.World, pc));
                    }
                    Warnings.Add($"'{n.Id}' is a prefab instance with overrides; it was unfolded into its parts so they stay where they are.");
                    continue;
                }
                MapTransform comp;
                try { comp = n.Parent is null ? n.Transform : TransformMath.Decompose(Doc.World(n.Id)); }
                catch (MapException e) { throw new MapException(e.Code, e.Message, n.Id); }
                items.Add(new Item(n.Name, n.Asset, n.Prefab, Doc.World(n.Id), comp));
            }
        if (items.Count == 0) throw new MapException("empty-selection", "The selection holds no asset or prefab instance to group.");
        var prefabId = Json.ReqStr(op, "prefab", "group-to-prefab", "bad-op");
        var prefab = BuildPrefab(prefabId, Json.Str(op["name"], "name", "bad-op", nullable: true), op["tags"], Json.Str(op["collision"], "collision", "bad-op", nullable: true),
            op["origin"], items, Provenance(items, null, null));
        Prefabs[prefab.Id] = prefab;
        CreatedPrefabs.Add(prefab.Id);
        if (op["replace"] is { } rn && Json.Bool(rn, "replace", "bad-op", false))
        {
            var first = Doc.Get(roots[0]);
            var gone = new HashSet<string>();
            foreach (var r in roots) foreach (var n in Doc.Subtree(r)) gone.Add(n.Id);
            Doc.Objects.RemoveAll(o => gone.Contains(o.Id));
            var id = NewId();
            Doc.Objects.Add(new MapObject(id, prefab.Name, null, first.Layer, true, MapTransform.Identity with { Position = prefab.Origin }, null, prefab.Id, []));
            Created.Add(id);
        }
    }

    static PrefabProvenance Provenance(List<Item> items, string? map, IReadOnlyList<string>? objects)
    {
        bool sm1 = items.Any(i => i.Asset is { IsSm1: true });
        bool custom = items.Any(i => i.Prefab is not null || i.Asset is { IsSm1: false });
        var maps = items.Where(i => i.Asset is { IsSm1: true }).Select(i => i.Asset!.Map!).Distinct().ToList();
        return new PrefabProvenance(sm1 && custom ? "mixed" : sm1 ? "sm1" : "custom", map ?? (maps.Count == 1 ? maps[0] : null),
            objects ?? items.Select(i => i.Name).ToList());
    }

    /// <summary>A new prefab from placed pieces whose world components are given; part positions become relative to the chosen origin.</summary>
    Prefab BuildPrefab(string id, string? name, JsonNode? tagsNode, string? collision, JsonNode? originSpec, List<Item> items, PrefabProvenance provenance)
    {
        if (!Json.IsSlug(id)) throw new MapException("bad-id", $"'{id}' is not a valid prefab id (lowercase letters, digits, '-' and '_').");
        if (GetPrefab(id) is not null) throw new MapException("duplicate-id", $"Prefab '{id}' already exists (use prefab-edit).");
        collision ??= "mesh";
        if (!Prefab.Collisions.Contains(collision)) throw new MapException("bad-op", $"collision must be one of: {string.Join(", ", Prefab.Collisions)}.");
        var tags = tagsNode is null ? [] : Json.Arr(tagsNode, "tags", "bad-op").Select(t => Json.Str(t, "tags[]", "bad-op")!).ToList();
        foreach (var item in items)
        {
            if (item.Asset is not null) CheckAsset(item.Asset); else RequirePrefab(item.Prefab!);
        }
        Bounds? all = null;
        bool known = true;
        foreach (var item in items)
        {
            var local = item.Asset is not null ? AssetBounds(item.Asset) : PrefabBounds(item.Prefab!);
            if (local is not { } lb) { known = false; continue; }
            var b = lb.Transformed(item.World);
            all = all is { } a ? a.Union(b) : b;
        }
        var origin = ResolveOrigin(originSpec, "bounds-bottom-center", known ? all : null, items[0].Comp.Position);
        var parts = items.Select((it, i) => new PrefabPart($"p{i + 1}", it.Name, it.Asset, it.Prefab,
            it.Comp with { Position = TransformMath.Round(it.Comp.Position - origin) })).ToList();
        return new Prefab(id, string.IsNullOrWhiteSpace(name) ? id : name, tags, origin, collision, parts.Count + 1, parts, provenance, PartsBounds(parts));
    }

    Vector3 ResolveOrigin(JsonNode? spec, string dflt, Bounds? worldBounds, Vector3 first)
    {
        string? word = spec is null ? dflt : spec is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        Vector3 result;
        if (word is null) result = Json.Vec(spec, "origin", "bad-origin");
        else if (word == "first") result = first;
        else if (word == "bounds-bottom-center")
        {
            if (worldBounds is { } b) result = new Vector3(b.Center.X, b.Center.Y, b.Min.Z);
            else
            {
                Warnings.Add("The bounds of some parts are unknown (no install or no mesh data), so the origin is the first part's position.");
                result = first;
            }
        }
        else throw new MapException("bad-origin", "origin must be \"bounds-bottom-center\", \"first\" or [x, y, z].");
        return TransformMath.Round(result);
    }

    /// <summary>Removes a prefab from the library. Refused while an object of this map or another prefab still uses it (delete those first, in the
    /// same batch if wanted); maps that are not open are not checked and show the placement as an unknown prefab.</summary>
    void DeletePrefab(JsonObject op)
    {
        var id = Json.ReqStr(op, "prefab", "delete-prefab", "bad-op");
        RequirePrefab(id);
        var users = Doc.Objects.Where(o => o.Prefab == id).Select(o => o.Id).ToList();
        if (users.Count > 0)
            throw new MapException("prefab-in-use", $"Prefab '{id}' is placed {users.Count} time(s) in this map ({string.Join(", ", users.Take(5))}{(users.Count > 5 ? ", ..." : "")}); delete those first.", users[0]);
        var all = Library.ListPrefabs().Select(p => p.Id).Concat(Prefabs.Keys).Distinct().Where(x => x != id);
        foreach (var other in all)
            if (GetPrefab(other) is { } p && p.Parts.Any(part => part.Prefab == id))
                throw new MapException("prefab-in-use", $"Prefab '{id}' is a part of prefab '{other}'; remove it there first.");
        Prefabs.Remove(id);
        CreatedPrefabs.Remove(id);
        DeletedPrefabs.Add(id);
    }

    /// <summary>Removes a custom asset from the library (its folder goes to the library trash; undo brings it back). Refused while an object of this map or a prefab still uses it.</summary>
    void DeleteAsset(JsonObject op)
    {
        var id = Json.ReqStr(op, "asset", "delete-asset", "bad-op");
        if (GetAsset(id) is null) throw new MapException("unknown-asset", $"No custom asset '{id}'.");
        bool Uses(AssetRef? a) => a is { IsSm1: false } && a.Id == id;
        var users = Doc.Objects.Where(o => Uses(o.Asset)).Select(o => o.Id).ToList();
        if (users.Count > 0)
            throw new MapException("asset-in-use", $"Asset '{id}' is placed {users.Count} time(s) in this map ({string.Join(", ", users.Take(5))}{(users.Count > 5 ? ", ..." : "")}); delete those first.", users[0]);
        var all = Library.ListPrefabs().Select(p => p.Id).Concat(Prefabs.Keys).Distinct();
        foreach (var other in all)
            if (GetPrefab(other) is { } p && p.Parts.Any(part => Uses(part.Asset)))
                throw new MapException("asset-in-use", $"Asset '{id}' is a part of prefab '{other}'; remove it there first.");
        DeletedAssets.Add(id);
        CreatedAssets.Remove(id);
    }

    void PrefabEdit(JsonObject op)
    {
        var id = Json.ReqStr(op, "prefab", "prefab-edit", "bad-op");
        var prefab = RequirePrefab(id);
        var parts = prefab.Parts.ToList();
        var removed = new List<string>();
        if (op["removeParts"] is { } rn)
            foreach (var x in Json.Arr(rn, "removeParts", "bad-op"))
            {
                var pid = Json.Str(x, "removeParts[]", "bad-op")!;
                if (parts.RemoveAll(p => p.Id == pid) == 0) throw new MapException("unknown-part", $"Prefab '{id}' has no part '{pid}'.");
                removed.Add(pid);
            }
        if (op["setPart"] is { } sn)
            foreach (var x in sn is JsonArray ? Json.Arr(sn, "setPart", "bad-op") : [sn])
            {
                var o = Json.Obj(x, "setPart[]", "bad-op");
                var pid = Json.ReqStr(o, "id", "setPart", "bad-op");
                int at = parts.FindIndex(p => p.Id == pid);
                if (at < 0) throw new MapException("unknown-part", $"Prefab '{id}' has no part '{pid}'.");
                var p = parts[at];
                var asset = o["asset"] is { } an ? AssetRef.FromNode(an, "setPart.asset", "bad-op") : null;
                var pref = Json.Str(o["prefab"], "setPart.prefab", "bad-op", nullable: true);
                if (asset is not null && pref is not null) throw new MapException("bad-op", "setPart: asset and prefab together.");
                if (asset is not null) { CheckAsset(asset); p = p with { Asset = asset, Prefab = null }; }
                if (pref is not null) { RequirePrefab(pref); p = p with { Asset = null, Prefab = pref }; }
                if (Json.Str(o["name"], "setPart.name", "bad-op", nullable: true) is { } pn) p = p with { Name = pn };
                p = p with { Transform = MapTransform.FromNode(o["transform"], p.Transform, "setPart.transform") };
                parts[at] = p;
            }
        int next = prefab.NextPart;
        if (op["addParts"] is { } an2)
            foreach (var x in Json.Arr(an2, "addParts", "bad-op"))
            {
                var o = Json.Obj(x, "addParts[]", "bad-op");
                var asset = o["asset"] is { } a ? AssetRef.FromNode(a, "addParts.asset", "bad-op") : null;
                var pref = Json.Str(o["prefab"], "addParts.prefab", "bad-op", nullable: true);
                if ((asset is null) == (pref is null)) throw new MapException("bad-op", "addParts: exactly one of asset or prefab per part.");
                if (asset is not null) CheckAsset(asset); else RequirePrefab(pref!);
                parts.Add(new PrefabPart($"p{next++}", Json.Str(o["name"], "addParts.name", "bad-op", nullable: true) ?? asset?.Label ?? pref!, asset, pref,
                    MapTransform.FromNode(o["transform"], MapTransform.Identity, "addParts.transform")));
            }
        var origin = prefab.Origin;
        if (op["origin"] is { } on)
        {
            Vector3 shift;
            if (on is JsonValue ov && ov.TryGetValue<string>(out var word))
            {
                if (parts.Count == 0) throw new MapException("empty-selection", "The prefab has no parts to take an origin from.");
                if (word == "first") shift = parts[0].Transform.Position;
                else if (word == "bounds-bottom-center")
                {
                    if (PartsBounds(parts) is { } b) shift = new Vector3(b.Center.X, b.Center.Y, b.Min.Z);
                    else { Warnings.Add("The bounds of some parts are unknown, so the origin was not moved."); shift = Vector3.Zero; }
                }
                else throw new MapException("bad-origin", "origin must be \"bounds-bottom-center\", \"first\" or [x, y, z].");
            }
            else shift = Json.Vec(on, "origin", "bad-origin");
            shift = TransformMath.Round(shift);
            if (shift != Vector3.Zero)
            {
                for (int i = 0; i < parts.Count; i++)
                    parts[i] = parts[i] with { Transform = parts[i].Transform with { Position = TransformMath.Round(parts[i].Transform.Position - shift) } };
                origin += shift;
            }
        }
        var tags = op["tags"] is { } tn ? Json.Arr(tn, "tags", "bad-op").Select(t => Json.Str(t, "tags[]", "bad-op")!).ToList() : prefab.Tags;
        var collision = Json.Str(op["collision"], "collision", "bad-op", nullable: true) ?? prefab.Collision;
        if (!Prefab.Collisions.Contains(collision)) throw new MapException("bad-op", $"collision must be one of: {string.Join(", ", Prefab.Collisions)}.");
        var updated = prefab with
        {
            Name = Json.Str(op["name"], "name", "bad-op", nullable: true) ?? prefab.Name, Tags = tags, Origin = origin, Collision = collision, NextPart = next, Parts = parts,
        };
        updated = updated with { Bounds = PartsBounds(parts) };
        Prefabs[id] = updated;
        if (PrefabGraph.FindCycle(id, GetPrefab) is { } cyc) throw new MapException("prefab-cycle", $"Prefab '{id}' would contain itself: {cyc}.");
        // Overrides survive while their part exists; the ones of removed parts go, with a warning.
        foreach (var inst in Doc.Objects.Where(o => o.Prefab == id && o.Overrides.Count > 0).ToList())
        {
            var keep = inst.Overrides.Where(ov => parts.Any(p => p.Id == ov.Part)).ToList();
            if (keep.Count == inst.Overrides.Count) continue;
            foreach (var dropped in inst.Overrides.Where(ov => keep.All(k => k.Part != ov.Part)))
                Warnings.Add($"Dropped the override of removed part '{dropped.Part}' on '{inst.Id}'.");
            Replace(inst with { Overrides = keep });
        }
        if (removed.Count == 0 && Prefabs[id].ToJson() == prefab.ToJson()) Warnings.Add($"prefab-edit changed nothing in '{id}'.");
    }

    void InstanceOverride(JsonObject op)
    {
        var id = ReqId(op);
        var o = Doc.Get(id);
        if (o.Prefab is null) throw new MapException("not-a-prefab-instance", $"'{id}' is not a prefab instance.", id);
        var prefab = RequirePrefab(o.Prefab);
        var partId = Json.ReqStr(op, "part", "instance-override", "bad-op");
        var part = prefab.Parts.FirstOrDefault(p => p.Id == partId) ?? throw new MapException("unknown-part", $"Prefab '{prefab.Id}' has no part '{partId}'.", id);
        var list = o.Overrides.ToList();
        int at = list.FindIndex(x => x.Part == partId);
        var current = at >= 0 ? list[at] : new PartOverride(partId, null, null);
        if (op["clear"] is { } cn && Json.Bool(cn, "clear", "bad-op", false))
        {
            if (at >= 0) list.RemoveAt(at);
        }
        else
        {
            if (!op.ContainsKey("visible") && !op.ContainsKey("transform")) throw new MapException("bad-op", "instance-override needs visible, transform or clear.", id);
            bool? vis = current.Visible;
            if (op.ContainsKey("visible")) vis = op["visible"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new MapException("bad-op", "visible must be true or false.", id);
            var tr = current.Transform;
            if (op["transform"] is { } tn) tr = MapTransform.FromNode(tn, current.Transform ?? part.Transform, "transform");
            var next = new PartOverride(partId, vis, tr);
            if (at >= 0) list[at] = next; else list.Add(next);
        }
        Replace(o with { Overrides = list });
    }

    // ---- library ----

    void ImportAsset(JsonObject op)
    {
        var path = Json.ReqStr(op, "path", "import-asset", "bad-op");
        var info = GltfReader.Inspect(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var id = Json.Str(op["id"], "id", "bad-op", nullable: true) ?? Json.Slug(stem);
        if (id.Length == 0) id = "asset";
        if (!Json.IsSlug(id)) throw new MapException("bad-id", $"'{id}' is not a valid asset id (lowercase letters, digits, '-' and '_').");
        var sha = HashOf(path, info);
        if (GetAsset(id) is { } existing)
        {
            if (existing.Sha256 == sha) { Warnings.Add($"Asset '{id}' is already in the library with the same content."); return; }
            throw new MapException("duplicate-id", $"Asset '{id}' already exists with different content.");
        }
        // The editor plugin's converter measures the loaded meshes and passes the box (glTF frame, Y up); it wins over the accessors' min/max.
        var bounds = info.Bounds;
        if (op["bounds"] is { } bn)
        {
            var given = Bounds.FromNode(bn, "bounds");
            if (!IsFinite(given.Min) || !IsFinite(given.Max) || given.Min.X > given.Max.X || given.Min.Y > given.Max.Y || given.Min.Z > given.Max.Z)
                throw new MapException("bad-op", "import-asset: bounds must be finite with min <= max.");
            bounds = given;
        }
        var from = Json.Str(op["from"], "from", "bad-op", nullable: true);
        if (from is { Length: > 200 }) from = from[..200];
        var meta = op["import"] is JsonObject io ? (JsonObject)io.DeepClone() : null;
        var asset = new AssetInfo(id, Json.Str(op["name"], "name", "bad-op", nullable: true) ?? stem, info.FileName, bounds, sha, from ?? Path.GetFileName(path)) { Import = meta };
        Assets[id] = new StagedAsset(asset, Path.GetDirectoryName(Path.GetFullPath(path))!, info.FileName, info.Companions);
        CreatedAssets.Add(id);
        if (bounds is null) Warnings.Add($"Asset '{id}': no POSITION min/max in the glTF, so its bounds are unknown (the editor measures the loaded mesh instead).");
    }

    /// <summary>SHA-256 of the main file followed by each companion (name and bytes, sorted), so a changed buffer or image is different content.</summary>
    internal static string HashOf(string path, GltfInfo info)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(File.ReadAllBytes(path));
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var rel in info.Companions.Order(StringComparer.Ordinal))
        {
            h.AppendData(Encoding.UTF8.GetBytes("\n" + rel + "\n"));
            h.AppendData(File.ReadAllBytes(Path.Combine(dir, rel)));
        }
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    void ImportPrefab(JsonObject op)
    {
        var path = Json.ReqStr(op, "path", "import-prefab", "bad-op");
        if (!File.Exists(path)) throw new MapException("bad-file", $"No file '{path}'.");
        var text = File.ReadAllText(path);
        var prefab = Prefab.Parse(text);
        foreach (var part in prefab.Parts)
        {
            if (part.Asset is not null) CheckAsset(part.Asset);
            else if (part.Prefab != prefab.Id) RequirePrefab(part.Prefab!);
        }
        if (GetPrefab(prefab.Id) is { } existing)
        {
            if (existing.ToJson() == prefab.ToJson()) { Warnings.Add($"Prefab '{prefab.Id}' is already in the library with the same content."); return; }
            throw new MapException("duplicate-id", $"Prefab '{prefab.Id}' already exists with different content.");
        }
        Prefabs[prefab.Id] = prefab;
        if (PrefabGraph.FindCycle(prefab.Id, GetPrefab) is { } cyc) throw new MapException("prefab-cycle", $"Prefab '{prefab.Id}' contains itself: {cyc}.");
        CreatedPrefabs.Add(prefab.Id);
    }

    void ExtractSm1(JsonObject op)
    {
        var map = Json.ReqStr(op, "map", "extract-sm1", "bad-op");
        var picks = Json.Arr(op["objects"], "objects", "bad-op").ToList();
        if (picks.Count == 0) throw new MapException("empty-selection", "extract-sm1 needs at least one object.");
        var prefabId = Json.ReqStr(op, "prefab", "extract-sm1", "bad-op");
        var source = Sm1 ?? throw new MapException("missing-install", "The Space Marine install was not found, so SM1 objects cannot be read. Set SM_GAME_DIR or pass --game-dir.");
        var scene = source.LoadMap(map);
        var items = new List<Item>();
        var picked = new List<(int Index, int Part)>();
        void Take(int index, int part)
        {
            if (picked.Contains((index, part)) || (part >= 0 && picked.Contains((index, -1)))) return;
            if (part < 0)
                // The whole placement replaces parts of it taken earlier (review 2026-11-05: they would be drawn twice).
                for (int k = picked.Count - 1; k >= 0; k--)
                    if (picked[k].Index == index) { picked.RemoveAt(k); items.RemoveAt(k); }
            var m = scene.Objects[index];
            var sdef = scene.Definitions.GetValueOrDefault(m.Definition);
            if (part >= 0)
            {
                int count = sdef?.Parts?.Count ?? 0;
                if (part >= count) throw new MapException("unknown-sm1-object", $"Placed object '{m.Name}' (index {index}) has no part {part} ({count} parts).");
                if (sdef!.Parts![part] is null) throw new MapException("unknown-sm1-object", $"Part {part} of placed object '{m.Name}' (index {index}) has no mesh data to draw.");
            }
            MapTransform comp;
            try { comp = TransformMath.Decompose(m.Transform); }
            catch (MapException e) { throw new MapException(e.Code, $"Placed object '{m.Name}' (index {index}): {e.Message}"); }
            picked.Add((index, part));
            string name = m.Name.Length > 0 ? m.Name : m.Definition;
            string? partName = part >= 0 && sdef?.PartNames is { } pn && part < pn.Count && pn[part].Length > 0 ? pn[part] : null;
            items.Add(new Item(part >= 0 ? partName ?? $"{name} part {part}" : name, AssetRef.Sm1(map, m.Definition, part >= 0 ? part : null), null, m.Transform, comp));
        }
        foreach (var pick in picks)
        {
            if (pick is JsonObject exact)
            {
                int index = exact["index"] is { } inode ? Json.Int(inode, "objects[].index", "bad-op") : throw new MapException("bad-op", "An objects entry must be a name or { \"index\": N }.");
                if (index < 0 || index >= scene.Objects.Count) throw new MapException("unknown-sm1-object", $"Map '{map}' has no placed object with index {index} (0..{scene.Objects.Count - 1}).");
                int part = exact["part"] is { } pnode ? Json.Int(pnode, "objects[].part", "bad-op") : -1;
                if (exact["part"] is not null && part < 0) throw new MapException("bad-op", "objects[].part must be 0 or more.");
                Take(index, part);
                continue;
            }
            var name = Json.Str(pick, "objects[]", "bad-op")!;
            var matches = Enumerable.Range(0, scene.Objects.Count).Where(i => string.Equals(scene.Objects[i].Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new MapException("unknown-sm1-object", $"Map '{map}' has no placed object named '{name}'.");
            if (matches.Count > 1) Warnings.Add($"Name '{name}' matches {matches.Count} placed objects; all were taken (use {{ \"index\": N }} to pick one).");
            foreach (var i in matches) Take(i, -1);
        }
        var prefab = BuildPrefab(prefabId, Json.Str(op["name"], "name", "bad-op", nullable: true), op["tags"], Json.Str(op["collision"], "collision", "bad-op", nullable: true),
            op["origin"], items, new PrefabProvenance("sm1", map, picked.Select(p => scene.Objects[p.Index].Name).ToList())
            { Indices = picked.Select(p => p.Index).ToList(), Parts = picked.Select(p => p.Part).ToList() });
        Prefabs[prefab.Id] = prefab;
        CreatedPrefabs.Add(prefab.Id);
    }
}
