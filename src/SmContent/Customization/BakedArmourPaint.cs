using System.Collections.Concurrent;
using SmFormats.Bod;
using SmFormats.Dds;

namespace SmContent.Customization;

/// <summary>
/// Makes the baked armour of the predefined sets paintable (armour-pieces-1125, ours): for each material of such a piece that has no paint mask, a mask
/// and a grey "paint diffuse" are built at load time by <see cref="PaintMaskBuilder"/> from the piece's own diffuse, specular and light property maps, and
/// named in the material as <c>PaintMask</c> and <see cref="PaintDiffuseKey"/>. The material's own <c>DiffuseMap</c> stays, so the unpainted (baked) look is
/// unchanged: a renderer draws a built mask only when the piece is worn on its own, not as part of its full predefined set. Nothing is written to disk.
/// </summary>
public static class BakedArmourPaint
{
    /// <summary>The material texture key of the grey diffuse a built mask is painted over (the painted shader uses it instead of <c>DiffuseMap</c>).</summary>
    public const string PaintDiffuseKey = "PaintDiffuse";
    const string MaskSuffix = "__built_pnt", GreySuffix = "__built_grey";

    static readonly ConcurrentDictionary<string, byte> Packages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers the visual packages whose baked materials get built masks (the catalogue does this for every predefined-set piece it offers).</summary>
    public static void Register(IEnumerable<string> packages) { foreach (var p in packages) Packages.TryAdd(p, 0); }

    /// <summary>Whether <paramref name="package"/> is registered (<see cref="Register"/>).</summary>
    public static bool Wanted(string package) => Packages.ContainsKey(package);

    /// <summary>Whether <paramref name="material"/> is drawn with a built mask (it names <see cref="PaintDiffuseKey"/>).</summary>
    public static bool IsBuilt(BmatMaterial? material) => material?.Textures.ContainsKey(PaintDiffuseKey) == true;

    /// <summary>
    /// Whether a material needs a built mask: it has no usable paint mask (none, or the literal "None" two DLC pads name) and is armour plate, i.e. normal-mapped
    /// (cloth, fur and tabards use the <c>diff_spec_lp</c> shaders without a normal map and keep their baked look: judgement call, docs/changes-from-original.md).
    /// </summary>
    public static bool NeedsMask(BmatMaterial material) =>
        (!material.Textures.TryGetValue("PaintMask", out var mask) || mask.Length == 0 || mask.Equals("None", StringComparison.OrdinalIgnoreCase))
        && material.Textures.ContainsKey("DiffuseMap")
        && (material.Textures.ContainsKey("NormalMap") || material.ShaderName?.Contains("norm", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(material.ShaderName, "character_wargear", StringComparison.OrdinalIgnoreCase));

    /// <summary>The model with built masks for every material that <see cref="NeedsMask"/> and whose diffuse decodes; unchanged when there is none.</summary>
    public static StaticModel Apply(StaticModel model, List<string>? notes = null)
    {
        var added = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var built = new Dictionary<string, BmatMaterial?>(StringComparer.OrdinalIgnoreCase);
        var surfaces = new List<StaticModelSurface>(model.Surfaces.Count);
        bool changed = false;
        foreach (var s in model.Surfaces)
        {
            if (s.Material is not { } m || !NeedsMask(m)) { surfaces.Add(s); continue; }
            string key = m.Name ?? m.Textures["DiffuseMap"];
            if (!built.TryGetValue(key, out var replaced))
            {
                replaced = Build(m, model.Textures, added, notes);
                built[key] = replaced;
            }
            if (replaced is null) { surfaces.Add(s); continue; }
            surfaces.Add(s with { Material = replaced });
            changed = true;
        }
        return changed ? model.WithSurfaces(surfaces, added) : model;
    }

    static BmatMaterial? Build(BmatMaterial m, IReadOnlyDictionary<string, byte[]> textures, Dictionary<string, byte[]> added, List<string>? notes)
    {
        string diffuseName = m.Textures["DiffuseMap"];
        if (!textures.TryGetValue(diffuseName, out var difBytes) || DdsImage.TryDecode(difBytes) is not { } diffuse)
        {
            notes?.Add($"material '{m.Name}': diffuse '{diffuseName}' missing or undecodable; no paint mask built (drawn as baked)");
            return null;
        }
        DdsImage? Opt(string param) => m.Textures.TryGetValue(param, out var n) && textures.TryGetValue(n, out var b) ? DdsImage.TryDecode(b) : null;
        var result = PaintMaskBuilder.Build(diffuse, Opt("SpecularMap"), Opt("LightPropMap"));
        string maskName = diffuseName + MaskSuffix, greyName = diffuseName + GreySuffix;
        added[maskName] = DdsImage.EncodeRgba8WithMips(result.Width, result.Height, result.Mask);
        added[greyName] = DdsImage.EncodeRgba8WithMips(result.Width, result.Height, result.Diffuse);
        var tex = new Dictionary<string, string>(m.Textures, StringComparer.Ordinal) { ["PaintMask"] = maskName, [PaintDiffuseKey] = greyName };
        return m with { Textures = tex };
    }
}
