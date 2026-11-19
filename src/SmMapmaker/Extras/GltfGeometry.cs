using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// The triangles of a glTF 2.0 model for the compiler (docs/mapmaker.md "SM1 pieces in play"): every mesh primitive of the scene's node tree with its node transforms applied, in glTF's own
/// frame (Y up; <see cref="ToDocumentFrame"/> turns it into the document's). Only what the editor's own loader needs: float POSITION, 8/16/32-bit indices (or none), triangle lists, strips
/// and fans, buffers from the .glb chunk, a data URI or a plain relative file beside it. Sparse accessors, normalised or quantised positions and compressed files are refused in plain words.
/// </summary>
public static class GltfGeometry
{
    public const int MaxVertices = 4_000_000, MaxIndices = 12_000_000, MaxPrimitives = 20_000;
    /// <summary>Largest model file read for collision (a shared library folder is somebody else's input).</summary>
    public const long MaxFileBytes = 64_000_000;

    /// <summary>The file's bytes, for hashing (the same cap as reading).</summary>
    public static byte[] FileBytesForHash(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new MapException("bad-asset", $"The model '{Path.GetFileName(path)}' is larger than {MaxFileBytes / 1_000_000} MB.");
        return File.ReadAllBytes(path);
    }

    /// <summary>glTF (x, y, z) to the document frame (x, -z, y): the same turn as <c>AssetInfo.GltfToDocument</c> and the editor's view.</summary>
    public static Vector3 ToDocumentFrame(Vector3 v) => new(v.X, -v.Z, v.Y);

    public static (Vector3[] Vertices, int[] Indices) Read(string path)
    {
        try { return ReadCore(path); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException or ArgumentException or OverflowException or IOException or UnauthorizedAccessException
            or NotSupportedException or JsonException or IndexOutOfRangeException)
        {
            throw new MapException("bad-asset", $"Could not read the model '{Path.GetFileName(path)}': {e.Message}");
        }
    }

