using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SmMapmaker;
using static SmMapmaker.ModelImport;

namespace SmClient;

/// <summary>
/// The engine-free half of <see cref="MaterialBuild"/> (added content, docs/mapmaker.md "Materials (editor)"): which Godot material feature and texture channel each input of a
/// resolved <see cref="PbrMaterial"/> becomes, the DirectX green flip, and how a picture's file name wires it into a material. Both importers use it (the model import's
/// Megascans / Fab texture sets, <c>AssetImporter</c>, and the standalone image import of the Materials panel), and the editor preview and the in-game client build every
/// surface from it, so preview = saved = in game. Tested without Godot (<c>MaterialPlanTests</c>).
/// </summary>
public static class MaterialPlan
{
    /// <summary>The surface inputs a texture can feed (Godot <c>StandardMaterial3D</c>: albedo, metallic, roughness, ambient occlusion, normal map, emission).</summary>
    public enum Feature { Albedo, Metallic, Roughness, Ao, Normal, Emission }

    /// <summary>Godot's <c>BaseMaterial3D.TextureChannel</c> value for a channel (Red 0, Green 1, Blue 2, Alpha 3; a colour read as one value takes red, as Godot does).</summary>
    public static int GodotChannel(TextureChannel c) => c switch { TextureChannel.G => 1, TextureChannel.B => 2, TextureChannel.A => 3, _ => 0 };

    /// <summary>One texture of a planned surface: the feature it feeds, the image, the channel read, and whether the picture's green channel is inverted first (DirectX normal map).</summary>
    public sealed record Binding(Feature Feature, string ImageId, TextureChannel Channel, bool FlipGreen);

    /// <summary>
    /// What one surface is: albedo colour (multiplied by its texture), metallic, roughness (scalars that multiply their texture, 1 when a texture is linked), the AO value
    /// (an image, or a 1 x 1 picture of the value when below 1: Godot applies AO only from a texture), normal strength, and UV scale / offset (repeats per metre, Godot's
    /// <c>uv1_scale</c> / <c>uv1_offset</c>). Textures in a fixed order (albedo, metallic, roughness, AO, normal).
    /// </summary>
    public sealed record Surface(System.Numerics.Vector3 Albedo, float Metallic, float Roughness, float Ao, float NormalStrength, System.Numerics.Vector2 UvScale, System.Numerics.Vector2 UvOffset, IReadOnlyList<Binding> Textures)
    {
        public Binding? Of(Feature f) => Textures.FirstOrDefault(t => t.Feature == f);
        /// <summary>AO from a constant below 1 (no AO image).</summary>
        public bool ConstantAo => Of(Feature.Ao) is null && Ao < 1f;
    }

    /// <summary>The plan of a resolved material (see <see cref="Surface"/>).</summary>
    public static Surface Of(PbrMaterial m)
    {
        var list = new List<Binding>();
        void Add(Feature f, TextureSlot? s, bool flip = false) { if (s is not null) list.Add(new Binding(f, s.ImageId, s.Channel, flip)); }
        Add(Feature.Albedo, m.BaseColorTexture);
        Add(Feature.Metallic, m.MetallicTexture);
        Add(Feature.Roughness, m.RoughnessTexture);
        Add(Feature.Ao, m.AoTexture);
        Add(Feature.Normal, m.NormalTexture, m.NormalDirectX);
        return new Surface(m.BaseColor, m.Metallic, m.Roughness, m.Ao, m.NormalStrength, m.UvScale, m.UvOffset, list);
    }

    /// <summary>Inverts the green byte of every pixel of RGBA8 data (DirectX normal maps point green down; Godot and glTF want it up).</summary>
    public static void FlipGreenRgba8(Span<byte> rgba)
    {
        for (int i = 1; i < rgba.Length; i += 4) rgba[i] = (byte)(255 - rgba[i]);
    }

    /// <summary>The features a texture of this role feeds and the channel each reads (an ORM picture: AO red, roughness green, metallic blue). Empty for roles a surface does not use.</summary>
    public static IReadOnlyList<(Feature Feature, TextureChannel Channel)> Uses(TextureRole role) => role switch
    {
        TextureRole.Albedo => [(Feature.Albedo, TextureChannel.Rgb)],
        TextureRole.Normal => [(Feature.Normal, TextureChannel.Rgb)],
        TextureRole.Roughness => [(Feature.Roughness, TextureChannel.R)],
        TextureRole.Metallic => [(Feature.Metallic, TextureChannel.R)],
        TextureRole.Occlusion => [(Feature.Ao, TextureChannel.R)],
        TextureRole.Orm => [(Feature.Ao, TextureChannel.R), (Feature.Roughness, TextureChannel.G), (Feature.Metallic, TextureChannel.B)],
        TextureRole.Emissive => [(Feature.Emission, TextureChannel.Rgb)],
        _ => [],
    };

