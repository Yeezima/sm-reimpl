using System;
using System.Globalization;
using System.Text;
using Godot;
using SmClient.ImpactRules;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// gore-streak (added content: gore; owner 2026-11-25): the blood streak renderer shared by Classic PvP with the Extended gore and by Warfront. Every streak is one instance of a
/// single <see cref="MultiMesh"/> (<see cref="GoreStreakRules.PoolSize"/>, the oldest reused beyond it): a flat quad on the streak's tilted plane through the wound, its X axis the
/// exit axis, Y across, Z the plane's normal, each scaled by the metres per table unit (body height x scale). The procedural shader draws the branches, the entry stub, the outer
/// fade and the grit erosion from the per-frame table (<see cref="GoreStreakRules.Keys"/>, generated into the shader) at the instance's age; the CPU sets one clock uniform a frame
/// and an instance's transform, colour and custom data only when it spawns. Two passes as repair 4's ribbons: a strip (blend_mul: under the blood only part of the red is kept, its
/// green and blue removed) then the blood red mixed over it; our darker red, never additive, white or pink. Drawn at <see cref="ClassicBloodRules.RibbonRenderPriority"/> and one more,
/// above the power weapons' flash.
/// <para>Instance data: CUSTOM = (start on the view clock incl. pre-age, seed, bend, branch bias); COLOR = (4th-branch flag, lethal, prewarm-off, 1).</para>
/// </summary>
public partial class GoreStreakView : Node3D
{
    static string F(float v) => v.ToString("0.0#####", CultureInfo.InvariantCulture);

    /// <summary>The clock period (s): the shader takes the clock modulo this, so its float keeps millisecond precision.</summary>
    const double ClockPeriod = 4096.0;