    static (Vector3[], int[]) ReadCore(string path)
    {
        byte[] bytes = FileBytesForHash(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        byte[]? glbBin = null;
        string jsonText;
        if (ext == ".glb")
        {
            jsonText = GltfReader.GlbJson(bytes);
            uint jsonLength = BitConverter.ToUInt32(bytes, 12);
            long at = 20L + jsonLength;
            at = (at + 3) & ~3L;
            if (at + 8 <= bytes.Length)
            {
                uint length = BitConverter.ToUInt32(bytes, (int)at), type = BitConverter.ToUInt32(bytes, (int)at + 4);
                if (type == 0x004E4942 && at + 8 + length <= bytes.Length) glbBin = bytes.AsSpan((int)at + 8, (int)length).ToArray();
            }
        }
        else jsonText = Encoding.UTF8.GetString(bytes);
        var root = JsonNode.Parse(jsonText) as JsonObject ?? throw new InvalidOperationException("the glTF JSON is not an object");
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;

        var buffers = new List<byte[]?>();
        if (root["buffers"] is JsonArray bl)
            for (int i = 0; i < bl.Count; i++)
            {
                string? uri = bl[i]?["uri"]?.GetValue<string>();
                if (uri is null) buffers.Add(i == 0 ? glbBin : null);
                else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = uri.IndexOf(',');
                    buffers.Add(comma < 0 ? null : Convert.FromBase64String(uri[(comma + 1)..]));
                }
                else
                {
                    string rel = Uri.UnescapeDataString(uri).Replace('\\', '/');
                    if (rel.Length == 0 || rel.Contains(':') || rel.StartsWith('/') || rel.Split('/').Any(s => s is "" or "." or "..")) throw new InvalidOperationException($"the buffer '{uri}' is not a plain relative file");
                    string full = Path.GetFullPath(Path.Combine(dir, rel));
                    buffers.Add(File.Exists(full) ? File.ReadAllBytes(full) : null);
                }
            }
        var views = root["bufferViews"] as JsonArray ?? [];
        var accessors = root["accessors"] as JsonArray ?? [];
        var meshes = root["meshes"] as JsonArray ?? [];
        var nodes = root["nodes"] as JsonArray;

        var vertices = new List<Vector3>();
        var indices = new List<int>();
        int primitives = 0;

        void AddPrimitive(JsonObject prim, double[] world)
        {
            if (++primitives > MaxPrimitives) throw new InvalidOperationException($"the node tree draws more than {MaxPrimitives:N0} mesh primitives");
            int mode = prim["mode"] is { } mn ? GltfReader.Index(mn) : 4;
            if (mode is not (4 or 5 or 6)) return;
            int acc = GltfReader.Index(prim["attributes"]?["POSITION"]);
            if (acc < 0 || acc >= accessors.Count || accessors[acc] is not JsonObject pa) return;
            // the caps come first: nothing is allocated for an accessor that already exceeds them
            int declared = GltfReader.Index(pa["count"]);
            if (declared < 0 || declared > MaxVertices - vertices.Count) throw new InvalidOperationException($"the model has more than {MaxVertices:N0} vertices");
            var positions = ReadVec3(pa, views, buffers);
            int[] source;
            int ia = GltfReader.Index(prim["indices"]);
            if (ia >= 0 && ia < accessors.Count && GltfReader.Index(accessors[ia]?["count"]) is var indexCount && (indexCount < 0 || indexCount > MaxIndices - indices.Count)) throw new InvalidOperationException($"the model has more than {MaxIndices:N0} indices");
            if (ia >= 0) source = ReadIndices(ia < accessors.Count ? accessors[ia] as JsonObject : null, views, buffers, positions.Length);
            else source = Enumerable.Range(0, positions.Length).ToArray();
            if (indices.Count + source.Length > MaxIndices) throw new InvalidOperationException($"the model has more than {MaxIndices:N0} indices");
            int first = vertices.Count;
            foreach (var p in positions)
            {
                var (x, y, z) = DetMath.Transform(p.X, p.Y, p.Z, world);
                vertices.Add(new Vector3((float)x, (float)-z, (float)y));   // glTF (x, y, z) to the document frame (x, -z, y)
            }
            switch (mode)
            {
                case 4:
                    for (int i = 0; i + 2 < source.Length; i += 3) indices.AddRange([first + source[i], first + source[i + 1], first + source[i + 2]]);
                    break;
                case 5:
                    for (int i = 0; i + 2 < source.Length; i++)
                        indices.AddRange((i & 1) == 0 ? [first + source[i], first + source[i + 1], first + source[i + 2]] : [first + source[i + 1], first + source[i], first + source[i + 2]]);
                    break;
                case 6:
                    for (int i = 1; i + 1 < source.Length; i++) indices.AddRange([first + source[0], first + source[i], first + source[i + 1]]);
                    break;
            }
        }

        int visits = 0;
        void Walk(int index, double[] parent, int depth)
        {
            if (++visits > 100_000) throw new InvalidOperationException("the node tree is too large or cyclic");
            if (nodes is null || index < 0 || index >= nodes.Count || nodes[index] is not JsonObject node || depth > 64) return;
            var world = DetMath.Mul(LocalD(node), parent);
            int mi = GltfReader.Index(node["mesh"]);
            if (mi >= 0 && mi < meshes.Count && meshes[mi]?["primitives"] is JsonArray prims)
                foreach (var prim in prims) if (prim is JsonObject po) AddPrimitive(po, world);
            if (node["children"] is JsonArray kids) foreach (var k in kids) Walk(GltfReader.Index(k), world, depth + 1);
        }

        var roots = new List<int>();
        if (root["scenes"] is JsonArray { Count: > 0 } scenes)
        {
            int s = GltfReader.Index(root["scene"]);
            if (s < 0) s = 0;
            if (s < scenes.Count && scenes[s]?["nodes"] is JsonArray ns) roots.AddRange(ns.Select(GltfReader.Index).Where(i => i >= 0));
        }
        else if (nodes is not null)
        {
            var children = nodes.SelectMany(n => (n?["children"] as JsonArray)?.Select(GltfReader.Index) ?? []).ToHashSet();
            roots.AddRange(Enumerable.Range(0, nodes.Count).Where(i => !children.Contains(i)));
        }
        foreach (var r in roots) Walk(r, DetMath.Identity(), 0);
        return ([.. vertices], [.. indices]);
    }

    static double D(JsonNode? n)
    {
        double v = n is JsonValue j ? j.GetValue<double>() : 0.0;
        if (!double.IsFinite(v) || Math.Abs(v) > DetMath.Limit) throw new InvalidOperationException("a node transform is not a finite number within range");
        return v;
    }

