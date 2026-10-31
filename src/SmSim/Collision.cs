using System.Numerics;

namespace SmSim;

/// <summary>A ray's nearest hit on a <see cref="StaticMesh"/>. <see cref="Normal"/> is the unit face normal turned to face the ray origin.</summary>
public readonly record struct RayHit(float Distance, Vector3 Point, Vector3 Normal, int Triangle);

/// <summary>
/// World-space static triangles (Z up, metres) with a bounding volume hierarchy built deterministically in the constructor
/// (median split on the longest centroid axis, ties broken by triangle index), so every process that builds the same data
/// gets the same tree and the same answers. Triangles are two-sided. Degenerate triangles (zero area or non-finite
/// vertices) are left out; <see cref="RayHit.Triangle"/> numbers triangles as in the index buffer (index / 3).
/// </summary>
public sealed class StaticMesh
{
    const int LeafSize = 4;
    const float EdgeTolerance = 1e-6f;

    /// <summary>Count &gt; 0: leaf over slots [Start, Start + Count). Count == 0: inner node, children at Start and Start + 1.</summary>
    struct Node
    {
        public Vector3 Min;
        public int Start;
        public Vector3 Max;
        public int Count;
    }

    readonly Node[] _nodes;
    // Triangle data in BVH slot order: first vertex, the two edges from it, the unit face normal, the original triangle index.
    readonly Vector3[] _v0, _e1, _e2, _n;
    readonly int[] _tri;

    /// <summary>Number of triangles kept (degenerate ones are not counted).</summary>
    public int TriangleCount => _tri.Length;
    /// <summary>Bounds of the kept triangles (zero for an empty mesh).</summary>
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    /// <summary>FNV-1a 64 over the vertex and index counts, every vertex float's bits and every index, little-endian.</summary>
    public ulong ContentHash { get; }
    /// <summary>Triangles in the index buffer(s) the mesh was made from, degenerate ones included (the next <see cref="RayHit.Triangle"/> number).</summary>
    internal int SourceTriangles { get; }

    // Surface material id per source triangle number (empty: none).
    readonly int[] _materials;

    /// <summary>
    /// The surface material id of triangle <paramref name="triangle"/> (numbered as in the index buffer, as
    /// <see cref="RayHit.Triangle"/>), or -1 when the mesh has no materials or the triangle is out of range (M25a; the level's
    /// ids and their meaning: <c>SmFormats.Havok.HavokCollisionLayer.Materials</c>, spec/behavior.md). Triangles added by
    /// <see cref="With"/> (spawn-room blockers) have none (-1). Not part of <see cref="ContentHash"/>.
    /// </summary>
    public int MaterialOf(int triangle) => (uint)triangle < (uint)_materials.Length ? _materials[triangle] : -1;

    /// <summary>Whether the mesh carries per-triangle materials.</summary>
    public bool HasMaterials => _materials.Length > 0;

    /// <summary>
    /// Kept triangles before the tree is built, in candidate order (which breaks centroid ties): first vertex, the two edges from
    /// it, unit normal, the triangle's number, its bounds and centroid.
    /// </summary>
    sealed class Candidates(int capacity)
    {
        public readonly List<Vector3> V0 = new(capacity), E1 = new(capacity), E2 = new(capacity), N = new(capacity), Lo = new(capacity), Hi = new(capacity), Centroid = new(capacity);
        public readonly List<int> Tri = new(capacity);

        /// <summary>Adds triangle <paramref name="t"/> (a, b, c) unless it is degenerate.</summary>
        public void Add(int t, Vector3 a, Vector3 b, Vector3 c)
        {
            if (!Geometry.Finite(a) || !Geometry.Finite(b) || !Geometry.Finite(c)) return;
            if (!(Vector3.Cross(b - a, c - a).LengthSquared() > 1e-16f)) return;
            Add(t, a, b - a, c - a, Vector3.Normalize(Vector3.Cross(b - a, c - a)), b, c);
        }

        void Add(int t, Vector3 v0, Vector3 e1, Vector3 e2, Vector3 n, Vector3 b, Vector3 c)
        {
            V0.Add(v0); E1.Add(e1); E2.Add(e2); N.Add(n); Tri.Add(t);
            Lo.Add(Vector3.Min(v0, Vector3.Min(b, c)));
            Hi.Add(Vector3.Max(v0, Vector3.Max(b, c)));
            Centroid.Add((v0 + b + c) / 3f);
        }
    }

    public StaticMesh(Vector3[] vertices, int[] indices) : this(vertices, indices, null) { }

    /// <param name="materials">One surface material id per triangle (index count / 3 entries), or null for none.</param>
    public StaticMesh(Vector3[] vertices, int[] indices, int[]? materials)
        : this(Prepare(vertices, indices), Hash(vertices, indices), indices.Length / 3)
    {
        if (materials is not null && materials.Length != indices.Length / 3)
            throw new ArgumentException($"{materials.Length} materials for {indices.Length / 3} triangles", nameof(materials));
        _materials = materials is null ? [] : (int[])materials.Clone();
    }

