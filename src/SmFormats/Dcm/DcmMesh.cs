using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SmFormats.Dcm;

/// <summary>
/// Read-only reader for ".dcm" meshes (Space Marine 2011, PC). Layout per docs/discovery.md ("DCM"):
/// a mesh is a list of geometry blocks; each block starts with a type byte.
/// Type 0 (static) is supported by evidence on every static mesh of the multiplayer maps; type 1 (skinned) on
/// every skinned block of the game's packages; type 2 (a static block followed by a trailer naming a further body range,
/// <see cref="DcmBlock.Simulation"/>) on the cables and ammunition belts of the heavy multiplayer weapons (M24) and the armour's belts, cloth and fur (fix/armour-pieces). Other types are
/// rejected with <see cref="NotSupportedException"/>.
/// </summary>
public sealed class DcmMesh
{
    const int StaticHeaderSize = 58, SkinnedHeaderSize = 61;
    /// <summary>Bytes after a type-2 block's material records (see <see cref="DcmSimulationData"/>).</summary>
    public const int SimulationTrailerSize = 16;
    /// <summary>Bytes after the material record of the armour's type-2 variant (belts, cloth, fur; its material record's last u32 is 1, the cables' 2).</summary>
    public const int ShortSimulationTrailerSize = 12;
    public const int StaticStride = 60;          // 12 position bytes + 48 attribute bytes
    public const int AttributeStride = 48;
    /// Skinned per-vertex streams: 32 (position, weights, bone indices), 28 (normal, tangent, sign), 84 (see docs).
    public const int SkinPositionStride = 32, SkinFrameStride = 28, SkinExtraStride = 84;

    public IReadOnlyList<DcmBlock> Blocks { get; }

    DcmMesh(IReadOnlyList<DcmBlock> blocks) => Blocks = blocks;

