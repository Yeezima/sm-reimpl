using System.Numerics;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// The first shader-node graph of the Mapmaker (owner-approved brief 2026-11-25): six node types, typed sockets, at most one link into an input. A graph is stored in the map
/// (<c>materials</c>), so it is part of the map's hash; <see cref="MaterialGraph.Resolve"/> flattens it into the one PBR description (<see cref="PbrMaterial"/>) that the editor preview and the
/// game both build their surface from, so preview = saved = in game. A graph that cannot be flattened (two different mappings, a channel fed to base colour) is refused when it is edited,
/// never half drawn.
/// </summary>
public static class NodeTypes
{
    public const string ImageTexture = "image-texture", Mapping = "mapping", NormalMap = "normal-map", SeparateRgb = "separate-rgb", PbrSurface = "pbr-surface", Output = "output";
    public static readonly string[] All = [ImageTexture, Mapping, NormalMap, SeparateRgb, PbrSurface, Output];

    /// <summary>Socket types. A link joins an output and an input of the same type, except colour into a float input (that takes the red channel, as Godot's default texture channel).</summary>
    public const string Vector = "vector", Color = "color", Float = "float", Normal = "normal", Surface = "surface";

    /// <summary>Input sockets of a node type (name, type).</summary>
    public static IReadOnlyList<(string Name, string Type)> Inputs(string type) => type switch
    {
        ImageTexture => [("vector", Vector)],
        Mapping => [],
        NormalMap => [("color", Color)],
        SeparateRgb => [("color", Color)],
        PbrSurface => [("base-color", Color), ("metallic", Float), ("roughness", Float), ("normal", Normal), ("ao", Float)],
        Output => [("surface", Surface)],
        _ => [],
    };

    /// <summary>Output sockets of a node type (name, type).</summary>
    public static IReadOnlyList<(string Name, string Type)> Outputs(string type) => type switch
    {
        ImageTexture => [("color", Color), ("alpha", Float)],
        Mapping => [("vector", Vector)],
        NormalMap => [("normal", Normal)],
        SeparateRgb => [("r", Float), ("g", Float), ("b", Float)],
        PbrSurface => [("surface", Surface)],
        _ => [],
    };

    public static bool Accepts(string inputType, string outputType) => inputType == outputType || (inputType == Float && outputType == Color);
}

/// <summary>
/// One node. Parameters by type (unknown keys are refused; missing ones take the default):
/// image-texture {image: image id or null}; mapping {scale: [u, v] repeats per metre, default [1, 1]; offset: [u, v], default [0, 0]};
/// normal-map {strength: 0..10, default 1; convention: "opengl" (default) | "directx" (green flipped)}; separate-rgb {}; output {};
/// pbr-surface {baseColor: [r, g, b] 0..1 (default 0.8 grey), metallic 0..1 (0), roughness 0..1 (0.5), ao 0..1 (1)}: the value an unlinked input uses.
/// <see cref="Position"/> is where the node editor draws it (editor layout; kept in the file, part of the hash like every other byte of the graph).
/// </summary>
public sealed record MaterialNode(string Id, string Type, JsonObject Params, Vector2 Position)
{
    public JsonObject ToNode() => new() { ["id"] = Id, ["type"] = Type, ["params"] = Params.DeepClone(), ["position"] = new JsonArray(Position.X, Position.Y) };

    public static MaterialNode FromNode(JsonNode? n, string what, string code)
    {
        var o = Json.Obj(n, what, code);
        var id = Json.ReqStr(o, "id", what, code);
        if (!Json.IsSlug(id) || id.Length > 32) throw new MapException(code, $"{what}: '{id}' is not a node id (lowercase letters, digits, '-', at most 32).");
        var type = Json.ReqStr(o, "type", what, code);
        if (!NodeTypes.All.Contains(type)) throw new MapException(code, $"{what}.type '{type}' is not a node type ({string.Join(", ", NodeTypes.All)}).");
        var ps = o["params"] is { } pn ? (JsonObject)Json.Obj(pn, what + ".params", code).DeepClone() : new JsonObject();
        var pos = o["position"] is JsonArray { Count: 2 } pa ? new Vector2(Json.Num(pa[0], what + ".position", code), Json.Num(pa[1], what + ".position", code)) : Vector2.Zero;
        var node = new MaterialNode(id, type, CanonicalParams(type, ps, what, code), pos);
        return node;
    }

