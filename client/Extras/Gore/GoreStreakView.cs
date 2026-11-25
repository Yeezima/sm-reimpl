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
/// filament texture stretched along the band, the ragged edge, the outer fade, the patch erosion and the pinholes (<see cref="GoreStreakRules.TexelAt"/> mirrors it). The CPU sets one
/// clock uniform a frame and an instance's transform, colour and custom data only when a streak spawns. Two passes: a strip (a multiply that keeps the red and takes green and blue from
/// the ground under the blood) then the blood red mixed over it; our darker red, never additive, white or pink. Drawn at <see cref="ClassicBloodRules.RibbonRenderPriority"/> and one
/// more, above the power weapons' flash.
/// <para>Instance (streak slot x 4 + band): transform = the plane frame (X exit axis, Y across leaning up, Z normal) x metres per table unit; CUSTOM = (start on the view clock incl.
/// pre-age, seed, bend, width multiplier); COLOR = (band / 4, entry share, on, shown: 0 for the prewarm).</para>
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

    /// <summary>The strip pass's output: a multiply keeping the red (Forward+), or on Compatibility, whose multiply blend blackened the ground, a mix toward a mid blood red.</summary>
    static readonly string StripMul = "    ALBEDO = vec3(1.0, 1.0 - st, 1.0 - st);";
    static readonly string StripCompat = $"    ALBEDO = vec3({F(GoreStreakRules.CompatibilityStripRed)}, 0.0, 0.0);   // GoreStreakRules.CompatibilityStripRed\n    ALPHA = st;";

    static string ShaderCode(bool strip, bool compat = false) => $$"""
shader_type spatial;
render_mode {{(strip && !compat ? "blend_mul" : "blend_mix")}}, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;

uniform float now = 0.0;                 // the view clock modulo {{F((float)ClockPeriod)}} s
uniform float age_override = -1.0;       // >= 0: every streak at this age (the capture aid)
uniform sampler2D fil : filter_linear_mipmap_anisotropic, repeat_enable;   // GoreStreakRules.FilamentTexels: R filaments, G blotches, B fine grain
uniform vec4 tint : source_color = vec4(1.0, 0.12, 0.08, 1.0);
uniform float strip_per_alpha = 6.0;     // GoreStreakRules.StripPerAlpha / CompatibilityStripPerAlpha
uniform float strip_max = 0.65;          // GoreStreakRules.StripMax / CompatibilityStripMax
uniform float shade = 1.0;               // GoreStreakRules.ForwardShade on Forward+, CompatibilityShade on Compatibility (the same darker red on both)
uniform float core_alpha = 0.85;         // GoreStreakRules.CoreAlpha / CompatibilityCoreAlpha
uniform float near_from = {{F(GoreStreakRules.NearFadeFrom)}};
uniform float near_to = {{F(GoreStreakRules.NearFadeTo)}};

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
const float B_ANG[4] = { {{BandArray(b => b.Angle)}} };
const float B_REACH[4] = { {{BandArray(b => b.Reach)}} };
const float B_WID[4] = { {{BandArray(b => b.Width)}} };
const float B_ROOT[4] = { {{BandArray(b => b.Root)}} };
const float B_AJ[4] = { {{BandArray(b => b.AngleJitter)}} };
const float B_RJ[4] = { {{BandArray(b => b.ReachJitter)}} };
const float B_BEND[4] = { {{BandArray(b => b.Bend)}} };

varying vec3 v_uvs;            // along 0..1, across (1 at the nominal edge), along in table units
varying float v_depth;
varying flat float v_hw;       // the band's half width (table units)
varying flat vec4 v_k;         // opacity x band share x shown, fill, grit, outer
varying flat vec4 v_r;         // root, texture offset u, texture offset v, how far the side bands have grown

float h01(float seed, float salt) { return fract(sin(seed * 12.9898 + salt * 78.233) * 43758.5453); }   // GoreStreakRules.ShaderHash
float prof(float u) { return (0.55 + 0.45 * smoothstep(0.0, 0.5, u)) * (1.0 - 0.75 * smoothstep(0.82, 1.0, u)); }   // GoreStreakRules.Profile

void vertex() {
    float start = INSTANCE_CUSTOM.x;
    float age = age_override >= 0.0 ? age_override : mod(now - start + {{F((float)ClockPeriod)}}, {{F((float)ClockPeriod)}});
    int band = int(COLOR.r * 4.0 + 0.5);
    float seed = INSTANCE_CUSTOM.y;
    float uu = VERTEX.x, vv = VERTEX.y;
    v_uvs = vec3(uu, vv * {{F(GoreStreakRules.EdgePad)}}, 0.0);
    v_k = vec4(0.0); v_r = vec4(0.0);
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
        float br = mix(K_BR[i], K_BR[b], u);
        // the band at this age (GoreStreakRules.BandOf)
        float ja = (h01(seed, 20.0 + float(band)) - 0.5) * 2.0 * B_AJ[band];
        float jr = 1.0 + (h01(seed, 30.0 + float(band)) - 0.5) * 2.0 * B_RJ[band];
        float deg, len, w = wd * B_WID[band] * INSTANCE_CUSTOM.w, alpha = 1.0;
        if (band == 2) {
            deg = 180.0 + B_ANG[2] + ja; len = en * COLOR.g * jr;
            alpha = 1.0 - smoothstep({{F(GoreStreakRules.EntryFadeFrom)}}, {{F(GoreStreakRules.EntryFadeTo)}}, age);
        } else if (band == 0) {
            deg = B_ANG[0] * fan + ja; len = ex * jr;
        } else {
            float g = clamp(br - 1.0, 0.0, 1.0);
            deg = B_ANG[band] * fan + ja; len = ex * B_REACH[band] * jr * (0.35 + 0.65 * g); w *= sqrt(g);
            if (band == 1) alpha = 1.0 - {{F(GoreStreakRules.UpperFade)}} * smoothstep({{F(GoreStreakRules.Keys[2].T)}}, {{F(GoreStreakRules.Keys[4].T)}}, age);
            if (g <= 0.0) { len = 0.0; alpha = 0.0; }
        }
        float ang = radians(deg);
        float bend = INSTANCE_CUSTOM.z * B_BEND[band];
        // the strip vertex on the plane (GoreStreakRules.RibbonPoint)
        float cs = cos(ang), sn = sin(ang);
        float s = uu * len;
        float across = bend * s * s / max(len, 0.05) + vv * w * prof(uu) * {{F(GoreStreakRules.EdgePad)}};
        VERTEX = vec3(B_ROOT[band] * ex + cs * s - sn * across, sn * s + cs * across, 0.0);
        vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
        v_depth = -view.z;
        POSITION = PROJECTION_MATRIX * view;   // written in both branches: once a shader writes POSITION, Godot takes it for every vertex
        v_uvs.z = s; v_hw = w;
        v_k = vec4(mix(K_OPAC[i], K_OPAC[b], u) * fade * alpha * COLOR.a, mix(K_FILL[i], K_FILL[b], u) * fade, mix(K_GRIT[i], K_GRIT[b], u), mix(K_OUT[i], K_OUT[b], u));
        v_r = vec4(mix(K_ROOT[i], K_ROOT[b], u), h01(seed, 50.0 + float(band)), h01(seed, 54.0 + float(band)), clamp(br - 1.0, 0.0, 1.0));
    }
}

void fragment() {
    float u = v_uvs.x, v = v_uvs.y, s = v_uvs.z;
    float av = abs(v);
    vec2 o = v_r.yz;
    // the filaments, flow-aligned across the band and stretched along it (GoreStreakRules.TexelAt)
    float warp = (texture(fil, vec2(s * 0.35 + o.x, o.y * 1.7)).g - 0.5) * 0.12;
    vec2 t = vec2(s / {{F(GoreStreakRules.FilAlongH)}} + o.x, v * {{F(GoreStreakRules.FilAcrossTiles)}} + o.y + warp);
    float fl = 0.65 * texture(fil, t).r + 0.35 * texture(fil, t * 2.3 + vec2(0.37, 0.61)).r;
    float fine = texture(fil, t * {{F(GoreStreakRules.FineScale)}} + vec2(0.2, 0.7)).b;
    float lump = texture(fil, vec2(s * {{F(GoreStreakRules.LumpAlong)}} + o.x * 2.0, (v > 0.0 ? 0.3 : 0.7) + o.y)).g;   // the lumpy outline
    float edge = 1.0 - smoothstep({{F(GoreStreakRules.EdgeFrom)}}, {{F(GoreStreakRules.EdgeTo)}}, av + 0.25 * (fine - 0.5) + 0.2 * (1.0 - fl) * av + {{F(GoreStreakRules.LumpDepth)}} * (lump - 0.5));
    float blot = texture(fil, vec2(s * 0.6 + o.x, v * 0.08 + o.y * 1.3)).g;   // lighter and darker clumps
    float along = smoothstep(0.0, 0.05, u) * (1.0 - smoothstep(0.8, 1.0, u + 0.15 * (fine - 0.5)));
    float outer = 1.0 - v_k.w * smoothstep(0.5, 1.0, u);
    // the patch erosion against 1 - Fill, the root held whole, the pinholes
    float e = texture(fil, vec2(u * {{F(GoreStreakRules.ErodeAlong)}} + o.x * 3.1, v * {{F(GoreStreakRules.ErodeAcross)}} + o.y * 2.3)).g;
    float thr = 1.0 - v_k.y;
    float cov = smoothstep(thr - {{F(GoreStreakRules.ErodeSoft)}}, thr + {{F(GoreStreakRules.ErodeSoft)}}, 0.8 * e + 0.2 * fl);
    float held = v_r.x * (1.0 - smoothstep({{F(0.6f * GoreStreakRules.RootShare)}}, {{F(GoreStreakRules.RootShare)}}, u));
    cov = max(cov, held);
    float hole = 1.0 - 0.45 * v_k.z;
    cov *= 1.0 - smoothstep(hole - 0.04, hole + 0.04, fine) * (1.0 - held);
    float nearf = smoothstep(near_from, near_to, v_depth);
    float solid = {{F(GoreStreakRules.SwipeSolid)}} * (1.0 - v_r.w);   // frame 0: one solid swipe
    float flo = {{F(GoreStreakRules.FilFloor)}} + solid, bf = {{F(GoreStreakRules.BlotFloor)}} + solid;
    float speck = texture(fil, vec2(s * {{F(GoreStreakRules.SpeckleTiles)}} + o.x * 5.0, v * v_hw * {{F(GoreStreakRules.SpeckleTiles)}} + o.y * 5.0)).b;   // isotropic grain on the plane
    float a = min(1.0, v_k.x * edge * along * outer * cov * (flo + (1.0 - flo) * fl) * (bf + (1.0 - bf) * 2.0 * blot) * ({{F(1f - GoreStreakRules.SpeckleDepth)}} + {{F(2f * GoreStreakRules.SpeckleDepth)}} * speck) * core_alpha) * nearf;
    if (a <= {{F(GoreStreakRules.MinAlpha)}}) discard;   // GoreStreakRules.MinAlpha: no faint pink fringe
{{(strip ? $$"""
    float st = clamp(strip_per_alpha * a, 0.0, strip_max);   // GoreStreakRules.Strip: green and blue only, never the red
{{(compat ? StripCompat : StripMul)}}
""" : $$"""
    float dens = fl * (1.0 - 0.45 * min(av, 1.0));   // lighter in the filaments' core
    ALBEDO = tint.rgb * shade * mix({{F(ClassicBloodRules.RibbonShadeFloor)}}, {{F(ClassicBloodRules.RibbonShadeTop)}}, smoothstep(0.15, 0.85, dens));
    ALPHA = a;