    /// <exception cref="InvalidDataException">The mesh is truncated or inconsistent (sizes, offsets, indices, records).</exception>
    /// <exception cref="NotSupportedException">A block type or layout not decoded yet (unknown types, other type-2 layouts,
    /// static strides other than 60).</exception>
    public static DcmMesh Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12) throw new InvalidDataException($"File is {d.Length} bytes; too small for a dcm header.");
        int headerSize = I32(d, 0), bodySize = I32(d, 4), blockCount = I32(d, 8);
        if (headerSize < 12 || bodySize < 0 || (long)headerSize + bodySize != d.Length)
            throw new InvalidDataException($"Header {headerSize} + body {bodySize} != file length {d.Length}.");
        if (blockCount is < 1 or > 4096) throw new InvalidDataException($"Implausible block count {blockCount}.");
        var body = d[headerSize..];

        var blocks = new List<DcmBlock>(blockCount);
        int p = 12;
        long expectedOffset = 0;
        for (int b = 0; b < blockCount; b++)
        {
            Need(p, 1, headerSize, "block type");
            byte type = d[p];
            var block = type switch
            {
                0 => ReadStatic(d, body, ref p, b, headerSize, bodySize, ref expectedOffset),
                2 => ReadSimulated(d, body, ref p, b, headerSize, bodySize, ref expectedOffset),
                1 => ReadSkinned(d, body, ref p, b, headerSize, bodySize, ref expectedOffset),
                _ => throw new NotSupportedException($"Block {b}: type {type} is not supported yet (types 0 static, 1 skinned and 2 simulated are)."),
            };
            blocks.Add(block);
        }
        if (p != headerSize) throw new InvalidDataException($"Header has {headerSize - p} unread bytes after the last block.");
        if (expectedOffset != bodySize) throw new InvalidDataException($"Blocks cover {expectedOffset} of {bodySize} body bytes.");
        return new DcmMesh(blocks);
    }

    /// <summary>
    /// Type 2: the variants with a 16-byte trailer (the weapons' cables) and a 12-byte one (the armour's belts, cloth and fur) are decoded (<see cref="DcmSimulationData"/>);
    /// the install's banners and props use other type-2 layouts, which stay <see cref="NotSupportedException"/> as before.
    /// </summary>
    static DcmBlock ReadSimulated(ReadOnlySpan<byte> d, ReadOnlySpan<byte> body, ref int p, int b, int headerSize, int bodySize, ref long expectedOffset)
    {
        int start = p;
        long expected = expectedOffset;
        try
        {
            var block = ReadStatic(d, body, ref p, b, headerSize, bodySize, ref expected);
            expectedOffset = expected;
            return block;
        }
        catch (InvalidDataException e)
        {
            p = start;
            throw new NotSupportedException($"Block {b}: this type-2 layout is not supported yet ({e.Message})", e);
        }
    }

    static DcmBlock ReadStatic(ReadOnlySpan<byte> d, ReadOnlySpan<byte> body, ref int p, int b, int headerSize, int bodySize, ref long expectedOffset)
    {
        Need(p, StaticHeaderSize, headerSize, "block header");
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]);
        bool simulated = d[p] == 2;
        int vertexCount = I32(d, p + 2), stride = I32(d, p + 6), indexCount = I32(d, p + 10);
        int bodyOffset = I32(d, p + 14), indexSize = I32(d, p + 18), materialCount = I32(d, p + 22);
        if (stride != StaticStride || indexSize != 2)
            throw new NotSupportedException($"Block {b}: static stride {stride}, index size {indexSize}; expected 60 and 2.");
        CheckCounts(b, vertexCount, indexCount, materialCount, 0);
        var (min, max) = Bounds(d, p + 26);
        int positionsOffset = I32(d, p + 50), attributesOffset = I32(d, p + 54);
        if (bodyOffset != expectedOffset) throw new InvalidDataException($"Block {b}: body offset {bodyOffset}, expected {expectedOffset}.");
        if ((long)positionsOffset != bodyOffset + 2L * indexCount || (long)attributesOffset != positionsOffset + 12L * vertexCount)
            throw new InvalidDataException($"Block {b}: stream offsets {positionsOffset}/{attributesOffset} do not follow the index and position streams.");
        long blockEnd = (long)attributesOffset + (long)AttributeStride * vertexCount;
        if (blockEnd > bodySize) throw new InvalidDataException($"Block {b}: data ends at {blockEnd}, past the body ({bodySize}).");
        p += StaticHeaderSize;

        var materials = ReadMaterials(d, ref p, b, materialCount, headerSize, vertexCount, indexCount);
        DcmSimulationData? simulation = null;
        if (simulated)
        {
            // Type 2 (docs/discovery.md "Type 2"): the single material record's last u32 is a count n, and n u32 follow it, then the body offset and size
            // of a further range that starts at the next 16-byte boundary after the attribute stream and ends the block. The heavy weapons' cables (M24)
            // have n = 2 (entries 0, 1: the 16-byte trailer), the armour's belts, cloth and fur (fix/armour-pieces) n = 1 (entry 0: 12 bytes). One
            // reading for both, so the variants cannot overlap; the entries' meaning is not known.
            bool Fits(int offset, int size) => offset >= blockEnd && offset - blockEnd < 16 && size >= 0 && (long)offset + size <= bodySize;
            uint n = materials.Count == 1 ? materials[0].Unknown6 : uint.MaxValue;
            if (n is not (1 or 2))
                throw new InvalidDataException($"Block {b}: type-2 block with {materials.Count} material record(s) and entry count {(n == uint.MaxValue ? "-" : n.ToString())}; only one record with 1 or 2 entries is known.");
            int trailer = 4 * (int)n + 8;
            Need(p, trailer, headerSize, "type-2 trailer");
            int at = p + 4 * (int)n;
            if (!Fits(I32(d, at), I32(d, at + 4)))
                throw new InvalidDataException($"Block {b}: type-2 range {I32(d, at)}+{I32(d, at + 4)} does not follow the attribute stream ending at {blockEnd}.");
            var entries = new uint[n];
            for (int k = 0; k < n; k++) entries[k] = (uint)I32(d, p + 4 * k);
            simulation = new DcmSimulationData(entries[0], n == 2 ? entries[1] : n, I32(d, at), I32(d, at + 4)) { TrailerSize = trailer, Entries = entries };
            p += trailer;
            blockEnd = (long)simulation.Offset + simulation.Size;
        }
        var indices = ReadIndices(body, b, bodyOffset, indexCount, vertexCount);
        var positions = new Vector3[vertexCount];
        for (int v = 0; v < vertexCount; v++) positions[v] = V3(body, positionsOffset + 12 * v);
        var normals = new Vector3[vertexCount];
        var tangents = new Vector4[vertexCount];
        var uv0 = new Vector2[vertexCount];
        var uv1 = new Vector2[vertexCount];
        var colors = new uint[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int a = attributesOffset + AttributeStride * v;
            normals[v] = V3(body, a);
            tangents[v] = new Vector4(V3(body, a + 12), F32(body, a + 24));
            uv0[v] = new Vector2(F32(body, a + 28), F32(body, a + 32));
            uv1[v] = new Vector2(F32(body, a + 36), F32(body, a + 40));
            colors[v] = BinaryPrimitives.ReadUInt32LittleEndian(body[(a + 44)..]);
        }
        expectedOffset = blockEnd;
        return new DcmBlock(flags, min, max, materials, indices, positions, normals, tangents, uv0, uv1, colors, Skin: null) { Simulation = simulation };
    }

    static DcmBlock ReadSkinned(ReadOnlySpan<byte> d, ReadOnlySpan<byte> body, ref int p, int b, int headerSize, int bodySize, ref long expectedOffset)
    {
        Need(p, SkinnedHeaderSize, headerSize, "skinned block header");
        int vertexCount = I32(d, p + 1), stride = I32(d, p + 5), indexCount = I32(d, p + 9);
        int bodyOffset = I32(d, p + 13), materialCount = I32(d, p + 17), boneCount = I32(d, p + 21);
        CheckCounts(b, vertexCount, indexCount, materialCount, boneCount);
        var (min, max) = Bounds(d, p + 25);
        int positionsOffset = I32(d, p + 49), framesOffset = I32(d, p + 53), extraOffset = I32(d, p + 57);
        if (bodyOffset != expectedOffset) throw new InvalidDataException($"Block {b}: body offset {bodyOffset}, expected {expectedOffset}.");
        if ((long)positionsOffset != bodyOffset + 2L * indexCount
            || (long)framesOffset != positionsOffset + (long)SkinPositionStride * vertexCount
            || (long)extraOffset != framesOffset + (long)SkinFrameStride * vertexCount)
            throw new InvalidDataException($"Block {b}: skinned stream offsets do not follow each other.");
        long blockEnd = extraOffset + (long)SkinExtraStride * vertexCount;
        if (blockEnd > bodySize) throw new InvalidDataException($"Block {b}: data ends at {blockEnd}, past the body ({bodySize}).");
        p += SkinnedHeaderSize;

        var bones = new List<DcmBone>(boneCount);
        for (int k = 0; k < boneCount; k++)
        {
            Need(p, 3, headerSize, "bone name");
            if (d[p] != 0xFF) throw new InvalidDataException($"Block {b} bone {k}: expected 0xFF at header offset {p}.");
            int len = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 1)..]);
            Need(p + 3, len + 68, headerSize, "bone record");
            string name = Encoding.ASCII.GetString(d.Slice(p + 3, len));
            int q = p + 3 + len;
            var m = new Matrix4x4(
                F32(d, q), F32(d, q + 4), F32(d, q + 8), F32(d, q + 12),
                F32(d, q + 16), F32(d, q + 20), F32(d, q + 24), F32(d, q + 28),
                F32(d, q + 32), F32(d, q + 36), F32(d, q + 40), F32(d, q + 44),
                F32(d, q + 48), F32(d, q + 52), F32(d, q + 56), F32(d, q + 60));
            bones.Add(new DcmBone(name, m, I32(d, q + 64)));
            p = q + 68;
        }
        var materials = ReadMaterials(d, ref p, b, materialCount, headerSize, vertexCount, indexCount, boneCount);
        int[] boneList;
        if (materialCount == 0)
        {
            // No material record, so no palette to hold the list: the block's bone list stands alone (the layout before the palettes were found).
            Need(p, 4 * boneCount, headerSize, "bone list");
            boneList = new int[boneCount];
            for (int k = 0; k < boneCount; k++) boneList[k] = I32(d, p + 4 * k);
            p += 4 * boneCount;
        }
        else if (materialCount == 1) boneList = [.. materials[0].BonePalette!];
        else boneList = [.. materials.SelectMany(m => m.BonePalette!).Distinct()];
        // Every vertex of a draw range indexes that range's palette (docs/discovery.md, "Bone palettes").
        var vertexRange = new int[vertexCount];
        Array.Fill(vertexRange, -1);
        for (int m = 0; m < materials.Count; m++)
            for (int v = materials[m].FirstVertex; v < materials[m].FirstVertex + materials[m].VertexCount; v++)
            {
                if (vertexRange[v] >= 0 && !materials[vertexRange[v]].BonePalette!.SequenceEqual(materials[m].BonePalette!))
                    throw new InvalidDataException($"Block {b}: vertex {v} lies in the draw ranges of materials {vertexRange[v]} and {m}, whose bone palettes differ.");
                vertexRange[v] = m;
            }

        var indices = ReadIndices(body, b, bodyOffset, indexCount, vertexCount);
        if (materialCount >= 2)
        {
            // With several palettes a triangle must draw only vertices of its own range, or its vertices would use another range's palette
            // (holds for all 2,757,096 indices of the install's multi-material skinned blocks).
            foreach (var m in materials)
                for (int i = m.StartIndex; i < m.StartIndex + m.IndexCount; i++)
                    if (indices[i] < m.FirstVertex || indices[i] >= m.FirstVertex + m.VertexCount)
                        throw new InvalidDataException($"Block {b} material '{m.Name}': index {indices[i]} at {i} lies outside its vertices {m.FirstVertex}..{m.FirstVertex + m.VertexCount - 1}.");
        }
        var positions = new Vector3[vertexCount];
        var weights = new Vector4[vertexCount];
        var boneIndices = new byte[4 * vertexCount];
        var normals = new Vector3[vertexCount];
        var tangents = new Vector4[vertexCount];
        var uv0 = new Vector2[vertexCount];
        var uv1 = new Vector2[vertexCount];
        var colors = new uint[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int a = positionsOffset + SkinPositionStride * v;
            positions[v] = V3(body, a);
            weights[v] = new Vector4(F32(body, a + 12), F32(body, a + 16), F32(body, a + 20), F32(body, a + 24));
            var palette = vertexRange[v] >= 0 ? materials[vertexRange[v]].BonePalette : null;
            if (palette is null && materialCount >= 2 && (weights[v].X > 0f || weights[v].Y > 0f || weights[v].Z > 0f || weights[v].W > 0f))
                throw new InvalidDataException($"Block {b}: weighted vertex {v} lies in no material's vertex range, so it has no bone palette.");   // none in the install
            for (int k = 0; k < 4; k++)
            {
                byte bi = body[a + 28 + k];
                bool weighted = Component(weights[v], k) > 0f;
                if (palette is not null && bi < palette.Count)
                    boneIndices[4 * v + k] = (byte)palette[bi];   // palette slot -> the block's bone index (entries are checked < boneCount <= 256)
                else
                {
                    // Not covered by a palette (or an unweighted slot past it): a weighted use is an error, an unweighted one stays as stored.
                    if (weighted && (palette is not null || bi >= boneCount))
                        throw new InvalidDataException($"Block {b}: vertex {v} uses bone slot {bi} of {(palette is null ? boneCount : palette.Count)}.");
                    boneIndices[4 * v + k] = bi;
                }
            }
            int f = framesOffset + SkinFrameStride * v;
            normals[v] = V3(body, f);
            tangents[v] = new Vector4(V3(body, f + 12), F32(body, f + 24));
            int x = extraOffset + 20 * v;   // first 20-byte record of the extra stream: uv0, uv1, colour
            uv0[v] = new Vector2(F32(body, x), F32(body, x + 4));
            uv1[v] = new Vector2(F32(body, x + 8), F32(body, x + 12));
            colors[v] = BinaryPrimitives.ReadUInt32LittleEndian(body[(x + 16)..]);
        }
        expectedOffset = blockEnd;
        var skin = new DcmSkin(stride, bones, boneList, weights, boneIndices);
        return new DcmBlock(1, min, max, materials, indices, positions, normals, tangents, uv0, uv1, colors, skin);
    }

    /// <param name="paletteBones">-1 for static blocks (28-byte records only). For skinned blocks the block's bone count: each record is
    /// followed by <c>Unknown6</c> u32, its bone palette, each entry a bone index of the block.</param>
    static List<DcmMaterialRange> ReadMaterials(ReadOnlySpan<byte> d, ref int p, int b, int materialCount, int headerSize, int vertexCount, int indexCount, int paletteBones = -1)
    {
        var materials = new List<DcmMaterialRange>(materialCount);
        for (int m = 0; m < materialCount; m++)
        {
            Need(p, 3, headerSize, "material name");
            if (d[p] != 0xFF) throw new InvalidDataException($"Block {b} material {m}: expected 0xFF at header offset {p}.");
            int len = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 1)..]);
            Need(p + 3, len + 28, headerSize, "material record");
            string name = Encoding.ASCII.GetString(d.Slice(p + 3, len));
            int q = p + 3 + len;
            var range = new DcmMaterialRange(name, I32(d, q), I32(d, q + 12), I32(d, q + 8), I32(d, q + 20), I32(d, q + 16),
                (uint)I32(d, q + 4), (uint)I32(d, q + 24));
            if (range.StartIndex < 0 || range.IndexCount < 0 || (long)range.StartIndex + range.IndexCount > indexCount
                || range.FirstVertex < 0 || range.VertexCount < 0 || (long)range.FirstVertex + range.VertexCount > vertexCount)
                throw new InvalidDataException($"Block {b} material '{name}': draw range outside the block.");
            p = q + 28;
            if (paletteBones >= 0)
            {
                if (range.Unknown6 > (uint)paletteBones) throw new InvalidDataException($"Block {b} material '{name}': bone palette of {range.Unknown6} entries, the block has {paletteBones} bones.");
                int count = (int)range.Unknown6;
                Need(p, 4 * count, headerSize, "bone palette");
                var palette = new int[count];
                for (int k = 0; k < count; k++)
                {
                    palette[k] = I32(d, p + 4 * k);
                    if ((uint)palette[k] >= (uint)paletteBones) throw new InvalidDataException($"Block {b} material '{name}': palette entry {k} is bone {palette[k]} of {paletteBones}.");
                }
                p += 4 * count;
                range = range with { BonePalette = palette };
            }
            materials.Add(range);
        }
        return materials;
    }

    static ushort[] ReadIndices(ReadOnlySpan<byte> body, int b, int bodyOffset, int indexCount, int vertexCount)
    {
        var indices = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++)
        {
            indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(body[(bodyOffset + 2 * i)..]);
            if (indices[i] >= vertexCount) throw new InvalidDataException($"Block {b}: index {indices[i]} at {i} >= vertex count {vertexCount}.");
        }
        return indices;
    }

    static void CheckCounts(int b, int vertexCount, int indexCount, int materialCount, int boneCount)
    {
        if (vertexCount < 0 || indexCount < 0 || materialCount is < 0 or > 4096 || boneCount is < 0 or > 256)
            throw new InvalidDataException($"Block {b}: implausible counts (vertices {vertexCount}, indices {indexCount}, materials {materialCount}, bones {boneCount}).");
    }

    static (Vector3, Vector3) Bounds(ReadOnlySpan<byte> d, int at) => (V3(d, at), V3(d, at + 12));
    static float Component(Vector4 v, int k) => k switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };
    static void Need(int pos, int count, int limit, string what)
    {
        if (count < 0 || (long)pos + count > limit) throw new InvalidDataException($"Truncated {what} at header offset {pos}.");
    }
    static int I32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt32LittleEndian(s[o..]);
    static float F32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleLittleEndian(s[o..]);
    static Vector3 V3(ReadOnlySpan<byte> s, int o) => new(F32(s, o), F32(s, o + 4), F32(s, o + 8));
}

