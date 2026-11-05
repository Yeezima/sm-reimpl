using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// The primitives of Ashfall Citadel's generator (docs/musou-ashfall-graybox.md; every size is ours): axis boxes, oriented boxes (yaw in 15 degree steps from a table, so no
/// trigonometry runs and every machine builds the same bits), box-step stairs, floor discs and the crater heightfield. Every solid winds outward as <see cref="TfwMap.Triangles"/>.
/// </summary>
internal sealed class AshfallBuilder
{
    public readonly List<AshfallPart> Parts = [];

    /// <summary>The family tag every part gets while <see cref="Tag"/> is set (parallel to <see cref="Parts"/>): the v5 families the kit dressing replaces (docs/musou-ashfall-graybox.md "Draft 2 (freestanding kit)") are tagged and removed after the layout is built.</summary>
    public readonly List<string?> Tags = [];

    /// <summary>The tag given to the parts added from now on (null: part of the graybox that stays).</summary>
    public string? Tag;

    /// <summary>The 2D footprints (min x, min y, max x, max y) of the structures rubble keeps 1.2 m away from.</summary>
    public readonly List<Vector4> Footprints = [];

    /// <summary>Places rubble keeps clear (spawns, centres): x, y, radius.</summary>
    public readonly List<Vector3> Reserved = [];

    readonly List<Vector3> _rubble = [];   // x, y, circumscribed radius of every rubble piece placed

    /// <summary>Riser and tread of a box-step stair (kit standard).</summary>
    public const float Riser = 0.25f, Tread = 0.5f;

    /// <summary>
    /// A stair's boxes stop this far inside its nominal width on each side (ours). The enemies' graph samples a 0.5 m grid and, on a box's far edge exactly, finds the ground under it instead
    /// of the step, which cuts the stair off from the ground beside it (found by the graph check of G1); a quarter metre in puts the side edges between two grid lines. The same holds along the run: the treads start a quarter metre early, so their edges fall between grid lines too (the last step still ends at the nominal end).
    /// </summary>
    public const float SideInset = 0.25f;

    // ---------------------------------------------------------------- angles (15 degree steps, table)

    static readonly float[] Quarter = [0f, 0.25881904510252074f, 0.5f, 0.7071067811865476f, 0.8660254037844386f, 0.9659258262890683f, 1f];

    /// <summary>Sine and cosine of <paramref name="step"/> x 15 degrees (counter-clockwise from east).</summary>
    public static (float Sin, float Cos) Trig(int step)
    {
        step = ((step % 24) + 24) % 24;
        int q = step / 6, r = step % 6;
        float s = Quarter[r], c = Quarter[6 - r];
        return q switch { 0 => (s, c), 1 => (c, -s), 2 => (-s, -c), _ => (-c, s) };
    }

    // ---------------------------------------------------------------- solids

    void Add(AshfallKind kind, Vector3[] corners, bool footprint, Vector4 area)
    {
        Parts.Add(new AshfallPart(kind, corners));
        Tags.Add(Tag);
        if (footprint) Footprints.Add(area);
    }

    static bool Structural(AshfallKind k) => k is AshfallKind.Wall or AshfallKind.Cover or AshfallKind.Floor or AshfallKind.Stair or AshfallKind.Perch or AshfallKind.Stronghold;

    /// <summary>An axis-aligned box.</summary>
    public void Box(AshfallKind kind, float x0, float y0, float z0, float x1, float y1, float z1) =>
        Add(kind, TfwMap.Triangles(new TfwSolid(new Vector3(x0, y0, z0), new Vector3(x1, y1, z1))), Structural(kind), new Vector4(x0, y0, x1, y1));

