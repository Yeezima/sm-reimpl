using System;
using System.Collections.Generic;
using Godot;
using SmContent;
using SmFormats.Bod;
using SmSim;

namespace SmClient;

/// <summary>
/// Painted armour (the <c>character_wargear</c> shader of the multiplayer armour). Evidence (Verified-local, viewed channel by channel):
/// the piece's <c>PaintMask</c> (<c>_pnt</c>, DXT5) holds the primary colour's region in its red channel, the secondary's in green, the
/// tertiary's in blue (regions of one piece barely overlap) and a dirt mask in alpha; the diffuse (<c>_dif</c>) is unpainted grey shading
/// (plus rust and rivets). The colours are the player's paint scheme (<see cref="PaintSchemes"/>); <c>DirtColour</c> is the material's.
/// How the engine combines them is not known: **provisional approximation** (the shading luminance times the paint colour, mixed in by
/// the mask; dirt mixed in by its alpha; the paint's specular colour sets the specular scale there).
/// M40-T13: the <c>LightPropMap</c> (<c>_lp</c>, DXT1) is read too (docs/discovery.md "Light property maps"): R a per-material property (most likely the specular power) used as a roughness scale
/// (lowest on metal ornaments, highest on cables and undersuit; the scale is R over the plates' value; low confidence), G the
/// self-illumination mask (eye lenses, plasma coils; emission = albedo x G x <see cref="LpGlowGain"/>, the gain ours; none inside the lens rectangles of a recoloured helmet: the chosen lens colour is the lens's colour, not a glow), B the reflection mask
/// (the gold ornaments; drawn as partial metalness and lower roughness, ours). The paint gain (<see cref="PaintGain"/>) is 3.1 since M40-T13,
/// measured against the pre-painted Ultramarine set (it was 2.5 by eye).
/// </summary>
public sealed partial class MapMaterials
{
    static readonly Dictionary<(bool, bool), Shader> PaintShaders = new();
    readonly Dictionary<Texture2D, bool> _dirtUsable = new();

    /// <summary>Test aid: SM_PAINT_LINEAR=1 reads the paint colours as linear instead of sRGB (the stored colour space is unknown).</summary>
    public static readonly bool PaintIsLinear = System.Environment.GetEnvironmentVariable("SM_PAINT_LINEAR") == "1";
    /// <summary>
    /// The shading gain: painted albedo (linear) = gain x the diffuse's linear luminance x the paint colour (linear). 3.1 (M40-T13): on the primary plates of the
    /// <c>sm_armour_mp_basic</c> torso and helmet the pre-painted <c>sm_armour_basic_001</c> blue is 3.1 x (grey x <c>ultramarines_blue</c>) in the green and blue
    /// channels (median texels; legs and pads 3.8 to 4.3), so the old 2.5 (by eye) was about 20% too dark. Test aid: SM_PAINT_GAIN=&lt;n&gt;.
    /// </summary>
    public static readonly float PaintGain = EnvFloat("SM_PAINT_GAIN", 3.1f);
    /// <summary>Emission from the light property map's G (the self-illumination mask): albedo x G x this (ours; the data's <c>EmissiveColor</c> is 1 where shaders name one). SM_LP_GLOW=&lt;n&gt;.</summary>
    public static readonly float LpGlowGain = EnvFloat("SM_LP_GLOW", 4f);
    /// <summary>Metalness where the light property map's B (the reflection mask) is 1 (ours). SM_LP_METAL=&lt;n&gt;.</summary>
    public static readonly float LpMetal = EnvFloat("SM_LP_METAL", 0.5f);
    /// <summary>Test aid: SM_LP_OFF=1 ignores the light property map (the look before M40-T13, apart from the paint gain).</summary>
    public static readonly bool LpOff = System.Environment.GetEnvironmentVariable("SM_LP_OFF") == "1";
    /// <summary>The light property map's R on the armour plates (0.93 to 0.95 on the basic sets): the roughness scale is R over this.</summary>
    public const float LpPlateR = 0.93f;

    static float EnvFloat(string name, float fallback) => float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : fallback;

