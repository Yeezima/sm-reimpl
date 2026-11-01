using System;
using Godot;

namespace SmClient;

/// <summary>
/// Musou Mode's shell bursts (M9): the dirt plume, the clods, the dust skirt and the fire that the install's effects do not have at this size. One <see cref="MultiMesh"/> of camera-facing puffs,
/// moved on the CPU (ballistic with drag, no collision), drawn by one shader from a noise texture made here, with a sphere's light on each puff so a plume reads as a volume. It also lays the ground's
/// shock rings and the lights of the flash. Godot space. All of it is ours (kind: ours, the reference is Post Scriptum's artillery); the original fire and sparks are played over it by
/// <c>NetworkView.TfwBurst</c>. <see cref="Capacity"/> puffs at most: a new one takes the oldest slot.
/// </summary>
public sealed partial class TfwBlastCloud : Node3D
{
    public const int Capacity = 3072, Lights = 6, Rings = 8;
    const int Stride = 20;   // 12 of the transform, 4 of the colour, 4 of the custom data

    const string PuffShader = """
        shader_type spatial;
        render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
        uniform sampler2D noise_tex : filter_linear_mipmap, repeat_enable;
        varying vec4 v_data;
        varying vec3 v_tint;
        varying float v_near;
        void vertex() {
            float size = length(MODEL_MATRIX[0].xyz);
            vec4 view = VIEW_MATRIX * vec4(MODEL_MATRIX[3].xyz, 1.0);
            float a = INSTANCE_CUSTOM.x * 6.2831 + INSTANCE_CUSTOM.y * 0.8;
            vec2 p = mat2(vec2(cos(a), sin(a)), vec2(-sin(a), cos(a))) * VERTEX.xy * size;
            view.xy += p;
            POSITION = PROJECTION_MATRIX * view;
            v_data = INSTANCE_CUSTOM;
            v_tint = COLOR.rgb;
            // A puff round the camera thins out: a burst must not paint the whole screen one colour.
            v_near = smoothstep(size * 0.35, size * 1.6 + 2.0, -view.z);
        }
        void fragment() {
            vec2 c = UV * 2.0 - 1.0;
            float seed = v_data.x * 31.7;
            float age = v_data.y;
            float n = texture(noise_tex, UV * 0.42 + vec2(seed, seed * 0.37)).r * 0.6 + texture(noise_tex, UV * 1.3 + vec2(seed * 0.61, age * 0.25)).r * 0.4;
            float r = length(c);
            float body = clamp(1.0 - r * r, 0.0, 1.0);
            float density = clamp(body * (0.62 + n * 1.25) - (0.1 + 0.6 * age * age), 0.0, 1.0);
            float alpha = density * density * (3.0 - 2.0 * density);
            vec3 normal = normalize(vec3(c * 1.2 + (vec2(n, 1.0 - n) - 0.5) * 1.6, 0.8));
            float light = clamp(dot(normal, normalize(vec3(-0.3, 0.8, 0.5))) * 0.8 + 0.4, 0.15, 1.35);
            vec3 colour = v_tint * light * (0.7 + 0.8 * (n - 0.4));
            float heat = v_data.z * (1.0 - smoothstep(0.1, 0.9, r * 0.75 + age * 0.85));
            colour = mix(colour, mix(vec3(1.0, 0.28, 0.05), vec3(1.0, 0.8, 0.42), clamp(heat * 1.4, 0.0, 1.0)) * 2.2, clamp(heat * 1.8, 0.0, 1.0));
            ALBEDO = colour;
            ALPHA = clamp(alpha * 1.5, 0.0, 1.0) * v_data.w * v_near;
        }
        """;

    const string RingShader = """
        shader_type spatial;
        render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
        uniform float age = 0.0;
        void fragment() {
            vec2 c = UV * 2.0 - 1.0;
            float r = length(c);
            float band = smoothstep(0.45 + age * 0.3, 0.92, r) * (1.0 - smoothstep(0.92, 1.0, r));
            ALBEDO = vec3(0.47, 0.42, 0.36);
            ALPHA = band * 0.2 * (1.0 - age) * (1.0 - age);
        }
        """;