    /// <summary>Validates and rewrites the parameters in a fixed form (defaults left out, one way to write each value), so the same graph hashes the same.</summary>
    internal static JsonObject CanonicalParams(string type, JsonObject ps, string what, string code)
    {
        string[] allowed = type switch
        {
            NodeTypes.ImageTexture => ["image"],
            NodeTypes.Mapping => ["scale", "offset"],
            NodeTypes.NormalMap => ["strength", "convention"],
            NodeTypes.PbrSurface => ["baseColor", "metallic", "roughness", "ao"],
            _ => [],
        };
        foreach (var (k, _) in ps) if (!allowed.Contains(k)) throw new MapException(code, $"{what}: a {type} node has no parameter '{k}'.");
        var o = new JsonObject();
        float Unit(string key, float dflt, float max = 1f)
        {
            if (ps[key] is null) return dflt;
            float f = Json.Num(ps[key], $"{what}.params.{key}", code);
            if (f < 0 || f > max) throw new MapException(code, $"{what}.params.{key} must be between 0 and {max}.");
            return f;
        }
        Vector2 Pair(string key, Vector2 dflt)
        {
            if (ps[key] is null) return dflt;
            if (ps[key] is not JsonArray { Count: 2 } a) throw new MapException(code, $"{what}.params.{key} must be [u, v].");
            var v = new Vector2(Json.Num(a[0], $"{what}.params.{key}", code), Json.Num(a[1], $"{what}.params.{key}", code));
            if (MathF.Abs(v.X) > 10_000 || MathF.Abs(v.Y) > 10_000) throw new MapException(code, $"{what}.params.{key} is beyond ±10000.");
            return v;
        }
        switch (type)
        {
            case NodeTypes.ImageTexture:
                if (Json.Str(ps["image"], what + ".params.image", code, nullable: true) is { } img)
                {
                    if (!MapImage.IsImageId(img)) throw new MapException(code, $"{what}.params.image '{img}' is not an image id (64 lowercase hex: the image's SHA-256).");
                    o["image"] = img;
                }
                break;
            case NodeTypes.Mapping:
            {
                var s = Pair("scale", Vector2.One); var off = Pair("offset", Vector2.Zero);
                if (s.X == 0 || s.Y == 0) throw new MapException(code, $"{what}.params.scale must not be zero.");
                if (s != Vector2.One) o["scale"] = new JsonArray(s.X, s.Y);
                if (off != Vector2.Zero) o["offset"] = new JsonArray(off.X, off.Y);
                break;
            }
            case NodeTypes.NormalMap:
            {
                float st = Unit("strength", 1f, 10f);
                if (st != 1f) o["strength"] = st;
                var conv = Json.Str(ps["convention"], what + ".params.convention", code, nullable: true) ?? "opengl";
                if (conv is not ("opengl" or "directx")) throw new MapException(code, $"{what}.params.convention must be \"opengl\" or \"directx\".");
                if (conv != "opengl") o["convention"] = conv;
                break;
            }
            case NodeTypes.PbrSurface:
            {
                var bc = ps["baseColor"] is { } bn ? Json.Vec(bn, what + ".params.baseColor", code) : PbrMaterial.DefaultBaseColor;
                if (bc.X is < 0 or > 1 || bc.Y is < 0 or > 1 || bc.Z is < 0 or > 1) throw new MapException(code, $"{what}.params.baseColor must be [r, g, b] between 0 and 1.");
                if (bc != PbrMaterial.DefaultBaseColor) o["baseColor"] = Json.Vec(bc);
                float m = Unit("metallic", 0f), r = Unit("roughness", 0.5f), ao = Unit("ao", 1f);
                if (m != 0f) o["metallic"] = m;
                if (r != 0.5f) o["roughness"] = r;
                if (ao != 1f) o["ao"] = ao;
                break;
            }
        }
        return o;
    }
}

/// <summary>A link from an output socket to an input socket.</summary>
public sealed record MaterialLink(string From, string Out, string To, string In)
{
    public JsonObject ToNode() => new() { ["from"] = From, ["out"] = Out, ["to"] = To, ["in"] = In };