    /// <summary>The DirectX reading of a normal map's name (DX / DirectX in the name, or an unmarked Megascans-style <c>id_4K_Normal</c>: ours, a refine item; see <see cref="ModelImport.UnknownNormalIsDirectX"/>).</summary>
    public static bool IsDirectXNormal(TextureInfo t) => t.Role == TextureRole.Normal && NormalNeedsGreenFlip(t);

    static readonly Dictionary<Feature, string> SurfaceInput = new()
    {
        [Feature.Albedo] = "base-color", [Feature.Metallic] = "metallic", [Feature.Roughness] = "roughness", [Feature.Ao] = "ao", [Feature.Normal] = "normal",
    };

    /// <summary>
    /// The <c>material-edit</c> that wires pictures into a material by their file names (the standalone image import, docs/mapmaker.md "Materials (editor)"): per picture an
    /// image-texture node, linked straight into the PBR surface (base colour, metallic, roughness, AO), through a normal-map node (convention from the name) or through a
    /// separate-rgb node (ORM). A picture whose name names no surface input is added as an unlinked node. Null when nothing is to be added. The graph must have a PBR surface
    /// (<paramref name="surfaceId"/>); existing links into the inputs it feeds are replaced (one link per input). Node ids are picked so they do not clash with <paramref name="existingIds"/>; <paramref name="mappingId"/> (the mapping node the material already uses) feeds every new picture, since a material has one UV mapping.
    /// </summary>
    public static JsonObject? WireByName(string materialId, string surfaceId, IReadOnlyCollection<string> existingIds, IEnumerable<(string ImageId, string FileName)> images, string? mappingId = null, float x0 = -700, float y0 = -200)
    {
        var used = new HashSet<string>(existingIds);
        string Fresh(string stem) { int k = 1; while (used.Contains($"{stem}-{k}")) k++; var id = $"{stem}-{k}"; used.Add(id); return id; }
        var add = new JsonArray(); var link = new JsonArray();
        var taken = new HashSet<string>();
        float y = y0;
        foreach (var (imageId, file) in images)
        {
            var info = ClassifyTexture(file.Contains('.') ? file : file + ".png");
            var uses = info is null ? [] : Uses(info.Role).Where(u => SurfaceInput.ContainsKey(u.Feature) && !taken.Contains(SurfaceInput[u.Feature])).ToList();
            string tex = Fresh("image");
            add.Add(Node(tex, NodeTypes.ImageTexture, new JsonObject { ["image"] = imageId }, x0, y));
            if (mappingId is not null) link.Add(Link(mappingId, "vector", tex, "vector"));   // one mapping per material: the new pictures share the one already used
            if (uses.Count == 1 && uses[0].Feature == Feature.Normal)
            {
                string nm = Fresh("normal-map");
                var ps = new JsonObject();
                if (IsDirectXNormal(info!)) ps["convention"] = "directx";
                add.Add(Node(nm, NodeTypes.NormalMap, ps, x0 + 300, y));
                link.Add(Link(tex, "color", nm, "color"));
                link.Add(Link(nm, "normal", surfaceId, "normal"));
            }
            else if (uses.Count > 1 || uses.Any(u => u.Channel is TextureChannel.G or TextureChannel.B))
            {
                string sep = Fresh("separate-rgb");
                add.Add(Node(sep, NodeTypes.SeparateRgb, new JsonObject(), x0 + 300, y));
                link.Add(Link(tex, "color", sep, "color"));
                foreach (var (f, c) in uses) link.Add(Link(sep, c switch { TextureChannel.G => "g", TextureChannel.B => "b", _ => "r" }, surfaceId, SurfaceInput[f]));
            }
            else foreach (var (f, _) in uses) link.Add(Link(tex, "color", surfaceId, SurfaceInput[f]));
            foreach (var u in uses) taken.Add(SurfaceInput[u.Feature]);
            y += 220;
        }
        if (add.Count == 0) return null;
        var op = new JsonObject { ["op"] = "material-edit", ["id"] = materialId, ["addNodes"] = add };
        if (link.Count > 0) op["link"] = link;
        return op;
    }

    static JsonObject Node(string id, string type, JsonObject ps, float x, float y) => new() { ["id"] = id, ["type"] = type, ["params"] = ps, ["position"] = new JsonArray(x, y) };
    static JsonObject Link(string from, string output, string to, string input) => new() { ["from"] = from, ["out"] = output, ["to"] = to, ["in"] = input };
}
