using System.Globalization;
using System.Numerics;
using System.Text;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// Exports of the dressed level for the Mapmaker editor (docs/musou-ashfall-graybox.md "Kit dressing (draft 1)"): the dressing as JSON and the graybox's solid parts as one glTF 2.0 binary.
/// Pure functions of the level; nothing is read from or written to disk here. Numbers use the invariant culture.
/// </summary>
public static class AshfallExport
{
    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    static string Vec(Vector3 v) => $"[{F(v.X)}, {F(v.Y)}, {F(v.Z)}]";

    /// <summary>The dressing as JSON: version numbers, the level hash (hex), every item in the dressing's order and the hidden parts.</summary>
    public static string DressingJson(AshfallLevel level, AshfallDressing dressing)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"levelVersion\": {AshfallMap.Version},\n");
        sb.Append($"  \"dressVersion\": {dressing.Version},\n");
        sb.Append($"  \"levelHash\": \"{level.Hash:X16}\",\n");
        sb.Append("  \"items\": [\n");
        for (int i = 0; i < dressing.Items.Count; i++)
        {
            var it = dressing.Items[i];
            sb.Append($"    {{ \"index\": {i}, \"asset\": \"{it.Asset}\", \"role\": \"{it.Role}\", \"part\": {it.Part}, \"position\": {Vec(it.Position)}, \"yaw\": {F(it.Yaw)}, \"size\": {Vec(it.Size)}, \"swapXZ\": {(it.SwapXZ ? "true" : "false")} }}");
            sb.Append(i + 1 < dressing.Items.Count ? ",\n" : "\n");
        }
        sb.Append("  ],\n");
        sb.Append($"  \"hiddenParts\": [{string.Join(", ", dressing.HiddenParts.Select(p => p.ToString(CultureInfo.InvariantCulture)))}]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// The level's solid parts as a glTF 2.0 binary: one mesh and one node per <see cref="AshfallKind"/> that has solid parts (named after the kind), triangles without indices,
    /// POSITION only, no materials. Y up, metres: game X is glTF X, game Y is glTF -Z, game Z is glTF Y (a proper rotation: the winding is kept).
    /// </summary>
    public static byte[] GrayboxGlb(AshfallLevel level)
    {
        var kinds = Enum.GetValues<AshfallKind>().Where(k => level.Parts.Any(p => p.Solid && p.Kind == k)).ToList();
        var bin = new MemoryStream();
        var views = new List<string>();
        var accessors = new List<string>();
        for (int k = 0; k < kinds.Count; k++)
        {
            long start = bin.Position;
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            int count = 0;
            foreach (var part in level.Parts.Where(p => p.Solid && p.Kind == kinds[k]))
                foreach (var c in part.Corners)
                {
                    var g = new Vector3(c.X, c.Z, -c.Y);
                    lo = Vector3.Min(lo, g); hi = Vector3.Max(hi, g);
                    bin.Write(BitConverter.GetBytes(g.X)); bin.Write(BitConverter.GetBytes(g.Y)); bin.Write(BitConverter.GetBytes(g.Z));
                    count++;
                }
            views.Add($"{{\"buffer\":0,\"byteOffset\":{start},\"byteLength\":{bin.Position - start},\"target\":34962}}");
            accessors.Add($"{{\"bufferView\":{k},\"componentType\":5126,\"count\":{count},\"type\":\"VEC3\",\"min\":[{F(lo.X)},{F(lo.Y)},{F(lo.Z)}],\"max\":[{F(hi.X)},{F(hi.Y)},{F(hi.Z)}]}}");
        }
        var json = new StringBuilder();
        json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"sm-reimpl ashfall-plan\"},\"scene\":0,");
        json.Append($"\"scenes\":[{{\"nodes\":[{string.Join(",", Enumerable.Range(0, kinds.Count))}]}}],");
        json.Append($"\"nodes\":[{string.Join(",", kinds.Select((k, i) => $"{{\"name\":\"{k}\",\"mesh\":{i}}}"))}],");
        json.Append($"\"meshes\":[{string.Join(",", kinds.Select((k, i) => $"{{\"name\":\"{k}\",\"primitives\":[{{\"attributes\":{{\"POSITION\":{i}}},\"mode\":4}}]}}"))}],");
        json.Append($"\"accessors\":[{string.Join(",", accessors)}],\"bufferViews\":[{string.Join(",", views)}],\"buffers\":[{{\"byteLength\":{bin.Length}}}]}}");

        var jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
        int jsonPad = (4 - jsonBytes.Length % 4) % 4, binPad = (int)((4 - bin.Length % 4) % 4);
        int jsonLen = jsonBytes.Length + jsonPad, binLen = (int)bin.Length + binPad;
        var outStream = new MemoryStream();
        var w = new BinaryWriter(outStream);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonLen + 8 + binLen));
        w.Write((uint)jsonLen); w.Write(0x4E4F534Au); w.Write(jsonBytes); for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)binLen); w.Write(0x004E4942u); w.Write(bin.ToArray()); for (int i = 0; i < binPad; i++) w.Write((byte)0);
        w.Flush();
        return outStream.ToArray();
    }
}
