using System.Globalization;
using System.Numerics;
using System.Text;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;

namespace SmTool;

/// <summary>
/// <c>smtool musou-army-routes [--maps tfw_war,tfw_war_field,tfw_war_draft10] [--table file.md]</c> (test aid; owner 2026-11-09, "Yes, route on avenues"): the routes the war's armies take between their places
/// (the landing zone, the Ork HQ and the points), searched on the map's own walking graph as the battle searches them: before (one body's shortest walk, <c>NavGraph.FindPath</c>) and after (the formation's width,
/// <c>NavGraph.FindPathWide</c> with <see cref="TfwArmyRules.RouteHalfWidth"/>). For each: the length, the width at its narrowest point on the final collision (the clear width across the route every metre,
/// <c>AshfallGrid.WidthAcross</c>; inside the compound and the stronghold left out) and where, and the march time at the army's pace (Guard from the landing zone, Orks from the HQ). Nothing is read from the install.
/// </summary>
static class MusouArmyRoutesCommand
{
    public static int Run(string[] rest)
    {
        string[] maps = [TfwMap.Name, TfwMap.FieldName, TfwMap.Draft10Name];
        string? table = null;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--maps" && i + 1 < rest.Length) maps = rest[++i].Split(',');
            else if (rest[i] == "--table" && i + 1 < rest.Length) table = Path.GetFullPath(rest[++i]);
            else { Console.Error.WriteLine($"musou-army-routes: unknown argument \"{rest[i]}\" (usage: musou-army-routes [--maps a,b] [--table file.md])"); return 2; }
        }
        var sb = new StringBuilder();
        sb.AppendLine("| Map | Route | Before: length (m) | Before: narrowest (m) | Before: march (s) | After: length (m) | After: narrowest (m) | After: march (s) |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        Console.WriteLine(sb.ToString().TrimEnd());
        foreach (var map in maps)
        {
            var data = TfwWorld.Load("", out string? problem, map) ?? throw new InvalidOperationException(problem);
            var nav = TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out string note);
            var grid = new AshfallGrid(AshfallMap.Build(TfwMap.LayoutOf(map)));
            var f = data.Field;
            var places = new List<(string Name, Vector3 At)> { ("landing zone", f.MarineStart), ("Ork HQ", f.Commander) };
            for (int k = 0; k < f.Points.Count; k++) places.Add(($"{(char)('A' + k)}", f.Points[k].Centre));
            (string, string)[] pairs = [("landing zone", "A"), ("landing zone", "B"), ("landing zone", "C"), ("Ork HQ", "A"), ("Ork HQ", "B"), ("Ork HQ", "C"), ("A", "B"), ("B", "C"), ("A", "C")];
            Console.Error.WriteLine($"musou-army-routes: {map}: {nav.NodeCount} nodes ({note})");
            foreach (var (a, b) in pairs)
            {
                var pa = places.First(p => p.Name == a).At; var pb = places.First(p => p.Name == b).At;
                float speed = a == "Ork HQ" ? TfwArmyRules.OrkSpeed : TfwArmyRules.GuardSpeed;
                var before = nav.FindPath(pa, pb, 10f, TfwPatrolRules.RouteExpansions);
                var after = nav.FindPathWide(pa, pb, TfwArmyRules.RouteHalfWidth, TfwArmyRules.RoutePenalty, 10f, TfwArmyRules.WideRouteExpansions);
                string Cells(NavPath? p)
                {
                    if (p is null || p.Partial) return "none | - | -";
                    var (w, at) = Narrowest(grid, p);
                    return $"{p.Length:0} | {w:0.0} at ({at.X:0}, {at.Y:0}) | {p.Length / speed:0}";
                }
                string row = $"| {map} | {a} to {b} | {Cells(before)} | {Cells(after)} |";
                Console.WriteLine(row);
                sb.AppendLine(row);
            }
        }
        if (table is not null) { Directory.CreateDirectory(Path.GetDirectoryName(table)!); File.WriteAllText(table, sb.ToString(), new UTF8Encoding(false)); Console.WriteLine($"musou-army-routes: table -> {table}"); }
        return 0;
    }

    /// <summary>The narrowest clear width across a route (every metre along its waypoints), inside the bases left out.</summary>
    static (float Width, Vector2 At) Narrowest(AshfallGrid grid, NavPath path)
    {
        float min = float.MaxValue; var at = Vector2.Zero;
        for (int i = 1; i < path.Waypoints.Count; i++)
        {
            var a = new Vector2(path.Waypoints[i - 1].Position.X, path.Waypoints[i - 1].Position.Y);
            var b = new Vector2(path.Waypoints[i].Position.X, path.Waypoints[i].Position.Y);
            float len = Vector2.Distance(a, b);
            if (len < 1e-3f) continue;
            var dir = (b - a) / len;
            for (float t = 0f; t <= len; t += 1f)
            {
                var q = a + dir * t;
                if ((q.X > -38f && q.X < 38f && q.Y > -218f && q.Y < -157f) || (q.X > -72f && q.X < 72f && q.Y > 108f && q.Y < 220f)) continue;   // inside the compound or the stronghold (their gates are authored)
                float w = grid.WidthAcross(q, dir);
                if (w < min) { min = w; at = q; }
            }
        }
        return (min == float.MaxValue ? 0f : min, at);
    }
}
