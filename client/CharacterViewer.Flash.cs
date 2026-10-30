using System;
using Godot;

namespace SmClient;

/// <summary>
/// fx/character-visuals (owner, 2026-10-30, item 13), reworked by fx/visuals-2 (round 2, item 34): the armour flashes on a character. The subtle yellow **hit** flash stays what it was, a rim-weighted additive
/// overlay on the armour pieces. The **recharge glow** (yellowish) and the **break flash** (red) are no longer drawn on the model's surface (the owner: not a layer on the model, not an outline along its edges):
/// each is a soft 2D halo, one billboard quad per character, that projects outward around the figure (<see cref="HaloCode"/>: a ring just outside the silhouette with a soft falloff to both sides and a faint
/// fill, pushed a little behind the character so the body hides its middle). The colours and strengths are **ours** (estimates, refine register). <c>--old-flash-outline</c> restores the model-surface glow
/// and outline of round 1. The placeholder side tint (<see cref="Overlay"/>) is given back when no flash runs.
/// </summary>
public partial class CharacterViewer
{
    /// <summary>Hit flash colour (subtle yellow), break flash colour (red), recharge glow colour (yellowish) and the old outline colour (ours).</summary>
    public static readonly Color HitColour = new(1f, 0.82f, 0.15f), BreakColour = new(1f, 0.12f, 0.08f), GlowColour = new(1f, 0.78f, 0.22f), OutlineColour = new(1f, 0.86f, 0.35f);
    /// <summary>The strengths at full intensity (ours): subtle for the hit; the model-surface break and glow strengths belong to <c>--old-flash-outline</c>.</summary>
    public const float HitStrength = 0.2f, BreakStrength = 0.2f, GlowStrength = 0.4f;
    /// <summary>The halo's brightness at full intensity for the glow and the break (ours), and how far it sits behind the character (m).</summary>
    public const float HaloGlowStrength = 0.75f, HaloBreakStrength = 0.9f, HaloPush = 0.35f;
    /// <summary>The halo quad's size (m: width, height).</summary>
    public static readonly Vector2 HaloSize = new(2.5f, 2.8f);
    /// <summary>The old outline's shell thickness (m, ours).</summary>
    public const float OutlineWidth = 0.028f;

    /// <summary><c>--old-flash-outline</c> (before / after aid): the round-1 glow and break flash on the model's surface with the outline, no halo.</summary>
    public static bool OldFlashOutline { get; set; }

    // The colours are constants in the shader; the intensities are a parameter of the character's own material.
    static string FlashCode(bool old) => $$"""
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_back, fog_disabled;

uniform vec4 flash_mix = vec4(0.0);

void fragment() {
    float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
    float rim = pow(1.0 - facing, 2.0);
    float flash_hit = flash_mix.x;
    float flash_break = flash_mix.y;
    float flash_glow = flash_mix.z;
    vec3 energy = vec3({{F(HitColour.R)}}, {{F(HitColour.G)}}, {{F(HitColour.B)}}) * flash_hit * {{F(HitStrength)}}
                + vec3({{F(BreakColour.R)}}, {{F(BreakColour.G)}}, {{F(BreakColour.B)}}) * flash_break * {{F(old ? BreakStrength : 0f)}}
                + vec3({{F(GlowColour.R)}}, {{F(GlowColour.G)}}, {{F(GlowColour.B)}}) * flash_glow * {{F(old ? GlowStrength : 0f)}};
    ALBEDO = energy * (0.06 + 1.0 * rim);
}
""";

