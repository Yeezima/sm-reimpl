using System.Numerics;

namespace SmFormats.Havok;

/// <summary>
/// Static level collision from a map region's Havok packfile (<c>wrld.hkt</c>, <c>r000_main.hkt</c>; hk_2010, 32-bit).
/// The root container holds one variant per collision layer, named by its number ("2", "7", "8" on multiplayer maps),
/// each an <c>hkpMoppBvTreeShape</c> over (an <c>hkpListShape</c> of) an <c>hkpStorageExtendedMeshShape</c>.
/// That shape places instances of shared meshes: each triangle subpart (112 bytes) has a transform and a vertex and
/// triangle count, and the game stores which <c>MeshSubpartStorage</c> it instances in the w component of the
/// otherwise unused extrusion vector (supported by evidence: holds for every subpart of all 11 maps, and counts
/// match). Field offsets are in docs/discovery.md. The MOPP code is not read; callers build their own tree.
/// </summary>
public sealed record HavokCollisionLayer(string Name, Vector3[] Vertices, int[] Indices)
{
    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// One surface material id per triangle (M25a; empty when not read): the subpart's <c>userData</c>, or, for a subpart whose
    /// <c>userData</c> is -1 and that indexes materials per triangle (8-bit indices), the indexed storage material's
    /// <c>userData</c> (<see cref="HavokWorldCollision"/>; offsets in docs/discovery.md). Values on Manufactorum: 0, 3, 4 (as the
    /// map objects' <c>CShapeMesh.MaterialID</c>). Their meaning (an index into the game's surface list) is an interpretation:
    /// see <c>SmContent.Surfaces</c>.
    /// </summary>
    public int[] Materials { get; init; } = [];
}

public static class HavokWorldCollision
{
    const int SubpartSize = 112;
    const int StridingInt16 = 2;
    /// <summary>For the whole file, far above any multiplayer map (largest: about 250,000 triangles in all layers), to bound corrupt files.</summary>
    public const int MaxTriangles = 4_000_000, MaxVertices = 4_000_000;

    public static IReadOnlyList<HavokCollisionLayer> Read(HavokPackfile pack)
    {
        var classes = new Dictionary<int, string>();
        foreach (var o in pack.Objects) classes.TryAdd(o.Offset, o.ClassName);
        var root = pack.Objects.FirstOrDefault(o => o.ClassName == "hkRootLevelContainer")
            ?? throw new InvalidDataException("No hkRootLevelContainer.");
        var (variantsAt, variantCount) = pack.Array(root.Offset, 12);
        var layers = new List<HavokCollisionLayer>();
        // Shared by all layers: no shape is decoded twice (the game's layers never share shapes), so the file's size bounds the work.
        var visited = new HashSet<int>();
        long totalTriangles = 0, totalVertices = 0;
        for (int i = 0; i < variantCount; i++)
        {
            int at = variantsAt!.Value + 12 * i;
            string name = pack.String(at) ?? $"variant{i}";
            if (pack.Pointer(at + 8) is not int shape) continue;
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            var materials = new List<int>();
            CollectShape(pack, classes, shape, vertices, indices, materials, visited, 0);
            totalTriangles += indices.Count / 3; totalVertices += vertices.Count;
            if (totalTriangles > MaxTriangles || totalVertices > MaxVertices)
                throw new InvalidDataException($"Collision exceeds {MaxTriangles:N0} triangles or {MaxVertices:N0} vertices in all layers.");
            if (indices.Count > 0) layers.Add(new HavokCollisionLayer(name, [.. vertices], [.. indices]) { Materials = [.. materials] });
        }
        return layers;
    }

    /// <summary>Each shape is visited once per file: a shape referenced twice (or a cycle) is rejected, not repeated.</summary>
    static void CollectShape(HavokPackfile pack, Dictionary<int, string> classes, int offset, List<Vector3> vertices, List<int> indices, List<int> materials,
        HashSet<int> visited, int depth)
    {
        if (depth > 8) throw new InvalidDataException("Collision shape nesting deeper than 8.");
        if (!visited.Add(offset)) throw new InvalidDataException($"Collision shape at {offset} is referenced more than once.");
        string? cls = classes.GetValueOrDefault(offset);
        switch (cls)
        {
            case "hkpMoppBvTreeShape":
                if (pack.Pointer(offset + 52) is int child) CollectShape(pack, classes, child, vertices, indices, materials, visited, depth + 1);
                break;
            case "hkpListShape":
            {
                var (childrenAt, count) = pack.Array(offset + 24, 16);
                for (int i = 0; i < count; i++)
                    if (pack.Pointer(childrenAt!.Value + 16 * i) is int c) CollectShape(pack, classes, c, vertices, indices, materials, visited, depth + 1);
                break;
            }
            case "hkpStorageExtendedMeshShape":
                CollectMesh(pack, offset, vertices, indices, materials);
                break;
            default:
                throw new NotSupportedException($"Collision shape class '{cls}' at {offset} is not supported yet.");
        }
    }

