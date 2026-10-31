using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// The Musou crowd's mid and far tier (design section D): each look's five clips are baked once, at load, into vertex-animation textures (the skinned
/// vertex positions and normals of every frame at <see cref="Fps"/> frames a second, in memory only, never written to disk), then drawn by one
/// <see cref="MultiMeshInstance3D"/> per look with an embedded shader that reads the vertex's place in two frames and blends them (the frames and the blend in
/// <c>INSTANCE_CUSTOM</c>, with the sink of a body in its fourth value). The mesh data and skin binds are read on the main thread; the skinning runs on a worker.
/// No normal map in this tier (no baked tangents); the albedo, its colour, the roughness and the alpha scissor are taken from each surface's own material.
/// Values marked ours: 15 frames a second, 2048 texels a row, half floats for the positions, 8-bit normals.
/// </summary>
public static class MusouVat
{
    public const int Fps = 15, Width = 2048;

    /// <summary>One clip's rows in the textures: the first frame, the number of frames, its length (s), whether it loops.</summary>
    public readonly record struct ClipRows(int First, int Frames, float Duration, bool Loops);

    /// <summary>A baked look: the image bytes (made on the worker) and, once <see cref="Upload"/> ran (main thread), the textures and the far mesh.</summary>
    public sealed class Baked
    {
        public required byte[] Positions, Normals;
        public required int Height, RowsPerFrame, Vertices;
        public required ClipRows[] Clips;
        public required int[] SurfaceOffsets;
        public required SurfaceData[] Surfaces;
        public ArrayMesh? Mesh;
        public double BakeMilliseconds;
    }

    /// <summary>A surface's arrays as read on the main thread.</summary>
    public sealed class SurfaceData
    {
        public required Vector3[] Positions, Normals;
        public required Vector2[] Uv;
        public required int[] Indices, Bones;
        public required float[] Weights;
        public Material? Material;
    }

