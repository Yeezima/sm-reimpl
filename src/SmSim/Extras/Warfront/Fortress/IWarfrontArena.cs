using System.Numerics;
using SmSim.Extras.Musou.Ashfall;

namespace SmSim.Extras.Warfront.Fortress;

/// <summary>One Ork source of the fortress and the stage it belongs to.</summary>
/// <param name="Name">The marker's name.</param>
/// <param name="Position">Where it stands.</param>
/// <param name="Stage">0: the outer streets inside the gate; 1: the flank lanes and the strip before the arena; 2: the arena plaza (the Warboss's own ground).</param>
public sealed record WarfrontArenaSource(string Name, Vector3 Position, int Stage);

/// <summary>A named post (a Marine start post or a defender post); <paramref name="Group"/> names the group it belongs to.</summary>
public sealed record WarfrontArenaPost(string Name, Vector3 Position, string Group);

/// <summary>One named dimension for its spec row (everything ours, an estimate).</summary>
public sealed record WarfrontArenaDimension(string Name, float Value, string Units);

/// <summary>A way from the landing zone to the Warboss spot: the named stops it must pass (the gates check that the walk through them exists).</summary>
public sealed record WarfrontArenaRoute(string Name, IReadOnlyList<Vector3> Via);

/// <summary>
/// Everything the final battle reads about the Ork fortress (plan section 2, "map interface"): the collision world and its hash, the landing zone, the Warboss's spot, the Ork sources by stage,
/// the posts, the routes and the widths of every opening an Ork walks. <see cref="WarfrontFortressMap"/> is the generated one; a handmade map (a Mapmaker export) that supplies the same members replaces it.
/// </summary>
public interface IWarfrontArena
{
    /// <summary>The arena's name.</summary>
    string Name { get; }
    /// <summary>The arena's version (raised with any change of its geometry or markers; folded into its hash).</summary>
    uint Version { get; }
    /// <summary>The collision world the battle walks and shoots in (a handmade map's collision, or the generated level's).</summary>
    CollisionWorld Collision { get; }
    /// <summary>The geometry and marker hash (the Challenge's map-hash check compares it).</summary>
    ulong Hash { get; }
    /// <summary>Where the Marines land.</summary>
    Vector3 LandingZone { get; }
    /// <summary>The centre of the Warboss's arena: where the Warboss stands.</summary>
    Vector3 BossSpot { get; }
    /// <summary>The arena plaza's radius round <see cref="BossSpot"/>, metres.</summary>
    float ArenaRadius { get; }
    /// <summary>The Ork sources in stages (stages count up from 0 without a gap).</summary>
    IReadOnlyList<WarfrontArenaSource> Sources { get; }
    /// <summary>The Marines' start posts and any defender posts.</summary>
    IReadOnlyList<WarfrontArenaPost> Posts { get; }
    /// <summary>At least two army-width routes from the landing zone to <see cref="BossSpot"/>.</summary>
    IReadOnlyList<WarfrontArenaRoute> Routes { get; }
    /// <summary>The widths of every opening an Ork walks, metres: each at least <see cref="WarfrontArenaGates.OrkOpening"/>.</summary>
    IReadOnlyList<WarfrontArenaDimension> RouteWidths { get; }
    /// <summary>The walking graph's area and seeds.</summary>
    NavArea WalkArea { get; }
}

/// <summary>The marker hash and the structural gates every arena must pass (generated or handmade).</summary>
public static class WarfrontArenaGates
{
    /// <summary>An opening an Ork walks is at least this wide and this clear, metres (a Boss Nob stands about 6.9 m).</summary>
    public const float OrkOpening = 8f;
    /// <summary>A passage a Marine walks is at least this wide, metres.</summary>
    public const float MarinePassage = 3f;

    /// <summary>The geometry hash of a part list at a version (what <see cref="MarkerHash"/> starts from).</summary>
    public static ulong GeometryHash(IReadOnlyList<AshfallPart> parts, uint version) => AshfallMap.HashOf(parts, version);

