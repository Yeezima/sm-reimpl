using System;
using Godot;
using SmNet.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The gore as drawn (the simulation is the engine-free <see cref="MusouGoreField"/>). The look follows the game's own trailer (owner, 2026-11-01): blood is a dark oxblood to near black,
/// never bright red, in two shapes. (a) Soft billowing mist that blooms outward, hangs and thins slowly. (b) Ink-like splatter: irregular, jagged, stringy shapes with holes and tendrils, like
/// spilled ink frozen mid-air, tumbling in small numbers beside a few small round droplets; the same shapes stain the ground, beside dark pools with ragged edges. There are no lumps and no flat
/// streaks. Nothing is loaded from the install (its decals are not read); the shapes are made in code at start (<see cref="GoreTextures"/>) and drawn as lit billboards and ground quads, so the
/// scene's lights and ambient colour them. One node in game space (Z up), five <see cref="MultiMeshInstance3D"/>s. Every size and colour is ours (kind: ours).
/// </summary>
public partial class MusouGoreView : Node3D
{
    // Shared by the ink and the mist: an atlas cell chosen by the instance, a lit surface, the thick parts nearly black and the thin rim brown-red.
    // //BILLBOARD and //BUMP are filled in for the camera-facing variant; //PLANE for the ground and the slash.
    const string AtlasShader = """
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, diffuse_lambert, specular_schlick_ggx//RENDER;
uniform sampler2D atlas : filter_linear_mipmap, repeat_disable;
uniform float cols = 4.0;
uniform float ink = 1.0;
varying vec3 tint;
varying vec4 cd;
void vertex() {
    tint = COLOR.rgb;
    cd = INSTANCE_CUSTOM;
//BILLBOARD
}
void fragment() {
    float cell = floor(cd.x + 0.5);
    vec2 uv = (vec2(mod(cell, cols), floor(cell / cols)) + UV) / cols;
    float a = texture(atlas, uv).r;
    if (ink > 0.5) {
        float body = smoothstep(0.12, 0.55, a);
        ALBEDO = mix(tint * 1.35, tint * 0.5, smoothstep(0.35, 0.95, a));
        EMISSION = ALBEDO * 0.1;
        ROUGHNESS = 0.3;
        SPECULAR = 0.45;
        ALPHA = body * cd.y;
//BUMP
    } else {
        ALBEDO = tint * mix(1.25, 0.75, a);
        EMISSION = ALBEDO * 0.3;
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
        ALPHA = a * cd.y * COLOR.a;
    }
}
""";

    const string BillboardCode = """
    float c = cos(cd.z), s = sin(cd.z);
    vec2 v = vec2(c * VERTEX.x - s * VERTEX.y, s * VERTEX.x + c * VERTEX.y);
    float sx = length(MODEL_MATRIX[0].xyz), sy = length(MODEL_MATRIX[1].xyz);
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
    VERTEX = vec3(v.x * sx, v.y * sy, 0.0);
""";

    // A little relief from the shape's own edge, so a fleck reads as a wet clot and sits in the light.
    const string BumpCode = """
        vec3 n = vec3(-dFdx(a) * 9.0, -dFdy(a) * 9.0, 1.0);
        NORMAL = normalize(n);
""";

