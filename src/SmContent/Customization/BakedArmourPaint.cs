using System.IO.Compression;
using System.Security.Cryptography;
using SmFormats.Bod;
using SmFormats.Dds;

namespace SmContent.Customization;

/// <summary>
/// Makes the baked armour of the predefined sets paintable (armour-pieces-1125, ours): for each material of such a piece that has no paint mask, a mask
/// and a grey "paint diffuse" are built at load time by <see cref="PaintMaskBuilder"/> from the piece's own diffuse, specular and light property maps, and
/// named in the material as <c>PaintMask</c> and <see cref="PaintDiffuseKey"/>. The material's own <c>DiffuseMap</c> stays, so the unpainted (baked) look is
/// unchanged: a renderer draws a built mask only when the piece is worn on its own, not as part of its full predefined set.
/// <para>
/// Which packages get masks is the caller's choice (<see cref="CharacterModel.LoadPiece"/>'s <c>buildPaintMasks</c>, from <see cref="ArmourPiece.BuiltMasks"/>
/// through the resolved look); nothing process-wide is registered. Load cost (the 92 % gate's finding 3): at most <see cref="MaxConcurrentBuilds"/> masks are
/// built at once whatever the number of packages read in parallel, and with <see cref="CacheDirectory"/> set (the client: the private cache folder, never
/// the repository or the install) each built mask is kept on disk, keyed by <see cref="BuilderVersion"/> and a hash of the very texture bytes and UV coverage it
/// was built from, so a changed install or builder builds it again.
/// </para>
/// </summary>
public static class BakedArmourPaint
{
    /// <summary>The material texture key of the grey diffuse a built mask is painted over (the painted shader uses it instead of <c>DiffuseMap</c>).</summary>
    public const string PaintDiffuseKey = "PaintDiffuse";
    const string MaskSuffix = "__built_pnt", GreySuffix = "__built_grey";

    /// <summary>The most masks built at the same time in the process (ours: a build holds about 100 MB of working arrays for a 1024 x 1024 texture).</summary>
    public const int MaxConcurrentBuilds = 2;
    static readonly SemaphoreSlim Gate = new(MaxConcurrentBuilds, MaxConcurrentBuilds);
    static int _running, _peak, _built, _hits, _failed;

    /// <summary>The most builds running at once since <see cref="ResetStatistics"/> (tests, <c>smtool built-mask-bench</c>).</summary>
    public static int PeakConcurrentBuilds => Volatile.Read(ref _peak);
    /// <summary>Masks built since <see cref="ResetStatistics"/>.</summary>
    public static int Built => Volatile.Read(ref _built);
    /// <summary>Masks read from the cache since <see cref="ResetStatistics"/>.</summary>
    public static int CacheHits => Volatile.Read(ref _hits);
    /// <summary>Materials whose build failed (drawn baked) since <see cref="ResetStatistics"/>.</summary>
    public static int Failed => Volatile.Read(ref _failed);
    public static void ResetStatistics() { _peak = 0; _built = 0; _hits = 0; _failed = 0; }

    /// <summary>
    /// The version of what <see cref="PaintMaskBuilder"/> makes: part of every cache key, so raising it (with any change to the builder's output) rebuilds every
    /// cached mask. 2: armour-pieces-1125 repair attempt 1; 3: repair attempt 2; 4: round 3; 5: round 4 (the built pieces' output changes: region statistics of
    /// the final mask and highlight ceilings, both applied after the cache; raised so a cache never mixes the rounds).
    /// </summary>
    public const int BuilderVersion = 5;

    /// <summary>
    /// Where built masks are kept between runs, or null (the default: nothing is written). The client sets it to its private cache folder
    /// (<c>built-masks</c> under the data root's cache). Past <see cref="CacheBudgetBytes"/> the oldest entries are removed.
    /// </summary>
    public static string? CacheDirectory { get; set; }
    /// <summary>The built-mask cache's size bound (ours).</summary>
    public const long CacheBudgetBytes = 256L * 1024 * 1024;

