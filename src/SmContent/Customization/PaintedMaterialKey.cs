using System.Globalization;
using SmSim;

namespace SmContent.Customization;

/// <summary>
/// The cache keys of the painted armour materials (engine-free so they can be tested): two surfaces with the same material, colours, pattern, decal and lens
/// get one shared GPU material, so sixteen players in the same look cost one material per surface, not sixteen.
/// </summary>
public static class PaintedMaterialKey
{
    /// <summary>
    /// The key of a painted surface. <paramref name="surface"/> names the surface (its piece package and material); <paramref name="decal"/> is <see cref="DecalKeyOf"/>;
    /// <paramref name="lens"/> only for a helmet surface that has lens rectangles (null: the original look), so every other surface shares its material whatever the lens.
    /// </summary>
    public static string Of(string surface, ResolvedPaint? paint, string? decal, (byte R, byte G, byte B)? lens)
    {
        var sb = new System.Text.StringBuilder(surface).Append('|');
        if (paint is null) sb.Append("unpainted");
        else
        {
            // The pattern flag is not in the key: with it off the resolver already made the secondary equal to the primary, and the shader reads only the colours.
            Append(sb, paint.Primary); Append(sb, paint.Secondary); Append(sb, paint.Tertiary);
        }
        sb.Append('|').Append(decal ?? "").Append('|');
        if (lens is { } l) sb.Append(l.R).Append(',').Append(l.G).Append(',').Append(l.B);
        return sb.ToString();
    }

    static void Append(System.Text.StringBuilder sb, ResolvedColour? c)
    {
        if (c is null) { sb.Append("-;"); return; }
        foreach (var v in c.Diffuse) sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        sb.Append('/');
        foreach (var v in c.Specular) sb.Append(v.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(';');
    }

    /// <summary>
    /// The decal key of a pad: "" for none, "b:&lt;texture&gt;" for a badge, "e:&lt;hash&gt;" for an emblem whose bytes are available (<paramref name="emblemAvailable"/>),
    /// otherwise the fallback badge's key (the pad shows it until the emblem has arrived).
    /// </summary>
    public static string DecalKeyOf(ResolvedDecal decal, bool emblemAvailable) => decal.Kind switch
    {
        DecalKind.Badge when decal.Badge is { IsBlank: false } b => "b:" + b.Texture,
        DecalKind.Emblem when emblemAvailable && !decal.Emblem.IsNone => "e:" + decal.Emblem.ToHex(),
        DecalKind.Emblem when decal.Badge is { IsBlank: false } b => "b:" + b.Texture,
        _ => "",
    };
}
