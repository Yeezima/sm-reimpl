using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// The lightning claw's power field and worn metals (owner, 2026-11-12: "a strong blue glow and electrical energy active on the weapon, shifting and crackling like real electricity arcs"; "wear and texture ... to
/// match the space marine armor"; docs/claw-spear-animations.md section 6, "The power field"). All **ours**, original shaders, nothing from the install here (the housing's armour texture is borrowed at run time by
/// <see cref="ClawArmourLook"/>):
/// <list type="bullet">
/// <item><b>Blade</b> (lit): brushed steel with nicks along the edges, hot near white-cyan at the cutting edges, a blue emission that stays on whatever the strike (HDR above 1, so the world's glow blooms it).</item>
/// <item><b>Halo</b> (additive, unshaded): a soft view-facing ribbon along each blade, the bloom that reads at gameplay distance and under the Compatibility renderer (no environment glow needed).</item>
/// <item><b>Arcs</b> (additive, unshaded): a handful of thin bright jagged ribbons that crawl along the blades, jump between neighbouring blades and spit off their edges. They live entirely in the vertex shader:
/// every arc is a fixed strip of 17 samples whose endpoints and zig-zag are re-drawn from a hash of the arc's number and its own step clock (14 to 28 steps a second), so they shift and crackle with no CPU work,
/// no allocation and no mesh rebuild; the ribbon is turned to the camera and never thinner than 1.5 pixels. <see cref="Lod"/> (a uniform) hides the less important arcs at a distance.</item>
/// <item><b>Worn metal</b> (lit): procedural grime, chips down to bare metal and scratches for the brass and the steel fittings, and the fallback for the housing where no armour is known.</item>
/// </list>
/// </summary>
public static class ClawPowerField
{
    /// <summary>Test aid <c>--no-claw-arcs</c> (low-end machines): the claw's electrical arcs are not built or drawn; the blue glow stays.</summary>
    public static readonly bool NoArcs = Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-claw-arcs") >= 0;

    /// <summary>The arcs of one claw (ours): per blade two that crawl along it and one that spits off an edge, and one jump between each pair of neighbouring blades.</summary>
    public const int ArcsPerClaw = 4 * 3 + 3;

    /// <summary>Samples along an arc (so 17 x 2 vertices and 32 triangles each; ours).</summary>
    public const int ArcSegments = 16;

    /// <summary>The faint crackle loop under a worn claw: the install's power conduit crackle event (also the Librarian's lightning loop, <c>LibrarianSounds.Loop</c>; the power sword has no idle hum of its own in the data), read through
    /// the sound system with the listener distance multiplied (<see cref="HumDistanceScale"/>, ours) so it stays faint. By ear to check.</summary>
    public const string HumEvent = "play_power_conduit_elec";
    public const float HumDistanceScale = 3.5f;

    public static readonly StringName Strike ="strike", Power = "power", LodName = "lod", Demo = "demo", Darken = "darken", UvWindow = "uv_window";

    static readonly string Common = """
float h11(float n) { return fract(sin(n * 12.9898 + 78.233) * 43758.5453); }
float h12(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
float vn(float x, float seed) { float i = floor(x); return mix(h11(i + seed), h11(i + 1.0 + seed), fract(x)); }
""";

    // ---- the arcs ----