    /// <summary>Test aid: called inside the build gate (a test holds builds open there to check the bound).</summary>
    internal static Action? BuildHook { get; set; }

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
    /// Whether a surface carries the scheme's shoulder decal (finding 1 of the gate): it has the game's own paint mask (not a built one) and a decal layout on
    /// its second UV set (<see cref="PaintSchemes.HasDecalUv"/>). A pad with a built mask never takes the chapter decal: in the original its baked insignia is
    /// the pad's own, and no customizable decal is laid over it.
    /// </summary>
    public static bool SurfaceTakesDecal(BmatMaterial? material, IReadOnlyList<System.Numerics.Vector2> uv1) =>
        material?.Textures.ContainsKey("PaintMask") == true && !IsBuilt(material) && PaintSchemes.HasDecalUv(uv1);

    /// <summary>Whether any drawn surface of <paramref name="piece"/> takes the decal (<see cref="SurfaceTakesDecal"/>).</summary>
    public static bool PieceTakesDecal(CharacterPiece piece) =>
        piece.Model.Surfaces.Any(s => piece.Hidden?.Contains(s.BlockIndex) != true && SurfaceTakesDecal(s.Material, piece.Model.Mesh.Blocks[s.BlockIndex].Uv1));

    /// <summary>
    /// Whether a material needs a built mask: it has no usable paint mask (none, or the literal "None" two DLC pads name) and is armour plate, i.e. normal-mapped
    /// (cloth, fur and tabards use the <c>diff_spec_lp</c> shaders without a normal map and keep their baked look: judgement call, docs/changes-from-original.md).
    /// </summary>
    public static bool NeedsMask(BmatMaterial material) =>
        (!material.Textures.TryGetValue("PaintMask", out var mask) || mask.Length == 0 || mask.Equals("None", StringComparison.OrdinalIgnoreCase))
        && material.Textures.ContainsKey("DiffuseMap")
        && (PaintMaskBuilder.OldMaskRules || !IsCloth(material))
        && (material.Textures.ContainsKey("NormalMap") || material.ShaderName?.Contains("norm", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(material.ShaderName, "character_wargear", StringComparison.OrdinalIgnoreCase));

    static readonly string[] ClothWords = ["cloth", "cape", "cloak", "tabard", "fur", "scroll", "parchment", "purity", "robe", "pelt"];

    /// <summary>
    /// A normal-mapped material that is cloth, fur or parchment by its name (repair attempt 1: the Salamanders' drake-scale cloak, named "cloth", has a normal map
    /// and was painted as plate). Cloth keeps its baked look, as the regular armour's cloth (no paint mask) does.
    /// </summary>
    public static bool IsCloth(BmatMaterial material) =>
        material.Name is { } name && ClothWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The textures a built mask is made from: materials naming the same three share one mask (and one coverage: all their surfaces).</summary>
    readonly record struct Sources(string Diffuse, string? Specular, string? LightProp);

    static Sources SourcesOf(BmatMaterial m) => new(m.Textures["DiffuseMap"], m.Textures.GetValueOrDefault("SpecularMap"), m.Textures.GetValueOrDefault("LightPropMap"));

    /// <summary>
    /// The model with built masks for every material that <see cref="NeedsMask"/> and whose diffuse decodes; unchanged when there is none. Materials that name
    /// the same diffuse, specular and light property maps share one mask built over all their surfaces' UVs (finding 2 of the gate: built per material name but
    /// stored by diffuse name, a second material overwrote the first's mask with one that left the first's texels unpainted). A material whose build fails is
    /// drawn baked, with a note; the rest of the package is unaffected (finding 5).
    /// </summary>
    public static StaticModel Apply(StaticModel model, List<string>? notes = null, string? package = null)
    {
        int[] channels = ChannelMap(package);
        var added = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var built = new Dictionary<Sources, (string Mask, string Grey)?>();
        var surfaces = new List<StaticModelSurface>(model.Surfaces.Count);
        var placeholder = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var s in model.Surfaces)
        {
            if (s.Material is not { } m || !NeedsMask(m)) { surfaces.Add(s); continue; }
            var key = SourcesOf(m);
            if (!built.TryGetValue(key, out var names))
            {
                var users = model.Surfaces.Where(o => o.Material is { } om && NeedsMask(om) && SourcesOf(om) == key).ToList();
                // Two different source sets with one diffuse (other specular maps) get distinct names.
                string stem = key.Diffuse + (built.Keys.Any(k => k.Diffuse.Equals(key.Diffuse, StringComparison.OrdinalIgnoreCase)) ? "__" + built.Count : "");
                names = Build(m, key, stem, model.Textures, added, notes, (w, h) => Coverage(model, users, w, h), channels, package);
                built[key] = names;
            }
            if (names is not { } n) { surfaces.Add(s); continue; }
            var tex = new Dictionary<string, string>(m.Textures, StringComparer.Ordinal) { ["PaintMask"] = n.Mask, [PaintDiffuseKey] = n.Grey };
            if (!OldRound4Look && m.Textures.TryGetValue("NormalMap", out var normalName) && model.Textures.TryGetValue(normalName, out var normalBytes)
                && (placeholder.TryGetValue(normalName, out bool isPlaceholder) ? isPlaceholder : placeholder[normalName] = IsPlaceholderNormal(normalBytes)))
                tex[PaintFlatNormalKey] = "1";
            surfaces.Add(s with { Material = m with { Textures = tex } });
            changed = true;
        }
        return changed ? model.WithSurfaces(surfaces, added) : model;
    }

