using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// The owner's "option in Graphics" (2026-11-08, musou/perf-pass): Ashfall's grime folded into derived kit materials, for weak GPUs (Options > Graphics > Ashfall grime: FAST; default EXACT, the overlay).
/// Each kit surface's standard material gets a derived shader (its common features, rebuilt) with the overlay's grime and the multiply pass worked into its albedo and roughness at the end of its fragment, so the
/// models draw once instead of three times. Not exact: the overlay's grime was lit on its own (no normal map, no specular) and the multiply came after the light and the fog; folded, both go under the
/// surface's own lighting. A surface whose material is not a standard one keeps the overlay. Measured costs and captures: docs/musou-perf-pass.md.
/// </summary>
public static partial class AshfallGrime
{
    static bool _folded = Array.IndexOf(OS.GetCmdlineUserArgs(), "--ashfall-grime-folded") >= 0 || FoldedForSafeMode;   // P1: folded from the start in safe mode
    static readonly Dictionary<ulong, Shader> FoldedShaders = new();                                    // the material's feature set -> derived shader
    static readonly Dictionary<Material, ShaderMaterial> FoldedMaterials = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<MeshInstance3D, Material?[]> Originals = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether the grime is folded into the kit's materials (the Graphics option, or FSR 2 upscaling; <c>--ashfall-grime-folded</c> forces it at start).</summary>
    public static bool Folded => _folded;

    /// <summary>The Graphics option's choice (FAST); <see cref="Folded"/> is also true while FSR 2 upscales.</summary>
    static bool _foldChoice = Array.IndexOf(OS.GetCmdlineUserArgs(), "--ashfall-grime-folded") >= 0;
    static bool _foldForFsr;

    /// <summary>The Graphics option: folds the grime into the kit's materials, or puts the overlay back; applies at once to the kit loaded now and to models that load later.</summary>
    public static void SetFolded(bool folded) { _foldChoice = folded; ApplyFold(); }

    /// <summary>
    /// hotfix/ashfall-floor-shimmer: while the viewport upscales with FSR 2 the overlay passes shimmered on the far floor even on a still camera (still-camera flicker on the floor 0.58 % of pixels against 0.08 %
    /// folded and 0.05 % with MSAA; the overlay is a transparent pass the upscaler's accumulation does not follow), so the grime is folded then whatever the option says, and goes back with it.
    /// </summary>
    static void SetFoldForFsr(bool fsr) { if (_foldForFsr == fsr) return; _foldForFsr = fsr; ApplyFold(); }

    /// <summary>
    /// polish pass P1 (2026-11-10, the owner's "Compatibility mode must stay smooth on GTX 9xx"): in safe mode (the Compatibility renderer or <c>--safe-mode</c>) the grime is folded
    /// whatever the Graphics option says (the overlay pass cost 0.5 to 1.2 ms of GPU and up to 580 draw calls in the perf pass's A/B, the part that renderer pays most for);
    /// <c>--ashfall-grime-exact-safe</c> keeps the option's choice in safe mode (the behaviour before P1).
    /// </summary>
    public static bool FoldedForSafeMode => SafeMode.GrimeFoldedInSafeMode;

    static void ApplyFold()
    {
        bool folded = _foldChoice || _foldForFsr || FoldedForSafeMode;
        if (_folded == folded) return;
        _folded = folded;
        if (_items is null || !GodotObject.IsInstanceValid(_items) || _top is null) return;
        void Set(Node n) { if (n is MeshInstance3D mi) Dress(mi); foreach (var c in n.GetChildren()) Set(c); }
        foreach (var child in _items.GetChildren()) Set(child);
    }

    /// <summary>One kit model: the overlay (exact) or its folded materials (fast), as the option says.</summary>
    static void Dress(MeshInstance3D mi)
    {
        if (!_overlayOn) { Restore(mi); mi.MaterialOverlay = null; return; }
        if (!_folded || _top != _overlay)   // the fold is of the full overlay (Medium and up, Forward+); the cheap multiply alone stays an overlay
        {
            Restore(mi);
            mi.MaterialOverlay = _top;
            return;
        }
        if (Fold(mi)) mi.MaterialOverlay = null;
        else { Restore(mi); mi.MaterialOverlay = _top; }
    }

    static void Restore(MeshInstance3D mi)
    {
        if (!Originals.Remove(mi, out var before) || !GodotObject.IsInstanceValid(mi)) return;
        for (int i = 0; i < before.Length && i < mi.GetSurfaceOverrideMaterialCount(); i++) mi.SetSurfaceOverrideMaterial(i, before[i]);
    }