    // A dark pool on the ground: ragged, finger-edged, with a few holes, near black in the thick middle and brown-red at the thin rim, wet.
    const string PoolShader = """
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled;
uniform sampler2D noise : filter_linear_mipmap, repeat_enable;
varying vec3 tint;
varying float seed;
varying float fade;
void vertex() {
    tint = COLOR.rgb;
    seed = INSTANCE_CUSTOM.x;
    fade = INSTANCE_CUSTOM.y;
}
void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float r = length(p);
    float ang = atan(p.y, p.x);
    float n1 = texture(noise, UV * 0.8 + vec2(seed * 7.1, seed * 3.3)).r;
    float n2 = texture(noise, UV * 2.2 + vec2(seed * 1.7, seed * 9.1)).r;
    float n3 = texture(noise, UV * 5.5 + vec2(seed * 4.3, seed * 2.2)).r;
    float rays = texture(noise, vec2(ang * 0.55 + seed * 5.0, 0.37)).r;
    float h = (1.0 - r / 0.95) + (n1 - 0.5) * 0.95 + (n2 - 0.5) * 0.55 + (n3 - 0.5) * 0.3 + (rays - 0.5) * 0.9 * r;
    float body = smoothstep(0.0, 0.05, h);
    float holes = smoothstep(0.1, 0.2, n2 + h * 0.4);
    ALBEDO = mix(tint * 1.35, tint * 0.45, smoothstep(0.02, 0.4, h));
    EMISSION = ALBEDO * 0.1;
    ROUGHNESS = 0.12;
    SPECULAR = 0.6;
    METALLIC = 0.0;
    ALPHA = body * holes * 0.93 * fade;
}
""";

    // buffer layouts: 12 floats of transform, then the colour (4), then the custom data (4)
    sealed class Layer
    {
        public required MultiMeshInstance3D Node;
        public required float[] Buffer;
        public int Count, Shown;
        public const int Stride = 20;
    }

    readonly Layer _air, _pools, _spatters, _mist, _slashes;
    readonly Layer[] _all;

    public MusouGoreField Field { get; } = new();
    /// <summary>Total milliseconds spent in <see cref="Frame"/> and the frames counted (for the log's mean), and the worst frame.</summary>
    public double TotalMs, PreMs, MaxMs;
    public int Frames;
    int _groundVersion = -1;
    double _groundAt = double.NegativeInfinity;

    static ShaderMaterial AtlasMaterial(string render, bool billboard, Texture2D atlas, float cols, bool ink, int priority)
    {
        string code = AtlasShader.Replace("//RENDER", render).Replace("//BILLBOARD", billboard ? BillboardCode : "").Replace("//BUMP", billboard ? BumpCode : "");
        var m = new ShaderMaterial { Shader = new Shader { Code = code }, RenderPriority = priority };
        m.SetShaderParameter("atlas", atlas);
        m.SetShaderParameter("cols", cols);
        m.SetShaderParameter("ink", ink ? 1f : 0f);
        return m;
    }

    public MusouGoreView()
    {
        Name = "MusouGore";
        var ink = GoreTextures.Ink();
        var cloud = GoreTextures.Cloud();
        var noise = GoreTextures.Ground();
        var inkQuad = new QuadMesh { Size = Vector2.One, Material = AtlasMaterial("", true, ink, 4f, true, 0) };
        var mistQuad = new QuadMesh { Size = Vector2.One, Material = AtlasMaterial("", true, cloud, 2f, false, 0) };
        var stainQuad = new QuadMesh { Size = Vector2.One, Material = AtlasMaterial("", false, ink, 4f, true, 2) };
        var slashQuad = new QuadMesh { Size = Vector2.One, Material = AtlasMaterial("", false, ink, 4f, true, 3) };
        var poolMaterial = new ShaderMaterial { Shader = new Shader { Code = PoolShader }, RenderPriority = 1 };
        poolMaterial.SetShaderParameter("noise", noise);
        var poolQuad = new QuadMesh { Size = Vector2.One, Material = poolMaterial };

        _air = Make("Ink", inkQuad, MusouGoreRules.GibPool + MusouGoreRules.DropletPool);
        _pools = Make("Pools", poolQuad, MusouGoreRules.PoolRing);
        _spatters = Make("Stains", stainQuad, MusouGoreRules.SpatterRing);
        _mist = Make("Mist", mistQuad, MusouGoreRules.MistPool);
        _slashes = Make("Slashes", slashQuad, MusouGoreRules.SlashPool);
        _all = [_air, _pools, _spatters, _mist, _slashes];
    }

