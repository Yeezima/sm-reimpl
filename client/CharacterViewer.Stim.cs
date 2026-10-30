using System;
using Godot;

namespace SmClient;

/// <summary>
/// fx/visuals-2 (owner's playtest of 2026-10-30, round 2, item 48; client presentation only): a player under a combat stim shows a much bigger red glow around the marine (the item-34 halo, red, 1.6 times its
/// size, pulsing slowly) and a red glowing health cross that floats above the head for the whole stim duration (the data's <c>StimSpec.Lifetime</c>). Everybody sees it. The install's HUD holds
/// no standalone cross or medic icon art that fits a world sprite (the health bar's diamond is a vector shape inside the movie), so the cross is **ours** (a procedural rounded cross with a soft glow),
/// tagged in the refine register; so are the aura's size and colour. <c>--no-stim-glow</c> removes both.
/// </summary>
public partial class CharacterViewer
{
    /// <summary>The stim aura's size (m, ours), the cross's side (m) and its height above the lower spine (m).</summary>
    public static readonly Vector2 StimAuraSize = new(4.0f, 4.5f);
    public const float StimCrossSize = 0.55f, StimCrossHeight = 1.55f;

    static readonly string StimCrossCode = $$"""
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_disabled, fog_disabled;

uniform float amount = 1.0;

void vertex() {
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
    MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
}

float box(vec2 p, vec2 b, float r) { vec2 d = abs(p) - b + r; return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r; }

void fragment() {
    vec2 p = (UV - 0.5) * 2.0;
    float d = min(box(p, vec2(0.30, 0.82), 0.08), box(p, vec2(0.82, 0.30), 0.08));   // the cross: two rounded bars
    float body = 1.0 - smoothstep(-0.04, 0.04, d);
    float glow = exp(-max(d, 0.0) * 5.0) * 0.55;
    float pulse = 0.8 + 0.2 * sin(TIME * 3.0);
    vec3 red = vec3({{F(BreakColour.R)}}, {{F(BreakColour.G * 0.6f)}}, {{F(BreakColour.B * 0.6f)}});
    ALBEDO = (red * (body * 0.95 + glow) + vec3(1.0, 0.3, 0.25) * body * 0.2) * pulse * amount;
}
""";

    static Shader? _stimCrossShader;
    MeshInstance3D? _stimAura, _stimCross;
    ShaderMaterial? _stimAuraMaterial, _stimCrossMaterial;
    float _stimShown;

    /// <summary>Whether the stim glow is showing (for tests and logs).</summary>
    public bool StimGlowVisible => _stimAura is { } a && GodotObject.IsInstanceValid(a) && a.Visible;

    /// <summary>Shows the stim glow at <paramref name="amount"/> (0 off, 1 full; the caller fades it in and out).</summary>
    public void SetStimGlow(float amount)
    {
        if (_skeleton is null) return;
        if (amount <= 0.001f)
        {
            if (_stimShown <= 0f) return;
            _stimShown = 0f;
            if (_stimAura is not null && GodotObject.IsInstanceValid(_stimAura)) _stimAura.Visible = false;
            if (_stimCross is not null && GodotObject.IsInstanceValid(_stimCross)) _stimCross.Visible = false;
            return;
        }
        _stimShown = amount;
        if (_stimAura is null || !GodotObject.IsInstanceValid(_stimAura))
        {
            _haloShader ??= new Shader { Code = HaloCode };
            _stimAuraMaterial = new ShaderMaterial { Shader = _haloShader };
            _stimAuraMaterial.SetShaderParameter("push", HaloPush + 0.25f);
            _stimAura = new MeshInstance3D { Name = "StimAura", Mesh = new QuadMesh { Size = StimAuraSize }, MaterialOverride = _stimAuraMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 4f };
            AddChild(_stimAura);
            _stimCrossShader ??= new Shader { Code = StimCrossCode };
            _stimCrossMaterial = new ShaderMaterial { Shader = _stimCrossShader };
            _stimCross = new MeshInstance3D { Name = "StimCross", Mesh = new QuadMesh { Size = new Vector2(StimCrossSize, StimCrossSize) }, MaterialOverride = _stimCrossMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 2f };
            AddChild(_stimCross);
        }
        _stimAura.Visible = true; _stimCross!.Visible = true;
        double t = Time.GetTicksMsec() / 1000.0;
        float pulse = 0.85f + 0.15f * (float)Math.Sin(t * 2.2);
        _stimAuraMaterial!.SetShaderParameter("halo_mix", new Vector4(amount * pulse, 0f, 0f, 1f));
        _stimCrossMaterial!.SetShaderParameter("amount", amount);
        if (_haloBone == -2) _haloBone = _skeleton.FindBone("RIG_SpineA");
        var centre = _haloBone >= 0 ? _skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(_haloBone).Origin) : GlobalPosition + Vector3.Up * 1.1f;
        _stimAura.GlobalPosition = centre + Vector3.Up * 0.12f;
        _stimCross.GlobalPosition = centre + Vector3.Up * (StimCrossHeight + 0.06f * (float)Math.Sin(t * 2.0));   // a slow bob
    }
}