    static string KeyArray(Func<GoreStreakRules.Key, float> pick)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < GoreStreakRules.Keys.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(F(pick(GoreStreakRules.Keys[i]))); }
        return sb.ToString();
    }

    static string BranchArray(int part)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < GoreStreakRules.MaxBranches; k++)
        {
            if (k > 0) sb.Append(", ");
            var b = GoreStreakRules.BranchShares[k];
            sb.Append(F(part switch { 0 => b.Angle, 1 => b.Reach, _ => b.Width }));
        }
        return sb.ToString();
    }

    static string ShaderCode(bool strip, bool compat = false) => $$"""
shader_type spatial;
render_mode {{(strip && !compat ? "blend_mul" : "blend_mix")}}, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;

uniform float now = 0.0;                 // the view clock modulo {{F((float)ClockPeriod)}} s
uniform float age_override = -1.0;       // >= 0: every streak at this age (the capture aid)
uniform sampler2D grit : filter_linear_mipmap, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0, 0.12, 0.08, 1.0);
uniform float strip_per_alpha = 5.0;   // GoreStreakRules.StripPerAlpha / CompatibilityStripPerAlpha
uniform float shade = 1.0;               // GoreStreakRules.ForwardShade on Forward+, CompatibilityShade on Compatibility (the same darker red on both)

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
const float B_ANG[4] = { {{BranchArray(0)}} };
const float B_REACH[4] = { {{BranchArray(1)}} };
const float B_WID[4] = { {{BranchArray(2)}} };

varying vec2 v_p;
varying float v_depth;
varying flat float v_exit;
varying flat float v_entry;
varying flat float v_fan;
varying flat float v_width;
varying flat float v_fill;
varying flat float v_opac;
varying flat float v_grit;
varying flat float v_br;
varying flat float v_out;
varying flat float v_root;
varying flat vec4 v_inst;     // seed, bend, bias, max branches

float h01(float seed, float salt) { return fract(sin(seed * 12.9898 + salt * 78.233) * 43758.5453); }   // GoreStreakRules.ShaderHash

void vertex() {
    float start = INSTANCE_CUSTOM.x;
    float age = age_override >= 0.0 ? age_override : mod(now - start + {{F((float)ClockPeriod)}}, {{F((float)ClockPeriod)}});
    v_p = VERTEX.xy;
    v_inst = vec4(INSTANCE_CUSTOM.y, INSTANCE_CUSTOM.z, INSTANCE_CUSTOM.w, COLOR.r > 0.5 ? 4.0 : 3.0);
    vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    v_depth = -view.z;
    // clipped: not born yet, gone, or retired (COLOR.b 0: a spent slot, finding M1, or the prewarm)
    if (COLOR.b < 0.5 || age < 0.0 || age >= {{F(GoreStreakRules.LifeSeconds)}}) {
        POSITION = vec4(2.0, 2.0, 2.0, 1.0);
        v_exit = 0.0; v_entry = 0.0; v_fan = 0.0; v_width = 0.0; v_fill = 0.0; v_opac = 0.0; v_grit = 0.0; v_br = 0.0; v_out = 0.0; v_root = 0.0;
    } else {
        POSITION = PROJECTION_MATRIX * view;   // written in both branches: once a shader writes POSITION, Godot takes it for every vertex
        int i = 0;
        for (int j = 0; j < NK - 2; j++) { if (age >= K_T[j + 1]) i = j + 1; }
        float fade = 1.0;
        float u;
        int b = i + 1;
        if (age >= K_T[NK - 1]) { i = NK - 1; b = NK - 1; u = 0.0; fade = 1.0 - (age - K_T[NK - 1]) / ({{F(GoreStreakRules.LifeSeconds)}} - K_T[NK - 1]); }
        else u = (age - K_T[i]) / (K_T[b] - K_T[i]);
        v_exit = mix(K_EXIT[i], K_EXIT[b], u);
        v_entry = mix(K_ENTRY[i], K_ENTRY[b], u);
        v_fan = mix(K_FAN[i], K_FAN[b], u);
        v_width = mix(K_WIDTH[i], K_WIDTH[b], u);
        v_fill = mix(K_FILL[i], K_FILL[b], u) * fade;
        v_opac = mix(K_OPAC[i], K_OPAC[b], u) * fade;
        v_grit = mix(K_GRIT[i], K_GRIT[b], u);
        v_br = mix(K_BR[i], K_BR[b], u);
        v_out = mix(K_OUT[i], K_OUT[b], u);
        v_root = mix(K_ROOT[i], K_ROOT[b], u);
    }
}

float prof(float u) { return (0.7 + 0.3 * smoothstep(0.0, 0.4, u)) * (1.0 - smoothstep(0.78, 1.06, u)); }   // GoreStreakRules.Profile

// one stroke (GoreStreakRules.Field): the best core field and its branch coordinates, the halo, the root
void stroke(vec2 p, float ang, float L, float w0, float bend, float outer, inout float best, inout vec2 sc, inout float halo, inout float root) {
    float cs = cos(ang), sn = sin(ang);
    float s = p.x * cs + p.y * sn;
    float c = -p.x * sn + p.y * cs;
    c -= bend * s * s / max(L, 0.05);
    float u = s / max(L, 1e-3);
    if (u < -0.08 || u > 1.12) return;
    float w = max(w0 * prof(u), 1e-4);
    float ac = abs(c);
    float along = smoothstep(-0.08, 0.0, u);
    float edge = 1.0 - smoothstep(w - {{F(GoreStreakRules.Feather)}}, w + {{F(GoreStreakRules.Feather)}}, ac);
    float f = edge * along * (1.0 - ac / (w + {{F(GoreStreakRules.Feather)}}) * 0.5) * (1.0 - outer * smoothstep(0.55, 1.0, u));
    float h = (1.0 - smoothstep(0.0, w * {{F(GoreStreakRules.HaloWidth)}} + {{F(GoreStreakRules.Feather)}}, ac)) * along * (1.0 - smoothstep(0.85, 1.12, u));
    float r = (1.0 - smoothstep({{F(0.7f * GoreStreakRules.RootShare)}}, {{F(1.15f * GoreStreakRules.RootShare)}}, u)) * edge * along;
    halo = max(halo, h);
    root = max(root, r);
    if (f > best) { best = f; sc = vec2(s, c); }
}

void fragment() {
    float seed = v_inst.x, bend = v_inst.y, bias = v_inst.z;
    float best = 0.0, halo = 0.0, root = 0.0;
    vec2 sc = vec2(0.0);
    float rad = 3.14159265 / 180.0;
    for (int k = 0; k < 4; k++) {
        if (float(k) >= v_inst.w) break;
        float weight = clamp(v_br - float(min(k, 2)), 0.0, 1.0);   // the heavy fourth comes and goes with the third
        if (weight <= 0.0) continue;
        float ja = 0.85 + 0.3 * h01(seed, 20.0 + float(k));
        float jr = 0.9 + 0.1 * h01(seed, 30.0 + float(k));
        stroke(v_p, (B_ANG[k] * ja + bias) * v_fan * rad, v_exit * B_REACH[k] * jr, v_width * B_WID[k] * sqrt(weight), bend, v_out, best, sc, halo, root);
    }
    if (v_entry > 0.01) stroke(v_p, 3.14159265 + (h01(seed, 40.0) - 0.5) * 0.3 * v_fan * rad, v_entry, v_width * 0.8, bend, v_out, best, sc, halo, root);
    if (halo <= 0.0 && best <= 0.0) discard;
    // two octaves of grit, stretched {{F(GoreStreakRules.GritStretch)}}:1 along the branch (GoreStreakRules.GritAt)
    vec2 guv = vec2(sc.x * {{F(GoreStreakRules.GritTiles / GoreStreakRules.GritStretch)}} + h01(seed, 50.0), sc.y * {{F(GoreStreakRules.GritTiles)}} + h01(seed, 51.0));
    vec3 g1 = texture(grit, guv).rgb;
    vec3 g2 = texture(grit, guv * {{F(GoreStreakRules.GritOctave)}} + vec2(0.37, 0.61)).rgb;
    float coarse = 0.62 * g1.r + 0.38 * g2.r, fine = 0.5 * g1.g + 0.5 * g2.g, smear = 0.6 * g1.b + 0.4 * g2.b;
    // continuous score and a soft step (no derivative of a discontinuous score: no hairlines; GoreStreakRules.Score, Coverage)
    float score = ({{F(GoreStreakRules.ScoreField)}} * best + {{F(GoreStreakRules.ScoreCoarse)}} * coarse + {{F(GoreStreakRules.ScoreSmear)}} * smear) * smoothstep(0.0, 0.2, best)
                + {{F(GoreStreakRules.RootBoost)}} * root * v_root;
    float thr = 1.0 - v_fill;
    float cov = smoothstep(thr - {{F(GoreStreakRules.CovSoft)}}, thr + {{F(GoreStreakRules.CovSoft)}}, score);
    float hole = 1.0 - 0.45 * v_grit;
    cov *= 1.0 - smoothstep(hole - 0.04, hole + 0.04, fine) * (1.0 - root * v_root);
    float dens = {{F(0.45f)}} + {{F(0.55f)}} * coarse;                                                    // GoreStreakRules.Density
    // the near fade (Warfront's near hits filled the screen) and the blood opacity: the core over the soft halo (GoreStreakRules.Alpha)
    float nearf = smoothstep({{F(GoreStreakRules.NearFadeFrom)}}, {{F(GoreStreakRules.NearFadeTo)}}, v_depth);
    float a = v_opac * max(cov * dens, {{F(GoreStreakRules.HaloAlpha)}} * halo) * nearf;
    if (a <= 0.003) discard;
{{(strip ? $$"""
    float st = clamp(strip_per_alpha * a, 0.0, 1.0) * (0.75 + 0.25 * cov);    // GoreStreakRules.Strip: fades with the opacity
{{(compat ? $$"""
    // Compatibility: its multiply blend darkened wherever the pass drew (even a multiply by 1), so the strip mixes the ground toward a dark blood red instead (GoreStreakRules.Composite)
    ALBEDO = vec3({{F(GoreStreakRules.CompatibilityStripRed)}}, 0.0, 0.0);
    ALPHA = st;
""" : $$"""
    ALBEDO = mix(vec3(1.0), vec3({{F(ClassicBloodRules.RibbonStripRed)}}, 0.0, 0.0), st);
    ALPHA = 1.0;
""")}}
""" : $$"""
    ALBEDO = tint.rgb * shade * mix({{F(ClassicBloodRules.RibbonShadeFloor)}}, {{F(ClassicBloodRules.RibbonShadeTop)}}, smoothstep(0.15, 0.85, dens));
    ALPHA = a;