    public int Painted { get; private set; }
    /// <summary>Painted surfaces whose light property map (<c>_lp</c>) was loaded (M40-T13).</summary>
    public int LightProps { get; private set; }
    public int Decals { get; private set; }
    readonly Dictionary<string, Texture2D?> _decalTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test aid: SM_DECAL_ORIENT=&lt;0..7&gt; (bit 0 flip U, bit 1 flip V, bit 2 swap U and V) turns the decal on the second UV set.</summary>
    public static readonly int DecalOrient = int.TryParse(System.Environment.GetEnvironmentVariable("SM_DECAL_ORIENT"), out var o) ? o : 0;

    /// <summary>A decal texture (a 128x128 DXT5 DDS), decoded once per name; null (with a note) when the bytes are missing or do not decode.</summary>
    public Texture2D? LoadDecal(string name, Func<byte[]?> read, List<string> notes)
    {
        if (_decalTextures.TryGetValue(name, out var cached)) return cached;
        Texture2D? texture = null;
        var bytes = read();
        if (bytes is null) notes.Add($"decal '{name}' is not in the install; not drawn");
        else
        {
            var image = new Image();
            var err = image.LoadDdsFromBuffer(bytes);
            if (err == Error.Ok)
            {
                // The DDS has no mipmaps; at play distance the badge is minified several times, so build them (the compressed image cannot).
                if (image.IsCompressed()) image.Decompress();
                if (!image.HasMipmaps()) image.GenerateMipmaps();
                texture = ImageTexture.CreateFromImage(image);
            }
            else notes.Add($"decal '{name}' failed to decode: {err}");
        }
        return _decalTextures[name] = texture;
    }

    /// <summary>
    /// The painted material of a wargear <paramref name="bmat"/> (it names <c>PaintMask</c>), or null when the mask or diffuse is missing.
    /// The normal and specular maps are handled as in <see cref="Build"/>.
    /// </summary>
    public Material? BuildPainted(BmatMaterial bmat, IReadOnlyDictionary<string, byte[]> dds, List<string> notes, PaintSet paint, bool normalMaps = true, Texture2D? decal = null)
    {
        if (PreparePainted(bmat, dds, notes, normalMaps) is not { } surface) return null;
        return MakePainted(surface, PaintColours.From(paint), decal, null);
    }

    /// <summary>
    /// The colour-independent part of a painted surface (customizer T6): the material's textures and constants, read once per surface of a piece. Many
    /// <see cref="ShaderMaterial"/>s with different colours, decals or lens colours come from it (<see cref="MakePainted"/>), sharing these textures.
    /// </summary>
    public sealed class PaintedSurface
    {
        internal Texture2D Diffuse = null!, Mask = null!;
        internal Texture2D? Normal, Gloss, LightProp;
        internal Color Dirt;
        internal float DirtAmount, Rough, Spec;
        internal bool CutOut, TwoSided;
        public string MaterialName { get; internal set; } = "";
    }

    /// <summary>
    /// The textures and constants of a wargear <paramref name="bmat"/> that names a <c>PaintMask</c> (null when the mask or diffuse is missing). Cut-out and
    /// two-sided materials as in <see cref="Build"/> (none of the 316 paint-mask materials in the install sets them: BlendMode and CullMode are 255, the
    /// default). EmissiveMap is not supported by the painted shader (none names one): noted if it ever occurs.
    /// </summary>
    public PaintedSurface? PreparePainted(BmatMaterial bmat, IReadOnlyDictionary<string, byte[]> dds, List<string> notes, bool normalMaps = true)
    {
        if (!bmat.Textures.TryGetValue("DiffuseMap", out var diffuseName) || Load(diffuseName, dds, notes) is not { } diffuse) return null;
        if (!bmat.Textures.TryGetValue("PaintMask", out var maskName) || Load(maskName, dds, notes) is not { } mask) return null;
        if (bmat.Textures.ContainsKey("EmissiveMap")) notes.Add($"material '{bmat.Name}': EmissiveMap is not drawn on painted materials");
        var s = new PaintedSurface
        {
            MaterialName = bmat.Name ?? "", Diffuse = diffuse, Mask = mask, CutOut = bmat.BlendMode == 8, TwoSided = bmat.CullMode == 0,
            DirtAmount = DirtUsable(mask) ? 0.6f : 0f,
        };
        var dirt = bmat.Vectors.TryGetValue("DirtColour", out var d) && d.Length >= 3 ? new Color(d[0], d[1], d[2]) : new Color(0.28f, 0.22f, 0.14f);
        s.Dirt = PaintIsLinear ? ToSrgb(dirt) : dirt;
        if (normalMaps && bmat.Textures.TryGetValue("NormalMap", out var normalName) && Load(normalName, dds, notes) is { } normal) { s.Normal = normal; NormalMapped++; }
        float roughness = RoughnessFor(bmat, "SpecularPower"), specular = SpecularFor(bmat, "SpecularColor");
        if (bmat.Textures.TryGetValue("SpecularMap", out var specName) && Gloss(specName, dds, notes) is { } gloss)
        {
            s.Gloss = gloss;
            roughness = Math.Min(1f, roughness * 1.4f);
            specular = Math.Max(specular, 0.4f);
            Specular++;
        }
        s.Rough = roughness; s.Spec = specular;
        if (!LpOff && bmat.Textures.TryGetValue("LightPropMap", out var lpName) && Load(lpName, dds, notes) is { } lp) { s.LightProp = lp; LightProps++; }
        return s;
    }

