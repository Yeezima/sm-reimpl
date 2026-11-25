using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;

namespace SmClient;

/// <summary>
/// The one builder of a Mapmaker material's surface (added content, docs/mapmaker.md "Materials (editor)"): a resolved <see cref="PbrMaterial"/> plus its pictures become a Godot
/// <see cref="StandardMaterial3D"/> (albedo, metallic / roughness / AO textures with their channel, a normal map with its strength and the DirectX green flip, <c>uv1</c> scale and
/// offset, mipmaps), and a piece's <see cref="RenderMesh"/> becomes the mesh it is drawn on (normals, tangents, UVs in metres per face). The Mapmaker editor (linked in as a
/// read-only source, like the client's other views) and the game's custom-map view both call this file, so the editor preview, the saved map and the surface in game are built by
/// the same code from the same description. The pure decisions (channels, flip, file-name wiring) are <see cref="MaterialPlan"/>.
/// </summary>
public static class MaterialBuild
{
    /// <summary>Decodes a map image's verified bytes (PNG or JPEG) into an RGBA8 picture; null when Godot cannot read it.</summary>
    public static Image? Decode(byte[] bytes, string format)
    {
        var image = new Image();
        var err = format == "jpg" ? image.LoadJpgFromBuffer(bytes) : image.LoadPngFromBuffer(bytes);
        if (err != Error.Ok || image.IsEmpty()) return null;
        if (image.IsCompressed()) image.Decompress();
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        return image;
    }

    /// <summary>Inverts the green channel (DirectX normal map to the OpenGL convention Godot expects). Shared with the model import (<c>AssetImporter</c>).</summary>
    public static void FlipGreen(Image image)
    {
        image.Convert(Image.Format.Rgba8);
        var data = image.GetData();
        MaterialPlan.FlipGreenRgba8(data);
        image.SetData(image.GetWidth(), image.GetHeight(), image.HasMipmaps(), Image.Format.Rgba8, data);
    }

    /// <summary>Puts <paramref name="texture"/> into one feature of <paramref name="mat"/>, reading <paramref name="channel"/> (shared with the model import, so both read channels the same way).</summary>
    public static void Bind(StandardMaterial3D mat, MaterialPlan.Feature feature, Texture2D texture, TextureChannel channel)
    {
        var ch = (BaseMaterial3D.TextureChannel)MaterialPlan.GodotChannel(channel);
        switch (feature)
        {
            case MaterialPlan.Feature.Albedo: mat.AlbedoTexture = texture; break;
            case MaterialPlan.Feature.Metallic: mat.MetallicTexture = texture; mat.MetallicTextureChannel = ch; break;
            case MaterialPlan.Feature.Roughness: mat.RoughnessTexture = texture; mat.RoughnessTextureChannel = ch; break;
            case MaterialPlan.Feature.Ao: mat.AOEnabled = true; mat.AOTexture = texture; mat.AOTextureChannel = ch; break;
            case MaterialPlan.Feature.Normal: mat.NormalEnabled = true; mat.NormalTexture = texture; break;
            case MaterialPlan.Feature.Emission: mat.EmissionEnabled = true; mat.EmissionTexture = texture; break;
        }
    }

