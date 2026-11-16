using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Fortress;

/// <summary>
/// What the fortress battle reads about its arena, derived once from an <see cref="IWarfrontArena"/> (slice W14b): the Musou layout, the stage of every layout source, the three waypoints, the posts that
/// hold elites and the two approaches that hold a Boss Ork. Nothing here names a generator type, so a handmade map that supplies the interface gets the same battle.
/// </summary>
/// <param name="Layout">The battle's places: landing zone; the three "positions" (waypoints, never an objective); the HQ and the boss spot are both the arena's boss spot; the sources, reordered so the first four suit the battle's four spawn points.</param>
/// <param name="SourceStage">The stage (0 to <paramref name="MaxStage"/>) of each entry of <c>Layout.Sources</c>.</param>
/// <param name="Breach">Waypoint 0: the second way in.</param>
/// <param name="Gate">Waypoint 1: the first way in, the layout's "centre" (a fresh spawn faces it).</param>
/// <param name="PlazaGate">Waypoint 2: the last stop before the arena plaza.</param>
/// <param name="BossSpot">Where the Warboss stands.</param>
/// <param name="ArenaRadius">The plaza's radius round <paramref name="BossSpot"/>, metres.</param>
/// <param name="ElitePosts">The posts of the arena's "elite" group: each holds a leashed pair.</param>
/// <param name="ApproachPosts">The spots on the two approaches where one Boss Ork stands.</param>
/// <param name="MaxStage">The highest stage the sources have.</param>
public sealed record WarfrontFortressPlan(TfwLayout Layout, int[] SourceStage, Vector3 Breach, Vector3 Gate, Vector3 PlazaGate, Vector3 BossSpot, float ArenaRadius,
    IReadOnlyList<Vector3> ElitePosts, IReadOnlyList<Vector3> ApproachPosts, int MaxStage);

/// <summary>Builds a <see cref="WarfrontFortressPlan"/> from any <see cref="IWarfrontArena"/> (slice W14b).</summary>
public static class WarfrontArenaLayout
{
    /// <summary>The post group whose posts hold the elite pairs.</summary>
    public const string ElitePostGroup = "elite";

    /// <summary>The stage the battle's four spawn points (layout sources 0 to 3) want, so that the crowds at the two entries start with the streets, the one at the plaza gate with the strip and the fourth in the plaza (ours).</summary>
    static readonly int[] SpawnPointStages = [0, 0, 1, 2];

    /// <summary>The battle's layout of <paramref name="arena"/> (see <see cref="Plan"/>).</summary>
    public static TfwLayout ToLayout(IWarfrontArena arena) => Plan(arena).Layout;

    /// <summary>The plan of <paramref name="arena"/>: deterministic, a pure function of the interface's members.</summary>
    public static WarfrontFortressPlan Plan(IWarfrontArena arena)
    {
        var (breach, gate, plazaGate) = Waypoints(arena);
        var (sources, stages) = Order(arena.Sources);
        var layout = new TfwLayout(arena.LandingZone, [breach, gate, plazaGate], arena.BossSpot, sources, [10f, 12f, 14f], [4f, 4f, 4f], arena.BossSpot);
        var elite = arena.Posts.Where(p => p.Group == ElitePostGroup).Select(p => p.Position).ToArray();
        return new WarfrontFortressPlan(layout, stages, breach, gate, plazaGate, arena.BossSpot, arena.ArenaRadius, elite, Approaches(arena), stages.Length == 0 ? 0 : stages.Max());
    }

    /// <summary>
    /// The three waypoints from the routes (ours): the first stops of the routes (the entries) sorted by their distance from the landing zone, the nearest is the gate and the next one 8 m or more from it the breach (the gate again
    /// when there is only one entry); the plaza gate is the last stop, over every route, nearest the boss spot.
    /// </summary>
    static (Vector3 Breach, Vector3 Gate, Vector3 PlazaGate) Waypoints(IWarfrontArena a)
    {
        var landing = a.LandingZone;
        var firsts = new List<Vector3>();
        foreach (var r in a.Routes)
        {
            var first = r.Via.Count > 0 ? r.Via[0] : a.BossSpot;
            if (!firsts.Exists(f => Flat(f, first) < WarfrontFortressBattleRules.DistinctWaypoint)) firsts.Add(first);
        }
        var ordered = firsts.Select((v, i) => (v, i)).OrderBy(t => Flat(t.v, landing)).ThenBy(t => t.i).Select(t => t.v).ToList();
        var gate = ordered.Count > 0 ? ordered[0] : a.BossSpot;
        var breach = ordered.Count > 1 ? ordered[1] : gate;
        var lasts = a.Routes.Select(r => r.Via.Count > 0 ? r.Via[^1] : a.BossSpot).ToList();
        var plazaGate = lasts.Count > 0 ? lasts.Select((v, i) => (v, i)).OrderBy(t => Flat(t.v, a.BossSpot)).ThenBy(t => t.i).First().v : a.BossSpot;
        return (breach, gate, plazaGate);
    }

    /// <summary>One Boss Ork's spot on each of the last two routes: the middle stop of the route (the boss spot for a route with no stops). A single route holds one.</summary>
    static IReadOnlyList<Vector3> Approaches(IWarfrontArena a)
    {
        var list = new List<Vector3>();
        var routes = a.Routes;
        for (int i = Math.Max(0, routes.Count - WarfrontFortressBattleRules.ApproachBossOrks); i < routes.Count; i++)
            list.Add(routes[i].Via.Count > 0 ? routes[i].Via[routes[i].Via.Count / 2] : a.BossSpot);
        return list;
    }

    /// <summary>
    /// The sources in stage order, then the first four hoisted to <see cref="SpawnPointStages"/> (the battle spawns crowds at spawn points 0 to 3 and gates each one by source index; a stage-2 source is the fourth point), padded
    /// by repeating from the start when the arena has fewer than four (the battle indexes the first four).
    /// </summary>
    static (Vector3[] Sources, int[] Stages) Order(IReadOnlyList<WarfrontArenaSource> sources)
    {
        var byStage = sources.Select((s, i) => (s, i)).OrderBy(t => t.s.Stage).ThenBy(t => t.i).Select(t => t.s).ToList();
        if (byStage.Count == 0) return ([Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero], [0, 0, 0, 0]);
        int top = byStage[^1].Stage;
        var taken = new bool[byStage.Count];
        var order = new List<int>();
        foreach (int want in SpawnPointStages)
        {
            int stage = Math.Min(want, top), pick = -1;
            for (int i = 0; i < byStage.Count && pick < 0; i++) if (!taken[i] && byStage[i].Stage == stage) pick = i;
            for (int i = 0; i < byStage.Count && pick < 0; i++) if (!taken[i] && byStage[i].Stage <= stage) pick = i;
            for (int i = 0; i < byStage.Count && pick < 0; i++) if (!taken[i]) pick = i;
            if (pick < 0) pick = byStage.FindIndex(s => s.Stage == stage);   // every source is taken already: a spawn point reuses one
            if (pick < 0) pick = 0;
            if (!taken[pick]) { taken[pick] = true; order.Add(pick); }
            else order.Add(pick);
        }
        var rest = new List<int>();
        for (int i = 0; i < byStage.Count; i++) if (!taken[i]) rest.Add(i);
        var all = new List<int>(order);
        all.AddRange(rest);
        for (int k = 0; all.Count < 4; k++) all.Add(all[k % all.Count]);   // fewer than four: repeat from the start
        return ([.. all.Select(i => byStage[i].Position)], [.. all.Select(i => byStage[i].Stage)]);
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();
}