/// <summary>One geometry block: its own index, position, and attribute streams.</summary>
/// <param name="Flags">Static blocks: u16 before the counts (its low byte is the type byte), 0 in every type-0 sample and 2 on
/// type-2 blocks. Skinned blocks: 1 (the type byte).</param>
/// <param name="Tangents">xyz tangent, w handedness (+1 or -1).</param>
/// <param name="Uv1">Second UV set (lightmap or detail; not yet determined).</param>
/// <param name="Colors">Per-vertex u32, bytes B, G, R, A (D3DCOLOR order; evidence: the rust-coloured junk pile reads warm only this way). RGB is mostly one grey tint (white = none); A (byte 3, 255 = default) is the layer weight of two-layer materials (layer 1 at 255), see docs/discovery.md.</param>
/// <param name="Skin">Skinning data for type-1 blocks, otherwise null.</param>
public sealed record DcmBlock(
    ushort Flags, Vector3 BoundsMin, Vector3 BoundsMax, IReadOnlyList<DcmMaterialRange> Materials,
    ushort[] Indices, Vector3[] Positions, Vector3[] Normals, Vector4[] Tangents, Vector2[] Uv0, Vector2[] Uv1, uint[] Colors,
    DcmSkin? Skin)
{
    /// <summary>Type-2 blocks only: the trailing body range (its content is not decoded). Null on other blocks.</summary>
    public DcmSimulationData? Simulation { get; init; }
}