    /// <summary>
    /// Subpart fields: +20 numTriangleShapes, +28 numVertices, +36 vertexStriding (16), +44 indexStriding (u16, 8),
    /// +46 stridingType (2 = 16-bit indices), +60 storage index (float), +64 hkQsTransform. Storage: +8 vertices
    /// (hkVector4), +32 indices16 (4 per triangle, the fourth is padding).
    /// Materials (M25a): subpart +1 material index striding type (1 = 8-bit indices), +10 numMaterials (u16), +16 userData
    /// (i32); storage +56 materialIndices (u8 per triangle), +68 materials (12 bytes each: filter info, restitution and
    /// friction as halves, userData i32 at +8).
    /// </summary>
    static void CollectMesh(HavokPackfile pack, int mesh, List<Vector3> vertices, List<int> indices, List<int> materials)
    {
        var (subpartsAt, subpartCount) = pack.Array(mesh + 184, SubpartSize);
        var (_, shapeSubparts) = pack.Array(mesh + 196, 4);
        var (_, weldingCount) = pack.Array(mesh + 208, 2);
        var (storagesAt, storageCount) = pack.Array(mesh + 240, 4);
        // Convex shape subparts are not read; none exist in the 11 multiplayer maps, so refuse rather than drop them.
        if (shapeSubparts != 0) throw new NotSupportedException($"Mesh at {mesh} has {shapeSubparts} shape subparts; only triangle subparts are supported.");
        int firstIndex = indices.Count;
        for (int s = 0; s < subpartCount; s++)
        {
            int at = subpartsAt!.Value + SubpartSize * s;
            int triangles = pack.ReadInt32(at + 20), vertexCount = pack.ReadInt32(at + 28);
            if (triangles < 0 || vertexCount < 0 || (long)indices.Count / 3 + triangles > MaxTriangles || (long)vertices.Count + vertexCount > MaxVertices)
                throw new InvalidDataException($"Subpart {s}: {triangles} triangles and {vertexCount} vertices exceed the limits ({MaxTriangles:N0} each per layer).");
            int stridingType = pack.ReadByte(at + 46), indexStriding = (ushort)pack.ReadInt16(at + 44);
            if (stridingType != StridingInt16 || indexStriding != 8)
                throw new NotSupportedException($"Subpart {s}: index striding type {stridingType} / {indexStriding} bytes is not supported yet.");
            float storageField = pack.ReadSingle(at + 60);
            if (!float.IsFinite(storageField) || storageField < 0 || storageField >= storageCount || storageField != MathF.Floor(storageField))
                throw new InvalidDataException($"Subpart {s}: storage index {storageField} outside 0..{storageCount - 1}.");
            int storage = pack.Pointer(storagesAt!.Value + 4 * (int)storageField)
                ?? throw new InvalidDataException($"Subpart {s}: storage {storageField} has no object.");
            var (vertexAt, storedVertices) = pack.Array(storage + 8, 16);
            var (indexAt, storedIndices) = pack.Array(storage + 32, 2);
            if (storedVertices != vertexCount || storedIndices != 4L * triangles || (triangles > 0 && (indexAt is null || vertexAt is null)))
                throw new InvalidDataException($"Subpart {s}: {vertexCount} vertices and {triangles} triangles, storage {storageField} has {storedVertices} and {storedIndices} indices.");

            var transform = HavokTransform.Read(pack, at + 64);
            int first = vertices.Count;
            for (int v = 0; v < vertexCount; v++)
            {
                int p = vertexAt!.Value + 16 * v;
                var local = new Vector3(pack.ReadSingle(p), pack.ReadSingle(p + 4), pack.ReadSingle(p + 8));
                vertices.Add(transform.Translation + Vector3.Transform(transform.Scale * local, transform.Rotation));
            }
            for (int t = 0; t < triangles; t++)
                for (int k = 0; k < 3; k++)
                {
                    int index = (ushort)pack.ReadInt16(indexAt!.Value + 8 * t + 2 * k);
                    if (index >= vertexCount) throw new InvalidDataException($"Subpart {s}, triangle {t}: index {index} of {vertexCount} vertices.");
                    indices.Add(first + index);
                }

            // Surface materials: per triangle through the storage's 8-bit indices when the subpart says so, else the subpart's own.
            int userData = pack.ReadInt32(at + 16);
            var (materialIndexAt, materialIndexCount) = pack.Array(storage + 56, 1);
            var (materialAt, materialCount) = pack.Array(storage + 68, 12);
            bool perTriangle = pack.ReadByte(at + 1) == 1 && (ushort)pack.ReadInt16(at + 10) > 0 && materialCount > 0
                && materialIndexCount == triangles && materialIndexAt is not null && materialAt is not null;
            for (int t = 0; t < triangles; t++)
            {
                if (!perTriangle) { materials.Add(userData); continue; }
                int m = pack.ReadByte(materialIndexAt!.Value + t);
                materials.Add(m < materialCount ? pack.ReadInt32(materialAt!.Value + 12 * m + 8) : -1);
            }
        }
        // The welding info has one entry per triangle (holds on all 11 maps): a cheap cross-check of the counts.
        if (weldingCount != 0 && weldingCount != (indices.Count - firstIndex) / 3)
            throw new InvalidDataException($"Mesh at {mesh}: {(indices.Count - firstIndex) / 3} triangles but {weldingCount} welding entries.");
    }
}
