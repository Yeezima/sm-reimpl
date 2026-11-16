using System.Collections.Generic;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// The Librarian's force sword (Musou Mode only; owner's brief 2026-11-07, **ours**, *placeholder*: the power sword model with a stronger glow of another look). It is the power sword's overlay
/// (<see cref="PowerBlade"/>: the same blade span, grip and tip, the same hit flare parameter) with the colour changed to a rainbow iridescence that drifts along the blade and over time, and a core and rim
/// about three times as bright, so it reads as a psychic weapon beside the Assault's blue sword. Only <see cref="NetworkView"/>'s sword frame chooses it, for a player of the Librarian class.
/// </summary>
public static class ForceBlade
{
    const string Code = """
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_back, fog_disabled;

uniform vec3 grip;
uniform vec3 axis;
uniform float blade_from;
uniform float blade_to;
uniform float core = 1.7;
uniform float rim = 4.2;
instance uniform float flare = 0.0;
varying float along;

vec3 hue(float h) {
    vec3 k = vec3(1.0, 2.0 / 3.0, 1.0 / 3.0);
    vec3 p = abs(fract(vec3(h) + k) * 6.0 - 3.0);
    return clamp(p - 1.0, 0.0, 1.0);
}

void vertex() {
    along = dot(VERTEX - grip, axis);
}

void fragment() {
    float f = (along - blade_from) / (blade_to - blade_from);
    float blade = smoothstep(0.0, 0.05, f) * (1.0 - smoothstep(0.96, 1.0, f));
    float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
    float edge = pow(1.0 - facing, 2.0);
    float shimmer = 0.8 + 0.2 * sin(TIME * 9.0 - f * 22.0);
    vec3 rainbow = mix(hue(f * 1.6 - TIME * 0.35 + edge * 0.5), vec3(1.0), 0.18);
    float energy = blade * (core + rim * edge) * shimmer * (1.0 + 3.0 * flare);
    ALBEDO = rainbow * energy + vec3(0.6) * blade * flare * 1.5;
}
""";

    static Shader? _shader;
    static readonly Dictionary<WeaponModel, ShaderMaterial?> Materials = new();

    /// <summary>The overlay for a sword model (cached per model); null when the model lacks the grip or tip locator.</summary>
    public static ShaderMaterial? OverlayFor(WeaponModel model)
    {
        if (Materials.TryGetValue(model, out var known)) return known;
        ShaderMaterial? material = null;
        if (model.Locator(PowerBlade.GripLocator) is { } grip && model.Locator(PowerBlade.TipLocator) is { } tip)
        {
            var g = new Vector3(grip.Position.X, grip.Position.Y, grip.Position.Z);
            var axis = new Vector3(tip.Position.X, tip.Position.Y, tip.Position.Z) - g;
            if (axis.LengthSquared() > 1e-6f)
            {
                _shader ??= new Shader { Code = Code };
                material = new ShaderMaterial { Shader = _shader };
                material.SetShaderParameter("grip", g);
                material.SetShaderParameter("axis", axis.Normalized());
                material.SetShaderParameter("blade_from", PowerBlade.BladeFrom);
                material.SetShaderParameter("blade_to", PowerBlade.BladeTo);
            }
        }
        Materials[model] = material;
        return material;
    }
}
