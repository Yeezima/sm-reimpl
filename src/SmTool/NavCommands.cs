using SmContent;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool nav-stats &lt;game-dir&gt; [--map mp_tdm_09] [--class assault|raptor|...] [--cache &lt;dir&gt;]</c>: builds (or reads) the test bots'
/// navigation graph for each side of a map and, for jump-pack classes, the jump links, and prints their sizes (test aid;
/// uses the same cache as the client). Read-only against the install.
/// </summary>
static class NavCommands
{
    public static int Stats(string gameDir, string map, string[] classes, string? cache, int top = 0)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        var tuning = GameplayTuning.Load(gameDir);
        var config = tuning.Config;
        var content = MapContent.Load(gameDir, map);
        var world = content.ToCollisionWorld();
        string dir = cache ?? BotNavigation.CacheDirectory;
        Console.WriteLine($"map {map}: {content.Spawns.Count} spawns, {content.ControlPoints.Count} points, {content.StartAreas.Count} start areas, {world.Blockers.Count} blockers, {world.KillVolumes.Count} kill volumes; cache {dir}");
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos })
        {
            var names = classes.Length > 0 ? classes : ["none"];
            foreach (string name in names)
            {
                int cls = -1;
                if (name != "none")
                {
                    for (int i = 0; i < config.Classes.Count; i++)
                        if (config.Classes[i].Name.Contains(name, StringComparison.OrdinalIgnoreCase) && config.Classes[i].Side == side) { cls = i; break; }
                    if (cls < 0) { Console.WriteLine($"{side}: no class matching '{name}'"); continue; }
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var graph = BotNavigation.LoadOrBuild(content, world, config, side, cls, dir, out string note);
                if (graph.Jumps is { } jumps)
                {
                    var rows = new List<(float Flat, float Dz, int Hold, int Dir)>();
                    for (int n = 0; n < graph.NodeCount; n++)
                        foreach (var (to, _, move) in jumps.From(n))
                        {
                            var a = graph.Position(n); var b = graph.Position(to);
                            rows.Add((new System.Numerics.Vector2(b.X - a.X, b.Y - a.Y).Length(), b.Z - a.Z, move.Hold, move.Direction));
                        }
                    string Bucket(Func<(float Flat, float Dz, int Hold, int Dir), float> f, float[] edges) =>
                        string.Join(" ", edges.Zip(edges.Skip(1).Append(float.MaxValue), (lo, hi) => $"[{lo}..{hi}):{rows.Count(r => f(r) >= lo && f(r) < hi)}"));
                    Console.WriteLine($"  flights: flat {Bucket(r => r.Flat, [3, 6, 10, 15, 20, 30])}; dz {Bucket(r => r.Dz, [-100, -8, -3, -0.5f, 0.5f, 3, 8, 14])}; hold {Bucket(r => r.Hold, [1, 4, 5, 7, 12])}");
                }
                if (top > 0 && graph.Jumps is { } topJumps)
                {
                    var plain = BotNavigation.LoadOrBuild(content, world, config, side, -1, dir, out _);
                    var rows = new List<(float Saving, string Text)>();
                    for (int n = 0; n < graph.NodeCount; n += 3)
                        foreach (var (to, _, move) in topJumps.From(n))
                        {
                            var a = graph.Position(n); var b = graph.Position(to);
                            float flat = new System.Numerics.Vector2(b.X - a.X, b.Y - a.Y).Length();
                            var walk = plain.FindPath(a, b, 1f, 100_000);
                            float walked = walk is null || walk.Partial ? 9999f : walk.Length;
                            rows.Add((walked - flat, $"{a.X:F0},{a.Y:F0},{a.Z:F1} -> {b.X:F0},{b.Y:F0},{b.Z:F1} flat {flat:F0} dz {b.Z - a.Z:F1} hold {move.Hold} walk {(walked >= 9999f ? "none" : walked.ToString("F0"))}"));
                        }
                    foreach (var r in rows.OrderByDescending(r => r.Saving).Take(top)) Console.WriteLine("  " + r.Text);
                }
                Console.WriteLine($"{side} {(cls >= 0 ? config.Classes[cls].Name : "(no jump pack)")}: {graph.NodeCount:N0} cells, {graph.LinkCount:N0} links ({graph.DropLinkCount:N0} drops), jump links {graph.Jumps?.LinkCount ?? 0:N0}; {watch.ElapsedMilliseconds} ms; {note}");
            }
        }
        if (world.Blockers.Count > 0) BlockerExposure(content, world, config, dir);
        return 0;
    }

    /// <summary>
    /// How much of a graph lies inside the blockers that stop a side: the one graph every bot used before (the neutral character
    /// mesh) against each side's own, and the route from each side's start area to the enemy's start area on both (waypoint
    /// segments sampled every half metre; a sample inside a blocker that stops the side is a wall the bot would walk into).
    /// </summary>
    static void BlockerExposure(MapContent content, CollisionWorld world, SimConfig config, string dir)
    {
        var neutral = BotNavigation.LoadOrBuild(content, world, config, Team.None, -1, dir, out _);
        Console.WriteLine($"blocker exposure (cells of a graph inside a blocker that stops the side; routes start area -> enemy start area):");
        foreach (var side in new[] { Team.SpaceMarines, Team.Chaos })
        {
            var own = BotNavigation.LoadOrBuild(content, world, config, side, -1, dir, out _);
            int InBlockers(NavGraph g)
            {
                int n = 0;
                for (int i = 0; i < g.NodeCount; i++)
                {
                    var p = g.Position(i) + new System.Numerics.Vector3(0, 0, 0.9f);
                    if (world.Blockers.Any(b => b.Blocks(side) && b.Contains(p))) n++;
                }
                return n;
            }
            var mine = content.StartAreas.FirstOrDefault(a => a.Side == side);
            var theirs = content.StartAreas.FirstOrDefault(a => a.Side != side && a.Side != Team.None);
            string Route(NavGraph g)
            {
                if (mine is null || theirs is null) return "no start areas";
                var path = g.FindPath(mine.Position, theirs.Position);
                if (path is null) return "no path";
                int inside = 0, samples = 0;
                for (int k = 1; k < path.Waypoints.Count; k++)
                {
                    var a = path.Waypoints[k - 1].Position; var b = path.Waypoints[k].Position;
                    int steps = Math.Max(1, (int)(System.Numerics.Vector3.Distance(a, b) / 0.5f));
                    for (int i = 0; i <= steps; i++)
                    {
                        var p = System.Numerics.Vector3.Lerp(a, b, (float)i / steps) + new System.Numerics.Vector3(0, 0, 0.9f);
                        samples++;
                        if (world.Blockers.Any(bl => bl.Blocks(side) && bl.Contains(p))) inside++;
                    }
                }
                return $"{path.Length:F0} m{(path.Partial ? " (partial)" : "")}, {inside} of {samples} samples inside a blocker that stops {side}";
            }
            Console.WriteLine($"  {side}: neutral graph {InBlockers(neutral)} cells inside, own graph {InBlockers(own)}; route on neutral graph: {Route(neutral)}; on own graph: {Route(own)}");
        }
    }
}