    /// <summary>Every surface of <paramref name="mi"/> to its folded material; false (nothing changed) when one cannot be folded.</summary>
    static bool Fold(MeshInstance3D mi)
    {
        if (Originals.ContainsKey(mi)) return true;
        int n = mi.GetSurfaceOverrideMaterialCount();
        if (n == 0) return false;
        var folded = new ShaderMaterial[n];
        for (int i = 0; i < n; i++)
        {
            if (mi.GetActiveMaterial(i) is not BaseMaterial3D source || FoldedMaterial(source) is not { } f) return false;
            folded[i] = f;
        }
        var before = new Material?[n];
        for (int i = 0; i < n; i++) { before[i] = mi.GetSurfaceOverrideMaterial(i); mi.SetSurfaceOverrideMaterial(i, folded[i]); }
        Originals[mi] = before;
        return true;
    }

    static ShaderMaterial? FoldedMaterial(BaseMaterial3D source)
    {
        if (FoldedMaterials.TryGetValue(source, out var known)) return known;
        // What the derived shader reproduces (the kit's imported and triplanar materials): albedo colour and texture, vertex colour, the normal map, roughness, metallic and occlusion (value and
        // texture), UV scale and offset, triplanar mapping and the cull mode. Anything else (transparency, emission, another shading mode, other features) keeps the overlay.
        if (source.Transparency != BaseMaterial3D.TransparencyEnum.Disabled || source.EmissionEnabled || source.ShadingMode != BaseMaterial3D.ShadingModeEnum.PerPixel
            || source.DetailEnabled || source.HeightmapEnabled || source.RimEnabled || source.ClearcoatEnabled || source.AnisotropyEnabled || source.SubsurfScatterEnabled || source.RefractionEnabled)
            return null;
        bool triplanar = source.Uv1Triplanar;
        ulong key = (triplanar ? 1UL : 0) | (source.Uv1WorldTriplanar ? 2UL : 0) | (source.NormalEnabled ? 4UL : 0) | (source.AOEnabled ? 8UL : 0) | ((ulong)source.CullMode << 4);
        if (!FoldedShaders.TryGetValue(key, out var shader))
            FoldedShaders[key] = shader = new Shader { Code = FoldedShaderCode(triplanar, source.Uv1WorldTriplanar, source.NormalEnabled, source.AOEnabled, source.CullMode) };
        var m = new ShaderMaterial { Shader = shader, RenderPriority = source.RenderPriority };
        m.SetShaderParameter("albedo", source.AlbedoColor);
        m.SetShaderParameter("albedo_vertex", source.VertexColorUseAsAlbedo);
        m.SetShaderParameter("texture_albedo", source.AlbedoTexture);
        m.SetShaderParameter("has_albedo", source.AlbedoTexture is not null);
        m.SetShaderParameter("roughness", source.Roughness);
        m.SetShaderParameter("texture_roughness", source.RoughnessTexture);
        m.SetShaderParameter("has_roughness", source.RoughnessTexture is not null);
        m.SetShaderParameter("roughness_channel", Channel(source.RoughnessTextureChannel));
        m.SetShaderParameter("metallic", source.Metallic);
        m.SetShaderParameter("specular", source.MetallicSpecular);
        m.SetShaderParameter("texture_metallic", source.MetallicTexture);
        m.SetShaderParameter("has_metallic", source.MetallicTexture is not null);
        m.SetShaderParameter("metallic_channel", Channel(source.MetallicTextureChannel));
        m.SetShaderParameter("texture_normal", source.NormalTexture);
        m.SetShaderParameter("normal_scale", source.NormalScale);
        m.SetShaderParameter("texture_ao", source.AOTexture);
        m.SetShaderParameter("ao_channel", Channel(source.AOTextureChannel));
        m.SetShaderParameter("ao_light_affect", source.AOLightAffect);
        m.SetShaderParameter("uv1_scale", source.Uv1Scale);
        m.SetShaderParameter("uv1_offset", source.Uv1Offset);
        m.SetShaderParameter("uv1_blend_sharpness", source.Uv1TriplanarSharpness);
        var overlay = OverlayMaterial();
        var amount = overlay.GetShaderParameter("amount");
        var wash = overlay.GetShaderParameter("wash");
        m.SetShaderParameter("grime_amount", amount.VariantType == Variant.Type.Nil ? 1f : amount);
        m.SetShaderParameter("grime_wash", wash.VariantType == Variant.Type.Nil ? 0f : wash);
        m.SetShaderParameter("grime_darken", Darken);
        FoldedMaterials[source] = m;
        return m;
    }

