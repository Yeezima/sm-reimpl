using System.Numerics;

namespace SmContent;

/// <summary>
/// Connected shells of a triangle mesh (M35): triangles that share a position belong to one shell, however the vertices are split for
/// normals and texture seams. The Chaos bolter and bolt pistol draw their magazine as a shell of the same mesh block as the gun body, so the
/// magazine can only be told apart by where its shell lies (<see cref="WeaponModel.EmbeddedMagazine"/>).
/// </summary>
public static class MeshShells
{
    /// <summary>
    /// Labels every vertex with the shell of the triangles that use it (-1 for a vertex no triangle uses). Positions closer than
    /// <paramref name="weld"/> in every axis (after rounding to that grid) are one point.
    /// </summary>
    /// <returns>The shell of each vertex and the number of shells.</returns>
    public static (int[] ShellOfVertex, int Count) Label(IReadOnlyList<Vector3> positions, IReadOnlyList<ushort> indices, float weld = 1e-4f)
    {
        var ids = new Dictionary<(long, long, long), int>();
        var parent = new List<int>();
        var pointOfVertex = new int[positions.Count];
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        for (int v = 0; v < positions.Count; v++)
        {
            var p = positions[v];
            var key = ((long)MathF.Round(p.X / weld), (long)MathF.Round(p.Y / weld), (long)MathF.Round(p.Z / weld));
            if (!ids.TryGetValue(key, out int id)) { id = parent.Count; parent.Add(id); ids[key] = id; }
            pointOfVertex[v] = id;
        }
        var used = new bool[positions.Count];
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if ((uint)a >= (uint)positions.Count || (uint)b >= (uint)positions.Count || (uint)c >= (uint)positions.Count) continue;
            used[a] = used[b] = used[c] = true;
            parent[Find(pointOfVertex[a])] = Find(pointOfVertex[b]);
            parent[Find(pointOfVertex[b])] = Find(pointOfVertex[c]);
        }
        var shellOfRoot = new Dictionary<int, int>();
        var result = new int[positions.Count];
        for (int v = 0; v < positions.Count; v++)
        {
            if (!used[v]) { result[v] = -1; continue; }
            int root = Find(pointOfVertex[v]);
            if (!shellOfRoot.TryGetValue(root, out int shell)) shellOfRoot[root] = shell = shellOfRoot.Count;
            result[v] = shell;
        }
        return (result, shellOfRoot.Count);
    }

    /// <summary>
    /// The shells whose bounding box's centre lies in the box (<paramref name="min"/>, <paramref name="max"/>), with the transform applied to
    /// the positions first: the vertices of those shells are marked.
    /// </summary>
    /// <param name="maxShells">When above 0, only that many of the picked shells are kept: the ones with the most triangles.</param>
    /// <returns>Per vertex whether it belongs to a picked shell, and the number of picked shells.</returns>
    public static (bool[] Picked, int Shells) PickInBox(IReadOnlyList<Vector3> positions, IReadOnlyList<ushort> indices, Matrix4x4 transform, Vector3 min, Vector3 max, int maxShells = 0)
    {
        var (shellOf, count) = Label(positions, indices);
        var low = new Vector3[count];
        var high = new Vector3[count];
        for (int s = 0; s < count; s++) { low[s] = new Vector3(float.MaxValue); high[s] = new Vector3(float.MinValue); }
        for (int v = 0; v < positions.Count; v++)
        {
            if (shellOf[v] < 0) continue;
            var p = Vector3.Transform(positions[v], transform);
            low[shellOf[v]] = Vector3.Min(low[shellOf[v]], p);
            high[shellOf[v]] = Vector3.Max(high[shellOf[v]], p);
        }
        var triangles = new int[count];
        for (int i = 0; i + 2 < indices.Count; i += 3)
            if ((uint)indices[i] < (uint)positions.Count && shellOf[indices[i]] >= 0) triangles[shellOf[indices[i]]]++;
        var inside = new bool[count];
        int picked = 0;
        for (int s = 0; s < count; s++)
        {
            var centre = (low[s] + high[s]) * 0.5f;
            if (centre.X >= min.X && centre.X <= max.X && centre.Y >= min.Y && centre.Y <= max.Y && centre.Z >= min.Z && centre.Z <= max.Z) { inside[s] = true; picked++; }
        }
        if (maxShells > 0 && picked > maxShells)
        {
            var keep = Enumerable.Range(0, count).Where(s => inside[s]).OrderByDescending(s => triangles[s]).Take(maxShells).ToHashSet();
            for (int s = 0; s < count; s++) inside[s] &= keep.Contains(s);
            picked = maxShells;
        }
        var result = new bool[positions.Count];
        for (int v = 0; v < positions.Count; v++) result[v] = shellOf[v] >= 0 && inside[shellOf[v]];
        return (result, picked);
    }
}
