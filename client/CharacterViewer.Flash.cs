using System;
using Godot;

namespace SmClient;

/// <summary>
/// fx/character-visuals (owner, 2026-10-30, item 13): the armour flashes on a character's model, drawn as one overlay on its armour pieces (additive rim glow, tinted) with a second pass
/// that draws the strong outline of the recharge glow (an inflated back-face shell). The colours and strengths are **ours** (estimates, refine register). The shaders are
/// shared, each character has its own pair of materials (the intensities are their parameters). The placeholder side tint (<see cref="Overlay"/>) is given back when no flash runs.
/// </summary>
public partial class CharacterViewer
{
    /// <summary>Hit flash colour (subtle yellow), break flash colour (subtle red), recharge glow colour (yellowish) and outline colour (ours).</summary>
    public static readonly Color HitColour = new(1f, 0.82f, 0.15f), BreakColour = new(1f, 0.12f, 0.08f), GlowColour = new(1f, 0.78f, 0.22f), OutlineColour = new(1f, 0.86f, 0.35f);
    /// <summary>The strengths at full intensity (ours): subtle for the hit and the break, strong for the glow.</summary>
    public const float HitStrength = 0.2f, BreakStrength = 0.2f, GlowStrength = 0.4f;
    /// <summary>The outline's shell thickness (m, ours).</summary>
    public const float OutlineWidth = 0.028f;

    // The colours are constants in the shader; the three intensities are a parameter of the character's own material.
    static readonly string FlashCode = $$"""
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
                + vec3({{F(BreakColour.R)}}, {{F(BreakColour.G)}}, {{F(BreakColour.B)}}) * flash_break * {{F(BreakStrength)}}
                + vec3({{F(GlowColour.R)}}, {{F(GlowColour.G)}}, {{F(GlowColour.B)}}) * flash_glow * {{F(GlowStrength)}};
    ALBEDO = energy * (0.06 + 1.0 * rim);
}
""";

    static string F(float v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

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

    static Shader? _flashShader, _outlineShader;
    ShaderMaterial? _flashMaterial, _outlineMaterial;
    bool _flashActive;

    /// <summary>This character's flash overlay (with the outline as its next pass); the shaders are shared, the materials are one pair per character.</summary>
    ShaderMaterial FlashMaterial()
    {
        if (_flashMaterial is not null) return _flashMaterial;
        _flashShader ??= new Shader { Code = FlashCode };
        _outlineShader ??= new Shader { Code = OutlineCode };
        _outlineMaterial = new ShaderMaterial { Shader = _outlineShader };
        _outlineMaterial.SetShaderParameter("width", OutlineWidth);
        _outlineMaterial.SetShaderParameter("outline_tint", OutlineColour);
        _flashMaterial = new ShaderMaterial { Shader = _flashShader, NextPass = _outlineMaterial };
        return _flashMaterial;
    }

    /// <summary>Whether a flash is on the model now (for tests and logs).</summary>
    public bool FlashActive => _flashActive;

    /// <summary>
    /// Sets the flashes of this frame (each 0..1; <paramref name="hit"/> yellow, <paramref name="broke"/> red, <paramref name="glow"/> the recharge glow with the outline). All zero
    /// puts the model back (the side tint). Cheap while nothing runs.
    /// </summary>
    public void SetArmourFlash(float hit, float broke, float glow)
    {
        if (_skeleton is null) return;
        float amount = Math.Max(Math.Max(hit * HitStrength, broke * BreakStrength), glow * GlowStrength);
        if (amount <= 0.001f)
        {
            if (!_flashActive) return;
            _flashActive = false;
            foreach (var child in _skeleton.GetChildren())
                if (child is MeshInstance3D mesh && mesh.HasMeta(ArmourMeta)) mesh.MaterialOverlay = Overlay;
            _flashMaterial?.SetShaderParameter("flash_mix", new Vector4(0f, 0f, 0f, 0f));
            _outlineMaterial?.SetShaderParameter("outline_amount", 0f);
            return;
        }
        var material = FlashMaterial();
        _flashActive = true;
        material.SetShaderParameter("flash_mix", new Vector4(hit, broke, glow, 1f));
        _outlineMaterial!.SetShaderParameter("outline_amount", glow);
        foreach (var child in _skeleton.GetChildren())
            if (child is MeshInstance3D mesh && mesh.HasMeta(ArmourMeta) && !ReferenceEquals(mesh.MaterialOverlay, material)) mesh.MaterialOverlay = material;   // also the pieces a new look added since
    }
}
