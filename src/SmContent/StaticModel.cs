using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// An engine-neutral static model: one original ".dcm" mesh with, for each draw range, its material and the raw
/// bytes of its diffuse ".dds" texture. Coordinates stay as in the files (Z up; triangles clockwise seen from the front).
/// Data lives in memory only; nothing is written anywhere.
/// </summary>
public sealed record StaticModel(string Archive, string MeshName, DcmMesh Mesh, IReadOnlyList<StaticModelSurface> Surfaces)
{
    /// <summary>
    /// Raw DDS bytes of every texture the surfaces' materials name as diffuse, normal, specular or emissive map
    /// (<see cref="MapScene.TextureParameters"/>), by texture name, plus an inferred normal map (see <see cref="Load"/>).
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> Textures { get; private set; } = new Dictionary<string, byte[]>();

    /// <summary>Drops <see cref="Textures"/> once a renderer has uploaded them (the raw DDS bytes are not needed afterwards).</summary>
    public void ReleaseTextures() => Textures = new Dictionary<string, byte[]>();

    /// <summary>
    /// Reads one mesh from an archive, plus every material it uses and their diffuse textures, in two passes over the
    /// archive's single zlib stream. Missing materials or textures leave the surface's texture null and add a note.
    /// </summary>
    /// <remarks>
    /// The Chaos multiplayer armour's wargear material names no normal map, but its package holds "&lt;mesh&gt;_nrm":
    /// when the material has no <c>NormalMap</c> and that texture exists, it is used as the material's normal map
    /// (**interpretation**, by naming; the Space Marine armour's material names its own).
    /// </remarks>
    /// <param name="textureArchives">Further archives searched, in order, for diffuse textures the main archive lacks (the Chaos
    /// multiplayer armour keeps them in separate "_wg" / "_tt1" / "_tt2" packages).</param>
    public static StaticModel Load(string archivePath, string meshName, List<string>? notes = null, IEnumerable<string>? textureArchives = null)
    {
        using var archive = ObpkArchive.Open(archivePath);
        DcmMesh? mesh = null;
        var materials = new Dictionary<string, BmatMaterial>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(
            e => archive.GroupName(e) is "bmat" || (archive.GroupName(e) == "dcm" && e.FileName.Equals(meshName, StringComparison.OrdinalIgnoreCase)),
            (e, data) =>
            {
                if (archive.GroupName(e) == "dcm") mesh ??= DcmMesh.Read(data);
                else
                {
                    try { materials[e.FileName] = BmatMaterial.From(BodDocument.Read(data)); }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { notes?.Add($"material '{e.FileName}' unreadable: {ex.Message}"); }
                }
            });
        if (mesh is null) throw new FileNotFoundException($"Mesh '{meshName}.dcm' not found in {Path.GetFileName(archivePath)}.");

        var ranges = mesh.Blocks.SelectMany((b, bi) => b.Materials.Select(r => (bi, r))).ToList();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string inferredNormal = meshName + "_nrm";
        foreach (var (_, r) in ranges)
            if (materials.TryGetValue(r.Name, out var m))
            {
                foreach (var parameter in MapScene.TextureParameters)
                    if (m.Textures.TryGetValue(parameter, out var tex)) wanted.Add(tex);
                if (m.Textures.TryGetValue("PaintMask", out var paintMask)) wanted.Add(paintMask);   // the armour's paint mask (see PaintSchemes)
                if (m.Textures.ContainsKey("PaintMask") && m.Textures.TryGetValue("LightPropMap", out var lightProp)) wanted.Add(lightProp);   // M40-T13: the painted armour's _lp
                if (!m.Textures.ContainsKey("NormalMap")) wanted.Add(inferredNormal);
            }

        var textures = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(
            e => archive.GroupName(e) == "dds" && wanted.Contains(e.FileName) && !textures.ContainsKey(e.FileName),
            (e, data) => textures.TryAdd(e.FileName, data.ToArray()));

        foreach (var extra in textureArchives ?? [])
        {
            if (!wanted.Any(w => !textures.ContainsKey(w)) || !File.Exists(extra)) continue;
            using var other = ObpkArchive.Open(extra);
            other.ReadEntries(
                e => other.GroupName(e) == "dds" && wanted.Contains(e.FileName) && !textures.ContainsKey(e.FileName),
                (e, data) => textures.TryAdd(e.FileName, data.ToArray()));
        }

        if (textures.ContainsKey(inferredNormal))
            foreach (var name in materials.Keys.ToList())
                if (!materials[name].Textures.ContainsKey("NormalMap"))
                    materials[name] = materials[name] with { Textures = new Dictionary<string, string>(materials[name].Textures) { ["NormalMap"] = inferredNormal } };

        var surfaces = new List<StaticModelSurface>(ranges.Count);
        foreach (var (bi, r) in ranges)
        {
            materials.TryGetValue(r.Name, out var m);
            string? diffuse = null;
            if (m is null) notes?.Add($"material '{r.Name}' not in archive");
            else if (!m.Textures.TryGetValue("DiffuseMap", out diffuse)) notes?.Add($"material '{r.Name}' has no DiffuseMap");
            byte[]? dds = diffuse is not null && textures.TryGetValue(diffuse, out var bytes) ? bytes : null;
            if (diffuse is not null && dds is null) notes?.Add($"texture '{diffuse}' not in archive");
            surfaces.Add(new StaticModelSurface(bi, r, m, diffuse, dds));
        }
        return new StaticModel(Path.GetFileName(archivePath), meshName, mesh, surfaces) { Textures = textures };
    }
}

/// <param name="BlockIndex">Index into <see cref="DcmMesh.Blocks"/> whose vertex streams this surface draws from.</param>
/// <param name="DiffuseDds">Raw DDS bytes of the material's DiffuseMap, or null if unresolved.</param>
public sealed record StaticModelSurface(int BlockIndex, DcmMaterialRange Range, BmatMaterial? Material, string? DiffuseName, byte[]? DiffuseDds);