    public static MaterialLink FromNode(JsonNode? n, string what, string code)
    {
        var o = Json.Obj(n, what, code);
        return new MaterialLink(Json.ReqStr(o, "from", what, code), Json.ReqStr(o, "out", what, code), Json.ReqStr(o, "to", what, code), Json.ReqStr(o, "in", what, code));
    }
}

/// <summary>A reusable material of the map: id (<c>m&lt;N&gt;</c>), name and node graph.</summary>
public sealed record MaterialGraph(string Id, string Name, IReadOnlyList<MaterialNode> Nodes, IReadOnlyList<MaterialLink> Links)
{
    public const int MaxNodes = 64, MaxLinks = 128;

    /// <summary>A new material: one PBR surface linked to the output (a plain grey surface until textures are linked).</summary>
    public static MaterialGraph Default(string id, string name) => new(id, name,
        [new MaterialNode("surface", NodeTypes.PbrSurface, new JsonObject(), new Vector2(0, 0)), new MaterialNode("output", NodeTypes.Output, new JsonObject(), new Vector2(300, 0))],
        [new MaterialLink("surface", "surface", "output", "surface")]);

    public static bool IsMaterialId(string? id) =>
        id is { Length: > 1 and <= 10 } && id[0] == 'm' && id.Skip(1).All(char.IsAsciiDigit) && int.TryParse(id.AsSpan(1), out var n) && n >= 1 && id == "m" + n;

    public MaterialNode? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public JsonObject ToNode() => new()
    {
        ["id"] = Id, ["name"] = Name,
        ["nodes"] = new JsonArray(Nodes.Select(n => (JsonNode)n.ToNode()).ToArray()),
        ["links"] = new JsonArray(Links.Select(l => (JsonNode)l.ToNode()).ToArray()),
    };

    public static MaterialGraph FromNode(JsonNode? n, string what, string code = "bad-file")
    {
        var o = Json.Obj(n, what, code);
        var id = Json.ReqStr(o, "id", what, code);
        if (!IsMaterialId(id)) throw new MapException(code, $"{what}: '{id}' is not a material id (m<N>).");
        var name = Json.Str(o["name"], what + ".name", code) ?? "";
        if (name.Length > MapDocument.MaxNameLength) throw new MapException("too-big", $"{what}: the name is longer than {MapDocument.MaxNameLength} characters.");
        var nodes = Json.Arr(o["nodes"], what + ".nodes", code);
        var links = o["links"] is { } ln ? Json.Arr(ln, what + ".links", code) : [];
        if (nodes.Count > MaxNodes || links.Count > MaxLinks) throw new MapException("too-big", $"{what}: more than {MaxNodes} nodes or {MaxLinks} links.");
        var g = new MaterialGraph(id, name, nodes.Select((x, i) => MaterialNode.FromNode(x, $"{what}.nodes[{i}]", code)).ToList(),
            links.Select((x, i) => MaterialLink.FromNode(x, $"{what}.links[{i}]", code)).ToList());
        g.Check(code);
        return g;
    }

