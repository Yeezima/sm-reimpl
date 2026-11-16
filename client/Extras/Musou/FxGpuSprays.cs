using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using SmContent.Extras.Musou;

namespace SmClient.Extras.Musou;

/// <summary>
/// musou/gpu-particles (Musou Mode, added content; the owner's choice of 2026-11-09): the GPU side of the effect player's spray particles. Every ring sprite and
/// streamer segment a one-shot spray's particles carry (directly or under one combo) is a spawn record, written once when the spray first shows (<c>FxPlayer.MusouGpu.cs</c>); this shader computes the particle's
/// place (the closed formula of <c>FxPlayer.ParticlePos</c>: start, direction, speed, drag, gravity, age), its size, colour, sprite cell and turn from the
/// time, then hands the quad to the same vertex tail and fragment as the CPU batch (<see cref="FxBatch.VertexTail"/>, <see cref="FxBatch.Fragment"/>: the
/// M42 fades, the pull and the atlas lookup are the same code). The CPU writes per frame only one frame per spray (its place, axes and scale, which combos
/// and attached effects move, the effect's opacity, tint and smoke rein) and the time.
/// <para>
/// Data (all RGBA32F, read with <c>texelFetch</c>): records in a texture array of <see cref="FxGpuLayout.RecordsPerLayer"/> records a layer (one pool for
/// additive sprites, one for alpha-blended ones; a layer is uploaded only when a record in it changed); spray frames in another
/// (<see cref="FxGpuLayout.SlotsPerLayer"/> a layer); the leaf types (a spray with one ring child: their fixed fields and curve references) and the curve
/// keys in two plain textures, uploaded when a new effect first plays. A frame's generation number in the record hides the records of a freed spray without
/// touching them.
/// </para>
/// Alpha-blended GPU sprites are drawn in record order (spawn order), not sorted back to front as the CPU batch sorts its quads (**ours**, provisional:
/// the blood and dust of one spray share their colour, so the order shows little); they draw before the CPU batch's alpha quads.
/// </summary>
public sealed partial class FxGpuSprays : Node3D
{
    public const int Layers = 256;   // the guaranteed minimum of array layers (Vulkan, OpenGL 3.3)
    public const int PoolCapacity = Layers * FxGpuLayout.RecordsPerLayer;
    public const int SlotLayers = 128;
    public const int SlotCapacity = SlotLayers * FxGpuLayout.SlotsPerLayer;
    public const int TypeRows = 64, MaxTypes = TypeRows * FxGpuLayout.TypesPerRow;
    public const int CurveRows = 64, CurveCapacity = CurveRows * FxGpuLayout.CurveRow;

    /// <summary>A pool of records for one blend mode: the CPU copy of its texture array (one byte array a layer), the texture and the multimesh that draws them.</summary>
    public sealed class Pool
    {
        public readonly FxGpuBlocks Blocks = new(PoolCapacity);
        public readonly byte[][] Data = new byte[Layers][];
        public readonly bool[] Dirty = new bool[Layers];
        public Texture2DArray? Texture;
        public MultiMesh? Mesh;
        public ShaderMaterial? Material;
        public long UploadedBytes;

        public Pool() { for (int i = 0; i < Layers; i++) Data[i] = new byte[FxGpuLayout.RecordsPerLayer * FxGpuLayout.RecordTexels * 16]; }

        /// <summary>Writes record <paramref name="id"/>: 16 floats, its four texel rows in order (the layer is marked for upload).</summary>
        public void WriteRecord(int id, ReadOnlySpan<float> values)
        {
            int layer = id / FxGpuLayout.RecordsPerLayer, x = id % FxGpuLayout.RecordsPerLayer;
            Dirty[layer] = true;
            var data = MemoryMarshal.Cast<byte, float>(Data[layer].AsSpan());
            for (int field = 0; field < FxGpuLayout.RecordTexels; field++)
                values.Slice(field * 4, 4).CopyTo(data.Slice((field * FxGpuLayout.RecordsPerLayer + x) * 4, 4));
        }