    static Candidates Prepare(Vector3[] vertices, int[] indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length % 3 != 0) throw new ArgumentException($"index count {indices.Length} is not a multiple of 3", nameof(indices));
        for (int k = 0; k < indices.Length; k++)
            if ((uint)indices[k] >= (uint)vertices.Length)
                throw new ArgumentOutOfRangeException(nameof(indices), $"index {indices[k]} at position {k} is outside the {vertices.Length} vertices");
        int triangles = indices.Length / 3;
        var kept = new Candidates(triangles);
        for (int t = 0; t < triangles; t++)
            kept.Add(t, vertices[indices[3 * t]], vertices[indices[3 * t + 1]], vertices[indices[3 * t + 2]]);
        return kept;
    }

    StaticMesh(Candidates kept, ulong hash, int sourceTriangles)
    {
        _materials = [];
        ContentHash = hash;
        SourceTriangles = sourceTriangles;
        int count = kept.Tri.Count;
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        Vector3[] lo = [.. kept.Lo], hi = [.. kept.Hi], centroid = [.. kept.Centroid];
        var nodes = new List<Node>(Math.Max(1, 2 * count / LeafSize + 1));
        if (count > 0)
        {
            nodes.Add(default);
            Build(nodes, 0, order, 0, count, lo, hi, centroid);
            Min = nodes[0].Min;
            Max = nodes[0].Max;
        }
        _nodes = nodes.ToArray();

        _v0 = new Vector3[count]; _e1 = new Vector3[count]; _e2 = new Vector3[count]; _n = new Vector3[count];
        _tri = new int[count];
        for (int slot = 0; slot < count; slot++)
        {
            int k = order[slot];
            _v0[slot] = kept.V0[k]; _e1[slot] = kept.E1[k]; _e2[slot] = kept.E2[k]; _n[slot] = kept.N[k];
            _tri[slot] = kept.Tri[k];
        }
    }

    StaticMesh(Node[] nodes, Vector3[] v0, Vector3[] e1, Vector3[] e2, Vector3[] n, int[] tri, ulong hash, int sourceTriangles, int[] materials)
    {
        _nodes = nodes; _v0 = v0; _e1 = e1; _e2 = e2; _n = n; _tri = tri; _materials = materials;
        ContentHash = hash;
        SourceTriangles = sourceTriangles;
        if (nodes.Length > 0) { Min = nodes[0].Min; Max = nodes[0].Max; }
    }

    /// <summary>
    /// This mesh plus the triangles of <paramref name="vertices"/> / <paramref name="indices"/> (numbered after this mesh's, in
    /// their order). The added triangles get a tree of their own, grafted beside this mesh's tree under a new root: this
    /// mesh's triangles, slots and tree are taken over unchanged (copied), so a query that meets none of the added triangles
    /// gives exactly this mesh's answer and visits this mesh's triangles in the same order (cost: a copy, no rebuild).
    /// <see cref="ContentHash"/>: FNV-1a 64 of this mesh's hash and the added triangles' (as for a mesh of their own).
    /// </summary>
    /// <param name="addedMaterials">Surface material ids of the added triangles (one per triangle; M39 props), or null for none (-1).
    /// Given to a mesh without materials, its own triangles get -1.</param>
    public StaticMesh With(Vector3[] vertices, int[] indices, int[]? addedMaterials = null)
    {
        if (addedMaterials is not null && addedMaterials.Length != indices.Length / 3)
            throw new ArgumentException($"{addedMaterials.Length} materials for {indices.Length / 3} triangles", nameof(addedMaterials));
        var extra = new StaticMesh(Prepare(vertices, indices), 0, 0);
        ulong hash = Geometry.Fnv(ContentHash, Hash(vertices, indices));
        int sources = SourceTriangles + indices.Length / 3;
        int slots = _tri.Length, added = extra._tri.Length;
        Vector3[] v0 = [.. _v0, .. extra._v0], e1 = [.. _e1, .. extra._e1], e2 = [.. _e2, .. extra._e2], n = [.. _n, .. extra._n];
        var tri = new int[slots + added];
        Array.Copy(_tri, tri, slots);
        for (int k = 0; k < added; k++) tri[slots + k] = SourceTriangles + extra._tri[k];
        // Materials stay indexed by source triangle number: this mesh's, then the added ones' (none: -1).
        int[] materials = addedMaterials is not null ? [.. HasMaterials ? _materials : Enumerable.Repeat(-1, SourceTriangles), .. addedMaterials]
            : HasMaterials ? [.. _materials, .. Enumerable.Repeat(-1, indices.Length / 3)] : [];
        if (added == 0) return new StaticMesh([.. _nodes], v0, e1, e2, n, tri, hash, sources, materials);
        if (slots == 0) return new StaticMesh([.. extra._nodes], v0, e1, e2, n, tri, hash, sources, materials);

        // Layout: 0 the new root, 1 this mesh's root, 2 the added root, then this mesh's other nodes, then the added ones'
        // (an inner node's two children stay next to each other; added leaves point past this mesh's slots).
        int b = _nodes.Length, e = extra._nodes.Length;
        int Base(int i) => i == 0 ? 1 : 2 + i;
        int Added(int j) => j == 0 ? 2 : b + 1 + j;
        var nodes = new Node[1 + b + e];
        for (int i = 0; i < b; i++)
        {
            var node = _nodes[i];
            if (node.Count == 0) node.Start = Base(node.Start);
            nodes[Base(i)] = node;
        }
        for (int j = 0; j < e; j++)
        {
            var node = extra._nodes[j];
            node.Start = node.Count == 0 ? Added(node.Start) : node.Start + slots;
            nodes[Added(j)] = node;
        }
        nodes[0] = new Node { Min = Vector3.Min(_nodes[0].Min, extra._nodes[0].Min), Max = Vector3.Max(_nodes[0].Max, extra._nodes[0].Max), Start = 1, Count = 0 };
        return new StaticMesh(nodes, v0, e1, e2, n, tri, hash, sources, materials);
    }

    static void Build(List<Node> nodes, int index, int[] order, int start, int count, Vector3[] lo, Vector3[] hi, Vector3[] centroid)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue), cmin = new(float.MaxValue), cmax = new(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            int t = order[i];
            min = Vector3.Min(min, lo[t]); max = Vector3.Max(max, hi[t]);
            cmin = Vector3.Min(cmin, centroid[t]); cmax = Vector3.Max(cmax, centroid[t]);
        }
        if (count <= LeafSize)
        {
            nodes[index] = new Node { Min = min, Max = max, Start = start, Count = count };
            return;
        }
        var extent = cmax - cmin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        order.AsSpan(start, count).Sort(new CentroidOrder(centroid, axis));
        int left = nodes.Count;
        nodes.Add(default);
        nodes.Add(default);
        nodes[index] = new Node { Min = min, Max = max, Start = left, Count = 0 };
        int half = count / 2;
        Build(nodes, left, order, start, half, lo, hi, centroid);
        Build(nodes, left + 1, order, start + half, count - half, lo, hi, centroid);
    }

    /// <summary>Orders triangles by centroid on one axis, then by index, so the order is unique whatever the sort algorithm.</summary>
    readonly struct CentroidOrder(Vector3[] centroid, int axis) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            int c = Geometry.Axis(centroid[x], axis).CompareTo(Geometry.Axis(centroid[y], axis));
            return c != 0 ? c : x.CompareTo(y);
        }
    }

    /// <summary>
    /// Nearest hit along the ray within [0, <paramref name="maxDistance"/>] (inclusive), either face. <paramref name="direction"/>
    /// must be unit length. Equal distances resolve to the lowest triangle index, so the answer does not depend on the tree.
    /// </summary>
    public RayHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (_nodes.Length == 0 || !(maxDistance >= 0f) || !Geometry.Finite(origin) || !Geometry.Finite(direction)) return null;
        var inv = new Vector3(Inverse(direction.X), Inverse(direction.Y), Inverse(direction.Z));
        float best = maxDistance;
        int bestSlot = -1, bestTri = int.MaxValue;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly Node node = ref _nodes[stack[--sp]];
            if (!SlabEntry(node.Min, node.Max, origin, inv, best, out _)) continue;
            if (node.Count > 0)
            {
                for (int slot = node.Start; slot < node.Start + node.Count; slot++)
                {
                    if (!RayTriangle(origin, direction, slot, out float t) || t > best) continue;
                    int tri = _tri[slot];
                    if (t < best || tri < bestTri) { best = t; bestSlot = slot; bestTri = tri; }
                }
                continue;
            }
            int l = node.Start, r = node.Start + 1;
            bool hl = SlabEntry(_nodes[l].Min, _nodes[l].Max, origin, inv, best, out float tl);
            bool hr = SlabEntry(_nodes[r].Min, _nodes[r].Max, origin, inv, best, out float tr);
            // Push the farther child first so the nearer one is searched first and shrinks `best` sooner.
            if (hl && hr)
            {
                if (tl <= tr) { stack[sp++] = r; stack[sp++] = l; }
                else { stack[sp++] = l; stack[sp++] = r; }
            }
            else if (hl) stack[sp++] = l;
            else if (hr) stack[sp++] = r;
        }
        if (bestSlot < 0) return null;
        var normal = _n[bestSlot];
        if (Vector3.Dot(normal, direction) > 0f) normal = -normal;
        return new RayHit(best, origin + direction * best, normal, bestTri);
    }

    static float Inverse(float d) => 1f / (MathF.Abs(d) < 1e-20f ? (d < 0f ? -1e-20f : 1e-20f) : d);

    static bool SlabEntry(Vector3 min, Vector3 max, Vector3 o, Vector3 inv, float limit, out float entry)
    {
        var t1 = (min - o) * inv;
        var t2 = (max - o) * inv;
        var near = Vector3.Min(t1, t2);
        var far = Vector3.Max(t1, t2);
        entry = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));
        float exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, limit));
        return entry <= exit;
    }

    /// <summary>Möller–Trumbore, both faces, with a small tolerance on the edges so shared edges leave no cracks.</summary>
    bool RayTriangle(Vector3 o, Vector3 d, int slot, out float t)
    {
        t = 0f;
        var e1 = _e1[slot]; var e2 = _e2[slot];
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float invDet = 1f / det;
        var s = o - _v0[slot];
        float u = Vector3.Dot(s, p) * invDet;
        if (u < -EdgeTolerance || u > 1f + EdgeTolerance) return false;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * invDet;
        if (v < -EdgeTolerance || u + v > 1f + EdgeTolerance) return false;
        t = Vector3.Dot(e2, q) * invDet;
        return t >= 0f;
    }

    /// <summary>
    /// How far a sphere of <paramref name="radius"/> travels from <paramref name="origin"/> along the unit <paramref name="direction"/> before touching a triangle, up to
    /// <paramref name="maxDistance"/> (null: free). The hit's point is the contact, its normal faces the sphere. A sphere that already overlaps a surface at the start is cast with half the
    /// distance to that surface as its radius, so a cast beginning close to a wall may run along it and is not stopped dead (camera/hop-and-walls: the follow cameras' casts).
    /// </summary>
    public RayHit? SweepSphere(Vector3 origin, Vector3 direction, float radius, float maxDistance)
    {
        if (_nodes.Length == 0 || !(maxDistance >= 0f) || !(radius >= 0f) || !Geometry.Finite(origin) || !Geometry.Finite(direction)) return null;
        float near = NearestWithin(origin, radius);
        if (near < radius) radius = near * 0.5f;
        var inv = new Vector3(Inverse(direction.X), Inverse(direction.Y), Inverse(direction.Z));
        var pad = new Vector3(radius);
        float best = maxDistance;
        int bestSlot = -1;
        var bestNormal = Vector3.Zero;
        var bestPoint = Vector3.Zero;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly Node node = ref _nodes[stack[--sp]];
            if (!SlabEntry(node.Min - pad, node.Max + pad, origin, inv, best, out _)) continue;
            if (node.Count > 0)
            {
                for (int slot = node.Start; slot < node.Start + node.Count; slot++)
                    if (SphereTriangle(origin, direction, radius, slot, best, out float t, out var contact)) { best = t; bestSlot = slot; bestPoint = contact; }
                continue;
            }
            stack[sp++] = node.Start;
            stack[sp++] = node.Start + 1;
        }
        if (bestSlot < 0) return null;
        var centre = origin + direction * best;
        var away = centre - bestPoint;
        bestNormal = away.LengthSquared() > 1e-12f ? Vector3.Normalize(away) : -direction;
        return new RayHit(best, bestPoint, bestNormal, _tri[bestSlot]);
    }

    /// <summary>The distance from <paramref name="p"/> to the nearest triangle closer than <paramref name="limit"/>; <paramref name="limit"/> when none is.</summary>
    float NearestWithin(Vector3 p, float limit)
    {
        var pad = new Vector3(limit);
        Vector3 min = p - pad, max = p + pad;
        float best = limit;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly Node node = ref _nodes[stack[--sp]];
            if (node.Min.X > max.X || node.Max.X < min.X || node.Min.Y > max.Y || node.Max.Y < min.Y || node.Min.Z > max.Z || node.Max.Z < min.Z) continue;
            if (node.Count == 0) { stack[sp++] = node.Start + 1; stack[sp++] = node.Start; continue; }
            for (int slot = node.Start; slot < node.Start + node.Count; slot++)
            {
                var a = _v0[slot];
                best = MathF.Min(best, Vector3.Distance(p, Geometry.ClosestOnTriangle(p, a, a + _e1[slot], a + _e2[slot])));
            }
        }
        return best;
    }

    /// <summary>The first touch of a moving sphere and one triangle before <paramref name="limit"/>: the face, then the three edges and corners. See <see cref="SweepSphere"/>.</summary>
    bool SphereTriangle(Vector3 o, Vector3 d, float radius, int slot, float limit, out float t, out Vector3 contact)
    {
        t = limit; contact = default;
        var a = _v0[slot]; var b = a + _e1[slot]; var c = a + _e2[slot];
        var n = _n[slot];
        float r2 = radius * radius;
        bool hit = false;
        float plane = Vector3.Dot(o - a, n);
        if (plane < 0f) { n = -n; plane = -plane; }
        float closing = -Vector3.Dot(d, n);
        if (closing > 1e-6f)
        {
            float at = (plane - radius) / closing;
            if (at >= 0f && at < t)
            {
                var touch = o + d * at - n * radius;
                if (Geometry.InsideTriangle(touch, a, b, c, _n[slot])) { t = at; contact = touch; hit = true; }
            }
        }
        if (hit) return true;   // the face comes first: an edge or corner can touch only later
        void Edge(Vector3 p, Vector3 q, ref float best, ref Vector3 where, ref bool found, float rr)
        {
            var e = q - p;
            float ee = Vector3.Dot(e, e);
            if (ee < 1e-12f) return;
            var m = o - p;
            float de = Vector3.Dot(d, e), me = Vector3.Dot(m, e), dm = Vector3.Dot(d, m);
            float qa = ee - de * de, qb = ee * dm - de * me, qc = ee * (Vector3.Dot(m, m) - rr) - me * me;
            if (qa > 1e-9f && qb < 0f)
            {
                float disc = qb * qb - qa * qc;
                if (disc >= 0f)
                {
                    float at = (-qb - MathF.Sqrt(disc)) / qa;
                    float along = me + at * de;
                    if (at >= 0f && at < best && along >= 0f && along <= ee) { best = at; where = p + e * (along / ee); found = true; }
                }
            }
        }
        void Corner(Vector3 p, ref float best, ref Vector3 where, ref bool found, float rr)
        {
            var m = o - p;
            float dm = Vector3.Dot(d, m), qc = Vector3.Dot(m, m) - rr;
            if (dm >= 0f) return;
            float disc = dm * dm - qc;
            if (disc < 0f) return;
            float at = -dm - MathF.Sqrt(disc);
            if (at >= 0f && at < best) { best = at; where = p; found = true; }
        }
        Edge(a, b, ref t, ref contact, ref hit, r2); Edge(b, c, ref t, ref contact, ref hit, r2); Edge(c, a, ref t, ref contact, ref hit, r2);
        Corner(a, ref t, ref contact, ref hit, r2); Corner(b, ref t, ref contact, ref hit, r2); Corner(c, ref t, ref contact, ref hit, r2);
        return hit;
    }

    /// <summary>Appends, in tree order, the slots of triangles whose bounds overlap the box (used by the character motor).</summary>
    internal void Overlap(Vector3 min, Vector3 max, List<int> slots)
    {
        if (_nodes.Length == 0) return;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            ref readonly Node node = ref _nodes[stack[--sp]];
            if (node.Min.X > max.X || node.Max.X < min.X || node.Min.Y > max.Y || node.Max.Y < min.Y || node.Min.Z > max.Z || node.Max.Z < min.Z) continue;
            if (node.Count > 0)
            {
                for (int slot = node.Start; slot < node.Start + node.Count; slot++) slots.Add(slot);
                continue;
            }
            stack[sp++] = node.Start + 1;
            stack[sp++] = node.Start;
        }
    }

    /// <summary>
    /// Contact between a capsule (segment a–b, radius r) and one triangle. On contact, <paramref name="normal"/> points from the
    /// triangle to the capsule, <paramref name="depth"/> is how far the capsule must move along it to just touch, and
    /// <paramref name="point"/> is the contact point on the triangle. <paramref name="face"/> says the contact is square on
    /// the triangle's face rather than on one of its edges or corners.
    /// </summary>
    internal bool Contact(int slot, Vector3 a, Vector3 b, float r, out Vector3 normal, out float depth, out Vector3 point, out bool face)
    {
        normal = default; depth = 0f; point = default; face = false;
        var v0 = _v0[slot]; var n = _n[slot];
        float d0 = Vector3.Dot(a - v0, n), d1 = Vector3.Dot(b - v0, n);
        if ((d0 >= r && d1 >= r) || (d0 <= -r && d1 <= -r)) return false;
        var v1 = v0 + _e1[slot]; var v2 = v0 + _e2[slot];
        var tmin = Vector3.Min(v0, Vector3.Min(v1, v2)); var tmax = Vector3.Max(v0, Vector3.Max(v1, v2));
        var smin = Vector3.Min(a, b) - new Vector3(r); var smax = Vector3.Max(a, b) + new Vector3(r);
        if (tmin.X > smax.X || tmax.X < smin.X || tmin.Y > smax.Y || tmax.Y < smin.Y || tmin.Z > smax.Z || tmax.Z < smin.Z) return false;

        // The segment pierces the triangle: push out to the side holding more of the segment (floors: upward).
        if (d0 != d1 && ((d0 <= 0f && d1 >= 0f) || (d0 >= 0f && d1 <= 0f)))
        {
            var q = a + (b - a) * (d0 / (d0 - d1));
            if (Geometry.InsideTriangle(q, v0, v1, v2, n))
            {
                // Ground-like faces (normal within 45° of vertical) always resolve upward, putting the feet on the surface:
                // a capsule placed a little under a floor must not be pushed down through it.
                float side = MathF.Abs(n.Z) >= 0.7071f ? (n.Z >= 0f ? 1f : -1f)
                    : MathF.Abs(d0) >= MathF.Abs(d1) ? (d0 >= 0f ? 1f : -1f) : (d1 >= 0f ? 1f : -1f);
                normal = n * side;
                depth = r - MathF.Min(side * d0, side * d1);
                point = q;
                face = true;
                return true;
            }
        }

        float distSq = Geometry.SegmentTriangle(a, b, v0, v1, v2, out var onSegment, out var onTriangle);
        if (distSq >= r * r) return false;
        float dist = MathF.Sqrt(distSq);
        if (dist > 1e-6f) normal = (onSegment - onTriangle) / dist;
        else normal = n * (d0 + d1 >= 0f ? 1f : -1f);
        depth = r - dist;
        point = onTriangle;
        face = MathF.Abs(Vector3.Dot(normal, n)) > 0.9999f;
        return true;
    }

    /// <summary>
    /// Sweeps a sphere (centre <paramref name="c"/>, radius r) straight down by up to <paramref name="maxDrop"/> against the
    /// given slots: the first time of impact against faces, edges and corners. Something it already overlaps from above is
    /// a hit at once (a face's drop is then negative: the lift that puts the sphere back on it). On a hit,
    /// <paramref name="point"/> is the contact point and <paramref name="face"/> says it is on a face's interior.
    /// </summary>
    internal bool SweepDown(Vector3 c, float r, float maxDrop, List<int> slots, out float drop, out Vector3 point, out bool face)
    {
        const float tolerance = 1e-4f;
        drop = float.MaxValue; point = default; face = false;
        float bottom = c.Z - r - maxDrop - tolerance, top = c.Z + r;
        foreach (int slot in slots)
        {
            var v0 = _v0[slot]; var v1 = v0 + _e1[slot]; var v2 = v0 + _e2[slot];
            if (MathF.Max(v0.Z, MathF.Max(v1.Z, v2.Z)) < bottom || MathF.Min(v0.Z, MathF.Min(v1.Z, v2.Z)) > top) continue;
            if (MathF.Min(v0.X, MathF.Min(v1.X, v2.X)) > c.X + r || MathF.Max(v0.X, MathF.Max(v1.X, v2.X)) < c.X - r) continue;
            if (MathF.Min(v0.Y, MathF.Min(v1.Y, v2.Y)) > c.Y + r || MathF.Max(v0.Y, MathF.Max(v1.Y, v2.Y)) < c.Y - r) continue;

            // Face: the plane side facing up, reached when the centre is r above it.
            var n = _n[slot];
            if (n.Z < 0f) n = -n;
            if (n.Z > 1e-6f)
            {
                float above = Vector3.Dot(c - v0, n);
                if (above >= 0f)   // centre on the upper side; overlapping (above < r) gives a negative drop: lift onto it
                {
                    float t = (above - r) / n.Z;
                    var at = new Vector3(c.X, c.Y, c.Z - t) - n * r;
                    if (t <= maxDrop && t < drop && Geometry.InsideTriangle(at, v0, v1, v2, _n[slot])) { drop = t; point = at; face = true; }
                }
            }
            Edge(c, r, v0, v1, maxDrop, ref drop, ref point, ref face);
            Edge(c, r, v1, v2, maxDrop, ref drop, ref point, ref face);
            Edge(c, r, v2, v0, maxDrop, ref drop, ref point, ref face);
            Corner(c, r, v0, maxDrop, ref drop, ref point, ref face);
            Corner(c, r, v1, maxDrop, ref drop, ref point, ref face);
            Corner(c, r, v2, maxDrop, ref drop, ref point, ref face);
        }
        return drop != float.MaxValue;

        static void Edge(Vector3 c, float r, Vector3 p, Vector3 q, float maxDrop, ref float drop, ref Vector3 point, ref bool face)
        {
            var e = q - p;
            float length = e.Length();
            if (length < 1e-6f) return;
            var u = e / length;
            var m = c - p;
            // The ray c - t·Z against the infinite cylinder of radius r around the edge's line.
            var mp = m - Vector3.Dot(m, u) * u;
            var dp = -Vector3.UnitZ + u.Z * u;
            float A = Vector3.Dot(dp, dp), B = Vector3.Dot(mp, dp), C = Vector3.Dot(mp, mp) - r * r;
            if (C < 0f)
            {
                // Already within reach of the edge's line: a contact now if the nearest point of the edge is in reach and
                // not above the centre.
                float along = Math.Clamp(Vector3.Dot(m, u), 0f, length);
                var nearest = p + u * along;
                if (Vector3.DistanceSquared(c, nearest) < r * r && nearest.Z <= c.Z && 0f < drop) { drop = 0f; point = nearest; face = false; }
                return;
            }
            if (A < 1e-9f) return;   // vertical edge
            float disc = B * B - A * C;
            if (disc < 0f) return;
            float t = (-B - MathF.Sqrt(disc)) / A;
            if (t < -1e-4f || t > maxDrop || t >= drop) return;
            float s = Vector3.Dot(m - t * Vector3.UnitZ, u);
            if (s < 0f || s > length) return;
            drop = t; point = p + u * s; face = false;
        }

        static void Corner(Vector3 c, float r, Vector3 v, float maxDrop, ref float drop, ref Vector3 point, ref bool face)
        {
            var m = c - v;
            float b = -m.Z, cc = Vector3.Dot(m, m) - r * r;
            if (cc < 0f)
            {
                if (v.Z <= c.Z && 0f < drop) { drop = 0f; point = v; face = false; }
                return;
            }
            float disc = b * b - cc;
            if (disc < 0f) return;
            float t = -b - MathF.Sqrt(disc);
            if (t < -1e-4f || t > maxDrop || t >= drop) return;
            drop = t; point = v; face = false;
        }
    }

    static ulong Hash(Vector3[] vertices, int[] indices)
    {
        ulong h = Geometry.FnvOffset;
        h = Geometry.Fnv(h, (uint)vertices.Length);
        h = Geometry.Fnv(h, (uint)indices.Length);
        foreach (var v in vertices)
        {
            h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.X));
            h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Y));
            h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Z));
        }
        foreach (int i in indices) h = Geometry.Fnv(h, (uint)i);
        return h;
    }
}

