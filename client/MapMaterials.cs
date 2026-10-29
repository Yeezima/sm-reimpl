using System;
using System.Collections.Generic;
using Godot;
using SmFormats.Bod;

namespace SmClient;

/// <summary>
/// Builds Godot materials from original <c>.bmat</c> parameters: diffuse, normal, specular and emissive maps plus the
/// specular constants. Evidence: the 144 materials of Manufactorum name <c>NormalMap</c> (133), <c>SpecularMap</c> (19)
/// and <c>EmissiveMap</c> (9) textures, all DXT1; <c>SpecularPower</c>/<c>SpecularColor</c> (122) and
/// <c>DerivedSpecParams</c> (103, "drvspec" shaders: specular derived from the diffuse; the four values are not understood)
/// are constants. Everything beyond the maps is a **provisional approximation** of the original shaders:
/// <list type="bullet">
/// <item>Normal maps are DXT1 RGB (x in R, y in G, z in B), no swizzle; Godot reads x and y and rebuilds z.</item>
/// <item>Roughness: Blinn-Phong exponent n (<c>SpecularPower</c>) to GGX roughness, (2 / (n + 2))^(1/4); with a specular map,
/// the map's luminance scales it (bright = glossy).</item>
/// <item>Specular strength: the luminance of <c>SpecularColor</c> (x 3, clamped to 0.05-0.6) as Godot's specular scale.</item>
/// <item>Emission: <c>EmissiveMap</c> times <c>EmissiveColor</c>.</item>
/// </list>
/// Pass 2 adds, also a **provisional approximation** (the engine's shader code is not available, the inputs are):
/// <list type="bullet">
/// <item>Second layer (53 materials: <c>DiffuseMap2</c>, <c>NormalMap2</c>, <c>SpecularPower2</c>/<c>SpecularColor2</c>, and
/// <c>TileU</c>/<c>TileV</c>, which only these materials have: layer 2's UV tiling): the blend weight is the **vertex colour's
/// alpha** (DCM colour byte 3, 0 to 255; the other three bytes are mostly equal, a grey tint). Alpha 255 (the default white) is layer 1:
/// most vertices of these materials sit near 255 (mean 150-250), so layer 2 is the sparse overlay (dirt, rust, damage).
/// <c>vtxblend</c>: layer 2 weight = 1 - alpha. <c>advvtxbld</c>: <c>AlphaMaskMap</c> is a noise texture that perturbs the weight, and
/// <c>VtxBlendParams</c> (x, y, z, w) are read as contrast, noise amplitude, noise tiling over UV 0 (w unused).
/// <c>mskblend</c>: <c>AlphaMaskMap</c> is the weight itself. Drawn with <see cref="LayeredShader"/>.</item>
/// <item>Alpha cut-out: <c>BlendMode</c> 8 (11 materials; the diffuse is DXT5 with alpha, exactly binary on the floor grate) =
/// alpha scissor at 0.5 (shadows follow); <c>CullMode</c> 0 = two-sided. Whether 8 is an alpha test or a blend is not known.</item>
/// <item>Vertex tint: shaders named <c>vtxtint</c> multiply the diffuse by the vertex colour RGB (see <see cref="TintVertexColours"/>).</item>
/// </list>
/// </summary>
public sealed partial class MapMaterials
{
    readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Texture2D?> _glossTextures = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(bool, bool), Shader> Shaders = new();
    ImageTexture? _flatNormal;

    public int NormalMapped { get; private set; }
    public int Specular { get; private set; }
    public int Emissive { get; private set; }
    public int Layered { get; private set; }
    public int CutOut { get; private set; }
    public int Tinted { get; private set; }

    /// <summary>Multiply by the vertex colour where the shader name has "vtxtint". Test aid: SM_NO_TINT=1 turns it off.</summary>
    public static readonly bool TintVertexColours = System.Environment.GetEnvironmentVariable("SM_NO_TINT") != "1";
    /// <summary>Whether the vertex RGB is gamma-encoded (D3DCOLOR usually is). Test aid: SM_TINT_SRGB=1 / 0 overrides the chosen default.</summary>
    public static readonly bool TintIsSrgb = System.Environment.GetEnvironmentVariable("SM_TINT_SRGB") switch { "1" => true, "0" => false, _ => false };

