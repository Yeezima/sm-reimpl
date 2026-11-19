using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SmContent;
using SmFormats.Dcm;

namespace SmMapmaker;

/// <summary>One drawn piece of a custom map's decoration (an SM1 definition or a custom model) with its world matrix in the document frame (Z up), as the client draws it.</summary>
/// <param name="Solid">The compiler made collision from its meshes (false for a part of a prefab whose collision is <c>original-cut</c>).</param>
public sealed record DecorPlacement(string ObjectId, string Name, AssetRef Asset, Matrix4x4 World, bool Solid);

/// <summary>What the compiler made of a map's SM1 placements, prefab instances and custom models (docs/mapmaker.md "SM1 pieces in play").</summary>
public sealed class DecorResult
{
    public List<DecorPlacement> Placements { get; } = [];
    public List<Vector3> Vertices { get; } = [];
    public List<int> Indices { get; } = [];
    /// <summary>The SM1 scenes the placements draw, by map (loaded with exactly the definitions the map uses).</summary>
    public Dictionary<string, MapScene> Scenes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; } = [];
    /// <summary>The library the custom models were read from (the client draws them from the same files).</summary>
    public MapLibrary? Library { get; set; }
    public int Triangles => Indices.Count / 3;
    /// <summary>How many SM1 and custom pieces were placed (drawn), and how many of them are solid.</summary>
    public int Drawn => Placements.Count;

    /// <summary>Things the editor's lenient compile left out that play would refuse (a missing model, an unreadable one, a map not loaded yet): the check reports each as an error.</summary>
    public List<string> Problems { get; } = [];
    /// <summary>The source data the triangles were made from, by name: a hash of each used mesh block of the install, each model file and each original collision.</summary>
    public SortedDictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

    public void Clear() { Placements.Clear(); Vertices.Clear(); Indices.Clear(); Sources.Clear(); }

    /// <summary>
    /// First 8 bytes of SHA-256 over the **source data** of the triangles (the install's mesh blocks, the model files, the original collision), not the transformed result: floating-point
    /// last bits differ between machines (Linux server, Windows clients), the files do not. Two machines whose installs or models differ get different digests, so the map hash refuses to match.
    /// </summary>
    public ulong Digest()
    {
        var sb = new System.Text.StringBuilder("decoration sources\n");
        foreach (var (k, v) in Sources) sb.Append(k).Append('=').Append(v).Append('\n');
        sb.Append("placements=").Append(Placements.Count).Append('\n');
        return BitConverter.ToUInt64(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())), 0);
    }

    /// <summary>Hex SHA-256 of raw arrays (vertex and index data as stored).</summary>
    public static string HashOf(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts) sha.AppendData(part);
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}

/// <summary>
/// Collision (and the list to draw) for the objects that place an SM1 definition, a prefab or a custom model. The same code runs on the server and on every client and builds the same
/// triangles from the same install: the parts an SM1 placement draws (<see cref="Sm1Geometry.DrawnVisuals"/>) transformed by the placement's world matrix. A prefab whose collision is
/// <c>original-cut</c> uses the original map's own collision triangles instead (the ones inside the prefab's box). Hidden objects and parts are neither drawn nor solid.
/// </summary>
public static class DecorGeometry
{
    /// <summary>Most prefab parts (and prefab nodes) one map may unfold to, counted over all instances: a hostile file can nest prefabs to fan out without bound.</summary>
    public const int MaxParts = 100_000;
    /// <summary>Most vertices and indices (three per triangle) all the decoration of one map may add, original-cut copies included.</summary>
    public const int MaxVertices = 10_000_000, MaxIndices = 30_000_000;
    /// <summary>Positions are rounded to this grid (1/4096 m, a power of two, so the rounding is exact) before they reach the collision.</summary>
    public const float Grid = 4096f;

    public static Vector3 Quantise(Vector3 v) => new(MathF.Round(v.X * Grid) / Grid, MathF.Round(v.Y * Grid) / Grid, MathF.Round(v.Z * Grid) / Grid);

