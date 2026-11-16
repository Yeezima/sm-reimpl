using System.Diagnostics;
using System.Globalization;
using SmSim;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Fortress;
using SmSim.Extras.Warfront.Hq;
using SmSim.Extras.Warfront.Maps;
using SmContent.Extras.Warfront.Maps;

namespace SmContent.Extras.Warfront;

/// <summary>One of Warfront's 11 maps loaded: collision, spawns, hash and, for a lane slot, the composed hub world (<see cref="Tfw"/>).</summary>
public sealed record WarfrontLoadedMap(string Name, CollisionWorld Collision, IReadOnlyList<SpawnPoint> Spawns, IReadOnlyList<StartArea> StartAreas, ulong Hash, TfwMapData? Tfw, NavArea? WalkArea);

/// <summary>
/// The name-to-world resolver of all 11 Warfront maps (slice W5a; ours): the nine lane slots through <see cref="WarfrontWorld"/>, <c>wf_hq</c> through <see cref="WarfrontHqMap"/>, <c>wf_fortress</c> through
/// <see cref="WarfrontFortressMap"/> (the <see cref="IWarfrontArena"/> a handmade map may later replace). Each map has its own walking-graph cache file (<c>&lt;name&gt;-musou-&lt;key&gt;.nav</c>); the lane slots also have their own layout
/// cache file (<see cref="WarfrontLayoutCache"/>), the HQ and fortress levels are built in code deterministically in well under a second so they need none. Nothing is read from the install.
/// </summary>
public static class WarfrontMaps
{
    public const string HqName = "wf_hq", FortressName = "wf_fortress";

    /// <summary>The 11 names in the plan's order.</summary>
    public static IReadOnlyList<string> AllNames { get; } = [.. WarfrontSlots.All.Select(s => s.Name), HqName, FortressName];

    public static bool IsWarfrontMap(string? name) => name is not null && AllNames.Contains(name);

    /// <summary>The map of a Warfront name; null with <paramref name="problem"/> for anything else or a failed build (never throws for those).</summary>
    public static WarfrontLoadedMap? Load(string name, out string? problem)
    {
        problem = null;
        try
        {
            if (WarfrontSlots.ByName(name) is { } slot)
            {
                var data = WarfrontWorld.Load(slot, out _);
                return new WarfrontLoadedMap(name, data.Collision, data.Spawns, data.StartAreas, data.Hash, data, data.Field.WalkArea());
            }
            if (name == HqName)
            {
                var hq = WarfrontHqMap.Build();
                var collision = AshfallContent.ToMapContent(hq.Level).ToCollisionWorld();
                return new WarfrontLoadedMap(name, collision, [new SpawnPoint(hq.LandingZone, 0f)], hq.Level.Areas, hq.Level.Hash, null, hq.WalkArea);
            }
            if (name == FortressName)
            {
                var f = WarfrontFortressMap.Build();
                return new WarfrontLoadedMap(name, f.Collision, [new SpawnPoint(f.LandingZone, 0f)], f.Level.Areas, f.Hash, null, f.WalkArea);
            }
            problem = $"{name} is not a Warfront map";
            return null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or AshfallGateException)
        {
            problem = $"the Warfront map {name} could not be built: {e.Message}";
            return null;
        }
    }

    /// <summary>The map's walking graph through the cache under its own name; <paramref name="cacheDirectory"/> null: the private cache.</summary>
    public static NavGraph Nav(WarfrontLoadedMap map, SimConfig config, out string note, string? cacheDirectory = null)
    {
        if (WarfrontSlots.ByName(map.Name) is { } slot && map.Tfw is { } data) return WarfrontWorld.Nav(slot, data, config, out note, cacheDirectory);
        var settings = NavSettings.From(config);
        var area = map.WalkArea ?? throw new InvalidOperationException(map.Name + " has no walk area");
        var mesh = map.Collision.Character;
        ulong key = NavGraph.KeyFor(mesh, map.Collision.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(cacheDirectory ?? BotNavigation.CacheDirectory, map.Name + TfwWorld.CacheSuffix, key, settings, () => NavGraph.Build(mesh, map.Collision.KillVolumes, settings, area), out note);
    }

    /// <summary>
    /// Builds the layout and walking-graph caches of <paramref name="name"/> in this process (the travel wait's work; call it off the server's tick thread) and releases the build afterwards. Returns a one-line note with the timings.
    /// A warm run reads both files back. <paramref name="progress"/> gets 0..100.
    /// </summary>
    public static string Prebuild(string name, SimConfig config, Action<int, string>? progress = null)
    {
        var total = Stopwatch.StartNew();
        progress?.Invoke(0, "building the map");
        var map = Load(name, out string? problem) ?? throw new InvalidOperationException(problem ?? "map not built");
        long layoutMs = total.ElapsedMilliseconds;
        progress?.Invoke(30, "building the walking graph");
        var nav = Nav(map, config, out string navNote);
        if (WarfrontSlots.ByName(name) is { } slot) { WarfrontSlots.Release(slot); WarfrontLayoutCache.Forget(slot); }
        progress?.Invoke(100, "ready");
        return string.Create(CultureInfo.InvariantCulture, $"{name}: map {layoutMs / 1000.0:0.0} s, graph {nav.NodeCount:N0} nodes ({(navNote.Length > 80 ? navNote[..80] : navNote)}), {total.Elapsed.TotalSeconds:0.0} s in all");
    }
}
