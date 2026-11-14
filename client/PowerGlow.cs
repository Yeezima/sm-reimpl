using System.Collections.Generic;
using System.Linq;
using Godot;
using SmFormats.Fx;
using SmClient.ImpactRules;

namespace SmClient;

/// <summary>
/// fx/weapon-visuals (owner, 2026-11-14; **ours**, client only): the Serrated Combat Blade's green power field and the light every power weapon throws (rules: <see cref="PowerWeaponGlow"/>).
/// The field is an additive overlay on the knife's whole mesh (the knife is all blade and a short grip; <see cref="PowerBlade"/>'s glow, whose locators the knife lacks, drawn over the whole mesh): a
/// soft core and a brighter rim, the "green outline", with a slow shimmer, in the knife's green. The light is a looping point light of the thunder hammer's data (<c>thunderhammer_idle_light</c>: 1 m,
/// full intensity) built as an effect so the effect player's pooled lights draw it like the hammer's.
/// </summary>
public static class PowerGlow
{
    const string Code = """
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_back, fog_disabled;

uniform vec3 glow_colour : source_color;
uniform float core = 0.45;
uniform float rim = 2.2;

void fragment() {
    float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
    float edge = pow(1.0 - facing, 2.0);
    float shimmer = 0.85 + 0.15 * sin(TIME * 6.0 + VERTEX.x * 14.0);
    ALBEDO = glow_colour * (core + rim * edge) * shimmer;
}
""";

    static Shader? _shader;
    static ShaderMaterial? _green;

    /// <summary>The knife's green field overlay (one shared material: no per-character state).</summary>
    public static ShaderMaterial GreenField()
    {
        if (_green is not null) return _green;
        _shader ??= new Shader { Code = Code };
        _green = new ShaderMaterial { Shader = _shader };
        var (r, g, b) = PowerWeaponGlow.Rgb(PowerGlowColour.Green);
        _green.SetShaderParameter("glow_colour", new Color(r, g, b));
        return _green;
    }

    /// <summary>The effect names of the two lights (<see cref="BuildLight"/>).</summary>
    public const string BlueLightName = "weapon_visuals_light_blue", GreenLightName = "weapon_visuals_light_green";

    public static string LightName(PowerGlowColour c) => c == PowerGlowColour.Green ? GreenLightName : BlueLightName;

    /// <summary>One looping point light as the hammer's data has it: the colour, attenuation end <see cref="PowerWeaponGlow.LightRange"/>, intensity <see cref="PowerWeaponGlow.LightIntensity"/>, no flicker.</summary>
    public static CSystem BuildLight(PowerGlowColour colour, string name)
    {
        var (r, g, b) = PowerWeaponGlow.Rgb(colour);
        static FxCurve C(float v) => new([new FxKey(0f, v)]);
        var light = new CLight
        {
            Kind = FxElementKind.Light, Name = "light", Duration = 0.5f, Life = 0.5f, Looping = true,
            Colour = new FxColourCurve([new FxColourKey(0f, new FxColour((byte)(r * 255f + 0.5f), (byte)(g * 255f + 0.5f), (byte)(b * 255f + 0.5f), 255))]),
            Intensity = C(PowerWeaponGlow.LightIntensity), AttenEnd = C(PowerWeaponGlow.LightRange), Power = C(1f),
        };
        return new CSystem { Name = name, Roots = [light], Lifetime = null };
    }

    /// <summary>Both lights, for <c>FxPlayer.Preload</c>.</summary>
    public static Dictionary<string, CSystem> Lights() => new()
    {
        [BlueLightName] = BuildLight(PowerGlowColour.Blue, BlueLightName),
        [GreenLightName] = BuildLight(PowerGlowColour.Green, GreenLightName),
    };
}