""")}}
}
""";

    static Shader? _shader, _stripShader;
    static ImageTexture? _tex;
    static ArrayMesh? _strip;
    static readonly StringName PNow = "now", PAgeOverride = "age_override", PFil = "fil", PTint = "tint", PShade = "shade", PStripPerAlpha = "strip_per_alpha", PCoreAlpha = "core_alpha", PStripMax = "strip_max",
        PNearFrom = "near_from", PNearTo = "near_to";

    MultiMesh _mm = null!;
    MultiMeshInstance3D _node = null!;
    ShaderMaterial _mat = null!, _stripMat = null!;
    readonly double[] _starts = new double[GoreStreakRules.PoolSize];
    readonly bool[] _own = new bool[GoreStreakRules.PoolSize];
    readonly int[] _slotVictim = new int[GoreStreakRules.PoolSize];
    readonly Dictionary<int, int> _victimSlot = new();
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
        set { _ageOverride = value; if (_mat is not null) { _mat.SetShaderParameter(PAgeOverride, value ?? -1f); _stripMat.SetShaderParameter(PAgeOverride, value ?? -1f); } }
    }
    float? _ageOverride;

    /// <summary>The near fade (m): Classic <see cref="GoreStreakRules.NearFadeFrom"/>..To, Warfront <see cref="GoreStreakRules.WarfrontNearFadeFrom"/>..To.</summary>
    public void SetNearFade(float from, float to)
    {
        _nearFrom = from; _nearTo = to;
        if (_mat is null) return;
        foreach (var m in new[] { _mat, _stripMat }) { m.SetShaderParameter(PNearFrom, from); m.SetShaderParameter(PNearTo, to); }
    }
    float _nearFrom = GoreStreakRules.NearFadeFrom, _nearTo = GoreStreakRules.NearFadeTo;

    /// <summary>The view clock (s since the view was made).</summary>
    public double Clock => _clock;

    /// <summary>Streaks showing now (counted once a frame, finding L3, plus the ones started since), and the own player's among them.</summary>
    public int Live => _live;
    public int LiveOwn => _liveOwn;

    /// <summary>The shader source (for tests and the log): the blood pass, or the strip pass.</summary>
    public static string Source(bool strip, bool compat = false) => ShaderCode(strip, compat);

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

    public override void _Ready()
    {
        _shader ??= new Shader { Code = ShaderCode(strip: false) };
        _stripShader ??= new Shader { Code = ShaderCode(strip: true, compat: RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility") };
        var tint = new Color(ClassicBloodRules.BloodRgb.R, ClassicBloodRules.BloodRgb.G, ClassicBloodRules.BloodRgb.B);
        _mat = new ShaderMaterial { Shader = _shader, RenderPriority = ClassicBloodRules.RibbonRenderPriority + 1 };
        _stripMat = new ShaderMaterial { Shader = _stripShader, RenderPriority = ClassicBloodRules.RibbonRenderPriority, NextPass = _mat };
        // the same darker red on both renderers (gate repair 8): Forward+ draws linear HDR through the tonemapper, Compatibility straight in display space
        bool compat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        float shade = compat ? GoreStreakRules.CompatibilityShade : GoreStreakRules.ForwardShade;
        float perAlpha = compat ? GoreStreakRules.CompatibilityStripPerAlpha : GoreStreakRules.StripPerAlpha;
        foreach (var m in new[] { _mat, _stripMat })
        {
            m.SetShaderParameter(PFil, Filament()); m.SetShaderParameter(PTint, tint); m.SetShaderParameter(PAgeOverride, _ageOverride ?? -1f); m.SetShaderParameter(PShade, shade);
            m.SetShaderParameter(PStripPerAlpha, perAlpha); m.SetShaderParameter(PStripMax, compat ? GoreStreakRules.CompatibilityStripMax : GoreStreakRules.StripMax); m.SetShaderParameter(PCoreAlpha, compat ? GoreStreakRules.CompatibilityCoreAlpha : GoreStreakRules.CoreAlpha);
            m.SetShaderParameter(PNearFrom, _nearFrom); m.SetShaderParameter(PNearTo, _nearTo);
        }
        _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = StripMesh() };
        _mm.InstanceCount = GoreStreakRules.PoolSize * NB;
        _mm.VisibleInstanceCount = 0;
        for (int i = 0; i < _starts.Length; i++) _starts[i] = double.NegativeInfinity;
        _node = new MultiMeshInstance3D
        {
            Name = "GoreStreaks", Multimesh = _mm, MaterialOverride = _stripMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true,
            Layers = FxBatch.FxRenderLayer, CustomAabb = new Aabb(new Vector3(-5000f, -5000f, -5000f), new Vector3(10000f, 10000f, 10000f)),
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
    /// Starts one streak (Godot world, see <see cref="GoreStreakRules.Streak"/>) on <paramref name="victim"/> (0: none known): a victim whose last streak started under
    /// <see cref="GoreStreakRules.ReplaceSeconds"/> ago has it replaced (the same slot starts over), never stacked. Returns its slot.
    /// </summary>
    public int Spawn(in GoreStreakRules.Streak s, int victim = 0, bool own = false)
    {
        if (_mm is null) return -1;
        _watch.Restart();
        int slot;
        if (victim != 0 && _victimSlot.TryGetValue(victim, out int vs) && _slotVictim[vs] == victim && vs != _prewarmSlot && GoreStreakRules.Replaces(_starts[vs], _clock))
        {
            slot = vs; Replaced++;
            if (GoreStreakRules.Live(_starts[vs], _clock)) { _live--; if (_own[vs]) _liveOwn--; }
        }
        else slot = TakeSlot();
        double start = _clock - s.PreAge;
        _starts[slot] = start;
        _own[slot] = own;
        _slotVictim[slot] = victim;
        if (victim != 0) { _victimSlot[victim] = slot; if (_victimSlot.Count > 1024) _victimSlot.Clear(); }
        var placement = Placement(s.Origin, s.Axis, s.Normal, s.Unit);
        var custom = new Color((float)(((start % ClockPeriod) + ClockPeriod) % ClockPeriod), GoreStreakRules.ShaderSeed(s.Seed), s.Bend, s.WidthMul);
        int bands = GoreStreakRules.BandCount(s.Fourth);
        for (int b = 0; b < NB; b++)
        {
            int i = slot * NB + b;
            _mm.SetInstanceTransform(i, placement);
            _mm.SetInstanceColor(i, new Color(b / 4f, s.EntryShare, b < bands ? 1f : 0f, 1f));   // r: band; g: entry share; b: on (0 retires it); a: shown
            _mm.SetInstanceCustomData(i, custom);
        }
        Spawned++;
        _live++; if (own) _liveOwn++;
        _cpuMs += _watch.Elapsed.TotalMilliseconds;
        return slot;
    }

    void Retire(int slot)
    {
        _starts[slot] = double.NegativeInfinity;
        for (int b = 0; b < NB; b++) _mm.SetInstanceColor(slot * NB + b, new Color(b / 4f, 1f, 0f, 1f));
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
            _mm.SetInstanceColor(i, new Color(b / 4f, 1f, 1f, 0f));   // on but not shown: every texel discarded (the passes still run)
            _mm.SetInstanceCustomData(i, new Color((float)((_starts[slot] % ClockPeriod + ClockPeriod) % ClockPeriod), 0.5f, 0f, 1f));
        }
    }

    public override void _Process(double delta)
    {
        if (_mm is null) return;
        BeforeFrame?.Invoke();
        _watch.Restart();
        _clock += delta;
        float now = (float)(_clock % ClockPeriod);
        _mat.SetShaderParameter(PNow, now);
        _stripMat.SetShaderParameter(PNow, now);
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
    public string Counters() => $"live {Live} (own {LiveOwn}), spawned {Spawned}, dropped {Dropped}, evicted {Evicted}, replaced {Replaced}";
}
