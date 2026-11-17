using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>What <see cref="GltfReader.Inspect"/> found. <see cref="Companions"/> are the external buffers and images of a .gltf, relative to its folder.</summary>
public sealed record GltfInfo(string FileName, Bounds? Bounds, IReadOnlyList<string> Companions);

/// <summary>
/// A minimal glTF 2.0 reader for the Mapmaker's imports (no package): the header and JSON of a .glb or .gltf, the node tree and the POSITION accessors' min/max.
/// Bounds are in glTF's own frame (Y up).
/// </summary>
public static class GltfReader
{
    const uint GlbMagic = 0x46546C67, JsonChunk = 0x4E4F534A;

    public static GltfInfo Inspect(string path)
    {
        try { return InspectCore(path); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException or ArgumentException or OverflowException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new MapException("bad-file", $"Could not read '{Path.GetFileName(path)}': {e.Message}");
        }
    }

    static GltfInfo InspectCore(string path)
    {
        if (!File.Exists(path)) throw new MapException("bad-file", $"No file '{path}'.");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".glb" or ".gltf")) throw new MapException("bad-file", $"'{Path.GetFileName(path)}' is not a .glb or .gltf file.");
        byte[] bytes = File.ReadAllBytes(path);
        string jsonText = ext == ".glb" ? GlbJson(bytes) : Encoding.UTF8.GetString(bytes);
        JsonObject root;
        try { root = Json.Obj(JsonNode.Parse(jsonText), "The glTF"); }
        catch (JsonException e) { throw new MapException("bad-file", $"The glTF JSON is invalid: {e.Message}"); }
        if (root["asset"] is not JsonObject asset || !(asset["version"]?.ToString() ?? "").StartsWith('2'))
            throw new MapException("bad-file", "Not a glTF 2.0 file (asset.version).");

        // Godot cannot decode these; the model would load as nothing (the "empty bounding box"). Say so at import.
        foreach (var key in new[] { "extensionsRequired", "extensionsUsed" })
            if (root[key] is JsonArray exts)
                foreach (var e in exts)
                {
                    string name = e?.ToString() ?? "";
                    if (name is "KHR_draco_mesh_compression" or "EXT_meshopt_compression")
                        throw new MapException("unsupported-compression", $"'{Path.GetFileName(path)}' is compressed with {(name.StartsWith("KHR_draco", StringComparison.Ordinal) ? "Draco" : "meshopt")} ({name}), which the editor cannot read. Download the uncompressed glTF/GLB (or the FBX) instead, or decompress it (for example with gltf-transform).");
                }

        var companions = new List<string>();
        if (ext == ".gltf")
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var key in new[] { "buffers", "images" })
                if (root[key] is JsonArray list)
                    foreach (var item in list)
                    {
                        if (item is not JsonObject entry) throw new MapException("bad-file", $"The glTF {key} entry is not an object.");
                        if (entry["uri"] is null) continue;
                        if (entry["uri"] is not JsonValue uv || !uv.TryGetValue<string>(out var uri)) throw new MapException("bad-file", $"The glTF {key} uri is not a string.");
                        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                        string rel = Uri.UnescapeDataString(uri).Replace('\\', '/');
                        if (rel.Length == 0 || rel.Contains(':') || rel.Contains('\0') || rel.StartsWith('/') || rel.Split('/').Any(seg => seg is "" or "." or ".."))
                            throw new MapException("bad-file", $"The glTF refers to '{uri}', which is not a plain relative file under its folder.");
                        string full = Path.GetFullPath(Path.Combine(dir, rel));
                        if (!full.StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new MapException("bad-file", $"The glTF refers to '{uri}' outside its folder.");
                        if (!File.Exists(full)) throw new MapException("bad-file", $"The glTF refers to missing file '{rel}'.");
                        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new MapException("bad-file", $"'{rel}' is a link, not a plain file.");
                        if (!companions.Contains(rel)) companions.Add(rel);
                    }
        }
        return new GltfInfo(Path.GetFileName(path), SceneBounds(root), companions);
    }

    static string GlbJson(byte[] b)
    {
        if (b.Length < 20 || BitConverter.ToUInt32(b, 0) != GlbMagic) throw new MapException("bad-file", "Not a .glb file (magic).");
        if (BitConverter.ToUInt32(b, 4) != 2) throw new MapException("bad-file", "Only glTF 2 binaries are supported.");
        if (BitConverter.ToUInt32(b, 8) > b.Length) throw new MapException("bad-file", "The .glb is truncated.");
        uint length = BitConverter.ToUInt32(b, 12), type = BitConverter.ToUInt32(b, 16);
        if (type != JsonChunk || 20L + length > b.Length) throw new MapException("bad-file", "The .glb has no JSON chunk first.");
        return Encoding.UTF8.GetString(b, 20, (int)length);
    }

    static Bounds? SceneBounds(JsonObject root)
    {
        var nodes = root["nodes"] as JsonArray;
        var roots = new List<int>();
        if (root["scenes"] is JsonArray { Count: > 0 } scenes)
        {
            int s = root["scene"] is JsonValue sv && sv.TryGetValue<int>(out var si) ? si : 0;
            if (s >= 0 && s < scenes.Count && scenes[s]?["nodes"] is JsonArray ns) roots.AddRange(ns.Select(Index).Where(i => i >= 0));
        }
        else if (nodes is not null)
        {
            var children = nodes.SelectMany(n => (n?["children"] as JsonArray)?.Select(Index) ?? []).ToHashSet();
            roots.AddRange(Enumerable.Range(0, nodes.Count).Where(i => !children.Contains(i)));
        }
        Bounds? result = null;
        bool unknown = false;
        int visits = 0;
        void Walk(int index, Matrix4x4 parent, int depth)
        {
            if (++visits > 100_000) throw new MapException("bad-file", "The glTF node tree is too large or cyclic.");
            if (nodes is null || index < 0 || index >= nodes.Count || nodes[index] is not JsonObject node || depth > 64) return;
            var world = Local(node) * parent;
            if (Index(node["mesh"]) is var mi and >= 0 && root["meshes"] is JsonArray meshes && mi < meshes.Count && meshes[mi]?["primitives"] is JsonArray prims)
                foreach (var prim in prims)
                {
                    int acc = Index(prim?["attributes"]?["POSITION"]);
                    if (acc < 0 || root["accessors"] is not JsonArray accessors || acc >= accessors.Count || accessors[acc] is not JsonObject a) continue;
                    if (a["min"] is JsonArray { Count: 3 } mn && a["max"] is JsonArray { Count: 3 } mx)
                    {
                        var box = new Bounds(new Vector3(F(mn[0]), F(mn[1]), F(mn[2])), new Vector3(F(mx[0]), F(mx[1]), F(mx[2]))).Transformed(world);
                        result = result is { } r ? r.Union(box) : box;
                    }
                    else unknown = true;
                }
            if (node["children"] is JsonArray kids) foreach (var k in kids) Walk(Index(k), world, depth + 1);
        }
        foreach (var r in roots) Walk(r, Matrix4x4.Identity, 0);
        return unknown ? null : result;
    }

    static int Index(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1;

    static float F(JsonNode? n) => n is JsonValue v ? (float)v.GetValue<double>() : 0f;

    /// <summary>A node's local matrix (row-vector form): its <c>matrix</c> (column-major in the file, which reads row-major here) or translation, rotation, scale.</summary>
    static Matrix4x4 Local(JsonObject node)
    {
        if (node["matrix"] is JsonArray { Count: 16 } m)
        {
            float[] a = m.Select(F).ToArray();
            return new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
        }
        var t = node["translation"] is JsonArray { Count: 3 } tn ? new Vector3(F(tn[0]), F(tn[1]), F(tn[2])) : Vector3.Zero;
        var s = node["scale"] is JsonArray { Count: 3 } sn ? new Vector3(F(sn[0]), F(sn[1]), F(sn[2])) : Vector3.One;
        var q = node["rotation"] is JsonArray { Count: 4 } rn ? new Quaternion(F(rn[0]), F(rn[1]), F(rn[2]), F(rn[3])) : Quaternion.Identity;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(t);
    }
}
