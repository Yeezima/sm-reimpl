using System;
using Godot;
using SmClient.ImpactRules;

namespace SmClient;

/// <summary>
/// classic-hit-effects, the Extended profile's blood spray (added content: gore; owner answer 4, 2026-11-24: "Redesign it"; client presentation only). A third design, measured on the
/// reference clip (review of 2b89de15): the blood is LAID along the weapon's path through the contact almost at once (full reach in about two frames), as 1 to 4 broad, flat, mottled bands on
/// both sides of the body (as many as the stroke's spread holds: a thrust one, a shot or a charge two, a sweep three, a heavy or lethal blow one more), fanning out past the body from
/// the middle band; then it HANGS in place (a slight lift and creep, no travel, no droop), brightest about 0.05-0.35 s after contact, strong to about 0.6 s and faint to about 1 s,
/// eroding into shrinking patches as it fades (never fading through translucency). The ribbons follow a curve built from the weapon's recorded samples before and after the contact
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
uniform float bundle = 0.756;              // the stack's height across the stroke (m; ClassicBloodRules.RibbonBundleFor: at least a band's width between neighbours)
uniform float size = 1.0;
uniform float mfloor = 0.25;              // the mottle floor (ClassicBloodRules.RibbonMottleFloorFor: higher for one- or two-band sprays)
uniform float grow = 1.0;                 // the share of the hang's 60 % growth this spray takes (ClassicBloodRules.RibbonGrow: the stack stays under its height cap)
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
        // the band's slot across the stack and its layout along the stroke (ClassicBloodRules.RibbonBandLayout; repair 4, finding N2: not a regular ladder of equal stripes): the outer
        // bands start past the body and are shorter and narrower, so the blood fans out from the middle band; one side of the stack (seeded) leads, so it reads as one slanted stroke;
        // each band has its own seeded shift and reach, so no two start or end together and no end is blunt
        float slot = count > 1.0 ? (ri + 0.5) / count - 0.5 : 0.0;
        float c2 = 2.0 * abs(slot);
        float lead = fract(seed * 7.31) < 0.5 ? -1.0 : 1.0;
        float shift = len_fwd * ({{F(ClassicBloodRules.RibbonStaggerOuter)}} * c2 + {{F(ClassicBloodRules.RibbonStaggerJitter)}} * (h2 - 0.5));
        float fwd = len_fwd * (0.6 + 0.4 * h1) * (1.0 - {{F(ClassicBloodRules.RibbonStaggerReach)}} * c2) * (1.0 + {{F(ClassicBloodRules.RibbonStaggerEchelon)}} * slot * lead);   // the leading side reaches farther: slanted far ends
        float back = len_back * (0.45 + 0.55 * h2) * (1.0 - {{F(2f * ClassicBloodRules.RibbonStaggerReach)}} * c2);
        float sl = mix(-back, fwd, UV.x);
        float s = shift + sl;
        float reveal = smoothstep(0.0, 0.05, age);          // laid at full reach within about two frames
        float hold = min(age, 0.6);
        float se = s * (0.3 + 0.7 * reveal) * (1.0 + 0.27 * hold);   // then the far end creeps on about 16 %
        vec3 p = curve(se);
        vec3 tng = curve(se + 0.03) - curve(se - 0.03);
        tng = length(tng) > 1e-5 ? normalize(tng) : vec3(1.0, 0.0, 0.0);
        vec3 side = abs(tng.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : normalize(cross(tng, vec3(0.0, 1.0, 0.0)));
        vec3 up2 = cross(side, tng);
        float dn = s >= 0.0 ? s / max(len_fwd, 1e-3) : -s / max(len_back, 1e-3);   // 0 at the contact, 1 at the full reach
        // 1 to 4 broad bands stacked across the stroke as the spread holds (ClassicBloodRules.RibbonBands), running straight through the body (no kink at the contact), a little wider
        // apart toward the far end, the bundle growing as it hangs (review 3: the reference's bands, its height +63 %), by the share `grow` that keeps the stack under
        // {{F(ClassicBloodRules.RibbonStackMax)}} m (RibbonGrow); neighbours at least a band's width apart (repair 3), so the stroke's direction reads
        vec3 across3 = normalize(up2 + side * 0.5 * (h3 - 0.5));
        float div = s <= 0.0 ? {{F(ClassicBloodRules.RibbonDivergeFrom)}} : mix({{F(ClassicBloodRules.RibbonDivergeFrom)}}, 1.0, clamp(s / max(len_fwd, 1e-3), 0.0, 1.0));   // ClassicBloodRules.RibbonDiverge: the bands leave the body together and part by angle (repair 5)
        float off = (slot * bundle * div + (h4 - 0.5) * 0.06) * (1.0 + grow * hold);
        p += across3 * off + vec3(0.0, 1.0, 0.0) * 0.2 * hold * (1.0 + 0.8 * grow * (h1 - 0.5));   // a lift of about 5 % of his height, no droop (the bands' lifts differ less as the growth is held back)
        vec3 pv = (MODELVIEW_MATRIX * vec4(p, 1.0)).xyz;
        vec3 tv = (MODELVIEW_MATRIX * vec4(p + tng * 0.05, 1.0)).xyz - pv;
        vec3 across = cross(length(tv) > 1e-6 ? normalize(tv) : vec3(0.0, 1.0, 0.0), normalize(pv));
        across = length(across) > 1e-5 ? normalize(across) : vec3(1.0, 0.0, 0.0);
        float w = size * {{F(ClassicBloodRules.RibbonHalfWidth)}} * (0.8 + 0.5 * h3) * (1.0 - {{F(ClassicBloodRules.RibbonStaggerWidth)}} * c2) * mix(1.0, 0.6, min(dn, 1.0)) * (1.0 + grow * hold);
        // no folds (repair of review 5, finding 2): a camera-facing strip wider than its curve's radius folds over itself on a bend and shows stepped polygon edges, so the half width
        // stays under {{F(ClassicBloodRules.RibbonFoldShare)}} of the local radius; and a band seen along its length (its tangent toward the camera) narrows, as a real ribbon would
        vec3 ta = curve(se + 0.11) - curve(se + 0.05), tb = curve(se - 0.05) - curve(se - 0.11);
        float kc = length(ta) > 1e-5 && length(tb) > 1e-5 ? length(normalize(ta) - normalize(tb)) / 0.16 : 0.0;
        w = min(w, {{F(ClassicBloodRules.RibbonFoldShare)}} / max(kc, 1e-3));
        float side_on = length(tv) > 1e-6 ? length(cross(normalize(tv), normalize(pv))) : 0.0;
        w *= mix({{F(ClassicBloodRules.RibbonEndOnWidth)}}, 1.0, smoothstep(0.15, 0.6, side_on));
        pv += across * (UV.y * 2.0 - 1.0) * w;
        v_dn = dn; v_s = s; v_w = w;
        v_end = sl >= 0.0 ? sl / max(fwd, 1e-3) : -sl / max(back, 1e-3);   // 0 at this band's own middle, 1 at its own ends
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
    // the band's shape as a field, 1 in its middle to 0 at its ragged edge and its staggered ends (the reference's broad mottled bands)
    float edge = 0.62 + 0.38 * vnoise(vec2(v_s * 5.0 + v_ri * 7.3 + seed * 13.0, v_ri * 3.1 + sign(x) * 4.7))
               + {{F(ClassicBloodRules.RibbonEdgeGrit)}} * (vnoise(vec2(v_s * 17.0 + v_ri * 2.3 + seed * 3.0, sign(x) * 6.1 + v_ri)) - 0.5);   // a finer octave on the ragged edge (repair 5)
    float prof = 1.0 - smoothstep(edge - 0.45, edge, abs(x));
    float taper = 1.0 - smoothstep(0.4, 1.0, v_end);
    float n = 0.6 * vnoise(vec2(v_s * 2.4 + seed * 17.0 + v_ri * 5.3, x * 1.1 + v_ri * 9.1 + age * 0.4))
            + 0.4 * vnoise(vec2(v_s * 7.5 - seed * 7.0 + v_ri * 2.7, x * 2.4 - age * 0.7));
    float grain = vnoise(vec2(v_s * 16.0 + v_ri * 3.7, x * 5.0 + seed * 11.0));
    float env = smoothstep(-0.01, 0.06, age) * exp(-{{F(ClassicBloodRules.RibbonDecay)}} * clamp(age - {{F(ClassicBloodRules.RibbonHoldSeconds)}}, 0.0, {{F(ClassicBloodRules.RibbonTailFrom - ClassicBloodRules.RibbonHoldSeconds)}}) - {{F(ClassicBloodRules.RibbonTailDecay)}} * max(0.0, age - {{F(ClassicBloodRules.RibbonTailFrom)}}))
              * (1.0 - smoothstep({{F(ClassicBloodRules.RibbonSeconds - 0.15f)}}, {{F(ClassicBloodRules.RibbonSeconds)}}, age));
    // ERODED, not faded (repair of review 5, finding 1: a fading translucent band let light floors show through as pink): a texel is blood (opacity about {{F(ClassicBloodRules.RibbonOpacity)}}) while its
    // mottled score stays above a threshold the envelope lowers (ClassicBloodRules.RibbonScore and RibbonThreshold), else clear; the bands break into shrinking patches and specks from
    // the edges inward and vanish, the covered area following the envelope (repair 3: RibbonCoverage, tested). Only a pixel-wide anti-aliased rim is in between, where the strip feathers with it:
    // the ground and the blood blended, no darker contour (repair 4, finding N3).
    float score = prof * taper * mix(mfloor, 1.0, smoothstep(0.15, 0.85, 0.75 * n + 0.25 * grain));
    // grit (repair 5, ClassicBloodRules.RibbonHole): small holes where a fine octave is high, more of them as the envelope falls, so late patches break into specks
    float fine = vnoise(vec2(v_s * {{F(ClassicBloodRules.RibbonGritScale)}} + v_ri * 4.1 + seed * 19.0, x * 7.0 + seed * 5.0));
    float hthr = {{F(ClassicBloodRules.RibbonHoleFrom)}} - {{F(ClassicBloodRules.RibbonHoleLate)}} * (1.0 - clamp(env, 0.0, 1.0));
    score *= 1.0 - smoothstep(hthr, hthr + 0.06, fine);
    float thr = 1.0 - {{F(1f - ClassicBloodRules.RibbonErodeFloor)}} * pow(max(env, 0.0), {{F(ClassicBloodRules.RibbonErodeCurve)}});
    float cov = clamp((score - thr) / max(1.5 * fwidth(score), 1e-4) + 0.5, 0.0, 1.0);
    // inside a patch the blood is mottled in density too: where it is thinner, the ground shows through as its own red only (the strip pass), so it reads as darker red, never pink
    float a = opacity * cov * mix({{F(ClassicBloodRules.RibbonDensityFloor)}}, 1.0, smoothstep(0.1, 0.9, 0.5 * n + 0.5 * grain));
    //PASS
}
""";

    /// <summary>The blood pass: the tint mixed over the scene, darker in the dense mottled patches, never brighter than the tint.</summary>
    static readonly string MixOut = $$"""
    ALBEDO = tint.rgb * mix({{F(ClassicBloodRules.RibbonShadeFloor)}}, {{F(ClassicBloodRules.RibbonShadeTop)}}, smoothstep(0.15, 0.85, n));
    ALPHA = a;
