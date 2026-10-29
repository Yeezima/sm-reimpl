using System.Numerics;

namespace SmSim;

/// <summary>
/// A movable prop the level holds as solid cover (M39): world-space triangles (Z up, metres) and their surface material id
/// (the prop's <c>CShapeMesh.MaterialID</c>, the same ids as the level's surfaces). Manufactorum's green ammo crates
/// (<c>ammo_crate_A</c>, 49 placements) are the props the owner asked for: in the original they are 70 kg rigid bodies whose
/// shape is not in the level's baked collision. Here they stay where they are placed (**change from the original**: they do
/// not move; spec/behavior.md M39). <see cref="CollisionWorld"/> adds them to both of its meshes.
/// </summary>
public sealed record CollisionProp(string Name, Vector3[] Vertices, int[] Indices, int Surface)
{
    public int TriangleCount => Indices.Length / 3;

    /// <summary>The props as given (none: empty), after checking each has finite vertices and whole, in-range triangles.</summary>
    internal static CollisionProp[] Checked(IEnumerable<CollisionProp>? props)
    {
        var list = props?.ToArray() ?? [];
        foreach (var p in list)
        {
            if (p is null || p.Vertices is null || p.Indices is null || p.Indices.Length == 0 || p.Indices.Length % 3 != 0)
                throw new ArgumentException("A prop has no whole triangles.", nameof(props));
            if (p.Vertices.Any(v => !Geometry.Finite(v)) || p.Indices.Any(i => (uint)i >= (uint)p.Vertices.Length))
                throw new ArgumentException($"Prop '{p.Name}' has a non-finite vertex or an index out of range.", nameof(props));
        }
        return list;
    }

    /// <summary><paramref name="level"/> plus every prop's triangles in list order (with their surface ids when <paramref name="surfaces"/>); the level itself without props.</summary>
    internal static StaticMesh AddTo(StaticMesh level, IReadOnlyList<CollisionProp> props, bool surfaces)
    {
        if (props.Count == 0) return level;
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var materials = new List<int>();
        foreach (var p in props)
        {
            int first = vertices.Count;
            vertices.AddRange(p.Vertices);
            foreach (int i in p.Indices) indices.Add(first + i);
            materials.AddRange(Enumerable.Repeat(p.Surface, p.TriangleCount));
        }
        return level.With([.. vertices], [.. indices], surfaces ? [.. materials] : null);
    }

    /// <summary>The first weapon-mesh triangle number of each prop, the level's <paramref name="levelTriangles"/> coming first.</summary>
    internal static int[] Starts(int levelTriangles, IReadOnlyList<CollisionProp> props)
    {
        var starts = new int[props.Count + 1];
        starts[0] = levelTriangles;
        for (int k = 0; k < props.Count; k++) starts[k + 1] = starts[k] + props[k].TriangleCount;
        return starts;
    }

    /// <summary>FNV-1a 64 of <paramref name="h"/> and each prop's surface, counts, vertex floats and indices; none: <paramref name="h"/>.</summary>
    internal static ulong Hash(ulong h, IReadOnlyList<CollisionProp> props)
    {
        foreach (var p in props)
        {
            h = Geometry.Fnv(h, (uint)p.Surface);
            h = Geometry.Fnv(h, (uint)p.Vertices.Length);
            h = Geometry.Fnv(h, (uint)p.Indices.Length);
            foreach (var v in p.Vertices)
            {
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.X));
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Y));
                h = Geometry.Fnv(h, BitConverter.SingleToUInt32Bits(v.Z));
            }
            foreach (int i in p.Indices) h = Geometry.Fnv(h, (uint)i);
        }
        return h;
    }
}