    sealed class Budget
    {
        int _parts;
        long _vertices, _indices;
        public void Part()
        {
            if (++_parts > MaxParts) throw new MapException("too-big", $"The map's prefabs unfold to more than {MaxParts:N0} parts; the file is refused.");
        }
        public void Add(int vertices, int indices)
        {
            _vertices += vertices; _indices += indices;
            if (_vertices > MaxVertices || _indices > MaxIndices)
                throw new MapException("too-big", $"The map's SM1 pieces, models and prefabs need more than {MaxIndices / 3:N0} triangles of collision (the limit is {MaxIndices / 3:N0} triangles and {MaxVertices:N0} vertices); the file is refused.");
        }
    }

    public static DecorResult Build(MapDocument doc, CompileOptions? options)
    {
        var result = new DecorResult { Library = options?.Library };
        bool strict = options?.Strict == true;
        try { BuildCore(doc, options, result, new Budget()); }
        catch (MapException e) when (!strict) { result.Clear(); result.Problems.Add(e.Message); result.Notes.Add(e.Message); }
        catch (MapException) { throw; }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or InvalidProgramException))
        {
            // Anything else a hostile or damaged file can provoke (a number out of range, a prefab that is not JSON, memory): a clean refusal, never a crash.
            var refusal = new MapException("bad-file", $"The map's SM1 pieces, prefabs or models could not be compiled ({e.GetType().Name}: {e.Message}).");
            if (strict) throw refusal;
            result.Clear(); result.Problems.Add(refusal.Message); result.Notes.Add(refusal.Message);
        }
        return result;
    }

    static void BuildCore(MapDocument doc, CompileOptions? options, DecorResult result, Budget budget)
    {
        bool strict = options?.Strict == true;
        void Problem(string code, string message)
        {
            if (strict) throw new MapException(code, message);
            result.Problems.Add(message);
            result.Notes.Add(message);
        }

        // 1. The parts, in document order (prefabs unfolded the way the editor unfolds them).
        var parts = new List<(string ObjectId, string Name, AssetRef Asset, double[] World, bool Solid)>();
        var cuts = new List<(string ObjectId, Prefab Prefab, double[] World)>();
        var prefabCache = new Dictionary<string, Prefab?>();
        var embeddedById = doc.EmbeddedPrefabs.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Prefab? GetPrefab(string id)
        {
            return GetPrefabNoCycle(id);   // the cycle and depth check ran once over everything the objects use (below)
        }
        Prefab? GetPrefabNoCycle(string id)
        {
            if (prefabCache.TryGetValue(id, out var have)) return have;
            Prefab? found = embeddedById.GetValueOrDefault(id);
            if (found is null)
            {
                try { found = options?.Library?.GetPrefab(id); }
                catch (Exception e) when (e is MapException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException) { }
                // A prefab that only this PC's library has is part of the world: its definition goes into the digest, so another PC with another copy is refused.
                if (found is not null) result.Sources[$"prefab {id}"] = DecorResult.HashOf(System.Text.Encoding.UTF8.GetBytes(found.ToJson()));
            }
            return prefabCache[id] = found;   // walked-through prefabs are cached too: a library chain is read once
        }
        void Expand(string objectId, string name, string prefabId, double[] world, bool visible, IReadOnlyList<PartOverride> overrides, int depth, bool solid)
        {
            budget.Part();
            if (depth > 16) { Problem("bad-prefab", $"'{objectId}': prefab nesting is too deep at '{prefabId}'."); return; }
            if (GetPrefab(prefabId) is not { } prefab)
            {
                Problem("missing-prefab", $"The map uses the prefab '{prefabId}' ('{name}', {objectId}), which is neither saved in the map file nor in this PC's library. Open the map in the Mapmaker on the PC that made it and save it again.");
                return;
            }
            bool cut = solid && prefab.Collision == "original-cut" && prefab.Provenance is { Source: "sm1", Map: not null };
            if (solid && prefab.Collision == "original-cut" && !cut) result.Notes.Add($"prefab '{prefab.Id}' asks for original-cut collision but does not come from one SM1 map; its meshes are used.");
            if (cut && depth == 0 && overrides.Count > 0) result.Notes.Add($"'{name}' ({objectId}) moves or hides parts of the original-cut prefab '{prefab.Id}': the original collision is cut for the whole prefab as it was extracted, the overrides change only what is drawn.");
            if (cut && visible) cuts.Add((objectId, prefab, world));
            foreach (var part in prefab.Parts)
            {
                budget.Part();
                var ov = depth == 0 ? overrides.FirstOrDefault(x => x.Part == part.Id) : null;
                var matrix = DetMath.Mul(DetMath.Local(ov?.Transform ?? part.Transform), world);
                bool vis = visible && (ov?.Visible ?? true);
                if (part.Asset is { } a) { if (vis) parts.Add((objectId, part.Name.Length > 0 ? part.Name : name, a, matrix, solid && !cut)); }
                else Expand(objectId, name, part.Prefab!, matrix, vis, overrides, depth + 1, solid && !cut);
            }
        }
        // One walk over every prefab the objects use (and everything reachable from them): a pass covers them all, so Expand does not walk again. Its cost is one visit per prefab and edge.
        if (PrefabGraph.FindCycle(doc.Objects.Where(x => x.Prefab is not null).Select(x => x.Prefab!).Distinct(), GetPrefabNoCycle) is { } cyc)
            throw new MapException("prefab-cycle", $"A prefab contains itself: {cyc}.");
        foreach (var o in doc.Objects)
        {
            if (o.Piece is not null || (o.Asset is null && o.Prefab is null)) continue;
            if (!VisibleChain(doc, o)) continue;
            budget.Part();
            double[] world;
            try { world = WorldOf(doc, o); }
            catch (MapException e) when (e.Code != "bad-transform") { result.Notes.Add($"'{o.Name}' ({o.Id}) is left out: {e.Message}"); continue; }
            if (o.Asset is { } asset) parts.Add((o.Id, o.Name, asset, world, true));
            else Expand(o.Id, o.Name, o.Prefab!, world, true, o.Overrides, 0, true);
        }
        if (parts.Count == 0 && cuts.Count == 0) return;

        // 2. The SM1 scenes (one read per map, only the definitions used).
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Where(p => p.Asset.IsSm1))
        {
            if (!wanted.TryGetValue(p.Asset.Map!, out var set)) wanted[p.Asset.Map!] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(p.Asset.Definition!);
        }
        var missingMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (map, set) in wanted.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (options?.Scenes is not { } scenes)
            {
                Problem("missing-install", $"The map uses pieces of the Space Marine map '{map}', and no Space Marine install was found on this PC. Install the game (or pass --game-dir) to play it.");
                missingMaps.Add(map);
                continue;
            }
            try { result.Scenes[map] = scenes.Scene(map, set); }
            catch (MapException e)
            {
                Problem(e.Code, $"The map uses pieces of the Space Marine map '{map}', which this PC cannot provide: {e.Message}");
                missingMaps.Add(map);
            }
        }
        var customCache = new Dictionary<string, (Vector3[] Vertices, int[] Indices)?>();
        var visualCache = new Dictionary<(string Map, string Definition, int Part), IReadOnlyList<VisualPart>?>();

        // 3. One drawn placement per part that can be drawn on this PC; the solid ones add triangles.
        foreach (var (objectId, name, asset, world, solid) in parts)
        {
            if (asset.IsSm1)
            {
                if (missingMaps.Contains(asset.Map!) || !result.Scenes.TryGetValue(asset.Map!, out var scene)) continue;
                var key = (asset.Map!, asset.Definition!, asset.Part ?? -1);
                if (!visualCache.TryGetValue(key, out var visuals))
                {
                    visualCache[key] = visuals = Sm1Geometry.DrawnVisuals(scene, asset.Definition!, asset.Part);
                    if (visuals is null)
                        Problem("missing-sm1-definition", $"The Space Marine map '{asset.Map}' on this PC has no {(asset.Part is { } pp ? $"part {pp} of " : "")}definition '{asset.Definition}' (used by '{name}', {objectId}), or it has nothing to draw. This PC's install is a different version than the map was made with.");
                }
                if (visuals is null) continue;
                result.Placements.Add(new DecorPlacement(objectId, name, asset, DetMath.ToFloat(world), solid));
                if (!solid) continue;
                foreach (var v in visuals) if (scene.Meshes.TryGetValue(v.MeshName, out var mesh) && v.MeshId >= 0 && v.MeshId < mesh.Blocks.Count)
                    {
                        var b = mesh.Blocks[v.MeshId];
                        budget.Add(b.Positions.Length, b.Indices.Length);
                        string sourceKey = $"sm1 {asset.Map}/{v.MeshName}#{v.MeshId}";
                        if (!result.Sources.ContainsKey(sourceKey)) result.Sources[sourceKey] = Sm1Geometry.BlockHash(scene, b);
                    }
                int before = result.Indices.Count;
                Sm1Geometry.AddTriangles(scene, visuals, world, result.Vertices, result.Indices);
                if (result.Indices.Count == before)
                    Problem("empty-sm1-definition", $"The Space Marine definition '{asset.Definition}' (used by '{name}', {objectId}) has no triangles to collide with on this PC.");
            }
            else
            {
                string id = asset.Id!;
                if (!customCache.TryGetValue(id, out var mesh))
                {
                    mesh = null;
                    try
                    {
                        var info = options?.Library?.GetAsset(id);
                        if (info is null) Problem("missing-asset", $"The map uses the imported model '{id}' ('{name}', {objectId}), which is not in this PC's library. Imported models are not saved in the map file: copy the model's folder from the Mapmaker library of the PC that made the map.");
                        else
                        {
                            string file = options!.Library!.AssetFilePath(info);
                            if (!File.Exists(file)) Problem("missing-asset", $"The imported model '{id}' has no file '{info.File}' in this PC's library.");
                            else
                            {
                                mesh = GltfGeometry.Read(file);
                                result.Sources[$"model {id}"] = DecorResult.HashOf(GltfGeometry.FileBytesForHash(file));
                            }
                        }
                    }
                    catch (MapException e) when (!strict || e.Code is "bad-asset") { Problem(e.Code, e.Message); }
                    customCache[id] = mesh;
                }
                if (mesh is not { } m) continue;
                result.Placements.Add(new DecorPlacement(objectId, name, asset, DetMath.ToFloat(world), solid));
                if (!solid) continue;
                budget.Add(m.Vertices.Length, m.Indices.Length);
                if (m.Indices.Length == 0) Problem("empty-asset", $"The imported model '{id}' has no triangles.");
                int first = result.Vertices.Count;
                foreach (var v in m.Vertices) { var (x, y, z) = DetMath.Transform(v.X, v.Y, v.Z, world); result.Vertices.Add(DetMath.Grid(x, y, z)); }
                foreach (int i in m.Indices) result.Indices.Add(first + i);
            }
        }

        // 4. Prefabs with original-cut collision: the original map's triangles inside the prefab's box.
        var cutTriangles = new Dictionary<string, (Vector3[] Vertices, int[] Indices)>();
        foreach (var (objectId, prefab, world) in cuts)
        {
            var map = prefab.Provenance.Map!;
            if (options?.Scenes is not { } scenes)
            {
                Problem("missing-install", $"The prefab '{prefab.Id}' uses the original collision of '{map}', and no Space Marine install was found on this PC.");
                continue;
            }
            if (!cutTriangles.TryGetValue(prefab.Id, out var cut))
            {
                try
                {
                    var box = PrefabBox(prefab, GetPrefab, result.Scenes, depth: 0);
                    var original = scenes.OriginalCollision(map);
                    result.Sources.TryAdd($"original {map}", DecorResult.HashOf(MemoryMarshal.AsBytes<Vector3>(original.Vertices).ToArray(), MemoryMarshal.AsBytes<int>(original.Indices).ToArray()));
                    cut = box is { } b ? CutOriginal(original, b, prefab.Origin) : ([], []);
                    if (box is null) result.Notes.Add($"prefab '{prefab.Id}' (original-cut): its box is not known, so it has no collision.");
                }
                catch (MapException e) { Problem(e.Code, e.Message); cut = ([], []); }
                cutTriangles[prefab.Id] = cut;
            }
            budget.Add(cut.Vertices.Length, cut.Indices.Length);
            int first = result.Vertices.Count;
            foreach (var v in cut.Vertices) { var (x, y, z) = DetMath.Transform(v.X, v.Y, v.Z, world); result.Vertices.Add(DetMath.Grid(x, y, z)); }
            foreach (int i in cut.Indices) result.Indices.Add(first + i);
        }
    }
    /// <summary>The object's world matrix, in double, through <see cref="DetMath"/>: its local matrix times its parent chain's (row vectors), the same on every OS.</summary>
    internal static double[] WorldOf(MapDocument doc, MapObject o)
    {
        var m = DetMath.Local(o.Transform);
        int guard = 0;
        for (var cur = o; cur.Parent is { } p; )
        {
            if (++guard > 1000) throw new MapException("parent-cycle", $"'{o.Id}' is in a parent cycle.", o.Id);
            cur = doc.Find(p) ?? throw new MapException("unknown-id", $"'{cur.Id}' has unknown parent '{p}'.", cur.Id);
            m = DetMath.Mul(m, DetMath.Local(cur.Transform));
        }
        return m;
    }

    static bool VisibleChain(MapDocument doc, MapObject o)
    {
        int guard = 0;
        for (var cur = o; cur is not null && guard++ < 1000; cur = cur.Parent is { } p ? doc.Find(p) : null)
            if (!cur.Visible) return false;
        return true;
    }

    /// <summary>The prefab's box in its own frame: the stored one, else the union of its SM1 parts' boxes (null when a part's box is unknown).</summary>
    static Bounds? PrefabBox(Prefab prefab, Func<string, Prefab?> get, Dictionary<string, MapScene> scenes, int depth)
    {
        if (prefab.Bounds is { } stored) return stored;
        if (depth > 16) return null;
        Bounds? total = null;
        foreach (var part in prefab.Parts)
        {
            Bounds? local;
            if (part.Asset is { IsSm1: true } a && scenes.TryGetValue(a.Map!, out var scene)) local = Sm1Geometry.BoundsOf(scene, a.Definition!, a.Part);
            else if (part.Prefab is { } pid && get(pid) is { } nested) local = PrefabBox(nested, get, scenes, depth + 1);
            else return null;
            if (local is not { } lb) return null;
            var b = lb.Transformed(part.Transform.ToMatrix());
            total = total is { } t ? t.Union(b) : b;
        }
        return total;
    }

    /// <summary>
    /// The part of <paramref name="original"/> (the original map's collision, source frame) that lies inside <paramref name="box"/> (the prefab's frame; plus its origin that is the source frame),
    /// cut out triangle by triangle (each triangle is clipped to the box, so a long ground triangle gives only the piece under the prefab) and moved into the prefab's frame (minus the origin).
    /// A quarter metre of slack so a floor under the prefab's bottom is kept (ours; interpretation).
    /// </summary>
    static (Vector3[], int[]) CutOriginal((Vector3[] Vertices, int[] Indices) original, Bounds box, Vector3 origin)
    {
        const float slack = 0.25f;
        var lo = box.Min + origin - new Vector3(slack);
        var hi = box.Max + origin + new Vector3(slack);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var polygon = new List<Vector3>(12);
        var next = new List<Vector3>(12);
        for (int t = 0; t + 2 < original.Indices.Length; t += 3)
        {
            Vector3 a = original.Vertices[original.Indices[t]], b = original.Vertices[original.Indices[t + 1]], c = original.Vertices[original.Indices[t + 2]];
            var tlo = Vector3.Min(a, Vector3.Min(b, c)); var thi = Vector3.Max(a, Vector3.Max(b, c));
            if (tlo.X > hi.X || tlo.Y > hi.Y || tlo.Z > hi.Z || thi.X < lo.X || thi.Y < lo.Y || thi.Z < lo.Z) continue;
            polygon.Clear(); polygon.Add(a); polygon.Add(b); polygon.Add(c);
            for (int axis = 0; axis < 3 && polygon.Count >= 3; axis++)
            {
                Clip(polygon, next, axis, lo, hi, keepAbove: true);
                Clip(next, polygon, axis, lo, hi, keepAbove: false);
            }
            if (polygon.Count < 3) continue;
            int first = vertices.Count;
            foreach (var p in polygon) vertices.Add(p - origin);
            for (int k = 1; k + 1 < polygon.Count; k++) indices.AddRange([first, first + k, first + k + 1]);
        }
        return ([.. vertices], [.. indices]);
    }

    static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>One Sutherland-Hodgman step: keeps the part of <paramref name="from"/> on one side of the box's plane on <paramref name="axis"/>, into <paramref name="into"/>.</summary>
    static void Clip(List<Vector3> from, List<Vector3> into, int axis, Vector3 lo, Vector3 hi, bool keepAbove)
    {
        into.Clear();
        float plane = Axis(keepAbove ? lo : hi, axis);
        bool Inside(Vector3 v) => keepAbove ? Axis(v, axis) >= plane : Axis(v, axis) <= plane;
        for (int i = 0; i < from.Count; i++)
        {
            var cur = from[i]; var prev = from[(i + from.Count - 1) % from.Count];
            bool curIn = Inside(cur), prevIn = Inside(prev);
            if (curIn != prevIn)
            {
                float d = Axis(cur, axis) - Axis(prev, axis);
                float s = d == 0f ? 0f : (plane - Axis(prev, axis)) / d;
                into.Add(prev + (cur - prev) * s);
            }
            if (curIn) into.Add(cur);
        }
    }
}