    /// <summary>The three paint colours of a painted material as the shader takes them (a colour the look does not define draws the shader's neutral grey).</summary>
    public readonly record struct PaintColours(Color Primary, Color Secondary, Color Tertiary, Vector3 Spec)
    {
        static readonly Color Neutral = new(0.5f, 0.5f, 0.5f);
        const float NeutralSpec = 0.4f;

        public static PaintColours From(PaintSet set) => new(PaintColourOf(set.Primary.Diffuse), PaintColourOf(set.Secondary.Diffuse), PaintColourOf(set.Tertiary.Diffuse),
            new Vector3(SpecularScale(set.Primary), SpecularScale(set.Secondary), SpecularScale(set.Tertiary)));

        /// <summary>From a resolved look's paint (null: all neutral). The specular scale comes from the colour's specular exactly as for a <see cref="PaintColour"/>.</summary>
        public static PaintColours From(SmContent.Customization.ResolvedPaint? paint) => paint is null ? new(Neutral, Neutral, Neutral, new Vector3(NeutralSpec, NeutralSpec, NeutralSpec))
            : new(Of(paint.Primary), Of(paint.Secondary), Of(paint.Tertiary), new Vector3(SpecOf(paint.Primary), SpecOf(paint.Secondary), SpecOf(paint.Tertiary)));

        static Color Of(SmContent.Customization.ResolvedColour? c) => c is null ? Neutral : PaintColourOf(c.Diffuse);
        static float SpecOf(SmContent.Customization.ResolvedColour? c) => c is null ? NeutralSpec : SpecularScaleOf(c.Specular);
    }

    /// <summary>A lens colour and the helmet's lens rectangles (<see cref="SmContent.Customization.LensRegions.Slots"/>) for the painted shader.</summary>
    public sealed record LensSetting(Color Colour, (float U0, float V0, float U1, float V1)[] Rects, float Gain = 1f);

