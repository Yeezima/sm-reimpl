using System;
using Godot;
using SmClient.ImpactRules;

namespace SmClient;

/// <summary>
/// classic-hit-effects (brief 2026-11-23 rev 1; client presentation only): the directional hit spray on a player in Classic PvP, one pooled mesh per spray, moved by its shader (no GPU particles,
/// no per-frame allocation). Thin streaks and a few droplets are released from just behind the moving weapon over <see cref="ClassicHitDirRules.EmitSeconds"/> after contact (the weapon's
/// path is fed in as it is drawn, or from its recorded history when the confirmation came late), each thrown along the weapon's motion there with a sideways spread that starts narrow and
/// grows, then slows in the air, widens, falls a little and fades smoothly. A gunfire hit feeds a straight path along the incoming shot. Two looks share the node:
/// <list type="bullet">
/// <item><b>Blood</b> (Extended only): the red of today's Classic health-hit sparks (<see cref="BloodTint"/>, the colour the brief keeps), additive like them.</item>
/// <item><b>Sparks</b> (both Classic profiles): yellow-white, faster, shorter.</item>
/// </list>
/// Counts, speeds, widths and lives are ours (spec/behavior.md "Classic hit effects").
/// </summary>
public partial class ClassicHitSpray : Node3D
{
    /// <summary>Today's Classic health-hit red (<c>NetworkView.RedSparks</c>' tint), kept for the blood spray (brief: preserve the current blood colour and tone).</summary>
    public static readonly Color BloodTint = new(1f, 0.12f, 0.08f);
    /// <summary>The armour sparks' yellow-white (ours, the look of the data's armour-hit sparks).</summary>
    public static readonly Color SparkTint = new(1f, 0.82f, 0.5f);

    public const int Pool = 32;
    const int Slots = 8;
    const float SlotSeconds = ClassicHitDirRules.EmitSeconds / (Slots - 1);