        /// <summary>The floats of record <paramref name="id"/>'s texel row <paramref name="field"/> (4 floats), marked for upload.</summary>
        public Span<float> Texel(int id, int field)
        {
            int layer = id / FxGpuLayout.RecordsPerLayer, x = id % FxGpuLayout.RecordsPerLayer;
            Dirty[layer] = true;
            return MemoryMarshal.Cast<byte, float>(Data[layer].AsSpan()).Slice((field * FxGpuLayout.RecordsPerLayer + x) * 4, 4);
        }
    }

    public readonly Pool Additive = new(), Alpha = new();
    readonly Pool[] _pools;
    readonly byte[] _typeBytes = new byte[MaxTypes * FxGpuLayout.TypeTexels * 16];
    readonly byte[] _curveBytes = new byte[CurveCapacity * 16];

    public FxGpuSprays() { _pools = [Additive, Alpha]; }
    readonly byte[][] _slots = new byte[SlotLayers][];
    readonly bool[] _slotDirty = new bool[SlotLayers];
    public readonly FxGpuBlocks SlotBlocks = new(SlotCapacity);
    readonly int[] _generation = new int[SlotCapacity];
    Texture2DArray? _slotTexture;
    readonly float[] _types = new float[MaxTypes * FxGpuLayout.TypeTexels * 4];
    public int TypeCount;
    bool _typesDirty;
    readonly List<float> _curves = new();
    bool _curvesDirty;
    ImageTexture? _typeTexture, _curveTexture;
    readonly Dictionary<object, (int Start, int Count)> _curveRefs = new(ReferenceEqualityComparer.Instance);
    public double Clock;
    public long SlotUploadBytes, TableUploadBytes;
    public bool Ready { get; private set; }