    static string F(float v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    // The halo: a camera-facing quad (the node's place, the camera's axes), pushed back from the camera so that the body covers its middle. Soft: no edge anywhere.
    static readonly string HaloCode = $$"""
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_disabled, fog_disabled;

uniform vec4 halo_mix = vec4(0.0);   // x: break, y: glow
uniform float push = {{F(HaloPush)}};

void vertex() {
    // turns about the vertical only (a camera looking down on the character must not tilt the card into the floor)
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], vec4(0.0, 1.0, 0.0, 0.0), vec4(normalize(cross(INV_VIEW_MATRIX[0].xyz, vec3(0.0, 1.0, 0.0))), 0.0), MODEL_MATRIX[3]);
    MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
    VERTEX.z -= push;
}

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void fragment() {
    vec2 p = vec2(UV.x - 0.5, 0.5 - UV.y) * 2.0;
    float r = length(p);
    float ang = atan(p.y, p.x);
    float wob = 0.6 + 0.4 * vnoise(vec2(ang * 2.0 + TIME * 0.7, TIME * 1.3));   // a slow shimmer along the ring
    float band = exp(-pow((r - 0.78) / 0.17, 2.0));          // brightest just outside the silhouette
    float fill = (1.0 - smoothstep(0.1, 1.0, r)) * 0.25;      // a faint soft fill that dies out at the quad's edge
    float a = (band * 0.85 + fill) * wob * (1.0 - smoothstep(0.92, 1.0, r)) * smoothstep(-0.97, -0.55, p.y);   // dies out toward the feet, so that the floor cuts it where it is already clear
    vec3 energy = vec3({{F(BreakColour.R)}}, {{F(BreakColour.G)}}, {{F(BreakColour.B)}}) * halo_mix.x * {{F(HaloBreakStrength)}}
                + vec3({{F(GlowColour.R)}}, {{F(GlowColour.G)}}, {{F(GlowColour.B)}}) * halo_mix.y * {{F(HaloGlowStrength)}};
    ALBEDO = energy * a;
}
""";

    const string OutlineCode = """
shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_never, cull_front, fog_disabled;

uniform float width = 0.028;
uniform vec3 outline_tint : source_color = vec3(1.0, 0.86, 0.35);
uniform float outline_amount = 0.0;

void vertex() {
    VERTEX += normalize(NORMAL) * width * step(0.001, outline_amount);
}

void fragment() {
    ALBEDO = outline_tint;
    ALPHA = clamp(outline_amount, 0.0, 1.0);
}
""";

    static Shader? _flashShader, _outlineShader, _haloShader;
    ShaderMaterial? _flashMaterial, _outlineMaterial, _haloMaterial;
    MeshInstance3D? _halo;
    int _haloBone = -2;
    bool _flashActive;

    /// <summary>This character's flash overlay (with the old outline as its next pass under <c>--old-flash-outline</c>); the shaders are shared, the materials are one set per character.</summary>
    ShaderMaterial FlashMaterial()
    {
        if (_flashMaterial is not null) return _flashMaterial;
        _flashShader ??= new Shader { Code = FlashCode(OldFlashOutline) };
        _flashMaterial = new ShaderMaterial { Shader = _flashShader };
        if (OldFlashOutline)
        {
            _outlineShader ??= new Shader { Code = OutlineCode };
            _outlineMaterial = new ShaderMaterial { Shader = _outlineShader };
            _outlineMaterial.SetShaderParameter("width", OutlineWidth);
            _outlineMaterial.SetShaderParameter("outline_tint", OutlineColour);
            _flashMaterial.NextPass = _outlineMaterial;
        }
        return _flashMaterial;
    }

    /// <summary>This character's halo quad (made on first use, a child of the viewer at chest height).</summary>
    MeshInstance3D Halo()
    {
        if (_halo is not null && GodotObject.IsInstanceValid(_halo)) return _halo;
        _haloShader ??= new Shader { Code = HaloCode };
        _haloMaterial = new ShaderMaterial { Shader = _haloShader };
        _halo = new MeshInstance3D
        {
            Name = "ArmourHalo", Mesh = new QuadMesh { Size = HaloSize }, MaterialOverride = _haloMaterial, Position = new Vector3(0f, 1.2f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4f, Visible = false,
        };
        AddChild(_halo);
        return _halo;
    }

    /// <summary>Whether the halo is showing (for tests and logs).</summary>
    public bool HaloVisible => _halo is { } h && GodotObject.IsInstanceValid(h) && h.Visible;

    readonly System.Collections.Generic.List<MeshInstance3D> _armourMeshes = new();
    int _armourMeshChildren = -1;

    /// <summary>The armour piece meshes, cached; rebuilt only when the skeleton's child count changes (a new look, weapons added).</summary>
    System.Collections.Generic.List<MeshInstance3D> ArmourMeshes()
    {
        int count = _skeleton!.GetChildCount();
        bool stale = count != _armourMeshChildren;
        foreach (var m in _armourMeshes) if (!GodotObject.IsInstanceValid(m)) { stale = true; break; }
        if (!stale) return _armourMeshes;
        _armourMeshChildren = count;
        _armourMeshes.Clear();
        foreach (var child in _skeleton.GetChildren()) if (child is MeshInstance3D mesh && mesh.HasMeta(ArmourMeta)) _armourMeshes.Add(mesh);
        return _armourMeshes;
    }

    /// <summary>Whether a flash is on the model now (for tests and logs).</summary>
    public bool FlashActive => _flashActive;

    /// <summary>
    /// Sets the flashes of this frame (each 0..1; <paramref name="hit"/> yellow on the armour, <paramref name="broke"/> the red halo, <paramref name="glow"/> the yellowish recharge halo). All zero
    /// puts the model back (the side tint) and hides the halo. Cheap while nothing runs.
    /// </summary>
    public void SetArmourFlash(float hit, float broke, float glow)
    {
        if (_skeleton is null) return;
        bool old = OldFlashOutline;
        float amount = Math.Max(Math.Max(hit * HitStrength, broke * (old ? BreakStrength : HaloBreakStrength)), glow * (old ? GlowStrength : HaloGlowStrength));
        if (amount <= 0.001f)
        {
            if (!_flashActive) return;
            _flashActive = false;
            foreach (var mesh in ArmourMeshes()) mesh.MaterialOverlay = Overlay;
            _flashMaterial?.SetShaderParameter("flash_mix", new Vector4(0f, 0f, 0f, 0f));
            _outlineMaterial?.SetShaderParameter("outline_amount", 0f);
            if (_halo is not null && GodotObject.IsInstanceValid(_halo)) _halo.Visible = false;
            return;
        }
        _flashActive = true;
        if (old || hit > 0.001f)
        {
            var material = FlashMaterial();
            material.SetShaderParameter("flash_mix", new Vector4(hit, broke, glow, 1f));
            _outlineMaterial?.SetShaderParameter("outline_amount", glow);
            foreach (var mesh in ArmourMeshes()) if (!ReferenceEquals(mesh.MaterialOverlay, material)) mesh.MaterialOverlay = material;   // also the pieces a new look added since
        }
        else if (_flashMaterial is not null)
        {
            _flashMaterial.SetShaderParameter("flash_mix", new Vector4(0f, 0f, 0f, 1f));
        }
        if (!old)
        {
            var halo = Halo();
            bool show = broke > 0.001f || glow > 0.001f;
            if (halo.Visible != show) halo.Visible = show;
            if (show)
            {
                _haloMaterial!.SetShaderParameter("halo_mix", new Vector4(broke, glow, 0f, 1f));
                // The model's body is not at the viewer's origin (the rig's root is offset): centre the halo on the lower spine, where the body is.
                if (_haloBone == -2) _haloBone = _skeleton.FindBone("RIG_SpineA");
                if (_haloBone >= 0) halo.GlobalPosition = _skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(_haloBone).Origin) + Vector3.Up * 0.12f;
            }
        }
    }
}