    /// <summary>
    /// Round 4 (ours, measured 2026-11-25 with <c>smtool built-mask-bench --mask-shares</c>): whether a material is painted with the specular strength of the regular
    /// masked armour, which names no <c>SpecularColor</c> (the client's default, <c>MapMaterials.SpecularFor</c>: 0.35 x SpecScale, which <c>PreparePainted</c> raises to at
    /// least 0.4 when the material has a specular map, as every regular and built armour material here has), instead of its own: true for every built material. The
    /// campaign Chaos materials name SpecularColor (1, 1, 1), which the client reads as the strongest specular (0.6, half again the regular 0.4): the campaign raptor
    /// and berserker legs read washed pale though their grey paint diffuse (raptor right leg median 72, 67 to 76) is no brighter than the regular leg's (72, 53 to 94)
    /// and their specular maps (median 63) match the regular leg's (68). The plain (baked) material keeps its own specular, so a full set draws as before. Off under
    /// <see cref="OldRegionStats"/>.
    /// </summary>
    public static bool UsesRegularSpecular(BmatMaterial m) => IsBuilt(m) && !OldRegionStats && !PaintMaskBuilder.OldMaskRules;

    /// <summary>The material texture key that marks a built material to be painted without its normal map (<see cref="PaintsWithoutNormalMap"/>).</summary>
    public const string PaintFlatNormalKey = "PaintFlatNormal";

    /// <summary>
    /// Round 5 (ours, measured 2026-11-26 with <c>smtool built-mask-bench --mask-shares</c>): whether a built material is painted without its normal map because that map
    /// is a placeholder (<see cref="IsPlaceholderNormal"/>). The campaign raptor pieces (<c>csm_armour_raptor_*</c>) and the berserker arms, legs, torso and backpack (<c>csm_armour_berserker_*</c>; the berserker helmet and pads have real maps) name
    /// a normal map that is one DXT1 colour throughout (sRGB 200, 200, 200 on every texel), which a renderer reads as a normal tilted about 55 degrees off the surface:
    /// lit from one side the whole piece drew washed pale (the round-4 gate's "right pad and right leg washed pale pink", "berserker shin and thigh washed pale and flat"),
    /// and the mirrored piece dark. The campaign basic pieces' normal maps are real (byte-identical to the MP basic ones). The plain (baked) material keeps its own normal map,
    /// so a full set draws as before. Off under <see cref="OldRound4Look"/>.
    /// </summary>
    public static bool PaintsWithoutNormalMap(BmatMaterial m) => IsBuilt(m) && m.Textures.ContainsKey(PaintFlatNormalKey) && !OldRound4Look && !OldRegionStats;