    static readonly string ArcCode = ("""
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
//COMMON
uniform float strike = 0.0;
uniform float demo = 0.0;
uniform float power = 1.0;
uniform float lod = 1.0;
uniform vec3 bow = vec3(0.0);
uniform vec3 core_col = vec3(0.85, 1.0, 1.25);
uniform vec3 edge_col = vec3(0.20, 0.55, 1.6);
varying float v_vis;
varying float v_bright;
varying vec2 v_uv;

vec3 blade_pt(vec3 a, vec3 bv, float t) { return a + bv * t + bow * (t * (1.0 - t)); }

vec3 arc_at(float s, vec3 p0, vec3 p1, vec3 ax1, vec3 ax2, float id, float st, float amp, float pin_end) {
    vec3 b = mix(p0, p1, s);
    float env = sin(s * 3.14159);
    if (pin_end < 0.5) env = min(1.0, s * 3.0) * (0.6 + 0.4 * s);   // a branch is pinned at its root only
    float k = id * 5.17 + st * 1.713;
    float j1 = (vn(s * 6.0, k) - 0.5) * 2.0 + (vn(s * 14.0, k + 31.0) - 0.5) * 0.9;
    float j2 = (vn(s * 6.0, k + 57.0) - 0.5) * 2.0 + (vn(s * 14.0, k + 83.0) - 0.5) * 0.9;
    return b + (ax1 * j1 + ax2 * j2) * amp * env;
}

void vertex() {
    float id = CUSTOM0.w;
    float kind = CUSTOM1.w;
    float u = UV.x;
    float side = UV.y;
    vec3 a = VERTEX;
    vec3 bv = CUSTOM0.xyz;
    vec3 dn = CUSTOM1.xyz;
    float rate = 14.0 + 14.0 * h11(id * 3.1);
    float tt = mod(TIME, 997.0) * rate + id * 7.31;
    float st = floor(tt);
    float r1 = h12(vec2(id, st)), r2 = h12(vec2(id + 11.0, st)), r3 = h12(vec2(id + 23.0, st)), r4 = h12(vec2(id + 37.0, st)), r5 = h12(vec2(id + 51.0, st));
    float on = max(strike, demo);
    float dens = mix(kind < 0.5 ? 0.92 : (kind < 1.5 ? 0.62 : 0.70), 1.0, on);
    float shown = step(r4, dens) * step(UV2.x, lod);
    vec3 b = normalize(bv);
    vec3 a1 = normalize(NORMAL - dot(NORMAL, b) * b);
    vec3 a2 = cross(b, a1);
    vec3 p0, p1;
    float pin = 1.0;
    if (kind < 0.5) {
        float t0 = r1 * 0.62;
        float t1 = min(1.0, t0 + 0.18 + r2 * 0.36);
        p0 = blade_pt(a, bv, t0);
        p1 = blade_pt(a, bv, t1);
    } else if (kind < 1.5) {
        float ta = r1 * 0.92;
        float tb = clamp(ta + (r2 - 0.5) * 0.45, 0.0, 1.0);
        p0 = blade_pt(a, bv, ta);
        p1 = blade_pt(a + dn, bv + 0.3 * dn, tb);
    } else {
        float t = 0.06 + r1 * 0.88;
        p0 = blade_pt(a, bv, t);
        float an = r2 * 6.2832;
        float len = 0.04 + 0.075 * r3;
        p1 = p0 + (a1 * cos(an) + a2 * sin(an)) * len + b * len * 0.5 * (r5 - 0.35);
        pin = 0.0;
    }
    float chord = length(p1 - p0);
    float amp = clamp(chord * 0.20, 0.008, 0.032) * (1.0 + 0.6 * on);
    float e = 0.04;
    vec3 pm = arc_at(u, p0, p1, a1, a2, id, st, amp, pin);
    vec3 pa = arc_at(clamp(u + e, 0.0, 1.0), p0, p1, a1, a2, id, st, amp, pin);
    vec3 pb = arc_at(clamp(u - e, 0.0, 1.0), p0, p1, a1, a2, id, st, amp, pin);
    vec4 pv = MODELVIEW_MATRIX * vec4(pm, 1.0);
    vec3 tv = (MODELVIEW_MATRIX * vec4(pa - pb, 0.0)).xyz;
    vec3 wd = normalize(cross(tv, normalize(pv.xyz)) + vec3(1e-6));
    float px = 2.0 * length(pv.xyz) / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float wid = max(0.0035, 1.4 * px) * 2.2;
    pv.xyz += wd * side * wid;
    POSITION = shown > 0.5 ? PROJECTION_MATRIX * vec4(pv.xyz, 1.0) : vec4(0.0, 0.0, -2.0, 1.0);
    v_vis = shown;
    v_bright = (0.65 + 0.35 * r5) * (1.0 + 1.2 * on) * (kind > 1.5 ? 0.85 : 1.0);
    v_uv = vec2(u, side);
}

void fragment() {
    float a = abs(v_uv.y);
    float core = exp(-a * a * 16.0);
    float halo = exp(-a * a * 3.2) * 0.30;
    float endfade = smoothstep(0.0, 0.07, v_uv.x) * smoothstep(1.0, 0.93, v_uv.x);
    vec3 col = mix(edge_col, core_col, core);
    ALBEDO = col * (core * 3.2 + halo) * v_bright * power * mix(1.0, endfade, 0.6);
    ALPHA = clamp(core + halo, 0.0, 1.0);
}
""").Replace("//COMMON", Common);