    /// <summary>
    /// The graph's rules: unique node ids, at most one output node, each link between existing sockets of matching type, at most one link into an input, no cycle; and it must flatten
    /// (<see cref="Resolve"/>). <c>code</c> (<c>bad-file</c> on read, <c>bad-graph</c> / <c>unsupported-graph</c> when edited) otherwise.
    /// </summary>
    public void Check(string code = "bad-graph")
    {
        var ids = new HashSet<string>();
        foreach (var nd in Nodes) if (!ids.Add(nd.Id)) throw new MapException(code, $"Material '{Name}': two nodes are called '{nd.Id}'.");
        if (Nodes.Count(x => x.Type == NodeTypes.Output) > 1) throw new MapException(code, $"Material '{Name}' has more than one output node.");
        var into = new HashSet<(string, string)>();
        foreach (var l in Links)
        {
            var from = Node(l.From) ?? throw new MapException(code, $"Material '{Name}': a link comes from a missing node '{l.From}'.");
            var to = Node(l.To) ?? throw new MapException(code, $"Material '{Name}': a link goes to a missing node '{l.To}'.");
            var outSock = NodeTypes.Outputs(from.Type).FirstOrDefault(s => s.Name == l.Out);
            var inSock = NodeTypes.Inputs(to.Type).FirstOrDefault(s => s.Name == l.In);
            if (outSock.Name is null) throw new MapException(code, $"Material '{Name}': a {from.Type} node has no output '{l.Out}'.");
            if (inSock.Name is null) throw new MapException(code, $"Material '{Name}': a {to.Type} node has no input '{l.In}'.");
            if (!NodeTypes.Accepts(inSock.Type, outSock.Type)) throw new MapException(code, $"Material '{Name}': {from.Id}.{l.Out} ({outSock.Type}) cannot feed {to.Id}.{l.In} ({inSock.Type}).");
            if (!into.Add((l.To, l.In))) throw new MapException(code, $"Material '{Name}': {to.Id}.{l.In} has two links into it.");
        }
        // No cycle (every node type is at a fixed depth in the supported graphs, but a hostile file may still loop).
        var state = new Dictionary<string, int>();
        bool Visit(string id, int depth)
        {
            if (depth > MaxNodes) return false;
            if (state.TryGetValue(id, out var s)) return s == 2;
            state[id] = 1;
            foreach (var l in Links.Where(l => l.To == id)) if (!Visit(l.From, depth + 1)) return false;
            state[id] = 2;
            return true;
        }
        foreach (var nd in Nodes) if (!Visit(nd.Id, 0)) throw new MapException(code, $"Material '{Name}': the links make a loop.");
        Resolve(code == "bad-file" ? code : "unsupported-graph");
    }

    MaterialLink? LinkInto(string node, string input) => Links.FirstOrDefault(l => l.To == node && l.In == input);

    /// <summary>
    /// The flat PBR description of the graph (what the editor preview and the game build). Rules (ours; docs/mapmaker.md "Materials"): the output's surface must come from a PBR surface (no
    /// output, or nothing linked into it: the default grey surface); an unlinked PBR input uses its parameter; base colour takes an image's colour; metallic, roughness and AO take an image's
    /// colour (its red channel), its alpha, or a separate-rgb channel of an image's colour; normal takes a normal-map of an image's colour. Every image used must share one mapping (the same node,
    /// or mappings with equal parameters; none = scale 1, offset 0). Anything else is <c>unsupported-graph</c>.
    /// </summary>
    public PbrMaterial Resolve(string code = "unsupported-graph")
    {
        var output = Nodes.FirstOrDefault(x => x.Type == NodeTypes.Output);
        if (output is null || LinkInto(output.Id, "surface") is not { } sl) return PbrMaterial.Plain;
        var surface = Node(sl.From)!;
        if (surface.Type != NodeTypes.PbrSurface) throw new MapException(code, $"Material '{Name}': the output must be fed by a PBR surface.");
        var p = surface.Params;
        Vector2? uvScale = null, uvOffset = null;
        string? ImageOf(MaterialNode tex)
        {
            var img = Json.Str(tex.Params["image"], "image", code, nullable: true);
            Vector2 s = Vector2.One, off = Vector2.Zero;
            if (LinkInto(tex.Id, "vector") is { } vl)
            {
                var m = Node(vl.From)!;
                if (m.Params["scale"] is JsonArray sa) s = new Vector2(Json.Num(sa[0], "scale", code), Json.Num(sa[1], "scale", code));
                if (m.Params["offset"] is JsonArray oa) off = new Vector2(Json.Num(oa[0], "offset", code), Json.Num(oa[1], "offset", code));
            }
            if (uvScale is { } us && (us != s || uvOffset != off)) throw new MapException(code, $"Material '{Name}': every image must use the same mapping (one UV scale and offset per material).");
            uvScale = s; uvOffset = off;
            return img;
        }
        TextureSlot? Channel(string input)
        {
            if (LinkInto(surface.Id, input) is not { } l) return null;
            var src = Node(l.From)!;
            if (src.Type == NodeTypes.ImageTexture) return ImageOf(src) is { } id ? new TextureSlot(id, l.Out == "alpha" ? TextureChannel.A : TextureChannel.R) : null;
            if (src.Type == NodeTypes.SeparateRgb && LinkInto(src.Id, "color") is { } cl && Node(cl.From) is { Type: NodeTypes.ImageTexture } tex)
                return ImageOf(tex) is { } id ? new TextureSlot(id, l.Out switch { "g" => TextureChannel.G, "b" => TextureChannel.B, _ => TextureChannel.R }) : null;
            if (src.Type == NodeTypes.SeparateRgb && LinkInto(src.Id, "color") is null) return null;
            throw new MapException(code, $"Material '{Name}': {input} must come from an image, an image's alpha, or a channel of an image (separate-rgb).");
        }
        TextureSlot? baseTex = null;
        if (LinkInto(surface.Id, "base-color") is { } bl)
        {
            var src = Node(bl.From)!;
            if (src.Type != NodeTypes.ImageTexture) throw new MapException(code, $"Material '{Name}': base colour must come from an image's colour.");
            baseTex = ImageOf(src) is { } id ? new TextureSlot(id, TextureChannel.Rgb) : null;
        }
        var metallic = Channel("metallic");
        var roughness = Channel("roughness");
        var ao = Channel("ao");
        TextureSlot? normal = null; float strength = 1f; bool directX = false;
        if (LinkInto(surface.Id, "normal") is { } nl)
        {
            var nm = Node(nl.From)!;   // only a normal-map node has a normal output
            strength = nm.Params["strength"] is { } sn ? Json.Num(sn, "strength", code) : 1f;
            directX = Json.Str(nm.Params["convention"], "convention", code, nullable: true) == "directx";
            if (LinkInto(nm.Id, "color") is { } ncl)
            {
                var tex = Node(ncl.From)!;
                if (tex.Type != NodeTypes.ImageTexture) throw new MapException(code, $"Material '{Name}': a normal map must be fed by an image's colour.");
                normal = ImageOf(tex) is { } id ? new TextureSlot(id, TextureChannel.Rgb) : null;
            }
        }
        var bc = p["baseColor"] is { } bn ? Json.Vec(bn, "baseColor", code) : PbrMaterial.DefaultBaseColor;
        float F(string k, float d) => p[k] is { } x ? Json.Num(x, k, code) : d;
        return new PbrMaterial(
            baseTex is null ? bc : Vector3.One, metallic is null ? F("metallic", 0f) : 1f, roughness is null ? F("roughness", 0.5f) : 1f, ao is null ? F("ao", 1f) : 1f,
            baseTex, metallic, roughness, ao, normal, strength, directX, uvScale ?? Vector2.One, uvOffset ?? Vector2.Zero);
    }

