using SmContent.Extras.Musou;
using SmNet;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The war map on the server (TF1, M1): the enemies' walking graph over the battlefield alone (<see cref="TfwBattlefield.WalkArea"/>, seeded by the map's own markers: <see cref="GameMap.Spawns"/>
/// are the Stormbird's), read from or written to the Musou graph cache as <c>tfw_war-musou-&lt;key&gt;.nav</c>, and the wire's box, wider than that graph so it also holds the Stormbird.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The battlefield's walking graph through the cache; <paramref name="cacheDirectory"/>: the cache folder (null: the private cache; tests pass their own).</summary>
    internal static NavGraph BuildTfwNav(GameMap map, TfwBattlefield field, SimConfig config, out string note, string? cacheDirectory = null) =>
        TfwWorld.Nav(map.Collision, field, config, out note, cacheDirectory);

    /// <summary>The graph for <see cref="CreateTfw"/>; a failed build leaves the war without one (noted) and the server starts, as <see cref="CreateMusou"/> does.</summary>
    NavGraph? TfwNav(TfwBattlefield field, out string note)
    {
        try
        {
            var nav = BuildTfwNav(Map!, field, World.Config, out string built);
            note = $"{built}, {nav.NodeCount} nodes";
            return nav;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            note = $"nav graph failed ({e.GetType().Name}: {e.Message})";
            return null;
        }
    }

    /// <summary>The wire's box until the war's director sets one: the map's, not the graph's (<see cref="SendMusou"/> would read the battlefield alone).</summary>
    void TfwUseWireBox()
    {
        _musouNavBox = World.Musou?.Box;
        _musouNavBoxRead = true;
    }
}
