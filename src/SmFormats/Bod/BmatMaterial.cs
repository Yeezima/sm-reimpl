namespace SmFormats.Bod;

/// <summary>
/// The parts of a ".bmat" material that rendering needs: its shader name and its texture parameters
/// (e.g. "DiffuseMap" → "painted_hazzard_large_dif", which resolves to a ".dds" entry).
/// The multiplayer armour's wargear shader (<c>character_wargear</c>) keeps its texture names in "WargearTextureParameter" structs (also
/// <c>PaintMask</c>, and <c>DirtColour</c> in <see cref="Vectors"/>). Field names come from the file's own string table.
/// <see cref="Floats"/> and <see cref="Vectors"/> hold the non-texture parameters (<c>FloatParameter</c> "Value",
/// <c>Vector4Parameter</c> "Value"; e.g. "SpecularPower", "SpecularColor", "EmissiveColor", "DerivedSpecParams").
/// <see cref="BlendMode"/> and <see cref="CullMode"/> are the material's own fields (255 in most: default; BlendMode 8 on the
/// 11 alpha-textured materials of Manufactorum; CullMode 0 on the two-sided ones).
/// </summary>
public sealed record BmatMaterial(string? Name, string? ShaderName, IReadOnlyDictionary<string, string> Textures,
    IReadOnlyDictionary<string, float>? FloatParameters = null, IReadOnlyDictionary<string, float[]>? VectorParameters = null,
    int? BlendMode = null, int? CullMode = null)
{
    public IReadOnlyDictionary<string, float> Floats => FloatParameters ?? EmptyFloats;
    public IReadOnlyDictionary<string, float[]> Vectors => VectorParameters ?? EmptyVectors;
    static readonly IReadOnlyDictionary<string, float> EmptyFloats = new Dictionary<string, float>();
    static readonly IReadOnlyDictionary<string, float[]> EmptyVectors = new Dictionary<string, float[]>();

    public static BmatMaterial From(BodDocument doc)
    {
        if (doc.Root is not BodStruct root || root.Class.Text != "Material")
            throw new InvalidDataException($"BOD root is '{(doc.Root as BodStruct)?.Class}', not a Material.");
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        var floats = new Dictionary<string, float>(StringComparer.Ordinal);
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (root["Parameters"] is BodList parameters)
            foreach (var item in parameters.Items)
            {
                if (item.Value is not BodStruct p || p["Name"] is not BodString { Text: { } name }) continue;
                if (p.Class.Text == "TextureParameter" && p["Value"] is BodString { Text: { Length: > 0 } texture }) textures[name] = texture;
                else if (p.Class.Text == "FloatParameter" && p["Value"] is BodFloat f) floats[name] = f.Value;
                else if (p.Class.Text == "Vector4Parameter" && p["Value"] is BodArray { Items: var v } && v.Count >= 1 && v.All(x => x is BodFloat))
                    vectors[name] = v.Select(x => ((BodFloat)x).Value).ToArray();
            }
        // The Chaos multiplayer armour's wargear shader: one parameter struct per texture type, each repeating the texture
        // names as fields (DiffuseMap, SpecularMap, LightPropMap, PaintMask); the first one's names are used.
        if (root["Parameters"] is BodList wargear)
            foreach (var item in wargear.Items)
                if (item.Value is BodStruct { Class.Text: "WargearTextureParameter" } wp)
                {
                    foreach (var key in new[] { "DiffuseMap", "SpecularMap", "LightPropMap", "PaintMask" })
                        if (wp[key] is BodString { Text: { Length: > 0 } t }) textures.TryAdd(key, t);
                    // DirtColour (RGBA): the tint of the dirt the paint mask's alpha channel selects.
                    if (wp["DirtColour"] is BodArray { Items: var dirt } && dirt.Count >= 3 && dirt.All(x => x is BodFloat))
                        vectors.TryAdd("DirtColour", dirt.Select(x => ((BodFloat)x).Value).ToArray());
                    break;
                }
        return new BmatMaterial((root["Name"] as BodString)?.Text, (root["ShaderName"] as BodString)?.Text, textures, floats, vectors,
            (root["BlendMode"] as BodInt)?.Value, (root["CullMode"] as BodInt)?.Value);
    }
}
