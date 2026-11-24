using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// The edit-mode and material operations (docs/mapmaker.md "Edit mode contract"). Like every operation they run in a batch: one batch is one transaction and one undo step, so a whole drag
/// committed as one <c>edit-mesh</c> is one Ctrl+Z (<see cref="MeshDrag"/>).
/// </summary>
internal sealed partial class Batch
{
    /// <summary>Images added by this batch (id -> bytes): written to the library's image folder when the batch commits.</summary>
    public SortedDictionary<string, byte[]> StagedImages { get; } = new(StringComparer.Ordinal);
    public List<string> CreatedMaterials { get; } = [];
    public List<string> CreatedImages { get; } = [];

    /// <summary>Runs the mesh and material operations; false when <paramref name="name"/> is not one of them.</summary>
    bool RunEdit(string name, JsonObject op)
    {
        switch (name)
        {
            case "edit-mesh": EditMeshOp(op); return true;
            case "set-mesh": SetMesh(op); return true;
            case "reset-mesh": foreach (var id in Ids(op)) { var o = PieceObject(id); Replace(o with { Mesh = null }); } return true;
            case "image-add": ImageAdd(op); return true;
            case "image-remove": ImageRemove(op); return true;
            case "material-add": MaterialAdd(op); return true;
            case "material-set": MaterialSet(op); return true;
            case "material-edit": MaterialEdit(op); return true;
            case "material-remove": MaterialRemove(op); return true;
            case "set-material": SetMaterial(op); return true;
            default: return false;
        }
    }

    MapObject PieceObject(string id)
    {
        var o = Doc.Get(id);
        if (o.Piece is null) throw new MapException("not-editable", $"'{o.Name}' ({id}) is not a piece; only Build and gameplay pieces have an editable shape.", id);
        return o;
    }

    /// <summary>The object with <paramref name="mesh"/>, stored as no mesh when it is the stock shape (so an edit and its reverse leave the map's hash as it was).</summary>
    static MapObject WithMesh(MapObject o, EditMesh mesh) => o with { Mesh = mesh.IsStock ? null : mesh };

    /// <summary>
    /// edit-mesh {targets: [{id, mode, ids}] (or id, mode, ids), move: [x,y,z] | rotate: degrees | scale: [x,y,z] or n, axis?: "x"|"y"|"z", pivot?: "selection"|"median"|"origin"|[x,y,z]}:
    /// one component edit applied to each target in its own axes (sized space, metres), about its own pivot. Only the listed objects change.
    /// </summary>
    void EditMeshOp(JsonObject op)
    {
        var edit = MeshEdit.FromOp(op);
        var targets = new List<(string Id, ComponentSelection Sel)>();
        if (op["targets"] is { } tn)
            foreach (var (t, i) in Json.Arr(tn, "targets", "bad-op").Select((x, i) => (x, i)))
            {
                var to = Json.Obj(t, $"targets[{i}]", "bad-op");
                targets.Add((Json.ReqStr(to, "id", $"targets[{i}]", "bad-op"), ReadSelection(to, $"targets[{i}]")));
            }
        else targets.Add((ReqId(op), ReadSelection(op, "edit-mesh")));
        if (targets.Count == 0) throw new MapException("empty-selection", "edit-mesh names no object.");
        if (targets.Select(t => t.Id).Distinct().Count() != targets.Count) throw new MapException("bad-op", "edit-mesh lists an object twice; put all its components in one target.");
        foreach (var (id, sel) in targets)
        {
            var o = PieceObject(id);
            try { Replace(WithMesh(o, MeshOps.Apply(EditMesh.Of(o), o.Transform.Scale, sel, edit))); }
            catch (MapException e) when (e.Id is null) { throw new MapException(e.Code, $"'{o.Name}' ({id}): {e.Message}", id); }
        }
    }