    /// <summary>A node's local matrix in double (row-vector form): its <c>matrix</c>, or scale, then rotation (a normalised quaternion), then translation; only IEEE operations, so it is the same on every OS.</summary>
    static double[] LocalD(JsonObject node)
    {
        if (node["matrix"] is JsonArray { Count: 16 } m) return m.Select(D).ToArray();
        double tx = 0, ty = 0, tz = 0, sx = 1, sy = 1, sz = 1, qx = 0, qy = 0, qz = 0, qw = 1;
        if (node["translation"] is JsonArray { Count: 3 } t) { tx = D(t[0]); ty = D(t[1]); tz = D(t[2]); }
        if (node["scale"] is JsonArray { Count: 3 } s) { sx = D(s[0]); sy = D(s[1]); sz = D(s[2]); }
        if (node["rotation"] is JsonArray { Count: 4 } r)
        {
            qx = D(r[0]); qy = D(r[1]); qz = D(r[2]); qw = D(r[3]);
            double len = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (len > 0) { qx /= len; qy /= len; qz /= len; qw /= len; } else { qx = qy = qz = 0; qw = 1; }
        }
        double[] R =
        [
            1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy + qz * qw), 2 * (qx * qz - qy * qw), 0,
            2 * (qx * qy - qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz + qx * qw), 0,
            2 * (qx * qz + qy * qw), 2 * (qy * qz - qx * qw), 1 - 2 * (qx * qx + qy * qy), 0,
            0, 0, 0, 1,
        ];
        double[] S = [sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, sz, 0, 0, 0, 0, 1];
        double[] T = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, tx, ty, tz, 1];
        return DetMath.Mul(DetMath.Mul(S, R), T);
    }

    static (byte[] Data, int Offset, int Stride, int Length) View(JsonObject accessor, JsonArray views, List<byte[]?> buffers, int elementSize)
    {
        if (accessor["sparse"] is not null) throw new NotSupportedException("sparse accessors are not supported");
        int vi = GltfReader.Index(accessor["bufferView"]);
        if (vi < 0 || vi >= views.Count || views[vi] is not JsonObject view) throw new InvalidOperationException("an accessor has no buffer view");
        int bi = GltfReader.Index(view["buffer"]);
        if (bi < 0 || bi >= buffers.Count || buffers[bi] is not { } data) throw new InvalidOperationException("a buffer is missing (a model file beside it was not found)");
        int offset = (view["byteOffset"] is { } vo ? GltfReader.Index(vo) : 0) + (accessor["byteOffset"] is { } ao ? GltfReader.Index(ao) : 0);
        int stride = view["byteStride"] is { } sn ? GltfReader.Index(sn) : 0;
        if (stride <= 0) stride = elementSize;
        int count = GltfReader.Index(accessor["count"]);
        if (count < 0 || offset < 0 || (long)offset + (long)(count == 0 ? 0 : count - 1) * stride + elementSize > data.Length) throw new InvalidOperationException("an accessor reaches outside its buffer");
        return (data, offset, stride, count);
    }

    static Vector3[] ReadVec3(JsonObject accessor, JsonArray views, List<byte[]?> buffers)
    {
        if (GltfReader.Index(accessor["componentType"]) != 5126 || accessor["type"]?.GetValue<string>() != "VEC3") throw new NotSupportedException("positions must be float VEC3 (quantised or normalised models are not supported)");
        var (data, offset, stride, count) = View(accessor, views, buffers, 12);
        var result = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + i * stride;
            result[i] = new Vector3(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8));
        }
        return result;
    }

    static int[] ReadIndices(JsonObject? accessor, JsonArray views, List<byte[]?> buffers, int vertexCount)
    {
        if (accessor is null) throw new InvalidOperationException("a primitive names a missing index accessor");
        int type = GltfReader.Index(accessor["componentType"]);
        int size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new NotSupportedException("indices must be 8, 16 or 32 bit unsigned") };
        var (data, offset, stride, count) = View(accessor, views, buffers, size);
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            int at = offset + i * stride;
            long v = size switch { 1 => data[at], 2 => BitConverter.ToUInt16(data, at), _ => BitConverter.ToUInt32(data, at) };
            if (v >= vertexCount) throw new InvalidOperationException("an index points past the vertices");
            result[i] = (int)v;
        }
        return result;
    }
}