    /// <summary>The material for <paramref name="bmat"/>; null when its diffuse texture is missing or does not decode.</summary>
    /// <param name="vertexColours">The meshes carry the DCM vertex colours (map meshes do; the character pieces do not).</param>
    /// <param name="normalMaps">False when the mesh lacks normals or tangents: normal maps are then left off.</param>
    public Material? Build(BmatMaterial bmat, IReadOnlyDictionary<string, byte[]> dds, List<string> notes, bool vertexColours = false, bool normalMaps = true)
    {
        if (!bmat.Textures.TryGetValue("DiffuseMap", out var diffuseName) || Load(diffuseName, dds, notes) is not { } diffuse) return null;
        bool cutOut = bmat.BlendMode == 8, twoSided = bmat.CullMode == 0;
        if (vertexColours && bmat.Textures.ContainsKey("DiffuseMap2") && BuildLayered(bmat, diffuse, dds, cutOut, twoSided, normalMaps, notes) is { } layered) return layered;
        var material = new StandardMaterial3D
        {
            AlbedoTexture = diffuse,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        if (cutOut)
        {
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            material.AlphaScissorThreshold = 0.5f;
            RenderQuality.ApplyCutOut(material);   // M40-T7: alpha-to-coverage with MSAA
            CutOut++;
        }
        if (twoSided) material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        // Vertex tint. A StandardMaterial3D also multiplies the alpha by the vertex alpha (the layer weight, not an opacity), which would
        // punch holes in a cut-out; so cut-outs are not tinted here (no Manufactorum cut-out has a single-layer vtxtint shader).
        if (vertexColours && TintVertexColours && !cutOut && bmat.ShaderName?.Contains("vtxtint", StringComparison.Ordinal) == true)
        {
            material.VertexColorUseAsAlbedo = true;
            material.VertexColorIsSrgb = TintIsSrgb;
            Tinted++;
        }

        if (normalMaps && bmat.Textures.TryGetValue("NormalMap", out var normalName) && Load(normalName, dds, notes) is { } normal)
        {
            material.NormalEnabled = true;
            material.NormalTexture = normal;
            NormalMapped++;
        }

        float roughness = RoughnessFor(bmat, "SpecularPower"), specular = SpecularFor(bmat, "SpecularColor");
        if (bmat.Textures.TryGetValue("SpecularMap", out var specName) && Gloss(specName, dds, notes) is { } gloss)
        {
            material.RoughnessTexture = gloss;
            material.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red;
            roughness = Math.Min(1f, roughness * 1.4f);
            specular = Math.Max(specular, 0.4f);
            Specular++;
        }
        material.Roughness = roughness;
        material.MetallicSpecular = specular;

        if (bmat.Textures.TryGetValue("EmissiveMap", out var emissiveName) && Load(emissiveName, dds, notes) is { } emissive)
        {
            var c = bmat.Vectors.TryGetValue("EmissiveColor", out var ec) && ec.Length >= 3 ? new Color(ec[0], ec[1], ec[2]) : Colors.White;
            material.EmissionEnabled = true;
            material.Emission = c;
            material.EmissionTexture = emissive;
            material.EmissionEnergyMultiplier = 1.5f;
            Emissive++;
        }
        return material;
    }

    /// <summary>The two-layer material (see the class remarks); null when layer 2's diffuse is missing.</summary>
    ShaderMaterial? BuildLayered(BmatMaterial bmat, Texture2D diffuse, IReadOnlyDictionary<string, byte[]> dds, bool cutOut, bool twoSided, bool normalMaps, List<string> notes)
    {
        if (Load(bmat.Textures["DiffuseMap2"], dds, notes) is not { } diffuse2) return null;
        string shader = bmat.ShaderName ?? "";
        int mode = shader.Contains("advvtxbld", StringComparison.Ordinal) ? 1 : shader.Contains("mskblend", StringComparison.Ordinal) ? 2 : 0;
        Texture2D? mask = bmat.Textures.TryGetValue("AlphaMaskMap", out var maskName) ? Load(maskName, dds, notes) : null;
        if (mode != 0 && mask is null) mode = 0;
        var flat = _flatNormal ??= ImageTexture.CreateFromImage(FlatNormal());
        Texture2D normal1 = normalMaps && bmat.Textures.TryGetValue("NormalMap", out var n1) && Load(n1, dds, notes) is { } a ? a : flat;
        Texture2D normal2 = normalMaps && bmat.Textures.TryGetValue("NormalMap2", out var n2) && Load(n2, dds, notes) is { } b ? b : flat;
        var material = new ShaderMaterial { Shader = ShaderFor(cutOut, twoSided) };
        material.SetShaderParameter("albedo1", diffuse);
        material.SetShaderParameter("albedo2", diffuse2);
        material.SetShaderParameter("normal1", normal1);
        material.SetShaderParameter("normal2", normal2);
        if (mask is not null) material.SetShaderParameter("mask", mask);
        material.SetShaderParameter("mode", mode);
        material.SetShaderParameter("tile2", new Vector2(bmat.Floats.GetValueOrDefault("TileU", 1f), bmat.Floats.GetValueOrDefault("TileV", 1f)));
        if (bmat.Vectors.TryGetValue("VtxBlendParams", out var p) && p.Length >= 4)
            material.SetShaderParameter("blend_params", new Vector4(Math.Clamp(p[0], 0.5f, 100f), Math.Clamp(p[1], -2f, 2f), Math.Clamp(p[2], 0.1f, 64f), p[3]));
        material.SetShaderParameter("use_tint", TintVertexColours && shader.Contains("vtxtint", StringComparison.Ordinal));
        material.SetShaderParameter("tint_srgb", TintIsSrgb);
        material.SetShaderParameter("rough", new Vector2(RoughnessFor(bmat, "SpecularPower"), RoughnessFor(bmat, "SpecularPower2")));
        material.SetShaderParameter("spec", new Vector2(SpecularFor(bmat, "SpecularColor"), SpecularFor(bmat, "SpecularColor2")));
        Layered++;
        if (cutOut) CutOut++;
        if (normal1 != flat) NormalMapped++;
        return material;
    }

    /// <summary>Material response of a lighting look (<c>--look</c> / <c>--look-set</c> <c>mat.rough</c>, <c>mat.spec</c>: factors on the roughness and specular
    /// scale below, map and character materials alike; 1 = unchanged; ours). Read once.</summary>
    static readonly float RoughScale = MapLightingView.V("mat.rough", 1f, 1f), SpecScale = MapLightingView.V("mat.spec", 1f, 1f);

    static float RoughnessFor(BmatMaterial bmat, string key) =>
        Math.Clamp((bmat.Floats.TryGetValue(key, out var power) && power > 0f ? Math.Clamp(MathF.Pow(2f / (power + 2f), 0.25f), 0.2f, 1f) : 0.8f) * RoughScale, 0.08f, 1f);

    static float SpecularFor(BmatMaterial bmat, string key) =>
        Math.Clamp((bmat.Vectors.TryGetValue(key, out var c) && c.Length >= 3 ? Math.Clamp((0.299f * c[0] + 0.587f * c[1] + 0.114f * c[2]) * 3f, 0.05f, 0.6f) : 0.35f) * SpecScale, 0f, 1f);

    static Image FlatNormal()
    {
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgb8);
        image.SetPixel(0, 0, new Color(0.5f, 0.5f, 1f));
        return image;
    }