/// <summary>
/// The level: what characters walk on and bump into (<see cref="Character"/>), and what shots stop at (<see cref="Weapons"/>).
/// The game files keep these apart, so they may differ (or be the same mesh). With spawn-room <paramref name="blockers"/>, each
/// side's players also bump into the other side's blockers (<see cref="CharacterFor"/>); shots pass through them. Every blocker
/// reaches <see cref="SpawnBlocker.AboveLevel"/> above the character collision's highest point (<see cref="Blockers"/>; M31,
/// **change from the original**, owner's decision 2026-10-28: no jump-pack flight passes over a spawn room's blockers).
/// With <paramref name="props"/> (M39: the map's movable props, such as Manufactorum's green ammo crates), each prop's triangles
/// are added to both meshes after the level's: every player bumps into them and every shot, cone, projectile and thrown item
/// stops at or bounces off them, except that a hit-scan shot with a <see cref="PierceSpec"/> may pass through them
/// (<see cref="TraceShot"/>; rules in Props.cs and spec/behavior.md M39).
/// </summary>
public sealed partial class CollisionWorld
{
    public CollisionWorld(StaticMesh character, StaticMesh weapons, IReadOnlyList<KillVolume>? killVolumes = null, IReadOnlyList<SpawnBlocker>? blockers = null,
        IReadOnlyList<CollisionProp>? props = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(weapons);
        Props = CollisionProp.Checked(props);
        Character = CollisionProp.AddTo(character, Props, surfaces: false);
        Weapons = CollisionProp.AddTo(weapons, Props, surfaces: true);
        _levelShotTriangles = weapons.SourceTriangles;
        _propStarts = CollisionProp.Starts(_levelShotTriangles, Props);
        _levelWeapons = weapons;
        _propMin = Props.Select(p => p.Vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min)).ToArray();
        _propMax = Props.Select(p => p.Vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max)).ToArray();
        KillVolumes = killVolumes?.ToArray() ?? [];
        Blockers = Raised(character, blockers);
        PlacedBlockers = SpawnBlocker.Checked(blockers).ToArray();
        ContentHash = CollisionProp.Hash(SpawnBlocker.Hash(KillVolume.Hash(Geometry.Fnv(Geometry.Fnv(Geometry.FnvOffset, character.ContentHash), weapons.ContentHash), killVolumes), Blockers), Props);
        _forMarines = SpawnBlocker.MeshFor(Character, Team.SpaceMarines, Blockers);
        _forChaos = SpawnBlocker.MeshFor(Character, Team.Chaos, Blockers);
    }

    /// <summary>What characters walk on and bump into: the level's character mesh, then the <see cref="Props"/>' triangles.</summary>
    public StaticMesh Character { get; }
    /// <summary>What shots stop at: the level's weapon mesh, then the <see cref="Props"/>' triangles (with their surface ids).</summary>
    public StaticMesh Weapons { get; }
    /// <summary>The props (M39), in order; their triangles follow the level's in <see cref="Weapons"/> (<see cref="PropOfShotTriangle"/>).</summary>
    public IReadOnlyList<CollisionProp> Props { get; }
    /// <summary>The level's kill volumes (M18): a living player whose body enters one dies (<see cref="KillsPlayer"/>).</summary>
    public IReadOnlyList<KillVolume> KillVolumes { get; }
    /// <summary>
    /// The sides' spawn-room blockers (finite, positive size): solid boxes for the players of the other side (blockers of
    /// <see cref="Team.None"/> block nobody), each raised to <see cref="SpawnBlocker.AboveLevel"/> above the top of
    /// the level's character mesh when it is lower (<see cref="SpawnBlocker.ReachingAbove"/>; M31). Spawn-room blocker rule: spec/behavior.md.
    /// </summary>
    public IReadOnlyList<SpawnBlocker> Blockers { get; }
    /// <summary>The same blockers as placed in the map (not raised): what the client draws (the raised part is invisible).</summary>
    public IReadOnlyList<SpawnBlocker> PlacedBlockers { get; }
    /// <summary>
    /// FNV-1a 64 over the level's two meshes' content hashes, character first, then each kill volume's floats (none: as before M18),
    /// then each blocker's side and floats as raised (<see cref="Blockers"/>; none: as before), then each prop's
    /// surface and geometry (<see cref="CollisionProp.Hash"/>; none: as before M39).
    /// </summary>
    public ulong ContentHash { get; }

    readonly StaticMesh _forMarines;
    readonly StaticMesh _forChaos;
    // Weapon-mesh triangles below this number are the level's; the props' follow, prop k from _propStarts[k].
    readonly int _levelShotTriangles;
    readonly int[] _propStarts;
    // The level's weapon mesh without the props (M39: the level stops every shot), and each prop's bounds.
    readonly StaticMesh _levelWeapons;
    readonly Vector3[] _propMin, _propMax;

    /// <summary>The blockers checked (<see cref="SpawnBlocker.Checked"/>) and raised to <see cref="SpawnBlocker.AboveLevel"/> above the level's top (M31).</summary>
    static SpawnBlocker[] Raised(StaticMesh character, IReadOnlyList<SpawnBlocker>? blockers) =>
        SpawnBlocker.Checked(blockers).Select(b => b.ReachingAbove(character.Max.Z + SpawnBlocker.AboveLevel)).ToArray();

    /// <summary>
    /// What a player of <paramref name="side"/> walks on and bumps into: <see cref="Character"/> plus the boxes of the other
    /// side's blockers (its own side's pass through; <see cref="Team.None"/>: <see cref="Character"/> alone). The level's
    /// triangles keep their exact geometry; each side's mesh is built once, the same way on server and client.
    /// </summary>
    public StaticMesh CharacterFor(Team side) => side switch
    {
        Team.SpaceMarines => _forMarines,
        Team.Chaos => _forChaos,
        _ => Character,
    };

    /// <summary>
    /// The player moved out of every blocker that stops its side and holds its capsule's axis (<see cref="SpawnBlocker.PushOut"/>,
    /// in list order); unchanged otherwise (always, for players of no side or a level without blockers).
    /// </summary>
    public PlayerState OutOfBlockers(PlayerState s, SimConfig c)
    {
        var feet = s.Position;
        foreach (var b in Blockers)
            if (b.Blocks(s.Team)) feet = b.PushOut(feet, MathF.Max(c.CapsuleRadius, 0.05f), c.CapsuleHeight);
        return feet == s.Position ? s : s with { Position = feet };
    }

    /// <summary>
    /// Whether a player moving from <paramref name="from"/> to <paramref name="to"/> (feet) this tick touched a kill volume:
    /// the feet's path, or the body's vertical span at <paramref name="to"/> (feet up to <paramref name="bodyHeight"/>), meets
    /// one. The swept path keeps a fast fall from passing a thin volume between ticks.
    /// </summary>
    public bool KillsPlayer(Vector3 from, Vector3 to, float bodyHeight)
    {
        foreach (var k in KillVolumes)
            if (k.Crosses(from, to) || k.Crosses(to, to + new Vector3(0f, 0f, MathF.Max(bodyHeight, 0f)))) return true;
        return false;
    }
}

