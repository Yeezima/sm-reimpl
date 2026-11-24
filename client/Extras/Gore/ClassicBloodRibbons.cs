using System;
using Godot;
using SmClient.ImpactRules;

namespace SmClient;

/// <summary>
/// classic-hit-effects, the Extended profile's blood spray (added content: gore; owner answer 4, 2026-11-24: "Redesign it"; client presentation only). A third design, measured on the
/// reference clip (review of 2b89de15): the blood is LAID along the weapon's path through the contact almost at once (full reach in about two frames), as soft, wispy ribbons on both
/// sides of the body, thickest and densest at the body; then it HANGS in place (a slight lift and creep, no travel, no droop), brightest about 0.05-0.35 s after contact, strong to about
/// 0.6 s and faint to about 1 s, thinning into patches as it fades. The ribbons follow a curve built from the weapon's recorded samples before and after the contact
/// (<see cref="ClassicBloodRules.RibbonPath"/>), a straight line along the shot or the charge for gunfire and charges. In today's Classic health-hit red
/// (<see cref="ClassicHitSpray.BloodTint"/>, the colour the brief keeps), MIXED over the scene (not added: never brighter than that red, the brightness cap of review 3 and owner answer 5).
/// <para>Visibility pass (2026-11-24, owner answer 6: the release build read as "faint red, easy to miss"): broader, flatter, more opaque bands with a ragged mottled edge
/// (<see cref="ClassicBloodRules.RibbonOpacity"/>, <see cref="ClassicBloodRules.RibbonHalfWidth"/>, <see cref="ClassicBloodRules.RibbonBundle"/>), drawn after the other transparent effects
/// (<see cref="ClassicBloodRules.RibbonRenderPriority"/>) so a thunder hammer's or daemon maul's hit flash at the same contact lies under the blood rather than over it.</para>
/// <para>One pooled mesh per spray (24, growing to 64; the most faded reused beyond), moved by its shader: no particles, nothing allocated per frame (the shader parameter names are cached).
/// Counts, lengths, widths and the envelope are ours (spec/behavior.md "Classic hit effects").</para>
/// </summary>
public partial class ClassicBloodRibbons : Node3D
{
    public const int Pool = 24, MaxPool = 64;
    const int Seg = ClassicBloodRules.RibbonSegments;
    const int Half = ClassicBloodRules.RibbonHalf;