    /// <summary>
    /// The surface of <paramref name="m"/>. <paramref name="picture"/> returns the decoded picture of an image id (null: missing, that input then uses its constant and a note is
    /// added). Textures get mipmaps and anisotropic filtering and repeat, so a wall shows its texture tiled at the mapping's repeats per metre.
    /// </summary>
    public static StandardMaterial3D Build(PbrMaterial m, Func<string, bool, Texture2D?> texture, List<string>? notes = null)
    {
        var plan = MaterialPlan.Of(m);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(plan.Albedo.X, plan.Albedo.Y, plan.Albedo.Z), Metallic = plan.Metallic, Roughness = plan.Roughness,
            Uv1Scale = new Vector3(plan.UvScale.X, plan.UvScale.Y, 1f), Uv1Offset = new Vector3(plan.UvOffset.X, plan.UvOffset.Y, 0f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic, TextureRepeat = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, NormalScale = plan.NormalStrength,
        };
        foreach (var b in plan.Textures)
        {
            if (texture(b.ImageId, b.FlipGreen) is { } tex) Bind(mat, b.Feature, tex, b.Channel);
            else
            {
                notes?.Add($"image {b.ImageId[..Math.Min(12, b.ImageId.Length)]}... is missing: {b.Feature} uses its value");
                // A missing picture: the scalar the input would use without it (Resolve set it to 1 because a texture was linked).
                if (b.Feature == MaterialPlan.Feature.Albedo) mat.AlbedoColor = new Color(PbrMaterial.DefaultBaseColor.X, PbrMaterial.DefaultBaseColor.Y, PbrMaterial.DefaultBaseColor.Z);
                if (b.Feature == MaterialPlan.Feature.Roughness) mat.Roughness = 0.5f;
                if (b.Feature == MaterialPlan.Feature.Metallic) mat.Metallic = 0f;
            }
        }
        if (plan.ConstantAo)
        {
            // Godot applies ambient occlusion only from a texture: a constant below 1 is a 1 x 1 picture of that value (the same effect on indirect light).
            var one = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            one.Fill(new Color(plan.Ao, plan.Ao, plan.Ao));
            Bind(mat, MaterialPlan.Feature.Ao, ImageTexture.CreateFromImage(one), TextureChannel.R);
        }
        return mat;
    }