    static Shader ShaderFor(bool cutOut, bool twoSided)
    {
        if (Shaders.TryGetValue((cutOut, twoSided), out var cached)) return cached;
        var shader = new Shader { Code = LayeredShader(cutOut, twoSided) };
        Shaders[(cutOut, twoSided)] = shader;
        return shader;
    }

    /// <summary>
    /// Two diffuse and normal layers blended by weight f (see the class remarks): mode 0 f = vertex alpha; mode 1 f = clamp(((alpha +
    /// (noise - 0.5) * amplitude) - 0.5) * contrast + 0.5); mode 2 f = mask. Roughness and specular blend with f; optional vertex tint;
    /// optional alpha scissor from layer 1's alpha. **Provisional approximation.**
    /// </summary>
    public static string LayeredShader(bool cutOut, bool twoSided) => $$"""
        shader_type spatial;
        render_mode {{(twoSided ? "cull_disabled, " : "")}}diffuse_burley, specular_schlick_ggx{{(cutOut ? RenderQuality.CutOutRenderMode : "")}};
        uniform sampler2D albedo1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D albedo2 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D normal1 : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D normal2 : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D mask : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform vec2 tile2 = vec2(1.0);
        uniform int mode = 0;
        uniform vec4 blend_params = vec4(4.0, 1.0, 20.0, 0.0);
        uniform vec2 rough = vec2(0.8);
        uniform vec2 spec = vec2(0.35);
        uniform bool use_tint = true;
        uniform bool tint_srgb = false;
        void fragment() {
            vec2 uv2 = UV * tile2;
            vec4 c1 = texture(albedo1, UV);
            vec4 c2 = texture(albedo2, uv2);
            float w = 1.0 - COLOR.a;   // layer 2's weight; vertex alpha 255 (the default white) selects layer 1
            float f = w;
            if (mode == 1) {
                float m = texture(mask, UV * blend_params.z).r;
                f = clamp(((w + (m - 0.5) * blend_params.y) - 0.5) * blend_params.x + 0.5, 0.0, 1.0);
            } else if (mode == 2) {
                f = texture(mask, UV).r;
            }
            vec3 albedo = mix(c1.rgb, c2.rgb, f);
            if (use_tint) albedo *= tint_srgb ? pow(COLOR.rgb, vec3(2.2)) : COLOR.rgb;
            ALBEDO = albedo;
            NORMAL_MAP = mix(texture(normal1, UV).xyz, texture(normal2, uv2).xyz, f);
            ROUGHNESS = mix(rough.x, rough.y, f);
            SPECULAR = mix(spec.x, spec.y, f);
        {{(cutOut ? "    ALPHA = c1.a;\n    ALPHA_SCISSOR_THRESHOLD = 0.5;" + RenderQuality.CutOutShaderLines("albedo1") : "")}}
        }
        """;

