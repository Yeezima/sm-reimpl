using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// The game-wide visual overhaul **proposal** (owner's request of 2026-11-06, docs/visual-overhaul-proposal.md): one switch,
/// <c>--visual-overhaul on|off</c> (default off: main's look is unchanged until the owner approves the previews). It changes how the original
/// content is lit and rendered, never the content itself: the map's own sun, ambient, fog and lamp colours stay the data's.
/// <list type="bullet">
/// <item>V1 eye adaptation: a narrow auto exposure (camera attributes; Forward+ only) that lifts interiors and deep shade by at most
/// <c>ae.boost</c> and never darkens the sunlit yards below the chosen exposure by more than <c>ae.cut</c>.</item>
/// <item>V2 large-scale contact shading: wider, stronger SSAO and SSIL (look constants below, layered between <c>--look</c> and <c>--look-set</c>).</item>
/// <item>V3 sharper sun shadows near the camera: a tighter first cascade and less blur.</item>
/// <item>V4 texture clarity: 16x anisotropic filtering on the 3D viewport.</item>
/// <item>V5 debanding of the fog and sky gradients.</item>
/// <item>V6 specular anti-aliasing: a stronger screen-space roughness limiter (less sparkle on normal-mapped grates and armour edges).</item>
/// <item>V7 smog sky: a static layer of smoke banks in the sky background (not in the ambient cubemap: the lighting does not change);
/// the depth fog and the volumetric smog cover less of the sky background so the banks read.</item>
/// <item>V8 smoother bloom: bicubic glow upscale.</item>
/// <item>V9 finer volumetric smog: a denser froxel grid (less blockiness and flicker on the light shafts; Forward+ only).</item>
/// </list>
/// Every value is **ours** (tuned by eye on the matched preview views) and a provisional approximation; <c>--look-set</c> still wins over each.
/// The Compatibility renderer gets V3 to V7 (it has no auto exposure, SSAO, SSIL or volumetric fog).
/// </summary>
public static class VisualOverhaul
{
    /// <summary><c>--visual-overhaul on|off</c> (default off).</summary>
    public static bool On { get; } = Parse();