    static Vector4 Channel(BaseMaterial3D.TextureChannel c) => c switch
    {
        BaseMaterial3D.TextureChannel.Green => new Vector4(0, 1, 0, 0), BaseMaterial3D.TextureChannel.Blue => new Vector4(0, 0, 1, 0),
        BaseMaterial3D.TextureChannel.Alpha => new Vector4(0, 0, 0, 1), BaseMaterial3D.TextureChannel.Grayscale => new Vector4(0.333333f, 0.333333f, 0.333333f, 0), _ => new Vector4(1, 0, 0, 0),
    };

    /// <summary>The derived kit shader: a standard material's common features, then the grime at the end of its fragment.</summary>
    public static string FoldedShaderCode(bool triplanar, bool world, bool normal, bool ao, BaseMaterial3D.CullModeEnum cull)
    {
        string cullMode = cull switch { BaseMaterial3D.CullModeEnum.Disabled => "cull_disabled", BaseMaterial3D.CullModeEnum.Front => "cull_front", _ => "cull_back" };
        var sb = new System.Text.StringBuilder();
        sb.Append("shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, " + cullMode + ", diffuse_burley, specular_schlick_ggx;\n");
        sb.Append(FoldedUniforms);
        sb.Append(FoldedHelpers);
        sb.Append("void vertex() {\n");
        if (triplanar)
        {
            sb.Append(world ? "\tvec3 n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);\n\tuv1_triplanar_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz * uv1_scale + uv1_offset;\n"
                            : "\tvec3 n = NORMAL;\n\tuv1_triplanar_pos = VERTEX * uv1_scale + uv1_offset;\n");
            sb.Append("\tuv1_power_normal = pow(abs(n), vec3(uv1_blend_sharpness));\n\tuv1_power_normal /= dot(uv1_power_normal, vec3(1.0));\n\tuv1_triplanar_pos *= vec3(1.0, -1.0, 1.0);\n");
        }
        else sb.Append("\tUV = UV * uv1_scale.xy + uv1_offset.xy;\n");
        sb.Append("}\n\nvec4 tex1(sampler2D s, vec2 uv) {\n");
        sb.Append(triplanar
            ? "\treturn texture(s, uv1_triplanar_pos.zy) * uv1_power_normal.x + texture(s, uv1_triplanar_pos.xz) * uv1_power_normal.y + texture(s, uv1_triplanar_pos.xy * vec2(-1.0, 1.0)) * uv1_power_normal.z;\n"
            : "\treturn texture(s, uv);\n");
        sb.Append("}\n\nvoid fragment() {\n");
        sb.Append("\tvec4 a = has_albedo ? tex1(texture_albedo, UV) : vec4(1.0);\n\tALBEDO = albedo.rgb * a.rgb;\n\tif (albedo_vertex) ALBEDO *= COLOR.rgb;\n");
        sb.Append("\tMETALLIC = has_metallic ? dot(tex1(texture_metallic, UV), metallic_channel) * metallic : metallic;\n\tSPECULAR = specular;\n");
        sb.Append("\tROUGHNESS = has_roughness ? dot(tex1(texture_roughness, UV), roughness_channel) * roughness : roughness;\n");
        if (normal) sb.Append("\tNORMAL_MAP = tex1(texture_normal, UV).rgb;\n\tNORMAL_MAP_DEPTH = normal_scale;\n");
        if (ao) sb.Append("\tAO = dot(tex1(texture_ao, UV), ao_channel);\n\tAO_LIGHT_AFFECT = ao_light_affect;\n");
        sb.Append(FoldedFragment);
        sb.Append("}\n");
        return sb.ToString();
    }

    const string FoldedUniforms = """
uniform vec4 albedo : source_color = vec4(1.0);
uniform bool albedo_vertex = false;
uniform sampler2D texture_albedo : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_albedo = false;
uniform float roughness : hint_range(0.0, 1.0) = 1.0;
uniform sampler2D texture_roughness : hint_roughness_r, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_roughness = false;
uniform vec4 roughness_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float specular : hint_range(0.0, 1.0) = 0.5;
uniform sampler2D texture_metallic : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_metallic = false;
uniform vec4 metallic_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform sampler2D texture_normal : hint_roughness_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float normal_scale = 1.0;
uniform sampler2D texture_ao : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 ao_channel = vec4(1.0, 0.0, 0.0, 0.0);
uniform float ao_light_affect = 0.0;
uniform vec3 uv1_scale = vec3(1.0);
uniform vec3 uv1_offset = vec3(0.0);
uniform float uv1_blend_sharpness = 1.0;
varying vec3 uv1_triplanar_pos;
varying vec3 uv1_power_normal;

""";