    /// <summary>The geometry hash with the markers folded in, so a moved marker changes the level's hash.</summary>
    public static ulong MarkerHash(ulong h, IReadOnlyList<ControlPoint> points, IReadOnlyList<StartArea> areas, IReadOnlyList<WarfrontArenaSource> sources, IReadOnlyList<WarfrontArenaPost> posts,
        IReadOnlyList<WarfrontArenaRoute> routes, IReadOnlyList<Vector3> spawns)
    {
        ulong F(ulong x, float f) => Geometry.Fnv(x, BitConverter.SingleToUInt32Bits(f));
        ulong V3(ulong x, Vector3 v) => F(F(F(x, v.X), v.Y), v.Z);
        ulong S(ulong x, string t) { foreach (char c in t) x = Geometry.Fnv(x, (uint)c); return x; }
        foreach (var p in points) h = F(F(V3(S(h, p.Name), p.Center), p.Radius), p.HalfHeight);
        foreach (var a in areas) h = F(V3(V3(Geometry.Fnv(h, (uint)a.Side), a.Center), a.Size), a.Yaw);
        foreach (var s in sources) h = Geometry.Fnv(V3(S(h, s.Name), s.Position), (uint)s.Stage);
        foreach (var q in posts) h = V3(S(S(h, q.Name), q.Group), q.Position);
        foreach (var r in routes) { h = S(h, r.Name); foreach (var v in r.Via) h = V3(h, v); }
        foreach (var v in spawns) h = V3(h, v);
        return h;
    }

    /// <summary>The checks that need no walking graph: widths, route count, stages, the markers inside the walking area, One line per failure (empty: all pass).</summary>
    public static IReadOnlyList<string> CheckStructure(IWarfrontArena a)
    {
        var f = new List<string>();
        foreach (var d in a.RouteWidths)
            if (d.Value < OrkOpening) f.Add($"opening '{d.Name}' is {d.Value} m, an Ork needs {OrkOpening}");
        if (a.Routes.Count < 2) f.Add($"only {a.Routes.Count} route(s): at least 2 are needed");
        if (a.Sources.Count == 0) f.Add("no Ork sources");
        else
        {
            var stages = a.Sources.Select(s => s.Stage).Distinct().Order().ToList();
            for (int i = 0; i < stages.Count; i++)
                if (stages[i] != i) { f.Add("stages do not count up from 0 without a gap"); break; }
        }
        var area = a.WalkArea;
        bool Inside(Vector3 p) => p.X >= area.Min.X && p.X <= area.Max.X && p.Y >= area.Min.Y && p.Y <= area.Max.Y;
        if (!Inside(a.LandingZone)) f.Add("landing zone outside the walking area");
        if (!Inside(a.BossSpot)) f.Add("boss spot outside the walking area");
        foreach (var s in a.Sources)
            if (!Inside(s.Position)) f.Add($"source '{s.Name}' outside the walking area");
        foreach (var p in a.Posts)
            if (!Inside(p.Position)) f.Add($"post '{p.Name}' outside the walking area");
        return f;
    }

    /// <summary>The checks over a walking graph: the landing zone reaches the boss spot through every route (whole, not partial) and every source is reachable from the landing zone. One line per failure.</summary>
    public static IReadOnlyList<string> CheckWalk(IWarfrontArena a, NavGraph nav)
    {
        var f = new List<string>();
        foreach (var route in a.Routes)
        {
            var stops = new List<Vector3> { a.LandingZone };
            stops.AddRange(route.Via);
            stops.Add(a.BossSpot);
            for (int i = 0; i + 1 < stops.Count; i++)
            {
                var path = nav.FindPath(stops[i], stops[i + 1], goalSnap: 1.5f);
                if (path is null || path.Partial) { f.Add($"route '{route.Name}' leg {i} has no whole path"); break; }
            }
        }
        foreach (var s in a.Sources)
        {
            var path = nav.FindPath(a.LandingZone, s.Position, goalSnap: 1.5f);
            if (path is null || path.Partial) f.Add($"source '{s.Name}' is not reachable from the landing zone");
        }
        return f;
    }
}