    struct Puff
    {
        public Vector3 Position, Velocity;
        public float Age, Life, Size0, Size1, Gravity, Drag, Seed, Heat, Opacity;
        public Color Tint;
    }

    readonly Puff[] _puffs = new Puff[Capacity];
    readonly float[] _buffer = new float[Capacity * Stride];
    readonly Random _rng = new(9);
    readonly OmniLight3D[] _lights = new OmniLight3D[Lights];
    readonly float[] _lightLeft = new float[Lights];
    readonly MeshInstance3D[] _rings = new MeshInstance3D[Rings];
    readonly ShaderMaterial[] _ringMaterials = new ShaderMaterial[Rings];
    readonly float[] _ringAge = new float[Rings];
    MultiMesh? _mesh;
    int _next, _light, _ring, _high;
    bool _any;
    static readonly StringName AgeName = "age";

    /// <summary>Puffs alive after the last frame (the log).</summary>
    public int Live { get; private set; }
    /// <summary>Puffs made since the start.</summary>
    public long Spawned { get; private set; }
    /// <summary>Whole frames' CPU time of the last frame (ms; the log).</summary>
    public double LastMs { get; private set; }

    public override void _Ready()
    {
        var material = new ShaderMaterial { Shader = new Shader { Code = PuffShader } };
        material.SetShaderParameter("noise_tex", NoiseTexture());
        _mesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, InstanceCount = Capacity, VisibleInstanceCount = 0, Mesh = new QuadMesh { Size = Vector2.One } };
        var node = new MultiMeshInstance3D { Name = "Puffs", Multimesh = _mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-4000f, -500f, -4000f), new Vector3(8000f, 1500f, 8000f)) };
        AddChild(node);
        for (int i = 0; i < Lights; i++)
        {
            _lights[i] = new OmniLight3D { LightColor = new Color(1f, 0.62f, 0.3f), OmniRange = 40f, LightEnergy = 0f, ShadowEnabled = false, Visible = false };
            AddChild(_lights[i]);
        }
        var ringShader = new Shader { Code = RingShader };
        var quad = new QuadMesh { Size = new Vector2(2f, 2f), Orientation = PlaneMesh.OrientationEnum.Y };
        for (int i = 0; i < Rings; i++)
        {
            _ringMaterials[i] = new ShaderMaterial { Shader = ringShader };
            _rings[i] = new MeshInstance3D { Mesh = quad, MaterialOverride = _ringMaterials[i], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
            AddChild(_rings[i]);
            _ringAge[i] = 2f;
        }
    }

    /// <summary>A small tiling noise made once, so nothing about the look comes from a file.</summary>
    static ImageTexture NoiseTexture()
    {
        const int N = 128;
        var rng = new Random(3);
        var image = Image.CreateEmpty(N, N, false, Image.Format.L8);
        var total = new float[N * N];
        float amplitude = 1f, sum = 0f;
        for (int cells = 6; cells <= 48; cells *= 2)
        {
            var grid = new float[cells * cells];
            for (int i = 0; i < grid.Length; i++) grid[i] = rng.NextSingle();
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float fx = x * cells / (float)N, fy = y * cells / (float)N;
                    int ix = (int)fx, iy = (int)fy;
                    float tx = fx - ix, ty = fy - iy;
                    tx = tx * tx * (3f - 2f * tx); ty = ty * ty * (3f - 2f * ty);
                    float a = grid[iy * cells + ix], b = grid[iy * cells + (ix + 1) % cells], c = grid[(iy + 1) % cells * cells + ix], d = grid[(iy + 1) % cells * cells + (ix + 1) % cells];
                    total[y * N + x] += amplitude * Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
                }
            sum += amplitude;
            amplitude *= 0.55f;
        }
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float v = Math.Clamp((total[y * N + x] / sum - 0.5f) * 2.1f + 0.5f, 0f, 1f);
                image.SetPixel(x, y, new Color(v, v, v));
            }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    float R(float min, float max) => min + _rng.NextSingle() * (max - min);

    void Add(Vector3 at, Vector3 velocity, float life, float size0, float size1, float gravity, float drag, float heat, float opacity, Color tint)
    {
        ref var p = ref _puffs[_next];
        p = new Puff { Position = at, Velocity = velocity, Life = life, Size0 = size0, Size1 = size1, Gravity = gravity, Drag = drag, Seed = _rng.NextSingle(), Heat = heat, Opacity = opacity, Tint = tint };
        _next = (_next + 1) % Capacity;
        _high = Math.Max(_high, _next == 0 ? Capacity : _next);
        _any = true;
        Spawned++;
    }

    /// <summary>
    /// A shell's burst at <paramref name="at"/> on the ground: the plume thrown high in a narrow cone, the clods that fall out of it, the skirt of dust that races out along the ground, a short
    /// fireball, a shock ring and the flash's light. <paramref name="detail"/> 1 full, less for a far one.
    /// </summary>
    public void Burst(Vector3 at, float detail)
    {
        var earth = new Color(0.20f, 0.155f, 0.115f);
        var dust = new Color(0.40f, 0.35f, 0.29f);
        int plume = (int)(40 * detail), clods = (int)(44 * detail), skirt = (int)(16 * detail), fire = (int)(24 * detail);
        float lean = R(0f, MathF.Tau);
        var tilt = new Vector3(MathF.Cos(lean), 0f, MathF.Sin(lean)) * R(0f, 0.12f);
        for (int i = 0; i < plume; i++)
        {
            float t = i / (float)Math.Max(1, plume);
            float speed = R(24f, 62f) * (0.35f + 0.65f * t);
            float a = R(0f, MathF.Tau), spread = R(0f, 0.16f);
            var way = (Vector3.Up + tilt + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * spread).Normalized();
            Add(at + new Vector3(R(-1.4f, 1.4f), R(0f, 1f), R(-1.4f, 1.4f)), way * speed, R(2.2f, 4.4f), R(3f, 5f), R(11f, 19f), 13f, 0.42f, 0f, 1f, earth.Lerp(dust, R(0f, 0.35f)));
        }
        for (int i = 0; i < clods; i++)
        {
            float a = R(0f, MathF.Tau), spread = R(0.08f, 0.62f);
            var way = (Vector3.Up + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * spread).Normalized();
            float size = R(0.14f, 0.5f);
            Add(at + Vector3.Up * 0.4f, way * R(13f, 34f), R(1.2f, 2.6f), size, size * 0.9f, 22f, 0.08f, 0f, 1f, new Color(0.055f, 0.045f, 0.04f));
        }
        for (int i = 0; i < skirt; i++)
        {
            float a = i * MathF.Tau / Math.Max(1, skirt) + R(-0.2f, 0.2f);
            var way = new Vector3(MathF.Cos(a), R(0.02f, 0.12f), MathF.Sin(a));
            Add(at + way * R(1f, 3f) + Vector3.Up * 0.6f, way * R(14f, 30f), R(2.4f, 5f), R(2f, 3.4f), R(7f, 11f), -0.4f, 2.2f, 0f, R(0.3f, 0.5f), dust);
        }
        for (int i = 0; i < fire; i++)
        {
            float a = R(0f, MathF.Tau), up = R(0.3f, 1f);
            var way = new Vector3(MathF.Cos(a) * (1f - up), up, MathF.Sin(a) * (1f - up));
            Add(at + Vector3.Up * R(0.3f, 2f), way * R(9f, 30f), R(0.55f, 1.15f), R(3.5f, 5.5f), R(9f, 14f), -1.5f, 2.4f, 1f, 1f, new Color(0.2f, 0.09f, 0.05f));
        }
        _lights[_light].GlobalPosition = at + Vector3.Up * 3f;
        _lights[_light].Visible = true;
        _lightLeft[_light] = 0.22f;
        _light = (_light + 1) % Lights;
        _rings[_ring].GlobalPosition = at + Vector3.Up * 0.18f;
        _ringAge[_ring] = 0f;
        _rings[_ring].Visible = true;
        _ring = (_ring + 1) % Rings;
    }

    /// <summary>A round's dust where it struck the ground, or the spray of one on a body.</summary>
    public void Spit(Vector3 at, bool hit)
    {
        var tint = hit ? new Color(0.26f, 0.05f, 0.04f) : new Color(0.33f, 0.29f, 0.25f);
        for (int i = 0; i < 3; i++)
            Add(at, new Vector3(R(-2f, 2f), R(2f, 6.5f), R(-2f, 2f)), R(0.3f, 0.65f), R(0.25f, 0.45f), R(0.8f, 1.4f), 9f, 0.9f, 0f, 0.8f, tint);
    }

    /// <summary>Moves the puffs and fades the lights and rings.</summary>
    public void Frame(float delta)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < Lights; i++)
        {
            if (_lightLeft[i] <= 0f) continue;
            _lightLeft[i] -= delta;
            float k = Math.Max(0f, _lightLeft[i] / 0.22f);
            _lights[i].LightEnergy = 36f * k;
            if (_lightLeft[i] <= 0f) _lights[i].Visible = false;
        }
        for (int i = 0; i < Rings; i++)
        {
            if (_ringAge[i] >= 1f) continue;
            _ringAge[i] += delta / 0.5f;
            float scale = 1.5f + 30f * MathF.Sqrt(Math.Min(1f, _ringAge[i]));
            _rings[i].Scale = new Vector3(scale, 1f, scale);
            _ringMaterials[i].SetShaderParameter(AgeName, Math.Min(1f, _ringAge[i]));
            if (_ringAge[i] >= 1f) _rings[i].Visible = false;
        }
        if (!_any || _mesh is null) return;
        int alive = 0;
        for (int i = 0; i < _high; i++)
        {
            ref var p = ref _puffs[i];
            int b = i * Stride;
            if (p.Age >= p.Life) { _buffer[b] = 0f; _buffer[b + 5] = 0f; _buffer[b + 10] = 0f; _buffer[b + 19] = 0f; continue; }
            p.Age += delta;
            float k = Math.Min(1f, p.Age / p.Life);
            p.Velocity *= MathF.Max(0f, 1f - p.Drag * delta);
            p.Velocity.Y -= p.Gravity * delta;
            p.Position += p.Velocity * delta;
            float size = Mathf.Lerp(p.Size0, p.Size1, 1f - (1f - k) * (1f - k));
            float fade = k < 0.06f ? k / 0.06f : 1f - Mathf.Clamp((k - 0.55f) / 0.45f, 0f, 1f);
            _buffer[b] = size; _buffer[b + 1] = 0f; _buffer[b + 2] = 0f; _buffer[b + 3] = p.Position.X;
            _buffer[b + 4] = 0f; _buffer[b + 5] = size; _buffer[b + 6] = 0f; _buffer[b + 7] = p.Position.Y;
            _buffer[b + 8] = 0f; _buffer[b + 9] = 0f; _buffer[b + 10] = size; _buffer[b + 11] = p.Position.Z;
            _buffer[b + 12] = p.Tint.R; _buffer[b + 13] = p.Tint.G; _buffer[b + 14] = p.Tint.B; _buffer[b + 15] = 1f;
            _buffer[b + 16] = p.Seed; _buffer[b + 17] = k; _buffer[b + 18] = p.Heat; _buffer[b + 19] = p.Opacity * fade;
            alive++;
        }
        Live = alive;
        _mesh.VisibleInstanceCount = _high;
        RenderingServer.MultimeshSetBuffer(_mesh.GetRid(), _buffer);
        if (alive == 0) { _any = false; _high = 0; _next = 0; _mesh.VisibleInstanceCount = 0; }
        LastMs = watch.Elapsed.TotalMilliseconds;
    }
}
