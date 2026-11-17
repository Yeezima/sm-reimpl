using System.Numerics;
using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// The movable cover the level's baked collision holds as Havok layer "3" (classic-maps-fidelity gameplay, 2026-11-18). A layer
/// named by a number is the <c>BodyType</c> of the bodies in it (every placement of a definition whose body has <c>BodyType</c> 3
/// has its shape in layer "3"); <c>BodyType</c> 3 is the movable and destructible cover: the green <c>ammo_crate_A</c> of Manufactorum
/// and Station Tertius and Dome Mechanicus (their shapes are <c>ppk</c> entries instead, <see cref="MapProps"/>), and on the other
/// maps the <c>healthy</c> states of <c>crate_a</c> (Hab Center, 35), <c>crate_b</c> (Desolation, 15), <c>barricade_sandbag_pile</c>
/// (Desolation, 4), <c>rubble_cover_b</c> / <c>_c</c> (Aquila Canyon, Desolation) and <c>barrel_exploding</c> (Chem Refinery, 3),
/// whose triangle counts match the placements (12 a crate). Neither character nor weapon collision (layers "2" and "8") holds them
/// (a triangle count of the layer is all new geometry on those four maps), so before this change a crate was walked and shot
/// through. Each connected set of triangles (a crate, a barrel, a pile: triangles that share vertices) is one prop, read as the
/// M39 props are (solid for characters and shots, pierceable; spec/behavior.md).
/// </summary>
public static class BakedProps
{
    /// <summary>The Havok layer name of <c>BodyType</c> 3 bodies.</summary>
    public const string LayerName = "3";

    /// <summary>
    /// One <see cref="MapProp"/> per set of triangles of <paramref name="layer"/> that share vertices, ordered by the set's lowest
    /// corner (Z, then X, then Y); the surface id is the commonest triangle material of the set (0 when the layer has none).
    /// </summary>
    public static List<MapProp> Read(HavokCollisionLayer? layer)
    {
        var props = new List<MapProp>();
        if (layer is null || layer.TriangleCount == 0) return props;
        int triangles = layer.TriangleCount;
        var parent = new int[triangles];
        for (int i = 0; i < triangles; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var owner = new Dictionary<int, int>();   // vertex index -> a triangle that uses it
        for (int t = 0; t < triangles; t++)
            for (int k = 0; k < 3; k++)
            {
                int v = layer.Indices[3 * t + k];
                if (owner.TryGetValue(v, out int other)) parent[Find(t)] = Find(other);
                else owner[v] = t;
            }
        var sets = new Dictionary<int, List<int>>();
        for (int t = 0; t < triangles; t++)
        {
            int root = Find(t);
            if (!sets.TryGetValue(root, out var list)) sets[root] = list = [];
            list.Add(t);
        }
        var found = new List<(Vector3 Low, MapProp Prop)>();
        foreach (var set in sets.Values)
        {
            var remap = new Dictionary<int, int>();
            var vertices = new List<Vector3>();
            var indices = new List<int>();
            foreach (int t in set)
                for (int k = 0; k < 3; k++)
                {
                    int v = layer.Indices[3 * t + k];
                    if (!remap.TryGetValue(v, out int local)) { local = vertices.Count; remap[v] = local; vertices.Add(layer.Vertices[v]); }
                    indices.Add(local);
                }
            int surface = layer.Materials.Length == triangles
                ? set.Select(t => layer.Materials[t]).Where(m => m >= 0).GroupBy(m => m).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).DefaultIfEmpty(0).First()
                : 0;
            var low = vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            found.Add((low, new MapProp("", "hkt layer 3", "wrld.hkt", surface, [.. vertices], [.. indices])));
        }
        int n = 0;
        foreach (var (_, p) in found.OrderBy(f => f.Low.Z).ThenBy(f => f.Low.X).ThenBy(f => f.Low.Y))
            props.Add(p with { Name = $"layer3_{++n:00}" });
        return props;
    }
}
