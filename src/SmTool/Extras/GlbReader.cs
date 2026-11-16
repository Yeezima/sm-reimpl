using System.Numerics;
using System.Text.Json;

namespace SmTool;

/// <summary>
/// A minimal glTF 2.0 binary (.glb) reader for the offline Ashfall kit tools: JSON and BIN chunks, triangle primitives, POSITION and indices, the node hierarchy with matrix or TRS applied.
/// No Draco, meshopt, sparse accessors or external buffers: it fails clearly instead.
/// </summary>
static class GlbReader
{
    public sealed record Mesh(List<Vector3> Positions, List<int> Indices);

    public static Mesh Read(string path)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67u) throw new InvalidDataException($"{path}: not a GLB file");
        if (BitConverter.ToUInt32(data, 4) != 2) throw new InvalidDataException($"{path}: GLB version is not 2");
        int total = (int)Math.Min(BitConverter.ToUInt32(data, 8), (uint)data.Length);
        int off = 12; string? json = null; int binStart = -1, binLen = 0;
        while (off + 8 <= total)
        {
            int len = (int)BitConverter.ToUInt32(data, off); uint type = BitConverter.ToUInt32(data, off + 4);
            if (len < 0 || off + 8 + len > data.Length) throw new InvalidDataException($"{path}: chunk overruns the file");
            if (type == 0x4E4F534Au) json = System.Text.Encoding.UTF8.GetString(data, off + 8, len);
            else if (type == 0x004E4942u && binStart < 0) { binStart = off + 8; binLen = len; }
            off += 8 + ((len + 3) & ~3);
        }
        if (json is null) throw new InvalidDataException($"{path}: no JSON chunk");
        if (binStart < 0) throw new InvalidDataException($"{path}: no BIN chunk");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("extensionsRequired", out var req) && req.GetArrayLength() > 0)
            throw new NotSupportedException($"{path}: required extensions not supported ({string.Join(", ", req.EnumerateArray().Select(e => e.GetString()))})");

        var views = root.GetProperty("bufferViews"); var accessors = root.GetProperty("accessors"); var meshes = root.GetProperty("meshes");
        var nodes = root.TryGetProperty("nodes", out var nd) ? nd : default;
        var result = new Mesh([], []);

        List<int> roots;
        if (root.TryGetProperty("scenes", out var scenes) && scenes.GetArrayLength() > 0)
        {
            int sc = root.TryGetProperty("scene", out var s) ? s.GetInt32() : 0;
            roots = scenes[sc].TryGetProperty("nodes", out var rn) ? rn.EnumerateArray().Select(e => e.GetInt32()).ToList() : [];
        }
        else roots = nodes.ValueKind == JsonValueKind.Array ? Enumerable.Range(0, nodes.GetArrayLength()).ToList() : [];

        foreach (int r in roots) Visit(r, Matrix4x4.Identity, 0);
        return result;

        void Visit(int ni, Matrix4x4 parent, int depth)
        {
            if (depth > 64) throw new InvalidDataException("node hierarchy too deep");
            var node = nodes[ni];
            var world = Local(node) * parent;
            if (node.TryGetProperty("mesh", out var mi)) AddMesh(meshes[mi.GetInt32()], world);
            if (node.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) Visit(c.GetInt32(), world, depth + 1);
        }

        static Matrix4x4 Local(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var m))
            {
                var f = m.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            }
            var sc = Vector3.One; var rot = Quaternion.Identity; var tr = Vector3.Zero;
            if (node.TryGetProperty("scale", out var s)) { var a = s.EnumerateArray().Select(e => e.GetSingle()).ToArray(); sc = new(a[0], a[1], a[2]); }
            if (node.TryGetProperty("rotation", out var r)) { var a = r.EnumerateArray().Select(e => e.GetSingle()).ToArray(); rot = new(a[0], a[1], a[2], a[3]); }
            if (node.TryGetProperty("translation", out var t)) { var a = t.EnumerateArray().Select(e => e.GetSingle()).ToArray(); tr = new(a[0], a[1], a[2]); }
            return Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(tr);
        }

        void AddMesh(JsonElement mesh, Matrix4x4 world)
        {
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                int mode = prim.TryGetProperty("mode", out var md) ? md.GetInt32() : 4;
                if (mode != 4) continue;
                if (!prim.GetProperty("attributes").TryGetProperty("POSITION", out var posAcc)) continue;
                var pos = ReadVec3(posAcc.GetInt32());
                int baseIndex = result.Positions.Count;
                foreach (var p in pos) result.Positions.Add(Vector3.Transform(p, world));
                if (prim.TryGetProperty("indices", out var ia))
                {
                    foreach (int i in ReadIndices(ia.GetInt32()))
                    {
                        if (i < 0 || i >= pos.Length) throw new InvalidDataException("index out of range");
                        result.Indices.Add(baseIndex + i);
                    }
                }
                else for (int i = 0; i < pos.Length; i++) result.Indices.Add(baseIndex + i);
            }
        }

        (int Start, int Stride, int Count) Locate(int acc, int elementSize)
        {
            var a = accessors[acc];
            if (a.TryGetProperty("sparse", out _)) throw new NotSupportedException("sparse accessors are not supported");
            var v = views[a.GetProperty("bufferView").GetInt32()];
            if (v.TryGetProperty("buffer", out var b) && b.GetInt32() != 0) throw new NotSupportedException("only the GLB's own buffer is supported");
            int start = (v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            int stride = v.TryGetProperty("byteStride", out var bs) && bs.GetInt32() > 0 ? bs.GetInt32() : elementSize;
            int count = a.GetProperty("count").GetInt32();
            if (start < 0 || count < 0 || (count > 0 && (long)start + (long)(count - 1) * stride + elementSize > binLen)) throw new InvalidDataException("accessor overruns the BIN chunk");
            return (binStart + start, stride, count);
        }

        Vector3[] ReadVec3(int acc)
        {
            var a = accessors[acc];
            if (a.GetProperty("type").GetString() != "VEC3" || a.GetProperty("componentType").GetInt32() != 5126) throw new NotSupportedException("POSITION must be float VEC3");
            var (start, stride, count) = Locate(acc, 12);
            var res = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int p = start + i * stride;
                res[i] = new(BitConverter.ToSingle(data, p), BitConverter.ToSingle(data, p + 4), BitConverter.ToSingle(data, p + 8));
            }
            return res;
        }

        int[] ReadIndices(int acc)
        {
            int comp = accessors[acc].GetProperty("componentType").GetInt32();
            int size = comp switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new NotSupportedException($"index component type {comp}") };
            var (start, stride, count) = Locate(acc, size);
            var res = new int[count];
            for (int i = 0; i < count; i++)
            {
                int p = start + i * stride;
                res[i] = size == 1 ? data[p] : size == 2 ? BitConverter.ToUInt16(data, p) : (int)BitConverter.ToUInt32(data, p);
            }
            return res;
        }
    }
}
