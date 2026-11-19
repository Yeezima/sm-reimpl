using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>One part of a prefab: an asset or another prefab, placed relative to the prefab's origin.</summary>
public sealed record PrefabPart(string Id, string Name, AssetRef? Asset, string? Prefab, MapTransform Transform)
{
    public JsonObject ToNode()
    {
        var o = new JsonObject { ["id"] = Id, ["name"] = Name };
        if (Asset is not null) o["asset"] = Asset.ToNode(); else o["prefab"] = Prefab;
        o["transform"] = Transform.ToNode();
        return o;
    }

    public static PrefabPart FromNode(JsonNode? n, string what)
    {
        var o = Json.Obj(n, what);
        var asset = o["asset"] is { } an ? AssetRef.FromNode(an, what + ".asset", "bad-file") : null;
        var prefab = Json.Str(o["prefab"], what + ".prefab", nullable: true);
        if ((asset is null) == (prefab is null)) throw new MapException("bad-file", $"{what}: exactly one of asset or prefab is required.");
        if (prefab is not null && !Json.IsSlug(prefab)) throw new MapException("bad-file", $"{what}: '{prefab}' is not a prefab id.");
        return new PrefabPart(Json.ReqStr(o, "id", what), Json.Str(o["name"], what + ".name", nullable: true) ?? "", asset, prefab,
            MapTransform.FromNode(o["transform"], MapTransform.Identity, what + ".transform"));
    }
}

/// <param name="Source">"sm1", "custom" or "mixed".</param>
public sealed record PrefabProvenance(string Source, string? Map, IReadOnlyList<string> Objects)
{
    /// <summary>For extract-sm1: each extracted placement's index in <c>Sm1Map.Objects</c>, parallel to <see cref="Objects"/> (empty otherwise).</summary>
    public IReadOnlyList<int> Indices { get; init; } = [];
    /// <summary>For extract-sm1 by part: the part of each entry (-1 = the whole placement), parallel to <see cref="Indices"/>; empty when no part was picked.</summary>
    public IReadOnlyList<int> Parts { get; init; } = [];

