using System;
using Godot;
using SmSim.Extras.Weather;

namespace SmClient.Extras.Weather;

/// <summary>
/// Rain motion (series 37; client only, presentation only, **ours**): one precipitation layer as a fixed set of drops in a world-anchored field wrapped round the camera (<see cref="WeatherFieldWrap"/>), drawn as one MultiMesh whose
/// vertex shader places each drop (seed in the instance's custom data, the field's drift and box as uniforms). Replaces the CPU-particle emitters of the rain, the grit, the flakes and the embers: those spawned drops over the camera and
/// let them fall, so a camera moving as fast as the drops (a jump-pack fall, a drop off a ledge, a fast slide) outran them and the rain thinned or vanished (measured on Manufactorum, series 37). Here every drop is a fixed world
/// point between frames and the box round the camera always holds all of them, at any speed. Nothing is simulated on the CPU (the drift is one vector a frame), so it costs less than the emitters did.
/// <para>Kinds: a streak (the rain's axial billboard, soft across and tapered along its fall) or a dot (a camera-facing soft round fleck; additive for embers, which also twinkle and shade from yellow to deep red).</para>
/// </summary>
public sealed partial class WeatherField : MultiMeshInstance3D
{
    public enum Kind { Streak, Dot, Ember }

    readonly Vector3[] _seeds;
    readonly ShaderMaterial _material;
    Vector3 _drift;

    /// <summary>The box round the camera (m, full size) this layer's drops live in.</summary>
    public Vector3 Box { get; }
    /// <summary>The velocity the field moves at (m/s; the fall, or the wind), set by the rig each frame.</summary>
    public Vector3 Velocity { get; set; }
    /// <summary>The material (tint, fall direction) the rig sets.</summary>
    public ShaderMaterial Material => _material;

    /// <summary>Drawing or not (the old emitters' switch; off hides every drop at once).</summary>
    public bool Emitting { get => Visible; set => Visible = value; }

    /// <summary>The drops drawn now (the trim's count; at most the capacity it was built with).</summary>
    public int Amount
    {
        get => Multimesh.VisibleInstanceCount;
        set => Multimesh.VisibleInstanceCount = Math.Clamp(value, 0, Multimesh.InstanceCount);
    }

    /// <summary>For the engine only (a node type needs a parameterless constructor); the rig always uses the full one.</summary>
    public WeatherField() : this("WeatherField", Kind.Streak, 1, 0, Vector3.One, Vector2.One, 1f, 1f, 1f, 0f, 0) { }

