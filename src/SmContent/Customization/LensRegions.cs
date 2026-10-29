namespace SmContent.Customization;

/// <summary>A rectangle of a helmet's diffuse texture in texels, <c>(X0, Y0)</c> inclusive to <c>(X1, Y1)</c> exclusive (the texture's rows from the top).</summary>
public readonly record struct LensRect(int X0, int Y0, int X1, int Y1)
{
    public int Width => X1 - X0;
    public int Height => Y1 - Y0;
    public bool Contains(int x, int y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;
}

/// <summary>
/// Where the eye lenses are on the helmets' diffuse textures (our addition: the original has no lens colour setting; docs/discovery.md "Helmet eye lenses",
/// customizer T6a). No helmet has a separate lens material: the lenses are texels of its <c>_dif</c> atlas, so the renderer recolours them by a rule
/// (<see cref="IsLensTexel"/>) applied inside these rectangles only. The numbers were measured on the 512 x 512 diffuse of each helmet
/// (<see cref="TextureSize"/>; a texture of another size scales the rectangles). The table holds numbers only, no original data.
/// </summary>
public static class LensRegions
{
    /// <summary>The side of the helmets' diffuse textures the rectangles were measured on.</summary>
    public const int TextureSize = 512;

    /// <summary>At most this many rectangles per helmet (the painted shader has that many uniform slots).</summary>
    public const int MaxRects = 4;

    // Keys are the head piece's package names. Chaos berserk_01 / berserk_02 have a black visor slit without lens texels: no entry (the Chaos eye glow
    // effects, a later task, are the only route). The Space Marine "mk6" dome lamp (126,236,150,261) and the "relic" side lamp are not lenses and are left out.
    static readonly Dictionary<string, LensRect[]> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sm_armour_mp_basic_helmet"] = [new(51, 305, 110, 334), new(144, 305, 203, 334)],
        ["sm_armour_mp_mk4_helmet"] = [new(253, 70, 323, 96), new(371, 69, 443, 96)],
        ["sm_armour_mp_mk6_helmet"] = [new(142, 47, 164, 106), new(142, 146, 163, 198)],
        ["sm_armour_mp_master_001_helmet"] = [new(426, 457, 504, 480), new(427, 423, 506, 446)],
        ["sm_armour_mp_veteran_helmet"] = [new(45, 291, 112, 316), new(154, 291, 223, 317)],
        ["sm_armour_mp_relic_helmet"] = [new(38, 97, 98, 121), new(256, 100, 280, 122)],
        ["sm_armour_mp_damage_helmet"] = [new(50, 304, 111, 335), new(142, 304, 203, 335)],
        ["csm_armour_mp_basic_helmet"] = [new(171, 280, 216, 314), new(106, 402, 141, 437), new(442, 214, 468, 239)],
        ["csm_armour_mp_berserk_03_helmet"] = [new(206, 150, 237, 169), new(278, 150, 308, 170)],
        ["csm_armour_mp_havoc_01_helmet"] = [new(337, 40, 379, 82), new(170, 370, 234, 411), new(282, 382, 342, 408)],
        ["csm_armour_mp_havoc_02_helmet"] = [new(292, 69, 358, 92), new(396, 69, 463, 93)],
        ["csm_armour_mp_havoc_03_helmet"] = [new(181, 388, 222, 413), new(266, 386, 286, 408)],
        ["csm_armour_mp_raptor_01_helmet"] = [new(206, 346, 254, 373), new(260, 210, 278, 226)],
    };

    /// <summary>The helmet packages that have lens rectangles.</summary>
    public static IEnumerable<string> Packages => Table.Keys;

    /// <summary>The lens rectangles of a helmet package (its name as in <see cref="ResolvedPiece.Packages"/>), empty for a helmet without lens texels or an unknown one.</summary>
    public static IReadOnlyList<LensRect> For(string package) => Table.TryGetValue(package, out var rects) ? rects : [];

    /// <summary>A rectangle as texture coordinates (0..1 from the top left) for a shader.</summary>
    public static (float U0, float V0, float U1, float V1) Uv(LensRect r) =>
        ((float)r.X0 / TextureSize, (float)r.Y0 / TextureSize, (float)r.X1 / TextureSize, (float)r.Y1 / TextureSize);

    /// <summary>The rule's constants (the painted shader mirrors them): saturation above <see cref="MinSaturation"/>, brightest channel above <see cref="MinValue"/>, paint mask below <see cref="MaxMaskSum"/>.</summary>
    public const float MinSaturation = 0.4f;
    public const int MinValue = 30, MaxMaskSum = 110;

    /// <summary>
    /// The lens rule on 8-bit values: the diffuse texel (sRGB-coded) is saturated, <c>(max - min) / max &gt; 0.4</c>, with a brightest channel above 30,
    /// and the paint mask's red + green + blue stays under 110 (the lens texels have 0 to 4, the armour about 100 / 100 / 0).
    /// </summary>
    public static bool IsLensTexel(byte r, byte g, byte b, int maskRed, int maskGreen, int maskBlue)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max > MinValue && (max - min) > MinSaturation * max && maskRed + maskGreen + maskBlue < MaxMaskSum;
    }

    /// <summary>The rectangles of a helmet package as a table of (u0, v0, u1, v1) tuples, padded with empty rectangles to <see cref="MaxRects"/>.</summary>
    public static (float U0, float V0, float U1, float V1)[] Slots(string package)
    {
        var slots = new (float, float, float, float)[MaxRects];
        var rects = For(package);
        for (int i = 0; i < rects.Count && i < MaxRects; i++) slots[i] = Uv(rects[i]);
        return slots;
    }
}