/// <summary>
/// A hit-scan shot's penetration (M39; wargear <c>delivery/penetration/entry</c>: the default entry, a charge stage's, or a
/// perk's, <see cref="Pierces.Of"/>): <see cref="Points"/> (<c>points</c>) and what passing through costs, from
/// <c>material_penetration_costs</c> (a missing cost: +∞, never passed): one prop <see cref="PropCost"/> (<c>default</c>; a prop
/// is read as material <c>default</c>, interpretation), a player hit in the body <see cref="BodyCost"/> (<c>default_body</c>, the
/// player blueprint's <c>material_simulation</c> for body, arms and legs) or the head <see cref="HeadCost"/> (<c>default_head</c>);
/// and the action at the point where the shot leaves a prop (<c>exit_environment_action</c>'s <c>area_of_effect</c>: a cone along
/// the shot, <see cref="Exit"/>, damaging every player it covers once, with line of sight from the exit point;
/// <see cref="ExitHitsAllies"/> for its <c>target_filter</c> "target_all", false for "target_enemies_and_neutrals").
/// Install: the lascannon (10 points; props 1, body 1, head 1; charged: every cost 0), the stalker bolter (10; props 5, body 4,
/// head 3), the bolter with the Kraken Bolts perk (2; props 1, body 1, head 1); every other multiplayer weapon has none or 0
/// points. **Rules (interpretation; the owner confirmed the outcomes 2026-10-28: the lascannon passes up to 10 players, the
/// stalker 2, Kraken Bolts 2, and these three pass the green crates):** the shot passes a player or prop while the points left
/// are at least its cost, spending it (a cost of 0 is free); otherwise it stops there (a player it stops in is still hit);
/// every player on the ray before the first prop takes the shot's full damage at its distance (the data has no reduction);
/// only props are pierced, never the level's own collision; past each pierced prop only the exit cone deals damage (the owner:
/// a shot through a crate does not keep its full damage).
/// </summary>
public sealed record PierceSpec(float Points, float PropCost, ConeSpec? Exit, bool ExitHitsAllies)
{
    /// <summary>What passing a player hit in the body costs (<c>default_body</c>; +∞: never).</summary>
    public float BodyCost { get; init; } = float.PositiveInfinity;
    /// <summary>What passing a player hit in the head costs (<c>default_head</c>; +∞: never).</summary>
    public float HeadCost { get; init; } = float.PositiveInfinity;

    /// <summary>Whether the shot passes through one more prop with <paramref name="remaining"/> points left.</summary>
    public bool CanPierce(float remaining) => CanPierce(remaining, PropCost);

    /// <summary>Whether the shot passes something costing <paramref name="cost"/> with <paramref name="remaining"/> points left.</summary>
    public bool CanPierce(float remaining, float cost) => Points > 0f && cost >= 0f && remaining >= cost;

    internal static void Write(BinaryWriter w, PierceSpec? p)
    {
        w.Write(p is not null);
        if (p is null) return;
        w.Write(p.Points); w.Write(p.PropCost); w.Write(p.BodyCost); w.Write(p.HeadCost); w.Write(p.ExitHitsAllies);
        ConeSpec.Write(w, p.Exit);
    }
}

/// <summary>A perk's penetration (M39): the upgrade string its wargear entry requires and the spec it gives (the bolter's Kraken Bolts).</summary>
public sealed record UpgradePierce(string Upgrade, PierceSpec Spec);

/// <summary>Which penetration a shot has, and what a player costs it (M39).</summary>
public static class Pierces
{
    /// <summary>
    /// The penetration of a weapon's shot named by <paramref name="stage"/> (0 basic, k a charge stage): a charge stage's own;
    /// for the basic shot the first perk entry whose upgrade <paramref name="hasUpgrade"/> grants (the entry that requires one
    /// wins over the default, as wargear entries do), else the default entry's; null: it stops at the first surface.
    /// </summary>
    public static PierceSpec? Of(WeaponSpec weapon, int stage, Func<string, bool> hasUpgrade)
    {
        if (stage > 0) return Shots.ChargeShotOf(weapon, stage)?.Pierce;
        foreach (var p in weapon.PerkPierces)
            if (hasUpgrade(p.Upgrade)) return p.Spec;
        return weapon.Pierce;
    }

