using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// classic-maps-fidelity fix 1: the map's static level decals (<see cref="PlacedDecal"/>: painted numbers and stripes, stains, seams, light spots, running lights, trim) drawn as projected
/// Godot <see cref="Decal"/> boxes, one per placement. Each box is the node's rotation and translation with the layer's scale as its size, the projection axis the node's own Y (the thin axis of
/// the data's box); textures come from the decal's <c>ssdecal</c> entry (diffuse, normal and emissive maps; the specular map is not used), tinted by the object's colour and opacity overrides.
/// <para>
/// Cost: a map holds up to ~830 of them, more than the renderer clusters at once, so only the nearest <see cref="MaxActive"/> within <see cref="Range"/> metres of the camera are visible (re-chosen
/// <see cref="TickSeconds"/> apart); each fades out over its last <see cref="FadeLength"/> metres (ours: the data has no fade distance). Decals project only on render layer 1, so characters
/// (layer 2, <c>NetworkView.SetUpDecalLayers</c>) are not painted.
/// </para>
/// </summary>
public sealed partial class MapDecalView : Node3D
{
    /// <summary>The most decals visible at once (ours; the renderer's cluster limit is shared with the lights).</summary>
    public const int MaxActive = 160;
    /// <summary>Decals further than this from the camera are not drawn (ours).</summary>
    public const float Range = 70f;
    /// <summary>The distance fade at the end of the range (ours).</summary>
    public const float FadeLength = 20f;
    const double TickSeconds = 0.25;

    sealed class Item
    {
        public required Decal Node;
        public required Vector3 Position;
        public float Radius;
        public bool Shown;
    }

    readonly List<Item> _items = new();
    readonly Dictionary<string, ImageTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    double _clock;

    /// <summary>Decals built.</summary>
    public int Count => _items.Count;
    /// <summary>Decals visible now.</summary>
    public int Active => _items.Count(i => i.Shown);

    /// <summary>Builds the node of <paramref name="scene"/>'s level decals; null when it has none that can be drawn. Problems go to <paramref name="notes"/>.</summary>
    public static MapDecalView? Build(MapScene scene, List<string> notes)
    {
        if (scene.Decals.Count == 0) return null;
        var view = new MapDecalView { Name = "MapDecals" };
        int skipped = 0;
        foreach (var d in scene.Decals)
        {
            if (!scene.DecalDefinitions.TryGetValue(d.Decal, out var def)) { skipped++; continue; }
            var albedo = view.Texture(def.DiffuseMap, scene);
            var emission = def.EmissiveMap.Length > 0 ? view.Texture(def.EmissiveMap, scene) : null;
            if (albedo is null && emission is null) { skipped++; continue; }
            var tint = d.Colour ?? def.Colour;
            float opacity = d.Opacity ?? def.Opacity;
            var node = new Decal
            {
                Size = new Vector3(d.Size.X, d.Size.Y, d.Size.Z),
                CullMask = 1u,
                NormalFade = Math.Clamp(1f - def.NormalThreshold / 90f, 0f, 1f),   // interpretation: the data's NormalThreshold read as the largest surface angle (degrees) the decal shows on
                DistanceFadeEnabled = true, DistanceFadeBegin = Range - FadeLength, DistanceFadeLength = FadeLength,
                Modulate = new Color(tint.X, tint.Y, tint.Z, Math.Clamp(opacity, 0f, 1f)),
                Visible = false,
                Transform = ToGodot(d.Transform),
            };
            if (def.AdditiveBlend)
            {
                // A Godot decal cannot blend additively: the diffuse drawn as emission only (interpretation).
                node.AlbedoMix = 0f;
                node.TextureEmission = emission ?? albedo;
                node.EmissionEnergy = 1f;
            }
            else
            {
                node.TextureAlbedo = albedo;
                if (def.NormalMap.Length > 0) node.TextureNormal = view.Texture(def.NormalMap, scene);
                if (emission is not null)
                {
                    node.TextureEmission = emission;
                    var e = d.EmissiveColour ?? def.EmissiveColour;
                    node.EmissionEnergy = Math.Max(e.X, Math.Max(e.Y, e.Z));
                }
            }
            view.AddChild(node);
            view._items.Add(new Item { Node = node, Position = node.Position, Radius = 0.5f * Math.Max(d.Size.X, Math.Max(d.Size.Y, d.Size.Z)) });
        }
        if (skipped > 0) notes.Add($"{skipped} level decals have no definition or texture and are not drawn");
        if (view._items.Count == 0) { view.Free(); return null; }
        notes.Add($"{view._items.Count} level decals (the nearest {MaxActive} within {Range:0} m are drawn at a time; provisional approximation)");
        return view;
    }

    ImageTexture? Texture(string name, MapScene scene)
    {
        if (name.Length == 0) return null;
        if (_textures.TryGetValue(name, out var cached)) return cached;
        ImageTexture? texture = null;
        if (scene.Textures.TryGetValue(name, out var bytes))
        {
            var image = new Image();
            if (image.LoadDdsFromBuffer(bytes) == Error.Ok)
            {
                if (!image.HasMipmaps()) image.GenerateMipmaps();
                texture = ImageTexture.CreateFromImage(image);
            }
        }
        _textures[name] = texture;
        return texture;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_clock < TickSeconds) return;
        _clock = 0;
        Refresh();
    }

    /// <summary>Shows the nearest decals of the camera's range, hides the others.</summary>
    public void Refresh()
    {
        var camera = GetViewport()?.GetCamera3D();
        if (camera is null) return;
        var at = ToLocal(camera.GlobalPosition);
        var near = new List<(float Distance, Item Item)>();
        foreach (var item in _items)
        {
            float d = item.Position.DistanceTo(at) - item.Radius;
            if (d < Range) near.Add((d, item));
        }
        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var show = new HashSet<Item>(near.Take(MaxActive).Select(n => n.Item));
        foreach (var item in _items)
        {
            bool on = show.Contains(item);
            if (on != item.Shown) { item.Shown = on; item.Node.Visible = on; }
        }
    }

    /// <summary>System.Numerics matrices are row-vector: rows 1-3 are the images of the axes, row 4 the translation.</summary>
    static Transform3D ToGodot(Num.Matrix4x4 m) => new(
        new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
        new Vector3(m.M41, m.M42, m.M43));
}
