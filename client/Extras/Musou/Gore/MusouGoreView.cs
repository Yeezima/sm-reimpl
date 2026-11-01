using System;
using Godot;
using SmNet.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's gore as drawn (Musou pass 2 M10; the simulation is the engine-free <see cref="MusouGoreField"/>). One node in game space (Z up) with seven
/// <see cref="MultiMeshInstance3D"/>s, one per kind of particle, filled each frame from the field's live particles and drawn in one call each: chunks and limbs (shaded,
/// wet), droplets (streaks along their velocity), pools and spatter on the ground (soft irregular blobs from a shader), red mist (camera-facing puffs) and the cleave slashes
/// (a bright stroke that sweeps across the enemy). Nothing is on the screen itself: no overlay, no screen-space effect, no light. Every size and colour is ours (kind: ours).
/// </summary>
public partial class MusouGoreView : Node3D
{
    const string BlobShader = """
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled;
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
    float a = atan(p.y, p.x);
    float wob = 1.0 + 0.17 * sin(a * 3.0 + seed * 40.0) + 0.11 * sin(a * 5.0 + seed * 77.0) + 0.07 * sin(a * 9.0 + seed * 13.0) + 0.1 * pow(max(0.0, sin(a * 4.0 + seed * 21.0)), 6.0);
    float edge = r / (0.88 * wob);
    float body = 1.0 - smoothstep(0.8, 1.0, edge);
    // thick and nearly black in the middle, thin and brighter at the rim
    ALBEDO = tint * mix(0.5, 1.0, smoothstep(0.1, 0.95, edge));
    EMISSION = vec3(ALBEDO.r * 0.5, 0.0, ALBEDO.b * 0.25);   // a little of its own, so wet blood still reads in the map's dark rooms
    ROUGHNESS = 0.22;
    SPECULAR = 0.3;
    METALLIC = 0.0;
    ALPHA = body * mix(0.96, 0.7, smoothstep(0.6, 1.0, edge)) * fade;
}
""";

    const string SlashShader = """
shader_type spatial;
render_mode unshaded, blend_mix, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled;
varying float prog;
void vertex() {
    prog = INSTANCE_CUSTOM.x;
}
void fragment() {
    float x = UV.x * 2.0 - 1.0;
    float y = UV.y * 2.0 - 1.0;
    float head = -1.0 + 2.25 * clamp(prog / 0.4, 0.0, 1.0);
    float bend = -0.32 * (1.0 - x * x);
    float t = 0.2 * pow(max(0.0, 1.0 - x * x), 0.75) + 0.012;
    float d = abs(y - bend);
    float trail = smoothstep(head - 1.2, head, x) * step(x, head);
    float a = (1.0 - smoothstep(t * 0.45, t, d)) * trail;
    float core = (1.0 - smoothstep(0.0, t * 0.55, d)) * smoothstep(head - 0.55, head, x);
    float out_ = 1.0 - smoothstep(0.45, 1.0, prog);
    ALBEDO = mix(vec3(0.78, 0.02, 0.03), vec3(1.0, 0.84, 0.74), core);
    ALPHA = a * out_;
}
""";

    // buffer layouts: 12 floats of transform, then the colour (4) when used, then the custom data (4) when used
    sealed class Layer
    {
        public required MultiMeshInstance3D Node;
        public required float[] Buffer;
        public required int Stride;
        public int Count, Shown;
    }

    readonly Layer _chunks, _limbs, _drops, _pools, _spatters, _mist, _slashes;
    readonly Layer[] _all;

    public MusouGoreField Field { get; } = new();
    /// <summary>Total milliseconds spent in <see cref="Frame"/> and the frames counted (for the log's mean), and the worst frame.</summary>
    public double TotalMs, PreMs, MaxMs;
    public int Frames;
    int _groundVersion = -1;
    double _groundAt = double.NegativeInfinity;