    /// <summary>A box turned about Z by <paramref name="yawStep"/> x 15 degrees: centre (<paramref name="cx"/>, <paramref name="cy"/>), size <paramref name="sx"/> along the turned x axis, <paramref name="sy"/> along its y.</summary>
    public void Oriented(AshfallKind kind, float cx, float cy, float z0, float z1, float sx, float sy, int yawStep)
    {
        var (s, c) = Trig(yawStep);
        var ax = new Vector3(c, s, 0f);
        var ay = new Vector3(-s, c, 0f);
        float hx = sx / 2f, hy = sy / 2f;
        var centre = new Vector3(cx, cy, (z0 + z1) / 2f);
        var corners = Frame(centre, ax, ay, Vector3.UnitZ, hx, hy, (z1 - z0) / 2f);
        float ex = MathF.Abs(c) * hx + MathF.Abs(s) * hy, ey = MathF.Abs(s) * hx + MathF.Abs(c) * hy;
        Add(kind, corners, Structural(kind), new Vector4(cx - ex, cy - ey, cx + ex, cy + ey));
    }

    /// <summary>
    /// A box pitched up along its length: its top surface starts at <paramref name="low"/> and rises along the yaw <paramref name="yawStep"/> (x 15 degrees) at the angle whose sine and cosine are given
    /// (a literal pair: no trigonometry runs). <paramref name="length"/> runs along the slope, <paramref name="width"/> across it, <paramref name="thick"/> below the top surface.
    /// </summary>
    public void Pitched(AshfallKind kind, Vector3 low, int yawStep, float sinPitch, float cosPitch, float length, float width, float thick)
    {
        var (s, c) = Trig(yawStep);
        var f = new Vector3(c, s, 0f);
        var right = new Vector3(s, -c, 0f);
        var fwd = f * cosPitch + Vector3.UnitZ * sinPitch;
        var up = Vector3.UnitZ * cosPitch - f * sinPitch;
        var centre = low + fwd * (length / 2f) - up * (thick / 2f);
        var corners = Frame(centre, right, fwd, up, width / 2f, length / 2f, thick / 2f);
        float minX = corners.Min(p => p.X), maxX = corners.Max(p => p.X), minY = corners.Min(p => p.Y), maxY = corners.Max(p => p.Y);
        Add(kind, corners, Structural(kind), new Vector4(minX, minY, maxX, maxY));
    }

    /// <summary>
    /// A convex solid from its vertices and faces (each face a cycle of vertex indices around a planar polygon): fan-triangulated, every triangle wound away from the solid's centre
    /// (deterministic, no trigonometry). Used by the pointed roofs of pass 2b.
    /// </summary>
    public void Convex(AshfallKind kind, Vector3[] v, int[][] faces)
    {
        var centre = Vector3.Zero;
        foreach (var p in v) centre += p;
        centre /= v.Length;
        var t = new List<Vector3>();
        foreach (var f in faces)
        {
            var fc = Vector3.Zero;
            foreach (int i in f) fc += v[i];
            fc /= f.Length;
            for (int k = 1; k + 1 < f.Length; k++) Wind(v[f[0]], v[f[k]], v[f[k + 1]], fc - centre, t);
        }
        float minX = v.Min(p => p.X), minY = v.Min(p => p.Y), maxX = v.Max(p => p.X), maxY = v.Max(p => p.Y);
        Add(kind, t.ToArray(), Structural(kind), new Vector4(minX, minY, maxX, maxY));
    }

    /// <summary>A gable roof: a triangular prism on the rectangle (x0, y0)..(x1, y1) at <paramref name="z"/>, its ridge <paramref name="height"/> above, running along x (<paramref name="alongX"/>) or y.</summary>
    public void Gable(AshfallKind kind, float x0, float y0, float x1, float y1, float z, float height, bool alongX)
    {
        Vector3 a = new(x0, y0, z), b = new(x1, y0, z), c = new(x1, y1, z), d = new(x0, y1, z);
        Vector3 r1, r2;
        if (alongX) { float ym = (y0 + y1) / 2f; r1 = new(x0, ym, z + height); r2 = new(x1, ym, z + height); }
        else { float xm = (x0 + x1) / 2f; r1 = new(xm, y0, z + height); r2 = new(xm, y1, z + height); }
        // vertices: 0 a, 1 b, 2 c, 3 d, 4 r1, 5 r2
        Convex(kind, [a, b, c, d, r1, r2], alongX
            ? [[3, 2, 1, 0], [0, 1, 5, 4], [2, 3, 4, 5], [0, 4, 3], [1, 2, 5]]
            : [[3, 2, 1, 0], [0, 3, 5, 4], [1, 2, 5, 4], [0, 1, 4], [3, 2, 5]]);
    }

