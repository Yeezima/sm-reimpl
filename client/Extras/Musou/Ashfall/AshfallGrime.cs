using System;
using System.Linq;
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
public static partial class AshfallGrime
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

    /// <summary>Whether the darkening (the multiply pass on the models and the ground's dark uniforms) applies; <c>--ashfall-darken off</c> (test aid) leaves only that out. Every renderer and quality.</summary>
    public static bool Darken => !Off && !DarkenOff;

    static bool DarkenOff
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--ashfall-darken");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "off";
        }
    }

    /// <summary>The ground and boundary shaders' <c>dark</c> multiplier and cool tint (ours; refine register).</summary>
    public const float GroundDark = 1.0f;

    /// <summary>The plaza paving's (pale cream texture, in full sun): darker still, to a mid-dark flagstone grey.</summary>
    public const float PavingDark = 0.50f;
    public static readonly Color GroundDarkTint = new(0.92f, 0.97f, 1.04f);

    static ShaderMaterial? _overlay;
    static Node? _items;
    static ShaderMaterial? _top;
    static bool _overlayOn = true;

    /// <summary>Test aid (<c>--perf-ab grime</c>, musou/perf-pass): the kit models' overlay pass (the grime and the multiply) on or off, to measure what it costs.</summary>
    public static void SetOverlay(bool on)
    {
        _overlayOn = on;
        if (_items is null || !GodotObject.IsInstanceValid(_items)) return;
        void Set(Node n) { if (n is MeshInstance3D mi) Dress(mi); foreach (var c in n.GetChildren()) Set(c); }
        foreach (var child in _items.GetChildren()) Set(child);
    }
    static ShaderMaterial? _darken;

    /// <summary>The models' multiply material (one, shared).</summary>
    public static ShaderMaterial DarkenMaterial() => _darken ??= new ShaderMaterial { Shader = DarkenShaderFor(_pulled), RenderPriority = OrderOld ? 0 : DarkenPriority };

    /// <summary>
    /// The passes' draw order (ashfall-no-flicker, 2026-11-08; ours). The multiply is the overlay's next pass: two transparent passes of the same instance at the same depth, which the engine's transparent sort left
    /// tied, so per instance and per frame either could draw first; mix-then-multiply and multiply-then-mix differ by about 12 % where the soot is, and whole models and floor slabs switched between the two
    /// (with any camera motion; which way a launch started was chance). Fixed priorities: the overlay at <see cref="OverlayPriority"/>, the multiply after it at <see cref="DarkenPriority"/>, both after the
    /// alpha-to-coverage cut-outs (<see cref="RenderQuality.CutOutPriority"/>, -100) and before the ground decals (gore pools -10, the impact glow -1) and every effect (0): blood, glow, smoke and fire are never multiplied by a wall's grime. <c>--ashfall-grime-order old</c>
    /// (test aid) leaves both at 0, as before.
    /// </summary>
    public const int OverlayPriority = -12, DarkenPriority = -11;

    static bool OrderOld => Array.IndexOf(OS.GetCmdlineUserArgs(), "--ashfall-grime-order") is int i && i >= 0 && i + 1 < OS.GetCmdlineUserArgs().Length && OS.GetCmdlineUserArgs()[i + 1] == "old";

    /// <summary>
    /// The overlay passes' depth pull (hotfix/ashfall-floor-shimmer, ours): each pass is drawn <see cref="Pull"/> of its distance nearer the camera along its own view ray (the same pixels), so it no longer
    /// loses the depth test against the surface it covers in patches as the camera moves. While the viewport upscales with FSR 2 the grime is folded instead (<see cref="SetFoldForFsr"/>) and any pass left
    /// (the multiply alone, Low preset) is drawn plain, as before (the lines marked <c>//PULL</c> left out). <c>--ashfall-grime-pull off</c> (test aid) always draws the plain passes.
    /// </summary>
    public static bool PullOn { get; } = !(Array.IndexOf(OS.GetCmdlineUserArgs(), "--ashfall-grime-pull") is int i && i >= 0 && i + 1 < OS.GetCmdlineUserArgs().Length && OS.GetCmdlineUserArgs()[i + 1] == "off");

    /// <summary>The pull's share of the distance (1 cm at 100 m; ours). Kept in the shaders' <c>pull</c> default.</summary>
    public const float Pull = 1e-4f;

    static Shader? _overlayPulled, _overlayPlain, _darkenPulled, _darkenPlain;
    static bool _pulled = PullOn;

    /// <summary>A shader's plain form: the <c>//PULL</c> lines and the hand transform left out (the engine transforms as before the hotfix).</summary>
    static string Plain(string code) => string.Join("\n", code.Split('\n').Where(l => !l.Contains("//PULL", StringComparison.Ordinal)))
        .Replace(", skip_vertex_transform;", ";", StringComparison.Ordinal);

    static Shader OverlayShaderFor(bool pulled) => pulled ? _overlayPulled ??= new Shader { Code = OverlayShader } : _overlayPlain ??= new Shader { Code = Plain(OverlayShader) };
    static Shader DarkenShaderFor(bool pulled) => pulled ? _darkenPulled ??= new Shader { Code = DarkenShader } : _darkenPlain ??= new Shader { Code = Plain(DarkenShader) };

    /// <summary>Called each frame by <see cref="AshfallStorm"/> (the Options can switch FSR 2 during a match): with FSR 2 the grime folded and the passes plain, else the option's choice and the pulled passes.</summary>
    public static void UpdateForViewport(Viewport? viewport)
    {
        bool fsr = viewport is not null && viewport.Scaling3DMode == Viewport.Scaling3DModeEnum.Fsr2;
        SetFoldForFsr(fsr);
        bool want = PullOn && viewport is not null && !fsr;
        if (want == _pulled) return;
        _pulled = want;
        if (_overlay is not null) _overlay.Shader = OverlayShaderFor(want);
        if (_darken is not null) _darken.Shader = DarkenShaderFor(want);
    }

    /// <summary>The floor wash of the concept light pass (0 to 1; ours): set on the shared overlay by <see cref="AshfallStorm"/>.</summary>
    public const float LightPassWash = 1f;

    /// <summary>The models' overlay material (one, shared).</summary>
    public static ShaderMaterial OverlayMaterial() => _overlay ??= new ShaderMaterial { Shader = OverlayShaderFor(_pulled), RenderPriority = OrderOld ? 0 : OverlayPriority };

    /// <summary>Applies the grime to the kit look under <paramref name="root"/> (the map's game space); returns how many ground materials took it. Models that load later get the overlay as they arrive.</summary>
    public static int Apply(Node? root, float quality, bool compat)
    {
        if (Off || root?.FindChild("KitLook", true, false) is not Node kit) return 0;
        int ground = 0;
        bool darken = Darken;
        if (kit.GetNodeOrNull("Remainder") is { } remainder)
            foreach (var child in remainder.GetChildren())
                if (child is MeshInstance3D { MaterialOverride: ShaderMaterial sm } && sm.Shader is { } sh)
                {
                    if (sh.Code.Contains("uniform float grime"))
                    {
                        sm.SetShaderParameter("grime", GroundAmount);
                        ground++;
                    }
                    if (darken && sh.Code.Contains("uniform float dark"))
                    {
                        sm.SetShaderParameter("dark", child.Name == "Paving" ? PavingDark : GroundDark);
                        sm.SetShaderParameter("dark_tint", new Vector3(GroundDarkTint.R, GroundDarkTint.G, GroundDarkTint.B));
                    }
                }
        bool overlay = Overlay(quality, compat);
        if ((overlay || darken) && kit.GetNodeOrNull("Items") is { } items)
        {
            ShaderMaterial top = overlay ? OverlayMaterial() : DarkenMaterial();
            if (overlay && darken) top.NextPass = DarkenMaterial();
            DarkenMaterial().SetShaderParameter("cheap", !overlay);   // Low and Compatibility: the blotches, streaks and soot band only (review: scale with graphics settings)
            _items = items; _top = top;
            void Give(Node n) { if (n is MeshInstance3D mi) Dress(mi); foreach (var c in n.GetChildren()) Give(c); }   // the overlay, or its folded materials (AshfallGrime.Folded.cs)
            foreach (var child in items.GetChildren()) Give(child);
            items.ChildEnteredTree += Give;
        }
        return ground;
    }

    /// <summary>
    /// The models' overlay: world-space noise, so it continues across pieces and never shows the models' UV seams. Soot (dark, alpha up to about 0.6) low on walls (the first 3.5 m above the ground), in blotches and in
    /// streaks down vertical faces; ash (pale) on faces that look up. Fades with distance a little so far walls keep their shape. Shaded, so the sun and the shade fall on it as on the stone.
    /// </summary>
    public const string OverlayShader = """
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, specular_disabled, skip_vertex_transform;
uniform float pull = 1e-4;   //PULL see vertex() (AshfallGrime.Pull)
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
	//PULL: hotfix/ashfall-floor-shimmer (AshfallGrime.PullOn): drawn over the model's own surface, this pass shows only where its depth matches the surface's, which another shader computed; with
	//PULL: the camera moving the two rounded differently frame by frame and patches of floor and wall lost or regained the grime. Pulled toward the camera along its own view ray by PULL of its
	//PULL: distance: the same pixels, always in front of its own surface (1 cm at 100 m).
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz * (1.0 - pull);   //PULL
	NORMAL = normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);   //PULL
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

    /// <summary>
    /// The models' multiply pass (ours; the owner, 2026-11-06: darker, grittier, dirtier): an unshaded blend_mul overlay whose colour is a cool grey times world-space variation (blotches at about 7 m, stains at about 1.7 m,
    /// streaks down walls, a soot band at the wall foot, fine grit), fading to 1 with distance as the fog thickens so far ruins keep their aerial perspective. Plain GLSL, runs on the Compatibility renderer too.
    /// </summary>
    public const string DarkenShader = """