    /// <summary>
    /// A normal map that carries no surface detail and is not flat: its R and G vary by at most 6 levels over the texture while their mean stands more than 16 levels from
    /// 128 (a flat map, 128 / 128, is harmless and is kept). False when it does not decode.
    /// </summary>
    public static bool IsPlaceholderNormal(byte[] dds)
    {
        if (DdsImage.TryDecode(dds) is not { } img || img.Width * img.Height == 0) return false;
        int minR = 255, maxR = 0, minG = 255, maxG = 0; double sumR = 0, sumG = 0; int n = 0;
        for (int i = 0; i < img.Width * img.Height; i += 3)
        {
            int r = img.Rgba[i * 4], g = img.Rgba[i * 4 + 1];
            minR = Math.Min(minR, r); maxR = Math.Max(maxR, r); minG = Math.Min(minG, g); maxG = Math.Max(maxG, g); sumR += r; sumG += g; n++;
        }
        return maxR - minR <= 6 && maxG - minG <= 6 && (Math.Abs(sumR / n - 128) > 16 || Math.Abs(sumG / n - 128) > 16);
    }

    /// <summary>
    /// Restore switch (round 5): true paints built pieces as round 4 did in the general rules round 5 added (placeholder normal maps kept, <see cref="PaintsWithoutNormalMap"/>;
    /// no island pass, <see cref="BuiltMaskOverride.MinIsland"/>). Per-piece region data is not switched. A look only (client; <c>--old-round4-look</c> or SM_OLD_ROUND4_LOOK=1).
    /// </summary>
    public static bool OldRound4Look
    {
        get => _oldRound4Look ?? (Environment.GetEnvironmentVariable("SM_OLD_ROUND4_LOOK") == "1" || Array.IndexOf(Environment.GetCommandLineArgs(), "--old-round4-look") >= 0);
        set => _oldRound4Look = value;
    }
    static bool? _oldRound4Look;

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

    /// <summary>
    /// The paint channel that carries the plates of the regular (masked) armour of a race and slot (repair attempt 2, measured on the install's own masks: share of
    /// R / G / B over the painted texels, and confirmed in the rendered regular armour): Chaos legs are mostly secondary (<c>csm_armour_mp_basic_legl</c> 20 / 62 / 17 %,
    /// <c>_legr</c> 16 / 72 / 12 %; the regular Chaos legs draw in the scheme's leg secondary). Chaos pads measure mostly blue (<c>_shldrl</c> 18 / 16 / 66 %) but draw in
    /// the pad's primary colour (Alpha Legion: blue plate, silver trim), so they stay primary. Chaos head, torso and backpack (61 / 48 % R) and every Space Marine slot (basic,
    /// Mk IV, Mk VI: R 38 to 100 %, G up to 62 % on pads, helmets and torsos where the schemes mostly give both one colour) are primary, and Chaos arms are mixed
    /// (R 28 / 41 %, G 44 / 43 %: primary kept). A built mask puts its plate region in that channel, so a built piece takes the colour the regular piece of its slot
    /// shows (the Death Guard and dlc2_set3 / dlc3_set2 legs and pads looked like another colour than the regular armour beside them). Key: the package's race
    /// prefix and slot suffix.
    /// </summary>
    public static int PlateChannel(string? package) => ChannelMap(package)[0];

    /// <summary>
    /// Where a built mask's three regions go (index: the builder's region, value: the paint channel), round 3 from the test_rgb still of the regular armour (primary red,
    /// secondary green, tertiary blue): regular Chaos trims, rims and ornaments are all tertiary, so a built Chaos piece's second colour (rust or metal trim on the
    /// Death Guard and dlc2_set3 pieces) goes to tertiary too; the leg plates go to secondary (<see cref="PlateChannel"/>). Space Marine pieces keep the identity.
    /// </summary>
    public static int[] ChannelMap(string? package)
    {
        if (package is null || PaintMaskBuilder.OldMaskRules || !package.StartsWith("csm_", StringComparison.OrdinalIgnoreCase)) return [0, 1, 2];
        return PaintSchemes.SlotOf(package) is "leftleg" or "rightleg" ? [1, 2, 2] : [0, 2, 2];
    }