    const string FoldedHelpers = """
uniform float grime_amount = 1.0;
uniform float grime_wash = 0.0;
uniform bool grime_darken = true;
uniform float grime_floor_dark = 0.82;   // FAST's extra darkening of floors and tops (ours, tuned to EXACT's luminance)
float grime_h21(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float grime_vn(vec2 p) {
	vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(grime_h21(i), grime_h21(i + vec2(1, 0)), f.x), mix(grime_h21(i + vec2(0, 1)), grime_h21(i + vec2(1, 1)), f.x), f.y);
}
float grime_fbm4(vec2 p) { float v = 0.0; float a = 0.5; for (int i = 0; i < 4; i++) { v += a * grime_vn(p); p = p * 2.07 + vec2(3.1, 7.7); a *= 0.5; } return v; }
float grime_fbm3(vec2 p) { float v = 0.0; float a = 0.5; for (int i = 0; i < 3; i++) { v += a * grime_vn(p); p = p * 2.07 + vec2(3.1, 7.7); a *= 0.5; } return v / 0.875; }

""";

    // The overlay (OverlayShader) and the multiply (DarkenShader) of AshfallGrime.cs, the same noise, put on the surface's albedo: the multiply first, then the overlay's colour mixed in by its alpha,
    // the roughness towards 1 where the grime covers. The multiply's fade to white in the fog is left to the surface's own fog.
    const string FoldedFragment = """

	{
		vec3 grime_wpos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 grime_wnor = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
		float up = grime_wnor.y;
		float side = 1.0 - abs(up);
		vec2 along = abs(up) > 0.7 ? grime_wpos.xz : (abs(grime_wnor.x) > abs(grime_wnor.z) ? grime_wpos.zy : grime_wpos.xy);
		if (grime_darken) {
			float m = mix(0.78, 1.05, smoothstep(0.25, 0.75, grime_fbm3(along * 0.14 + 1.3)));
			m *= 1.0 - 0.3 * smoothstep(0.58, 0.8, grime_fbm3(along * 0.6 + 8.0));
			m *= 1.0 - 0.22 * smoothstep(0.55, 0.92, grime_vn(vec2(dot(grime_wpos.xz, vec2(1.7, 1.3)) * 2.2, grime_wpos.y * 0.12))) * side;
			m *= 1.0 - 0.25 * (1.0 - smoothstep(0.0, 2.5, grime_wpos.y)) * side;
			m *= 1.0 + 0.12 * (grime_vn(along * 3.3 + 11.0) - 0.5);
			ALBEDO *= vec3(0.90, 0.92, 0.97) * m;
		}
		float blot = grime_fbm4(along * 0.35 + 1.3);
		float soot = smoothstep(0.46, 0.76, blot) * 0.5;
		float streak = smoothstep(0.55, 0.92, grime_vn(vec2(dot(grime_wpos.xz, vec2(1.7, 1.3)) * 2.2, grime_wpos.y * 0.12))) * side * 0.35;
		float low = (1.0 - smoothstep(0.0, 3.5, grime_wpos.y)) * side * (0.35 + 0.35 * grime_fbm4(along * 1.3));
		float grit = (grime_fbm4(along * 3.1 + 11.0) - 0.5) * 0.3;
		float film = smoothstep(0.5, 0.95, up) * (0.22 + 0.2 * grime_fbm4(along * 0.9 + 5.0));
		float s = clamp(soot + streak + low + grit + film, 0.0, 0.7);
		float ash = smoothstep(0.6, 0.95, up) * smoothstep(0.62, 0.85, grime_fbm4(grime_wpos.xz * 0.5 + 9.0)) * 0.35;
		float far = 1.0 - 0.4 * smoothstep(60.0, 180.0, length(VERTEX));
		vec3 dirt = mix(vec3(0.045, 0.042, 0.038), vec3(0.11, 0.10, 0.09), film / max(s, 1e-3));
		vec3 col = mix(dirt, vec3(0.34, 0.33, 0.32), clamp(ash / max(ash + s, 1e-3), 0.0, 1.0));
		float a = clamp(max(s, ash), 0.0, 0.7) * grime_amount * far;
		float w = grime_wash * smoothstep(0.5, 0.95, up) * (0.25 + 0.3 * grime_fbm4(along * 0.6 + 2.0)) * grime_amount;
		float total = a + w * (1.0 - a);
		vec3 over = (col * a + vec3(0.10, 0.102, 0.11) * w * (1.0 - a)) / max(total, 1e-3);
		ALBEDO = mix(ALBEDO, over, total);
		ROUGHNESS = mix(ROUGHNESS, 1.0, total);
		SPECULAR *= 1.0 - total;   // the overlay drew its grime with no specular
		ALBEDO *= mix(1.0, grime_floor_dark, smoothstep(0.5, 0.95, up));   // the owner, 2026-11-08: "make FAST darker" (floors and tops lit brighter folded than under the overlay; tuned on the paired captures)
	}
""";
}