    static Layer Make(string name, Mesh mesh, int capacity)
    {
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true, Mesh = mesh, InstanceCount = capacity };
        mm.VisibleInstanceCount = 0;
        var node = new MultiMeshInstance3D
        {
            Name = name, Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            CustomAabb = new Aabb(new Vector3(-4000f, -4000f, -500f), new Vector3(8000f, 8000f, 1000f)),
        };
        return new Layer { Node = node, Buffer = new float[capacity * Layer.Stride] };
    }

    public override void _Ready() { foreach (var l in _all) AddChild(l.Node); }

    static void Put(Layer l, in Basis b, Vector3 o, float r, float g, float bl, float a, float cx, float cy, float cz = 0f)
    {
        int at = l.Count * Layer.Stride;
        var f = l.Buffer;
        f[at] = b.X.X; f[at + 1] = b.Y.X; f[at + 2] = b.Z.X; f[at + 3] = o.X;
        f[at + 4] = b.X.Y; f[at + 5] = b.Y.Y; f[at + 6] = b.Z.Y; f[at + 7] = o.Y;
        f[at + 8] = b.X.Z; f[at + 9] = b.Y.Z; f[at + 10] = b.Z.Z; f[at + 11] = o.Z;
        f[at + 12] = r; f[at + 13] = g; f[at + 14] = bl; f[at + 15] = a;
        f[at + 16] = cx; f[at + 17] = cy; f[at + 18] = cz; f[at + 19] = 0f;
        l.Count++;
    }

    static Vector3 V(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Atlas cells of the ink: 0 to 5 splats with tendrils, 6 to 9 streaks, 10 to 13 jagged shards, 14 and 15 round droplets.</summary>
    static float Cell(float seed, int first, int count) => first + MathF.Min(count - 1, MathF.Floor(seed * count));

    /// <summary>Steps the field and uploads this frame's particles.</summary>
    public void Frame(float delta)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Field.Step(delta);
        var f = Field;
        // The ground (pools and stains) is rebuilt when it changed and ten times a second for the spreading and the fading; between, its last buffers stay on the card.
        bool ground = f.GroundVersion != _groundVersion || f.Time - _groundAt >= 0.1;
        if (ground) { _groundVersion = f.GroundVersion; _groundAt = f.Time; }
        foreach (var l in _all) if (ground || (l != _pools && l != _spatters)) l.Count = 0;

        for (int i = 0; i < f.Gibs.Length; i++)
        {
            ref readonly var g = ref f.Gibs[i];
            if (!g.Live) continue;
            bool limb = g.Kind == 1;
            float cell = limb ? Cell(g.Size * 31f % 1f, 6, 4) : Cell(g.Size * 17f % 1f, 10, 4);
            float s = g.Size * (limb ? 4.6f : 2.6f);
            Put(_air, new Basis(Vector3.Right * s, Vector3.Up * s * (limb ? 0.55f : 1f), Vector3.Back), V(g.Pos), g.Colour.X, g.Colour.Y, g.Colour.Z, 1f, cell, 0.97f, g.Angle.Z);
        }
        for (int i = 0; i < f.Drops.Length; i++)
        {
            ref readonly var d = ref f.Drops[i];
            if (!d.Live) continue;
            float fadeOut = Math.Clamp((d.Life - d.Age) / 0.25f, 0f, 1f);
            if (d.Ink)
            {
                float s = d.Size * 2.4f;
                Put(_air, new Basis(Vector3.Right * s, Vector3.Up * s, Vector3.Back), V(d.Pos), d.Colour.X, d.Colour.Y, d.Colour.Z, 1f, Cell(d.Seed, 0, 10), 0.97f * fadeOut, d.Rot);
            }
            else
            {
                float s = d.Size * 2.6f;
                Put(_air, new Basis(Vector3.Right * s, Vector3.Up * s, Vector3.Back), V(d.Pos), d.Colour.X, d.Colour.Y, d.Colour.Z, 1f, 14f + (d.Seed > 0.5f ? 1f : 0f), 0.97f * fadeOut, d.Rot);
            }
        }
        for (int i = 0; ground && i < f.Pools.Length; i++)
        {
            ref readonly var p = ref f.Pools[i];
            if (!p.Live) continue;
            float spread = 1f - MathF.Exp(-p.Age / 1.6f);   // spilling out over a few seconds
            float radius = p.Radius * (0.12f + 0.88f * spread);
            float alpha = Math.Clamp((MusouGoreRules.PoolSeconds - p.Age) / MusouGoreRules.FadeSeconds, 0f, 1f);
            Put(_pools, new Basis(new Vector3(radius * 2f, 0, 0), new Vector3(0, radius * 2f, 0), Vector3.Back), new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z + 0.02f + i * 0.0003f), p.Colour.X, p.Colour.Y, p.Colour.Z, 1f, p.Seed, alpha);
        }
        for (int i = 0; ground && i < f.Spatters.Length; i++)
        {
            ref readonly var s = ref f.Spatters[i];
            if (!s.Live) continue;
            float alpha = Math.Clamp((MusouGoreRules.SpatterSeconds - s.Age) / MusouGoreRules.FadeSeconds, 0f, 1f);
            var basis = new Basis(Vector3.Back, s.Rotation) * Basis.FromScale(new Vector3(s.Size * 2f, s.Size * 2f, 1f));
            Put(_spatters, basis, new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z + 0.03f + i * 0.0003f), s.Colour.X, s.Colour.Y, s.Colour.Z, 1f, Cell(s.Seed, 0, 6), alpha);
        }
        for (int i = 0; i < f.Mists.Length; i++)
        {
            ref readonly var m = ref f.Mists[i];
            if (!m.Live) continue;
            float t = m.Age / m.Life;
            // blooms outward fast, then hangs and keeps swelling slowly; in quickly, out slowly
            float size = m.Size * (0.4f + 0.95f * (1f - MathF.Pow(1f - t, 3f)) + 0.15f * t);
            float alpha = 0.62f * MathF.Min(1f, t / 0.07f) * MathF.Pow(1f - t, 1.4f);
            Put(_mist, new Basis(Vector3.Right * size, Vector3.Up * size, Vector3.Back), V(m.Pos), m.Colour.X, m.Colour.Y, m.Colour.Z, 1f, Cell(m.Seed * 3.7f % 1f, 0, 4), alpha, m.Seed * MathF.Tau + m.Age * (m.Seed < 0.5f ? 0.18f : -0.18f));
        }
        for (int i = 0; i < f.Slashes.Length; i++)
        {
            ref readonly var s = ref f.Slashes[i];
            if (!s.Live) continue;
            // The stroke sweeps in over the first 0.1 s (it grows from its start toward its end) and fades by the end: an ink streak across the enemy.
            float age = s.Age / MusouGoreRules.SlashSeconds;
            float reveal = Math.Clamp(age / 0.4f, 0.08f, 1f);
            float fade = 1f - Math.Clamp((age - 0.5f) / 0.5f, 0f, 1f);
            var axis = V(s.Axis);
            var centre = V(s.Pos) + axis * (s.Length * 0.5f * (reveal - 1f));
            float len = s.Length * reveal;
            Put(_slashes, new Basis(axis * len, V(s.Up) * s.Length * 0.34f, V(s.Normal)), centre, 0.035f, 0.003f, 0.004f, 1f, Cell(s.Seed, 6, 4), 0.95f * fade);
        }
        foreach (var l in _all)
        {
            var mm = l.Node.Multimesh;
            if (l.Count != l.Shown) { mm.VisibleInstanceCount = l.Count; l.Shown = l.Count; }
            if (l.Count > 0 && (ground || (l != _pools && l != _spatters))) RenderingServer.MultimeshSetBuffer(mm.GetRid(), l.Buffer);
        }
        double ms = watch.Elapsed.TotalMilliseconds;
        TotalMs += ms; Frames++;
        if (ms > MaxMs) MaxMs = ms;
    }

    /// <summary>Particles drawn now: flying ink (clots and droplets), pools, stains, mist, slashes.</summary>
    public string Describe() => $"{_air.Count} flying, {_pools.Count} pools, {_spatters.Count} stains, {_mist.Count} mist, {_slashes.Count} slashes";
}