""";

    /// <summary>
    /// The strip pass, drawn first (blend_mul): what lies under the blood keeps <see cref="ClassicBloodRules.RibbonStripRed"/> of its red and loses all its green and blue, so a thinner
    /// patch of the mottle over light blue armour, a bright red surface or a power weapon's flash reads as darker red, never pink, orange or white (owner 2026-11-24, series 37
    /// answer 4 "Blood only"; repair 3, finding 3). Full strength wherever the patch covers the pixel, feathered over the patch's own anti-aliased rim (<see cref="ClassicBloodRules.RibbonStrip"/>;
    /// repair 4, finding N3: a full strip under the rim's faint blood drew a dark contour); where the band is clear it multiplies by 1 and changes nothing.
    /// </summary>
    static readonly string StripOut = $$"""
    float strip = cov * cov * cov;   // feathered with the patch's own anti-aliased edge (ClassicBloodRules.RibbonStrip; repair 4, finding N3: full from 1/255 it drew a dark contour)
    ALBEDO = mix(vec3(1.0), vec3({{F(ClassicBloodRules.RibbonStripRed)}}, 0.0, 0.0), strip);   // darken the red under the band, take out all its green and blue: only the blood's own green and blue remain
    ALPHA = 1.0;
""";

    static string Source(bool strip) => strip
        ? Code.Replace("render_mode blend_mix,", "render_mode blend_mul,").Replace("    //PASS", StripOut)
        : Code.Replace("    //PASS", MixOut);

    static ArrayMesh? _mesh;
    static Shader? _shader, _stripShader;
    static readonly StringName PAge = "age", PCtrl = "ctrl", PBack = "len_back", PFwd = "len_fwd", PCount = "count", PBundle = "bundle", PSize = "size", PSeed = "seed", PGrow = "grow", PFloor = "mfloor", PTint = "tint", POpacity = "opacity";

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
    int _made, _next, _events = 1;   // the first spray takes RibbonSeed(1), not RibbonSeed(0), a seed whose one- and two-band sprays erode early (repair 5, finding 4)
    /// <summary>The prewarm spray while it lives: kept in front of the camera every frame (repair 5, finding 5), so its pipelines are built whatever the camera does.</summary>
    Spray? _warm;
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
        s.On = true; s.Age = ClassicBloodRules.RibbonSeconds - 1.0;   // drawn (empty) for a second: both passes' pipelines are built in the background meanwhile (repair 4, finding N6)
        s.Node.GlobalTransform = new Transform3D(Basis.Identity, at);
        _warm = s;
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
        if (ReferenceEquals(s, _warm)) _warm = null;
        s.On = true; s.Age = preAge;
        s.Node.GlobalTransform = new Transform3D(Basis.Identity, contact);
        s.Set(PCtrl, _ctrl);
        s.Set(PBack, back);
        s.Set(PFwd, fwd);
        int count = ClassicBloodRules.RibbonBands(kind, spread, amount);   // the spread drives the stack (repair 4, finding N1)
        float size = ClassicBloodRules.RibbonWidthScale(amount);
        float bundle = ClassicBloodRules.RibbonBundleFor(count, spread, size);
        s.Set(PCount, (float)count);
        s.Set(PBundle, bundle);
        s.Set(PGrow, ClassicBloodRules.RibbonGrow(count, bundle, size, fwd, back));
        s.Set(PSize, size);
        s.Set(PSeed, ClassicBloodRules.RibbonSeed(_events++));   // golden-ratio steps from 0.5 (repair 5, finding 4: the first spray always took seed 0, which erodes early)
        s.Set(PFloor, ClassicBloodRules.RibbonMottleFloorFor(count));
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
            if (s.Age >= ClassicBloodRules.RibbonSeconds) { s.On = false; s.Node.Visible = false; if (ReferenceEquals(s, _warm)) _warm = null; continue; }
            if (ReferenceEquals(s, _warm) && GetViewport()?.GetCamera3D() is { } cam) s.Node.GlobalTransform = new Transform3D(Basis.Identity, cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 3f);
            s.Set(PAge, (float)s.Age);
        }
    }
}