    /// <summary>Every image id the graph's nodes name (linked or not).</summary>
    public IEnumerable<string> ImageIds() =>
        Nodes.Where(n => n.Type == NodeTypes.ImageTexture).Select(n => Json.Str(n.Params["image"], "image", "bad-file", nullable: true)).OfType<string>().Distinct();
}

public enum TextureChannel { Rgb, R, G, B, A }

/// <summary>One texture input of a resolved material: the image and the channel read from it.</summary>
public sealed record TextureSlot(string ImageId, TextureChannel Channel);

/// <summary>
/// A resolved material: what both renderers (editor preview, game) turn into one surface (Godot: a <c>StandardMaterial3D</c> with albedo, metallic, roughness, normal and AO textures and their
/// channels, <c>uv1_scale</c> / <c>uv1_offset</c>). Scalars multiply their texture (they are 1 when a texture is linked, as Godot multiplies). UVs are in metres along each face
/// (<see cref="RenderMesh"/>), so <see cref="UvScale"/> is repeats per metre. A DirectX normal map has its green channel flipped when it is built.
/// </summary>
public sealed record PbrMaterial(Vector3 BaseColor, float Metallic, float Roughness, float Ao, TextureSlot? BaseColorTexture, TextureSlot? MetallicTexture, TextureSlot? RoughnessTexture,
    TextureSlot? AoTexture, TextureSlot? NormalTexture, float NormalStrength, bool NormalDirectX, Vector2 UvScale, Vector2 UvOffset)
{
    public static readonly Vector3 DefaultBaseColor = new(0.8f, 0.8f, 0.8f);
    public static readonly PbrMaterial Plain = new(DefaultBaseColor, 0f, 0.5f, 1f, null, null, null, null, null, 1f, false, Vector2.One, Vector2.Zero);

    public IEnumerable<TextureSlot> Textures() => new[] { BaseColorTexture, MetallicTexture, RoughnessTexture, AoTexture, NormalTexture }.OfType<TextureSlot>();
}
