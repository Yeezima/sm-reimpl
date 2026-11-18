using System.Numerics;
using SmFormats.Bod;

namespace SmContent;

/// <summary>
/// A static level decal: a <c>ScreenSpaceDecal</c> object in a map layer (painted numbers and stripes, stains, light spots, running lights, trim). It projects its texture
/// along the node's local Y axis (the thin axis of its box) onto whatever surface lies inside the box.
/// </summary>
/// <param name="Decal">The <c>SSDName</c>: the <c>ssdecal</c> entry of the region archive that holds the material (<see cref="MapDecalDefinition"/>).</param>
/// <param name="Transform">Rotation and translation of the box (the node's world transform without its scale).</param>
/// <param name="Size">The box's extent (m) along its own X, Y (the projection axis) and Z: the node's scale. Kind <c>interpretation</c>: the layer's Scale is read as the box size, as the O3d <c>ssdecalbox</c> unit.</param>
/// <param name="Colour">The object's own colour tint; null unless <c>OverrideColor</c>.</param>
/// <param name="EmissiveColour">Null unless <c>OverrideEmissiveColor</c>.</param>
/// <param name="Opacity">Null unless <c>OverrideOpacity</c>.</param>
public sealed record PlacedDecal(string Name, string Decal, string Layer, Matrix4x4 Transform, Vector3 Size, Vector3? Colour, Vector3? EmissiveColour, float? Opacity, int DrawOrder);

/// <summary>An <c>ssdecal</c> entry (<c>ScreenSpaceDecalDescription</c>): the textures and default tint of a decal.</summary>
public sealed record MapDecalDefinition(string Name, string DiffuseMap, string NormalMap, string SpecularMap, string EmissiveMap, bool AdditiveBlend,
    Vector3 Colour, Vector3 EmissiveColour, float Opacity, float NormalThreshold, float Width, float Height, float Depth)
{
    /// <summary>Reads a <c>ScreenSpaceDecalDescription</c>; null when <paramref name="root"/> is some other struct.</summary>
    public static MapDecalDefinition? Read(string name, BodValue root)
    {
        if (root is not BodStruct { Class.Text: "ScreenSpaceDecalDescription" } s) return null;
        return new MapDecalDefinition(name, Text(s, "DiffuseMap"), Text(s, "NormalMap"), Text(s, "SpecularMap"), Text(s, "EmissiveMap"),
            s["AdditiveBlend"] is BodBool { Value: true }, Rgb(s["Color"], Vector3.One), Rgb(s["EmissiveColor"], Vector3.One),
            Number(s["Opacity"], 1f), Number(s["NormalThreshold"], 80f), Number(s["Width"], 1f), Number(s["Height"] ?? s["height"], 1f), Number(s["Depth"], 0.1f));
    }

    /// <summary>The material textures this decal names (the non-empty ones).</summary>
    public IEnumerable<string> TextureNames => new[] { DiffuseMap, NormalMap, SpecularMap, EmissiveMap }.Where(n => n.Length > 0);

    static string Text(BodStruct s, string field) => s[field] is BodString { Text: { } t } ? t : "";
    internal static float Number(BodValue? v, float fallback) => v switch { BodFloat f => f.Value, BodInt i => i.Value, _ => fallback };
    internal static Vector3 Rgb(BodValue? v, Vector3 fallback) =>
        v is BodArray { Items: [BodFloat r, BodFloat g, BodFloat b, ..] } ? new Vector3(r.Value, g.Value, b.Value) : fallback;
}