    static ComponentSelection ReadSelection(JsonObject o, string what)
    {
        var mode = ComponentSelection.ParseMode(Json.Str(o["mode"], what + ".mode", "bad-op"));
        var ids = Json.Arr(o["ids"], what + ".ids", "bad-op").Select((x, i) => Json.Int(x, $"{what}.ids[{i}]", "bad-op")).Distinct().ToList();
        if (ids.Count == 0) throw new MapException("empty-selection", $"{what}: no {ComponentSelection.ModeName(mode)} is selected.");
        return new ComponentSelection(mode, ids);
    }

    /// <summary>set-mesh {id, mesh: {topology, vertices, faceSlots?} | null}: sets the whole shape (null: the stock shape). For tools; the editor commits edits with edit-mesh.</summary>
    void SetMesh(JsonObject op)
    {
        var id = ReqId(op);
        var o = PieceObject(id);
        if (op["mesh"] is null) { Replace(o with { Mesh = null }); return; }
        var mesh = EditMesh.FromNode(op["mesh"], o.Piece!, "mesh", "bad-op");
        var d = MeshDims.Of(mesh, o.Transform.Scale);
        if (d.X < MeshOps.MinExtent || d.Y < MeshOps.MinExtent || d.Z < MeshOps.MinExtent) throw new MapException("bad-mesh", "The shape is flatter than 1 cm along one of its axes.", id);
        Replace(WithMesh(o, mesh));
    }

    // ---- images and materials ----

