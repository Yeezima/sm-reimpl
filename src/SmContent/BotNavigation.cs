using SmSim;

namespace SmContent;

/// <summary>
/// The test bots' navigation graph for a map and a side (test aid, not the original AI), built or read from the private cache
/// the same way by the client and by <c>smtool nav-stats</c>. The graph covers the box around the map's spawns, control points
/// and start areas (plus <see cref="Margin"/> metres), keeping what they reach, over the character collision of the bot's
/// side (<see cref="CollisionWorld.CharacterFor"/>: the other side's spawn-room blockers are walls only for that side, so each
/// side plans around the blockers that stop it and through the ones that do not). A class with a jump pack also gets the jump
/// links for its pack (<see cref="NavJumpSet"/>), flown with the real jump rules on the same collision.
/// </summary>
public static class BotNavigation
{
    /// <summary>Metres around the map's spawns, points and start areas the graph covers (test script).</summary>
    public const float Margin = 30f;

    /// <summary>The graph's settings and area for <paramref name="map"/>; null when it has no spawns, points or start areas to build around.</summary>
    public static (NavSettings Settings, NavArea Area)? Setup(MapContent map, SimConfig config)
    {
        var seeds = map.Spawns.Select(s => s.Position).Concat(map.ControlPoints.Select(p => p.Position)).Concat(map.StartAreas.Select(a => a.Position)).ToList();
        if (seeds.Count == 0) return null;
        return (NavSettings.From(config), NavArea.Around(seeds, Margin));
    }

    /// <summary>The private cache folder (outside the repo).</summary>
    public static string CacheDirectory => Path.Combine(GameInstall.DataRoot, "cache", "nav");

    /// <summary>
    /// The walking graph (no jump links) of <paramref name="side"/> on <paramref name="world"/>, read from the cache or built and cached: the
    /// same graph a bot of that side without a jump pack uses. M44a: the bots read the other side's to find where its players leave their
    /// spawn room (<c>NetworkView.BotSpawn.cs</c>).
    /// </summary>
    public static NavGraph LoadOrBuildWalk(MapContent map, CollisionWorld world, SimConfig config, Team side, string cacheDirectory, out string note)
    {
        var (settings, area) = Setup(map, config) ?? throw new InvalidOperationException("no spawns or points to build around");
        var mesh = world.CharacterFor(side);
        ulong key = NavGraph.KeyFor(mesh, world.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(cacheDirectory, map.Name, key, settings, () => NavGraph.Build(mesh, world.KillVolumes, settings, area), out note);
    }

    /// <summary>
    /// The graph of <paramref name="side"/> on <paramref name="world"/> (with its jump links when <paramref name="playerClass"/>
    /// carries a jump pack in <paramref name="config"/>), read from the cache or built and cached. <paramref name="note"/> says how.
    /// Throws when the map has nothing to build around (see <see cref="Setup"/>), or <see cref="OperationCanceledException"/> when
    /// <paramref name="cancel"/> is cancelled during the jump-link build (a graph build is short and runs to its end).
    /// </summary>
    public static NavGraph LoadOrBuild(MapContent map, CollisionWorld world, SimConfig config, Team side, int playerClass, string cacheDirectory, out string note, CancellationToken cancel = default)
    {
        var (settings, area) = Setup(map, config) ?? throw new InvalidOperationException("no spawns or points to build around");
        var mesh = world.CharacterFor(side);
        ulong key = NavGraph.KeyFor(mesh, world.KillVolumes, settings, area);
        var graph = NavCache.LoadOrBuild(cacheDirectory, map.Name, key, settings, () => NavGraph.Build(mesh, world.KillVolumes, settings, area), out note);
        if (Loadouts.JumpPack(config, playerClass) is not null)
        {
            var options = new NavJumpOptions();
            ulong jumpKey = NavJumpSet.KeyFor(graph, config, playerClass, options);
            var set = NavCache.LoadOrBuildJumps(cacheDirectory, map.Name, jumpKey, graph.NodeCount,
                () => NavJumpSet.Build(graph, world, side, config, playerClass, options, cancel), out string jumpNote);
            graph = graph.WithJumps(set);
            note += "; " + jumpNote;
        }
        return graph;
    }
}
