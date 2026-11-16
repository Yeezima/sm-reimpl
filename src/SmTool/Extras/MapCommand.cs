using System.Text;
using System.Text.Json.Nodes;
using SmMapmaker;

namespace SmTool;

/// <summary>
/// <c>smtool map ...</c> (docs/mapmaker.md "CLI"): the Mapmaker core without an editor, for agents and scripts. JSON on stdout; exit 0 ok, 1 validation
/// errors (the JSON lists them), 2 bad arguments. Every command takes <c>--library &lt;dir&gt;</c>; commands that read the install take <c>--game-dir &lt;dir&gt;</c>.
/// </summary>
public static class MapCommand
{
    public static int Run(string[] args, TextWriter? stdout = null, TextReader? stdin = null)
    {
        stdout ??= Console.Out;
        try
        {
            return Dispatch(args, stdout, stdin ?? Console.In);
        }
        catch (ArgException e) { stdout.Write(Fail("bad-args", e.Message).ToJson()); return 2; }
        catch (MapException e) { stdout.Write(Fail(e.Code, e.Message).ToJson()); return 1; }
        catch (System.Text.Json.JsonException e) { stdout.Write(Fail("bad-file", e.Message).ToJson()); return 1; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stdout.Write(Fail("io-error", e.Message).ToJson()); return 1; }
    }

    sealed class ArgException(string message) : Exception(message);

    static OpResult Fail(string code, string message) => OpResult.Fail(code, message, "map");