    /// <summary>image-add {path, name?}: reads a PNG or JPEG, lists it in the map by its SHA-256 and stages its bytes for the library (written when the batch commits). Returns the id in createdImages.</summary>
    void ImageAdd(JsonObject op)
    {
        var path = Json.ReqStr(op, "path", "image-add", "bad-op");
        byte[] data;
        try
        {
            if (!File.Exists(path)) throw new MapException("bad-image", $"No image file '{path}'.");
            if (new FileInfo(path).Length > MapImage.MaxBytes) throw new MapException("bad-image", $"'{Path.GetFileName(path)}' is larger than {MapImage.MaxBytes / 1_000_000} MB.");
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new MapException("bad-image", $"Cannot read '{path}': {e.Message}"); }
        var info = MapImage.Inspect(data, Path.GetFileName(path));
        if (Json.Str(op["name"], "name", "bad-op", nullable: true) is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 80) throw new MapException("bad-op", "An image name must be 1 to 80 characters.");
            info = info with { Name = name };
        }
        if (Doc.Images.FirstOrDefault(i => i.Id == info.Id) is { } have) { Warnings.Add($"The map already has this picture (as '{have.Name}')."); CreatedImages.Add(have.Id); return; }
        if (Doc.Images.Count >= MapImage.MaxImages) throw new MapException("too-big", $"A map uses at most {MapImage.MaxImages} images.");
        Doc.Images.Add(info);
        StagedImages[info.Id] = data;
        CreatedImages.Add(info.Id);
    }

    void ImageRemove(JsonObject op)
    {
        var id = ReqId(op);
        if (Doc.Images.All(i => i.Id != id)) throw new MapException("unknown-image", $"The map has no image '{id}'.", id);
        if (Doc.Materials.FirstOrDefault(m => m.ImageIds().Contains(id)) is { } user) throw new MapException("image-in-use", $"Material '{user.Name}' uses this image; take it out of the material first.", id);
        Doc.Images.RemoveAll(i => i.Id == id);
        StagedImages.Remove(id);
    }

    MaterialGraph CheckedGraph(MaterialGraph g)
    {
        if (g.Nodes.Count > MaterialGraph.MaxNodes || g.Links.Count > MaterialGraph.MaxLinks) throw new MapException("too-big", $"A material holds at most {MaterialGraph.MaxNodes} nodes and {MaterialGraph.MaxLinks} links.");
        g.Check("bad-graph");
        foreach (var img in g.ImageIds()) if (Doc.Images.All(i => i.Id != img)) throw new MapException("unknown-image", $"Material '{g.Name}' names image {img}, which the map does not have (image-add it first).");
        return g;
    }

    static string MaterialName(JsonObject op, string dflt)
    {
        var name = (Json.Str(op["name"], "name", "bad-op", nullable: true) ?? dflt).Trim();
        if (name.Length is 0 or > 80) throw new MapException("bad-op", "A material name must be 1 to 80 characters.");
        return name;
    }

    static MaterialGraph GraphFrom(string id, string name, JsonObject op) => MaterialGraph.FromNode(new JsonObject
    {
        ["id"] = id, ["name"] = name, ["nodes"] = op["nodes"]?.DeepClone() ?? new JsonArray(), ["links"] = op["links"]?.DeepClone() ?? new JsonArray(),
    }, "material", "bad-graph");

    /// <summary>material-add {name?, nodes?, links?}: a new material (default: a PBR surface into the output). Returns its id in createdMaterials.</summary>
    void MaterialAdd(JsonObject op)
    {
        if (Doc.Materials.Count >= MapDocument.MaxMaterials) throw new MapException("too-big", $"A map holds at most {MapDocument.MaxMaterials} materials.");
        var id = Doc.NewMaterialId();
        var name = MaterialName(op, "Material " + id[1..]);
        var g = op["nodes"] is null && op["links"] is null ? MaterialGraph.Default(id, name) : GraphFrom(id, name, op);
        Doc.Materials.Add(CheckedGraph(g));
        CreatedMaterials.Add(id);
    }

    MaterialGraph RequireMaterial(string id) => Doc.FindMaterial(id) ?? throw new MapException("unknown-material", $"The map has no material '{id}'.", id);

    void PutMaterial(MaterialGraph g) => Doc.Materials[Doc.Materials.FindIndex(m => m.Id == g.Id)] = CheckedGraph(g);

    /// <summary>material-set {id, name?, nodes?, links?}: replaces the name and/or the whole graph.</summary>
    void MaterialSet(JsonObject op)
    {
        var m = RequireMaterial(ReqId(op));
        var name = MaterialName(op, m.Name);
        var g = op["nodes"] is null && op["links"] is null ? m with { Name = name } : GraphFrom(m.Id, name, new JsonObject
        {
            ["nodes"] = op["nodes"]?.DeepClone() ?? new JsonArray(m.Nodes.Select(n => (JsonNode)n.ToNode()).ToArray()),
            ["links"] = op["links"]?.DeepClone() ?? new JsonArray(m.Links.Select(l => (JsonNode)l.ToNode()).ToArray()),
        });
        PutMaterial(g);
    }

    /// <summary>
    /// material-edit {id, addNodes?: [{id?, type, params?, position?}], removeNodes?: [ids], setParams?: [{node, params}], moveNodes?: [{node, position}], link?: [{from, out, to, in}],
    /// unlink?: [{to, in}]}: the node editor's steps, applied in that order (a link into an input that already has one replaces it; removing a node removes its links). One batch = one undo.
    /// </summary>
    void MaterialEdit(JsonObject op)
    {
        var m = RequireMaterial(ReqId(op));
        var nodes = m.Nodes.ToList();
        var links = m.Links.ToList();
        if (op["addNodes"] is { } an)
            foreach (var (x, i) in Json.Arr(an, "addNodes", "bad-op").Select((x, i) => (x, i)))
            {
                var no = (JsonObject)Json.Obj(x, $"addNodes[{i}]", "bad-op").DeepClone();
                if (no["id"] is null)
                {
                    var type = Json.ReqStr(no, "type", $"addNodes[{i}]", "bad-op");
                    int k = 1; while (nodes.Any(n => n.Id == $"{type}-{k}")) k++;
                    no["id"] = $"{type}-{k}";
                }
                var node = MaterialNode.FromNode(no, $"addNodes[{i}]", "bad-graph");
                if (nodes.Any(n => n.Id == node.Id)) throw new MapException("bad-graph", $"The material already has a node '{node.Id}'.");
                nodes.Add(node);
            }
        if (op["removeNodes"] is { } rn)
            foreach (var x in Json.Arr(rn, "removeNodes", "bad-op"))
            {
                var nid = Json.Str(x, "removeNodes[]", "bad-op")!;
                if (nodes.RemoveAll(n => n.Id == nid) == 0) throw new MapException("bad-graph", $"The material has no node '{nid}'.");
                links.RemoveAll(l => l.From == nid || l.To == nid);
            }
        if (op["setParams"] is { } sp)
            foreach (var (x, i) in Json.Arr(sp, "setParams", "bad-op").Select((x, i) => (x, i)))
            {
                var so = Json.Obj(x, $"setParams[{i}]", "bad-op");
                var nid = Json.ReqStr(so, "node", $"setParams[{i}]", "bad-op");
                int at = nodes.FindIndex(n => n.Id == nid);
                if (at < 0) throw new MapException("bad-graph", $"The material has no node '{nid}'.");
                var merged = (JsonObject)nodes[at].Params.DeepClone();
                foreach (var (k, v) in Json.Obj(so["params"], $"setParams[{i}].params", "bad-op")) merged[k] = v?.DeepClone();   // a null value takes the default back
                foreach (var k in merged.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) merged.Remove(k);
                nodes[at] = nodes[at] with { Params = MaterialNode.CanonicalParams(nodes[at].Type, merged, $"setParams[{i}]", "bad-graph") };
            }
        if (op["moveNodes"] is { } mn)
            foreach (var (x, i) in Json.Arr(mn, "moveNodes", "bad-op").Select((x, i) => (x, i)))
            {
                var mo = Json.Obj(x, $"moveNodes[{i}]", "bad-op");
                var nid = Json.ReqStr(mo, "node", $"moveNodes[{i}]", "bad-op");
                int at = nodes.FindIndex(n => n.Id == nid);
                if (at < 0) throw new MapException("bad-graph", $"The material has no node '{nid}'.");
                if (mo["position"] is not JsonArray { Count: 2 } pa) throw new MapException("bad-op", $"moveNodes[{i}].position must be [x, y].");
                nodes[at] = nodes[at] with { Position = new Vector2(Json.Num(pa[0], "position", "bad-op"), Json.Num(pa[1], "position", "bad-op")) };
            }
        if (op["unlink"] is { } ul)
            foreach (var (x, i) in Json.Arr(ul, "unlink", "bad-op").Select((x, i) => (x, i)))
            {
                var uo = Json.Obj(x, $"unlink[{i}]", "bad-op");
                string to = Json.ReqStr(uo, "to", $"unlink[{i}]", "bad-op"), inp = Json.ReqStr(uo, "in", $"unlink[{i}]", "bad-op");
                if (links.RemoveAll(l => l.To == to && l.In == inp) == 0) throw new MapException("bad-graph", $"Nothing is linked into {to}.{inp}.");
            }
        if (op["link"] is { } lk)
            foreach (var (x, i) in Json.Arr(lk, "link", "bad-op").Select((x, i) => (x, i)))
            {
                var l = MaterialLink.FromNode(x, $"link[{i}]", "bad-op");
                links.RemoveAll(e => e.To == l.To && e.In == l.In);   // one link per input: a new one replaces the old (as in Blender)
                links.Add(l);
            }
        PutMaterial(m with { Nodes = nodes, Links = links });
    }

    void MaterialRemove(JsonObject op)
    {
        var m = RequireMaterial(ReqId(op));
        if (Doc.Objects.FirstOrDefault(o => o.Material == m.Id) is { } user) throw new MapException("material-in-use", $"'{user.Name}' ({user.Id}) uses material '{m.Name}'; assign it another material first.", m.Id);
        Doc.Materials.RemoveAll(x => x.Id == m.Id);
    }

    /// <summary>set-material {ids, material: id | null}: assigns or replaces the material of drawn pieces (null: back to the default look).</summary>
    void SetMaterial(JsonObject op)
    {
        var mid = Json.Str(op["material"], "material", "bad-op", nullable: true);
        if (mid is not null) RequireMaterial(mid);
        foreach (var id in Ids(op))
        {
            var o = Doc.Get(id);
            if (o.Piece is not { } p || !PieceKind.IsDrawn(p.Kind))
                throw new MapException("not-renderable", $"'{o.Name}' ({id}) is not drawn in the game; only blocks, ramps and columns take a material.", id);
            Replace(o with { Material = mid });
        }
    }
}