    // ---- the halo ----

    static readonly string HaloCode = ("""
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
//COMMON
uniform float strike = 0.0;
uniform float demo = 0.0;
uniform float power = 1.0;
uniform vec3 tint = vec3(0.16, 0.50, 1.7);
uniform vec3 hot = vec3(0.55, 0.9, 1.4);
varying vec2 v_uv;
varying float v_level;

void vertex() {
    float t = UV.x;
    float side = UV.y;
    vec4 pv = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    vec3 tv = (MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz;
    vec3 wd = normalize(cross(tv, normalize(pv.xyz)) + vec3(1e-6));
    float px = 2.0 * length(pv.xyz) / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
    float w = max(0.062 * (1.0 - 0.55 * t) * (1.0 + 0.5 * max(strike, demo)), 4.0 * px);
    pv.xyz += wd * side * w;
    POSITION = PROJECTION_MATRIX * vec4(pv.xyz, 1.0);
    float step_t = floor(mod(TIME, 997.0) * 30.0);
    v_level = (0.82 + 0.18 * h12(vec2(step_t, floor(t * 3.0)))) * (1.0 + 1.1 * max(strike, demo)) * power;
    v_uv = vec2(t, side);
}

void fragment() {
    float a = abs(v_uv.y);
    float g = exp(-a * a * 4.5);
    float inner = exp(-a * a * 22.0);
    float tip = smoothstep(0.55, 1.0, v_uv.x);
    float fade = smoothstep(0.0, 0.05, v_uv.x) * (1.0 - 0.45 * smoothstep(0.8, 1.0, v_uv.x));
    ALBEDO = (tint * g * 0.9 + hot * inner * 0.7 * (0.6 + 0.4 * tip)) * v_level * fade;
    ALPHA = clamp(g * 0.55 + inner * 0.5, 0.0, 1.0) * fade;
}
""").Replace("//COMMON", Common);

    // ---- the blades ----

    static readonly string BladeCode = ("""
shader_type spatial;
render_mode cull_disabled;
//COMMON
uniform float strike = 0.0;
uniform float demo = 0.0;
uniform float power = 1.0;
uniform vec3 steel = vec3(0.62, 0.66, 0.74);
uniform vec3 field = vec3(0.30, 0.72, 1.7);
uniform vec3 white_hot = vec3(0.80, 1.05, 1.30);
float vn2(vec2 p, float seed) { vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f); return mix(mix(h12(i + seed), h12(i + vec2(1.0, 0.0) + seed), f.x), mix(h12(i + vec2(0.0, 1.0) + seed), h12(i + vec2(1.0, 1.0) + seed), f.x), f.y); }
void fragment() {
    float edge = smoothstep(0.30, 1.0, abs(UV.y));
    float brushed = vn2(vec2(UV.x * 7.0, UV.y * 160.0), 3.0) * 0.6 + vn2(vec2(UV.x * 23.0, UV.y * 400.0), 9.0) * 0.4;
    float nick = step(0.80, vn2(vec2(UV.x * 90.0, 7.0), 21.0)) * smoothstep(0.65, 1.0, abs(UV.y));   // chips in the cutting edges
    vec3 base = steel * (0.55 + 0.6 * brushed) * (1.0 - 0.6 * nick);
    float on = max(strike, demo);
    float step_t = floor(mod(TIME, 997.0) * 22.0);
    float flick = 0.85 + 0.15 * h12(vec2(step_t, floor(UV.x * 5.0)));
    ALBEDO = base * 0.55;
    METALLIC = 0.9;
    ROUGHNESS = clamp(0.22 + 0.32 * brushed + 0.3 * nick, 0.1, 0.9);
    // the power field: always on (strong), near white-cyan on the cutting edges and toward the point, bluer on the flats, brighter on a strike
    float tip = smoothstep(0.2, 1.0, UV.x);
    vec3 glow = mix(field, white_hot, clamp(edge * 0.75 + tip * 0.35, 0.0, 1.0));
    EMISSION = glow * (0.7 + 1.0 * edge + 0.5 * tip + 1.6 * on) * flick * power * (1.0 - 0.8 * nick);
}
""").Replace("//COMMON", Common);

