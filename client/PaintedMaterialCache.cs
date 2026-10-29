using System;
using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// Painted armour materials by key (<c>SmContent.Customization.PaintedMaterialKey</c>: surface, colours, decal, lens). Two surfaces with the same
/// key share one <see cref="ShaderMaterial"/>, so players in the same look cost nothing extra on the GPU; the textures underneath are shared by every
/// material of a surface (<see cref="MapMaterials.PaintedSurface"/>). Bounded: a peer's lens colour is any 24-bit value, so past <see cref="MaxMaterials"/> the
/// dictionary is cleared (characters keep the materials they show; the next look builds its own again).
/// </summary>
public sealed class PaintedMaterialCache
{
    /// <summary>The most materials kept (ours): 16 players of 17 to 20 surfaces each fit several times over.</summary>
    public const int MaxMaterials = 512;

    readonly Dictionary<string, Material> _materials = new(StringComparer.Ordinal);

    /// <summary>Materials held now.</summary>
    public int Count => _materials.Count;
    /// <summary>Lookups that found an existing material.</summary>
    public int Hits { get; private set; }
    /// <summary>Times the cache was cleared by reaching <see cref="MaxMaterials"/>.</summary>
    public int Overflows { get; private set; }

    public Material Get(string key, Func<Material> make)
    {
        if (_materials.TryGetValue(key, out var found)) { Hits++; return found; }
        if (_materials.Count >= MaxMaterials) { _materials.Clear(); Overflows++; }
        return _materials[key] = make();
    }

    public void Clear() { _materials.Clear(); Hits = 0; }
}