    /// <param name="capacity">Drops allocated (the most it can show); <paramref name="amount"/> are shown at first.</param>
    /// <param name="size">The quad (width, length) for a streak, (diameter, diameter) for a dot (m).</param>
    /// <param name="scaleMin">Per-drop size variance.</param>
    public WeatherField(string name, Kind kind, int capacity, int amount, Vector3 box, Vector2 size, float scaleMin, float scaleMax, float nearFadeM, float nearFullM, int seed)
    {
        Name = name;
        Box = box;
        _material = new ShaderMaterial { Shader = ShaderFor(kind) };
        _material.SetShaderParameter("box", box);
        _material.SetShaderParameter("near_fade", nearFadeM);
        _material.SetShaderParameter("near_full", nearFullM);
        var mesh = new QuadMesh { Size = size, Material = _material };
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = mesh,
            InstanceCount = Math.Max(1, capacity),
        };
        _seeds = new Vector3[mm.InstanceCount];
        var rng = new Random(seed);
        for (int i = 0; i < mm.InstanceCount; i++)
        {
            var s = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            _seeds[i] = s;
            mm.SetInstanceTransform(i, Transform3D.Identity);
            mm.SetInstanceCustomData(i, new Color(s.X, s.Y, s.Z, scaleMin + (scaleMax - scaleMin) * (float)rng.NextDouble()));
        }
        mm.CustomAabb = new Aabb(-box * 0.5f - Vector3.One * 2f, box + Vector3.One * 4f);   // the shader moves every drop within the box round this node
        mm.VisibleInstanceCount = Math.Clamp(amount, 0, mm.InstanceCount);
        Multimesh = mm;
        CastShadow = ShadowCastingSetting.Off;
        Visible = false;
    }

    /// <summary>One frame of <paramref name="dt"/> real seconds: the field moves by its velocity (kept inside one box).</summary>
    public void Advance(double dt)
    {
        var d = WeatherFieldWrap.Advance(new System.Numerics.Vector3(_drift.X, _drift.Y, _drift.Z), new System.Numerics.Vector3(Velocity.X, Velocity.Y, Velocity.Z), dt, new System.Numerics.Vector3(Box.X, Box.Y, Box.Z));
        _drift = new Vector3(d.X, d.Y, d.Z);
        _material.SetShaderParameter("drift", _drift);
    }

    /// <summary>The far rain's dry ring under cover: no drop within <paramref name="holeM"/> of the centre horizontally nor beyond <paramref name="ringM"/>; 0 keeps the whole box.</summary>
    public void SetHole(float holeM, float ringM)
    {
        HoleM = holeM;
        _material.SetShaderParameter("hole", holeM);
        _material.SetShaderParameter("ring", ringM);
        _ringM = ringM;
    }

    public float HoleM { get; private set; }
    float _ringM;

    /// <summary>Test aid (the density log, the selftest): the drops drawn now within <paramref name="radius"/> of <paramref name="at"/>, by the shader's own arithmetic (<see cref="WeatherFieldWrap"/>), not faded out at a face or in the hole.</summary>
    public int CountWithin(Vector3 at, float radius)
    {
        if (!Visible) return 0;
        var c = GlobalPosition;
        var centre = new System.Numerics.Vector3(c.X, c.Y, c.Z);
        var box = new System.Numerics.Vector3(Box.X, Box.Y, Box.Z);
        var drift = new System.Numerics.Vector3(_drift.X, _drift.Y, _drift.Z);
        var p = new System.Numerics.Vector3(at.X, at.Y, at.Z);
        int n = 0, shown = Amount;
        float r2 = radius * radius;
        for (int i = 0; i < shown; i++)
        {
            var w = WeatherFieldWrap.Place(new System.Numerics.Vector3(_seeds[i].X, _seeds[i].Y, _seeds[i].Z), box, drift, centre);
            if (System.Numerics.Vector3.DistanceSquared(w, p) > r2) continue;
            if (WeatherFieldWrap.EdgeFade(w, centre, box) * WeatherFieldWrap.HoleFade(w, centre, HoleM, _ringM) < 0.5f) continue;
            n++;
        }
        return n;
    }

    // ---- the shaders: the arithmetic of WeatherFieldWrap.Place, EdgeFade and HoleFade per vertex ----

    static Shader? _streak, _dot, _ember;

    static Shader ShaderFor(Kind kind) => kind switch
    {
        Kind.Streak => _streak ??= new Shader { Code = Code(StreakRender, StreakVertex, StreakFragment) },
        Kind.Dot => _dot ??= new Shader { Code = Code(DotRender, DotVertex, DotFragment) },
        _ => _ember ??= new Shader { Code = Code(EmberRender, DotVertex, EmberFragment) },
    };

    static string Code(string render, string vertexTail, string fragment) => $$"""
        shader_type spatial;
        {{render}}
        uniform vec4 tint : source_color = vec4(0.74, 0.80, 0.90, 0.3);
        uniform vec3 fall_dir = vec3(0.0, -1.0, 0.0);
        uniform vec3 box = vec3(24.0);
        uniform vec3 drift = vec3(0.0);
        uniform float near_fade = 1.2;
        uniform float near_full = 0.0;
        uniform float hole = 0.0;
        uniform float ring = 0.0;
        varying float v_fade;
        varying float v_hash;
        float axis_fade(float d, float size) {
            float q = abs(d) / (size * 0.5);
            return 1.0 - smoothstep(1.0 - {{WeatherFieldWrap.EdgeShare.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)}}, 1.0, q);
        }
        void vertex() {
            vec3 centre = MODEL_MATRIX[3].xyz;
            vec3 lo = centre - box * 0.5;
            vec3 seed = INSTANCE_CUSTOM.xyz;
            float sc = INSTANCE_CUSTOM.w;
            vec3 rel = mod(seed * box + drift - lo, box);
            vec3 c = lo + rel;
            vec3 d = c - centre;
            float fade = axis_fade(d.x, box.x) * axis_fade(d.y, box.y) * axis_fade(d.z, box.z);
            if (hole > 0.0) { float r = length(d.xz); fade *= smoothstep(hole, hole + 2.0, r) * (1.0 - smoothstep(ring - 2.0, ring, r)); }
            v_hash = fract(sin(dot(seed, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
            vec3 to_cam = CAMERA_POSITION_WORLD - c;
            float dist = max(length(to_cam), 1e-4);
            float near = near_full > near_fade ? smoothstep(near_fade, near_full, dist) : smoothstep(0.35, 0.35 + near_fade, dist);
            v_fade = fade * near;
            {{vertexTail}}
            POSITION = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(world, 1.0));
        }
        void fragment() {
            {{fragment}}
        }
        """;

    const string StreakRender = "render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;";
    const string DotRender = "render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;";
    const string EmberRender = "render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;";

    /// <summary>The axial billboard of the weather finish: the quad turned about the fall direction to face the camera.</summary>
    const string StreakVertex = """
            vec3 axis = normalize(fall_dir);
            vec3 side = cross(axis, to_cam / dist);
            float sl = length(side);
            side = sl > 1e-3 ? side / sl : vec3(1.0, 0.0, 0.0);
            vec3 world = c + side * (VERTEX.x * sc) - axis * (VERTEX.y * sc);
        """;
    const string StreakFragment = """
            float x = (UV.x - 0.5) * 2.0;
            float across = exp(-x * x * 3.5);
            float along = mix(0.25, 1.0, smoothstep(0.0, 0.85, UV.y)) * (1.0 - smoothstep(0.9, 1.0, UV.y)) * smoothstep(0.0, 0.08, UV.y);
            ALBEDO = tint.rgb;
            ALPHA = clamp(tint.a * across * along * v_fade, 0.0, 1.0);
        """;

    /// <summary>A camera-facing quad (the old particle billboard).</summary>
    const string DotVertex = """
            vec3 world = c + (INV_VIEW_MATRIX[0].xyz * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y) * sc;
        """;
    /// <summary>The soft round dot of the old SoftDot texture: a solid core (1 to 0.9 at half the radius), a soft rim to 0.</summary>
    const string DotFragment = """
            float r = length(UV - vec2(0.5)) * 2.0;
            float prof = r < 0.5 ? mix(1.0, 0.9, r / 0.5) : mix(0.9, 0.0, clamp((r - 0.5) / 0.5, 0.0, 1.0));
            ALBEDO = tint.rgb;
            ALPHA = clamp(tint.a * prof * v_fade, 0.0, 1.0);
        """;
    /// <summary>Embers: the old life ramp (pale yellow to deep red, fading) run as a slow per-ember cycle (about 4 s, the old life), faded in at its start so an ember never pops.</summary>
    const string EmberFragment = """
            float r = length(UV - vec2(0.5)) * 2.0;
            float prof = r < 0.5 ? mix(1.0, 0.9, r / 0.5) : mix(0.9, 0.0, clamp((r - 0.5) / 0.5, 0.0, 1.0));
            float life = fract(v_hash * 7.31 + TIME * 0.25);
            vec3 ramp = mix(vec3(1.0, 0.85, 0.45), vec3(0.9, 0.18, 0.04), life);
            float a = smoothstep(0.0, 0.15, life) * (1.0 - life);
            ALBEDO = tint.rgb * ramp;
            ALPHA = clamp(tint.a * prof * a * v_fade, 0.0, 1.0);
        """;
}