/// <summary>
/// A box that kills a living player who enters it (M18; the map's <c>base/KillRegion</c> objects): centre, half extents
/// (metres) and rotation about Z (radians). Only the box test is data; which part of the player counts is an
/// interpretation (<see cref="CollisionWorld.KillsPlayer"/>, spec/behavior.md).
/// </summary>
public readonly record struct KillVolume(Vector3 Center, Vector3 HalfExtents, float Yaw)
{
    Vector3 Local(Vector3 p)
    {
        var d = p - Center;
        float c = MathF.Cos(Yaw), s = MathF.Sin(Yaw);
        return new Vector3(c * d.X + s * d.Y, -s * d.X + c * d.Y, d.Z);
    }

    /// <summary>Whether the point is inside the box (faces included).</summary>
    public bool Contains(Vector3 p)
    {
        var l = Local(p);
        return MathF.Abs(l.X) <= HalfExtents.X && MathF.Abs(l.Y) <= HalfExtents.Y && MathF.Abs(l.Z) <= HalfExtents.Z;
    }

    /// <summary>Whether the segment from <paramref name="a"/> to <paramref name="b"/> meets the box (slab test; a point segment is <see cref="Contains"/>).</summary>
    public bool Crosses(Vector3 a, Vector3 b)
    {
        if (!Geometry.Finite(a) || !Geometry.Finite(b)) return false;
        Vector3 la = Local(a), d = Local(b) - la;
        float t0 = 0f, t1 = 1f;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? la.X : axis == 1 ? la.Y : la.Z, v = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            float h = axis == 0 ? HalfExtents.X : axis == 1 ? HalfExtents.Y : HalfExtents.Z;
            if (MathF.Abs(v) < 1e-12f) { if (o < -h || o > h) return false; continue; }
            float u = (-h - o) / v, w = (h - o) / v;
            if (u > w) (u, w) = (w, u);
            t0 = MathF.Max(t0, u); t1 = MathF.Min(t1, w);
            if (t0 > t1) return false;
        }
        return true;
    }

    internal static ulong Hash(ulong h, IReadOnlyList<KillVolume>? volumes)
    {
        if (volumes is null) return h;
        foreach (var k in volumes)
            foreach (float f in new[] { k.Center.X, k.Center.Y, k.Center.Z, k.HalfExtents.X, k.HalfExtents.Y, k.HalfExtents.Z, k.Yaw })
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(f));
        return h;
    }
}

