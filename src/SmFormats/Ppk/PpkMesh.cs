using System.Buffers.Binary;
using System.Numerics;

namespace SmFormats.Ppk;

/// <summary>
/// A physics shape mesh (<c>ppk</c> entries such as <c>ammo_crate_A__d</c>, named by an object definition's
/// <c>CShapeMesh.MeshName</c>; M39). Layout (**supported by evidence**: docs/discovery.md, "Physics shape meshes"):
/// a 0x50-byte header, then from <see cref="DataStart"/> the vertices (3 floats each), the triangles (3 u16 indices each)
/// at a 16-byte-aligned offset, and a MOPP code block (not read; callers build their own tree).
/// <code>
/// +0x00 u32 0x66 (every ppk entry)   +0x04 u32 part count (1..34 in the install; only 1 is read)   +0x08 u32 0x50 (data start)
/// +0x10 u32 vertex count             +0x18 u32 triangle count
/// +0x1C u32 triangle offset (from the data start)
/// +0x2C u32 MOPP size (padded)        +0x30 u32 MOPP offset (from the data start)
/// +0x34 float4 MOPP code info (offset xyz, scale w)
/// </code>
/// </summary>
public sealed record PpkMesh(Vector3[] Vertices, int[] Indices)
{
    public const uint Magic = 0x66;
    public const int DataStart = 0x50;
    /// <summary>Far above the largest entry in the install (to bound corrupt data).</summary>
    public const int MaxVertices = 65_536, MaxTriangles = 200_000;

    public int TriangleCount => Indices.Length / 3;

    public static bool HasMagic(ReadOnlySpan<byte> data) =>
        data.Length >= DataStart && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic
        && BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) == DataStart;

    /// <summary>Reads the vertices and triangles; throws <see cref="InvalidDataException"/> when the layout does not hold.</summary>
    public static PpkMesh Read(ReadOnlySpan<byte> data)
    {
        if (!HasMagic(data)) throw new InvalidDataException("Not a physics shape mesh (magic 0x66, data start 0x50).");
        if (U32(data, 0x04) != 1) throw new InvalidDataException($"Physics shape mesh with {U32(data, 0x04)} parts (only single-part meshes are read).");
        uint vertexCount = U32(data, 0x10), triangleCount = U32(data, 0x18), triangleOffset = U32(data, 0x1C);
        if (vertexCount is 0 or > MaxVertices || triangleCount is 0 or > MaxTriangles)
            throw new InvalidDataException($"{vertexCount} vertices, {triangleCount} triangles: out of range.");
        long vertexEnd = DataStart + 12L * vertexCount;
        long trianglesAt = DataStart + (long)triangleOffset, triangleEnd = trianglesAt + 6L * triangleCount;
        if (triangleOffset % 16 != 0 || trianglesAt < vertexEnd || triangleEnd > data.Length)
            throw new InvalidDataException($"Triangles at 0x{trianglesAt:X} ({triangleCount}) do not fit after {vertexCount} vertices in {data.Length} bytes.");
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int at = DataStart + 12 * i;
            var v = new Vector3(F32(data, at), F32(data, at + 4), F32(data, at + 8));
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) throw new InvalidDataException($"Vertex {i} is not finite.");
            vertices[i] = v;
        }
        var indices = new int[3 * triangleCount];
        for (int i = 0; i < indices.Length; i++)
        {
            int index = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(trianglesAt + 2 * i)..]);
            if (index >= vertexCount) throw new InvalidDataException($"Index {index} at triangle {i / 3} is outside the {vertexCount} vertices.");
            indices[i] = index;
        }
        return new PpkMesh(vertices, indices);
    }

    static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);
    static float F32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadSingleLittleEndian(d[at..]);
}

/// <summary>
/// Builds synthetic physics shape meshes in the layout <see cref="PpkMesh"/> reads (an empty MOPP block). For tests only; a
/// passing test proves internal consistency, not compatibility with original files.
/// </summary>
public static class SyntheticPpkWriter
{
    public static byte[] Build(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices)
    {
        int vertexBytes = Align(12 * vertices.Count), triangleBytes = Align(2 * indices.Count);
        var data = new byte[PpkMesh.DataStart + vertexBytes + triangleBytes + 16];
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), v);
        U32(0x00, PpkMesh.Magic); U32(0x04, 1); U32(0x08, PpkMesh.DataStart);
        U32(0x10, (uint)vertices.Count); U32(0x18, (uint)(indices.Count / 3)); U32(0x1C, (uint)vertexBytes);
        U32(0x2C, 16); U32(0x30, (uint)(vertexBytes + triangleBytes));
        for (int i = 0; i < vertices.Count; i++)
        {
            int at = PpkMesh.DataStart + 12 * i;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), vertices[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 4), vertices[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 8), vertices[i].Z);
        }
        for (int i = 0; i < indices.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(PpkMesh.DataStart + vertexBytes + 2 * i), checked((ushort)indices[i]));
        return data;
    }

    static int Align(int n) => (n + 15) & ~15;
}