    public JsonObject ToNode()
    {
        var o = new JsonObject { ["source"] = Source };
        if (Map is not null) o["map"] = Map;
        o["objects"] = new JsonArray(Objects.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        if (Indices.Count > 0) o["indices"] = new JsonArray(Indices.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        if (Parts.Any(p => p >= 0)) o["parts"] = new JsonArray(Parts.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        return o;
    }

    public static PrefabProvenance FromNode(JsonNode? n)
    {
        if (n is null) return new PrefabProvenance("custom", null, []);
        var o = Json.Obj(n, "provenance");
        var source = Json.ReqStr(o, "source", "provenance");
        if (source is not ("sm1" or "custom" or "mixed")) throw new MapException("bad-file", "provenance.source must be sm1, custom or mixed.");
        var objs = o["objects"] is { } on ? Json.Arr(on, "provenance.objects").Select(x => Json.Str(x, "provenance.objects[]")!).ToList() : [];
        var idx = o["indices"] is { } xn ? Json.Arr(xn, "provenance.indices").Select(x => Json.Int(x, "provenance.indices[]", "bad-file")).ToList() : [];
        var parts = o["parts"] is { } pn ? Json.Arr(pn, "provenance.parts").Select(x => Json.Int(x, "provenance.parts[]", "bad-file")).ToList() : [];
        return new PrefabProvenance(source, Json.Str(o["map"], "provenance.map", nullable: true), objs) { Indices = idx, Parts = parts };
    }
}

/// <summary>A <c>.smprefab</c> (formatVersion 1): parts relative to <see cref="Origin"/>, which is the source-frame point that became (0,0,0) (provenance only).</summary>
/// <param name="NextPart">The counter part ids <c>p&lt;N&gt;</c> come from; never reused (an addition to the contract's field list, so removed parts' overrides never attach to new ones).</param>
/// <param name="Collision">"mesh" or "original-cut".</param>
public sealed record Prefab(
    string Id, string Name, IReadOnlyList<string> Tags, Vector3 Origin, string Collision, int NextPart, IReadOnlyList<PrefabPart> Parts,
    PrefabProvenance Provenance, Bounds? Bounds)
{
    public const string FormatName = "smprefab";
    public const int FormatVersion = 1;
    public static readonly string[] Collisions = ["mesh", "original-cut"];

    public JsonObject ToNode()
    {
        var o = new JsonObject
        {
            ["format"] = FormatName, ["formatVersion"] = FormatVersion, ["id"] = Id, ["name"] = Name,
            ["tags"] = new JsonArray(Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["origin"] = Json.Vec(Origin), ["collision"] = Collision, ["nextPart"] = NextPart,
            ["parts"] = new JsonArray(Parts.Select(p => (JsonNode)p.ToNode()).ToArray()),
            ["provenance"] = Provenance.ToNode(),
        };
        if (Bounds is { } b) o["bounds"] = b.ToNode();
        return o;
    }

    public string ToJson() => CanonicalJson.Write(ToNode());

    public static Prefab Parse(string json)
    {
        var o = Json.Obj(Json.ParseText(json, "The prefab"), "The prefab");
        if (Json.Str(o["format"], "format", nullable: true) != FormatName) throw new MapException("bad-file", "Not a .smprefab (format must be \"smprefab\").");
        if (o["formatVersion"] is null || Json.Int(o["formatVersion"], "formatVersion", "bad-file") != FormatVersion)
            throw new MapException("bad-file", $"Unsupported formatVersion (this build reads {FormatVersion}).");
        var id = Json.ReqStr(o, "id", "prefab");
        if (!Json.IsSlug(id)) throw new MapException("bad-file", $"'{id}' is not a prefab id.");
        var collision = Json.Str(o["collision"], "collision", nullable: true) ?? "mesh";
        if (!Collisions.Contains(collision)) throw new MapException("bad-file", $"collision must be one of: {string.Join(", ", Collisions)}.");
        var parts = new List<PrefabPart>();
        var seen = new HashSet<string>();
        foreach (var (n, i) in Json.Arr(o["parts"], "parts").Select((x, i) => (x, i)))
        {
            var p = PrefabPart.FromNode(n, $"parts[{i}]");
            if (!seen.Add(p.Id)) throw new MapException("bad-file", $"Duplicate part id '{p.Id}'.");
            parts.Add(p);
        }
        int max = parts.Select(p => PartNumber(p.Id)).DefaultIfEmpty(0).Max();
        int next = o["nextPart"] is { } np ? Json.Int(np, "nextPart", "bad-file") : max + 1;
        var tags = o["tags"] is { } tn ? Json.Arr(tn, "tags").Select(t => Json.Str(t, "tags[]")!).ToList() : [];
        return new Prefab(id, Json.Str(o["name"], "name", nullable: true) ?? id, tags, o["origin"] is { } on ? Json.Vec(on, "origin", "bad-file") : Vector3.Zero,
            collision, Math.Max(next, 1), parts, PrefabProvenance.FromNode(o["provenance"]), o["bounds"] is { } bn ? SmMapmaker.Bounds.FromNode(bn, "bounds") : null);
    }

    public static int PartNumber(string partId) => partId.Length > 1 && partId[0] == 'p' && int.TryParse(partId.AsSpan(1), out var n) ? n : 0;
}

/// <summary>A library asset (<c>library/assets/&lt;id&gt;/asset.json</c>). <see cref="Bounds"/> are in the glTF file's own frame (Y up, as imported).</summary>
public sealed record AssetInfo(string Id, string Name, string File, Bounds? Bounds, string Sha256, string ImportedFrom)
{
    /// <summary>What the importer did (scale applied, textures, source format, LOD); free-form, read by the editor's toast and docs only.</summary>
    public JsonObject? Import { get; init; }

    /// <summary>"gltf-y-up": the stored bounds and the file are in glTF's frame; the document frame is Z up (<see cref="BoundsInDocumentFrame"/>).</summary>
    public const string Frame = "gltf-y-up";

    public JsonObject ToNode()
    {
        var o = new JsonObject { ["id"] = Id, ["name"] = Name, ["file"] = File };
        if (Bounds is { } b) o["bounds"] = b.ToNode(); else o["bounds"] = null;
        o["sha256"] = Sha256;
        o["importedFrom"] = ImportedFrom;
        o["frame"] = Frame;
        if (Import is { } meta) o["import"] = meta.DeepClone();
        return o;
    }

    public static AssetInfo Parse(string json)
    {
        var o = Json.Obj(Json.ParseText(json, "asset.json"), "asset.json");
        var id = Json.ReqStr(o, "id", "asset");
        if (!Json.IsSlug(id)) throw new MapException("bad-file", $"'{id}' is not an asset id.");
        var file = Json.ReqStr(o, "file", "asset");
        if (!IsPlainFileName(file)) throw new MapException("bad-file", $"asset.json: '{file}' is not a plain file name.");
        return new AssetInfo(id, Json.Str(o["name"], "name", nullable: true) ?? id, file,
            o["bounds"] is { } bn ? SmMapmaker.Bounds.FromNode(bn, "bounds") : null, Json.Str(o["sha256"], "sha256", nullable: true) ?? "", Json.Str(o["importedFrom"], "importedFrom", nullable: true) ?? "")
        {
            Import = o["import"] is JsonObject meta ? (JsonObject)meta.DeepClone() : null,
        };
    }

    /// <summary>A name with no folder, no "..", no drive or stream colon.</summary>
    public static bool IsPlainFileName(string f) =>
        f.Length > 0 && f != "." && f != ".." && f.IndexOfAny(['/', '\\', ':', '\0']) < 0 && !f.Contains("..");

    /// <summary>The bounds as the document frame sees them: glTF (x, y, z) becomes (x, -z, y), the usual Y-up to Z-up turn.</summary>
    public Bounds? BoundsInDocumentFrame => Bounds is { } b ? GltfToDocument(b) : null;

    public static SmMapmaker.Bounds GltfToDocument(SmMapmaker.Bounds b)
    {
        static Vector3 T(Vector3 v) => new(v.X, -v.Z, v.Y);
        var lo = T(b.Min);
        var hi = T(b.Max);
        return new Bounds(Vector3.Min(lo, hi), Vector3.Max(lo, hi));
    }
}

/// <summary>Walks prefab-in-prefab references (iteratively: a hostile chain of thousands of prefabs must not overflow the stack).</summary>
public static class PrefabGraph
{
    /// <summary>Most prefab levels one instance may nest (the compiler's own limit: an instance's prefab is level 0, so 17 prefabs deep).</summary>
    public const int MaxLevels = 17;

    /// <summary>A cycle reachable from <paramref name="start"/> as "a -> b -> a", or null. Unknown prefabs end a path.</summary>
    /// <exception cref="MapException"><c>too-big</c>: nested deeper than <see cref="MaxLevels"/>.</exception>
    public static string? FindCycle(string start, Func<string, Prefab?> get) => FindCycle([start], get);

    /// <summary>
    /// The first cycle reachable from any of <paramref name="starts"/>, or null; one walk state is shared by all starts, so checking every prefab of a file costs one visit per prefab and edge.
    /// </summary>
    public static string? FindCycle(IEnumerable<string> starts, Func<string, Prefab?> get)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1: on the current path, 2: finished
        var height = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        var iterators = new List<IEnumerator<string>>();
        var best = new List<int>();
        static IEnumerator<string> Children(Prefab p) => p.Parts.Where(x => x.Prefab is not null).Select(x => x.Prefab!).Distinct().GetEnumerator();
        foreach (var start in starts)
        {
            if (state.ContainsKey(start) || get(start) is not { } root) continue;
            path.Add(start); iterators.Add(Children(root)); best.Add(0); state[start] = 1;
            while (path.Count > 0)
            {
                int top = path.Count - 1;
                if (iterators[top].MoveNext())
                {
                    string child = iterators[top].Current;
                    if (state.TryGetValue(child, out int s))
                    {
                        if (s == 1) return string.Join(" -> ", path.Skip(path.IndexOf(child)).Append(child));
                        if (path.Count + height[child] > MaxLevels) throw new MapException("too-big", $"Prefabs are nested more than {MaxLevels} levels deep (at '{child}').");   // the same answer in any order of listing
                        best[top] = Math.Max(best[top], height[child]);
                    }
                    else if (get(child) is { } p)
                    {
                        if (path.Count >= MaxLevels) throw new MapException("too-big", $"Prefabs are nested more than {MaxLevels} levels deep (at '{child}').");
                        path.Add(child); iterators.Add(Children(p)); best.Add(0); state[child] = 1;
                    }
                }
                else
                {
                    int h = best[top] + 1;
                    string id = path[top];
                    state[id] = 2; height[id] = h;
                    path.RemoveAt(top); iterators.RemoveAt(top); best.RemoveAt(top);
                    if (top > 0) best[top - 1] = Math.Max(best[top - 1], h);
                }
            }
        }
        return null;
    }

    public static string? FindCycle(string start, MapLibrary lib) => FindCycle(start, lib.GetPrefab);
}