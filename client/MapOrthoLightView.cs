using System;
using System.Collections.Generic;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// classic-maps-fidelity round 2, fix 1: the map's <c>base/OrthoLight</c> projectors (window and grate light patterns, lava glow; Basilica 8, Waste Management 11, Hab Center 14, Tertius 8, Aquila 5, Habs Ablaze 2)
/// drawn as Godot <see cref="Decal"/> boxes that add light only (emission): the box is the light's Width by Height across and its AttenEnd deep, starting at the light and running along its forward
/// direction, the mask picture (<c>light_mask_*</c>, a texture of the region archive) tinted by the light's colour. **Interpretation** (low-medium): the engine's orthographic light is a real projected light that
/// also lights characters and moving things; ours paints only the level (render layer 1) and adds, it does not shade. The energy (an eighth of Power, 0.05 to 1.5) and the fade along the depth are ours.
/// At most 14 per map, so no distance culling beyond the engine's own decal fade.
/// </summary>
public sealed partial class MapOrthoLightView : Node3D
{
    /// <summary>Lights further than this fade out (ours).</summary>
    public const float Range = 90f;

    readonly Dictionary<string, ImageTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ortho lights built.</summary>
    public int Count { get; private set; }

    /// <summary>Builds the node of <paramref name="scene"/>'s orthographic lights; null when it has none that can be drawn.</summary>
    public static MapOrthoLightView? Build(MapScene scene, List<string> notes)
    {
        var lights = scene.Lighting?.OrthoLights;
        if (lights is null || lights.Count == 0) return null;
        var view = new MapOrthoLightView { Name = "MapOrthoLights" };
        int skipped = 0;
        foreach (var l in lights)
        {
            var texture = view.Texture(l, scene);
            if (texture is null) { skipped++; continue; }
            var forward = new Vector3(l.Forward.X, l.Forward.Y, l.Forward.Z);
            var x = new Vector3(l.Right.X, l.Right.Y, l.Right.Z);
            var y = -forward;                // a Godot decal projects along its local -Y
            var basis = new Basis(x, y, x.Cross(y));
            var node = new Decal
            {
                Size = new Vector3(l.Width, l.Depth, l.Height),
                CullMask = 1u,
                AlbedoMix = 0f,
                TextureEmission = texture,
                EmissionEnergy = Math.Clamp(l.Power * 0.125f, 0.05f, 1.5f),
                UpperFade = 2f, LowerFade = 5f,   // soft top edge and a strong fade along the depth (ours)
                DistanceFadeEnabled = true, DistanceFadeBegin = Range - 20f, DistanceFadeLength = 20f,
                Transform = new Transform3D(basis, new Vector3(l.Position.X, l.Position.Y, l.Position.Z) + forward * (l.Depth * 0.5f)),
            };
            view.AddChild(node);
            view.Count++;
        }
        if (skipped > 0) notes.Add($"{skipped} orthographic lights have no mask texture and are not drawn");
        if (view.Count == 0) { view.Free(); return null; }
        notes.Add($"{view.Count} orthographic lights drawn as emissive projected boxes (provisional approximation)");
        return view;
    }

    /// <summary>The mask tinted by the light's colour (cached per mask and colour): the mask's red channel is the intensity.</summary>
    ImageTexture? Texture(OrthoLight l, MapScene scene)
    {
        string key = $"{l.MaskName}|{l.Colour.X:0.###},{l.Colour.Y:0.###},{l.Colour.Z:0.###}";
        if (_textures.TryGetValue(key, out var cached)) return cached;
        ImageTexture? texture = null;
        if (scene.Textures.TryGetValue(l.MaskName, out var bytes))
        {
            var image = new Image();
            if (image.LoadDdsFromBuffer(bytes) == Error.Ok)
            {
                if (image.IsCompressed()) image.Decompress();
                image.ClearMipmaps();
                image.Convert(Image.Format.Rgba8);
                var data = image.GetData();
                float r = Math.Clamp(l.Colour.X, 0f, 1f), g = Math.Clamp(l.Colour.Y, 0f, 1f), b = Math.Clamp(l.Colour.Z, 0f, 1f);
                for (int i = 0; i + 3 < data.Length; i += 4)
                {
                    float v = data[i] / 255f;
                    data[i] = (byte)(v * r * 255f); data[i + 1] = (byte)(v * g * 255f); data[i + 2] = (byte)(v * b * 255f); data[i + 3] = 255;
                }
                var tinted = Image.CreateFromData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
                tinted.GenerateMipmaps();
                texture = ImageTexture.CreateFromImage(tinted);
            }
        }
        _textures[key] = texture;
        return texture;
    }
}