    // ---- the worn metals and the housing's fallback ----

    static readonly string WornCode = ("""
shader_type spatial;
render_mode cull_disabled;
//COMMON
uniform vec3 base : source_color = vec3(0.4);
uniform vec3 bare : source_color = vec3(0.7);
uniform vec3 grime_col : source_color = vec3(0.05, 0.04, 0.03);
uniform float metal = 0.8;
uniform float rough = 0.4;
uniform float wear = 0.5;
uniform float grime = 0.5;
varying vec3 mp;
float n3(vec3 p, float seed) {
    vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float a = h12(i.xy + i.z * 17.0 + seed), b = h12(i.xy + vec2(1.0, 0.0) + i.z * 17.0 + seed);
    float c = h12(i.xy + vec2(0.0, 1.0) + i.z * 17.0 + seed), d = h12(i.xy + vec2(1.0, 1.0) + i.z * 17.0 + seed);
    float a2 = h12(i.xy + (i.z + 1.0) * 17.0 + seed), b2 = h12(i.xy + vec2(1.0, 0.0) + (i.z + 1.0) * 17.0 + seed);
    float c2 = h12(i.xy + vec2(0.0, 1.0) + (i.z + 1.0) * 17.0 + seed), d2 = h12(i.xy + vec2(1.0, 1.0) + (i.z + 1.0) * 17.0 + seed);
    return mix(mix(mix(a, b, f.x), mix(c, d, f.x), f.y), mix(mix(a2, b2, f.x), mix(c2, d2, f.x), f.y), f.z);
}
float fbm(vec3 p, float seed) { return n3(p, seed) * 0.55 + n3(p * 2.1, seed + 5.0) * 0.3 + n3(p * 4.3, seed + 9.0) * 0.15; }
void vertex() { mp = VERTEX; }
void fragment() {
    float g = fbm(mp * 22.0, 1.0);
    float grime_m = smoothstep(0.45, 0.8, g) * grime;
    float chips = smoothstep(0.64, 0.70, fbm(mp * 75.0, 4.0)) * wear;   // paint or patina chipped down to bare metal
    float sc1 = smoothstep(0.80, 0.93, n3(mp * vec3(320.0, 14.0, 14.0), 11.0));
    float sc2 = smoothstep(0.82, 0.94, n3(mp * vec3(12.0, 300.0, 18.0), 17.0));
    float scratches = max(sc1, sc2) * wear;
    vec3 c = base * (0.82 + 0.35 * fbm(mp * 9.0, 2.0));
    c = mix(c, grime_col, grime_m * 0.7);
    c = mix(c, bare, clamp(chips + scratches * 0.7, 0.0, 1.0));
    ALBEDO = c;
    METALLIC = clamp(metal * (1.0 - 0.5 * grime_m) + 0.15 * chips, 0.0, 1.0);
    ROUGHNESS = clamp(rough + 0.35 * grime_m - 0.18 * chips - 0.1 * scratches, 0.12, 1.0);
}
""").Replace("//COMMON", Common);

    static Shader? _arcs, _halo, _blade, _worn;
    static ShaderMaterial? _arcMaterial, _haloMaterial, _bladeMaterial, _housingPlate, _housingDark, _brass, _steel;

    /// <summary>The arcs' material (shared by every claw until <see cref="ClawArmourLook"/> gives a rig its own copy); null with <c>--no-claw-arcs</c>.</summary>
    public static ShaderMaterial ArcMaterial => _arcMaterial ??= new ShaderMaterial { Shader = _arcs ??= new Shader { Code = ArcCode }, RenderPriority = 4 };
    public static ShaderMaterial HaloMaterial => _haloMaterial ??= new ShaderMaterial { Shader = _halo ??= new Shader { Code = HaloCode }, RenderPriority = 3 };
    public static ShaderMaterial BladeMaterial => _bladeMaterial ??= new ShaderMaterial { Shader = _blade ??= new Shader { Code = BladeCode } };

    static ShaderMaterial Worn(Color baseColour, Color bare, Color grime, float metal, float rough, float wear, float grimeAmount)
    {
        var m = new ShaderMaterial { Shader = _worn ??= new Shader { Code = WornCode } };
        m.SetShaderParameter("base", baseColour); m.SetShaderParameter("bare", bare); m.SetShaderParameter("grime_col", grime);
        m.SetShaderParameter("metal", metal); m.SetShaderParameter("rough", rough); m.SetShaderParameter("wear", wear); m.SetShaderParameter("grime", grimeAmount);
        return m;
    }

