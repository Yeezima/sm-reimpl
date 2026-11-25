using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SmMapmaker;

namespace SmTool;

/// <summary>
/// <c>smtool map</c> commands of the edit-mode contract (docs/mapmaker.md "Edit mode contract"): component edits, dimensions, the mesh and its ids, materials and images, packs, a scripted
/// session with undo and redo, and the point-in-shape test of the compiled volumes. Same JSON and exit codes as the other map commands.
/// </summary>
public static partial class MapCommand
{
    static bool TryEditCommand(string cmd, List<string> pos, Dictionary<string, string> opts, MapLibrary library, TextWriter o, TextReader stdin, out int code)
    {
        string Pos(int i, string what) => pos.Count > i ? pos[i] : throw new ArgException($"Missing {what}. {EditUsage}");
        code = 0;
        switch (cmd)
        {
            case "edit-mesh":
            case "edit-face":
            {
                // edit-mesh <file> <id> (--face +x | --mode vertex|edge|face --ids 1,2) (--move x,y,z | --rotate deg | --scale f|x,y,z) [--axis x|y|z] [--pivot selection|median|origin]
                var file = Pos(0, "<file>");
                var id = Pos(1, "<object id>");
                bool dry = opts.ContainsKey("dry-run");
                // a dry run only reads the file (no upgrade rewrite, no image copy into the library)
                var session = dry ? new MapSession(ReadDoc(file), library, null, Path.GetFullPath(file)) : MapSession.Load(file, library);
                var obj = session.Document.Get(id);
                var op = new JsonObject { ["op"] = "edit-mesh", ["id"] = id };
                if (opts.GetValueOrDefault("face") is { } faceName)
                {
                    var topo = EditMesh.Of(obj).Topology;
                    int face = topo.FaceNamed(faceName);
                    if (face < 0) throw new ArgException($"'{obj.Name}' has no face '{faceName}' (faces: {string.Join(", ", topo.FaceNames.Select((n, i) => $"{i} {n}"))}).");
                    op["mode"] = "face"; op["ids"] = new JsonArray(face);
                }
                else
                {
                    op["mode"] = opts.GetValueOrDefault("mode") ?? (cmd == "edit-face" ? "face" : throw new ArgException("--mode vertex|edge|face (or --face <name>) is needed."));
                    op["ids"] = new JsonArray((opts.GetValueOrDefault("ids") ?? throw new ArgException("--ids 1,2,... is needed.")).Split(',').Select(s => (JsonNode)JsonValue.Create(ParseInt(s))).ToArray());
                }
                if (opts.GetValueOrDefault("move") is { } mv) op["move"] = Json.Vec(ParseVec(mv));
                if (opts.GetValueOrDefault("scale") is { } sc) op["scale"] = sc.Contains(',') ? Json.Vec(ParseVec(sc)) : JsonValue.Create(ParseFloat(sc));
                if (opts.GetValueOrDefault("rotate") is { } ro) op["rotate"] = ParseFloat(ro);
                if (opts.GetValueOrDefault("axis") is { } ax) op["axis"] = ax;
                if (opts.GetValueOrDefault("pivot") is { } pv) op["pivot"] = pv.Contains(',') ? Json.Vec(ParseVec(pv)) : JsonValue.Create(pv);
                if (dry)
                {
                    // the would-be shape's dimensions, from the same pure edit the operation runs
                    var preview = MeshOps.Apply(EditMesh.Of(obj), obj.Transform.Scale, new ComponentSelection(ComponentSelection.ParseMode((string?)op["mode"]), op["ids"]!.AsArray().Select(x => (int)x!).ToList()), MeshEdit.FromOp(op));
                    var dn = session.Apply(op, true).ToNode();
                    if ((bool)dn["ok"]!) dn["dims"] = DimsNode(obj with { Mesh = preview.IsStock ? null : preview });
                    o.Write(CanonicalJson.Write(dn));
                    code = (bool)dn["ok"]! ? 0 : 1;
                    return true;
                }
                var result = session.Apply(op);
                if (result.Ok) session.Save();
                var node = result.ToNode();
                if (result.Ok) node["dims"] = DimsNode(session.Document.Get(id));
                o.Write(CanonicalJson.Write(node));
                code = result.Ok ? 0 : 1;
                return true;
            }
            case "dims":
            {
                // dims <file> [ids...]: the dimensions of pieces (all pieces when no id is given)
                var doc = ReadDoc(Pos(0, "<file>"));
                var ids = pos.Skip(1).ToList();
                var list = (ids.Count == 0 ? doc.Objects.Where(x => x.Piece is not null) : ids.Select(doc.Get)).Select(x => (JsonNode)DimsNode(x)).ToArray();
                o.Write(CanonicalJson.Write(new JsonObject { ["ok"] = true, ["dims"] = new JsonArray(list) }));
                return true;
            }
            case "mesh":
            {
                // mesh <file> <id>: the piece's shape with its ids (vertices unit and sized, edges, faces with names)
                var doc = ReadDoc(Pos(0, "<file>"));
                var obj = doc.Get(Pos(1, "<object id>"));
                var mesh = EditMesh.Of(obj);
                var s = obj.Transform.Scale;
                var root = new JsonObject
                {
                    ["ok"] = true, ["id"] = obj.Id, ["name"] = obj.Name, ["topology"] = mesh.Topology.Name, ["edited"] = obj.Mesh is not null,
                    ["vertices"] = new JsonArray(mesh.Vertices.Select((v, i) => (JsonNode)new JsonObject { ["id"] = i, ["unit"] = Json.Vec(v), ["sized"] = Json.Vec(v * s) }).ToArray()),
                    ["edges"] = new JsonArray(mesh.Edges.Select((e, i) => (JsonNode)new JsonObject { ["id"] = i, ["a"] = e.A, ["b"] = e.B }).ToArray()),
                    ["faces"] = new JsonArray(mesh.Faces.Select((f, i) => (JsonNode)new JsonObject { ["id"] = i, ["name"] = mesh.Topology.FaceNames[i], ["vertices"] = new JsonArray(f.Select(x => (JsonNode)JsonValue.Create(x)).ToArray()) }).ToArray()),
                    ["dims"] = DimsNode(obj),
                };
                o.Write(CanonicalJson.Write(root));
                return true;
            }
            case "script":
            {
                // script <file> <steps.json|-> [--save]: one session: [{"apply": op | [ops]}, {"undo": true}, {"redo": true}, {"dims": id}, {"save": true}, {"reopen": true}]
                var file = Pos(0, "<file>");
                var stepsPath = Pos(1, "<steps.json|->");
                string text = stepsPath == "-" ? stdin.ReadToEnd() : File.Exists(stepsPath) ? File.ReadAllText(stepsPath) : throw new ArgException($"No steps file '{stepsPath}'.");
                var steps = Json.Arr(Json.ParseText(text, "The steps"), "The steps", "bad-op");
                var session = MapSession.Load(file, library);
                var outs = new JsonArray();
                bool ok = true;
                foreach (var (stepNode, i) in steps.Select((x, i) => (x, i)))
                {
                    var step = Json.Obj(stepNode, $"steps[{i}]", "bad-op");
                    JsonObject r;
                    if (step["apply"] is { } ap) r = session.Apply(ap.ToJsonString()).ToNode();
                    else if (step.ContainsKey("undo")) r = session.Undo().ToNode();
                    else if (step.ContainsKey("redo")) r = session.Redo().ToNode();
                    else if (step["dims"] is { } dn) r = new JsonObject { ["ok"] = true, ["dims"] = DimsNode(session.Document.Get(Json.Str(dn, "dims", "bad-op")!)) };
                    else if (step.ContainsKey("save")) { session.Save(); r = new JsonObject { ["ok"] = true, ["saved"] = true }; }
                    else if (step.ContainsKey("reopen")) { session = MapSession.Load(session.Path ?? file, library); r = new JsonObject { ["ok"] = true, ["reopened"] = true }; }
                    else throw new ArgException($"steps[{i}] is not apply, undo, redo, dims, save or reopen.");
                    r["step"] = i;
                    r["undoDepth"] = session.UndoDepth; r["redoDepth"] = session.RedoDepth;
                    outs.Add(r);
                    if (r["ok"] is JsonValue okv && !okv.GetValue<bool>()) { ok = false; break; }
                }
                if (ok && opts.ContainsKey("save")) session.Save();
                o.Write(CanonicalJson.Write(new JsonObject { ["ok"] = ok, ["steps"] = outs }));
                code = ok ? 0 : 1;
                return true;
            }
            case "material":
            {
                // material list|add|image|assign|resolve|remove <file> ...
                var sub = Pos(0, "list|add|image|assign|resolve|remove");
                var file = Pos(1, "<file>");
                switch (sub)
                {
                    case "list":   // read-only: the file is only parsed
                    {
                        var doc = ReadDoc(file);
                        o.Write(CanonicalJson.Write(new JsonObject
                        {
                            ["ok"] = true,
                            ["materials"] = new JsonArray(doc.Materials.Select(m => (JsonNode)m.ToNode()).ToArray()),
                            ["images"] = new JsonArray(doc.Images.Select(i => (JsonNode)i.ToNode()).ToArray()),
                            ["missingImages"] = new JsonArray(MapImages.Missing(doc, file, library).Select(i => (JsonNode)JsonValue.Create(i.Id)).ToArray()),
                        }));
                        return true;
                    }
                    case "resolve":
                    {
                        var m = ReadDoc(file).FindMaterial(Pos(2, "<material id>")) ?? throw new MapException("unknown-material", "No such material.");
                        o.Write(CanonicalJson.Write(new JsonObject { ["ok"] = true, ["material"] = PbrNode(m.Resolve()) }));
                        return true;
                    }
                }
                var session = MapSession.Load(file, library);
                JsonObject? op = null;
                switch (sub)
                {
                    case "add": op = new JsonObject { ["op"] = "material-add" }; if (opts.GetValueOrDefault("name") is { } n) op["name"] = n; break;
                    case "image": op = new JsonObject { ["op"] = "image-add", ["path"] = Pos(2, "<image file>") }; if (opts.GetValueOrDefault("name") is { } inm) op["name"] = inm; break;
                    case "assign":
                        op = new JsonObject { ["op"] = "set-material", ["material"] = Pos(2, "<material id|none>") is "none" ? null : pos[2], ["ids"] = new JsonArray(pos.Skip(3).Select(x => (JsonNode)JsonValue.Create(x)).ToArray()) };
                        break;
                    case "remove": op = new JsonObject { ["op"] = "material-remove", ["id"] = Pos(2, "<material id>") }; break;
                    default: throw new ArgException($"material wants list, add, image, assign, resolve or remove, not '{sub}'.");
                }
                var result = session.Apply(op);
                if (result.Ok) session.Save();
                o.Write(result.ToJson());
                code = result.Ok ? 0 : 1;
                return true;
            }
            case "pack":
            {
                var file = Pos(0, "<file>");
                var outPath = opts.GetValueOrDefault("out") ?? Path.ChangeExtension(file, MapPack.Extension);
                MapPack.Pack(file, outPath, library);
                o.Write(CanonicalJson.Write(new JsonObject { ["ok"] = true, ["out"] = outPath }));
                return true;
            }
            case "unpack":
            {
                var pack = Pos(0, "<pack>");
                var map = MapPack.Unpack(pack, opts.GetValueOrDefault("into") ?? library.MapsDir);
                o.Write(CanonicalJson.Write(new JsonObject { ["ok"] = true, ["map"] = map }));
                return true;
            }
            case "inside":
            {
                // inside <file> x,y,z: which compiled gameplay volumes hold the point: by their exact shape (edited) and by their box
                var map = CustomMap.Compile(ReadDoc(Pos(0, "<file>")));
                var p = ParseVec(Pos(1, "<x,y,z>"));
                var hits = new JsonArray();
                foreach (var (name, centre, yaw, size, round) in map.Content.ControlPoints.Select(c => (c.Name, c.Position, c.Yaw, c.Scale, true))
                    .Concat(map.Content.StartAreas.Select(a => (a.Name, a.Position, a.Yaw, a.Scale, false)))
                    .Concat(map.Content.Blockers.Select(b => (b.Name, b.Position, b.Yaw, b.Scale, false)))
                    .Concat(map.Content.KillRegions.Select(k => (k.Name, k.Position, k.Yaw, k.Scale, false))))
                {
                    // an unedited capture point is the circle play uses (radius X / 2, as SmSim.ControlPoint); the others their turned box
                    bool inBox = round && !map.Volumes.ContainsKey(name)
                        ? (p.X - centre.X) * (p.X - centre.X) + (p.Y - centre.Y) * (p.Y - centre.Y) <= size.X * size.X / 4 && MathF.Abs(p.Z - centre.Z) <= size.Z / 2
                        : InTurnedBox(p, centre, yaw, size);
                    bool? inShape = map.Volumes.TryGetValue(name, out var vol) ? vol.Contains(p) : null;
                    if (inBox || inShape == true) hits.Add(new JsonObject { ["name"] = name, ["inBox"] = inBox, ["inShape"] = inShape is { } b ? b : inBox, ["edited"] = inShape is not null });
                }
                o.Write(CanonicalJson.Write(new JsonObject { ["ok"] = true, ["point"] = Json.Vec(p), ["volumes"] = hits }));
                return true;
            }
            default: return false;
        }
    }