    /// <summary>A texture from a decoded picture: a copy with the green channel flipped when asked, mipmaps generated.</summary>
    public static ImageTexture Texture(Image picture, bool flipGreen)
    {
        using var image = (Image)picture.Duplicate();   // the texture keeps its own copy of the pixels: the working copy's handle is released at once
        if (flipGreen) FlipGreen(image);
        if (!image.HasMipmaps()) image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// A piece's drawn mesh from its <see cref="RenderMesh"/> (document frame, Z up, counter-clockwise from outside): two corners of each triangle are swapped for Godot's clockwise
    /// front faces (as the client's custom-map view does), normals, tangents (w = handedness) and UVs are carried per corner.
    /// </summary>
    public static ArrayMesh Mesh(RenderMesh rm)
    {
        int n = rm.Positions.Length;
        var v = new Vector3[n]; var nor = new Vector3[n]; var uv = new Vector2[n]; var tan = new float[n * 4];
        for (int t = 0; t + 2 < n; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int src = t + (k == 1 ? 2 : k == 2 ? 1 : 0), dst = t + k;
                var p = rm.Positions[src]; var q = rm.Normals[src]; var w = rm.Tangents[src]; var u = rm.Uvs[src];
                v[dst] = new Vector3(p.X, p.Y, p.Z); nor[dst] = new Vector3(q.X, q.Y, q.Z); uv[dst] = new Vector2(u.X, u.Y);
                tan[dst * 4] = w.X; tan[dst * 4 + 1] = w.Y; tan[dst * 4 + 2] = w.Z; tan[dst * 4 + 3] = TangentSign * w.W;
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = v;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = nor;
        arrays[(int)Godot.Mesh.ArrayType.Tangent] = tan;
        arrays[(int)Godot.Mesh.ArrayType.TexUV] = uv;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// The sign that turns <see cref="RenderMesh"/>'s handedness (w: the bitangent is +dP/dv) into Godot's (Godot's binormal is cross(normal, tangent) x w and points toward -v, the
    /// picture's up, for an OpenGL normal map). Checked against Godot's own MikkTSpace tangents by the editor selftest (<c>--mm-materials</c>, "tangents agree with Godot's").
    /// </summary>
    public const float TangentSign = -1f;

    /// <summary>
    /// The surfaces of one map: every material resolved and built once, every picture decoded once (by image id and flip), read from beside the map file first, then the library
    /// (<see cref="MapImages.Read"/>: only verified copies). <see cref="For"/> returns null for an object with no material (the caller's default look).
    /// </summary>
    /// <remarks>
    /// <see cref="Dispose"/> releases the C# handles of everything it built (the engine keeps what a mesh still uses). Callers that live until the engine quits (the editor's 3D view)
    /// dispose it on the way out, so no handle is left for Godot .NET's shutdown sweep (an editor test once crashed at exit there: 0xC0000005 in
    /// <c>DisposablesTracker.OnGodotShuttingDown</c>, M4 verification 2026-11-25; our reading: the sweep and the finalizer thread releasing the same handle).
    /// <para><paramref name="reuse"/> (a cache this one replaces): its decoded pictures and their textures for the images <paramref name="doc"/> still lists are taken over, not decoded
    /// again. An image id is the SHA-256 of its bytes, so the same id is the same picture wherever it is read from. The caller still disposes <paramref name="reuse"/>, which then releases
    /// only what was not taken over.</para>
    /// </remarks>
    public sealed class Cache(MapDocument doc, string? mapPath, MapLibrary library, Cache? reuse = null) : IDisposable
    {
        public void Dispose()
        {
            foreach (var m in _built.Values) m?.Dispose();
            foreach (var t in _textures.Values) t?.Dispose();
            foreach (var p in _pictures.Values) p?.Dispose();
            _built.Clear(); _textures.Clear(); _pictures.Clear();
        }

        readonly Dictionary<string, StandardMaterial3D?> _built = new();
        readonly Dictionary<(string, bool), Texture2D?> _textures = reuse?.HandOverTextures(doc) ?? new();
        readonly Dictionary<string, Image?> _pictures = reuse?.HandOverPictures(doc) ?? new();
        public List<string> Notes { get; } = new();

        /// <summary>Moves the decoded pictures of the images <paramref name="next"/> lists out of this cache (a missing one is not carried: the next cache looks again).</summary>
        Dictionary<string, Image?> HandOverPictures(MapDocument next)
        {
            var keep = new Dictionary<string, Image?>();
            foreach (var img in next.Images)
                if (_pictures.Remove(img.Id, out var p) && p is not null) keep[img.Id] = p;
            return keep;
        }

        /// <summary>Moves the textures made from the images <paramref name="next"/> lists out of this cache (keyed by image id and green flip, so content-addressed too).</summary>
        Dictionary<(string, bool), Texture2D?> HandOverTextures(MapDocument next)
        {
            var ids = next.Images.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var keep = new Dictionary<(string, bool), Texture2D?>();
            foreach (var key in _textures.Keys.Where(k => ids.Contains(k.Item1)).ToList())
                if (_textures.Remove(key, out var t) && t is not null) keep[key] = t;
            return keep;
        }

        public StandardMaterial3D? For(string? materialId)
        {
            if (materialId is null) return null;
            if (_built.TryGetValue(materialId, out var have)) return have;
            StandardMaterial3D? mat = null;
            if (doc.FindMaterial(materialId) is { } g)
            {
                try { mat = Build(g.Resolve(), Texture, Notes); mat.ResourceName = g.Name; }
                catch (MapException e) { Notes.Add($"material {g.Name}: {e.Message}"); }
            }
            else Notes.Add($"material {materialId} is not in the map");
            _built[materialId] = mat;
            return mat;
        }

        Texture2D? Texture(string imageId, bool flip)
        {
            if (_textures.TryGetValue((imageId, flip), out var t)) return t;
            if (!_pictures.TryGetValue(imageId, out var pic))
            {
                pic = null;
                if (doc.Images.FirstOrDefault(i => i.Id == imageId) is { } img && MapImages.Read(img, mapPath, library) is { } bytes) pic = Decode(bytes, img.Format);
                _pictures[imageId] = pic;
            }
            t = pic is null ? null : MaterialBuild.Texture(pic, flip);
            _textures[(imageId, flip)] = t;
            return t;
        }
    }
}
