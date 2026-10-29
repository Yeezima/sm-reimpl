using System.Numerics;
using System.Text;

namespace SmFormats.Dcm;

/// <summary>
/// Builds synthetic ".dcm" meshes (static type-0, skinned type-1 and simulated type-2 blocks) following our documented interpretation
/// of the layout. For parser tests only; a passing test proves internal consistency, not compatibility with original files.
/// </summary>
public static class SyntheticDcmWriter
{
    public sealed record Material(string Name, int StartIndex, int IndexCount, int FirstVertex, int VertexCount);
    public sealed record SkinBone(string Name, Matrix4x4 InverseBind);
    /// <param name="BoneIndices">Four bone slots per vertex: indices into the palette of the vertex's material range.</param>
    /// <param name="Palettes">One bone palette per material (entries are indices of <paramref name="Bones"/>), written after each material
    /// record. Null: every material gets the identity palette 0..n-1 (what every single-material block in the install has).</param>
    public sealed record Skin(SkinBone[] Bones, Vector4[] Weights, byte[] BoneIndices, int[][]? Palettes = null);
    /// <param name="SimulationBytes">When not null: a type-2 block, a static block followed by a trailer naming this many further
    /// body bytes (filled with a recognizable pattern), placed at the next 16-byte boundary after the attribute stream.</param>
    /// <param name="ShortTrailer">With <paramref name="SimulationBytes"/>: the armour's variant (fix/armour-pieces): the material records' last u32 is 1 and
    /// the trailer is three u32 (0, offset, size) instead of the weapons' four (0, 1, offset, size).</param>
    public sealed record Block(Vector3[] Positions, ushort[] Indices, Material[] Materials, ushort Flags = 0, Skin? Skinning = null, int? SimulationBytes = null,
        bool ShortTrailer = false);

    public static byte[] Build(IReadOnlyList<Block> blocks)
    {
        var header = new MemoryStream();
        var h = new BinaryWriter(header, Encoding.ASCII);
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        h.Write(blocks.Count);
        foreach (var b in blocks)
        {
            int nv = b.Positions.Length, ni = b.Indices.Length, offset = (int)body.Length;
            var min = b.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = b.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            if (b.Skinning is { } skin)
            {
                int positions = offset + 2 * ni, frames = positions + DcmMesh.SkinPositionStride * nv, extra = frames + DcmMesh.SkinFrameStride * nv;
                h.Write((byte)1); h.Write(nv); h.Write(40); h.Write(ni); h.Write(offset); h.Write(b.Materials.Length); h.Write(skin.Bones.Length);
                WriteBounds(h, min, max);
                h.Write(positions); h.Write(frames); h.Write(extra);
                for (int k = 0; k < skin.Bones.Length; k++)
                {
                    WriteName(h, skin.Bones[k].Name);
                    var m = skin.Bones[k].InverseBind;
                    foreach (var f in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) h.Write(f);
                    h.Write(k + 100);                    // per-mesh bone number; differs from the local index in real files
                }
                var palettes = skin.Palettes ?? b.Materials.Select(_ => Enumerable.Range(0, skin.Bones.Length).ToArray()).ToArray();
                WriteMaterials(h, b.Materials, palettes);
                if (b.Materials.Length == 0) for (int k = 0; k < skin.Bones.Length; k++) h.Write(k);   // no record to hold a palette: the bone list alone

                foreach (var i in b.Indices) w.Write(i);
                for (int v = 0; v < nv; v++)
                {
                    WriteVector(w, b.Positions[v]);
                    w.Write(skin.Weights[v].X); w.Write(skin.Weights[v].Y); w.Write(skin.Weights[v].Z); w.Write(skin.Weights[v].W);
                    for (int k = 0; k < 4; k++) w.Write(skin.BoneIndices[4 * v + k]);
                }
                for (int v = 0; v < nv; v++) { w.Write(0f); w.Write(0f); w.Write(1f); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(1f); }
                for (int v = 0; v < nv; v++) { w.Write(b.Positions[v].X); w.Write(b.Positions[v].Y); w.Write(v * 0.5f); w.Write(-v * 0.5f); w.Write(0xFF808080u); }
                for (int v = 0; v < nv; v++) { w.Write(0); WriteVector(w, b.Positions[v]); w.Write(0f); w.Write(0f); w.Write(1f); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(1f); }
                for (int v = 0; v < nv; v++) w.Write(new byte[20]);
                continue;
            }

            h.Write(b.SimulationBytes is null ? b.Flags : (ushort)2); h.Write(nv); h.Write(DcmMesh.StaticStride); h.Write(ni); h.Write(offset); h.Write(2); h.Write(b.Materials.Length);
            WriteBounds(h, min, max);
            h.Write(offset + 2 * ni); h.Write(offset + 2 * ni + 12 * nv);
            WriteMaterials(h, b.Materials, lastField: b.SimulationBytes is null ? 0 : b.ShortTrailer ? 1 : 2);

            foreach (var i in b.Indices) w.Write(i);
            foreach (var p in b.Positions) WriteVector(w, p);
            for (int v = 0; v < nv; v++)
            {
                // Recognizable attribute values: normal +Z, tangent +X with w = 1, UVs from the position, grey colour.
                w.Write(0f); w.Write(0f); w.Write(1f);
                w.Write(1f); w.Write(0f); w.Write(0f); w.Write(1f);
                w.Write(b.Positions[v].X); w.Write(b.Positions[v].Y);
                w.Write(v * 0.5f); w.Write(-v * 0.5f);
                w.Write(0xFF808080u);
            }
            if (b.SimulationBytes is int simBytes)
            {
                w.Flush();
                int start = (int)((body.Length + 15) / 16 * 16);
                while (body.Length < start) w.Write((byte)0);
                for (int k = 0; k < simBytes; k++) w.Write((byte)(0xA0 + k % 16));
                if (b.ShortTrailer) { h.Write(0); h.Write(start); h.Write(simBytes); }
                else { h.Write(0); h.Write(1); h.Write(start); h.Write(simBytes); }
            }
        }
        h.Flush(); w.Flush();

        var output = new MemoryStream();
        var o = new BinaryWriter(output);
        o.Write(8 + (int)header.Length);     // header size includes the two size fields
        o.Write((int)body.Length);
        o.Write(header.ToArray());
        o.Write(body.ToArray());
        return output.ToArray();
    }

    static void WriteBounds(BinaryWriter h, Vector3 min, Vector3 max) { WriteVector(h, min); WriteVector(h, max); }
    static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static void WriteName(BinaryWriter h, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        h.Write((byte)0xFF); h.Write((ushort)bytes.Length); h.Write(bytes);
    }
    /// <param name="palettes">Skinned blocks: per material the u32 palette that follows its record (its length is the record's last field).</param>
    /// <param name="lastField">Static blocks: the records' last u32 (0; on type-2 blocks the count of u32 before the trailer's offset: 2 for the cables, 1 for the armour's cloth).</param>
    static void WriteMaterials(BinaryWriter h, Material[] materials, int[][]? palettes = null, int lastField = 0)
    {
        for (int m = 0; m < materials.Length; m++)
        {
            var mat = materials[m];
            WriteName(h, mat.Name);
            h.Write(m); h.Write(0); h.Write(mat.VertexCount); h.Write(mat.FirstVertex); h.Write(mat.IndexCount); h.Write(mat.StartIndex);
            h.Write(palettes?[m].Length ?? lastField);
            if (palettes is not null) foreach (var entry in palettes[m]) h.Write(entry);
        }
    }
}