    static string F(float v) => v.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);

    static readonly string Code = $$"""
shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;   // mixed, not added: the colour never exceeds the tint, so stacked bands stay blood red, never white (review 3, finding 5)

uniform float age = -1.0;                 // seconds since contact (negative: waiting for the tracer)
uniform vec3 ctrl[{{2 * Half + 1}}];        // the ribbon curve relative to the contact: back end .. contact (index {{Half}}) .. far end
uniform float len_back = 0.9;
uniform float len_fwd = 1.8;
uniform float count = 7.0;
uniform float spread = 0.42;
uniform float size = 1.0;
uniform float seed = 0.0;
uniform vec4 tint : source_color = vec4(1.0, 0.12, 0.08, 1.0);
uniform float opacity = 0.95;             // a band's opacity at its middle while held (mixed: stacking only makes it more opaque)
varying float v_dn;
varying float v_s;
varying float v_ri;
varying float v_w;
varying float v_end;

vec3 cp(int i) { return ctrl[clamp(i, 0, {{2 * Half}})]; }
vec3 curve(float s) {
    float fi = s < 0.0 ? {{F(Half)}} + s / max(len_back, 1e-3) * {{F(Half)}} : {{F(Half)}} + s / max(len_fwd, 1e-3) * {{F(Half)}};
    if (fi >= {{F(2 * Half)}}) return cp({{2 * Half}}) + (cp({{2 * Half}}) - cp({{2 * Half - 1}})) * (fi - {{F(2 * Half)}});
    if (fi <= 0.0) return cp(0) + (cp(0) - cp(1)) * (-fi);
    int i = int(floor(fi));
    float u = fi - float(i);
    vec3 p0 = cp(i - 1), p1 = cp(i), p2 = cp(i + 1), p3 = cp(i + 2);
    if (i == 0) p0 = 2.0 * p1 - p2;
    if (i >= {{2 * Half - 1}}) p3 = 2.0 * p2 - p1;
    float u2 = u * u, u3 = u2 * u;
    return 0.5 * ((2.0 * p1) + (-p0 + p2) * u + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * u2 + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * u3);
}

void vertex() {
    float ri = UV2.x;
    v_ri = ri; v_dn = 1.0; v_s = 0.0; v_w = 0.0; v_end = 1.0;
    if (ri >= count || age < 0.0) {
        POSITION = vec4(2.0, 2.0, 2.0, 1.0);   // clipped
    } else {
        float h1 = fract(COLOR.r + seed * 0.6180340);
        float h2 = fract(COLOR.g + seed * 0.3819660);
        float h3 = fract(COLOR.b + seed * 0.7548777);
        float h4 = fract(COLOR.a + seed * 0.5698403);
        // each band's own reach on both sides of the contact: staggered, so no two end together and no end is blunt
        float fwd = len_fwd * (0.6 + 0.4 * h1);
        float back = len_back * (0.45 + 0.55 * h2);
        float s = mix(-back, fwd, UV.x);
        float reveal = smoothstep(0.0, 0.05, age);          // laid at full reach within about two frames
        float hold = min(age, 0.6);
        float se = s * (0.3 + 0.7 * reveal) * (1.0 + 0.27 * hold);   // then the far end creeps on about 16 %
        vec3 p = curve(se);
        vec3 tng = curve(se + 0.03) - curve(se - 0.03);
        tng = length(tng) > 1e-5 ? normalize(tng) : vec3(1.0, 0.0, 0.0);
        vec3 side = abs(tng.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : normalize(cross(tng, vec3(0.0, 1.0, 0.0)));
        vec3 up2 = cross(side, tng);
        float dn = s >= 0.0 ? s / max(len_fwd, 1e-3) : -s / max(len_back, 1e-3);   // 0 at the contact, 1 at the full reach
        // 2 to 3 broad bands stacked across the stroke, running straight through the body (no kink at the contact), a little wider apart toward the far end, the bundle about
        // 60 % taller as it hangs (review 3: the reference's bands, its height +63 %)
        float slot = count > 1.0 ? (ri + 0.5) / count - 0.5 : 0.0;
        float bundle = {{F(ClassicBloodRules.RibbonBundle)}} * clamp(spread / 0.42, 0.35, 1.25) * size;
        vec3 across3 = normalize(up2 + side * 0.5 * (h3 - 0.5));
        float off = (slot * bundle + (h4 - 0.5) * 0.06) * (1.0 + 0.1 * s) * (1.0 + hold);
        p += across3 * off + vec3(0.0, 1.0, 0.0) * 0.2 * hold * (0.6 + 0.8 * h1);   // a lift of about 5 % of his height, no droop
        vec3 pv = (MODELVIEW_MATRIX * vec4(p, 1.0)).xyz;
        vec3 tv = (MODELVIEW_MATRIX * vec4(p + tng * 0.05, 1.0)).xyz - pv;
        vec3 across = cross(length(tv) > 1e-6 ? normalize(tv) : vec3(0.0, 1.0, 0.0), normalize(pv));
        across = length(across) > 1e-5 ? normalize(across) : vec3(1.0, 0.0, 0.0);
        float w = size * {{F(ClassicBloodRules.RibbonHalfWidth)}} * (0.8 + 0.5 * h3) * mix(1.0, 0.6, min(dn, 1.0)) * (1.0 + hold);
        pv += across * (UV.y * 2.0 - 1.0) * w;
        v_dn = dn; v_s = s; v_w = w;
        v_end = s >= 0.0 ? s / max(fwd, 1e-3) : -s / max(back, 1e-3);   // 0 at the contact, 1 at this band's own ends
        POSITION = PROJECTION_MATRIX * vec4(pv, 1.0);
    }
}

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

void fragment() {
    float x = UV.y * 2.0 - 1.0;
    // broad and solid across, with a ragged, mottled edge that wanders along the band (the reference's broad mottled bands; was a thin gaussian, exp(-3 x^2))
    float edge = 0.62 + 0.38 * vnoise(vec2(v_s * 5.0 + v_ri * 7.3 + seed * 13.0, v_ri * 3.1 + sign(x) * 4.7));
    float prof = 1.0 - smoothstep(edge - 0.4, edge, abs(x));
    float taper = 1.0 - smoothstep(0.4, 1.0, v_end);        // each band's own ends fade out, never blunt
    // mottled from the start, breaking into patches from about 0.25 s (the reference)
    float n = 0.6 * vnoise(vec2(v_s * 2.4 + seed * 17.0 + v_ri * 5.3, x * 1.1 + v_ri * 9.1 + age * 0.4))
            + 0.4 * vnoise(vec2(v_s * 7.5 - seed * 7.0 + v_ri * 2.7, x * 2.4 - age * 0.7));
    float grain = vnoise(vec2(v_s * 16.0 + v_ri * 3.7, x * 5.0 + seed * 11.0));
    float mottle = mix({{F(ClassicBloodRules.RibbonMottleFloor)}}, 1.0, smoothstep(0.2, 0.8, n)) * mix(0.8, 1.0, grain);
    float thr = mix(0.0, 0.45, smoothstep(0.25, 0.8, age));
    float patches = smoothstep(thr - 0.15, thr + 0.35, n);
    float env = smoothstep(-0.01, 0.06, age) * exp(-{{F(ClassicBloodRules.RibbonDecay)}} * clamp(age - {{F(ClassicBloodRules.RibbonHoldSeconds)}}, 0.0, {{F(ClassicBloodRules.RibbonTailFrom - ClassicBloodRules.RibbonHoldSeconds)}}) - {{F(ClassicBloodRules.RibbonTailDecay)}} * max(0.0, age - {{F(ClassicBloodRules.RibbonTailFrom)}}))
              * (1.0 - smoothstep({{F(ClassicBloodRules.RibbonSeconds - 0.15f)}}, {{F(ClassicBloodRules.RibbonSeconds)}}, age));
    float a = clamp(opacity * prof * taper * mottle * patches * env, 0.0, 1.0);
    //PASS
}
""";

    /// <summary>The blood pass: the tint mixed over the scene, darker in the dense mottled patches, never brighter than the tint.</summary>
    static readonly string MixOut = $$"""
    ALBEDO = tint.rgb * mix({{F(ClassicBloodRules.RibbonShadeFloor)}}, 1.0, smoothstep(0.15, 0.85, n));
    ALPHA = a;
""";

    /// <summary>
    /// The strip pass, drawn first (blend_mul): what lies under the blood keeps its red and loses its green and blue toward the tint's own share, so a translucent edge over light blue armour
    /// or a power weapon's flash reads as darker red, never pink or white (owner 2026-11-24, series 37 answer 4 "Blood only"). Full strength from 1 / <see cref="ClassicBloodRules.RibbonStripGain"/> of
    /// the band's opacity; where the band is clear (opacity 0) it multiplies by 1 and changes nothing.
    /// </summary>
    static readonly string StripOut = $$"""
    float strip = min(1.0, a * {{F(ClassicBloodRules.RibbonStripGain)}});
    ALBEDO = mix(vec3(1.0), vec3(1.0, tint.g / max(tint.r, 1e-3), tint.b / max(tint.r, 1e-3)), strip);
    ALPHA = 1.0;
""";

    static string Source(bool strip) => strip
        ? Code.Replace("render_mode blend_mix,", "render_mode blend_mul,").Replace("    //PASS", StripOut)
        : Code.Replace("    //PASS", MixOut);

    static ArrayMesh? _mesh;
    static Shader? _shader, _stripShader;
    static readonly StringName PAge = "age", PCtrl = "ctrl", PBack = "len_back", PFwd = "len_fwd", PCount = "count", PSpread = "spread", PSize = "size", PSeed = "seed", PTint = "tint", POpacity = "opacity";

    sealed class Spray
    {
        public MeshInstance3D Node = null!;
        public ShaderMaterial Material = null!;
        /// <summary>The strip pass drawn just before the blood (<see cref="StripOut"/>); its parameters are kept equal to <see cref="Material"/>'s.</summary>
        public ShaderMaterial Strip = null!;
        public void Set(StringName p, Variant v) { Material.SetShaderParameter(p, v); Strip.SetShaderParameter(p, v); }
        public bool On;
        public double Age;
    }

    readonly Spray[] _sprays = new Spray[MaxPool];
    readonly Vector3[] _ctrl = new Vector3[2 * Half + 1];
    readonly System.Numerics.Vector3[] _ctrlN = new System.Numerics.Vector3[2 * Half + 1];
    (double T, System.Numerics.Vector3 P)[] _pathN = new (double, System.Numerics.Vector3)[64];
    int _made, _next, _events;
    /// <summary>Sprays started, live sprays reused because every one was live (for logs and tests).</summary>
    public int Started { get; private set; }
    public int Evicted { get; private set; }
    public int Live { get { int n = 0; for (int i = 0; i < _made; i++) if (_sprays[i].On) n++; return n; } }

    static ArrayMesh SharedMesh()
    {
        if (_mesh is not null) return _mesh;
        int q = ClassicBloodRules.MaxRibbons;
        var rng = new Random(5413);
        int perRibbon = (Seg + 1) * 2;
        var verts = new Vector3[q * perRibbon]; var uvs = new Vector2[q * perRibbon]; var uv2 = new Vector2[q * perRibbon]; var cols = new Color[q * perRibbon];
        var idx = new int[q * Seg * 6];
        int ii = 0;
        for (int r = 0; r < q; r++)
        {
            var c = new Color((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            for (int k = 0; k <= Seg; k++)
                for (int e = 0; e < 2; e++)
                {
                    int v = r * perRibbon + k * 2 + e;
                    verts[v] = Vector3.Zero; uvs[v] = new Vector2(k / (float)Seg, e); uv2[v] = new Vector2(r, 0f); cols[v] = c;
                }
            for (int k = 0; k < Seg; k++)
            {
                int b = r * perRibbon + k * 2;
                idx[ii++] = b; idx[ii++] = b + 1; idx[ii++] = b + 2; idx[ii++] = b + 1; idx[ii++] = b + 3; idx[ii++] = b + 2;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Color] = cols;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        _mesh = new ArrayMesh();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _mesh.CustomAabb = new Aabb(new Vector3(-4f, -4f, -4f), new Vector3(8f, 8f, 8f));
        return _mesh;
    }

    public override void _Ready()
    {
        _shader ??= new Shader { Code = Source(strip: false) };
        _stripShader ??= new Shader { Code = Source(strip: true) };
        for (int i = 0; i < Pool; i++) Make();
    }

    Spray Make()
    {
        // after the power weapons' additive hit flash at the same contact (visibility pass): first the strip pass, then the blood mixed over it
        var m = new ShaderMaterial { Shader = _shader, RenderPriority = ClassicBloodRules.RibbonRenderPriority + 1 };
        var strip = new ShaderMaterial { Shader = _stripShader, RenderPriority = ClassicBloodRules.RibbonRenderPriority, NextPass = m };
        var node = new MeshInstance3D { Mesh = SharedMesh(), MaterialOverride = strip, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, TopLevel = true, Layers = FxBatch.FxRenderLayer };
        AddChild(node);
        var s = new Spray { Node = node, Material = m, Strip = strip };
        _sprays[_made++] = s;
        return s;
    }

    Spray Take()
    {
        for (int k = 0; k < _made; k++) { var s = _sprays[(_next + k) % _made]; if (!s.On) { _next = (_next + k + 1) % _made; return s; } }
        if (_made < MaxPool) return Make();
        Spray oldest = _sprays[0];
        for (int k = 1; k < _made; k++) if (_sprays[k].Age > oldest.Age) oldest = _sprays[k];
        Evicted++;
        return oldest;
    }

    /// <summary>Draws one empty spray for a moment at <paramref name="at"/>, so the shader's pipeline is prepared at the join rather than on the first hit.</summary>
    public void Prewarm(Vector3 at)
    {
        if (_made == 0) return;
        var s = Take();
        s.On = true; s.Age = ClassicBloodRules.RibbonSeconds - 0.25;
        s.Node.GlobalTransform = new Transform3D(Basis.Identity, at);
        s.Set(PCount, 0f);
        s.Set(PAge, (float)s.Age);
        s.Node.Visible = true;
    }

    /// <summary>
    /// One blood spray at <paramref name="contact"/> (world): ribbons along <paramref name="path"/> (the weapon's drawn points, world, with their times from the contact: before it
    /// negative; empty for a straight spray along <paramref name="dir"/>), <paramref name="kind"/>'s reach, <paramref name="amount"/> (1 normal, more heavy or lethal), a negative
    /// <paramref name="preAge"/> waits that long (a tracer in flight), a positive one starts it already aged (a late confirmation).
    /// </summary>
    public void Spawn(Vector3 contact, ReadOnlySpan<(double T, Vector3 P)> path, Vector3 dir, ClassicBloodRules.RibbonKind kind, float spread, float amount, double preAge)
    {
        if (_made == 0) return;
        if (_pathN.Length < path.Length) _pathN = new (double, System.Numerics.Vector3)[path.Length];
        for (int i = 0; i < path.Length; i++) _pathN[i] = (path[i].T, new System.Numerics.Vector3(path[i].P.X, path[i].P.Y, path[i].P.Z));
        var (back, fwd) = ClassicBloodRules.RibbonReach(kind, amount);
        ClassicBloodRules.RibbonPath(new ReadOnlySpan<(double, System.Numerics.Vector3)>(_pathN, 0, path.Length), new System.Numerics.Vector3(contact.X, contact.Y, contact.Z),
            new System.Numerics.Vector3(dir.X, dir.Y, dir.Z), back, fwd, _ctrlN);
        for (int i = 0; i < _ctrl.Length; i++) _ctrl[i] = new Vector3(_ctrlN[i].X, _ctrlN[i].Y, _ctrlN[i].Z);
        var s = Take();
        s.On = true; s.Age = preAge;
        s.Node.GlobalTransform = new Transform3D(Basis.Identity, contact);
        s.Set(PCtrl, _ctrl);
        s.Set(PBack, back);
        s.Set(PFwd, fwd);
        s.Set(PCount, (float)ClassicBloodRules.RibbonCount(amount));
        s.Set(PSpread, spread);
        s.Set(PSize, ClassicBloodRules.RibbonWidthScale(amount));
        s.Set(PSeed, (float)((_events++ * 0.137) % 1.0));
        s.Set(PTint, new Color(ClassicBloodRules.BloodRgb.R, ClassicBloodRules.BloodRgb.G, ClassicBloodRules.BloodRgb.B));   // = ClassicHitSpray.BloodTint; blood only (tested)
        s.Set(POpacity, ClassicBloodRules.RibbonOpacity);
        s.Set(PAge, (float)s.Age);
        s.Node.Visible = true;
        Started++;
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < _made; i++)
        {
            var s = _sprays[i];
            if (!s.On) continue;
            s.Age += delta;
            if (s.Age >= ClassicBloodRules.RibbonSeconds) { s.On = false; s.Node.Visible = false; continue; }
            s.Set(PAge, (float)s.Age);
        }
    }
}