    static int Dispatch(string[] args, TextWriter o, TextReader stdin)
    {
        if (args.Length == 0) throw new ArgException(Usage);
        var (pos, opts) = Split(args.Skip(1).ToArray());
        var library = new MapLibrary(opts.GetValueOrDefault("library"));
        ISm1Source? Sm1() => InstalledSm1Source.TryCreate(opts.GetValueOrDefault("game-dir"));
        string Pos(int i, string what) => pos.Count > i ? pos[i] : throw new ArgException($"Missing {what}. {Usage}");

        switch (args[0])
        {
            case "new":
            {
                var file = Pos(0, "<file>");
                if (File.Exists(file)) return Print(o, Fail("file-exists", $"'{file}' already exists."));
                var session = MapSession.New(opts.GetValueOrDefault("name") ?? Path.GetFileNameWithoutExtension(file), library);
                var made = new OpResult(true, [], [], []);
                if (opts.GetValueOrDefault("template") is { } templateId)
                {
                    var template = MapTemplate.Find(templateId) ?? throw new ArgException($"No template '{templateId}' (smtool map templates lists them).");
                    made = session.Apply(template.Ops());
                    if (!made.Ok) return Print(o, made);
                }
                session.Save(file);
                return Print(o, made);
            }
            case "apply":
            {
                var file = Pos(0, "<file>");
                var opsPath = Pos(1, "<ops.json|->");
                bool dry = opts.ContainsKey("dry-run");
                var session = MapSession.Load(file, library, Sm1());
                string text = opsPath == "-" ? stdin.ReadToEnd() : File.Exists(opsPath) ? File.ReadAllText(opsPath) : throw new ArgException($"No ops file '{opsPath}'.");
                var result = session.Apply(text, dry);
                if (result.Ok && !dry) session.Save();
                return Print(o, result);
            }
            case "query":
            {
                var file = Pos(0, "<file>");
                var what = Pos(1, "objects|prefabs|assets");
                var root = new JsonObject { ["ok"] = true };
                switch (what)
                {
                    case "objects":
                        var doc = MapDocument.Parse(File.Exists(file) ? File.ReadAllText(file) : throw new MapException("bad-file", $"No file '{file}'."));
                        root["name"] = doc.Name;
                        root["objects"] = new JsonArray(doc.Objects.Select(x => (JsonNode)x.ToNode()).ToArray());
                        break;
                    case "prefabs":
                        root["prefabs"] = new JsonArray(library.ListPrefabs().Select(p => (JsonNode)new JsonObject
                        {
                            ["id"] = p.Id, ["name"] = p.Name, ["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                            ["parts"] = p.Parts, ["bounds"] = p.Bounds?.ToNode(),
                        }).ToArray());
                        break;
                    case "assets":
                        root["assets"] = new JsonArray(library.ListAssets().Select(a => (JsonNode)a.ToNode()).ToArray());
                        break;
                    default: throw new ArgException($"query wants objects, prefabs or assets, not '{what}'.");
                }
                o.Write(CanonicalJson.Write(root));
                return 0;
            }
            case "query-sm1":
            {
                var map = Pos(0, "<map>");
                var source = Sm1() ?? throw new MapException("missing-install", "The Space Marine install was not found. Set SM_GAME_DIR or pass --game-dir.");
                var scene = source.LoadMap(map);
                var filter = opts.GetValueOrDefault("filter");
                bool Match(string s) => filter is null || s.Contains(filter, StringComparison.OrdinalIgnoreCase);
                var objects = scene.Objects.Select((x, i) => (Obj: x, Index: i)).Where(x => Match(x.Obj.Name) || Match(x.Obj.Definition)).ToList();
                var used = objects.Select(x => x.Obj.Definition).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var root = new JsonObject
                {
                    ["ok"] = true, ["map"] = scene.Map,
                    ["definitions"] = new JsonArray(scene.Definitions.Values.Where(d => Match(d.Name) || used.Contains(d.Name)).OrderBy(d => d.Name, StringComparer.Ordinal)
                        .Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["visuals"] = d.Visuals, ["bounds"] = d.Bounds?.ToNode() }).ToArray()),
                    ["objects"] = new JsonArray(objects.Select(x => (JsonNode)ObjectNode(x.Obj, x.Index)).ToArray()),
                };
                o.Write(CanonicalJson.Write(root));
                return 0;
            }
            case "templates":
            {
                var root = new JsonObject { ["ok"] = true, ["templates"] = new JsonArray(MapTemplate.All.Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["label"] = t.Label, ["description"] = t.Description }).ToArray()) };
                o.Write(CanonicalJson.Write(root));
                return 0;
            }
            case "pieces":
            {
                var root = new JsonObject { ["ok"] = true, ["pieces"] = new JsonArray(PieceKind.All.Select(k => (JsonNode)new JsonObject { ["kind"] = k.Id, ["label"] = k.Label, ["tip"] = k.Tip, ["defaultSize"] = Json.Vec(k.DefaultSize), ["side"] = k.HasSide, ["solid"] = k.Solid }).ToArray()) };
                o.Write(CanonicalJson.Write(root));
                return 0;
            }
            case "check":
            {
                // The instant check in plain words (docs/mapmaker.md "The check"): the game install adds the bots' route (--no-bots leaves it out); --text prints a list instead of JSON.
                var file = Pos(0, "<file>");
                var map = CustomMaps.Load(file);
                SmSim.SimConfig? config = null;
                if (!opts.ContainsKey("no-bots"))
                {
                    try { config = SmContent.GameplayTuning.Load(SmContent.GameInstall.Locate(opts.GetValueOrDefault("game-dir"))).Config; }
                    catch (Exception e) when (e is DirectoryNotFoundException or IOException) { }
                }
                var report = MapCheck.Run(map, config, opts.GetValueOrDefault("nav-cache"));
                if (opts.ContainsKey("text")) o.Write(report.ToText());
                else
                {
                    var node = report.ToNode();
                    node["ok"] = report.AnnihilationReady || report.SeizeReady;
                    node["name"] = map.Document.Name; node["wireName"] = map.Name; node["hash"] = map.Hash.ToString("X16");
                    o.Write(CanonicalJson.Write(node));
                }
                return report.AnnihilationReady || report.SeizeReady ? 0 : 1;
            }
            case "tour":
            {
                // Camera places for the screenshot aids (client --observe-tour): "label@x,y,z,yaw,pitch|label@..." (--raw) or JSON. Test aid, derived from the map's own spawns and points.
                var map = CustomMaps.Load(Pos(0, "<file>"));
                var places = MapTour.Places(map);
                if (opts.ContainsKey("raw")) o.Write(string.Join("|", places.Select(p => $"{p.Label}@{p.Pose}")));
                else o.Write(CanonicalJson.Write(new JsonObject { ["ok"] = true, ["places"] = new JsonArray(places.Select(p => (JsonNode)new JsonObject { ["label"] = p.Label, ["pose"] = p.Pose }).ToArray()) }));
                return 0;
            }
            case "validate":
            {
                var file = Pos(0, "<file>");
                var result = MapSession.Load(file, library).Validate();
                return Print(o, result);
            }
            case "preview":
            {
                var file = Pos(0, "<file>");
                var session = MapSession.Load(file, library, Sm1());
                var svg = session.PreviewSvg();
                var root = new JsonObject { ["ok"] = true };
                if (opts.GetValueOrDefault("out") is { } outPath)
                {
                    MapLibrary.WriteAtomic(outPath, new UTF8Encoding(false).GetBytes(svg));
                    root["out"] = outPath;
                }
                else root["svg"] = svg;
                o.Write(CanonicalJson.Write(root));
                return 0;
            }
            case "import-asset":
            {
                var op = new JsonObject { ["op"] = "import-asset", ["path"] = Pos(0, "<path>") };
                if (opts.GetValueOrDefault("id") is { } id) op["id"] = id;
                if (opts.GetValueOrDefault("name") is { } name) op["name"] = name;
                return Print(o, MapSession.New("library", library).Apply(op));
            }
            case "import-prefab":
                return Print(o, MapSession.New("library", library).Apply(new JsonObject { ["op"] = "import-prefab", ["path"] = Pos(0, "<path>") }));
            default:
                throw new ArgException($"Unknown map command '{args[0]}'. {Usage}");
        }
    }

    static JsonObject ObjectNode(Sm1Object x, int index)
    {
        var node = new JsonObject { ["index"] = index, ["name"] = x.Name, ["definition"] = x.Definition, ["layer"] = x.Layer, ["depth"] = x.Depth };
        try
        {
            var t = MapTransform.FromMatrix(x.Transform);
            node["position"] = Json.Vec(t.Position);
            node["rotation"] = Json.Vec(t.Rotation);
            node["scale"] = Json.Vec(t.Scale);
        }
        catch (MapException)
        {
            node["position"] = Json.Vec(new System.Numerics.Vector3(x.Transform.M41, x.Transform.M42, x.Transform.M43));
            node["rotation"] = null;
            node["scale"] = null;
        }
        return node;
    }

    static int Print(TextWriter o, OpResult r)
    {
        o.Write(r.ToJson());
        return r.Ok ? 0 : 1;
    }

    static (List<string> Pos, Dictionary<string, string> Opts) Split(string[] args)
    {
        var pos = new List<string>();
        var opts = new Dictionary<string, string>();
        string[] flags = ["dry-run", "no-bots", "text", "raw"];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-") { pos.Add("-"); continue; }
            if (!args[i].StartsWith("--")) { pos.Add(args[i]); continue; }
            var key = args[i][2..];
            if (flags.Contains(key)) opts[key] = "true";
            else if (i + 1 < args.Length) opts[key] = args[++i];
            else throw new ArgException($"--{key} needs a value.");
        }
        return (pos, opts);
    }

    const string Usage = "usage: smtool map new <file> [--name n] [--template arena|open-field|blank] | templates | pieces | check <file> [--no-bots] [--text] [--game-dir d] | apply <file> <ops.json|-> [--dry-run] | query <file> objects|prefabs|assets | query-sm1 <map> [--filter text] | validate <file> | preview <file> [--out svg] | import-asset <path> [--id id] [--name n] | import-prefab <path>; all take --library <dir>; --game-dir <dir> where the install is read.";
}
