using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Fx;
using System.Linq;

namespace SmClient;

/// <summary>
/// fx/impact-pass (the owner, 2026-10-29: the power sword "should glow blue and flare up when hitting an enemy"; **ours**): the power sword's blade glows blue. The model has one
/// material for hilt and blade (<c>powersword</c>: diffuse, normal, specular and light-property maps, no glow map; <c>smtool weapon-parts powersword --markers</c>), so the glow is an
/// additive overlay (<see cref="GeometryInstance3D.MaterialOverlay"/>) on the blade's part of the mesh only: from <see cref="BladeFrom"/> to <see cref="BladeTo"/> metres along the
/// axis from the grip locator <c>RIG_Prop_Rhand</c> toward <c>fx_prefix_03</c> near the tip (measured on both swords with <c>smtool weapon-parts sm_mp_melee_powersword --profile
/// RIG_Prop_Rhand,fx_prefix_03</c>: the guard is 0.05 to 0.25 m, the blade 0.25 to 1.78 m). A soft core glow and a brighter rim, a slow shimmer along the blade, and a flare
/// (<see cref="FlareParameter"/>, an instance parameter: no material per character) that the hit drives. The data's own <c>powersword_idle</c> (blue edge sprites and a light
/// at <c>fx_powersword_swing</c>) and <c>powersword_dmg</c> (the hit's flash and sparks) are played by <see cref="NetworkView"/>. Colour: the data's <c>powersword_idle</c> light
/// (115, 186, 255), on both races (the Chaos sword binds the same effect; no Chaos colour exists in the data).
/// </summary>
public static class PowerBlade
{
    /// <summary>Where the glow starts and ends along the grip-to-tip axis (m, ours: measured on the model, see the class summary).</summary>
    public const float BladeFrom = 0.27f, BladeTo = 1.78f;
    public const string GripLocator = "RIG_Prop_Rhand", TipLocator = "fx_prefix_03";
    /// <summary>The instance parameter the hit flare sets (0: none, 1: full).</summary>
    public const string FlareParameter = "flare";
    /// <summary>The glow's colour: the data's <c>powersword_idle</c> light colour.</summary>
    public static readonly Color Colour = new(115f / 255f, 186f / 255f, 1f);
    /// <summary>How long a hit's flare lasts (s, ours) and its curve: full at once, then out with the square of the time left.</summary>
    public const float FlareSeconds = ImpactRules.SwordFlare.Seconds;
    public static float FlareAt(float since) => ImpactRules.SwordFlare.At(since);   // engine-free, tested

    const string Code = """
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_back, fog_disabled;

uniform vec3 grip;
uniform vec3 axis;
uniform float blade_from;
uniform float blade_to;
uniform vec3 glow_colour : source_color;
uniform float core = 0.55;
uniform float rim = 1.6;
instance uniform float flare = 0.0;
varying float along;

void vertex() {
    along = dot(VERTEX - grip, axis);
}

void fragment() {
    float f = (along - blade_from) / (blade_to - blade_from);
    float blade = smoothstep(0.0, 0.05, f) * (1.0 - smoothstep(0.96, 1.0, f));
    float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
    float edge = pow(1.0 - facing, 2.0);
    float shimmer = 0.82 + 0.18 * sin(TIME * 7.0 - f * 18.0);
    float energy = blade * (core + rim * edge) * shimmer * (1.0 + 3.5 * flare);
    ALBEDO = glow_colour * energy + vec3(0.35, 0.45, 0.6) * blade * flare * 1.5;
}
""";

    static Shader? _shader;
    static readonly Dictionary<WeaponModel, ShaderMaterial?> Materials = new();

    /// <summary>Whether a melee wargear file is a power sword (both races: <c>sm_mp_melee_powersword</c>, <c>csm_mp_melee_powersword</c>).</summary>
    public static bool IsPowerSword(string? wargear) => wargear?.Contains("powersword", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The overlay for a sword model (cached per model); null when the model lacks the grip or tip locator.</summary>
    public static ShaderMaterial? OverlayFor(WeaponModel model)
    {
        if (Materials.TryGetValue(model, out var known)) return known;
        ShaderMaterial? material = null;
        if (model.Locator(GripLocator) is { } grip && model.Locator(TipLocator) is { } tip)
        {
            var g = new Vector3(grip.Position.X, grip.Position.Y, grip.Position.Z);
            var axis = new Vector3(tip.Position.X, tip.Position.Y, tip.Position.Z) - g;
            if (axis.LengthSquared() > 1e-6f)
            {
                _shader ??= new Shader { Code = Code };
                material = new ShaderMaterial { Shader = _shader };
                material.SetShaderParameter("grip", g);
                material.SetShaderParameter("axis", axis.Normalized());
                material.SetShaderParameter("blade_from", BladeFrom);
                material.SetShaderParameter("blade_to", BladeTo);
                material.SetShaderParameter("glow_colour", Colour);
            }
        }
        Materials[model] = material;
        return material;
    }

    /// <summary>Our hit flare's system name (<see cref="BuildFlare"/>).</summary>
    public const string FlareName = "impact_pass_sword_flare";

    /// <summary>
    /// fx/impact-pass (**ours**): the hit's flare, held long enough to be seen: the data's <c>powersword_dmg</c> flashes for 1 ms (a frame), so a camera-facing bloom of its own sprite
    /// (<c>add_point_cool</c>, else <c>flash_spikes</c>) 1.6 m across that fades over 0.2 s, a smaller white core, and a blue light (the glow's colour, 5 m, 0.25 s). Null without the sprite.
    /// </summary>
    public static CSystem? BuildFlare(IEnumerable<CSystem> systems)
    {
        var rings = systems.SelectMany(s => s.Roots.SelectMany(r => r.AllNodes())).OfType<CRing>().Where(r => r.Additive && !r.Material.IsEmpty).ToList();
        var bloom = rings.FirstOrDefault(r => r.Material.Name.Equals("add_point_cool", StringComparison.OrdinalIgnoreCase))
                    ?? rings.FirstOrDefault(r => r.Material.Name.Equals("flash_spikes", StringComparison.OrdinalIgnoreCase));
        if (bloom is null) return null;
        static FxCurve C(params (float T, float V)[] keys) => new(keys.Select(k => new FxKey(k.T, k.V)).ToArray());
        static FxColourCurve Col(params (float T, byte R, byte G, byte B, byte A)[] keys) => new(keys.Select(k => new FxColourKey(k.T, new FxColour(k.R, k.G, k.B, k.A))).ToArray());
        CRing Ring(float radius, float seconds, byte r, byte g, byte b) => new()
        {
            Kind = SmFormats.Fx.FxElementKind.Ring, Name = "flare", Material = bloom.Material, Billboard = true, Duration = seconds, Life = seconds,
            Radius = C((0f, radius), (1f, radius * 0.6f)), Colour = Col((0f, r, g, b, 255), (1f, r, g, b, 0)),
        };
        var light = new CLight
        {
            Kind = SmFormats.Fx.FxElementKind.Light, Name = "flare light", Duration = 0.25f, Life = 0.25f, Colour = Col((0f, 115, 186, 255, 255)),
            Intensity = C((0f, 3f), (1f, 0f)), AttenEnd = C((0f, 5f)), Power = C((0f, 1f)),
        };
        return new CSystem { Name = FlareName, Roots = [Ring(0.8f, 0.2f, 140, 200, 255), Ring(0.3f, 0.12f, 255, 255, 255), light], Lifetime = 0.3f };
    }
}
