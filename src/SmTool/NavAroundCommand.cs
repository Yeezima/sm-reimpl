using System.Numerics;
using SmContent;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool nav-around &lt;game-dir|auto&gt; --map m --side marines|chaos --at x,y,z [--radius 5]</c> (bots/new-maps test aid): a map of the cells round a point on the bots' navigation graph, for the places
/// where bots get stuck. Per 0.5 m column: the graph cell's height above <c>--at</c>'s z in half-metres (a digit, or a letter for more; "-" for below) or "." for none, and under it the
/// real motor's verdict for a capsule put there at the point's height: "S" stands (no push, grounded), "x" pushed out or falling, so a "." over an "S" is floor the graph misses. Read-only against the install.
/// </summary>
static class NavAroundCommand
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        string Opt(string name, string fallback) { int i = Array.IndexOf(rest, name); return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : fallback; }
        string map = Opt("--map", "mp_tdm_09");
        var side = Opt("--side", "marines").StartsWith("c", StringComparison.OrdinalIgnoreCase) ? Team.Chaos : Team.SpaceMarines;
        var at = Opt("--at", "0,0,0").Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        float radius = float.Parse(Opt("--radius", "5"), System.Globalization.CultureInfo.InvariantCulture);
        var config = GameplayTuning.Load(gameDir).Config;
        var content = MapContent.Load(gameDir, map);
        var world = content.ToCollisionWorld();
        NavGraph graph;
        string note;
        if (rest.Contains("--old"))
        {
            // The graph as it was before PruneTraps (read from the cache the old bots used).
            var (settings, area) = BotNavigation.Setup(content, config)!.Value;
            settings = settings with { PruneTraps = false, StrictDrops = false };
            var mesh = world.CharacterFor(side);
            ulong key = NavGraph.KeyFor(mesh, world.KillVolumes, settings, area);
            graph = NavCache.LoadOrBuild(BotNavigation.CacheDirectory, content.Name, key, settings, () => NavGraph.Build(mesh, world.KillVolumes, settings, area), out note);
        }
        else graph = BotNavigation.LoadOrBuildWalk(content, world, config, side, BotNavigation.CacheDirectory, out note);
        Console.WriteLine($"{map} {side}: {note}");
        if (rest.Contains("--check"))
        {
            // Every spawn, point and start area of the map: on the graph, and a walk to it from the side's own start area.
            var own = content.StartAreas.FirstOrDefault(a => a.Side == side)?.Position;
            var targets = content.Spawns.Select((sp, i) => ($"spawn{i + 1}", sp.Position)).Concat(content.ControlPoints.Select(pt => ($"point {pt.Name}", pt.Position))).Concat(content.StartAreas.Select(a => ($"start {a.Side}", a.Position))).ToList();
            int onGraph = 0, reach = 0;
            foreach (var (name, pos) in targets)
            {
                bool on = graph.Nearest(pos, 2f, 3f) >= 0;
                bool walk = on && own is { } o && graph.FindPath(o, pos) is { Partial: false };
                if (on) onGraph++;
                if (walk) reach++;
                if (!walk) Console.WriteLine($"  {name} at {pos.X:F0},{pos.Y:F0},{pos.Z:F0}: {(on ? "on the graph, no walk from the own start" : "no cell")}");
            }
            Console.WriteLine($"{targets.Count} places: {onGraph} on the graph, {reach} walkable from the {side} start area");
            return 0;
        }
        if (rest.Contains("--walk"))
        {
            // The real motor walking forward (full stick) from --at facing --yaw degrees (0 = +Y, positive turns toward -X) for --ticks ticks: where the capsule gets to, every 5 ticks.
            float yaw = float.Parse(Opt("--yaw", "0"), System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180f;
            int ticks = int.Parse(Opt("--ticks", "60"));
            var walker = Combat.Respawned(new PlayerState(1, new Vector3(at[0], at[1], at[2] + 0.02f), Vector3.Zero, 0f, false) { Team = side }, config);
            for (int t = 0; t < ticks; t++)
            {
                walker = Combat.StepPlayer(walker, new PlayerInput((uint)t, 0f, 1f, yaw, InputButtons.None, 0f), config, out _, world);
                if (t % 5 == 4) Console.WriteLine($"  tick {t + 1}: {walker.Position.X:F2},{walker.Position.Y:F2},{walker.Position.Z:F2} grounded {walker.Grounded} speed {new Vector2(walker.Velocity.X, walker.Velocity.Y).Length():F1}");
            }
            return 0;
        }
        if (rest.Contains("--rays"))
        {
            // Horizontal rays (--dir x,y; default toward -x) from --at at heights 0 to 1 m above it, on the side's character collision: where each meets level (the curb's profile).
            var mesh = world.CharacterFor(side);
            var dd = Opt("--dir", "-1,0").Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var rayDir = Vector3.Normalize(new Vector3(dd[0], dd[1], 0f));
            for (float dz = 0f; dz <= 1.0f; dz += 0.1f)
            {
                var hit = mesh.Raycast(new Vector3(at[0], at[1], at[2] + dz), rayDir, 3f);
                Console.WriteLine(hit is { } h ? $"  +{dz:F1}: hit at {h.Point.X:F2},{h.Point.Y:F2}, normal {h.Normal.X:F2},{h.Normal.Y:F2},{h.Normal.Z:F2}" : $"  +{dz:F1}: clear for 3 m");
            }
            return 0;
        }
        if (rest.Contains("--pathto"))
        {
            // The planner's path from the side's start area (or --at) to --pathto x,y,z: partial or whole, its length, and the drop and jump links it uses.
            var goal = Opt("--pathto", "0,0,0").Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var from = rest.Contains("--at") ? new Vector3(at[0], at[1], at[2]) : content.StartAreas.First(a => a.Side == side).Position;
            var path = graph.FindPath(from, new Vector3(goal[0], goal[1], goal[2]));
            if (path is null) { Console.WriteLine("no path"); return 0; }
            Console.WriteLine($"path {(path.Partial ? "PARTIAL" : "whole")}, {path.Length:F0} m, {path.Waypoints.Count} waypoints; ends at {path.Waypoints[^1].Position.X:F1},{path.Waypoints[^1].Position.Y:F1},{path.Waypoints[^1].Position.Z:F1}");
            foreach (var w in path.Waypoints.Where(w => w.Kind != NavLinkKind.Walk)) Console.WriteLine($"  {w.Kind} to {w.Position.X:F1},{w.Position.Y:F1},{w.Position.Z:F1}");
            return 0;
        }
        if (rest.Contains("--cylinder"))
        {
            // The graph's cells inside a vertical cylinder round --at: radius --radius, half height --half (a Seize Ground point's capture zone), and the ones nearest its centre.
            float half = float.Parse(Opt("--half", "7.5"), System.Globalization.CultureInfo.InvariantCulture);
            var c0 = new Vector3(at[0], at[1], at[2]);
            var inside = Enumerable.Range(0, graph.NodeCount).Where(n => { var p = graph.Position(n); return new Vector2(p.X - c0.X, p.Y - c0.Y).Length() <= radius && MathF.Abs(p.Z - c0.Z) <= half; }).ToList();
            Console.WriteLine($"{inside.Count} cells in the cylinder (r {radius}, half height {half}); with a link: {inside.Count(n => graph.Links(n).Any())}");
            foreach (var n in inside.OrderBy(n => Vector3.Distance(graph.Position(n), c0)).Take(5)) Console.WriteLine($"  nearest: {graph.Position(n).X:F1},{graph.Position(n).Y:F1},{graph.Position(n).Z:F1}");
            var below = Enumerable.Range(0, graph.NodeCount).Where(n => { var p = graph.Position(n); return new Vector2(p.X - c0.X, p.Y - c0.Y).Length() <= radius; }).Select(n => MathF.Round(graph.Position(n).Z)).GroupBy(z => z).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}");
            Console.WriteLine("  floors under the whole disc (height:cells): " + string.Join(" ", below));
            return 0;
        }
        if (rest.Contains("--zrow"))
        {
            // The graph cells' exact heights along x at --at's y (every 0.5 m, --radius each way), and the walk links from each to its +x neighbour.
            for (float x = MathF.Round(at[0] * 2f) / 2f - radius; x <= at[0] + radius + 0.01f; x += 0.5f)
            {
                int n = graph.Nearest(new Vector3(x, at[1], at[2]), 0.01f, 1.5f);
                Console.WriteLine(n < 0 ? $"  x {x:F1}: no cell" : $"  x {x:F1}: z {graph.Position(n).Z:F2}  links " + string.Join(" ", graph.Links(n).Select(l => $"{l.Kind}->({graph.Position(l.To).X:F1},{graph.Position(l.To).Y:F1},{graph.Position(l.To).Z:F2})")));
            }
            return 0;
        }
        var centre = new Vector3(at[0], at[1], at[2]);
        int steps = (int)(radius / 0.5f);
        var graphRows = new List<string>(); var motorRows = new List<string>();
        for (int j = steps; j >= -steps; j--)
        {
            var g = new System.Text.StringBuilder(); var m = new System.Text.StringBuilder();
            for (int i = -steps; i <= steps; i++)
            {
                float x = MathF.Round(at[0] * 2f) / 2f + i * 0.5f, y = MathF.Round(at[1] * 2f) / 2f + j * 0.5f;
                int node = graph.Nearest(new Vector3(x, y, at[2]), 0.01f, 0.7f);
                if (node < 0) g.Append('.');
                else
                {
                    int h = (int)MathF.Round((graph.Position(node).Z - at[2]) * 2f);
                    g.Append(h < 0 ? '-' : h < 10 ? (char)('0' + h) : (char)('a' + Math.Min(h - 10, 25)));
                }
                var s = Combat.Respawned(new PlayerState(1, new Vector3(x, y, at[2] + 0.02f), Vector3.Zero, 0f, false) { Team = side }, config);
                var after = s;
                for (int t = 0; t < 12; t++) after = Combat.StepPlayer(after, new PlayerInput((uint)t, 0f, 0f, 0f, InputButtons.None, 0f), config, out _, world);
                var moved = new Vector2(after.Position.X - x, after.Position.Y - y).Length();
                m.Append(moved < 0.03f && MathF.Abs(after.Position.Z - at[2]) < 0.2f ? 'S' : 'x');
            }
            graphRows.Add(g.ToString()); motorRows.Add(m.ToString());
        }
        Console.WriteLine("graph cells (half-metres above the point; . none)          motor (S stands)");
        for (int r = 0; r < graphRows.Count; r++) Console.WriteLine($"{graphRows[r]}   {motorRows[r]}   y {MathF.Round(at[1] * 2f) / 2f + (steps - r) * 0.5f:F1}");
        Console.WriteLine($"x from {MathF.Round(at[0] * 2f) / 2f - steps * 0.5f:F1} step 0.5; the point is the middle column");
        return 0;
    }
}