    /// <summary>
    /// The luminance each paint channel of a built piece is shaded to (round 3, ours, measured 2026-11-25): the median diffuse luminance (sRGB 0..255) of the regular
    /// masked piece of the same race and slot (<c>sm_armour_mp_basic_*</c>, <c>csm_armour_mp_basic_*</c>) over its texels mostly in that channel (smtool
    /// built-mask-bench --mask-shares). One value for every piece (83, ornaments 62) drew the built Chaos torsos, arms and backpacks paler than the regular armour
    /// beside them (regular Chaos torso plate 50, arms 61, backpack 60: gate "washed pale") and the built Space Marine pieces a little darker (regular 84 to 98).
    /// Channels the regular piece hardly uses (under 5 %) keep the old value. <see cref="OldBrightness"/> restores the single value.
    /// <para>Round 4 (finding 2 of the round-3 re-gate, ours, measured 2026-11-25 the same way): each channel's highlight ceiling is the regular piece's 99th
    /// percentile (<see cref="PaintMaskBuilder.Targets.PrimaryCeiling"/>, a soft shoulder): built domes shaded up to 3 x their target clipped white (Ultramarines
    /// Champion right pad) and read pale (the Chaos left pads, Dark Angels pads). The Space Marine pad rim (secondary) ceiling is 137 (p99 141 / 133), the tertiary
    /// ceiling of Space Marine slots, which the regular pieces hardly use, 139 (the torso's). The Chaos left pad's primary target is 106 (round 3: 109), the mean of
    /// the regular left pad's primary and secondary medians (109 / 103), which draw in one colour in every scheme without a pattern.</para>
    /// </summary>
    public static PaintMaskBuilder.Targets LuminanceTargets(string? package)
    {
        if (package is null || OldBrightness) return PaintMaskBuilder.Targets.Default;
        bool chaos = package.StartsWith("csm_", StringComparison.OrdinalIgnoreCase);
        const float t0 = PaintMaskBuilder.RegularOrnamentLuminance;
        static PaintMaskBuilder.Targets T(float p, float s, float t, float pc, float sc, float tc) =>
            new(p, s, t) { PrimaryCeiling = pc, SecondaryCeiling = sc, TertiaryCeiling = tc };
        return (chaos, PaintSchemes.SlotOf(package)) switch
        {
            (false, "head") => T(94f, 96f, t0, 114f, 113f, 139f),
            (false, "torso") => T(84f, 84f, 77f, 119f, 121f, 139f),
            (false, "leftshoulder" or "rightshoulder") => T(98f, 55f, t0, 121f, 137f, 139f),
            (false, "leftarm" or "rightarm") => T(92f, 90f, t0, 116f, 116f, 139f),
            (false, "leftleg" or "rightleg") => T(92f, 100f, t0, 117f, 122f, 139f),
            (false, "backpack") => T(91f, 89f, t0, 122f, 122f, 133f),
            (true, "head") => T(83f, 58f, 77f, 122f, 108f, 190f),
            (true, "torso") => T(50f, 72f, 62f, 105f, 114f, 159f),
            (true, "leftshoulder") => T(106f, 103f, 56f, 133f, 123f, 146f),
            (true, "rightshoulder") => T(83f, 80f, 69f, 116f, 119f, 163f),
            (true, "leftarm") => T(61f, 74f, 61f, 118f, 124f, 149f),
            (true, "rightarm") => T(61f, 72f, 59f, 113f, 116f, 154f),
            (true, "leftleg") => T(77f, 74f, 83f, 127f, 126f, 174f),
            (true, "rightleg") => T(89f, 72f, 80f, 124f, 124f, 182f),
            (true, "backpack") => T(60f, 63f, 67f, 126f, 117f, 130f),
            _ => PaintMaskBuilder.Targets.Default,
        };
    }