    public MusouGoreView()
    {
        Name = "MusouGore";
        var wet = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.28f, Metallic = 0f, MetallicSpecular = 0.7f, ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel };
        var chunkMesh = Lumpy(new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 10, Rings = 6 }, 0.34f, 3, wet);
        var limbMesh = Lumpy(new CapsuleMesh { Radius = 0.5f, Height = 2f, RadialSegments = 8, Rings = 4 }, 0.24f, 7, wet);
        var streak = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var dropMesh = new BoxMesh { Size = Vector3.One, Material = streak };
        var blob = new ShaderMaterial { Shader = new Shader { Code = BlobShader }, RenderPriority = 1 };
        var quad = new QuadMesh { Size = Vector2.One, Material = blob };
        var mistTexture = new GradientTexture2D
        {
            Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 64, Height = 64,
            Gradient = new Gradient { Offsets = [0f, 0.5f, 1f], Colors = [new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.45f), new Color(1, 1, 1, 0)] },
        };
        var mistMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, VertexColorUseAsAlbedo = true, AlbedoTexture = mistTexture,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, BillboardKeepScale = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled, DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        };
        var mistMesh = new QuadMesh { Size = Vector2.One, Material = mistMaterial };
        // A soft lens (a radial fill stretched along the stroke): thick in the middle, tapering to both ends.
        var slashTexture = new GradientTexture2D
        {
            Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 128, Height = 32,
            Gradient = new Gradient { Offsets = [0f, 0.5f, 1f], Colors = [new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.8f), new Color(1, 1, 1, 0)] },
        };
        var slashMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, VertexColorUseAsAlbedo = true, AlbedoTexture = slashTexture,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = true, DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled, RenderPriority = 2,
        };
        var slashMesh = new QuadMesh { Size = Vector2.One, Material = slashMaterial };

        _chunks = Make("Chunks", chunkMesh, MusouGoreRules.GibPool, colours: true, custom: false);
        _limbs = Make("Limbs", limbMesh, MusouGoreRules.GibPool, colours: true, custom: false);
        _drops = Make("Droplets", dropMesh, MusouGoreRules.DropletPool, colours: true, custom: false);
        _pools = Make("Pools", quad, MusouGoreRules.PoolRing, colours: true, custom: true);
        _spatters = Make("Spatter", quad, MusouGoreRules.SpatterRing, colours: true, custom: true);
        _mist = Make("Mist", mistMesh, MusouGoreRules.MistPool, colours: true, custom: false);
        _slashes = Make("Slashes", slashMesh, MusouGoreRules.SlashPool * 2, colours: true, custom: false);
        _all = [_chunks, _limbs, _drops, _pools, _spatters, _mist, _slashes];
    }

    /// <summary>A mesh with its vertices pushed in and out by a hash of their place (the same place the same push, so the seams stay shut), normals made again: a lump of meat, not a pill.</summary>
    static ArrayMesh Lumpy(Mesh source, float amount, int seed, Material material)
    {
        var arrays = source.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            uint h = (uint)(MathF.Round(v.X * 40f) * 73856093f) ^ (uint)(MathF.Round(v.Y * 40f) * 19349663f) ^ (uint)(MathF.Round(v.Z * 40f) * 83492791f) ^ (uint)(seed * 2654435761u);
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            float push = 1f + ((h & 0xFFFF) / 65535f - 0.5f) * 2f * amount;
            vertices[i] = v * push;
        }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        foreach (int i in indices) tool.AddVertex(vertices[i]);
        tool.GenerateNormals();
        var mesh = tool.Commit();
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }

    Layer Make(string name, Mesh mesh, int capacity, bool colours, bool custom)
    {
        int stride = 12 + (colours ? 4 : 0) + (custom ? 4 : 0);
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = colours, UseCustomData = custom, Mesh = mesh, InstanceCount = capacity };
        mm.VisibleInstanceCount = 0;
        var node = new MultiMeshInstance3D
        {
            Name = name, Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            CustomAabb = new Aabb(new Vector3(-4000f, -4000f, -500f), new Vector3(8000f, 8000f, 1000f)),
        };
        AddChild(node);
        return new Layer { Node = node, Buffer = new float[capacity * stride], Stride = stride };
    }

    static void Put(Layer l, in Basis b, Vector3 o)
    {
        int at = l.Count * l.Stride;
        var f = l.Buffer;
        f[at] = b.X.X; f[at + 1] = b.Y.X; f[at + 2] = b.Z.X; f[at + 3] = o.X;
        f[at + 4] = b.X.Y; f[at + 5] = b.Y.Y; f[at + 6] = b.Z.Y; f[at + 7] = o.Y;
        f[at + 8] = b.X.Z; f[at + 9] = b.Y.Z; f[at + 10] = b.Z.Z; f[at + 11] = o.Z;
    }

    static void Colour(Layer l, float r, float g, float b, float a)
    {
        int at = l.Count * l.Stride + 12;
        l.Buffer[at] = r; l.Buffer[at + 1] = g; l.Buffer[at + 2] = b; l.Buffer[at + 3] = a;
    }

    static void Custom(Layer l, int offset, float x, float y)
    {
        int at = l.Count * l.Stride + offset;
        l.Buffer[at] = x; l.Buffer[at + 1] = y; l.Buffer[at + 2] = 0f; l.Buffer[at + 3] = 0f;
    }

    static Vector3 V(Num.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Steps the field and uploads this frame's particles.</summary>
    public void Frame(float delta)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Field.Step(delta);
        var f = Field;
        // The ground (pools and spots) is rebuilt when it changed and ten times a second for the spreading and the fading; between, its last buffers stay on the card.
        bool ground = f.GroundVersion != _groundVersion || f.Time - _groundAt >= 0.1;
        if (ground) { _groundVersion = f.GroundVersion; _groundAt = f.Time; }
        foreach (var l in _all) if (ground || (l != _pools && l != _spatters)) l.Count = 0;

        for (int i = 0; i < f.Gibs.Length; i++)
        {
            ref readonly var g = ref f.Gibs[i];
            if (!g.Live) continue;
            float shrink = g.Resting ? Math.Clamp(1f - (g.Age - MusouGoreRules.GibRestSeconds) / MusouGoreRules.FadeSeconds, 0f, 1f) : 1f;
            var basis = Basis.FromEuler(V(g.Angle));
            var layer = g.Kind == 1 ? _limbs : _chunks;
            float s = g.Size * shrink;
            basis = basis * Basis.FromScale(g.Kind == 1 ? new Vector3(s * 0.9f, s * 1.7f, s * 0.9f) : new Vector3(s * 2f, s * 1.5f, s * 1.8f));
            Put(layer, basis, V(g.Pos));
            Colour(layer, g.Colour.X, g.Colour.Y, g.Colour.Z, 1f);
            layer.Count++;
        }
        for (int i = 0; i < f.Drops.Length; i++)
        {
            ref readonly var d = ref f.Drops[i];
            if (!d.Live) continue;
            var vel = V(d.Vel);
            float speed = vel.Length();
            var along = speed > 1e-3f ? vel / speed : Vector3.Up;
            var side = along.Cross(Vector3.Up);
            if (side.LengthSquared() < 1e-4f) side = Vector3.Right;
            side = side.Normalized();
            var up = side.Cross(along);
            float length = Math.Clamp(d.Size * 2.5f + speed * 0.012f, 0.05f, 0.4f);
            Put(_drops, new Basis(side * d.Size, up * d.Size, along * length), V(d.Pos));
            Colour(_drops, d.Colour.X, d.Colour.Y, d.Colour.Z, 1f);
            _drops.Count++;
        }
        for (int i = 0; ground && i < f.Pools.Length; i++)
        {
            ref readonly var p = ref f.Pools[i];
            if (!p.Live) continue;
            float spread = 1f - MathF.Exp(-p.Age / 1.6f);   // spilling out over a few seconds
            float radius = p.Radius * (0.12f + 0.88f * spread);
            float alpha = Math.Clamp((MusouGoreRules.PoolSeconds - p.Age) / MusouGoreRules.FadeSeconds, 0f, 1f);
            Put(_pools, new Basis(new Vector3(radius * 2f, 0, 0), new Vector3(0, radius * 2f, 0), Vector3.Back), new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z + 0.02f + i * 0.0003f));
            Colour(_pools, p.Colour.X, p.Colour.Y, p.Colour.Z, 1f);
            Custom(_pools, 16, p.Seed, alpha);
            _pools.Count++;
        }
        for (int i = 0; ground && i < f.Spatters.Length; i++)
        {
            ref readonly var s = ref f.Spatters[i];
            if (!s.Live) continue;
            float alpha = Math.Clamp((MusouGoreRules.SpatterSeconds - s.Age) / MusouGoreRules.FadeSeconds, 0f, 1f);
            var basis = new Basis(Vector3.Back, s.Rotation) * Basis.FromScale(new Vector3(s.Size * 2f, s.Size * 2f, 1f));
            Put(_spatters, basis, new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z + 0.03f + i * 0.0003f));
            Colour(_spatters, s.Colour.X, s.Colour.Y, s.Colour.Z, 1f);
            Custom(_spatters, 16, s.Seed, alpha);
            _spatters.Count++;
        }
        for (int i = 0; i < f.Mists.Length; i++)
        {
            ref readonly var m = ref f.Mists[i];
            if (!m.Live) continue;
            float t = m.Age / m.Life;
            float size = m.Size * (0.5f + 1.6f * (1f - (1f - t) * (1f - t)));
            Put(_mist, new Basis(Vector3.Right * size, Vector3.Up * size, Vector3.Back * size), V(m.Pos));
            Colour(_mist, 0.62f, 0.02f, 0.03f, 0.85f * (1f - t) * (1f - t));
            _mist.Count++;
        }
        for (int i = 0; i < f.Slashes.Length; i++)
        {
            ref readonly var s = ref f.Slashes[i];
            if (!s.Live) continue;
            // The stroke sweeps in over the first 0.1 s (it grows from its start toward its end) and fades by the end: a wide red one under a thin bright one.
            float age = s.Age / MusouGoreRules.SlashSeconds;
            float reveal = Math.Clamp(age / 0.4f, 0.08f, 1f);
            float fade = 1f - Math.Clamp((age - 0.45f) / 0.55f, 0f, 1f);
            var axis = V(s.Axis);
            var centre = V(s.Pos) + axis * (s.Length * 0.5f * (reveal - 1f));
            float len = s.Length * reveal;
            Put(_slashes, new Basis(axis * len, V(s.Up) * s.Length * 0.22f, V(s.Normal)), centre);
            Colour(_slashes, 0.8f, 0.03f, 0.04f, 0.75f * fade);
            _slashes.Count++;
            Put(_slashes, new Basis(axis * len, V(s.Up) * s.Length * 0.09f, V(s.Normal)), centre);
            Colour(_slashes, 1f, 0.62f, 0.55f, fade);
            _slashes.Count++;
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

    /// <summary>Particles drawn now: chunks, limbs, droplets, pools, spatter, mist, slashes.</summary>
    public string Describe() => $"{_chunks.Count} chunks, {_limbs.Count} limbs, {_drops.Count} drops, {_pools.Count} pools, {_spatters.Count} spatter, {_mist.Count} mist, {_slashes.Count} slashes";
}