    /// <summary>
    /// A painted material for <paramref name="colours"/>, with the <paramref name="decal"/> (null: none) and the <paramref name="lens"/> recolouring
    /// (null: the original look). Cheap: only uniforms differ, the shader and textures are shared.
    /// </summary>
    public ShaderMaterial MakePainted(PaintedSurface s, PaintColours colours, Texture2D? decal, LensSetting? lens)
    {
        if (!PaintShaders.TryGetValue((s.CutOut, s.TwoSided), out var shader))
            PaintShaders[(s.CutOut, s.TwoSided)] = shader = new Shader { Code = PaintShaderCode(s.CutOut, s.TwoSided) };
        var material = new ShaderMaterial { Shader = shader };
        if (s.CutOut) CutOut++;
        material.SetShaderParameter("albedo_tex", s.Diffuse);
        material.SetShaderParameter("paint_mask", s.Mask);
        material.SetShaderParameter("primary", colours.Primary);
        material.SetShaderParameter("secondary", colours.Secondary);
        material.SetShaderParameter("tertiary", colours.Tertiary);
        material.SetShaderParameter("dirt_colour", s.Dirt);
        material.SetShaderParameter("paint_spec", colours.Spec);
        material.SetShaderParameter("gain", PaintGain);
        if (decal is not null)
        {
            material.SetShaderParameter("decal_tex", decal);
            material.SetShaderParameter("use_decal", true);
            material.SetShaderParameter("decal_orient", DecalOrient);
            Decals++;
        }
        material.SetShaderParameter("dirt_amount", s.DirtAmount);
        if (s.Normal is not null) { material.SetShaderParameter("normal_tex", s.Normal); material.SetShaderParameter("use_normal", true); }
        if (s.Gloss is not null) { material.SetShaderParameter("gloss_tex", s.Gloss); material.SetShaderParameter("use_gloss", true); }
        material.SetShaderParameter("rough", s.Rough);
        material.SetShaderParameter("spec", s.Spec);
        if (s.LightProp is not null)
        {
            material.SetShaderParameter("lp_tex", s.LightProp);
            material.SetShaderParameter("use_lp", true);
            material.SetShaderParameter("lp_ref", LpPlateR);
            material.SetShaderParameter("glow_gain", LpGlowGain);
            material.SetShaderParameter("reflect_metal", LpMetal);
        }
        if (lens is not null && lens.Rects.Length > 0)
        {
            material.SetShaderParameter("use_lens", true);
            material.SetShaderParameter("lens_colour", lens.Colour);
            material.SetShaderParameter("lens_gain", lens.Gain);
            for (int i = 0; i < lens.Rects.Length && i < 4; i++)
            {
                var r = lens.Rects[i];
                material.SetShaderParameter($"lens_rect{i}", new Vector4(r.U0, r.V0, r.U1, r.V1));
            }
        }
        Painted++;
        return material;
    }

    /// <summary>
    /// A custom emblem as a decal texture: the 16,384 bytes of a 128 x 128 BC3 (DXT5) image, as the badges are (<see cref="LoadDecal"/> builds mipmaps the same way).
    /// Decoded once per hash. On a miss the bytes must be exactly 16,384 and hash to <paramref name="hash"/> (a peer's image is untrusted: the blob cache verifies too, this
    /// is the last check before the GPU); otherwise null with <paramref name="problem"/> and nothing cached, so the right bytes arriving later replace the failure.
    /// At most <see cref="MaxEmblemTextures"/> are kept: past that they are all dropped (the textures live on in the characters that show them).
    /// </summary>
    public Texture2D? LoadEmblem(EmblemHash hash, byte[] bc3, out string? problem)
    {
        problem = null;
        string key = EmblemKey(hash);
        if (_decalTextures.TryGetValue(key, out var cached) && cached is not null) return cached;
        if (bc3.Length != EmblemHash.BlobBytes) { problem = $"emblem {hash.ToHex()}: {bc3.Length} bytes, not a 128 x 128 BC3 image; not drawn"; return null; }
        if (EmblemHash.Of(bc3) != hash) { problem = $"emblem {hash.ToHex()}: the bytes do not hash to it; not drawn"; return null; }
        var image = Image.CreateFromData(EmblemHash.Side, EmblemHash.Side, false, Image.Format.Dxt5, bc3);
        if (image is null || image.IsEmpty()) { problem = $"emblem {hash.ToHex()} could not be read as BC3; not drawn"; return null; }
        image.Decompress();
        image.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(image);
        if (_emblemKeys.Count >= MaxEmblemTextures)
        {
            foreach (var k in _emblemKeys) _decalTextures.Remove(k);
            _emblemKeys.Clear();
        }
        _decalTextures[key] = texture;
        _emblemKeys.Add(key);
        return texture;
    }

    /// <summary>The cache key of an emblem's texture.</summary>
    public static string EmblemKey(EmblemHash hash) => "e:" + hash.ToHex();

    /// <summary>The decoded emblem for <paramref name="hash"/>, or null when it is not cached (the bytes need not be fetched again when it is).</summary>
    public Texture2D? CachedEmblem(EmblemHash hash) => _decalTextures.GetValueOrDefault(EmblemKey(hash));

    /// <summary>Whether a decal texture (or its known absence) is cached under <paramref name="key"/>: <see cref="LoadDecal"/> would not read anything.</summary>
    public bool HasDecalTexture(string key) => _decalTextures.ContainsKey(key);