    /// <summary>
    /// Restore switch (round 4): true shades built pieces against the builder's region medians (as round 3) instead of <see cref="PaintMaskBuilder.WithRegionStats"/>,
    /// keeps the builder's shares and leaves specks alone (<see cref="PaintMaskBuilder.Despeckle"/>), and drops the round-4 regular specular, shading speckle clamp,
    /// UV padding and per-piece contrast. A look only (client; <c>--old-region-stats</c> or SM_OLD_REGION_STATS=1); not part of the mask cache.
    /// <para>Not an exact round-3 restore (round-4 gate, LOW): the highlight ceilings and the Chaos left pad target 106 stay (they go with <see cref="OldBrightness"/>),
    /// as do the per-piece brightness scales (Emperor's Children helmet 0.75, dlc2_set3 / dlc3_set2 helmet 0.72), every region-data entry added since round 3 and the
    /// one-sided hue filter fix; the round-5 additions (placeholder normal maps, island pass, broad-shading removal) also go with it.</para>
    /// </summary>
    public static bool OldRegionStats
    {
        get => _oldRegionStats ?? (Environment.GetEnvironmentVariable("SM_OLD_REGION_STATS") == "1" || Array.IndexOf(Environment.GetCommandLineArgs(), "--old-region-stats") >= 0);
        set => _oldRegionStats = value;
    }
    static bool? _oldRegionStats;

    /// <summary>
    /// Restore switch: true shades every built piece to the single first-build brightness (plates 83, ornaments 62) instead of <see cref="LuminanceTargets"/>.
    /// A look only (client; <c>--old-built-brightness</c> or SM_OLD_BUILT_BRIGHTNESS=1); not part of the mask cache.
    /// </summary>
    public static bool OldBrightness
    {
        get => _oldBrightness ?? (Environment.GetEnvironmentVariable("SM_OLD_BUILT_BRIGHTNESS") == "1" || Array.IndexOf(Environment.GetCommandLineArgs(), "--old-built-brightness") >= 0);
        set => _oldBrightness = value;
    }
    static bool? _oldBrightness;