shader_type spatial;
render_mode unshaded, blend_mul, depth_draw_never, depth_test_default, cull_back, fog_disabled, shadows_disabled, skip_vertex_transform;
uniform float pull = 1e-4;   //PULL as the overlay's
uniform vec3 base = vec3(0.90, 0.92, 0.97);   // the baked kit is already dark and grimy: the runtime multiply keeps its variation, little of its darkening
uniform float fog_density = 0.0036;
uniform bool cheap = false;   // fewer noise lookups (Low, Compatibility)
varying vec3 wpos;
varying vec3 wnor;
float h21(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float vn(vec2 p) {
	vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y);
}
float fbm(vec2 p) { float v = 0.0; float a = 0.5; for (int i = 0; i < 3; i++) { v += a * vn(p); p = p * 2.07 + vec2(3.1, 7.7); a *= 0.5; } return v / 0.875; }
void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	wnor = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
	//PULL: hotfix/ashfall-floor-shimmer (AshfallGrime.PullOn): drawn over the model's own surface, this pass shows only where its depth matches the surface's, which another shader computed; with
	//PULL: the camera moving the two rounded differently frame by frame and patches of floor and wall lost or regained the grime. Pulled toward the camera along its own view ray by PULL of its
	//PULL: distance: the same pixels, always in front of its own surface (1 cm at 100 m).
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz * (1.0 - pull);   //PULL
	NORMAL = normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);   //PULL
}
void fragment() {
	float up = wnor.y;
	float side = 1.0 - abs(up);
	vec2 along = abs(up) > 0.7 ? wpos.xz : (abs(wnor.x) > abs(wnor.z) ? wpos.zy : wpos.xy);
	float blot = fbm(along * 0.14 + 1.3);
	float m = mix(0.78, 1.05, smoothstep(0.25, 0.75, blot));
	if (!cheap) { float stain = fbm(along * 0.6 + 8.0); m *= 1.0 - 0.3 * smoothstep(0.58, 0.8, stain); }
	float streak = smoothstep(0.55, 0.92, vn(vec2(dot(wpos.xz, vec2(1.7, 1.3)) * 2.2, wpos.y * 0.12)));
	m *= 1.0 - 0.22 * streak * side;
	m *= 1.0 - 0.25 * (1.0 - smoothstep(0.0, 2.5, wpos.y)) * side;
	if (!cheap) m *= 1.0 + 0.12 * (vn(along * 3.3 + 11.0) - 0.5);
	vec3 col = base * m;
	float dist = length(VERTEX);
	col = mix(col, vec3(1.0), 1.0 - exp(-fog_density * dist * 1.2));
	ALBEDO = col;
}
""";
}