    /// <summary>The housing's armour plates (the placeholder the rig replaces with the wearer's gauntlet material; this worn dark-grey plate stays where none is known).</summary>
    public static ShaderMaterial HousingPlate => _housingPlate ??= Worn(new Color(0.30f, 0.33f, 0.38f), new Color(0.62f, 0.64f, 0.68f), new Color(0.05f, 0.045f, 0.04f), 0.55f, 0.46f, 0.7f, 0.8f);
    public static ShaderMaterial HousingDark => _housingDark ??= Worn(new Color(0.15f, 0.16f, 0.19f), new Color(0.50f, 0.52f, 0.56f), new Color(0.04f, 0.035f, 0.03f), 0.55f, 0.5f, 0.7f, 0.8f);
    public static ShaderMaterial Brass => _brass ??= Worn(new Color(0.52f, 0.36f, 0.15f), new Color(0.88f, 0.70f, 0.36f), new Color(0.07f, 0.055f, 0.03f), 0.9f, 0.42f, 0.7f, 1.0f);
    public static ShaderMaterial Steel => _steel ??= Worn(new Color(0.42f, 0.44f, 0.50f), new Color(0.78f, 0.80f, 0.86f), new Color(0.06f, 0.055f, 0.05f), 0.85f, 0.38f, 0.65f, 0.8f);

    /// <summary>Whether a material is one of the claw's power-field materials (a rig gets its own copy: the strike level, the arc level of detail).</summary>
    public static bool IsPerRig(Material? m) =>
        m is ShaderMaterial s && (ReferenceEquals(s, _arcMaterial) || ReferenceEquals(s, _haloMaterial) || ReferenceEquals(s, _bladeMaterial) || ReferenceEquals(s, ClawCrackle.Material));

    /// <summary>The model-space direction the blades bow (their tips toward the palm), for the arcs' blade curve; set per claw model at build.</summary>
    public static void SetBow(ShaderMaterial arcs, Vector3 bow) => arcs.SetShaderParameter("bow", bow);

    /// <summary>The share of a claw's arcs drawn at <paramref name="distance"/> metres from the camera (ours): all within 14 m, then fewer; the Compatibility renderer and safe mode draw at most half.</summary>
    public static float Lod(float distance)
    {
        float lod = distance < 14f ? 1f : distance < 28f ? 0.55f : distance < 55f ? 0.3f : 0.12f;
        return SafeMode.Active ? Mathf.Min(lod, 0.5f) : lod;
    }

    /// <summary>The arc surface of a claw: <see cref="ArcsPerClaw"/> strips, each in the vertex shader's own terms (see <see cref="ArcCode"/>); blade data comes from the caller.</summary>
    public static void AddArc(SurfaceTool arcs, Vector3 anchor, Vector3 bladeVector, Vector3 neighbour, Vector3 axis, int id, int kind, float rank)
    {
        for (int i = 0; i <= ArcSegments; i++)
            foreach (float side in new[] { -1f, 1f })
            {
                arcs.SetUV(new Vector2(i / (float)ArcSegments, side));
                arcs.SetUV2(new Vector2(rank, 0f));
                arcs.SetNormal(axis);
                arcs.SetCustom(0, new Color(bladeVector.X, bladeVector.Y, bladeVector.Z, id));
                arcs.SetCustom(1, new Color(neighbour.X, neighbour.Y, neighbour.Z, kind));
                arcs.AddVertex(anchor);
            }
        // the strip's indices are added by the caller's SurfaceTool.AddIndex in order: vertex 2i, 2i+1 per sample
    }

    /// <summary>The indices of one arc strip whose first vertex is <paramref name="first"/>.</summary>
    public static void AddArcIndices(SurfaceTool arcs, int first)
    {
        for (int i = 0; i < ArcSegments; i++)
        {
            int a = first + 2 * i, b = a + 1, c = a + 2, d = a + 3;
            arcs.AddIndex(a); arcs.AddIndex(c); arcs.AddIndex(b);
            arcs.AddIndex(b); arcs.AddIndex(c); arcs.AddIndex(d);
        }
    }
}