/// <summary>
/// The character controller used by <see cref="Movement.Step"/> when a level is loaded: an upright capsule (feet at the
/// player position) against the character mesh. Horizontal moves go in sub-steps no longer than half the radius, pushing
/// out of every triangle after each (so nothing thin is crossed) and classifying contacts by their normal: walkable ground
/// (faces up to <see cref="SimConfig.MaxWalkableSlope"/> at any height; edges only within a step of the ground stood on)
/// pushes straight up, walls push horizontally (sliding along them), ceilings push along the normal. A sub-step whose
/// push-outs leave more overlap than before (a gap narrower than the capsule) or carry the axis through a triangle is
/// refused. Downward moves (ground snap, step-down, falling) sweep the bottom sphere, so they stop exactly on what is below
/// at any speed. Grounded players try a step up (<see cref="SimConfig.StepHeight"/>) when blocked and follow the ground down
/// by a step plus what the steepest walkable slope drops over the distance walked. Falls are capped at
/// <see cref="SimConfig.MaxFallSpeed"/>; a capsule wedged where it can neither stand nor slide stands there. Single-threaded,
/// fixed iteration order, no time or hash-order
/// dependence: client prediction reproduces it exactly. A **provisional approximation**: the original uses a Havok
/// character controller whose rules are not decoded.
/// </summary>
internal static class CharacterMotor
{
    const float Skin = 1e-4f;          // left between the capsule and what it touches
    const int MaxSubsteps = 64;
    const int Passes = 4;               // push-out sweeps over the nearby triangles per sub-step
    const float Tolerance = 0.01f;      // penetration left after push-outs that still counts as clear

