using SmContent;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The war map on the server (TF1): the enemies' walking graph over the arena alone (<see cref="TfwMap.WalkArea"/>), read from or written to the Musou graph cache as
/// <c>tfw_war-musou-&lt;key&gt;.nav</c>, and the wire's box, wider than that graph so it also holds the Stormbird.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The arena's walking graph through the cache; <paramref name="cacheDirectory"/>: the cache folder (null: the private cache; tests pass their own).</summary>
    internal static NavGraph BuildTfwNav(GameMap map, SimConfig config, out string note, string? cacheDirectory = null)
    {
        var settings = NavSettings.From(config);
        var area = TfwMap.WalkArea();
        var mesh = map.Collision.Character;
        ulong key = NavGraph.KeyFor(mesh, map.Collision.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(cacheDirectory ?? BotNavigation.CacheDirectory, map.Name + MusouNavCacheSuffix, key, settings,
            () => NavGraph.Build(mesh, map.Collision.KillVolumes, settings, area), out note);
    }

    /// <summary>The graph for <see cref="CreateTfw"/>; a failed build leaves the war without one (noted) and the server starts, as <see cref="CreateMusou"/> does.</summary>
    NavGraph? TfwNav(out string note)
    {
        try
        {
            var nav = BuildTfwNav(Map!, World.Config, out string built);
            note = $"{built}, {nav.NodeCount} nodes";
            return nav;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            note = $"nav graph failed ({e.GetType().Name}: {e.Message})";
            return null;
        }
    }

    /// <summary>The wire's box until the war's director sets one: the map's, not the graph's (<see cref="SendMusou"/> would read the arena alone).</summary>
    void TfwUseWireBox()
    {
        _musouNavBox = World.Musou?.Box;
        _musouNavBoxRead = true;
    }
}