    Texture2D? Load(string name, IReadOnlyDictionary<string, byte[]> dds, List<string> notes)
    {
        if (_textures.TryGetValue(name, out var cached)) return cached;
        Texture2D? texture = null;
        if (dds.TryGetValue(name, out var bytes))
        {
            var image = new Image();
            var err = image.LoadDdsFromBuffer(bytes);
            texture = err == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
            if (texture is null) notes.Add($"texture '{name}' failed to decode: {err}");
        }
        else notes.Add($"texture '{name}' is not in the archive");
        _textures[name] = texture;
        return texture;
    }

    /// <summary>A specular map as a roughness texture: 1 - luminance, one byte per pixel (so bright specular = low roughness).</summary>
    Texture2D? Gloss(string name, IReadOnlyDictionary<string, byte[]> dds, List<string> notes)
    {
        if (_glossTextures.TryGetValue(name, out var cached)) return cached;
        Texture2D? texture = null;
        if (dds.TryGetValue(name, out var bytes))
        {
            var image = new Image();
            if (image.LoadDdsFromBuffer(bytes) == Error.Ok)
            {
                if (image.IsCompressed()) image.Decompress();
                image.ClearMipmaps();   // the first level only: the roughness map builds its own mipmaps
                image.Convert(Image.Format.Rgba8);
                var rgba = image.GetData();
                var gloss = new byte[rgba.Length / 4];
                for (int i = 0; i < gloss.Length; i++)
                {
                    float luma = (0.299f * rgba[4 * i] + 0.587f * rgba[4 * i + 1] + 0.114f * rgba[4 * i + 2]) / 255f;
                    gloss[i] = (byte)Math.Clamp((int)MathF.Round((1f - 0.7f * luma) * 255f), 0, 255);
                }
                var roughness = Image.CreateFromData(image.GetWidth(), image.GetHeight(), false, Image.Format.R8, gloss);
                roughness.GenerateMipmaps();
                texture = ImageTexture.CreateFromImage(roughness);
            }
            else notes.Add($"texture '{name}' failed to decode");
        }
        else notes.Add($"specular map '{name}' is not in the archive; the constants are used");
        _glossTextures[name] = texture;
        return texture;
    }
}