/// <summary>
/// The trailer of a type-2 block: a body range after the block's static streams. Every block with this variant (21 in the
/// install) is a heavy weapon's cable or ammunition belt whose object-tree node has a "_sim" sibling (e.g. GEO_belt and GEO_belt_sim), so the range is
/// **interpreted** as cloth or rope simulation data; its layout is not decoded.
/// </summary>
/// <param name="Unknown">First u32; 0 in every sample.</param>
/// <param name="Count">Second u32; 1 in every sample.</param>
/// <param name="Offset">Body offset of the range (the next 16-byte boundary after the attribute stream in every sample).</param>
/// <param name="Size">Size in bytes; the range ends the block.</param>
public sealed record DcmSimulationData(uint Unknown, uint Count, int Offset, int Size)
{
    /// <summary>
    /// The trailer's size in the header: <see cref="DcmMesh.SimulationTrailerSize"/> (16, the weapons' cables: unknown, count, offset, size) or
    /// <see cref="DcmMesh.ShortSimulationTrailerSize"/> (12, the armour's belts, cloth and fur: unknown, offset, size; <see cref="Count"/> is then the
    /// material record's last u32, 1 in every sample).
    /// </summary>
    public int TrailerSize { get; init; } = DcmMesh.SimulationTrailerSize;
    /// <summary>The n u32 between the material record (whose last u32 is n) and the offset: 0, 1 on the weapons' cables; 0 on the armour's cloth. Meaning unknown.</summary>
    public IReadOnlyList<uint> Entries { get; init; } = [];
}