    [Flags]
    internal enum Touch { None = 0, Ground = 1, Wall = 2, Ceiling = 4 }

    readonly struct Shape(SimConfig c)
    {
        public readonly float R = MathF.Max(c.CapsuleRadius, 0.05f);
        /// <summary>Height of the upper sphere's centre above the feet (equal to R when the capsule is a sphere).</summary>
        public readonly float Top = MathF.Max(c.CapsuleHeight - MathF.Max(c.CapsuleRadius, 0.05f), MathF.Max(c.CapsuleRadius, 0.05f));
        public readonly float CosMax = MathF.Cos(Math.Clamp(c.MaxWalkableSlope, 0f, 89.9f) * (MathF.PI / 180f));
        /// <summary>Tangent of the walkable limit (capped at 85°), for how far down a slope a grounded player follows per tick.</summary>
        public readonly float TanMax = MathF.Tan(Math.Clamp(c.MaxWalkableSlope, 0f, 85f) * (MathF.PI / 180f));
        public readonly float StepHeight = MathF.Max(c.StepHeight, 0f);
        public readonly float MaxFallSpeed = c.MaxFallSpeed > 0f ? c.MaxFallSpeed : float.PositiveInfinity;
    }

    /// <summary>
    /// Per-move state: the triangles gathered near the path, the box they were gathered for, and the height of the ground
    /// the capsule last stood on (edges more than a step above it are walls; walkable faces are ground at any height).
    /// </summary>
    sealed class Scratch
    {
        public readonly List<int> Near = new(64);
        public Vector3 GatherMin, GatherMax;
    }

    [ThreadStatic] static Scratch? t_scratch;

    /// <param name="gravity">Gravity while airborne (null: <see cref="SimConfig.Gravity"/>; a jump pack's phases pass their own).</param>
    internal static PlayerState Step(PlayerState s, float yaw, Vector2 horizontal, float vz, bool grounded, SimConfig c, StaticMesh mesh, float? gravity = null)
    {
        float dt = c.TickSeconds;
        float g = gravity ?? c.Gravity;
        var shape = new Shape(c);
        var scratch = t_scratch ??= new Scratch();
        var pos = s.Position;

        // Never start embedded: push out, as long as that leaves less overlap than there was (in a gap narrower than
        // the capsule the push-outs cannot clear, stay put rather than be shoved about).
        Gather(mesh, shape, pos, pos, scratch);
        float overlap = Penetration(mesh, shape, pos, scratch);
        if (overlap > 0f)
        {
            var cleared = pos;
            float noLimit = float.PositiveInfinity;
            Resolve(mesh, shape, ref cleared, ref noLimit, scratch, guard: false);
            if (Penetration(mesh, shape, cleared, scratch) < overlap) pos = cleared;
        }
        var start = pos;
        Touch touched;

        if (grounded)
        {
            vz = 0f;
            var walk = new Vector3(horizontal.X * dt, horizontal.Y * dt, 0f);
            var a = start;
            float groundA = start.Z;
            bool stepped = false;
            touched = Move(mesh, shape, ref a, walk, ref groundA, scratch);
            if ((touched & Touch.Wall) != 0 && shape.StepHeight > 0f && walk.LengthSquared() > 0f)
            {
                // Blocked: try again from one step higher, then come back down onto ground no more than a step up.
                var b = start;
                float groundB = start.Z;
                Move(mesh, shape, ref b, new Vector3(0, 0, shape.StepHeight), ref groundB, scratch);
                float lifted = b.Z - start.Z;
                if (lifted > 1e-3f)
                {
                    var across = Move(mesh, shape, ref b, walk, ref groundB, scratch);
                    // Coming down, a step's edge under the capsule's round bottom holds it up, so a slow approach still
                    // gets onto the step (the next tick's walk rolls it the rest of the way up).
                    var dir = new Vector2(walk.X, walk.Y);
                    float progressA = Vector2.Dot(new Vector2(a.X - start.X, a.Y - start.Y), dir);
                    float progressB = Vector2.Dot(new Vector2(b.X - start.X, b.Y - start.Y), dir);
                    if (progressB > progressA + 1e-4f * dir.Length() &&
                        Lower(mesh, shape, ref b, lifted, start.Z + shape.StepHeight, edgeSupport: true, slopeRun: -1f, scratch) == Landing.Ground)
                    {
                        a = b;
                        touched = across;
                        stepped = true;
                    }
                }
            }
            pos = a;
            // Stay on the ground going down stairs and slopes: as far as a step, plus what the steepest walkable slope
            // drops over the distance walked this tick. Walking off a ledge starts a fall.
            float run = new Vector2(pos.X - start.X, pos.Y - start.Y).Length();
            var landing = Lower(mesh, shape, ref pos, shape.StepHeight + run * shape.TanMax, float.PositiveInfinity, edgeSupport: stepped, slopeRun: run, scratch);
            if (landing == Landing.Edge)
            {
                // Over a convex edge too steep to stand on: let the push-out roll the capsule off it, onto ground within a
                // step below (stairs going down) or into a fall (a ledge).
                var snap = pos;
                float groundS = pos.Z;
                if ((Move(mesh, shape, ref snap, new Vector3(0, 0, -shape.StepHeight), ref groundS, scratch) & Touch.Ground) != 0) pos = snap;
                else if (snap.Z < pos.Z - 1e-3f) grounded = false;
                // else: wedged on steep edges it cannot slide off (a crevice narrower than the capsule): it stands there.
            }
            else if (landing != Landing.Ground) grounded = false;
        }
        else
        {
            vz = MathF.Max(vz - g * dt, -shape.MaxFallSpeed);
            float dz = vz * dt;
            float air = float.PositiveInfinity;
            // Horizontal part first, then the vertical part.
            touched = Move(mesh, shape, ref pos, new Vector3(horizontal.X * dt, horizontal.Y * dt, 0f), ref air, scratch);
            var landing = dz < 0f ? Lower(mesh, shape, ref pos, -dz, float.PositiveInfinity, edgeSupport: false, slopeRun: -1f, scratch) : Landing.None;
            if (dz < 0f && landing == Landing.Ground) touched |= Touch.Ground;          // landed (the sweep cannot tunnel)
            else if (dz < 0f && landing == Landing.None) pos.Z += dz;                   // nothing below within this tick's fall
            else if (dz != 0f)
            {
                // Rising, or falling onto something too steep to stand on: move with the push-out (sliding down steep
                // faces and off edges), clamping the vertical part at the first surface over or under the capsule's end
                // sphere, just into it so the push-out registers the contact, so nothing thin is crossed.
                if (dz < 0f && mesh.Raycast(pos + new Vector3(0, 0, shape.R), -Vector3.UnitZ, -dz + shape.R) is { } floor)
                    dz = -MathF.Min(-dz, MathF.Max(0f, floor.Distance - shape.R) + 2 * Skin);
                else if (dz > 0f && mesh.Raycast(pos + new Vector3(0, 0, shape.Top), Vector3.UnitZ, dz + shape.R) is { } roof)
                    dz = MathF.Min(dz, MathF.Max(0f, roof.Distance - shape.R) + 2 * Skin);
                float before = pos.Z;
                touched |= Move(mesh, shape, ref pos, new Vector3(0, 0, dz), ref air, scratch);
                // Falling but held up (wedged between steep surfaces it cannot slide down): it stands there.
                if (dz < 0f && pos.Z - before > 0.5f * dz) touched |= Touch.Ground;
            }
            if ((touched & Touch.Ground) != 0 && vz <= 0f) { grounded = true; vz = 0f; }
            if ((touched & Touch.Ceiling) != 0 && vz > 0f) vz = 0f;
        }

        if ((touched & Touch.Wall) != 0 && dt > 0f)
        {
            // Walls take away the velocity going into them; what remains is the slide actually made (never faster than before).
            var moved = new Vector2(pos.X - start.X, pos.Y - start.Y) / dt;
            float limit = horizontal.Length();
            if (moved.Length() > limit) moved = limit > 0f ? Vector2.Normalize(moved) * limit : Vector2.Zero;
            horizontal = moved;
        }
        return s with { Position = pos, Velocity = new Vector3(horizontal.X, horizontal.Y, vz), Yaw = yaw, Grounded = grounded };
    }