    /// <summary>
    /// Whether the player's perks switch on <paramref name="upgrade"/> (the string a wargear entry requires, e.g. the bolter's
    /// <c>mp_perk_weap_bolter_kraken_rounds</c>): one of <see cref="Loadouts.UpgradeStrings"/> (the perks in the player's two
    /// slots; the perk data's <c>apply_upgrade_string</c>), ignoring case.
    /// </summary>
    public static bool HasUpgrade(SimConfig config, PlayerState player, string upgrade)
    {
        foreach (var u in Loadouts.UpgradeStrings(config, player))
            if (string.Equals(u, upgrade, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>What passing a player hit in <paramref name="zone"/> costs (+∞ without penetration or an unknown zone).</summary>
    public static float PlayerCost(PierceSpec? pierce, HitZone zone) => pierce is null ? float.PositiveInfinity
        : zone == HitZone.Head ? pierce.HeadCost : zone == HitZone.Body ? pierce.BodyCost : float.PositiveInfinity;
}

/// <summary>
/// A prop a shot passed through: which (<see cref="CollisionWorld.Props"/> index), the distances where it entered and left it,
/// and whether it left into the open (no other prop around the exit point; false also when the shot stopped or ran out inside it).
/// </summary>
public readonly record struct PiercedProp(int Prop, float Entry, float Exit, bool IntoOpen);

/// <summary>
/// Where a hit-scan ray went (<see cref="CollisionWorld.TraceShot"/>): its first weapon-collision hit (level or prop), where it
/// stopped (the level, or a prop it could not pierce; null: it reached its range), and the props it passed through, in the order it left them.
/// </summary>
public readonly record struct ShotTrace(RayHit? First, RayHit? Stop, IReadOnlyList<PiercedProp> Pierced);

public sealed partial class CollisionWorld
{
    /// <summary>Most props one trace passes through (a guard; far above any stack on the maps).</summary>
    public const int MaxPiercedProps = 64;
    /// <summary>Props whose spans along a ray are closer than this are one span: touching and overlapping crates (metres; ours).</summary>
    public const float SpanGap = 2e-3f;
    /// <summary>Hits on one prop closer than this along a ray are one hit (a shared edge or vertex; metres; ours).</summary>
    const float SameHit = 1e-5f;

    /// <summary>The prop (index into <see cref="Props"/>) a <see cref="Weapons"/> triangle belongs to, or -1 for the level's own.</summary>
    public int PropOfShotTriangle(int triangle)
    {
        if (triangle < _levelShotTriangles || triangle >= _propStarts[^1]) return -1;
        int k = Array.BinarySearch(_propStarts, triangle);
        return k >= 0 ? k : ~k - 1;
    }

    /// <summary>
    /// A hit-scan ray from <paramref name="origin"/> (unit <paramref name="direction"/>) within <paramref name="range"/>. Without
    /// <paramref name="pierce"/> (or props) it stops at the first <see cref="Weapons"/> surface. With it, every prop is taken on
    /// its own: where the ray enters and leaves it (its own triangles, two-sided; a ray starting inside it enters at 0; a ray
    /// that only grazes an edge or face does not enter), cut at the level's first surface, which always stops the shot.
    /// Spans closer than <see cref="SpanGap"/> merge (touching, coplanar or overlapping crates are one span); in entry order each
    /// prop costs its <see cref="PierceSpec.PropCost"/> (**per prop**, not per span; interpretation), and the first prop the
    /// points left do not cover stops the shot at its entry. <see cref="PiercedProp.IntoOpen"/> is set on the prop that ends a
    /// span (the exit action fires once per span, where the ray leaves it into the open).
    /// </summary>
    public ShotTrace TraceShot(Vector3 origin, Vector3 direction, float range, PierceSpec? pierce)
    {
        var first = Weapons.Raycast(origin, direction, range);
        if (first is null || pierce is null || Props.Count == 0) return new ShotTrace(first, first, []);
        var level = _levelWeapons.Raycast(origin, direction, range);
        float limit = level?.Distance ?? range;
        var spans = new List<(int Prop, float Entry, float Exit, RayHit EntryHit)>();
        for (int k = 0; k < Props.Count; k++)
            if (PropSpan(k, origin, direction, limit) is { } s) spans.Add((k, s.Entry, s.Exit, s.Hit));
        if (spans.Count == 0) return new ShotTrace(first, level, []);
        spans.Sort((a, b) => a.Entry != b.Entry ? a.Entry.CompareTo(b.Entry) : a.Prop.CompareTo(b.Prop));

        var pierced = new List<PiercedProp>();
        float remaining = pierce.Points;
        RayHit? stop = level;
        int i = 0;
        while (i < spans.Count)
        {
            // One merged span: props in entry order whose entries fall before the span's end (plus the gap).
            int j = i;
            float end = spans[i].Exit;
            while (j + 1 < spans.Count && spans[j + 1].Entry <= end + SpanGap) { j++; end = MathF.Max(end, spans[j].Exit); }
            int passed = 0;
            for (int k = i; k <= j; k++)
            {
                if (!pierce.CanPierce(remaining) || pierced.Count + passed >= MaxPiercedProps) { stop = spans[k].EntryHit; break; }
                remaining -= pierce.PropCost;
                passed++;
            }
            bool through = passed == j - i + 1;
            bool open = through && end < limit;   // a span cut by the level (or the range) is not left into the open
            int last = -1;
            if (open) for (int k = i; k <= j; k++) if (last < 0 || spans[k].Exit >= spans[last].Exit) last = k;
            for (int k = i; k < i + passed; k++)
                pierced.Add(new PiercedProp(spans[k].Prop, spans[k].Entry, spans[k].Exit, k == last));
            if (!through) return new ShotTrace(first, stop, pierced);
            i = j + 1;
        }
        return new ShotTrace(first, level, pierced);
    }

    /// <summary>
    /// Where the ray enters and leaves prop <paramref name="k"/> before <paramref name="limit"/>: null when it misses, only grazes
    /// (one distinct crossing) or enters at or beyond the limit. Entry 0 when the origin is inside (<see cref="Inside"/>); exit
    /// the last crossing (the first when starting inside), cut at the limit. The entry hit names the prop's triangle and surface.
    /// </summary>
    (float Entry, float Exit, RayHit Hit)? PropSpan(int k, Vector3 origin, Vector3 direction, float limit)
    {
        var p = Props[k];
        if (!RayMeetsBox(origin, direction, limit, _propMin[k], _propMax[k])) return null;
        var hits = Crossings(p, origin, direction);
        float entry, exit;
        int triangle;
        if (Inside(p, origin, direction))
        {
            entry = 0f;
            exit = hits.Count > 0 ? hits[0].T : float.PositiveInfinity;
            triangle = hits.Count > 0 ? hits[0].Triangle : 0;
        }
        else
        {
            if (hits.Count < 2) return null;
            entry = hits[0].T; exit = hits[^1].T; triangle = hits[0].Triangle;
        }
        if (entry >= limit) return null;
        var a = p.Vertices[p.Indices[3 * triangle]];
        var n = Vector3.Cross(p.Vertices[p.Indices[3 * triangle + 1]] - a, p.Vertices[p.Indices[3 * triangle + 2]] - a);
        n = n.LengthSquared() > 0f ? Vector3.Normalize(n) : -direction;
        if (Vector3.Dot(n, direction) > 0f) n = -n;
        return (entry, MathF.Min(exit, limit), new RayHit(entry, origin + direction * entry, n, _propStarts[k] + triangle));
    }

    /// <summary>The distinct distances (≥ 0) where a ray crosses a prop's triangles, ascending, each with a triangle.</summary>
    static List<(float T, int Triangle)> Crossings(CollisionProp p, Vector3 origin, Vector3 direction)
    {
        var hits = new List<(float T, int Triangle)>();
        for (int t = 0; t < p.TriangleCount; t++)
        {
            var a = p.Vertices[p.Indices[3 * t]]; var b = p.Vertices[p.Indices[3 * t + 1]]; var c = p.Vertices[p.Indices[3 * t + 2]];
            if (RayTriangle(origin, direction, a, b, c) is float d && d >= 0f) hits.Add((d, t));
        }
        hits.Sort((x, y) => x.T != y.T ? x.T.CompareTo(y.T) : x.Triangle.CompareTo(y.Triangle));
        var distinct = new List<(float T, int Triangle)>();
        foreach (var h in hits) if (distinct.Count == 0 || h.T - distinct[^1].T > SameHit) distinct.Add(h);
        return distinct;
    }

    /// <summary>Whether a point is inside a closed prop: odd crossing counts along the shot and two skewed fixed directions (two of three).</summary>
    static bool Inside(CollisionProp p, Vector3 point, Vector3 direction)
    {
        int odd = 0;
        foreach (var d in new[] { direction, Vector3.Normalize(new Vector3(0.31f, 0.27f, 0.91f)), Vector3.Normalize(new Vector3(-0.83f, 0.47f, -0.29f)) })
            if (Crossings(p, point, d).Count % 2 == 1) odd++;
        return odd >= 2;
    }

    /// <summary>Möller–Trumbore, two-sided: the distance along the ray to the triangle, or null.</summary>
    static float? RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a; var e2 = c - a;
        var q = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, q);
        if (MathF.Abs(det) < 1e-12f) return null;
        float inv = 1f / det;
        var s = o - a;
        float u = Vector3.Dot(s, q) * inv;
        if (u < -1e-6f || u > 1f + 1e-6f) return null;
        var r = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, r) * inv;
        if (v < -1e-6f || u + v > 1f + 1e-6f) return null;
        return Vector3.Dot(e2, r) * inv;
    }

