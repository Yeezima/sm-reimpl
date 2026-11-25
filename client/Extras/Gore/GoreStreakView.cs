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

    static string ShaderCode(bool strip) => $$"""
shader_type spatial;
render_mode {{(strip ? "blend_mul" : "blend_mix")}}, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;

uniform float now = 0.0;                 // the view clock modulo {{F((float)ClockPeriod)}} s
uniform float age_override = -1.0;       // >= 0: every streak at this age (the capture aid)
uniform sampler2D grit : filter_linear_mipmap, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0, 0.12, 0.08, 1.0);

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
const float B_ANG[4] = { {{BranchArray(0)}} };
const float B_REACH[4] = { {{BranchArray(1)}} };
const float B_WID[4] = { {{BranchArray(2)}} };

varying vec2 v_p;
varying flat float v_exit;
varying flat float v_entry;
varying flat float v_fan;
varying flat float v_width;
varying flat float v_fill;
varying flat float v_opac;
varying flat float v_grit;
varying flat float v_br;
varying flat float v_out;
varying flat vec4 v_inst;     // seed, bend, bias, max branches
varying flat float v_on;

// the same hash as GoreStreakRules.Hash01 is not needed bit for bit: the seeded jitter only has to be stable per streak
float h01(float seed, float salt) { return fract(sin(seed * 12.9898 + salt * 78.233) * 43758.5453); }

void vertex() {
    float start = INSTANCE_CUSTOM.x;
    float age = age_override >= 0.0 ? age_override : mod(now - start + {{F((float)ClockPeriod)}}, {{F((float)ClockPeriod)}});
    v_p = VERTEX.xy;
    v_inst = vec4(INSTANCE_CUSTOM.y, INSTANCE_CUSTOM.z, INSTANCE_CUSTOM.w, COLOR.r > 0.5 ? 4.0 : 3.0);
    v_on = COLOR.b;
    if (age < 0.0 || age >= {{F(GoreStreakRules.LifeSeconds)}}) {
        POSITION = vec4(2.0, 2.0, 2.0, 1.0);   // clipped: not born yet or gone
        v_exit = 0.0; v_entry = 0.0; v_fan = 0.0; v_width = 0.0; v_fill = 0.0; v_opac = 0.0; v_grit = 0.0; v_br = 0.0; v_out = 0.0;
    } else {
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
    }
}

float prof(float u) { return (0.45 + 0.55 * smoothstep(0.0, 0.4, u)) * (1.0 - smoothstep(0.78, 1.06, u)); }

// one branch: keeps the best field and that branch's coordinates (GoreStreakRules.Field)
void branch(vec2 p, float ang, float L, float w0, float bend, float outer, inout float best, inout vec2 sc) {
    float cs = cos(ang), sn = sin(ang);
    float s = p.x * cs + p.y * sn;
    float c = -p.x * sn + p.y * cs;
    c -= bend * s * s / max(L, 0.05);
    float u = s / max(L, 1e-3);
    if (u < -0.05 || u > 1.12) return;
    float w = max(w0 * prof(u), 1e-4);
    float f = clamp(1.0 - abs(c) / w, 0.0, 1.0) * smoothstep(-0.05, 0.02, u);
    f *= 1.0 - outer * smoothstep(0.55, 1.0, u);
    if (f > best) { best = f; sc = vec2(s, c); }
}

void fragment() {
    float seed = v_inst.x, bend = v_inst.y, bias = v_inst.z;
    float best = 0.0;
    vec2 sc = vec2(0.0);
    float rad = 3.14159265 / 180.0;
    for (int k = 0; k < 4; k++) {
        if (float(k) >= v_inst.w) break;
        float weight = clamp(v_br - float(min(k, 2)), 0.0, 1.0);   // the heavy fourth comes and goes with the third (GoreStreakRules.Field)
        if (weight <= 0.0) continue;
        float ja = 0.85 + 0.3 * h01(seed, 20.0 + float(k));
        float jr = 0.9 + 0.1 * h01(seed, 30.0 + float(k));
        branch(v_p, (B_ANG[k] * ja + bias) * v_fan * rad, v_exit * B_REACH[k] * jr, v_width * B_WID[k] * sqrt(weight), bend, v_out, best, sc);
    }
    if (v_entry > 0.01) branch(v_p, 3.14159265 + (h01(seed, 40.0) - 0.5) * 0.3 * v_fan * rad, v_entry, v_width * 0.75, bend, v_out, best, sc);
    // the grit: stretched {{F(GoreStreakRules.GritStretch)}}:1 along the branch (GoreStreakRules.GritUv)
    vec2 guv = vec2(sc.x * {{F(GoreStreakRules.GritTiles / GoreStreakRules.GritStretch)}} + h01(seed, 50.0), sc.y * {{F(GoreStreakRules.GritTiles)}} + h01(seed, 51.0));
    vec3 g = texture(grit, guv).rgb;
    float score = best <= {{F(GoreStreakRules.ShapeEdge)}} ? 0.0 : {{F(GoreStreakRules.ScoreField)}} * best + {{F(GoreStreakRules.ScoreCoarse)}} * g.r + {{F(GoreStreakRules.ScoreSmear)}} * g.b;     // GoreStreakRules.Score
    float thr = 1.0 - v_fill;
    float aa = max(1.5 * fwidth(score), 1e-4);
    float cov = clamp((score - thr) / aa + 0.5, 0.0, 1.0) * step(1e-4, best);
    float hole = 1.0 - 0.45 * v_grit;                                           // GoreStreakRules.HoleThreshold
    cov *= 1.0 - clamp((g.g - hole) / max(1.5 * fwidth(g.g), 1e-4) + 0.5, 0.0, 1.0);
    cov *= v_on;
    float dens = 0.55 + 0.45 * g.r;                                            // GoreStreakRules.Density
{{(strip ? $$"""
    float s3 = cov * cov * cov;
    ALBEDO = mix(vec3(1.0), vec3({{F(ClassicBloodRules.RibbonStripRed)}}, 0.0, 0.0), s3);
    ALPHA = 1.0;