    static (string Mask, string Grey)? Build(BmatMaterial m, Sources src, string stem, IReadOnlyDictionary<string, byte[]> textures, Dictionary<string, byte[]> added,
        List<string>? notes, Func<int, int, bool[]?> coverage, int[]? channels = null, string? package = null)
    {
        if (!textures.TryGetValue(src.Diffuse, out var difBytes))
        {
            notes?.Add($"material '{m.Name}': diffuse '{src.Diffuse}' missing; no paint mask built (drawn as baked)");
            return null;
        }
        byte[]? Bytes(string? name) => name is not null && textures.TryGetValue(name, out var b) ? b : null;
        Gate.Wait();
        int running = Interlocked.Increment(ref _running);
        try
        {
            for (int peak = Volatile.Read(ref _peak); running > peak && Interlocked.CompareExchange(ref _peak, running, peak) != peak; peak = Volatile.Read(ref _peak)) { }
            BuildHook?.Invoke();
            if (DdsImage.TryDecode(difBytes) is not { } diffuse)
            {
                notes?.Add($"material '{m.Name}': diffuse '{src.Diffuse}' undecodable; no paint mask built (drawn as baked)");
                return null;
            }
            var used = coverage(diffuse.Width, diffuse.Height);
            byte[]? specBytes = Bytes(src.Specular), lpBytes = Bytes(src.LightProp);
            string? key = CacheDirectory is null ? null : CacheKey(difBytes, specBytes, lpBytes, used);
            PaintMaskBuilder.MaskOnly maskOnly;
            if (key is not null && ReadCache(key, diffuse.Width, diffuse.Height) is { } cached)
            {
                maskOnly = cached;
                Interlocked.Increment(ref _hits);
            }
            else
            {
                DdsImage? Opt(byte[]? b) => b is null ? null : DdsImage.TryDecode(b);
                maskOnly = PaintMaskBuilder.BuildMask(diffuse, Opt(specBytes), Opt(lpBytes), used);
                Interlocked.Increment(ref _built);
                if (key is not null) WriteCache(key, maskOnly);
            }
            // Round 3: per-piece data (BuiltMaskOverrides) may set the plate channel and place regions by hand, after the rules; never under --old-mask-rules.
            var overrides = PaintMaskBuilder.OldMaskRules ? [] : BuiltMaskOverrides.For(package, m.Name).ToList();
            var map = overrides.LastOrDefault(o => o.Channels is not null)?.Channels ?? channels ?? [0, 1, 2];
            var permuted = PaintMaskBuilder.Permute(maskOnly, map);
            var remapped = permuted;
            foreach (var o in overrides) remapped = BuiltMaskOverrides.Apply(remapped, diffuse, o.Regions, used);
            // Round 4 (findings 1, 3 and 4): medians and shares from the final mask, each texel shaded against the texels that came into its channel the same way.
            // Specks of one region inside another join it first (PaintMaskBuilder.Despeckle).
            if (!PaintMaskBuilder.OldMaskRules && !OldRegionStats)
            {
                var cleaned = PaintMaskBuilder.Despeckle(PaintMaskBuilder.Despeckle(remapped, used), used, radius: 4, holesOnly: true);
                // Round 5: per-piece island pass (BuiltMaskOverride.MinIsland).
                int minIsland = OldRound4Look ? 0 : overrides.Select(o => o.MinIsland).DefaultIfEmpty(0).Max();
                if (minIsland > 1) cleaned = PaintMaskBuilder.MergeIslands(cleaned, used, minIsland);
                remapped = PaintMaskBuilder.WithRegionStats(maskOnly, permuted, cleaned, diffuse, used);
            }
            var targets = PaintMaskBuilder.OldMaskRules ? PaintMaskBuilder.Targets.Default : LuminanceTargets(package);
            float scale = OldBrightness ? 1f : overrides.Aggregate(1f, (a, o) => a * o.Brightness);
            if (scale != 1f) targets = targets.Scaled(scale);
            float contrast = OldBrightness || OldRegionStats ? 1f : overrides.Aggregate(1f, (a, o) => a * o.Contrast);
            if (contrast != 1f) targets = targets with { Contrast = contrast };
            // Round 5: per-piece broad-shading removal (BuiltMaskOverride.Flatten).
            float flatten = OldBrightness || OldRegionStats || OldRound4Look ? 0f : overrides.Select(o => o.Flatten).DefaultIfEmpty(0f).Max();
            if (flatten > 0f) targets = targets with { Flatten = Math.Clamp(flatten, 0f, 1f) };
            // Round 6: per-piece shading speckle clamp (BuiltMaskOverride.Speckle; the smallest share given applies).
            float speckle = OldBrightness || OldRegionStats || OldRound4Look ? 0f : overrides.Where(o => o.Speckle > 0f).Select(o => o.Speckle).DefaultIfEmpty(0f).Min();
            if (speckle > 0f) targets = targets with { Speckle = speckle };
            var result = PaintMaskBuilder.WithGrey(diffuse, remapped, targets);
            if (!PaintMaskBuilder.OldMaskRules && !OldRegionStats) result = PaintMaskBuilder.PadOutside(result, used);
            if (result.Primary + result.Secondary + result.Tertiary < 0.005f)
            {
                notes?.Add($"material '{m.Name}': the built mask paints nothing; drawn as baked");
                return null;
            }
            string maskName = stem + MaskSuffix, greyName = stem + GreySuffix;
            added[maskName] = DdsImage.EncodeRgba8WithMips(result.Width, result.Height, result.Mask);
            added[greyName] = DdsImage.EncodeRgba8WithMips(result.Width, result.Height, result.Diffuse);
            return (maskName, greyName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Finding 5: one material the builder cannot handle is drawn baked; the package and its other materials are drawn as usual.
            Interlocked.Increment(ref _failed);
            notes?.Add($"material '{m.Name}': paint mask build failed ({ex.GetType().Name}: {ex.Message}); drawn as baked");
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            Gate.Release();
        }
    }

    // ---- the built-mask cache ----

    const uint CacheMagic = 0x4D42_4D53;   // "SMBM"

    /// <summary>The cache key: SHA-256 over the builder version, the restore switch, and the source bytes and coverage (hex, 40 characters).</summary>
    internal static string CacheKey(byte[] diffuse, byte[]? specular, byte[]? lightProp, bool[]? used)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> head = stackalloc byte[8];
        BitConverter.TryWriteBytes(head, BuilderVersion);   // the channel remap is applied after the cache, so it is not part of the key
        BitConverter.TryWriteBytes(head[4..], (PaintMaskBuilder.OldDarkPlates ? 1 : 0) | (PaintMaskBuilder.OldMaskRules ? 2 : 0));
        h.AppendData(head);
        foreach (var part in new[] { diffuse, specular ?? [], lightProp ?? [] })
        {
            Span<byte> len = stackalloc byte[4];
            BitConverter.TryWriteBytes(len, part.Length);
            h.AppendData(len);
            h.AppendData(part);
        }
        if (used is not null)
        {
            var bits = new byte[(used.Length + 7) / 8];
            for (int i = 0; i < used.Length; i++) if (used[i]) bits[i >> 3] |= (byte)(1 << (i & 7));
            h.AppendData(bits);
        }
        return Convert.ToHexString(h.GetHashAndReset())[..40];
    }

