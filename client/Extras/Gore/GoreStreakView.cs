using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;
using SmClient.ImpactRules;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// gore-streak (added content: gore; owner 2026-11-25; ribbon rebuild, owner card 2026-11-26): the blood streak renderer shared by Classic PvP with the Extended gore and by Warfront.
/// Every streak is up to <see cref="GoreStreakRules.MaxBranches"/> ribbons, each one instance of a single <see cref="MultiMesh"/> whose mesh is a thin strip of
/// <see cref="GoreStreakRules.Segments"/> segments: the vertex stage lays each strip out on the streak's tilted plane (its band's angle, length, width profile and bend at the instance's
/// age, from the per-frame table and band layouts generated into the shader: all the trigonometry per vertex, none per pixel), and the fragment stage draws it with the startup-made
/// spray texture mapped onto the plane (soft flow streaks), the soft body with its lumpy, jagged edge, the outer fade and the large jagged tears (gore round 4;
/// <see cref="GoreStreakRules.TexelAt"/> mirrors it). The CPU sets one clock uniform a frame and an instance's transform, colour and custom data only when a streak spawns (or a merged
/// hit holds it back). One pass: a translucent red mixed over the ground (pinkish where thin, deeper red in the dense streaks; owner 2026-11-26), times the level's ambient and sun
/// light so it never glows on a dark map; never additive or white. Drawn at <see cref="ClassicBloodRules.RibbonRenderPriority"/> plus one, above the power weapons' flash.
/// <para>Instance (streak slot x 6 + band): transform = the plane frame (X exit axis, Y across leaning up, Z normal) x metres per table unit; CUSTOM = (start on the view clock incl.
/// pre-age, seed, bend, width multiplier); COLOR = (band / 8, entry share, on, the fan share: 0 for the prewarm).</para>
/// </summary>
public partial class GoreStreakView : Node3D
{
    static string F(float v) => v.ToString("0.0#####", CultureInfo.InvariantCulture);

    /// <summary>The clock period (s): the shader takes the clock modulo this, so its float keeps millisecond precision.</summary>
    const double ClockPeriod = 4096.0;
    const int NB = GoreStreakRules.MaxBranches;