""" : $$"""
    ALBEDO = tint.rgb * mix({{F(ClassicBloodRules.RibbonShadeFloor)}}, {{F(ClassicBloodRules.RibbonShadeTop)}}, smoothstep(0.15, 0.85, dens));
    ALPHA = v_opac * cov * dens;
""")}}
}
""";

    static Shader? _shader, _stripShader;
    static ImageTexture? _grit;
    static ArrayMesh? _quad;
    static readonly StringName PNow = "now", PAgeOverride = "age_override", PGrit = "grit", PTint = "tint";

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
    public static string Source(bool strip) => ShaderCode(strip);

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
        _stripShader ??= new Shader { Code = ShaderCode(strip: true) };
        var tint = new Color(ClassicBloodRules.BloodRgb.R, ClassicBloodRules.BloodRgb.G, ClassicBloodRules.BloodRgb.B);
        _mat = new ShaderMaterial { Shader = _shader, RenderPriority = ClassicBloodRules.RibbonRenderPriority + 1 };
        _strip = new ShaderMaterial { Shader = _stripShader, RenderPriority = ClassicBloodRules.RibbonRenderPriority, NextPass = _mat };
        foreach (var m in new[] { _mat, _strip }) { m.SetShaderParameter(PGrit, Grit()); m.SetShaderParameter(PTint, tint); m.SetShaderParameter(PAgeOverride, _ageOverride ?? -1f); }
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
        _mm.SetInstanceColor(slot, new Color(s.Lethal || s.Heavy ? 1f : 0f, s.Lethal ? 1f : 0f, 1f, 1f));
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

    /// <summary>The counters line (the <c>gore streak:</c> log line's body).</summary>
    public string Counters() => $"live {Live}, spawned {Spawned}, dropped {Dropped}, evicted {Evicted}";
}