    /// <summary>Reads what the bake needs from <paramref name="assets"/> (main thread) and starts the bake on a worker; null when there is nothing to bake.</summary>
    public static Task<Baked?>? Start(MarineAssets assets, HavokBoundAnimation?[] clips)
    {
        if (assets.Pieces.Count == 0) return null;
        var skeleton = assets.Model.Rig.Skeleton;
        int boneCount = skeleton.BoneNames.Count;
        var parents = new int[boneCount];
        var rest = new Transform3D[boneCount];
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < boneCount; i++) { parents[i] = skeleton.Parents[i]; rest[i] = MarineAssets.ToGodot(skeleton.ReferencePose[i]); index[skeleton.BoneNames[i]] = i; }
        var surfaces = new List<SurfaceData>();
        var bindBones = new List<int[]>();
        var bindPoses = new List<Transform3D[]>();
        foreach (var piece in assets.Pieces)
        {
            var skin = piece.Skin;
            int binds = skin.GetBindCount();
            var bb = new int[binds];
            var bp = new Transform3D[binds];
            for (int b = 0; b < binds; b++) { bb[b] = index.TryGetValue(skin.GetBindName(b), out int bone) ? bone : 0; bp[b] = skin.GetBindPose(b); }
            for (int s = 0; s < piece.Mesh.GetSurfaceCount(); s++)
            {
                var a = piece.Mesh.SurfaceGetArrays(s);
                surfaces.Add(new SurfaceData
                {
                    Positions = a[(int)Mesh.ArrayType.Vertex].AsVector3Array(), Normals = a[(int)Mesh.ArrayType.Normal].AsVector3Array(),
                    Uv = a[(int)Mesh.ArrayType.TexUV].AsVector2Array(), Indices = a[(int)Mesh.ArrayType.Index].AsInt32Array(),
                    Bones = a[(int)Mesh.ArrayType.Bones].AsInt32Array(), Weights = a[(int)Mesh.ArrayType.Weights].AsFloat32Array(),
                    Material = (s < piece.Overrides.Count ? piece.Overrides[s] : null) ?? piece.Mesh.SurfaceGetMaterial(s),   // a look's paint first
                });
                bindBones.Add(bb); bindPoses.Add(bp);
            }
        }
        if (surfaces.Count == 0) return null;
        return Task.Run(() => Bake(parents, rest, surfaces.ToArray(), bindBones.ToArray(), bindPoses.ToArray(), clips));
    }

    static Baked? Bake(int[] parents, Transform3D[] rest, SurfaceData[] surfaces, int[][] bindBones, Transform3D[][] bindPoses, HavokBoundAnimation?[] clips)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int vertices = 0;
        var offsets = new int[surfaces.Length];
        for (int s = 0; s < surfaces.Length; s++) { offsets[s] = vertices; vertices += surfaces[s].Positions.Length; }
        if (vertices == 0) return null;
        int rowsPerFrame = (vertices + Width - 1) / Width;
        var rows = new ClipRows[clips.Length];
        int frames = 0;
        for (int c = 0; c < clips.Length; c++)
        {
            float duration = clips[c]?.Animation.Duration ?? 0f;
            int n = Math.Max(2, (int)MathF.Ceiling(duration * Fps));
            rows[c] = new ClipRows(frames, n, MathF.Max(duration, 1e-3f), MusouLooks.Loops(c));
            frames += n;
        }
        int height = frames * rowsPerFrame;
        if (height > 16384) return null;   // beyond a texture's height: the look stays in the skinned tier
        var positions = new byte[Width * height * 8];
        var normals = new byte[Width * height * 4];
        int bones = parents.Length;
        var local = new Transform3D[bones];
        var global = new Transform3D[bones];
        var sample = Array.Empty<HavokTransform>();
        var skinning = new Transform3D[surfaces.Length][];
        for (int s = 0; s < surfaces.Length; s++) skinning[s] = new Transform3D[bindBones[s].Length];
        for (int c = 0; c < clips.Length; c++)
        {
            var clip = clips[c];
            var r = rows[c];
            for (int f = 0; f < r.Frames; f++)
            {
                Array.Copy(rest, local, bones);
                if (clip is not null)
                {
                    float t = r.Loops ? r.Duration * f / r.Frames : r.Duration * f / (r.Frames - 1);
                    if (sample.Length != clip.Animation.TrackCount) sample = new HavokTransform[clip.Animation.TrackCount];
                    clip.Animation.SampleInto(MathF.Min(t, clip.Animation.Duration), sample);
                    for (int k = 0; k < sample.Length; k++)
                    {
                        int bone = clip.TrackToBone[k];
                        if ((uint)bone < (uint)bones) local[bone] = MarineAssets.ToGodot(sample[k]);
                    }
                }
                LockRoot(local, rest);
                for (int b = 0; b < bones; b++) global[b] = parents[b] >= 0 && parents[b] < b ? global[parents[b]] * local[b] : local[b];
                int row0 = (r.First + f) * rowsPerFrame;
                for (int s = 0; s < surfaces.Length; s++)
                {
                    var bb = bindBones[s]; var bp = bindPoses[s]; var m = skinning[s];
                    for (int b = 0; b < bb.Length; b++) m[b] = global[bb[b]] * bp[b];
                    var sd = surfaces[s];
                    for (int v = 0; v < sd.Positions.Length; v++)
                    {
                        Vector3 p = Vector3.Zero, n = Vector3.Zero;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = sd.Weights[4 * v + k];
                            if (w <= 0f) continue;
                            int bind = sd.Bones[4 * v + k];
                            if ((uint)bind >= (uint)m.Length) continue;
                            p += m[bind] * sd.Positions[v] * w;
                            n += m[bind].Basis * sd.Normals[v] * w;
                        }
                        int id = offsets[s] + v;
                        int texel = (row0 + id / Width) * Width + id % Width;
                        WriteHalf(positions, texel * 8, p.X); WriteHalf(positions, texel * 8 + 2, p.Y); WriteHalf(positions, texel * 8 + 4, p.Z); WriteHalf(positions, texel * 8 + 6, 1f);
                        n = n.LengthSquared() > 1e-8f ? n.Normalized() : Vector3.Up;
                        normals[texel * 4] = (byte)Math.Clamp((int)MathF.Round((n.X * 0.5f + 0.5f) * 255f), 0, 255);
                        normals[texel * 4 + 1] = (byte)Math.Clamp((int)MathF.Round((n.Y * 0.5f + 0.5f) * 255f), 0, 255);
                        normals[texel * 4 + 2] = (byte)Math.Clamp((int)MathF.Round((n.Z * 0.5f + 0.5f) * 255f), 0, 255);
                        normals[texel * 4 + 3] = 255;
                    }
                }
            }
        }
        return new Baked
        {
            Positions = positions, Normals = normals, Height = height, RowsPerFrame = rowsPerFrame, Vertices = vertices, Clips = rows,
            SurfaceOffsets = offsets, Surfaces = surfaces, BakeMilliseconds = watch.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>The root bone stays over the body's place (the server moves the enemy; a clip's own travel would double it): its rest's horizontal place is kept. Both tiers do this.</summary>
    public static void LockRoot(Transform3D[] local, Transform3D[] rest)
    {
        if (local.Length == 0) return;
        var o = local[0].Origin;
        local[0].Origin = new Vector3(rest[0].Origin.X, rest[0].Origin.Y, o.Z);
    }

    static void WriteHalf(byte[] into, int at, float v)
    {
        ushort h = BitConverter.HalfToUInt16Bits((Half)v);
        into[at] = (byte)h; into[at + 1] = (byte)(h >> 8);
    }

    /// <summary>The frames and blend of a clip at <paramref name="time"/> seconds into it, as <c>INSTANCE_CUSTOM</c>'s first three values.</summary>
    public static (float Row0, float Row1, float Blend) FramesAt(ClipRows r, float time)
    {
        if (r.Loops)
        {
            float f = time / r.Duration * r.Frames;
            f -= MathF.Floor(f / r.Frames) * r.Frames;
            int f0 = Math.Clamp((int)f, 0, r.Frames - 1);
            return (r.First + f0, r.First + (f0 + 1) % r.Frames, f - f0);
        }
        float g = Math.Clamp(time / r.Duration, 0f, 1f) * (r.Frames - 1);
        int g0 = Math.Clamp((int)g, 0, r.Frames - 2);
        return (r.First + g0, r.First + g0 + 1, Math.Clamp(g - g0, 0f, 1f));
    }

    // ---------------------------------------------------------------- the GPU side (main thread)

    static Shader? _shader;

    const string ShaderCode = @"