    /// <summary>A square (or rectangular) pyramid: base (x0, y0)..(x1, y1) at <paramref name="z"/>, the apex <paramref name="height"/> above its middle.</summary>
    public void Pyramid(AshfallKind kind, float x0, float y0, float x1, float y1, float z, float height) =>
        Convex(kind, [new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z), new((x0 + x1) / 2f, (y0 + y1) / 2f, z + height)],
            [[3, 2, 1, 0], [0, 1, 4], [1, 2, 4], [2, 3, 4], [3, 0, 4]]);

    /// <summary>The 36 corners of a box on right-handed axes (<paramref name="ax"/> x <paramref name="ay"/> = <paramref name="az"/>), outward winding as <see cref="TfwMap.Triangles"/>.</summary>
    static Vector3[] Frame(Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, float hx, float hy, float hz)
    {
        Vector3 P(float x, float y, float z) => c + ax * (x * hx) + ay * (y * hy) + az * (z * hz);
        Vector3[] p = [P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1)];
        int[][] faces = [[3, 2, 1, 0], [4, 5, 6, 7], [0, 1, 5, 4], [2, 3, 7, 6], [3, 0, 4, 7], [1, 2, 6, 5]];
        var corners = new Vector3[36];
        for (int f = 0; f < 6; f++)
        {
            int[] q = faces[f];
            ReadOnlySpan<int> order = [q[0], q[1], q[2], q[0], q[2], q[3]];
            for (int k = 0; k < 6; k++) corners[f * 6 + k] = p[order[k]];
        }
        return corners;
    }

    /// <summary>
    /// A staircase of solid box steps (0.25 m risers, 0.5 m treads) rising from <paramref name="z0"/> by <paramref name="rise"/>: the first step's near edge is the line through
    /// (<paramref name="sx"/>, <paramref name="sy"/>) across the direction (<paramref name="dx"/>, <paramref name="dy"/>), <paramref name="width"/> wide (less <see cref="SideInset"/> a side) centred on that point.
    /// Each step is solid down to <paramref name="z0"/>; <paramref name="skip"/> steps' height is already climbed (the second flight of a stair starts that high).
    /// </summary>
    public void Stair(float sx, float sy, int dx, int dy, float width, float z0, float rise, int skip = 0, AshfallKind kind = AshfallKind.Stair)
    {
        int steps = (int)MathF.Round(rise / Riser);
        for (int i = 0; i < steps; i++)
        {
            float a = Tread * i - SideInset, b = Tread * (i + 1) - SideInset + (i == steps - 1 ? SideInset : 0f), top = z0 + Riser * (skip + i + 1), h = width / 2f - SideInset;
            float xa = sx + dx * a, xb = sx + dx * b, ya = sy + dy * a, yb = sy + dy * b;
            if (dx != 0) Box(kind, MathF.Min(xa, xb), sy - h, z0, MathF.Max(xa, xb), sy + h, top);
            else Box(kind, sx - h, MathF.Min(ya, yb), z0, sx + h, MathF.Max(ya, yb), top);
        }
    }

    /// <summary>
    /// A wall along x (<paramref name="alongX"/>) or y from <paramref name="lo"/> to <paramref name="hi"/>, <paramref name="t0"/>..<paramref name="t1"/> thick across, with openings
    /// (<c>A</c>..<c>B</c> along the wall, <c>Za</c>..<c>Zb</c> high; an opening from the foot has no piece below).
    /// </summary>
    public void HoleWall(AshfallKind kind, bool alongX, float lo, float hi, float t0, float t1, float z0, float z1, params (float A, float B, float Za, float Zb)[] holes)
    {
        void Piece(float a, float b, float za, float zb)
        {
            if (b - a <= 0f || zb - za <= 0f) return;
            if (alongX) Box(kind, a, t0, za, b, t1, zb); else Box(kind, t0, a, za, t1, b, zb);
        }
        float at = lo;
        foreach (var h in holes.OrderBy(h => h.A))
        {
            Piece(at, h.A, z0, z1);
            Piece(h.A, h.B, z0, h.Za);
            Piece(h.A, h.B, h.Zb, z1);
            at = h.B;
        }
        Piece(at, hi, z0, z1);
    }

    // ---------------------------------------------------------------- flat things

    /// <summary>A floor marker disc (24 sides, not solid): <paramref name="radius"/> around (<paramref name="cx"/>, <paramref name="cy"/>) at <paramref name="z"/>, facing up.</summary>
    public void Disc(float cx, float cy, float z, float radius)
    {
        const int sides = 24;
        var corners = new Vector3[sides * 3];
        for (int k = 0; k < sides; k++)
        {
            var (s0, c0) = Trig(k);
            var (s1, c1) = Trig(k + 1);
            corners[k * 3] = new Vector3(cx, cy, z);
            corners[k * 3 + 1] = new Vector3(cx + c0 * radius, cy + s0 * radius, z);
            corners[k * 3 + 2] = new Vector3(cx + c1 * radius, cy + s1 * radius, z);
        }
        Parts.Add(new AshfallPart(AshfallKind.Objective, corners));
        Tags.Add(null);
    }

    /// <summary>
    /// The heightfield over the rectangle (<paramref name="x0"/>, <paramref name="y0"/>)..(<paramref name="x1"/>, <paramref name="y1"/>) in <paramref name="cell"/> m cells (the surface faces up) with a skirt
    /// down to <paramref name="skirt"/> on its four sides.
    /// </summary>
    public void Heightfield(AshfallKind kind, float x0, float y0, float x1, float y1, float cell, float skirt, Func<float, float, float> height)
    {
        int nx = (int)MathF.Round((x1 - x0) / cell), ny = (int)MathF.Round((y1 - y0) / cell);
        var h = new float[nx + 1, ny + 1];
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++) h[i, j] = height(x0 + i * cell, y0 + j * cell);
        Vector3 V(int i, int j) => new(x0 + i * cell, y0 + j * cell, h[i, j]);
        var t = new List<Vector3>(nx * ny * 6 + (nx + ny) * 12);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (((i + j) & 1) == 0) t.AddRange([V(i, j), V(i + 1, j), V(i + 1, j + 1), V(i, j), V(i + 1, j + 1), V(i, j + 1)]);
                else t.AddRange([V(i, j), V(i + 1, j), V(i, j + 1), V(i + 1, j), V(i + 1, j + 1), V(i, j + 1)]);
            }
        void Skirt(Vector3 a, Vector3 b, Vector3 outward)
        {
            var a0 = a with { Z = skirt };
            var b0 = b with { Z = skirt };
            Wind(a0, b0, b, outward, t);
            Wind(a0, b, a, outward, t);
        }
        for (int i = 0; i < nx; i++) { Skirt(V(i, 0), V(i + 1, 0), -Vector3.UnitY); Skirt(V(i, ny), V(i + 1, ny), Vector3.UnitY); }
        for (int j = 0; j < ny; j++) { Skirt(V(0, j), V(0, j + 1), -Vector3.UnitX); Skirt(V(nx, j), V(nx, j + 1), Vector3.UnitX); }
        Parts.Add(new AshfallPart(kind, t.ToArray()));
        Tags.Add(Tag);
    }

    /// <summary>One triangle wound so that its normal points to <paramref name="outward"/>'s side.</summary>
    static void Wind(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, List<Vector3> into)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) (b, c) = (c, b);
        into.AddRange([a, b, c]);
    }

    /// <summary>
    /// Removes every tagged part (the families the kit replaces) and returns them grouped by tag (<c>family</c> or <c>family:id</c>), in first-appearance order. The remaining parts keep their order.
    /// </summary>
    public List<AshfallRemoved> Compact()
    {
        var groups = new List<AshfallRemoved>();
        var lists = new Dictionary<string, List<AshfallPart>>();
        var keep = new List<AshfallPart>();
        for (int i = 0; i < Parts.Count; i++)
        {
            string? tag = Tags[i];
            if (tag == null) { keep.Add(Parts[i]); continue; }
            if (!tag.Contains(':')) { groups.Add(new AshfallRemoved(tag, -1, [Parts[i]])); continue; }   // a tag without an id: every part is its own group (low walls)
            if (!lists.TryGetValue(tag, out var list))
            {
                lists[tag] = list = [];
                int colon = tag.IndexOf(':');
                groups.Add(new AshfallRemoved(colon < 0 ? tag : tag[..colon], colon < 0 ? -1 : int.Parse(tag[(colon + 1)..]), list));
            }
            list.Add(Parts[i]);
        }
        Parts.Clear(); Parts.AddRange(keep);
        Tags.Clear(); Tags.AddRange(keep.Select(_ => (string?)null));
        return groups;
    }

    // ---------------------------------------------------------------- rubble

    /// <summary>A small integer hash (no random numbers; the same on every machine).</summary>
    public static uint Mix(uint seed, uint i)
    {
        uint h = seed * 2654435761u + i * 40503u + 0x9E3779B9u;
        h ^= h >> 15; h *= 0x2C1B3C6Du;
        h ^= h >> 12; h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    static readonly float[] Low = [0.2f, 0.3f, 0.35f, 0.3f], Tall = [1.0f, 1.5f, 2.0f, 2.5f];

    /// <summary>
    /// A cluster of rubble: up to <paramref name="count"/> oriented boxes within <paramref name="spread"/> m of the centre on the 0.5 m grid, yaw in 15 degree steps, each either at most 0.35 m high
    /// (walked over) or 1.0 to 2.5 m (blocking; nothing in the 0.4..0.9 m dead band), gaps to every other piece and structure at least 1.2 m (circumscribed circles; touching pieces never occur).
    /// <paramref name="surface"/> is the ground's height there; <paramref name="tallShare"/>: of 4 pieces, how many are tall.
    /// </summary>
    public int Cluster(uint seed, float cx, float cy, int count, float spread, int tallShare, Func<float, float, float> surface)
    {
        int placed = 0;
        for (uint i = 0; i < (uint)count * 8 && placed < count; i++)
        {
            uint h = Mix(seed, i);
            float Q(float v) => MathF.Round(v * 2f) / 2f;
            float x = cx + Q(((int)(h & 63) - 32) * spread / 32f), y = cy + Q((((int)(h >> 6) & 63) - 32) * spread / 32f);
            float sx = 1f + ((h >> 12) & 3) * 0.5f, sy = 1f + ((h >> 14) & 3) * 0.5f;
            int yaw = (int)((h >> 16) % 24);
            bool tall = (int)((h >> 22) & 3) < tallShare;
            float height = tall ? Tall[(h >> 24) & 3] : Low[(h >> 24) & 3];
            float r = 0.5f * MathF.Sqrt(sx * sx + sy * sy);
            if (!Clear(x, y, r)) continue;
            float z = surface(x, y);
            if (surface(x - r, y - r) != z || surface(x + r, y + r) != z || surface(x - r, y + r) != z || surface(x + r, y - r) != z) continue;   // flat ground only
            Oriented(AshfallKind.Rubble, x, y, z, z + height, sx, sy, yaw);
            _rubble.Add(new Vector3(x, y, r));
            placed++;
        }
        return placed;
    }

    bool Clear(float x, float y, float r)
    {
        foreach (var p in _rubble) if (Vector2.Distance(new Vector2(x, y), new Vector2(p.X, p.Y)) - r - p.Z < 1.2f) return false;
        foreach (var k in Reserved) if (Vector2.Distance(new Vector2(x, y), new Vector2(k.X, k.Y)) < r + k.Z) return false;
        foreach (var f in Footprints)
        {
            float dx = MathF.Max(MathF.Max(f.X - x, x - f.Z), 0f), dy = MathF.Max(MathF.Max(f.Y - y, y - f.W), 0f);
            if (MathF.Sqrt(dx * dx + dy * dy) - r < 1.2f) return false;
        }
        return true;
    }
}

/// <summary>A group of removed graybox parts (one <c>family:id</c> tag, or one part of an id-less tag): the family, the id and the parts.</summary>
internal sealed record AshfallRemoved(string Family, int Id, IReadOnlyList<AshfallPart> Parts);