""")}}
}
""";

    static Shader? _shader, _stripShader;
    static ImageTexture? _grit;
    static ArrayMesh? _quad;
    static readonly StringName PNow = "now", PAgeOverride = "age_override", PGrit = "grit", PTint = "tint", PShade = "shade", PStripPerAlpha = "strip_per_alpha";

    MultiMesh _mm = null!;
    MultiMeshInstance3D _node = null!;
    ShaderMaterial _mat = null!, _strip = null!;
    readonly double[] _starts = new double[GoreStreakRules.PoolSize];
    double _clock;
    long _spawned;
    int _high;
    int _prewarmSlot = -1;
    double _prewarmUntil = double.NegativeInfinity;
    readonly System.Diagnostics.Stopwatch _watch = new();

    /// <summary>Streaks started, dropped by a caller's budget (<see cref="NoteDropped"/>), live ones evicted from a full pool.</summary>
    public long Spawned { get; private set; }
    public long Dropped { get; private set; }
    public long Evicted { get; private set; }
    /// <summary>CPU spent in <see cref="Spawn"/> and the frame's clock update (ms, since the last <see cref="TakeCpuMs"/>).</summary>
    double _cpuMs;
    public double TakeCpuMs() { double v = _cpuMs; _cpuMs = 0; return v; }
    public void NoteDropped(int n = 1) => Dropped += n;

    /// <summary>When set, every streak is drawn at this age (s) and the clock stops mattering (the capture aid, <c>--gore-streak-capture</c>).</summary>
    public float? AgeOverride
    {
        get => _ageOverride;
        set { _ageOverride = value; if (_mat is not null) { _mat.SetShaderParameter(PAgeOverride, value ?? -1f); _strip.SetShaderParameter(PAgeOverride, value ?? -1f); } }
    }
    float? _ageOverride;

    /// <summary>The view clock (s since the view was made).</summary>
    public double Clock => _clock;

    /// <summary>Streaks showing now.</summary>
    public int Live { get { int n = 0; for (int i = 0; i < _high; i++) if (i != _prewarmSlot && GoreStreakRules.Live(_starts[i], _clock)) n++; return n; } }

    /// <summary>The shader source (for tests and the log): the blood pass, or the strip pass.</summary>
    public static string Source(bool strip, bool compat = false) => ShaderCode(strip, compat);

    static ArrayMesh Quad()
    {
        if (_quad is not null) return _quad;
        float b = GoreStreakRules.QuadBack, f = GoreStreakRules.QuadFwd, s = GoreStreakRules.QuadSide;
        var verts = new[] { new Vector3(-b, -s, 0f), new Vector3(f, -s, 0f), new Vector3(f, s, 0f), new Vector3(-b, s, 0f) };
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        _quad = new ArrayMesh();
        _quad.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return _quad;
    }

    static ImageTexture Grit()
    {
        if (_grit is not null) return _grit;
        int n = GoreStreakRules.GritSize;
        var image = Image.CreateFromData(n, n, false, Image.Format.Rgba8, GoreStreakRules.GritTexels(n, 1));
        image.GenerateMipmaps();
        return _grit = ImageTexture.CreateFromImage(image);
    }

    public override void _Ready()
    {
        _shader ??= new Shader { Code = ShaderCode(strip: false) };
        _stripShader ??= new Shader { Code = ShaderCode(strip: true, compat: RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility") };
        var tint = new Color(ClassicBloodRules.BloodRgb.R, ClassicBloodRules.BloodRgb.G, ClassicBloodRules.BloodRgb.B);
        _mat = new ShaderMaterial { Shader = _shader, RenderPriority = ClassicBloodRules.RibbonRenderPriority + 1 };
        _strip = new ShaderMaterial { Shader = _stripShader, RenderPriority = ClassicBloodRules.RibbonRenderPriority, NextPass = _mat };
        // the same darker red on both renderers (gate repair 8): Forward+ draws linear HDR through the tonemapper, Compatibility straight in display space
        bool compat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility";
        float shade = compat ? GoreStreakRules.CompatibilityShade : GoreStreakRules.ForwardShade;
        float perAlpha = compat ? GoreStreakRules.CompatibilityStripPerAlpha : GoreStreakRules.StripPerAlpha;
        foreach (var m in new[] { _mat, _strip }) { m.SetShaderParameter(PGrit, Grit()); m.SetShaderParameter(PTint, tint); m.SetShaderParameter(PAgeOverride, _ageOverride ?? -1f); m.SetShaderParameter(PShade, shade); m.SetShaderParameter(PStripPerAlpha, perAlpha); }
        _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = Quad() };
        _mm.InstanceCount = GoreStreakRules.PoolSize;
        _mm.VisibleInstanceCount = 0;
        for (int i = 0; i < _starts.Length; i++) _starts[i] = double.NegativeInfinity;
        _node = new MultiMeshInstance3D
        {
            Name = "GoreStreaks", Multimesh = _mm, MaterialOverride = _strip, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true,
            Layers = FxBatch.FxRenderLayer, CustomAabb = new Aabb(new Vector3(-5000f, -5000f, -5000f), new Vector3(10000f, 10000f, 10000f)),
        };
        AddChild(_node);
    }

    int TakeSlot()
    {
        int slot = GoreStreakRules.PoolSlot(_spawned++);
        if (slot == _prewarmSlot) { _prewarmSlot = -1; _prewarmUntil = double.NegativeInfinity; }
        else if (GoreStreakRules.Live(_starts[slot], _clock)) Evicted++;
        if (slot + 1 > _high) { _high = slot + 1; _mm.VisibleInstanceCount = _high; }
        return slot;
    }

    static Transform3D Placement(Num.Vector3 origin, Num.Vector3 axis, Num.Vector3 normal, float unit)
    {
        var x = new Vector3(axis.X, axis.Y, axis.Z).Normalized();
        var z = new Vector3(normal.X, normal.Y, normal.Z);
        z = (z - x * z.Dot(x)).Normalized();
        var y = z.Cross(x).Normalized();
        if (y.Y < 0f) { y = -y; z = -z; }   // the plane's +across leans up: the upper branch fans upward (gate repair 4)
        return new Transform3D(new Basis(x * unit, y * unit, z * unit), new Vector3(origin.X, origin.Y, origin.Z));
    }

    /// <summary>Starts one streak (Godot world, see <see cref="GoreStreakRules.Streak"/>). Returns its slot.</summary>
    public int Spawn(in GoreStreakRules.Streak s, float branchBias = 0f)
    {
        if (_mm is null) return -1;
        _watch.Restart();
        int slot = TakeSlot();
        double start = _clock - s.PreAge;
        _starts[slot] = start;
        _mm.SetInstanceTransform(slot, Placement(s.Origin, s.Axis, s.Normal, s.Unit));
        _mm.SetInstanceColor(slot, new Color(s.Lethal || s.Heavy ? 1f : 0f, 0f, 1f, 1f));   // r: the fourth branch; b: on (0 retires the slot)
        _mm.SetInstanceCustomData(slot, new Color((float)(((start % ClockPeriod) + ClockPeriod) % ClockPeriod), GoreStreakRules.ShaderSeed(s.Seed), s.Bend, branchBias));
        Spawned++;
        _cpuMs += _watch.Elapsed.TotalMilliseconds;
        return slot;
    }

    /// <summary>Draws one invisible streak for a second in front of the camera (it follows the camera), so both passes' pipelines exist before the first hit.</summary>
    public void Prewarm(Vector3 at)
    {
        if (_mm is null) return;
        int slot = TakeSlot();
        _prewarmSlot = slot;
        _prewarmUntil = _clock + 1.0;
        _starts[slot] = _clock - 0.05;
        _mm.SetInstanceTransform(slot, new Transform3D(Basis.Identity.Scaled(new Vector3(0.5f, 0.5f, 0.5f)), at));
        _mm.SetInstanceColor(slot, new Color(0f, 0f, 0f, 1f));   // blue 0: every texel clear (the passes still run)
        _mm.SetInstanceCustomData(slot, new Color((float)((_starts[slot] % ClockPeriod + ClockPeriod) % ClockPeriod), 0.5f, 0f, 0f));
    }

    public override void _Process(double delta)
    {
        if (_mm is null) return;
        _watch.Restart();
        _clock += delta;
        float now = (float)(_clock % ClockPeriod);
        _mat.SetShaderParameter(PNow, now);
        _strip.SetShaderParameter(PNow, now);
        if (_prewarmSlot >= 0)
        {
            if (_clock >= _prewarmUntil) _prewarmSlot = -1;
            else
            {
                // keep it young and in front of the camera for its second
                _starts[_prewarmSlot] = _clock - 0.05;
                _mm.SetInstanceCustomData(_prewarmSlot, new Color((float)((_starts[_prewarmSlot] % ClockPeriod + ClockPeriod) % ClockPeriod), 0.5f, 0f, 0f));
                if (GetViewport()?.GetCamera3D() is { } cam)
                    _mm.SetInstanceTransform(_prewarmSlot, new Transform3D(cam.GlobalTransform.Basis.Orthonormalized().Scaled(new Vector3(0.5f, 0.5f, 0.5f)),cam.GlobalPosition - cam.GlobalTransform.Basis.Z * 3f));
            }
        }
        // retire spent slots (finding M1): switched off and forgotten, so none replays when the shader's clock wraps after 4096 s; a few slots a frame, round the pool
        if (_ageOverride is null)
            for (int n = 0; n < 16 && _high > 0; n++)
            {
                _retire = (_retire + 1) % _high;
                if (_retire != _prewarmSlot && GoreStreakRules.Spent(_starts[_retire], _clock)) { _starts[_retire] = double.NegativeInfinity; _mm.SetInstanceColor(_retire, new Color(0f, 0f, 0f, 1f)); }
            }
        _cpuMs += _watch.Elapsed.TotalMilliseconds;
        if (_clock >= _logAt)
        {
            // the gore streak: line every 10 s while streaks are spawned or showing (live, spawned, dropped, evicted, CPU)
            _logAt = _clock + 10.0;
            int live = Live;
            if (Spawned != _logSpawned || live > 0) { _logSpawned = Spawned; GD.Print($"gore streak: live {live}, spawned {Spawned}, dropped {Dropped}, evicted {Evicted}, cpu {TakeCpuMs():F2} ms in 10 s (view t {_clock:F1})"); }
        }
    }
    double _logAt = 10.0;
    long _logSpawned;
    int _retire;

    /// <summary>The counters line (the <c>gore streak:</c> log line's body).</summary>
    public string Counters() => $"live {Live}, spawned {Spawned}, dropped {Dropped}, evicted {Evicted}";
}