    enum Landing { None, Ground, Steep, Edge }

    /// <summary>
    /// Lowers the capsule by up to <paramref name="distance"/> onto what is under it (a swept sphere, so it never sinks in).
    /// Ground: it now stands there. Otherwise it is not moved, and the result says whether nothing was in reach, a steep
    /// face, or an edge or corner too steep to stand on. With <paramref name="slopeRun"/> ≥ 0 (the ground snap), ground found
    /// deeper than a step counts only if its own slope over that horizontal run explains the drop (following a slope down,
    /// not dropping off a ledge onto a floor).
    /// </summary>
    static Landing Lower(StaticMesh mesh, in Shape shape, ref Vector3 pos, float distance, float maxGroundZ, bool edgeSupport, float slopeRun, Scratch scratch)
    {
        Gather(mesh, shape, pos, pos - new Vector3(0, 0, distance), scratch);
        var centre = pos + new Vector3(0, 0, shape.R);
        if (!mesh.SweepDown(centre, shape.R, distance, scratch.Near, out float drop, out var point, out bool face)) return Landing.None;
        var n = centre - new Vector3(0, 0, drop) - point;
        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
        bool ground = point.Z <= maxGroundZ && (n.Z >= shape.CosMax || (edgeSupport && !face && n.Z >= 0.2f));
        if (!ground) return face ? Landing.Steep : Landing.Edge;
        if (slopeRun >= 0f && drop > shape.StepHeight + 1e-3f)
        {
            float tan = MathF.Sqrt(MathF.Max(0f, 1f - n.Z * n.Z)) / n.Z;
            if (drop > shape.StepHeight + slopeRun * tan + 1e-3f) return Landing.None;
        }
        pos.Z -= drop - Skin;
        return Landing.Ground;
    }

    /// <summary>
    /// Moves by <paramref name="displacement"/> in sub-steps of at most half the radius, pushing out after each; returns
    /// what was touched. <paramref name="groundZ"/> is the height of the ground last stood on (edges more than a step above
    /// it are walls), updated as the capsule climbs walkable ground; +∞ in the air.
    /// </summary>
    static Touch Move(StaticMesh mesh, in Shape shape, ref Vector3 pos, Vector3 displacement, ref float groundZ, Scratch scratch)
    {
        float length = displacement.Length();
        int steps = Math.Clamp((int)MathF.Ceiling(length / (0.5f * shape.R)), 1, MaxSubsteps);
        Gather(mesh, shape, pos, pos + displacement, scratch);
        var step = displacement / steps;
        var touched = Touch.None;
        float residual = -1f;   // penetration left at pos; computed when first needed
        for (int i = 0; i < steps; i++)
        {
            var from = pos;
            var next = from + step;
            float ground = groundZ;
            var t = Resolve(mesh, shape, ref next, ref ground, scratch, guard: true, from: from);
            touched |= t;
            if (t == Touch.None) { pos = next; residual = 0f; continue; }
            // Never move into a place the push-outs cannot clear (a gap narrower than the capsule): accept the resolved
            // position only if it leaves no more penetration than before. A capsule already squeezed somewhere (placed
            // there, or by older geometry) may still move as long as that does not make it worse, so it can get out.
            if (residual < 0f) residual = Penetration(mesh, shape, from, scratch);
            float after = Penetration(mesh, shape, next, scratch);
            if (after <= MathF.Max(Tolerance, residual)) { pos = next; groundZ = ground; residual = after; continue; }
            var raw = from + step;
            float rawAfter = Penetration(mesh, shape, raw, scratch);
            if (rawAfter <= residual + 1e-3f) { pos = raw; residual = rawAfter; }
            touched |= Touch.Wall;
        }
        return touched;
    }

    /// <summary>Deepest overlap of the capsule at <paramref name="pos"/> with the gathered triangles (0 when clear).</summary>
    static float Penetration(StaticMesh mesh, in Shape shape, Vector3 pos, Scratch scratch)
    {
        float deepest = 0f;
        var a = pos + new Vector3(0, 0, shape.R);
        var b = pos + new Vector3(0, 0, shape.Top);
        foreach (int slot in scratch.Near)
            if (mesh.Contact(slot, a, b, shape.R, out _, out float depth, out _, out _) && depth > deepest) deepest = depth;
        return deepest;
    }

