using System.Numerics;
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
                // W13b: the HQ level composed with the hub exactly as a lane slot is (so the run, the classes, the squads and the ship all work on it); the hash is the composed world's
                var hq = WarfrontHqLayout.Build;
                var data = TfwMap.Compose(AshfallContent.ToMapContent(hq.Level).ToCollisionWorld(), hq.Level.Points, hq.Level.Areas, hq.Level.Spawns, manufactorum: false);
                return new WarfrontLoadedMap(name, data.Collision, data.Spawns, data.StartAreas, data.Hash, data, data.Field.WalkArea());
            }
            if (name == FortressName)
            {
                // W14b: the arena of WarfrontSlots.Fortress composed with the hub exactly as the HQ is (the one line a handmade map changes is that property). The three waypoints stand in as the
                // composed field's three points (never an objective: the battle reads its own layout), the landing zone and the boss spot as the two start areas.
                var arena = WarfrontSlots.Fortress;
                var plan = WarfrontArenaLayout.Plan(arena);
                var collision = arena is WarfrontFortressBuild generated ? AshfallContent.ToMapContent(generated.Level).ToCollisionWorld() : arena.Collision;   // the generated level is drawn by the client from its Level: same collision on both sides
                ControlPoint[] points = [new("Breach", plan.Breach, 10f, 12f), new("Gate", plan.Gate, 12f, 12f), new("Plaza gate", plan.PlazaGate, 14f, 12f)];
                StartArea[] areas = [new(Team.SpaceMarines, arena.LandingZone, new Vector3(60f, 24f, 8f), 0f), new(Team.Chaos, arena.BossSpot, new Vector3(80f, 80f, 8f), 0f)];
                Vector3[] spawns = [arena.LandingZone, arena.BossSpot, .. arena.Sources.Select(x => x.Position), .. arena.Posts.Select(x => x.Position)];
                var data = TfwMap.Compose(collision, points, areas, spawns, manufactorum: false);
                return new WarfrontLoadedMap(name, data.Collision, data.Spawns, data.StartAreas, data.Hash, data, data.Field.WalkArea());
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
        if (map.Name is HqName or FortressName && map.Tfw is { } composed) return NavOf(map.Name, composed, config, out note, cacheDirectory);
        var settings = NavSettings.From(config);
        var area = map.WalkArea ?? throw new InvalidOperationException(map.Name + " has no walk area");
        var mesh = map.Collision.Character;
        ulong key = NavGraph.KeyFor(mesh, map.Collision.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(cacheDirectory ?? BotNavigation.CacheDirectory, map.Name + TfwWorld.CacheSuffix, key, settings, () => NavGraph.Build(mesh, map.Collision.KillVolumes, settings, area), out note);
    }

    /// <summary>The walking graph of a composed non-slot map (the HQ) over its battlefield's walk area, through the cache as <c>&lt;name&gt;-musou-&lt;key&gt;.nav</c>.</summary>
    public static NavGraph NavOf(string name, TfwMapData data, SimConfig config, out string note, string? cacheDirectory = null)
    {
        var settings = NavSettings.From(config);
        var area = data.Field.WalkArea();
        var mesh = data.Collision.Character;
        ulong key = NavGraph.KeyFor(mesh, data.Collision.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(cacheDirectory ?? BotNavigation.CacheDirectory, name + TfwWorld.CacheSuffix, key, settings, () => NavGraph.Build(mesh, data.Collision.KillVolumes, settings, area), out note);
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