/// <param name="StrideField">The block's stride field (40 in every skinned sample); its meaning is not settled.</param>
/// <param name="Bones">Bones this block's vertices reference, in local index order.</param>
/// <param name="BoneList">The block's bone list: the single material's <see cref="DcmMaterialRange.BonePalette"/> (the u32 per bone after
/// the material record; the identity 0..n-1 in every single-material block of the install), the distinct union of the palettes of a
/// multi-material block, or, with no material record, the u32 per bone after the bone records. Not used for skinning.</param>
/// <param name="Weights">Four weights per vertex; they sum to 1 on every vertex checked.</param>
/// <param name="BoneIndices">Four local bone indices per vertex (into <see cref="Bones"/>). The file stores slots of the vertex's draw
/// range's <see cref="DcmMaterialRange.BonePalette"/>; the reader maps them through it, so these are block-level indices **for weighted
/// slots**. An unweighted slot (weight 0) has no meaning: it is mapped when it lies inside the palette and otherwise left as stored (it may
/// be 200, past the bone count), so a consumer must not index with it unless its weight is positive.</param>
public sealed record DcmSkin(int StrideField, IReadOnlyList<DcmBone> Bones, int[] BoneList, Vector4[] Weights, byte[] BoneIndices);

/// <summary>A bone referenced by a skinned block. Bind it to the skeleton by <see cref="Name"/>; <see cref="Index"/> differs per mesh.</summary>
/// <param name="InverseBind">Row-vector (Direct3D-style) matrix from model space to the bone's bind space.</param>
public sealed record DcmBone(string Name, Matrix4x4 InverseBind, int Index);

/// <summary>
/// A triangle-list draw range using one material. Indices are absolute within the block
/// (verified on all 5,550 ranges of the multiplayer maps).
/// </summary>
/// <param name="Name">Material name; resolves to a ".bmat" entry in the same archive.</param>
/// <param name="Unknown1">Field after the material index; 0 in every sample.</param>
/// <param name="Unknown6">Last field: 0 on static blocks; on skinned blocks the number of u32 in <see cref="BonePalette"/>, which
/// follow the record in the header.</param>
public sealed record DcmMaterialRange(
    string Name, int MaterialIndex, int FirstVertex, int VertexCount, int StartIndex, int IndexCount, uint Unknown1, uint Unknown6)
{
    /// <summary>
    /// Skinned blocks only (null on static ones): the bones this range's vertices use, as indices into <see cref="DcmSkin.Bones"/>. A
    /// vertex's stored bone indices are slots of this list (**evidence**, docs/discovery.md "Bone palettes"); <see cref="DcmSkin.BoneIndices"/>
    /// are already mapped through it.
    /// </summary>
    public IReadOnlyList<int>? BonePalette { get; init; }
}
