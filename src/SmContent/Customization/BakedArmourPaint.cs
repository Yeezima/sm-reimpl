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
    /// cached mask. 2: armour-pieces-1125 repair attempt 1; 3: repair attempt 2.
    /// </summary>
    public const int BuilderVersion = 3;

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
        int plateChannel = PlateChannel(package);
        var added = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var built = new Dictionary<Sources, (string Mask, string Grey)?>();
        var surfaces = new List<StaticModelSurface>(model.Surfaces.Count);
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
                names = Build(m, key, stem, model.Textures, added, notes, (w, h) => Coverage(model, users, w, h), plateChannel);
                built[key] = names;
            }
            if (names is not { } n) { surfaces.Add(s); continue; }
            var tex = new Dictionary<string, string>(m.Textures, StringComparer.Ordinal) { ["PaintMask"] = n.Mask, [PaintDiffuseKey] = n.Grey };
            surfaces.Add(s with { Material = m with { Textures = tex } });
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

    /// <summary>
    /// The paint channel that carries the plates of the regular (masked) armour of a race and slot (repair attempt 2, measured on the install's own masks: share of
    /// R / G / B over the painted texels): Chaos legs are mostly secondary (<c>csm_armour_mp_basic_legl</c> 20 / 62 / 17 %, <c>_legr</c> 16 / 72 / 12 %) and Chaos pads
    /// mostly tertiary (<c>_shldrl</c> 18 / 16 / 66 %, <c>_shldrr</c> 16 / 9 / 75 %); Chaos head, torso and backpack (61 / 48 % R) and every Space Marine slot (basic,
    /// Mk IV, Mk VI: R 38 to 100 %, G up to 62 % on pads, helmets and torsos where the schemes mostly give both one colour) are primary, and Chaos arms are mixed
    /// (R 28 / 41 %, G 44 / 43 %: primary kept). A built mask puts its plate region in that channel, so a built piece takes the colour the regular piece of its slot
    /// shows (the Death Guard and dlc2_set3 / dlc3_set2 legs and pads looked like another colour than the regular armour beside them). Key: the package's race
    /// prefix and slot suffix.
    /// </summary>
    public static int PlateChannel(string? package)
    {
        if (package is null || PaintMaskBuilder.OldMaskRules || !package.StartsWith("csm_", StringComparison.OrdinalIgnoreCase)) return 0;
        return PaintSchemes.SlotOf(package) switch { "leftleg" or "rightleg" => 1, "leftshoulder" or "rightshoulder" => 2, _ => 0 };
    }

    static (string Mask, string Grey)? Build(BmatMaterial m, Sources src, string stem, IReadOnlyDictionary<string, byte[]> textures, Dictionary<string, byte[]> added,
        List<string>? notes, Func<int, int, bool[]?> coverage, int plateChannel = 0)
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
            var result = PaintMaskBuilder.WithGrey(diffuse, PaintMaskBuilder.Remap(maskOnly, plateChannel));
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
