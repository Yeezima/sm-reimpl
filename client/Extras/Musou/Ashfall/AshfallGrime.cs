using System;
using Godot;

namespace SmClient;

/// <summary>
/// Grime for Ashfall's concept pass (the owner, 2026-11-06: "add varied dirt, ash and grime across surfaces so the battlefield no longer looks clean. Use reversible overlays, decals or material effects rather than
/// permanently recoloring the original assets"). Two layers, both runtime material effects (nothing on disk changes; <c>--ashfall-grime off</c> or <c>--ashfall-concept off</c> leaves them out):
/// <list type="bullet">
/// <item>the ground and boundary shaders' own <c>grime</c> amount (<see cref="AshfallKitView"/>'s ground and masonry shaders): soot blotches at about 9 and 2 m, pale ash drifts, fine grit; every renderer;</item>
/// <item>a <see cref="GeometryInstance3D.MaterialOverlay"/> on every kit model (<see cref="OverlayShader"/>): soot gathering low on walls and in blotches, rain-and-soot streaks down vertical faces, ash on upward faces.
/// One more transparent pass over the models, so only from the Medium lighting preset up and never on the Compatibility renderer (<see cref="Overlay"/>).</item>
/// </list>
/// Every value is ours (placeholder demonstration, refine register).
/// </summary>
public static class AshfallGrime
{
    /// <summary>Whether the command line leaves the grime out.</summary>
    public static bool Off
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--ashfall-grime");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "off";
        }
    }

    /// <summary>The ground shaders' grime amount (0 none, 1 full; ours).</summary>
    public const float GroundAmount = 1f;

    /// <summary>Whether the models get the overlay pass at this quality (the storm's scale: 1 at Medium) and renderer.</summary>
    public static bool Overlay(float quality, bool compat) => !compat && quality >= 0.9f && !Off;

    static ShaderMaterial? _overlay;

    /// <summary>The floor wash of the concept light pass (0 to 1; ours): set on the shared overlay by <see cref="AshfallStorm"/>.</summary>
    public const float LightPassWash = 1f;

    /// <summary>The models' overlay material (one, shared).</summary>
    public static ShaderMaterial OverlayMaterial() => _overlay ??= new ShaderMaterial { Shader = new Shader { Code = OverlayShader } };

    /// <summary>Applies the grime to the kit look under <paramref name="root"/> (the map's game space); returns how many ground materials took it. Models that load later get the overlay as they arrive.</summary>
    public static int Apply(Node? root, float quality, bool compat)
    {
        if (Off || root?.FindChild("KitLook", true, false) is not Node kit) return 0;
        int ground = 0;
        if (kit.GetNodeOrNull("Remainder") is { } remainder)
            foreach (var child in remainder.GetChildren())
                if (child is MeshInstance3D { MaterialOverride: ShaderMaterial sm } && sm.Shader?.Code.Contains("uniform float grime") == true)
                {
                    sm.SetShaderParameter("grime", GroundAmount);
                    ground++;
                }
        if (Overlay(quality, compat) && kit.GetNodeOrNull("Items") is { } items)
        {
            var overlay = OverlayMaterial();
            foreach (var child in items.GetChildren())
                if (child is MeshInstance3D mi) mi.MaterialOverlay = overlay;
            items.ChildEnteredTree += n => { if (n is MeshInstance3D mi) mi.MaterialOverlay = overlay; };
        }
        return ground;
    }

    /// <summary>
    /// The models' overlay: world-space noise, so it continues across pieces and never shows the models' UV seams. Soot (dark, alpha up to about 0.6) low on walls (the first 3.5 m above the ground), in blotches and in
    /// streaks down vertical faces; ash (pale) on faces that look up. Fades with distance a little so far walls keep their shape. Shaded, so the sun and the shade fall on it as on the stone.
    /// </summary>
    public const string OverlayShader = """
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, specular_disabled;
uniform float amount = 1.0;
uniform float wash = 0.0;   // the concept light pass: a cool ash-grey wash on floors and tops (0: the first concept pass)
varying vec3 wpos;
varying vec3 wnor;
float h21(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float vn(vec2 p) {
	vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y);
}
float fbm(vec2 p) { float v = 0.0; float a = 0.5; for (int i = 0; i < 4; i++) { v += a * vn(p); p = p * 2.07 + vec2(3.1, 7.7); a *= 0.5; } return v; }
void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnor = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}
void fragment() {
	float up = wnor.y;
	float side = 1.0 - abs(up);
	// the face's own plane: floors and tops by x and z, walls along their run and the height
	vec2 along = abs(up) > 0.7 ? wpos.xz : (abs(wnor.x) > abs(wnor.z) ? wpos.zy : wpos.xy);
	float blot = fbm(along * 0.35 + 1.3);
	float soot = smoothstep(0.46, 0.76, blot) * 0.5;
	float streak = smoothstep(0.55, 0.92, vn(vec2(dot(wpos.xz, vec2(1.7, 1.3)) * 2.2, wpos.y * 0.12))) * side * 0.35;
	float low = (1.0 - smoothstep(0.0, 3.5, wpos.y)) * side * (0.35 + 0.35 * fbm(along * 1.3));
	float grit = (fbm(along * 3.1 + 11.0) - 0.5) * 0.3;
	float film = smoothstep(0.5, 0.95, up) * (0.22 + 0.2 * fbm(along * 0.9 + 5.0));   // a dirt film on floors and tops
	float s = clamp(soot + streak + low + grit + film, 0.0, 0.7);
	float ash = smoothstep(0.6, 0.95, up) * smoothstep(0.62, 0.85, fbm(wpos.xz * 0.5 + 9.0)) * 0.35;   // pale drifts in patches only
	float far = 1.0 - 0.4 * smoothstep(60.0, 180.0, length(VERTEX));
	vec3 dirt = mix(vec3(0.045, 0.042, 0.038), vec3(0.11, 0.10, 0.09), film / max(s, 1e-3));
	vec3 col = mix(dirt, vec3(0.34, 0.33, 0.32), clamp(ash / max(ash + s, 1e-3), 0.0, 1.0));
	float a = clamp(max(s, ash), 0.0, 0.7) * amount * far;
	float w = wash * smoothstep(0.5, 0.95, up) * (0.25 + 0.3 * fbm(along * 0.6 + 2.0)) * amount;
	float total = a + w * (1.0 - a);
	ALBEDO = (col * a + vec3(0.10, 0.102, 0.11) * w * (1.0 - a)) / max(total, 1e-3);
	ALPHA = total;
	ROUGHNESS = 1.0;
}
""";
}