    /// <summary>The most emblem textures kept (ours): a peer can send any number of different images, so the cache is bounded.</summary>
    public const int MaxEmblemTextures = 64;
    readonly HashSet<string> _emblemKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// False when the mask's alpha (the dirt) is nearly constant and high (mean above 0.5, deviation under 0.05): then it selects nothing, the whole
    /// piece would be dirt. In the install the alpha is sparse on every mask checked (mean 0-25 of 255, exactly 0 on the heavy bolter and Chaos
    /// jump pack masks), so this only guards against a mask where it is not.
    /// </summary>
    bool DirtUsable(Texture2D mask)
    {
        if (_dirtUsable.TryGetValue(mask, out var cached)) return cached;
        bool usable = true;
        if (mask.GetImage() is { } image)
        {
            if (image.IsCompressed()) image.Decompress();
            image.ClearMipmaps();
            image.Convert(Image.Format.Rgba8);
            image.Resize(32, 32, Image.Interpolation.Nearest);
            var data = image.GetData();
            double sum = 0, sq = 0;
            for (int i = 3; i < data.Length; i += 4) { double a = data[i] / 255.0; sum += a; sq += a * a; }
            double n = data.Length / 4, mean = sum / n, std = Math.Sqrt(Math.Max(0, sq / n - mean * mean));
            usable = !(mean > 0.5 && std < 0.05);
        }
        return _dirtUsable[mask] = usable;
    }

    static Color PaintColourOf(float[] rgb)
    {
        var c = new Color(Math.Clamp(rgb[0], 0f, 1f), Math.Clamp(rgb[1], 0f, 1f), Math.Clamp(rgb[2], 0f, 1f));
        return PaintIsLinear ? ToSrgb(c) : c;   // the shader's source_color hint converts sRGB to linear: pre-encode a linear reading
    }

    static Color ToSrgb(Color c) => new(Enc(c.R), Enc(c.G), Enc(c.B));
    static float Enc(float v) => v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    /// <summary>The specular scale the paint's specular colour gives (luminance 0.25-0.8; gold is brightest). Provisional.</summary>
    static float SpecularScale(PaintColour c) => SpecularScaleOf(c.Specular);

    static float SpecularScaleOf(float[] specular) =>
        Math.Clamp((0.299f * specular[0] + 0.587f * specular[1] + 0.114f * specular[2]) * 0.8f, 0.25f, 0.8f);