    /// <summary>Whether the segment from <paramref name="o"/> along <paramref name="d"/> to <paramref name="max"/> meets the box (1e-4 m slack).</summary>
    static bool RayMeetsBox(Vector3 o, Vector3 d, float max, Vector3 lo, Vector3 hi)
    {
        float t0 = 0f, t1 = max;
        for (int axis = 0; axis < 3; axis++)
        {
            float oa = Geometry.Axis(o, axis), da = Geometry.Axis(d, axis), l = Geometry.Axis(lo, axis) - 1e-4f, h = Geometry.Axis(hi, axis) + 1e-4f;
            if (MathF.Abs(da) < 1e-12f) { if (oa < l || oa > h) return false; continue; }
            float a = (l - oa) / da, b = (h - oa) / da;
            if (a > b) (a, b) = (b, a);
            t0 = MathF.Max(t0, a); t1 = MathF.Min(t1, b);
            if (t0 > t1) return false;
        }
        return true;
    }
}

public sealed partial class World
{
    /// <summary>
    /// A hit-scan shot that passed through props and met no player before the first (<see cref="CollisionWorld.TraceShot"/>):
    /// at each exit into the open (<see cref="PiercedProp.IntoOpen"/>) the <see cref="PierceSpec.Exit"/> cone fires along the shot, cut to what is left of the
    /// shot's range. A player takes its damage once per shot (the first exit that reaches it): when the cone's axis meets the
    /// player's hit shapes (head or body as hit) or the cone covers a sample point (body), and the line from the exit point to that
    /// point is clear of weapon collision (as <c>line_of_sight</c> in the file). Returns where the shot went: the nearest damaged
    /// player, else where the ray stopped (the level or an unpierced prop, with its surface), else the point at range.
    /// </summary>
    ShotOutcome ResolvePierce(int shooter, IReadOnlyDictionary<int, PlayerState> seen, IReadOnlyDictionary<int, FlightView> flight, PlayerState state, Vector3 origin, Vector3 direction, float range,
        ShotTrace trace, PierceSpec pierce, IReadOnlySet<int>? alreadyHit = null)
    {
        var outcome = WorldOutcome(origin, direction, range, trace.Stop);
        if (pierce.Exit is not { } cone) return outcome;
        float nearest = float.MaxValue;
        int weapon = Loadouts.WeaponIndex(Config, state, state.Slot);
        var done = alreadyHit is null ? new HashSet<int>() : new HashSet<int>(alreadyHit);   // players the ray already hit are not hit again
        var order = seen.OrderBy(kv => kv.Key).ToList();
        foreach (var p in trace.Pierced)
        {
            if (!p.IntoOpen) continue;
            float length = MathF.Min(cone.Length, range - p.Exit);
            if (!(length > 0f)) continue;
            var exit = origin + direction * p.Exit;
            var reach = cone with { Length = length };
            foreach (var (id, then) in order)
            {
                if (id == shooter || done.Contains(id) || !then.Alive || !_players.TryGetValue(id, out var now) || !now.Alive) continue;
                if (!pierce.ExitHitsAllies && !Hostile(state.Team, now.Team)) continue;
                var body = BodyOf(id, then, flight);   // Ork O4: the target's own volumes; fix/hitboxes: as drawn in a jump
                var (zone, along) = Combat.HitTest(exit, direction, then.Position, body, length, then.Yaw);
                Vector3 point;
                if (zone != HitZone.None) point = exit + direction * along;
                else if (Cones.Coverage(reach, exit, direction, then.Position, body, then.Yaw) > 0f)
                {
                    zone = HitZone.Body;
                    point = then.Position + new Vector3(0, 0, ChestOf(then));
                    along = Vector3.Distance(exit, point);
                }
                else continue;
                if (Blocked(exit + direction * 2e-3f, point)) continue;
                done.Add(id);
                float distance = p.Exit + along;
                bool armoured = now.Shield > 0f;   // before this hit (hit feedback, protocol 22)
                ApplyHit(shooter, id, cone.Damage, zone, distance, 1f, 0, false, weapon: weapon, direction: direction);
                if (distance < nearest && Harms(shooter, id))
                {
                    nearest = distance;
                    outcome = new ShotOutcome(origin, point, zone == HitZone.Head ? ShotHit.Head : ShotHit.Body, -1, id, armoured);
                }
            }
        }
        return outcome;
    }
}