    public bool Initialize(FxBatch batch)
    {
        for (int i = 0; i < SlotLayers; i++) _slots[i] = new byte[FxGpuLayout.SlotsPerLayer * FxGpuLayout.SlotTexels * 16];
        _slotTexture = Array(SlotLayers, FxGpuLayout.SlotsPerLayer * FxGpuLayout.SlotTexels, 1, _slots);
        if (_slotTexture is null) return false;
        _typeTexture = ImageTexture.CreateFromImage(Image.CreateFromData(FxGpuLayout.TypesPerRow * FxGpuLayout.TypeTexels, TypeRows, false, Image.Format.Rgbaf, new byte[FxGpuLayout.TypesPerRow * FxGpuLayout.TypeTexels * TypeRows * 16]));
        _curveTexture = ImageTexture.CreateFromImage(Image.CreateFromData(FxGpuLayout.CurveRow, CurveRows, false, Image.Format.Rgbaf, new byte[FxGpuLayout.CurveRow * CurveRows * 16]));
        var quad = new QuadMesh { Size = Vector2.One };
        var huge = new Aabb(new Vector3(-5000, -5000, -5000), new Vector3(10000, 10000, 10000));
        foreach (var (pool, blend, name) in new[] { (Alpha, "blend_mix", "GpuAlphaSprites"), (Additive, "blend_add", "GpuAdditiveSprites") })
        {
            pool.Texture = Array(Layers, FxGpuLayout.RecordsPerLayer, FxGpuLayout.RecordTexels, pool.Data);
            if (pool.Texture is null) return false;
            pool.Material = batch.Material(FxBatch.ShaderHeader + GpuUniforms + GpuVertex + FxBatch.VertexTail + GpuVertexClose + FxBatch.Fragment, blend);
            pool.Material.RenderPriority = -1;   // before the CPU batch's sorted alpha quads
            pool.Material.SetShaderParameter("recs", pool.Texture);
            pool.Material.SetShaderParameter("slots", _slotTexture);
            pool.Material.SetShaderParameter("types", _typeTexture);
            pool.Material.SetShaderParameter("curves", _curveTexture);
            pool.Material.SetShaderParameter("min_show", FxPlayer.MinShow);
            pool.Mesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = quad, InstanceCount = PoolCapacity, VisibleInstanceCount = 0 };
            AddChild(new MultiMeshInstance3D { Name = name, Multimesh = pool.Mesh, MaterialOverride = pool.Material, CustomAabb = huge, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Layers = FxBatch.FxRenderLayer });
        }
        Ready = true;
        return true;
    }

    /// <summary>A texture array of <paramref name="layers"/> RGBA float layers, or null when the renderer could not make it (an old GL / ANGLE driver).</summary>
    static Texture2DArray? Array(int layers, int width, int height, byte[][] data)
    {
        var images = new Godot.Collections.Array<Image>();
        for (int i = 0; i < layers; i++) images.Add(Image.CreateFromData(width, height, false, Image.Format.Rgbaf, data[i]));
        var t = new Texture2DArray();
        if (t.CreateFromImages(images) != Error.Ok || t.GetLayers() != layers) { GD.PushWarning($"effects: a {layers}-layer {width}x{height} float texture array could not be made"); return null; }
        return t;
    }

    // ---------------------------------------------------------------------------------------- slots (one frame per live spray)

    /// <summary>A new spray frame (its generation advanced so that records of an earlier user stay hidden), or -1 when all are taken.</summary>
    public int NewSlot(out int generation)
    {
        int s = SlotBlocks.Allocate(1);
        generation = 0;
        if (s < 0) return -1;
        generation = ++_generation[s];
        if (generation > 1 << 22) generation = _generation[s] = 1;
        return s;
    }

    public void FreeSlot(int slot)
    {
        var t = SlotTexel(slot, 1, mark: false);
        if (t[3] != -1f) { t[3] = -1f; _slotDirty[slot / FxGpuLayout.SlotsPerLayer] = true; }
        SlotBlocks.Free(slot, 1);
    }

    Span<float> SlotTexel(int slot, int field, bool mark)
    {
        int layer = slot / FxGpuLayout.SlotsPerLayer, x = slot % FxGpuLayout.SlotsPerLayer;
        if (mark) _slotDirty[layer] = true;
        return MemoryMarshal.Cast<byte, float>(_slots[layer].AsSpan()).Slice((x * FxGpuLayout.SlotTexels + field) * 4, 4);
    }

    /// <summary>Writes a spray's frame for this frame; the layer is uploaded only when a value changed (a still effect costs no upload).</summary>
    public void WriteSlot(int slot, ReadOnlySpan<float> values)
    {
        int layer = slot / FxGpuLayout.SlotsPerLayer, x = slot % FxGpuLayout.SlotsPerLayer;
        var dst = MemoryMarshal.Cast<byte, float>(_slots[layer].AsSpan()).Slice(x * FxGpuLayout.SlotTexels * 4, FxGpuLayout.SlotTexels * 4);
        if (dst.SequenceEqual(values)) return;
        values.CopyTo(dst);
        _slotDirty[layer] = true;
    }

    // ---------------------------------------------------------------------------------------- tables

    /// <summary>The curve table's place of a scalar curve (packed once per curve object).</summary>
    public (int Start, int Count) Curve(SmFormats.Fx.FxCurve curve)
    {
        if (curve.IsEmpty) return (0, 0);
        if (_curveRefs.TryGetValue(curve, out var r)) return r;
        int start = _curves.Count / 4;
        if (start + curve.Keys.Count > CurveCapacity) return (0, -1);
        int n = FxGpuLayout.PackCurve(curve, _curves);
        _curvesDirty = true;
        return _curveRefs[curve] = (start, n);
    }

    public (int Start, int Count) Colours(SmFormats.Fx.FxColourCurve curve)
    {
        if (curve.IsEmpty) return (0, 0);
        if (_curveRefs.TryGetValue(curve, out var r)) return r;
        int start = _curves.Count / 4;
        if (start + 2 * curve.Keys.Count > CurveCapacity) return (0, -1);
        int n = FxGpuLayout.PackColours(curve, _curves);
        _curvesDirty = true;
        return _curveRefs[curve] = (start, n);
    }

    /// <summary>Type <paramref name="type"/>'s texel <paramref name="field"/> (4 floats) in the type table.</summary>
    public Span<float> TypeTexel(int type, int field)
    {
        _typesDirty = true;
        return _types.AsSpan((type * FxGpuLayout.TypeTexels + field) * 4, 4);
    }

    // ---------------------------------------------------------------------------------------- frame

    /// <summary>Uploads what changed this frame and sets the time; the multimeshes draw up to their pools' high-water marks.</summary>
    public void EndFrame()
    {
        if (!Ready) return;
        for (int i = 0; i < SlotLayers; i++)
            if (_slotDirty[i]) { _slotDirty[i] = false; _slotTexture!.UpdateLayer(Image.CreateFromData(FxGpuLayout.SlotsPerLayer * FxGpuLayout.SlotTexels, 1, false, Image.Format.Rgbaf, _slots[i]), i); SlotUploadBytes += _slots[i].Length; }
        if (_typesDirty)
        {
            _typesDirty = false;
            // The type table is laid out as rows of TypesPerRow types; _types is in type order, which is the same order row by row.
            MemoryMarshal.AsBytes(_types.AsSpan()).CopyTo(_typeBytes);
            _typeTexture!.Update(Image.CreateFromData(FxGpuLayout.TypesPerRow * FxGpuLayout.TypeTexels, TypeRows, false, Image.Format.Rgbaf, _typeBytes));
            TableUploadBytes += _typeBytes.Length;
        }
        if (_curvesDirty)
        {
            _curvesDirty = false;
            // The table only grows (curves are never removed), so the rest of the reused buffer stays zero.
            MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_curves)).CopyTo(_curveBytes);
            _curveTexture!.Update(Image.CreateFromData(FxGpuLayout.CurveRow, CurveRows, false, Image.Format.Rgbaf, _curveBytes));
            TableUploadBytes += _curveBytes.Length;
        }
        foreach (var pool in _pools)
        {
            for (int i = 0; i < Layers; i++)
                if (pool.Dirty[i]) { pool.Dirty[i] = false; pool.Texture!.UpdateLayer(Image.CreateFromData(FxGpuLayout.RecordsPerLayer, FxGpuLayout.RecordTexels, false, Image.Format.Rgbaf, pool.Data[i]), i); pool.UploadedBytes += pool.Data[i].Length; }
            pool.Mesh!.VisibleInstanceCount = pool.Blocks.HighWater;
            pool.Material!.SetShaderParameter("now", (float)Clock);
        }
    }

    // ---------------------------------------------------------------------------------------- shader

    const string GpuUniforms = @"