    static bool Parse()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--visual-overhaul");
        if (i < 0) return false;
        string text = i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "on";
        switch (text)
        {
            case "on" or "1" or "true": return true;
            case "off" or "0" or "false": return false;
            default:
                if (text.StartsWith("--", StringComparison.Ordinal)) return true;   // a bare --visual-overhaul
                GD.PrintErr($"--visual-overhaul '{text}' is not known (on, off); off is used");
                return false;
        }
    }

    /// <summary>
    /// The lighting constants the overhaul sets (see <see cref="MapLightingView.V"/>; the look's values below, <c>--look-set</c> above). Empty when off.
    /// </summary>
    public static IReadOnlyDictionary<string, float> LookOverrides => On ? Constants : Empty;

    static readonly Dictionary<string, float> Empty = new();

    static readonly Dictionary<string, float> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        // V2: contact shading over a larger scale (under consoles, in the hall's corners, under the containers) and a little on direct light;
        // the indirect bounce reaches further and a little stronger (warm sunlit floors fill the shade).
        ["ssao.radius"] = 2.4f, ["ssao.power"] = 1.8f, ["ssao.detail"] = 0.8f, ["ssao.light"] = 0.3f, ["ssao.channel"] = 0.5f,
        ["ssil.radius"] = 9f, ["ssil.int"] = 2.6f,
        // V3: first cascade 3 % of the data's 400 m (12 m: the player and the next cover in the sharpest map), second 10 %, less blur.
        // V7: the depth fog and the volumetric smog cover less of the sky background (M42 0.35 and 0.1), so the smoke banks read; the horizon haze stays.
        ["fog.sky"] = 0.15f, ["vf.sky"] = 0.04f,
        ["sh.s1"] = 0.03f, ["sh.s2"] = 0.1f, ["sh.s3"] = 0.35f, ["sh.blur"] = 0.8f,
    };

    /// <summary>A constant: the overhaul's own value unless <c>--look-set</c> names it (through <see cref="MapLightingView.V"/>).</summary>
    static float K(string key, float value) => MapLightingView.V(key, value, value);

    /// <summary>
    /// V4 to V6 and V8, renderer-wide and per viewport (call once per root viewport, after <see cref="RenderQuality.Apply"/>). Returns the log line,
    /// or null when off.
    /// </summary>
    public static string? ApplyViewport(Viewport viewport)
    {
        if (!On) return null;
        viewport.AnisotropicFilteringLevel = Viewport.AnisotropicFiltering.Anisotropy16X;   // V4
        viewport.UseDebanding = true;                                                           // V5
        // V6: the limiter raises the roughness where normals change faster than a pixel; amount and limit up from M40-T7's 0.25 and 0.18.
        if (RenderQuality.Aa != AaMode.Off) RenderingServer.ScreenSpaceRoughnessLimiterSetActive(true, K("srl.amount", 0.4f), K("srl.limit", 0.28f));
        RenderingServer.EnvironmentGlowSetUseBicubicUpscale(true);                              // V8
        return "visual overhaul: on (proposal; docs/visual-overhaul-proposal.md): 16x anisotropic, debanding, roughness limiter 0.4/0.28, bicubic glow"
               + (SafeMode.Compatibility ? "; Compatibility renderer: no eye adaptation, SSAO/SSIL or volumetric changes" : "");
    }

    /// <summary>
    /// V1, V7 and V9 for a loaded map (call after <see cref="MapLightingView.Apply"/>, also when it failed: then only V1 applies). Returns the log line, or null when off.
    /// </summary>
    public static string? ApplyWorld(Camera3D? camera, Godot.Environment? environment)
    {
        if (!On) return null;
        var parts = new List<string>();
        if (camera is not null && !SafeMode.Compatibility && K("ae.on", 1f) > 0.5f)
        {
            camera.Attributes = EyeAdaptation();
            parts.Add($"eye adaptation (x{1f / K("ae.cut", 0.8f):0.##} down to x{K("ae.boost", 1.5f):0.##} up)");
        }
        if (environment?.Sky?.SkyMaterial is ShaderMaterial sky && K("sky.smog", 1f) > 0f && sky.Shader is { } shader && shader.Code.Contains("uniform vec3 bounce", StringComparison.Ordinal))
        {
            // V7: the same sky shader plus the smoke banks; its uniforms keep their values (same names), the blender keeps setting them.
            sky.Shader = SmogSky ??= new Shader { Code = SmogSkyCode(shader.Code) };
            sky.SetShaderParameter("smog_amount", K("sky.smog", 1f));
            parts.Add("smog sky");
        }
        if (environment is not null && environment.VolumetricFogEnabled && !SafeMode.Compatibility)
        {
            // V9: a denser froxel grid than M42 medium's 96 x 64 (high keeps its own 160 x 96).
            if (MapLightingView.Preset == MapLightingView.LightingPreset.Medium)
                RenderingServer.EnvironmentSetVolumetricFogVolumeSize((int)K("vf.grid", 128f), (int)K("vf.depth", 80f));
            parts.Add("finer volumetric smog");
        }
        return "visual overhaul (map): " + (parts.Count > 0 ? string.Join(", ", parts) : "nothing to change");
    }

    /// <summary>
    /// V1: Godot's auto exposure measures the scene's mean luminance <c>L</c> and multiplies the exposure by <c>scale / L</c>, with <c>L</c> clamped
    /// between the camera attributes' min and max (practical attributes: luminance = ISO x 12.5 / 100 / sensitivity 100, i.e. ISO / 800).
    /// The clamp keeps the range narrow: at most <c>ae.boost</c> brighter (interiors, deep shade) and <c>1 / ae.cut</c> darker (the brightest yard
    /// views), so the owner's exposure for the yards stays and PvP never loses a player to a dark room. Adaptation over about a second (<c>ae.speed</c>).
    /// </summary>
    static CameraAttributesPractical EyeAdaptation()
    {
        float scale = K("ae.scale", 0.11f), boost = Math.Max(1f, K("ae.boost", 1.5f)), cut = Math.Clamp(K("ae.cut", 0.8f), 0.1f, 1f);
        float minLum = scale / boost, maxLum = scale / cut;
        return new CameraAttributesPractical
        {
            AutoExposureEnabled = true,
            AutoExposureScale = scale,
            AutoExposureSpeed = K("ae.speed", 1.2f),
            AutoExposureMinSensitivity = minLum * 800f,
            AutoExposureMaxSensitivity = maxLum * 800f,
        };
    }

    static Shader? SmogSky;

    /// <summary>
    /// V7: the map sky shader (<see cref="MapLightingView"/>'s, passed in so the overhaul follows its changes) with a layer of smoke banks over the
    /// background only (<c>AT_CUBEMAP_PASS</c> is untouched, so ambient light and reflections stay as they are). The banks are value noise on the sky
    /// dome's plane, densest a little above the horizon, coloured from the haze itself (darker and browner), lit on the sun's side. No <c>TIME</c>:
    /// a time-dependent sky would re-render every frame.
    /// </summary>
    static string SmogSkyCode(string baseCode)
    {
        const string Helpers = """
            uniform float smog_amount = 1.0;
            float vo_hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
            float vo_noise(vec2 p) {
                vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
                return mix(mix(vo_hash(i), vo_hash(i + vec2(1.0, 0.0)), f.x), mix(vo_hash(i + vec2(0.0, 1.0)), vo_hash(i + vec2(1.0, 1.0)), f.x), f.y);
            }
            float vo_fbm(vec2 p) { float a = 0.5, s = 0.0; for (int i = 0; i < 5; i++) { s += a * vo_noise(p); p = p * 2.03 + vec2(17.1, 9.2); a *= 0.5; } return s; }
            vec3 vo_smog(vec3 c, vec3 dir, vec3 horizon) {
                if (dir.y < -0.02 || smog_amount <= 0.0) return c;
                vec2 p = dir.xz / (dir.y + 0.12) * 1.4;
                float n = vo_fbm(p + vec2(3.0, 7.0));
                float banks = smoothstep(0.38, 0.72, n) * smoothstep(-0.02, 0.06, dir.y) * (1.0 - smoothstep(0.25, 0.8, dir.y));
                vec3 smoke = horizon * vec3(0.46, 0.4, 0.36);
                if (LIGHT0_ENABLED) smoke += horizon * 0.35 * pow(max(dot(dir, LIGHT0_DIRECTION), 0.0), 4.0);
                return mix(c, smoke, banks * 0.85 * clamp(smog_amount, 0.0, 2.0));
            }
            void sky() {
            """;
        string code = baseCode.Replace("void sky() {", Helpers, StringComparison.Ordinal);
        // The background colour is written last as "COLOR = c;" in the non-cubemap branch.
        return code.Replace("COLOR = c;", "COLOR = vo_smog(c, EYEDIR, sky_horizon);", StringComparison.Ordinal);
    }
}