    static string F(float v) => v.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);

    static readonly string Code = $$"""
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;

uniform float age = 0.0;
uniform float filled = 0.0;               // seconds of the path known (no streak is released past it)
uniform vec3 path[{{Slots}}];              // where the weapon was, every slot_dt seconds after contact (relative to the contact)
uniform vec3 vel[{{Slots}}];               // the throw velocity there (m/s)
uniform float slot_dt = {{F(SlotSeconds)}};
uniform float emit = {{F(ClassicHitDirRules.EmitSeconds)}};
uniform float streak_life = {{F(ClassicHitDirRules.StreakSeconds)}};
uniform float spread = 0.4;
uniform float count = 26.0;
uniform float size = 1.0;
uniform float gravity = 3.5;
uniform float back_share = 0.0;           // gunfire: a share of droplets splashes back toward the shooter
uniform vec4 tint : source_color = vec4(1.0, 0.12, 0.08, 1.0);
uniform float glow = 1.8;
varying float fade;
varying float kind;

int slot_of(float t) { return clamp(int(floor(t / slot_dt)), 0, {{Slots - 2}}); }
vec3 path_at(float t) { t = clamp(t, 0.0, filled); int i = slot_of(t); float u = clamp(t / slot_dt - float(i), 0.0, 1.0); return mix(path[i], path[i + 1], u); }
vec3 vel_at(float t) { t = clamp(t, 0.0, filled); int i = slot_of(t); float u = clamp(t / slot_dt - float(i), 0.0, 1.0); return mix(vel[i], vel[i + 1], u); }
float reach(float t) { return (1.0 - exp(-max(t, 0.0) * 3.2)) / 3.2; }

void vertex() {
    float idx = UV2.x;
    float t0 = COLOR.r, spd = COLOR.g, ang = COLOR.b * 6.2831853, mag = COLOR.a;
    float sz = NORMAL.x, lag = NORMAL.y;
    kind = NORMAL.z;
    float te = t0 * emit;
    float tau = age - te;
    float life = streak_life * (0.7 + 0.3 * spd);
    vec3 pos = vec3(0.0);
    fade = 0.0;
    if (idx < count && tau >= 0.0 && te <= filled + 1e-4 && tau < life) {
        vec3 e = path_at(te);
        vec3 v = vel_at(te);
        float vl = max(length(v), 1e-3);
        vec3 f = v / vl;
        vec3 side = abs(f.y) > 0.95 ? vec3(1.0, 0.0, 0.0) : normalize(cross(f, vec3(0.0, 1.0, 0.0)));
        vec3 up2 = cross(side, f);
        vec3 sd = cos(ang) * side + sin(ang) * up2;
        float s = spread * mag * (0.25 + 0.75 * t0);          // narrow at contact, wider later in the stroke
        vec3 throwv = (f + sd * s) * vl * mix(0.55, 1.0, spd);
        if (back_share > 0.0 && fract(idx * 0.618034) < back_share) throwv = -throwv * 0.35;
        vec3 down = vec3(0.0, -1.0, 0.0);
        vec3 bloom = sd * (0.12 + 0.5 * s) * size;           // the plume widens as it slows
        vec3 headp = e + throwv * reach(tau) + down * 0.5 * gravity * tau * tau + bloom * tau;
        float ta = max(tau - lag, 0.0);
        vec3 tailp = e + throwv * reach(ta) + down * 0.5 * gravity * ta * ta + bloom * ta;
        vec3 hv = (MODELVIEW_MATRIX * vec4(headp, 1.0)).xyz;
        vec3 tv = (MODELVIEW_MATRIX * vec4(tailp, 1.0)).xyz;
        vec3 pv = mix(tv, hv, UV.x);
        vec3 al = hv - tv;
        al = length(al) > 1e-4 ? normalize(al) : vec3(0.0, 1.0, 0.0);
        vec3 across = normalize(cross(al, normalize(pv)));
        float w = (kind > 0.5 ? 0.022 : 0.011 * (0.25 + 0.75 * UV.x)) * size * sz * (1.0 + 1.6 * tau);
        pos = pv + across * (UV.y * 2.0 - 1.0) * w;
        fade = smoothstep(0.0, 0.03, tau) * pow(clamp(1.0 - tau / life, 0.0, 1.0), 1.4);
        POSITION = PROJECTION_MATRIX * vec4(pos, 1.0);
    } else {
        POSITION = vec4(2.0, 2.0, 2.0, 1.0);   // clipped
    }
}

void fragment() {
    float across = 1.0 - abs(UV.y * 2.0 - 1.0);
    float m = kind > 0.5 ? 1.0 - smoothstep(0.2, 1.0, length(UV * 2.0 - 1.0)) : across * across * (0.25 + 0.75 * smoothstep(0.0, 0.8, UV.x));
    ALBEDO = tint.rgb * glow * m * fade;
}
""";

    static ArrayMesh? _mesh;
    static Shader? _shader;

    sealed class Spray
    {
        public MeshInstance3D Node = null!;
        public ShaderMaterial Material = null!;
        public bool On;
        public double Age;
        public float Life;
        public Func<Vector3?>? Weapon;   // the weapon point as drawn now (world), while the stroke is fed
        public Vector3 Origin;
        public readonly Vector3[] Path = new Vector3[Slots];
        public readonly Vector3[] Vel = new Vector3[Slots];
        public int Known;                // path slots set
        public Vector3 LastWeapon;
        public float SpeedScale = 1f;
    }

    readonly Spray[] _sprays = new Spray[Pool];
    int _next;
    /// <summary>Sprays started (blood, sparks), for tests and logs.</summary>
    public int Blood { get; private set; }
    public int Sparks { get; private set; }
    public int Live { get { int n = 0; foreach (var s in _sprays) if (s is { On: true }) n++; return n; } }

    static ArrayMesh SharedMesh()
    {
        if (_mesh is not null) return _mesh;
        int q = ClassicHitDirRules.MaxStreaks;
        var rng = new Random(9173);
        var verts = new Vector3[q * 4]; var uvs = new Vector2[q * 4]; var uv2 = new Vector2[q * 4]; var cols = new Color[q * 4]; var nors = new Vector3[q * 4]; var idx = new int[q * 6];
        for (int i = 0; i < q; i++)
        {
            bool drop = i % 5 == 4;
            // the release time spreads over the stroke, most streaks early (the weapon is still in the body)
            float t0 = (float)Math.Pow(rng.NextDouble(), 1.6);
            var c = new Color(t0, (float)rng.NextDouble(), (float)rng.NextDouble(), (float)Math.Sqrt(rng.NextDouble()));
            var n = drop ? new Vector3((float)(0.6 + 0.8 * rng.NextDouble()), 0.01f, 1f) : new Vector3((float)(0.6 + 0.9 * rng.NextDouble()), (float)(0.05 + 0.13 * rng.NextDouble()), 0f);
            for (int k = 0; k < 4; k++)
            {
                int v = i * 4 + k;
                verts[v] = Vector3.Zero; uvs[v] = new Vector2(k / 2, k % 2); uv2[v] = new Vector2(i, 0f); cols[v] = c; nors[v] = n;
            }
            int b = i * 4, t = i * 6;
            idx[t] = b; idx[t + 1] = b + 1; idx[t + 2] = b + 2; idx[t + 3] = b + 1; idx[t + 4] = b + 3; idx[t + 5] = b + 2;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = nors;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Color] = cols;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        _mesh = new ArrayMesh();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _mesh.CustomAabb = new Aabb(new Vector3(-5f, -5f, -5f), new Vector3(10f, 10f, 10f));
        return _mesh;
    }

    public override void _Ready()
    {
        _shader ??= new Shader { Code = Code };
        for (int i = 0; i < Pool; i++)
        {
            var m = new ShaderMaterial { Shader = _shader };
            var node = new MeshInstance3D { Mesh = SharedMesh(), MaterialOverride = m, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, TopLevel = true, Layers = FxBatch.FxRenderLayer };
            AddChild(node);
            _sprays[i] = new Spray { Node = node, Material = m };
        }
    }

    /// <summary>What one spray looks like and how it moves.</summary>
    public readonly record struct Look(bool Sparks, float Amount, float Spread, float Size = 1f, float BackShare = 0f);

    Spray Take()
    {
        // The oldest goes first when every spray is in use (it is the most faded).
        Spray? pick = null;
        for (int k = 0; k < Pool; k++) { var s = _sprays[(_next + k) % Pool]; if (!s.On) { pick = s; _next = (_next + k + 1) % Pool; break; } }
        if (pick is null) { pick = _sprays[_next]; _next = (_next + 1) % Pool; }
        return pick;
    }

    void Begin(Spray s, Vector3 contact, Look look, double preAge)
    {
        s.On = true; s.Age = preAge; s.Origin = contact;
        s.Life = ClassicHitDirRules.SprayLife(look.Sparks);
        s.Node.GlobalTransform = new Transform3D(Basis.Identity, contact);
        var m = s.Material;
        int count = look.Sparks ? ClassicHitDirRules.SparkStreaks : ClassicHitDirRules.StreakCount(look.Amount);
        m.SetShaderParameter("count", (float)count);
        m.SetShaderParameter("spread", look.Spread);
        m.SetShaderParameter("size", look.Size * (look.Sparks ? 0.7f : 0.9f + 0.1f * look.Amount));
        m.SetShaderParameter("streak_life", look.Sparks ? ClassicHitDirRules.SparkSeconds : ClassicHitDirRules.StreakSeconds);
        m.SetShaderParameter("gravity", look.Sparks ? 9.8f : 3.5f);
        m.SetShaderParameter("back_share", look.BackShare);
        m.SetShaderParameter("tint", look.Sparks ? SparkTint : BloodTint);
        m.SetShaderParameter("glow", look.Sparks ? 3.2f : 1.8f);
        s.SpeedScale = look.Sparks ? 1.6f : 1f;
        s.Node.Visible = true;
        if (look.Sparks) Sparks++; else Blood++;
    }

    static Vector3 ThrowOf(Vector3 v, float scale)
    {
        float l = v.Length();
        if (l < 1e-4f) return Vector3.Zero;
        return v / l * Math.Clamp(l * 0.6f, 3f, 9f) * scale;
    }

    /// <summary>
    /// A melee spray at <paramref name="contact"/> (world): <paramref name="history"/> is the weapon's drawn path from the contact on (world points with their times relative to the contact,
    /// oldest first; may be empty when the contact is now), <paramref name="weapon"/> where the weapon is drawn from now on (fed until the stroke's emission ends), <paramref name="fallback"/> the
    /// direction when the weapon cannot be read.
    /// </summary>
    public void SpawnMelee(Vector3 contact, ReadOnlySpan<(double T, Vector3 P)> history, Func<Vector3?>? weapon, Vector3 fallback, Look look, double preAge)
    {
        if (_sprays[0] is null) return;
        var s = Take();
        s.Weapon = weapon; s.Known = 0;
        // slot 0: the contact itself; then the history, interpolated at every slot it covers
        s.Path[0] = Vector3.Zero;
        var lastP = contact; double lastT = 0.0;
        for (int k = 1; k < Slots; k++)
        {
            double t = k * SlotSeconds;
            if (history.Length == 0 || history[^1].T < t) break;
            Vector3 p = history[^1].P;
            for (int h = 0; h < history.Length; h++)
                if (history[h].T >= t)
                {
                    var (ta, pa) = h > 0 ? history[h - 1] : (0.0, contact);
                    var (tb, pb) = history[h];
                    float u = tb - ta > 1e-6 ? (float)((t - ta) / (tb - ta)) : 1f;
                    p = pa.Lerp(pb, Mathf.Clamp(u, 0f, 1f));
                    break;
                }
            s.Path[k] = p - contact;
            s.Known = k;
            lastP = p; lastT = t;
        }
        s.LastWeapon = lastP;
        FillVelocities(s, fallback);
        Begin(s, contact, look, preAge);
        Push(s);
    }

    /// <summary>A gunfire spray at <paramref name="contact"/> along the incoming shot <paramref name="dir"/> (world); a negative <paramref name="preAge"/> waits that long (the tracer still in flight).</summary>
    public void SpawnShot(Vector3 contact, Vector3 dir, Look look, double preAge = 0.0)
    {
        if (_sprays[0] is null) return;
        var s = Take();
        s.Weapon = null;
        dir = dir.LengthSquared() < 1e-8f ? Vector3.Up : dir.Normalized();
        float speed = look.Sparks ? 8f : 6.5f;
        for (int k = 0; k < Slots; k++) { s.Path[k] = dir * 0.15f * k / (Slots - 1); s.Vel[k] = dir * speed * (look.Sparks ? 1.6f : 1f); }
        s.Known = Slots - 1;
        Begin(s, contact, look, preAge);   // negative: it starts when the shot arrives
        Push(s);
    }

    void FillVelocities(Spray s, Vector3 fallback)
    {
        var fb = fallback.LengthSquared() < 1e-8f ? Vector3.Up : fallback.Normalized();
        for (int k = 0; k <= s.Known; k++)
        {
            int a = Math.Max(0, k - 1), b = Math.Min(s.Known, k + 1);
            var v = b > a ? (s.Path[b] - s.Path[a]) / (float)((b - a) * SlotSeconds) : fb * 6f;
            if (v.Length() > ClassicHitDirRules.MaxWeaponSpeed || v.Length() < 0.3f) v = k > 0 ? s.Vel[k - 1] / Math.Max(s.SpeedScale, 1e-3f) : fb * 6f;
            s.Vel[k] = ThrowOf(v, s.SpeedScale);
            if (s.Vel[k] == Vector3.Zero) s.Vel[k] = fb * 4f * s.SpeedScale;
        }
        for (int k = s.Known + 1; k < Slots; k++) { s.Path[k] = s.Path[s.Known]; s.Vel[k] = s.Vel[s.Known]; }
    }

    void Push(Spray s)
    {
        var m = s.Material;
        m.SetShaderParameter("path", s.Path);
        m.SetShaderParameter("vel", s.Vel);
        m.SetShaderParameter("filled", (float)(s.Known * SlotSeconds));
        m.SetShaderParameter("age", (float)s.Age);
    }

    public override void _Process(double delta)
    {
        foreach (var s in _sprays)
        {
            if (s is null || !s.On) continue;
            s.Age += delta;
            if (s.Age >= s.Life) { s.On = false; s.Node.Visible = false; s.Weapon = null; continue; }
            // feed the stroke: the weapon as drawn now fills the path slots its time has reached
            if (s.Known < Slots - 1)
            {
                int reached = Math.Min(Slots - 1, (int)Math.Floor(s.Age / SlotSeconds));
                if (reached > s.Known)
                {
                    var now = s.Weapon?.Invoke();
                    var p = now is { } w && w.DistanceTo(s.LastWeapon) / Math.Max(delta, 1e-3) < ClassicHitDirRules.MaxWeaponSpeed * 1.5 ? w : s.LastWeapon + (s.Known > 0 ? s.Vel[s.Known] / Math.Max(s.SpeedScale, 1e-3f) * (float)delta * 0.6f : Vector3.Zero);
                    var from = s.Path[s.Known];
                    for (int k = s.Known + 1; k <= reached; k++) s.Path[k] = from.Lerp(p - s.Origin, (k - s.Known) / (float)(reached - s.Known));
                    s.Known = reached; s.LastWeapon = p;
                    FillVelocities(s, s.Vel[0]);
                    s.Material.SetShaderParameter("path", s.Path);
                    s.Material.SetShaderParameter("vel", s.Vel);
                }
                s.Material.SetShaderParameter("filled", (float)Math.Min(s.Age, s.Known * SlotSeconds + SlotSeconds * 0.999));
            }
            else s.Material.SetShaderParameter("filled", (float)(s.Known * SlotSeconds));
            s.Material.SetShaderParameter("age", (float)s.Age);
        }
    }
}