uniform sampler2DArray recs : filter_nearest;
uniform sampler2DArray slots : filter_nearest;
uniform sampler2D types : filter_nearest;
uniform sampler2D curves : filter_nearest;
uniform float now = 0.0;
uniform float min_show = 0.045;
vec4 key(int k) { return texelFetch(curves, ivec2(k % 1024, k / 1024), 0); }
// FxCurve.Evaluate on a packed curve (start, count); the fallback when empty (FxPlayer.At).
float curve(vec2 r, float t, float fallback) {
    int s = int(r.x); int n = int(r.y);
    if (n <= 0) return fallback;
    vec4 k0 = key(s);
    if (!(t > k0.x)) return k0.y;
    for (int i = 1; i < n; i++) {
        vec4 b = key(s + i);
        if (t > b.x) continue;
        vec4 a = key(s + i - 1);
        float span = b.x - a.x;
        return span > 0.0 ? a.y + (b.y - a.y) * ((t - a.x) / span) : b.y;
    }
    return key(s + n - 1).y;
}
// FxColourCurve.Evaluate (two texels a key: time, colour); opaque white when empty.
vec4 colour_curve(vec2 r, float t) {
    int s = int(r.x); int n = int(r.y);
    if (n <= 0) return vec4(1.0);
    if (!(t > key(s).x)) return key(s + 1);
    for (int i = 1; i < n; i++) {
        float tb = key(s + 2 * i).x;
        if (t > tb) continue;
        float ta = key(s + 2 * i - 2).x;
        float span = tb - ta;
        return span > 0.0 ? mix(key(s + 2 * i - 1), key(s + 2 * i + 1), (t - ta) / span) : key(s + 2 * i + 1);
    }
    return key(s + 2 * n - 1);
}
// FxPlayer.Mix and FxPlayer.R, in uints.
uint mixs(uint a, int b) { return (a ^ (uint(b) * 0x9E3779B1u)) * 0x01000193u + 0x9E3779B9u; }
float rnd(uint seed, int k) {
    uint h = seed * 0x2C9277B5u + uint(k) * 0xAC564B05u + 1u;
    h ^= h >> 16u; h *= 0x7FEB352Du; h ^= h >> 15u; h *= 0x846CA68Bu; h ^= h >> 16u;
    return float(h & 0xFFFFFFu) / 16777216.0;
}
// Godot's Basis(axis, angle) as a column-major matrix.
mat3 rot(vec3 a, float phi) {
    float c = cos(phi), s = sin(phi), t = 1.0 - c;
    return mat3(vec3(t * a.x * a.x + c, t * a.x * a.y + s * a.z, t * a.x * a.z - s * a.y),
                vec3(t * a.x * a.y - s * a.z, t * a.y * a.y + c, t * a.y * a.z + s * a.x),
                vec3(t * a.x * a.z + s * a.y, t * a.y * a.z - s * a.x, t * a.z * a.z + c));
}
// FxPlayer.FromY.
mat3 from_y(vec3 y, float roll) {
    y = dot(y, y) < 1e-10 ? vec3(0.0, 1.0, 0.0) : normalize(y);
    vec3 r = abs(y.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
    vec3 x = normalize(cross(r, y));
    vec3 z = cross(x, y);
    return rot(y, roll) * mat3(x, y, z);
}
// FxBatch.Sprite: a sheet cell's rect (negative width: stored rotated).
vec4 cell_rect(vec4 r, vec4 tile, int cell) {
    int tx = int(tile.x); int ty = int(tile.y); int count = tx * ty;
    cell = ((cell % count) + count) % count;
    float cx = float(cell % tx), cy = float(cell / tx), ftx = float(tx), fty = float(ty);
    if (tile.z < 0.5) return vec4(r.x + r.z * cx / ftx, r.y + r.w * cy / fty, r.z / ftx, r.w / fty);
    if (rot_cw) return vec4(r.x + r.z * (1.0 - (cy + 1.0) / fty), r.y + r.w * cx / ftx, -(r.z / fty), r.w / ftx);
    return vec4(r.x + r.z * cy / fty, r.y + r.w * (1.0 - (cx + 1.0) / ftx), -(r.z / fty), r.w / ftx);
}
// FxPlayer.ParticlePos (with Drop): start, direction, speed, drag, gravity, age.
vec3 particle_pos(vec3 origin, vec3 move, float speed, float d, float g, float tau) {
    float disp = d > 1e-4 ? speed * (1.0 - exp(-d * tau)) / d : speed * tau;
    float drop = d > 1e-4 ? g * (tau / d - (1.0 - exp(-d * tau)) / (d * d)) : 0.5 * g * tau * tau;
    return origin + move * disp + vec3(0.0, -drop, 0.0);
}
vec3 srgb_to_linear(vec3 c) {
    return vec3(c.r < 0.04045 ? c.r * (1.0 / 12.92) : pow((c.r + 0.055) * (1.0 / 1.055), 2.4),
                c.g < 0.04045 ? c.g * (1.0 / 12.92) : pow((c.g + 0.055) * (1.0 / 1.055), 2.4),
                c.b < 0.04045 ? c.b * (1.0 / 12.92) : pow((c.b + 0.055) * (1.0 / 1.055), 2.4));
}
";

    /// <summary>
    /// The GPU vertex prelude: FxPlayer.DrawSpray's particle, a combo between it and the leaf (DrawCombo), then DrawRing's quad or one segment of DrawStreamer's ribbon, from the
    /// record, its spray's frame and its type.
    /// </summary>
    const string GpuVertex = @"
void vertex() {
    int id = INSTANCE_ID;
    ivec3 rp = ivec3(id % 512, 0, id / 512);
    vec4 r0 = texelFetch(recs, rp, 0);
    int slot = int(r0.x);
    ivec3 sp = ivec3((slot % 64) * 5, 0, slot / 64);
    vec4 s1 = texelFetch(slots, sp + ivec3(1, 0, 0), 0);
    float tau = now - r0.z;
    int ty = int(r0.y);
    ivec2 tp = ivec2((ty % 64) * 16, ty / 64);
    vec4 n0 = texelFetch(types, tp, 0);
    float life = n0.w;
    bool hide = s1.w != r0.w || r0.w < 0.5 || tau < 0.0 || tau > life;
    vec3 ax = vec3(0.0), ay = vec3(0.0), centre = vec3(0.0);
    vec4 col = vec4(0.0);
    if (!hide) {
        vec4 s0 = texelFetch(slots, sp, 0);
        vec4 s2 = texelFetch(slots, sp + ivec3(2, 0, 0), 0);
        vec4 s3 = texelFetch(slots, sp + ivec3(3, 0, 0), 0);
        vec4 s4 = texelFetch(slots, sp + ivec3(4, 0, 0), 0);
        vec4 r1 = texelFetch(recs, rp + ivec3(0, 1, 0), 0);
        vec4 r2 = texelFetch(recs, rp + ivec3(0, 2, 0), 0);
        vec4 r3 = texelFetch(recs, rp + ivec3(0, 3, 0), 0);
        vec4 n1 = texelFetch(types, tp + ivec2(1, 0), 0);
        vec4 n2 = texelFetch(types, tp + ivec2(2, 0), 0);
        vec4 n3 = texelFetch(types, tp + ivec2(3, 0), 0);
        vec4 n4 = texelFetch(types, tp + ivec2(4, 0), 0);
        vec4 n5 = texelFetch(types, tp + ivec2(5, 0), 0);
        vec4 n6 = texelFetch(types, tp + ivec2(6, 0), 0);
        vec4 n7 = texelFetch(types, tp + ivec2(7, 0), 0);
        vec4 n8 = texelFetch(types, tp + ivec2(8, 0), 0);
        vec4 n9 = texelFetch(types, tp + ivec2(9, 0), 0);
        vec4 n10 = texelFetch(types, tp + ivec2(10, 0), 0);
        vec4 n11 = texelFetch(types, tp + ivec2(11, 0), 0);
        vec4 n12 = texelFetch(types, tp + ivec2(12, 0), 0);
        float S = s0.w;
        mat3 B = mat3(s1.xyz, s2.xyz, s3.xyz);
        float rein = s3.w;
        int flags = int(s4.w + 0.5);
        // DrawSpray: the particle in the spray's current frame.
        vec3 dir = normalize(B * r2.xyz);
        vec3 move = (rein < 1.0 && n6.y > 0.5) ? B * vec3(r2.x * rein, r2.y, r2.z * rein) : dir;
        float speed = r2.w * (n0.z > 0.5 ? S : 1.0);
        vec3 origin = s0.xyz + B * (r1.xyz * S);
        float d = n0.x, g = n0.y;
        vec3 ppos = particle_pos(origin, move, speed, d, g, tau);
        uint ps = uint(r3.x) | (uint(r3.y) << 16u);
        mat3 pb = from_y(dir, rnd(ps, 6) * TAU);
        float scale = S * curve(n7.xy, clamp(tau / life, 0.0, 1.0), 1.0);
        vec3 pos = ppos;
        uint rs = mixs(ps, int(n6.x) + 5);
        if (n12.x > 0.5) {
            // DrawCombo between the particle and the leaf: its scale and offset curves over its own time, its start angles (or the seed's), the leaf's seed.
            vec4 n13 = texelFetch(types, tp + ivec2(13, 0), 0);
            vec4 n14 = texelFetch(types, tp + ivec2(14, 0), 0);
            vec4 n15 = texelFetch(types, tp + ivec2(15, 0), 0);
            float cd = max(n12.z, 0.001);
            float ct = n12.w > 0.5 ? mod(tau, cd) / cd : min(1.0, tau / cd);
            vec3 off = vec3(curve(n14.zw, ct, 0.0), curve(n15.xy, ct, 0.0), curve(n15.zw, ct, 0.0)) * scale;
            int cu = int(n13.w + 0.5);
            float cx = (cu & 1) != 0 ? n13.x : rnd(rs, 1) * TAU;
            float cy = (cu & 2) != 0 ? n13.y : rnd(rs, 2) * TAU;
            float cz = (cu & 4) != 0 ? n13.z : rnd(rs, 3) * TAU;
            pos = pos + pb * off;
            pb = pb * (rot(vec3(0.0, 0.0, 1.0), cz) * rot(vec3(0.0, 1.0, 0.0), cy) * rot(vec3(1.0, 0.0, 0.0), cx));
            scale = scale * curve(n14.xy, ct, 1.0);
            rs = mixs(rs, int(n12.y) + 11);
        }
        // LeafTime.
        float dur = n1.x + n1.y * (rnd(rs, 7) - 0.5);
        float t = 0.0;
        if (n1.z > 0.5) { t = dur <= 0.001 ? 0.0 : mod(tau, dur) / dur; }
        else { dur = max(dur, 0.001); t = min(1.0, max(0.0, tau) / dur); hide = hide || tau > max(dur, min_show); }
        if (n6.w > 0.5) {
            // DrawStreamer in a particle: segment j of 8 through the particle's own earlier path, moved by the combo's offset.
            float trail = curve(n7.zw, t, 0.3);
            float secs = n2.x > 0.5 ? trail : trail / max(2.0, abs(speed));
            int j = int(r3.z + 0.5);
            vec3 shift = pos - ppos;
            vec3 a = particle_pos(origin, move, speed, d, g, max(0.0, tau - secs * float(j) / 8.0)) + shift;
            vec3 b = particle_pos(origin, move, speed, d, g, max(0.0, tau - secs * float(j + 1) / 8.0)) + shift;
            vec3 dd = b - a;
            float len = length(dd);
            hide = hide || trail <= 0.001 || len < 1e-4;
            float u0 = float(j) / 8.0, u1 = float(j + 1) / 8.0;
            if (n2.y > 0.001) {
                // The U along the ribbon: the earlier segments' lengths (a segment too short to draw adds none, as on the CPU).
                float u = 0.0;
                for (int q = 0; q < j; q++) {
                    vec3 qa = particle_pos(origin, move, speed, d, g, max(0.0, tau - secs * float(q) / 8.0));
                    vec3 qb = particle_pos(origin, move, speed, d, g, max(0.0, tau - secs * float(q + 1) / 8.0));
                    float ql = length(qb - qa);
                    if (ql >= 1e-4) u += ql / n2.y;
                }
                u0 = u; u1 = u + len / n2.y;
            }
            vec3 mid = (a + b) * 0.5;
            vec3 side = cross(dd / max(len, 1e-6), CAMERA_POSITION_WORLD - mid);
            side = dot(side, side) < 1e-8 ? INV_VIEW_MATRIX[0].xyz : normalize(side);
            float f = (float(j) + 0.5) / 8.0;
            float master = n8.w > 0.5 ? colour_curve(n8.zw, t).w : 1.0;
            float width = curve(n8.xy, t, 0.1) * scale;
            if (rein < 1.0 && n2.w > 0.5) width *= rein;
            width *= 1.0 - 0.7 * f;
            col = n11.w > 0.5 ? colour_curve(n11.zw, f) * vec4(1.0, 1.0, 1.0, master) : vec4(1.0);
            centre = mid; ax = dd; ay = side * width;
            v_rect = cell_rect(n4, n5, 0);
            v_info = vec3(u0, u1, n2.z + ((flags & 4) != 0 ? 32.0 : 0.0));
        } else {
            // DrawRing on the particle (radius, colour, cell, place, turn).
            float jit = n2.x == 0.0 ? 1.0 : max(0.25, 1.0 + n2.x * (rnd(rs, 8) - 0.5));
            float radius = curve(n7.zw, t, 0.1) * scale * jit;
            if (rein < 1.0 && n2.w > 0.5) radius *= rein;
            hide = hide || radius <= 0.0005;
            col = colour_curve(n11.zw, t);
            int cells = int(n6.z + 0.5);
            int cell = 0;
            if (cells > 1) {
                if (n5.w > 0.5) cell = min(cells - 1, int(rnd(rs, 9) * float(cells)));
                else cell = clamp(int(t * curve(n11.xy, t, 1.0) * float(cells)), 0, cells - 1);
            }
            v_rect = cell_rect(n4, n5, cell);
            centre = pos + pb * (vec3(curve(n8.xy, t, 0.0), curve(n8.zw, t, 0.0), curve(n9.xy, t, 0.0)) * scale);
            float sdir = (n2.y > 0.5 && rnd(rs, 12) < 0.5) ? -1.0 : 1.0;
            int use = int(n3.w + 0.5);
            if (n1.w > 0.5) {
                float roll = ((use & 4) != 0 ? n3.z : rnd(rs, 3) * TAU) + sdir * curve(n10.zw, t, 0.0) * tau;
                float cs = cos(roll), sn = sin(roll);
                vec3 right = INV_VIEW_MATRIX[0].xyz, up = INV_VIEW_MATRIX[1].xyz;
                ax = (right * cs + up * sn) * (2.0 * radius);
                ay = (up * cs - right * sn) * (2.0 * radius);
            } else {
                float rx = ((use & 1) != 0 ? n3.x : rnd(rs, 1) * TAU) + sdir * curve(n9.zw, t, 0.0) * tau;
                float ry = ((use & 2) != 0 ? n3.y : rnd(rs, 2) * TAU) + sdir * curve(n10.xy, t, 0.0) * tau;
                float rz = ((use & 4) != 0 ? n3.z : rnd(rs, 3) * TAU) + sdir * curve(n10.zw, t, 0.0) * tau;
                mat3 bb = pb * (rot(vec3(0.0, 0.0, 1.0), rz) * rot(vec3(0.0, 1.0, 0.0), ry) * rot(vec3(1.0, 0.0, 0.0), rx));
                ax = bb[0] * (2.0 * radius);
                ay = bb[2] * (2.0 * radius);
            }
            v_info = vec3(0.0, 1.0, n2.z + ((flags & 4) != 0 ? 32.0 : 0.0));
        }
        // FxPlayer.Shade: tint, display colours, opacity; FxBatch.Add drops the nearly transparent.
        if ((flags & 1) != 0) col.rgb = s4.rgb * max(col.r, max(col.g, col.b));
        if ((flags & 2) != 0) col.rgb = srgb_to_linear(col.rgb);
        float o = clamp(s2.w, 0.0, 1.0);
        if (o < 1.0) col *= o;
        hide = hide || col.a <= 0.003;
    }
    if (hide) {
        VERTEX = vec3(0.0, 0.0, 1e6);   // behind the camera: the four corners at one point, nothing drawn
    } else {
    COLOR = col;
";
    /// <summary>Closes the <c>else</c> the GPU prelude opens around the shared vertex tail (a vertex function may not return early).</summary>
    const string GpuVertexClose = @"
}
";
}
