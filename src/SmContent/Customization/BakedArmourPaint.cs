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

    /// <summary>
    /// Developer materials that name none of their package's textures (armour-pieces-1125 Series A, ours): the co-op elite Khorne Berserker's helmet
    /// (<c>csm_armor_berserk_001_helmet</c>: shader "pink", the engine's missing-material placeholder, no parameters; its package holds one diffuse, misnamed
    /// <c>csm_armour_mp_basic_helmet_dif</c>, that the helmet's UVs fit: 86 % of its lit texels are covered and under 10 % of the helmet's texels fall on its black
    /// background, against 18 to 32 % on every MP helmet's own diffuse) and backpack (<c>csm_armor_berserk_001_backpack</c>: <c>character_wargear</c> without
    /// parameters; its packages hold a flat grey diffuse, a flat normal map and a 64 x 64 paint mask that is all primary but all dirt in alpha, which draws the
    /// pack in the dirt colour). Both become <c>character_wargear</c> materials on their package's diffuse (and the backpack's flat normal map); a mask is then
    /// built (the backpack's comes out all primary, dirt 0). Key: package name.
    /// </summary>
    static readonly Dictionary<string, Dictionary<string, string>> DeveloperMaterials = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csm_armor_berserk_001_helmet"] = new(StringComparer.Ordinal) { ["DiffuseMap"] = "csm_armour_mp_basic_helmet_dif" },
        ["csm_armor_berserk_001_backpack"] = new(StringComparer.Ordinal)
        {
            ["DiffuseMap"] = "gray_dif", ["NormalMap"] = "blank_nrm",
        },
    };

    /// <summary>The material rewrite for <paramref name="package"/> (<see cref="DeveloperMaterials"/>), or null when it needs none.</summary>
    public static Func<BmatMaterial, BmatMaterial>? FixMaterial(string package) =>
        DeveloperMaterials.TryGetValue(package, out var textures) ? m => Fix(m, textures) : null;

    /// <summary>A developer material (one with no diffuse) given <paramref name="textures"/> as a <c>character_wargear</c> material; any other unchanged.</summary>
    public static BmatMaterial Fix(BmatMaterial m, IReadOnlyDictionary<string, string> textures)
    {
        if (m.Textures.ContainsKey("DiffuseMap")) return m;
        var tex = new Dictionary<string, string>(m.Textures, StringComparer.Ordinal);
        foreach (var (k, v) in textures) tex[k] = v;
        return m with { ShaderName = "character_wargear", Textures = tex };
    }

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
                var users = model.Surfaces.Where(o => o.Material is { } om && (om.Name ?? om.Textures.GetValueOrDefault("DiffuseMap")) == key).ToList();
                replaced = Build(m, model.Textures, added, notes, (w, h) => Coverage(model, users, w, h));
                built[key] = replaced;
            }
            if (replaced is null) { surfaces.Add(s); continue; }
            surfaces.Add(s with { Material = replaced });
            changed = true;
        }
        return changed ? model.WithSurfaces(surfaces, added) : model;
    }

    /// <summary>
    /// The texels the surfaces' UV triangles cover (UVs wrapped into 0..1, V down as the textures are stored), grown by two texels so filtering at seams reads
    /// painted texels; null when nothing is covered (then every texel counts).
    /// </summary>
    public static bool[]? Coverage(StaticModel model, IEnumerable<StaticModelSurface> surfaces, int w, int h)
    {
        var used = new bool[w * h];
        bool any = false;
        foreach (var s in surfaces)
        {
            var block = model.Mesh.Blocks[s.BlockIndex];
            if (block.Uv0.Length == 0) continue;
            int end = Math.Min(block.Indices.Length, s.Range.StartIndex + s.Range.IndexCount);
            for (int k = s.Range.StartIndex; k + 2 < end; k += 3)
            {
                var a = block.Uv0[block.Indices[k]]; var b = block.Uv0[block.Indices[k + 1]]; var c = block.Uv0[block.Indices[k + 2]];
                // Wrap the triangle as a whole by its first corner's tile so tiled UVs land in the texture.
                float ox = MathF.Floor(a.X), oy = MathF.Floor(a.Y);
                any |= Raster(used, w, h, (a.X - ox) * w, (a.Y - oy) * h, (b.X - ox) * w, (b.Y - oy) * h, (c.X - ox) * w, (c.Y - oy) * h);
            }
        }
        if (!any) return null;
        for (int pass = 0; pass < 2; pass++)
        {
            var grown = (bool[])used.Clone();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (!used[y * w + x] && ((x > 0 && used[y * w + x - 1]) || (x < w - 1 && used[y * w + x + 1]) || (y > 0 && used[(y - 1) * w + x]) || (y < h - 1 && used[(y + 1) * w + x])))
                    grown[y * w + x] = true;
            used = grown;
        }
        return used;
    }

    static bool Raster(bool[] used, int w, int h, float x0, float y0, float x1, float y1, float x2, float y2)
    {
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2)))), maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2)))), maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        bool any = false;
        if (MathF.Abs(area) < 1e-6f)
        {
            // Degenerate: mark the corners.
            foreach (var (px, py) in new[] { (x0, y0), (x1, y1), (x2, y2) })
                if ((int)px >= 0 && (int)px < w && (int)py >= 0 && (int)py < h) { used[(int)py * w + (int)px] = true; any = true; }
            return any;
        }
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float e0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area, e1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area, e2 = 1f - e0 - e1;
            if (e0 >= -0.02f && e1 >= -0.02f && e2 >= -0.02f) { used[y * w + x] = true; any = true; }
        }
        return any;
    }

    static BmatMaterial? Build(BmatMaterial m, IReadOnlyDictionary<string, byte[]> textures, Dictionary<string, byte[]> added, List<string>? notes, Func<int, int, bool[]?> coverage)
    {
        string diffuseName = m.Textures["DiffuseMap"];
        if (!textures.TryGetValue(diffuseName, out var difBytes) || DdsImage.TryDecode(difBytes) is not { } diffuse)
        {
            notes?.Add($"material '{m.Name}': diffuse '{diffuseName}' missing or undecodable; no paint mask built (drawn as baked)");
            return null;
        }
        DdsImage? Opt(string param) => m.Textures.TryGetValue(param, out var n) && textures.TryGetValue(n, out var b) ? DdsImage.TryDecode(b) : null;
        var result = PaintMaskBuilder.Build(diffuse, Opt("SpecularMap"), Opt("LightPropMap"), coverage(diffuse.Width, diffuse.Height));
        string maskName = diffuseName + MaskSuffix, greyName = diffuseName + GreySuffix;
        added[maskName] = DdsImage.EncodeRgba8WithMips(result.Width, result.Height, result.Mask);
        added[greyName] = DdsImage.EncodeRgba8WithMips(result.Width, result.Height, result.Diffuse);
        var tex = new Dictionary<string, string>(m.Textures, StringComparer.Ordinal) { ["PaintMask"] = maskName, [PaintDiffuseKey] = greyName };
        return m with { Textures = tex };
    }
}