/// <summary>The meshes of SM1 placements: which parts a placement draws and their triangles (shared by the compiler and the client's drawing, so what is drawn is what is solid).</summary>
public static class Sm1Geometry
{
    /// <summary>
    /// The visual parts a placement of <paramref name="definition"/> draws: with a <paramref name="part"/> that one part of the nearest LOD (as the editor's picks index it), else the client's
    /// default rule for the map (<see cref="MapScene.DrawnParts"/>: highest LOD of each family, plus the members that the better ones do not cover on the ten imported Classic maps). Null when
    /// the definition is not in the scene, the part does not exist, or nothing is left to draw.
    /// </summary>
    public static IReadOnlyList<VisualPart>? DrawnVisuals(MapScene scene, string definition, int? part)
    {
        if (!scene.Definitions.TryGetValue(definition, out var def)) return null;
        IReadOnlyList<VisualPart> list;
        if (part is { } p)
        {
            var near = def.NearestLod().ToList();
            if (p < 0 || p >= near.Count) return null;
            list = [near[p]];
        }
        else list = scene.DrawnParts(def, oldRule: false, keepUncovered: KeepsUncovered(scene.Map));
        return list.Count == 0 ? null : list;
    }

    /// <summary>The client's <c>MapDecorLists.KeepsUncoveredLodMembers</c> for its default flags: the imported Classic maps (<c>mp_*</c> but Manufactorum).</summary>
    public static bool KeepsUncovered(string map) => map.StartsWith("mp_", StringComparison.OrdinalIgnoreCase) && !map.Equals("mp_tdm_09", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a surface is a sky box (the engine draws those around the camera; the client does not draw them as map geometry, so they are not solid).</summary>
    public static bool IsSky(MapScene scene, string material) => scene.Materials.TryGetValue(material, out var m) && m.ShaderName?.StartsWith("sky_", StringComparison.Ordinal) == true;

    /// <summary>Adds the triangles of <paramref name="visuals"/> placed by <paramref name="world"/> (the placement's matrix; each part's own node transform comes first). Surfaces are two-sided in the collision, so mirrored placements need no flipping.</summary>
    public static void AddTriangles(MapScene scene, IReadOnlyList<VisualPart> visuals, double[] world, List<Vector3> vertices, List<int> indices)
    {
        foreach (var part in visuals)
        {
            if (!scene.Meshes.TryGetValue(part.MeshName, out var mesh) || part.MeshId < 0 || part.MeshId >= mesh.Blocks.Count) continue;
            var block = mesh.Blocks[part.MeshId];
            var matrix = DetMath.Mul(part.DetNode?.Values ?? DetMath.FromFloat(part.NodeTransform), world);   // the node transform rebuilt in double from the raw node data
            // Only the ranges that will be used decide which vertices are copied; the block's positions are moved once.
            var ranges = new List<(int Start, int Count)>();
            foreach (var range in block.Materials)
            {
                if (IsSky(scene, range.Name)) continue;
                int count = range.IndexCount - range.IndexCount % 3;
                if (count < 3 || range.StartIndex < 0 || range.StartIndex + count > block.Indices.Length) continue;
                ranges.Add((range.StartIndex, count));
            }
            if (ranges.Count == 0) continue;
            int first = vertices.Count;
            foreach (var p in block.Positions) { var (x, y, z) = DetMath.Transform(p.X, p.Y, p.Z, matrix); vertices.Add(DetMath.Grid(x, y, z)); }
            foreach (var (start, count) in ranges)
                for (int i = start; i < start + count; i += 3)
                {
                    int a = block.Indices[i], b = block.Indices[i + 1], c = block.Indices[i + 2];
                    if (a >= block.Positions.Length || b >= block.Positions.Length || c >= block.Positions.Length) continue;
                    indices.Add(first + a); indices.Add(first + b); indices.Add(first + c);
                }
        }
    }

    /// <summary>Hex SHA-256 of a mesh block as the install stores it (positions, indices, and which ranges are used or skipped as sky): the same on every OS for the same install.</summary>
    public static string BlockHash(MapScene scene, DcmBlock block)
    {
        var ranges = new System.Text.StringBuilder();
        foreach (var r in block.Materials) ranges.Append(r.StartIndex).Append(':').Append(r.IndexCount).Append(':').Append(IsSky(scene, r.Name) ? 1 : 0).Append(';');
        return DecorResult.HashOf(System.Runtime.InteropServices.MemoryMarshal.AsBytes<Vector3>(block.Positions).ToArray(), System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(block.Indices).ToArray(), System.Text.Encoding.UTF8.GetBytes(ranges.ToString()));
    }

    /// <summary>The box (in the definition's own frame) of what a placement draws, from the blocks' stored boxes; null when nothing is known.</summary>
    public static Bounds? BoundsOf(MapScene scene, string definition, int? part)
    {
        if (DrawnVisuals(scene, definition, part) is not { } visuals) return null;
        Bounds? total = null;
        foreach (var v in visuals)
        {
            if (!scene.Meshes.TryGetValue(v.MeshName, out var mesh) || v.MeshId < 0 || v.MeshId >= mesh.Blocks.Count) continue;
            var b = new Bounds(mesh.Blocks[v.MeshId].BoundsMin, mesh.Blocks[v.MeshId].BoundsMax).Transformed(v.NodeTransform);
            total = total is { } t ? t.Union(b) : b;
        }
        return total;
    }
}