    static MapDocument ReadDoc(string file) => MapDocument.Parse(File.Exists(file) ? File.ReadAllText(file) : throw new MapException("bad-file", $"No file '{file}'."));

    static bool InTurnedBox(Vector3 p, Vector3 centre, float yaw, Vector3 size)
    {
        var d = p - centre;
        float c = MathF.Cos(-yaw), s = MathF.Sin(-yaw);
        float x = d.X * c - d.Y * s, y = d.X * s + d.Y * c;
        return MathF.Abs(x) <= size.X / 2 && MathF.Abs(y) <= size.Y / 2 && MathF.Abs(d.Z) <= size.Z / 2;
    }

    static JsonObject DimsNode(MapObject x)
    {
        var d = MeshDims.Of(x);
        return new JsonObject { ["id"] = x.Id, ["name"] = x.Name, ["dims"] = Json.Vec(d), ["label"] = MeshDims.Label(d), ["edited"] = x.Mesh is not null };
    }

    static JsonObject PbrNode(PbrMaterial m)
    {
        static JsonNode? Tex(TextureSlot? t) => t is null ? null : new JsonObject { ["image"] = t.ImageId, ["channel"] = t.Channel.ToString().ToLowerInvariant() };
        return new JsonObject
        {
            ["baseColor"] = Json.Vec(m.BaseColor), ["metallic"] = m.Metallic, ["roughness"] = m.Roughness, ["ao"] = m.Ao,
            ["baseColorTexture"] = Tex(m.BaseColorTexture), ["metallicTexture"] = Tex(m.MetallicTexture), ["roughnessTexture"] = Tex(m.RoughnessTexture), ["aoTexture"] = Tex(m.AoTexture),
            ["normalTexture"] = Tex(m.NormalTexture), ["normalStrength"] = m.NormalStrength, ["normalDirectX"] = m.NormalDirectX,
            ["uvScale"] = new JsonArray(m.UvScale.X, m.UvScale.Y), ["uvOffset"] = new JsonArray(m.UvOffset.X, m.UvOffset.Y),
        };
    }

    static int ParseInt(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new ArgException($"'{s}' is not a whole number.");
    static float ParseFloat(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : throw new ArgException($"'{s}' is not a number.");

    static Vector3 ParseVec(string s)
    {
        var p = s.Split(',');
        if (p.Length != 3) throw new ArgException($"'{s}' is not x,y,z.");
        return new Vector3(ParseFloat(p[0]), ParseFloat(p[1]), ParseFloat(p[2]));
    }

    const string EditUsage = "edit-mesh <file> <id> (--face +x | --mode vertex|edge|face --ids 1,2) (--move x,y,z | --rotate deg --axis z | --scale f|x,y,z) [--axis x|y|z] [--pivot selection|median|origin|x,y,z] [--dry-run] | edit-face <file> <id> --face +x --move 1,0,0 | dims <file> [ids] | mesh <file> <id> | script <file> <steps.json|-> [--save] | material list|add|image|assign|resolve|remove <file> ... | pack <file> [--out f.smpack] | unpack <f.smpack> [--into dir] | inside <file> x,y,z";
}