    public static string PaintShaderCode(bool cutOut, bool twoSided) => $$"""
        shader_type spatial;
        render_mode {{(twoSided ? "cull_disabled, " : "")}}diffuse_burley, specular_schlick_ggx{{(cutOut ? RenderQuality.CutOutRenderMode : "")}};
        uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D paint_mask : filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform sampler2D gloss_tex : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform bool use_normal = false;
        uniform bool use_gloss = false;
        uniform sampler2D decal_tex : source_color, filter_linear_mipmap, repeat_disable;
        uniform bool use_decal = false;
        uniform int decal_orient = 0;
        uniform vec3 primary : source_color = vec3(0.5);
        uniform vec3 secondary : source_color = vec3(0.5);
        uniform vec3 tertiary : source_color = vec3(0.5);
        uniform vec3 dirt_colour : source_color = vec3(0.28, 0.22, 0.14);
        uniform vec3 paint_spec = vec3(0.4);
        uniform float gain = 3.1;
        uniform float dirt_amount = 0.6;
        uniform float rough = 0.8;
        uniform float spec = 0.4;
        // M40-T13: the light property map (_lp; linear data): R a per-material property read as a roughness scale, G self-illumination mask, B reflection mask (discovery.md "Light property maps").
        uniform sampler2D lp_tex : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform bool use_lp = false;
        uniform float lp_ref = 0.93;
        uniform float glow_gain = 4.0;
        uniform float reflect_metal = 0.5;
        // Eye lens colour (ours; discovery.md "Helmet eye lenses"): inside the helmet's lens rectangles (0..1 over the diffuse, rows from the top), texels
        // that are saturated, not dark and outside the paint mask are the lens; they take lens_colour at the texel's own brightness. The lens is recoloured,
        // it does not glow: inside the rectangles the light property map's self-illumination is switched off while a lens colour is chosen.
        uniform bool use_lens = false;
        uniform vec3 lens_colour : source_color = vec3(1.0, 0.0, 0.0);
        uniform float lens_gain = 1.0;
        uniform vec4 lens_rect0 = vec4(0.0);
        uniform vec4 lens_rect1 = vec4(0.0);
        uniform vec4 lens_rect2 = vec4(0.0);
        uniform vec4 lens_rect3 = vec4(0.0);
        float in_rect(vec2 t, vec4 r) {
            return step(r.x, t.x) * step(t.x, r.z) * step(r.y, t.y) * step(t.y, r.w) * step(r.x + 0.0001, r.z);
        }
        void fragment() {
            vec3 base = texture(albedo_tex, UV).rgb;
            vec4 m = texture(paint_mask, UV);
            float luma = dot(base, vec3(0.2126, 0.7152, 0.0722));
            float shade = clamp(luma * gain, 0.0, 1.5);
            float w = clamp(m.r + m.g + m.b, 0.0, 1.0);
            vec3 colour = (primary * m.r + secondary * m.g + tertiary * m.b) / max(m.r + m.g + m.b, 0.001);
            vec3 albedo = mix(base, colour * shade, w);
            albedo = mix(albedo, dirt_colour * shade, m.a * dirt_amount);
            float lens_zone = 0.0;
            if (use_lens) {
                vec2 t = fract(UV);
                float inside = max(max(in_rect(t, lens_rect0), in_rect(t, lens_rect1)), max(in_rect(t, lens_rect2), in_rect(t, lens_rect3)));
                if (inside > 0.5) {
                    lens_zone = 1.0;
                    // The rule is stated on sRGB-coded values (saturation above 0.4, brightest channel above 30/255, mask sum under 110/255); the soft
                    // edges only keep the lens outline from aliasing.
                    vec3 s = mix(base * 12.92, 1.055 * pow(max(base, vec3(0.0001)), vec3(1.0 / 2.4)) - 0.055, step(0.0031308, base));
                    float mx = max(s.r, max(s.g, s.b));
                    float sat = (mx - min(s.r, min(s.g, s.b))) / max(mx, 0.0001);
                    float w_lens = smoothstep(0.36, 0.44, sat) * smoothstep(0.10, 0.13, mx) * (1.0 - smoothstep(0.40, 0.46, m.r + m.g + m.b));
                    albedo = mix(albedo, lens_colour * (lens_gain * max(base.r, max(base.g, base.b))), w_lens);
                }
            }
            if (use_decal) {
                // The decal (chapter or legion badge) is laid over the pad through its second UV set, which is one island over the unit square.
                vec2 duv = UV2;
                if ((decal_orient & 4) != 0) duv = duv.yx;
                if ((decal_orient & 1) != 0) duv.x = 1.0 - duv.x;
                if ((decal_orient & 2) != 0) duv.y = 1.0 - duv.y;
                vec4 d = texture(decal_tex, duv);
                float inside = step(0.0, duv.x) * step(duv.x, 1.0) * step(0.0, duv.y) * step(duv.y, 1.0);
                albedo = mix(albedo, d.rgb, d.a * inside);
            }
            ALBEDO = albedo;
            if (use_normal) NORMAL_MAP = texture(normal_tex, UV).xyz;
            float roughness = rough * (use_gloss ? texture(gloss_tex, UV).r : 1.0);
            if (use_lp) {
                // R scales the roughness around the plates' value (metal ornaments a little smoother, cables a little rougher); B (reflective gold) is
                // partly metallic and smoother; G glows in the texel's own colour (not inside a recoloured lens's rectangles: the lens takes the colour, no glow). Factors: ours.
                vec3 lp = texture(lp_tex, UV).rgb;
                roughness *= clamp(lp.r / lp_ref, 0.5, 1.2) * (1.0 - 0.35 * lp.b);
                METALLIC = lp.b * reflect_metal;
                EMISSION = albedo * lp.g * glow_gain * (1.0 - lens_zone);
            }
            ROUGHNESS = clamp(roughness, 0.04, 1.0);
            float paint_s = (paint_spec.x * m.r + paint_spec.y * m.g + paint_spec.z * m.b) / max(m.r + m.g + m.b, 0.001);
            SPECULAR = mix(spec, paint_s, w);
        {{(cutOut ? "    ALPHA = texture(albedo_tex, UV).a;\n    ALPHA_SCISSOR_THRESHOLD = 0.5;" + RenderQuality.CutOutShaderLines("albedo_tex") : "")}}
        }
        """;
}