    static string KeyArray(Func<GoreStreakRules.Key, float> pick)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < GoreStreakRules.Keys.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(F(pick(GoreStreakRules.Keys[i]))); }
        return sb.ToString();
    }

    static string BandArray(Func<GoreStreakRules.BandShape, float> pick)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < NB; k++) { if (k > 0) sb.Append(", "); sb.Append(F(pick(GoreStreakRules.Bands[k]))); }
        return sb.ToString();
    }

    /// <summary>The one pass (gore round 4): a translucent red mixed over the ground, unshaded, times the level's measured ambient and sun light (<see cref="SceneLight"/>); the same values on both renderers.</summary>
    static string ShaderCode() => $$"""
shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;

uniform float now = 0.0;                 // the view clock modulo {{F((float)ClockPeriod)}} s
uniform float age_override = -1.0;       // >= 0: every streak at this age (the capture aid)
uniform float probe = -1.0;              // >= 0: the capture aid's colour probe (every band drawn solid at this opacity)
uniform sampler2D fil : filter_linear_mipmap_anisotropic, repeat_enable;   // GoreStreakRules.FilamentTexels: R flow noise, G blotches, B grain
uniform vec3 thin_rgb = vec3({{F(GoreStreakRules.ThinRgb.R)}}, {{F(GoreStreakRules.ThinRgb.G)}}, {{F(GoreStreakRules.ThinRgb.B)}});      // GoreStreakRules.ThinRgb (scene linear)
uniform vec3 dense_rgb = vec3({{F(GoreStreakRules.DenseRgb.R)}}, {{F(GoreStreakRules.DenseRgb.G)}}, {{F(GoreStreakRules.DenseRgb.B)}});   // GoreStreakRules.DenseRgb
uniform float near_from = {{F(GoreStreakRules.NearFadeFrom)}};
uniform float near_to = {{F(GoreStreakRules.NearFadeTo)}};
uniform float scene_light = {{F(GoreStreakRules.SceneLight)}};   // the level's ambient and sun light on the blood (GoreStreakView.SceneLight, measured on the CPU): it never glows on a dark map
uniform float spread = 1.0;              // the per-hit variation's share (GoreStreakRules.SprayTilt, BandScatter, LengthVariation): 1, or 0 with --old-gore-spread
uniform bool srgb_out = false;           // Compatibility: its albedo goes through display-to-linear once more on the way, so the scene-linear values are sent in display form

const int NK = {{GoreStreakRules.Keys.Length}};
const float K_T[NK] = { {{KeyArray(k => k.T)}} };
const float K_EXIT[NK] = { {{KeyArray(k => k.Exit)}} };
const float K_ENTRY[NK] = { {{KeyArray(k => k.Entry)}} };
const float K_FAN[NK] = { {{KeyArray(k => k.Fan)}} };
const float K_WIDTH[NK] = { {{KeyArray(k => k.Width)}} };
const float K_FILL[NK] = { {{KeyArray(k => k.Fill)}} };
const float K_OPAC[NK] = { {{KeyArray(k => k.Opacity)}} };
const float K_GRIT[NK] = { {{KeyArray(k => k.Grit)}} };
const float K_BR[NK] = { {{KeyArray(k => k.Branches)}} };
const float K_OUT[NK] = { {{KeyArray(k => k.Outer)}} };
const float K_ROOT[NK] = { {{KeyArray(k => k.Root)}} };
const float K_HAZE[NK] = { {{KeyArray(k => k.Haze)}} };
const float K_DEPTH[NK] = { {{KeyArray(k => k.Depth)}} };
const int NB = {{NB}};
const float B_ANG[NB] = { {{BandArray(b => b.Angle)}} };
const float B_REACH[NB] = { {{BandArray(b => b.Reach)}} };
const float B_WID[NB] = { {{BandArray(b => b.Width)}} };
const float B_ROOT[NB] = { {{BandArray(b => b.Root)}} };
const float B_ACROSS[NB] = { {{BandArray(b => b.Across)}} };
const float B_AJ[NB] = { {{BandArray(b => b.AngleJitter)}} };
const float B_RJ[NB] = { {{BandArray(b => b.ReachJitter)}} };
const float B_BEND[NB] = { {{BandArray(b => b.Bend)}} };
const float B_EARLY[NB] = { {{BandArray(b => b.Early)}} };

varying vec3 v_uvs;            // along 0..1, across (1 at the body's nominal edge), along in table units
varying float v_depth;
varying flat float v_hw;       // the band's half width (table units)
varying flat vec4 v_k;         // opacity x band share x shown, fill, grit, outer
varying flat vec4 v_r;         // root, texture offset u, texture offset v, haze
varying flat float v_dp;       // depth (the deeper red's share)

vec3 out_col(vec3 c) { return srgb_out ? mix(12.92 * c, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)) : c; }
float h01(float seed, float salt) { return fract(sin(seed * 12.9898 + salt * 78.233) * 43758.5453); }   // GoreStreakRules.ShaderHash
float prof(float u) { return (0.7 + 0.3 * smoothstep(0.0, 0.5, u)) * (1.0 - 0.6 * smoothstep(0.82, 1.0, u)); }   // GoreStreakRules.Profile

void vertex() {
    float start = INSTANCE_CUSTOM.x;
    float age = age_override >= 0.0 ? age_override : mod(now - start + {{F((float)ClockPeriod)}}, {{F((float)ClockPeriod)}});
    int band = clamp(int(COLOR.r * 8.0 + 0.5), 0, NB - 1);
    float seed = INSTANCE_CUSTOM.y;
    float uu = VERTEX.x, vv = VERTEX.y;
    v_uvs = vec3(uu, vv * {{F(GoreStreakRules.EdgePad)}}, 0.0);
    v_k = vec4(0.0); v_r = vec4(0.0); v_dp = 0.0;
    v_depth = 0.0; v_hw = 0.0;
    // clipped: not born yet, gone, or retired (COLOR.b 0: a spent slot or a band this streak does not draw)
    if (COLOR.b < 0.5 || age < 0.0 || age >= {{F(GoreStreakRules.LifeSeconds)}}) {
        POSITION = vec4(2.0, 2.0, 2.0, 1.0);
    } else {
        int i = 0;
        for (int j = 0; j < NK - 2; j++) { if (age >= K_T[j + 1]) i = j + 1; }
        float fade = 1.0;
        float u;
        int b = i + 1;
        if (age >= K_T[NK - 1]) { i = NK - 1; b = NK - 1; u = 0.0; fade = 1.0 - (age - K_T[NK - 1]) / ({{F(GoreStreakRules.LifeSeconds)}} - K_T[NK - 1]); }
        else u = (age - K_T[i]) / (K_T[b] - K_T[i]);
        float ex = mix(K_EXIT[i], K_EXIT[b], u), en = mix(K_ENTRY[i], K_ENTRY[b], u), fan = mix(K_FAN[i], K_FAN[b], u), wd = mix(K_WIDTH[i], K_WIDTH[b], u);
        float g = clamp(mix(K_BR[i], K_BR[b], u) - 1.0, 0.0, 1.0);
        // the band at this age (GoreStreakRules.BandOf)
        float ja = (h01(seed, 20.0 + float(band)) - 0.5) * 2.0 * B_AJ[band];
        float jr = 1.0 + (h01(seed, 30.0 + float(band)) - 0.5) * 2.0 * B_RJ[band];
        float deg, len, w = wd * B_WID[band] * INSTANCE_CUSTOM.w, alpha = 1.0;
        if (band == 2) {
            deg = 180.0 + B_ANG[2] + ja; len = en * COLOR.g * jr;
            alpha = 1.0 - smoothstep({{F(GoreStreakRules.EntryFadeFrom)}}, {{F(GoreStreakRules.EntryFadeTo)}}, age);
        } else {
            float gb = B_EARLY[band] + (1.0 - B_EARLY[band]) * g;
            deg = B_ANG[band] * fan * COLOR.a + ja; len = ex * B_REACH[band] * jr * (0.35 + 0.65 * gb); w *= sqrt(gb);   // COLOR.a: the source's fan share (GoreStreakRules.FanShareOf; 0 for the prewarm: not shown)
            if (band == 1) alpha = 1.0 - {{F(GoreStreakRules.UpperFade)}} * smoothstep({{F(GoreStreakRules.Keys[2].T)}}, {{F(GoreStreakRules.Keys[4].T)}}, age);
            if (gb <= 0.0) { len = 0.0; alpha = 0.0; }
        }
        // the hit's variation (owner 2026-11-28; GoreStreakRules.Varied): the band's own length and in-plane turn about its root
        len *= 1.0 + spread * ({{F(GoreStreakRules.SprayLengthShare)}} * (2.0 * h01(seed, {{F(GoreStreakRules.SprayLengthSalt)}}) - 1.0) + {{F(GoreStreakRules.BandLengthShare)}} * (2.0 * h01(seed, {{F(GoreStreakRules.BandLengthSalt)}} + float(band)) - 1.0));
        float br = spread * {{F(GoreStreakRules.BandScatterDegrees * MathF.PI / 180f)}} * sqrt(h01(seed, {{F(GoreStreakRules.ScatterSalt)}} + float(band))), bp = 6.2831853 * h01(seed, {{F(GoreStreakRules.ScatterTurnSalt)}} + float(band));
        float ang = radians(deg) + br * cos(bp);
        float bet = br * sin(bp);
        float bend = INSTANCE_CUSTOM.z * B_BEND[band];
        float phase = h01(seed, 60.0 + float(band)) * 6.2831853;
        // the strip vertex on the plane (GoreStreakRules.RibbonPoint, GoreStreakRules.Meander)
        float cs = cos(ang), sn = sin(ang);
        float s = uu * len;
        float meander = ({{F(GoreStreakRules.MeanderWidth)}} * w + {{F(GoreStreakRules.MeanderLength)}} * len) * sin(3.14159265 * uu) * sin(6.2831853 * s / {{F(GoreStreakRules.MeanderWave)}} + phase);
        float across = bend * s * s / max(len, 0.05) + meander + vv * w * prof(uu) * {{F(GoreStreakRules.EdgePad)}};
        vec2 rt = vec2(B_ROOT[band] * ex, B_ACROSS[band] * (0.3 + 0.7 * g * g));   // the roots part late (GoreStreakRules.BandOf)
        vec2 pp = rt + vec2(cs * s - sn * across, sn * s + cs * across);
        // the band turned out of the plane about its root, then the whole spray turned about the wound (GoreStreakRules.VariedPoint): the start stays at the wound, the far ends scatter
        float ra = dot(pp - rt, vec2(cs, sn));
        pp -= vec2(cs, sn) * ra * (1.0 - cos(bet));
        float pz = ra * sin(bet);
        float tr = spread * {{F(GoreStreakRules.SprayTiltDegrees * MathF.PI / 180f)}} * sqrt(h01(seed, {{F(GoreStreakRules.TiltSalt)}})), tq = 6.2831853 * h01(seed, {{F(GoreStreakRules.TiltTurnSalt)}});
        float t_in = tr * cos(tq), t_out = tr * sin(tq);
        pp = vec2(pp.x * cos(t_in) - pp.y * sin(t_in), pp.x * sin(t_in) + pp.y * cos(t_in));
        VERTEX = vec3(pp.x * cos(t_out) - pz * sin(t_out), pp.y, pp.x * sin(t_out) + pz * cos(t_out));
        vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
        v_depth = -view.z;
        POSITION = PROJECTION_MATRIX * view;   // written in both branches: once a shader writes POSITION, Godot takes it for every vertex
        v_uvs.z = s; v_hw = w;
        v_k = vec4(mix(K_OPAC[i], K_OPAC[b], u) * fade * alpha * step(0.01, COLOR.a), mix(K_FILL[i], K_FILL[b], u) * fade, mix(K_GRIT[i], K_GRIT[b], u), mix(K_OUT[i], K_OUT[b], u));
        v_r = vec4(mix(K_ROOT[i], K_ROOT[b], u), h01(seed, 50.0 + float(band)), h01(seed, 54.0 + float(band)), mix(K_HAZE[i], K_HAZE[b], u));
        v_dp = mix(K_DEPTH[i], K_DEPTH[b], u);
    }
}

void fragment() {
    float u = v_uvs.x, v = v_uvs.y, s = v_uvs.z;
    float av = abs(v);
    float hwv = v * v_hw;   // across in table units
    vec2 o = v_r.yz;
    // the spray texture on the plane (GoreStreakRules.TexelAt)
    float flow = texture(fil, vec2(s / {{F(GoreStreakRules.FlowAlongH)}} + o.x, hwv / {{F(GoreStreakRules.FlowAcrossH)}} + o.y)).r;
    float jag = texture(fil, vec2(s / {{F(GoreStreakRules.JagAlongH)}} + o.x * 1.9, hwv / {{F(GoreStreakRules.JagAcrossH)}} + o.y * 1.3)).r;
    float gap = texture(fil, vec2(s / {{F(GoreStreakRules.GapAlongH)}} + o.x * 3.1, hwv / {{F(GoreStreakRules.GapAcrossH)}} + o.y * 2.3)).g;
    float grain = texture(fil, vec2(s / {{F(GoreStreakRules.GrainAlongH)}} + o.x * 1.3, hwv / {{F(GoreStreakRules.GrainAcrossH)}} + o.y * 1.7)).b;
    float lump = texture(fil, vec2(s * {{F(GoreStreakRules.LumpAlong)}} + o.x * 2.0, (v > 0.0 ? 0.3 : 0.7) + o.y)).g;
    // the soft body with a lumpy, jagged edge
    float d = av + {{F(GoreStreakRules.LumpDepth)}} * (lump - 0.5) + {{F(GoreStreakRules.JagEdge)}} * (jag - 0.5) * min(av + 0.3, 1.0);
    float body = 1.0 - smoothstep({{F(GoreStreakRules.BodyFrom)}} * (1.0 - 0.7 * v_r.w), 1.0, d);
    float along = smoothstep(0.0, 0.06, u) * (1.0 - smoothstep(0.72, 1.0, u + 0.15 * (gap - 0.5)));
    float outer = 1.0 - v_k.w * smoothstep(0.45, 1.0, u);
    // the large jagged tears against 1 - Fill; the root and the tip held
    float thr = 1.0 - v_k.y;
    float tn = 0.75 * gap + 0.25 * flow + {{F(GoreStreakRules.TearJag)}} * (0.5 + v_k.z) * (jag - 0.5);
    float keep = smoothstep(thr - {{F(GoreStreakRules.TearSoft)}}, thr + {{F(GoreStreakRules.TearSoft)}}, tn);
    float held = v_r.x * (1.0 - smoothstep({{F(0.6f * GoreStreakRules.RootShare)}}, {{F(GoreStreakRules.RootShare)}}, u));
    float tip = {{F(GoreStreakRules.TipHold)}} * (1.0 - v_r.x) * smoothstep(0.7, 0.85, u) * smoothstep(0.35, 0.65, flow) * smoothstep(0.0, 0.1, v_k.y);
    keep = max(keep, max(held, tip));
    float ff = mix({{F(GoreStreakRules.FlowFloor)}}, {{F(GoreStreakRules.HazeFlowFloor)}}, v_r.w);   // the late haze evener (no breaking into specks)
    float a = v_k.x * body * along * outer * keep * (ff + (1.0 - ff) * flow) * (1.0 + {{F(GoreStreakRules.GrainGain)}} * (1.0 - {{F(GoreStreakRules.HazeGrainCut)}} * v_r.w) * (grain - 0.5));
    a = min({{F(GoreStreakRules.AlphaMax)}}, a * {{F(GoreStreakRules.AlphaGain)}} * (srgb_out ? {{F(GoreStreakRules.CompatAlphaGain)}} : 1.0));   // GoreStreakRules.CompatAlphaGain
    float dens = smoothstep(0.35, 0.95, flow * body) * v_dp;
    if (probe >= 0.0) { a = (av < 1.0 && u > 0.1 && u < 0.9) ? probe : 0.0; dens = 0.5; }
    a *= smoothstep(near_from, near_to, v_depth);
    if (a < 0.004) discard;
    vec3 c = mix(thin_rgb, dense_rgb, dens);
    if (srgb_out) c.gb = mix(c.gb, vec2(c.r), {{F(GoreStreakRules.CompatLift)}});   // Compatibility mixes in display values: a little more green and blue keeps its result on Forward+'s (GoreStreakRules.CompatLift)
    ALBEDO = out_col(c * scene_light);
    ALPHA = a;
}
""";

    static Shader? _shader;
    static ImageTexture? _tex;
    static ArrayMesh? _strip;
    static readonly StringName PNow = "now", PAgeOverride = "age_override", PFil = "fil", PNearFrom = "near_from", PNearTo = "near_to", PSrgbOut = "srgb_out", PProbe = "probe", PSceneLight = "scene_light", PSpread = "spread";

    MultiMesh _mm = null!;
    MultiMeshInstance3D _node = null!;
    ShaderMaterial _mat = null!;
    readonly double[] _starts = new double[GoreStreakRules.PoolSize];
    readonly bool[] _own = new bool[GoreStreakRules.PoolSize];
    readonly int[] _slotVictim = new int[GoreStreakRules.PoolSize];
    readonly double[] _lastHit = new double[GoreStreakRules.PoolSize];
    readonly bool[] _lethal = new bool[GoreStreakRules.PoolSize];
    readonly float[] _scale = new float[GoreStreakRules.PoolSize];
    readonly Dictionary<int, int> _victimSlot = new();
    readonly List<int> _victimStale = new();
    double _clock;
    long _spawned;
    int _high;
    int _prewarmSlot = -1;
    double _prewarmUntil = double.NegativeInfinity;
    int _live, _liveOwn;
    readonly System.Diagnostics.Stopwatch _watch = new();

    /// <summary>Streaks started, dropped by a caller's budget (<see cref="NoteDropped"/>), live ones evicted from a full pool, and ones that replaced their victim's live streak.</summary>
    public long Spawned { get; private set; }
    public long Dropped { get; private set; }
    public long Evicted { get; private set; }
    public long Replaced { get; private set; }
    /// <summary>Hits merged into their victim's running streak (<see cref="GoreStreakRules.Join.Merge"/>: no new streak, the running one held at its peak).</summary>
    public long Merged { get; private set; }
    /// <summary>CPU spent in <see cref="Spawn"/> and the frame's clock update (ms, since the last <see cref="TakeCpuMs"/>).</summary>
    double _cpuMs;
    public double TakeCpuMs() { double v = _cpuMs; _cpuMs = 0; return v; }
    public void NoteDropped(int n = 1) => Dropped += n;

    /// <summary>Called once a frame before the view's clock moves (Warfront admits the frame's waiting streaks here, own and nearest first).</summary>
    public Action? BeforeFrame;

    /// <summary>When set, every streak is drawn at this age (s) and the clock stops mattering (the capture aid, <c>--gore-streak-capture</c>).</summary>
    public float? AgeOverride
    {
        get => _ageOverride;
        set { _ageOverride = value; _mat?.SetShaderParameter(PAgeOverride, value ?? -1f); }
    }
    float? _ageOverride;

    /// <summary>The capture aid's colour probe (<c>--gore-streak-probe</c>): every band drawn solid at this opacity, or null (off).</summary>
    public float? Probe
    {
        get => _probe;
        set { _probe = value; _mat?.SetShaderParameter(PProbe, value ?? -1f); }
    }
    float? _probe;

    /// <summary>The near fade (m): Classic <see cref="GoreStreakRules.NearFadeFrom"/>..To, Warfront <see cref="GoreStreakRules.WarfrontNearFadeFrom"/>..To.</summary>
    public void SetNearFade(float from, float to)
    {
        _nearFrom = from; _nearTo = to;
        if (_mat is null) return;
        _mat.SetShaderParameter(PNearFrom, from); _mat.SetShaderParameter(PNearTo, to);
    }
    float _nearFrom = GoreStreakRules.NearFadeFrom, _nearTo = GoreStreakRules.NearFadeTo;

    /// <summary>The view clock (s since the view was made).</summary>
    public double Clock => _clock;

    /// <summary>Streaks showing now (counted once a frame, finding L3, plus the ones started since), and the own player's among them.</summary>
    public int Live => _live;
    public int LiveOwn => _liveOwn;

    /// <summary>The shader source (for tests and the log).</summary>
    public static string Source() => ShaderCode();

    /// <summary>The strip mesh: <see cref="GoreStreakRules.Segments"/> segments along (x = 0..1), two vertices across (y = -1, 1); the vertex stage lays it out.</summary>
    static ArrayMesh StripMesh()
    {
        if (_strip is not null) return _strip;
        int n = GoreStreakRules.Segments;
        var verts = new Vector3[2 * (n + 1)];
        var normals = new Vector3[verts.Length];
        for (int i = 0; i <= n; i++)
        {
            float u = i / (float)n;
            verts[2 * i] = new Vector3(u, -1f, 0f); verts[2 * i + 1] = new Vector3(u, 1f, 0f);
            normals[2 * i] = normals[2 * i + 1] = Vector3.Back;
        }
        var idx = new int[6 * n];
        for (int i = 0; i < n; i++)
        {
            int a = 2 * i;
            idx[6 * i] = a; idx[6 * i + 1] = a + 2; idx[6 * i + 2] = a + 3; idx[6 * i + 3] = a; idx[6 * i + 4] = a + 3; idx[6 * i + 5] = a + 1;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        _strip = new ArrayMesh();
        _strip.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return _strip;
    }

    static ImageTexture Filament()
    {
        if (_tex is not null) return _tex;
        int n = GoreStreakRules.FilamentSize;
        var image = Image.CreateFromData(n, n, false, Image.Format.Rgba8, GoreStreakRules.FilamentTexels(n, 1));
        image.GenerateMipmaps();
        return _tex = ImageTexture.CreateFromImage(image);
    }

    /// <summary>Capture aid <c>--capture-1080</c> (live recordings with <c>--write-movie</c>): the window made borderless at 1920 x 1080 when the view starts (the private test desktop otherwise caps it).</summary>
    static readonly bool Capture1080 = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--capture-1080") >= 0;

    /// <summary>Before / after switch <c>--old-gore-spread</c> (<see cref="GoreStreakRules.OldSpreadFlag"/>): no per-hit variation (the look before 2026-11-28).</summary>
    static readonly bool OldSpread = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--" + GoreStreakRules.OldSpreadFlag) >= 0;

    /// <summary>Makes <paramref name="w"/> a borderless 1920 x 1080 window at the corner (true 1080p frames for the capture aids).</summary>
    public static void CaptureWindow(Window w)
    {
        w.Borderless = true;
        w.Position = Vector2I.Zero;
        w.Size = new Vector2I(1920, 1080);
    }

    public override void _Ready()
    {
        if (Capture1080) CaptureWindow(GetWindow());
        _shader ??= new Shader { Code = ShaderCode() };
        _mat = new ShaderMaterial { Shader = _shader, RenderPriority = ClassicBloodRules.RibbonRenderPriority + 1 };
        // the same values on both renderers (both blend in linear light before the tonemapper; Compatibility takes them in display form)
        _mat.SetShaderParameter(PFil, Filament()); _mat.SetShaderParameter(PAgeOverride, _ageOverride ?? -1f);
        _mat.SetShaderParameter(PNearFrom, _nearFrom); _mat.SetShaderParameter(PNearTo, _nearTo);
        _mat.SetShaderParameter(PSrgbOut, RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility"); _mat.SetShaderParameter(PProbe, _probe ?? -1f);
        _mat.SetShaderParameter(PSpread, OldSpread ? 0f : 1f);   // --old-gore-spread: every band on its designed line and reach (the look before 2026-11-28)
        _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = StripMesh() };
        _mm.InstanceCount = GoreStreakRules.PoolSize * NB;
        _mm.VisibleInstanceCount = 0;
        for (int i = 0; i < _starts.Length; i++) _starts[i] = double.NegativeInfinity;
        _node = new MultiMeshInstance3D
        {
            Name = "GoreStreaks", Multimesh = _mm, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true,
            Layers = FxBatch.FxRenderLayer, CustomAabb = new Aabb(-Vector3.One * GoreStreakRules.ViewBoundsMetres, Vector3.One * (2f * GoreStreakRules.ViewBoundsMetres)),
        };
        AddChild(_node);
    }

    int TakeSlot()
    {
        int slot = GoreStreakRules.PoolSlot(_spawned++);
        if (slot == _prewarmSlot) { _prewarmSlot = -1; _prewarmUntil = double.NegativeInfinity; }
        else if (GoreStreakRules.Live(_starts[slot], _clock)) { Evicted++; _live--; if (_own[slot]) _liveOwn--; }
        if (slot + 1 > _high) { _high = slot + 1; _mm.VisibleInstanceCount = _high * NB; }
        return slot;
    }

    static Transform3D Placement(Num.Vector3 origin, Num.Vector3 axis, Num.Vector3 normal, float unit)
    {
        var x = new Vector3(axis.X, axis.Y, axis.Z).Normalized();
        var z = new Vector3(normal.X, normal.Y, normal.Z);
        z = (z - x * z.Dot(x)).Normalized();
        var y = z.Cross(x).Normalized();
        if (y.Y < 0f) { y = -y; z = -z; }   // the plane's +across leans up: the upper band rises (gate repair 4)
        return new Transform3D(new Basis(x * unit, y * unit, z * unit), new Vector3(origin.X, origin.Y, origin.Z));
    }

    /// <summary>
    /// Starts one streak (Godot world, see <see cref="GoreStreakRules.Streak"/>) on <paramref name="victim"/> (0: none known), or joins the victim's running streak
    /// (<see cref="GoreStreakRules.JoinOf"/>): one streak per victim at a time. A hit within <see cref="GoreStreakRules.MergeSeconds"/> of the victim's last one, or on a streak younger
    /// than <see cref="GoreStreakRules.HoldUntilAge"/>, merges into it (held back to <see cref="GoreStreakRules.HoldAge"/>; a kill's streak is never replaced by a hit that did not
    /// kill); a killing or heavier hit on a running non-killing streak, or any hit on a faded one, takes its slot and starts over; with none showing a new streak. Returns its slot.
    /// </summary>
    public int Spawn(in GoreStreakRules.Streak s, int victim = 0, bool own = false)
    {
        if (_mm is null) return -1;
        _watch.Restart();
        int slot;
        if (victim != 0 && _victimSlot.TryGetValue(victim, out int vs) && _slotVictim[vs] == victim && vs != _prewarmSlot)
        {
            switch (GoreStreakRules.JoinOf(_starts[vs], _lastHit[vs], _lethal[vs], _clock, s.Lethal, _scale[vs], s.Scale))
            {
                case GoreStreakRules.Join.Ignore:   // a hit on a corpse after the kill's hold: the kill's streak fades out
                    _cpuMs += _watch.Elapsed.TotalMilliseconds;
                    return vs;
                case GoreStreakRules.Join.Merge:
                {
                    if (!_lethal[vs]) _lastHit[vs] = _clock;   // a kill's streak keeps its kill time (GoreStreakRules.KillHoldSeconds)
                    double held = GoreStreakRules.HoldStart(_starts[vs], _clock);
                    if (held != _starts[vs])
                    {
                        _starts[vs] = held;
                        for (int b = 0; b < NB; b++)
                        {
                            int i = vs * NB + b;
                            var c = _mm.GetInstanceCustomData(i);
                            c.R = (float)(((held % ClockPeriod) + ClockPeriod) % ClockPeriod);
                            _mm.SetInstanceCustomData(i, c);
                        }
                    }
                    Merged++;
                    _cpuMs += _watch.Elapsed.TotalMilliseconds;
                    return vs;
                }
                case GoreStreakRules.Join.Upgrade:
                    slot = vs; Replaced++;
                    if (GoreStreakRules.Live(_starts[vs], _clock)) { _live--; if (_own[vs]) _liveOwn--; }
                    break;
                default:
                    slot = TakeSlot();
                    break;
            }
        }
        else slot = TakeSlot();
        double start = _clock - s.PreAge;
        _starts[slot] = start;
        _lastHit[slot] = _clock;
        _lethal[slot] = s.Lethal;
        _scale[slot] = s.Scale;
        _own[slot] = own;
        _slotVictim[slot] = victim;
        if (victim != 0)
        {
            _victimSlot[victim] = slot;
            if (_victimSlot.Count > 1024) PruneVictims();
        }
        var placement = Placement(s.Origin, s.Axis, s.Normal, s.Unit);
        var custom = new Color((float)(((start % ClockPeriod) + ClockPeriod) % ClockPeriod), GoreStreakRules.ShaderSeed(s.Seed), s.Bend, s.WidthMul);
        for (int b = 0; b < NB; b++)
        {
            int i = slot * NB + b;
            _mm.SetInstanceTransform(i, placement);
            _mm.SetInstanceColor(i, new Color(b / 8f, s.EntryShare, GoreStreakRules.BandOn((GoreStreakRules.Band)b, s.Fourth) ? 1f : 0f, Math.Max(0.05f, s.FanShare)));   // r: band / 8; g: entry share; b: on (0 retires it); a: the fan share (shown; 0 only for the prewarm)
            _mm.SetInstanceCustomData(i, custom);
        }
        Spawned++;
        _live++; if (own) _liveOwn++;
        _cpuMs += _watch.Elapsed.TotalMilliseconds;
        return slot;
    }

    /// <summary>Forgets the victims whose slot is gone or spent (lows: a full clear dropped the merge rule for every victim at once); only those, so a live streak keeps its rule.</summary>
    void PruneVictims()
    {
        _victimStale.Clear();
        foreach (var (v, sl) in _victimSlot)
            if (_slotVictim[sl] != v || !GoreStreakRules.Live(_starts[sl], _clock)) _victimStale.Add(v);
        foreach (int v in _victimStale) _victimSlot.Remove(v);
    }

    /// <summary>Retires every streak and forgets the victims (the capture aid's repeated hit, <c>--gore-streak-capture-repeat</c>: each repeat a fresh hit, never merged into the last).</summary>
    public void ClearAll()
    {
        if (_mm is null) return;
        for (int i = 0; i < _high; i++) if (i != _prewarmSlot) Retire(i);
        _victimSlot.Clear();
    }

    void Retire(int slot)
    {
        _starts[slot] = double.NegativeInfinity;
        for (int b = 0; b < NB; b++) _mm.SetInstanceColor(slot * NB + b, new Color(b / 8f, 1f, 0f, 1f));
    }

    /// <summary>
    /// Draws one invisible streak for a second in front of the camera (it follows the camera; every texel discarded, but both passes' pipelines exist before the first hit; finding L5:
    /// Warfront prewarms as Classic does).
    /// </summary>
    public void Prewarm(Vector3 at)
    {
        if (_mm is null) return;
        int slot = TakeSlot();
        _prewarmSlot = slot;
        _prewarmUntil = _clock + 1.0;
        _starts[slot] = _clock - 0.05;
        _slotVictim[slot] = 0;
        for (int b = 0; b < NB; b++)
        {
            int i = slot * NB + b;
            _mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(new Vector3(0.5f, 0.5f, 0.5f)), at));
            _mm.SetInstanceColor(i, new Color(b / 8f, 1f, 1f, 0f));   // on but not shown: every texel discarded (the passes still run)
            _mm.SetInstanceCustomData(i, new Color((float)((_starts[slot] % ClockPeriod + ClockPeriod) % ClockPeriod), 0.5f, 0f, 1f));
        }
    }

    /// <summary>
    /// The light on the blood (gore round 4, exposure-aware: the gate-4 live clips showed unshaded red glowing on dark maps): the environment's ambient light plus the visible
    /// directional lights (energy x colour's luminance x the wrapped facing share), in scene-linear units, clamped to <see cref="GoreStreakRules.SceneLightMin"/>..Max; the map's local
    /// lights and the capture aid's fill light are left out, so it is the same on both renderers and in captures and live play. The suns are looked up every 10 s, the energies read
    /// every half second.
    /// </summary>
    public float SceneLight => _sceneLight;
    float _sceneLight = GoreStreakRules.SceneLight, _lightTarget = GoreStreakRules.SceneLight, _lightSent = float.NaN;
    double _lightAt = double.NegativeInfinity, _sunsAt = double.NegativeInfinity;
    DirectionalLight3D[] _suns = System.Array.Empty<DirectionalLight3D>();
    ulong _lightEnvId;
    int _sunLookups;

    void UpdateSceneLight(float dt)
    {
        // ease toward the measured target every frame (no popping), the uniform sent only when it moved
        _sceneLight = GoreStreakRules.EaseLight(_sceneLight, _lightTarget, dt);
        if (float.IsNaN(_lightSent) || MathF.Abs(_sceneLight - _lightSent) > 1e-3f) { _lightSent = _sceneLight; _mat.SetShaderParameter(PSceneLight, _sceneLight); }
        if (_clock - _lightAt < 0.5) return;
        _lightAt = _clock;
        var world = GetViewport()?.World3D;
        var env = world?.Environment ?? world?.FallbackEnvironment;
        // the suns looked up again at once when a cached one is gone or the environment changed (a map load), else every 10 s
        ulong envId = env?.GetInstanceId() ?? 0;
        bool stale = envId != _lightEnvId;
        foreach (var d in _suns) if (!GodotObject.IsInstanceValid(d)) { stale = true; break; }
        if (stale) { _sunsAt = double.NegativeInfinity; _lightEnvId = envId; }
        float amb = GoreStreakRules.AmbientFallback;
        if (env is not null)
        {
            var c = env.AmbientLightColor.SrgbToLinear();
            amb = env.AmbientLightSource switch
            {
                Godot.Environment.AmbientSource.Disabled => 0f,
                Godot.Environment.AmbientSource.Color => GoreStreakRules.Luma(c.R, c.G, c.B) * env.AmbientLightEnergy,
                _ => GoreStreakRules.SkyAmbientShare * env.AmbientLightEnergy * env.BackgroundEnergyMultiplier,   // sky or background: an estimate
            };
        }
        if (_clock - _sunsAt >= 10.0)
        {
            _sunsAt = _clock; _sunLookups++;
            var found = new List<DirectionalLight3D>();
            foreach (var n in GetTree().Root.FindChildren("*", "DirectionalLight3D", true, false)) if (n is DirectionalLight3D d) found.Add(d);
            _suns = found.ToArray();
        }
        float sun = 0f;
        foreach (var d in _suns)
            if (GodotObject.IsInstanceValid(d) && d.IsVisibleInTree())
            {
                var lc = d.LightColor.SrgbToLinear();
                sun += d.LightEnergy * GoreStreakRules.Luma(lc.R, lc.G, lc.B);
            }
        float light = GoreStreakRules.LightOf(amb, sun);
        _lightTarget = light;
        // logged at the first measure and whenever it moves by a quarter (at most every 5 s): a map change, the weather, a lighting preset
        if (float.IsNaN(_lightLogged) || (MathF.Abs(light - _lightLogged) > 0.25f * MathF.Max(_lightLogged, 0.25f) && _clock - _lightLogAt >= 5.0))
        {
            _lightLogged = light; _lightLogAt = _clock;
            GD.Print($"gore streak: scene light {light:F2} (ambient {amb:F2}, suns {sun:F2} from {_suns.Length}, {_sunLookups} lookups; view t {_clock:F1})");
        }
    }
    float _lightLogged = float.NaN;
    double _lightLogAt;

    public override void _Process(double delta)
    {
        if (_mm is null) return;
        BeforeFrame?.Invoke();
        _watch.Restart();
        _clock += delta;
        UpdateSceneLight((float)delta);
        float now = (float)(_clock % ClockPeriod);
        _mat.SetShaderParameter(PNow, now);
        if (_prewarmSlot >= 0)
        {
            if (_clock >= _prewarmUntil) { Retire(_prewarmSlot); _prewarmSlot = -1; }
            else
            {
                // keep it young and in front of the camera for its second
                _starts[_prewarmSlot] = _clock - 0.05;
                var t = GetViewport()?.GetCamera3D() is { } cam
                    ? new Transform3D(cam.GlobalTransform.Basis.Orthonormalized().Scaled(new Vector3(0.5f, 0.5f, 0.5f)), cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 3f) : (Transform3D?)null;
                for (int b = 0; b < NB; b++)
                {
                    int i = _prewarmSlot * NB + b;
                    _mm.SetInstanceCustomData(i, new Color((float)((_starts[_prewarmSlot] % ClockPeriod + ClockPeriod) % ClockPeriod), 0.5f, 0f, 1f));
                    if (t is { } tt) _mm.SetInstanceTransform(i, tt);
                }
            }
        }
        // retire spent slots: switched off and forgotten, so none replays when the shader's clock wraps after 4096 s; a few slots a frame, round the pool
        if (_ageOverride is null)
            for (int n = 0; n < 16 && _high > 0; n++)
            {
                _retire = (_retire + 1) % _high;
                if (_retire != _prewarmSlot && GoreStreakRules.Spent(_starts[_retire], _clock)) Retire(_retire);
            }
        // the live counts, once a frame (finding L3)
        int live = 0, liveOwn = 0;
        for (int i = 0; i < _high; i++)
            if (i != _prewarmSlot && GoreStreakRules.Live(_starts[i], _clock)) { live++; if (_own[i]) liveOwn++; }
        _live = live; _liveOwn = liveOwn;
        _cpuMs += _watch.Elapsed.TotalMilliseconds;
        if (_clock >= _logAt)
        {
            // the gore streak: line every 10 s while streaks are spawned or showing (live, spawned, dropped, evicted, replaced, CPU)
            _logAt = _clock + 10.0;
            if (Spawned != _logSpawned || live > 0) { _logSpawned = Spawned; GD.Print($"gore streak: {Counters()}, cpu {TakeCpuMs():F2} ms in 10 s (view t {_clock:F1})"); }
        }
    }
    double _logAt = 10.0;
    long _logSpawned;
    int _retire;

    /// <summary>The counters line (the <c>gore streak:</c> log line's body).</summary>
    public string Counters() => $"live {Live} (own {LiveOwn}), spawned {Spawned}, dropped {Dropped}, evicted {Evicted}, replaced {Replaced}, merged {Merged}";
}