    /// <summary>Collects the triangles near the capsule's path, with a margin for push-outs.</summary>
    static void Gather(StaticMesh mesh, in Shape shape, Vector3 from, Vector3 to, Scratch scratch)
    {
        scratch.Near.Clear();
        float m = shape.R + shape.R;
        scratch.GatherMin = Vector3.Min(from, to) - new Vector3(m, m, m);
        scratch.GatherMax = Vector3.Max(from, to) + new Vector3(m, m, shape.Top + m);
        mesh.Overlap(scratch.GatherMin, scratch.GatherMax, scratch.Near);
    }

    /// <summary>
    /// Pushes the capsule out of the nearby triangles, one at a time in a fixed order (face contacts first, so the seams
    /// between coplanar triangles do not shove it sideways), repeated until nothing touches or the passes run out.
    /// Walkable faces push straight up at any height; walkable edges and corners do only when no more than a step above
    /// <paramref name="groundZ"/>. If the pushes carry the capsule out of the gathered region, the triangles are gathered
    /// again. With <paramref name="guard"/>, a sub-step whose result would carry the capsule's axis through a triangle
    /// (an acute wedge squeezing it through a thin wall) is undone and counts as blocked.
    /// </summary>
    static Touch Resolve(StaticMesh mesh, in Shape shape, ref Vector3 pos, ref float groundZ, Scratch scratch, bool guard, Vector3 from = default)
    {
        var touched = Touch.None;
        var near = scratch.Near;
        bool climbed = false;
        for (int pass = 0; pass < Passes; pass++)
        {
            bool pushed = false;
            for (int sweep = 0; sweep < 2; sweep++)
            {
                for (int i = 0; i < near.Count; i++)
                {
                    var a = pos + new Vector3(0, 0, shape.R);
                    var b = pos + new Vector3(0, 0, shape.Top);
                    if (!mesh.Contact(near[i], a, b, shape.R, out var n, out float depth, out var point, out bool face)) continue;
                    if (sweep == 0 && !face) continue;
                    float amount = depth + Skin;
                    Vector3 push;
                    if (n.Z >= shape.CosMax && (face || point.Z <= groundZ + shape.StepHeight))
                    {
                        push = new Vector3(0, 0, amount / n.Z);
                        touched |= Touch.Ground;
                        climbed = true;
                    }
                    else if (n.Z <= -shape.CosMax)
                    {
                        push = n * amount;
                        touched |= Touch.Ceiling;
                    }
                    else
                    {
                        var flat = new Vector2(n.X, n.Y);
                        float l = flat.Length();
                        push = l > 1e-3f ? new Vector3(flat / l * (amount / MathF.Max(l, 0.5f)), 0f) : n * amount;
                        touched |= Touch.Wall;
                    }
                    pos += push;
                    pushed = true;
                }
            }
            if (!pushed) break;
            if (!Inside(pos, shape, scratch))
            {
                // Pushed beyond the triangles gathered: gather around where it is now (keeping the old ones, so earlier
                // contacts are still seen) and resolve again.
                var keep = near.ToArray();
                Gather(mesh, shape, pos, pos, scratch);
                foreach (int slot in keep) if (!near.Contains(slot)) near.Add(slot);
            }
        }
        if (guard && (touched != Touch.None) && Crosses(mesh, shape, from, pos))
        {
            pos = from;
            return touched | Touch.Wall;
        }
        if (climbed && float.IsFinite(groundZ)) groundZ = pos.Z;
        return touched;
    }

    /// <summary>Whether everything the capsule at <paramref name="pos"/> can touch lies within the gathered box.</summary>
    static bool Inside(Vector3 pos, in Shape shape, Scratch scratch)
    {
        float r = shape.R;
        return pos.X - r >= scratch.GatherMin.X && pos.X + r <= scratch.GatherMax.X &&
               pos.Y - r >= scratch.GatherMin.Y && pos.Y + r <= scratch.GatherMax.Y &&
               pos.Z >= scratch.GatherMin.Z && pos.Z + shape.Top + r <= scratch.GatherMax.Z;
    }

    /// <summary>
    /// Whether moving from <paramref name="from"/> to <paramref name="to"/> carries the capsule's axis (sampled at the two
    /// sphere centres and midway) through a triangle. A resolved capsule's axis stays a radius clear of every surface, so a
    /// crossing only happens when push-outs squeeze it through.
    /// </summary>
    static bool Crosses(StaticMesh mesh, in Shape shape, Vector3 from, Vector3 to)
    {
        var delta = to - from;
        float length = delta.Length();
        if (length < 1e-6f) return false;
        var dir = delta / length;
        float mid = 0.5f * (shape.R + shape.Top);
        return mesh.Raycast(from + new Vector3(0, 0, shape.R), dir, length) is not null ||
               mesh.Raycast(from + new Vector3(0, 0, mid), dir, length) is not null ||
               mesh.Raycast(from + new Vector3(0, 0, shape.Top), dir, length) is not null;
    }
}

static class Geometry
{
    internal const ulong FnvOffset = 14695981039346656037UL;
    const ulong FnvPrime = 1099511628211UL;

    /// <summary>FNV-1a over the four little-endian bytes of <paramref name="value"/>.</summary>
    internal static ulong Fnv(ulong h, uint value)
    {
        for (int i = 0; i < 4; i++) { h ^= (value >> (8 * i)) & 0xFF; h *= FnvPrime; }
        return h;
    }

    internal static ulong Fnv(ulong h, ulong value) => Fnv(Fnv(h, (uint)value), (uint)(value >> 32));

    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    internal static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    internal static bool InsideTriangle(Vector3 q, Vector3 a, Vector3 b, Vector3 c, Vector3 n) =>
        Vector3.Dot(Vector3.Cross(b - a, q - a), n) >= 0f &&
        Vector3.Dot(Vector3.Cross(c - b, q - b), n) >= 0f &&
        Vector3.Dot(Vector3.Cross(a - c, q - c), n) >= 0f;

    /// <summary>
    /// Squared distance between segment p–q and triangle a–b–c for a segment that does not pierce the triangle: the
    /// minimum is at an end point against the triangle or at the segment against an edge.
    /// </summary>
    internal static float SegmentTriangle(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c, out Vector3 onSegment, out Vector3 onTriangle)
    {
        onSegment = p; onTriangle = ClosestOnTriangle(p, a, b, c);
        float best = Vector3.DistanceSquared(onSegment, onTriangle);
        var t = ClosestOnTriangle(q, a, b, c);
        float d = Vector3.DistanceSquared(q, t);
        if (d < best) { best = d; onSegment = q; onTriangle = t; }
        Edge(p, q, a, b, ref best, ref onSegment, ref onTriangle);
        Edge(p, q, b, c, ref best, ref onSegment, ref onTriangle);
        Edge(p, q, c, a, ref best, ref onSegment, ref onTriangle);
        return best;
    }

    static void Edge(Vector3 p, Vector3 q, Vector3 a, Vector3 b, ref float best, ref Vector3 onSegment, ref Vector3 onTriangle)
    {
        float d = SegmentSegment(p, q, a, b, out var c1, out var c2);
        if (d < best) { best = d; onSegment = c1; onTriangle = c2; }
    }

    /// <summary>Closest point on a triangle to a point (Ericson, Real-Time Collision Detection, 5.1.5).</summary>
    internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    /// <summary>Closest points of two segments, squared distance (Ericson 5.1.9).</summary>
    internal static float SegmentSegment(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        const float eps = 1e-12f;
        if (a <= eps && e <= eps) { c1 = p1; c2 = p2; return Vector3.DistanceSquared(c1, c2); }
        if (a <= eps) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= eps) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > eps ? Math.Clamp((b * f - c * e) / denom, 0f, 1f) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
                else if (t > 1f) { t = 1f; s = Math.Clamp((b - c) / a, 0f, 1f); }
            }
        }
        c1 = p1 + d1 * s; c2 = p2 + d2 * t;
        return Vector3.DistanceSquared(c1, c2);
    }
}