shader_type spatial;
render_mode cull_back, depth_draw_opaque;
uniform sampler2D vat_pos : filter_nearest;
uniform sampler2D vat_nrm : filter_nearest;
uniform int vat_width = 2048;
uniform int rows_per_frame = 1;
uniform int vertex_offset = 0;
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform float roughness_value = 0.8;
uniform float alpha_cut = 0.0;
varying float keep;

void vertex() {
    int id = VERTEX_ID + vertex_offset;
    ivec2 c0 = ivec2(id % vat_width, int(INSTANCE_CUSTOM.r) * rows_per_frame + id / vat_width);
    ivec2 c1 = ivec2(id % vat_width, int(INSTANCE_CUSTOM.g) * rows_per_frame + id / vat_width);
    VERTEX = mix(texelFetch(vat_pos, c0, 0).xyz, texelFetch(vat_pos, c1, 0).xyz, INSTANCE_CUSTOM.b);
    VERTEX.z -= INSTANCE_CUSTOM.a;
    NORMAL = normalize(mix(texelFetch(vat_nrm, c0, 0).xyz, texelFetch(vat_nrm, c1, 0).xyz, INSTANCE_CUSTOM.b) * 2.0 - 1.0);
}

void fragment() {
    vec4 c = texture(albedo_tex, UV) * albedo_color;
    if (alpha_cut > 0.0 && c.a < alpha_cut) discard;
    ALBEDO = c.rgb;
    ROUGHNESS = roughness_value;
    SPECULAR = 0.3;
}
";

    /// <summary>Makes the textures, the far mesh and its materials of a bake (main thread, once).</summary>
    public static void Upload(Baked b)
    {
        if (b.Mesh is not null) return;
        _shader ??= new Shader { Code = ShaderCode };
        var pos = ImageTexture.CreateFromImage(Image.CreateFromData(Width, b.Height, false, Image.Format.Rgbah, b.Positions));
        var nrm = ImageTexture.CreateFromImage(Image.CreateFromData(Width, b.Height, false, Image.Format.Rgba8, b.Normals));
        var mesh = new ArrayMesh();
        for (int s = 0; s < b.Surfaces.Length; s++)
        {
            var sd = b.Surfaces[s];
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = sd.Positions;
            arrays[(int)Mesh.ArrayType.Normal] = sd.Normals;
            arrays[(int)Mesh.ArrayType.TexUV] = sd.Uv;
            arrays[(int)Mesh.ArrayType.Index] = sd.Indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var m = new ShaderMaterial { Shader = _shader };
            m.SetShaderParameter("vat_pos", pos);
            m.SetShaderParameter("vat_nrm", nrm);
            m.SetShaderParameter("vat_width", Width);
            m.SetShaderParameter("rows_per_frame", b.RowsPerFrame);
            m.SetShaderParameter("vertex_offset", b.SurfaceOffsets[s]);
            if (sd.Material is BaseMaterial3D std)
            {
                if (std.AlbedoTexture is { } tex) m.SetShaderParameter("albedo_tex", tex);
                m.SetShaderParameter("albedo_color", std.AlbedoColor);
                m.SetShaderParameter("roughness_value", std.Roughness);
                if (std.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor) m.SetShaderParameter("alpha_cut", std.AlphaScissorThreshold);
            }
            else if (sd.Material is ShaderMaterial sm)
            {
                // The map's two-layer material (albedo1), or the armour's paint (albedo_tex tinted by the primary colour: a provisional approximation of the paint mask).
                if ((sm.GetShaderParameter("albedo_tex").Obj ?? sm.GetShaderParameter("albedo1").Obj) is Texture2D t) m.SetShaderParameter("albedo_tex", t);
                if (sm.GetShaderParameter("primary").VariantType == Variant.Type.Color) m.SetShaderParameter("albedo_color", sm.GetShaderParameter("primary").AsColor());
            }
            mesh.SurfaceSetMaterial(s, m);
        }
        // The vertices move far from the bind pose (a body lying down): a generous box, so no instance is culled while on screen.
        mesh.CustomAabb = new Aabb(new Vector3(-2.5f, -2.5f, -1.5f), new Vector3(5f, 5f, 4.5f));
        b.Mesh = mesh;
        b.Positions = []; b.Normals = [];   // uploaded
    }
}
