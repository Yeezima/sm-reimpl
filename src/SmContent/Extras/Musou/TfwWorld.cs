using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmContent.Extras.Musou;

/// <summary>
/// Musou Mode's world from the player's install (design section A): Manufactorum's collision, kill regions, props, Seize Ground points and start areas (original asset loaded), composed with the
/// generated Stormbird by <see cref="TfwMap.Compose"/>. Nothing is written and nothing leaves the install's folder.
/// </summary>
public static class TfwWorld
{
    /// <summary>What a player without the map is told: the mode cannot run without it, there is no stand-in.</summary>
    public static string Missing(string detail) =>
        $"Musou Mode's battlefield, Manufactorum ({TfwMap.Battlefield}), could not be read from the game install: {detail}. Check the game's files in Steam, or point --game-dir at the install.";

    /// <summary>
    /// The enemies' walking graph over the battlefield of <paramref name="collision"/> (<see cref="TfwBattlefield.WalkArea"/>), through the Musou graph cache as <c>tfw_war-musou-&lt;key&gt;.nav</c>;
    /// <paramref name="cacheDirectory"/>: the cache folder (null: the private cache). The server's war walks it, and a test aid of the client finds its way on it.
    /// </summary>
    public static NavGraph Nav(CollisionWorld collision, TfwBattlefield field, SimConfig config, out string note, string? cacheDirectory = null)
    {
        var settings = NavSettings.From(config);
        var area = field.WalkArea();
        var mesh = collision.Character;
        ulong key = NavGraph.KeyFor(mesh, collision.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(cacheDirectory ?? BotNavigation.CacheDirectory, TfwMap.Name + CacheSuffix, key, settings, () => NavGraph.Build(mesh, collision.KillVolumes, settings, area), out note);
    }

    /// <summary>The suffix of a Musou graph's cache name.</summary>
    public const string CacheSuffix = "-musou";

    /// <summary>The world of the install at <paramref name="gameDir"/>; null with <paramref name="problem"/> set for an install without a usable Manufactorum.</summary>
    public static TfwMapData? Load(string gameDir, out string? problem)
    {
        problem = null;
        try
        {
            if (!MapContent.Available(gameDir).Contains(TfwMap.Battlefield)) { problem = Missing("the map is not in this install"); return null; }
            var content = MapContent.Load(gameDir, TfwMap.Battlefield);
            return TfwMap.Compose(content.ToCollisionWorld(), content.SimControlPoints(), content.SimStartAreas(), content.Spawns.Select(s => s.Position).ToArray());
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = Missing(e.Message);
            return null;
        }
    }
}