    static string CachePath(string key) => Path.Combine(CacheDirectory!, key + ".bm");

    /// <summary>The cached mask for <paramref name="key"/> (null when absent, of another size, or unreadable: then it is built again).</summary>
    static PaintMaskBuilder.MaskOnly? ReadCache(string key, int w, int h)
    {
        try
        {
            string path = CachePath(key);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != CacheMagic || r.ReadInt32() != BuilderVersion || r.ReadInt32() != w || r.ReadInt32() != h) return null;
            float pm = r.ReadSingle(), sm = r.ReadSingle(), om = r.ReadSingle(), sp = r.ReadSingle(), ss = r.ReadSingle(), st = r.ReadSingle();
            var rgb = new byte[w * h * 3];
            using (var z = new ZLibStream(fs, CompressionMode.Decompress, leaveOpen: true)) z.ReadExactly(rgb);
            var mask = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { mask[i * 4] = rgb[i * 3]; mask[i * 4 + 1] = rgb[i * 3 + 1]; mask[i * 4 + 2] = rgb[i * 3 + 2]; }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // recently used: pruned last
            return new PaintMaskBuilder.MaskOnly(w, h, mask, pm, sm, om, sp, ss, st);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException) { return null; }
    }

    static void WriteCache(string key, PaintMaskBuilder.MaskOnly built)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory!);
            string path = CachePath(key), tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(CacheMagic); w.Write(BuilderVersion); w.Write(built.Width); w.Write(built.Height);
                w.Write(built.PlateMedian); w.Write(built.SecondaryMedian); w.Write(built.OrnamentMedian); w.Write(built.Primary); w.Write(built.Secondary); w.Write(built.Tertiary);
                w.Flush();
                var rgb = new byte[built.Width * built.Height * 3];
                for (int i = 0; i < built.Width * built.Height; i++) { rgb[i * 3] = built.Mask[i * 4]; rgb[i * 3 + 1] = built.Mask[i * 4 + 1]; rgb[i * 3 + 2] = built.Mask[i * 4 + 2]; }
                using var z = new ZLibStream(fs, CompressionLevel.Fastest, leaveOpen: true);
                z.Write(rgb);
            }
            File.Move(tmp, path, overwrite: true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a cache that cannot be written only costs the next start a build
    }

    /// <summary>Keeps the cache under <see cref="CacheBudgetBytes"/>: the least recently used entries go first.</summary>
    internal static void Prune()
    {
        var files = new DirectoryInfo(CacheDirectory!).GetFiles("*.bm").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
            if (total > CacheBudgetBytes) try { f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
