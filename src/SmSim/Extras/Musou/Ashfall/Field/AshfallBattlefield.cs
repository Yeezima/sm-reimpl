using System.Globalization;
using System.Numerics;
using System.Text;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou.Ashfall;

/// <summary>
/// The battlefield checks of docs/ashfall-layout-brief.md, measured on the final collision of any layout (draft 11's report and the comparison against draft 10): army route widths at their narrowest point, pinch points,
/// formation-capable approaches per point, travel times along real walks, sightlines and elevated views, protected places, and every constraint with its threshold, passed or failed. Ours (placeholder demonstration).
/// </summary>
public sealed class AshfallBattlefield
{
    /// <summary>The game metrics the measures use (spec "Ashfall battlefield layout (draft 11)"): a Space Marine's capsule (0.45 m) and run (5.0 m/s), the army layer's march speeds (<c>TfwArmyRules</c>).</summary>
    public const float PlayerRadius = 0.45f, PlayerSpeed = 5.0f, OrkArmySpeed = TfwArmyRules.OrkSpeed, GuardArmySpeed = TfwArmyRules.GuardSpeed;
    /// <summary>Eye height for sightlines (m over the ground; a Marine's eye, ours).</summary>
    public const float Eye = 1.6f;

    public sealed record Place(string Name, Vector2 At, bool Base);
    public sealed record Link(string From, string To, float Width, Vector2 Pinch, float? ArmyLength, float? WalkLength)
    {
        public float? GuardSeconds => ArmyLength / GuardArmySpeed;
        public float? OrkSeconds => ArmyLength / OrkArmySpeed;
        public float? PlayerSeconds => WalkLength / PlayerSpeed;
    }
    public sealed record Approach(string Point, int Count, float[] Bearings, int CapableSamples);
    public sealed record Constraint(string Name, bool Passed, string Value, string Threshold);
    public sealed record RouteWidth(string Route, float MinWidth, Vector2 Pinch, List<(Vector2 At, float Width)> Pinches);
    public sealed record Elevated(string Name, Vector3 Eye, int PointsSeen, string Seen);

    public AshfallLayout Layout { get; }
    public AshfallLevel Level { get; }
    public AshfallDressing Dressing { get; }
    public AshfallGrid Grid { get; }
    public float MinArmyWidth { get; }
    public IReadOnlyList<Place> Places { get; }
    public List<Link> Links { get; } = [];
    public List<Approach> Approaches { get; } = [];
    public List<RouteWidth> RouteWidths { get; } = [];
    public List<(string From, string To, bool Sees)> PointSight { get; } = [];
    public List<(string Point, string Approach, float Share)> Scouting { get; } = [];
    public List<(string Landmark, Vector3 Top, string SeenFrom, int Count)> Landmarks { get; } = [];
    public List<Elevated> Heights { get; } = [];
    /// <summary>Platforms of the stronghold (its terraces; authored, outside the rule).</summary>
    public int StrongholdHeights { get; private set; }
    public List<(string Area, int Blocked, int Cells)> ProtectedBlocked { get; } = [];
    public List<(string District, float Cover)> Density { get; } = [];
    public List<Constraint> Constraints { get; } = [];
    public AshfallFieldPlan? Plan { get; }

    /// <summary>Measures a layout (the game's default parameters for the field).</summary>
    public static AshfallBattlefield Measure(AshfallLayout layout) { var d = AshfallMap.BuildDraft5(layout); return new AshfallBattlefield(layout, d.Level, d.Dressing, layout == AshfallLayout.Field ? AshfallField.Default : null); }

    public AshfallBattlefield(AshfallLayout layout, AshfallLevel level, AshfallDressing dressing, AshfallFieldPlan? plan)
    {
        Layout = layout; Level = level; Dressing = dressing; Plan = plan;
        MinArmyWidth = plan?.Params.MinArmyWidth ?? AshfallFieldParams.Default.MinArmyWidth;
        Grid = new AshfallGrid(level);
        var pts = level.Points.Select(p => new Place(p.Name, new Vector2(p.Center.X, p.Center.Y), false)).ToList();
        // The bases' army nodes: just outside their gates (the compound's 16 m gate and the stronghold's gatehouse are authored exceptions); the field's assembly areas.
        Vector2 baseNode = plan is null ? new(0f, -150f) : Mid(plan.Assembly[0].Rect), shNode = plan is null ? new(0f, 98f) : Mid(plan.Assembly[1].Rect);
        Places = [new("base", baseNode, true), new("stronghold", shNode, true), .. pts];
        Run();
    }

    static Vector2 Mid(Vector4 r) => new((r.X + r.Z) / 2f, (r.Y + r.W) / 2f);

    Place P(string n) => Places.First(p => p.Name == n);

    void Run()
    {
        float half = MinArmyWidth / 2f;
        // 1. Links between the bases and every point, and every pair of points.
        (string, string)[] pairs = [("base", "A"), ("base", "B"), ("base", "C"), ("stronghold", "A"), ("stronghold", "B"), ("stronghold", "C"), ("A", "B"), ("B", "C"), ("A", "C")];
        foreach (var (a, b) in pairs)
        {
            var (w, pinch, _) = Grid.Widest(P(a).At, P(b).At);
            var army = Grid.Shortest(P(a).At, P(b).At, half);
            var walk = Grid.Shortest(P(a).At, P(b).At, PlayerRadius);
            Links.Add(new(a, b, w, pinch, army?.Length, walk?.Length));
        }
        // 2. Approaches: from a ring just outside each point's open zone, the directions an army can come in from a base without crossing the zone.
        float clearR = Plan?.Params.ObjectiveClear ?? 16f;
        foreach (var p in Level.Points)
        {
            var c = new Vector2(p.Center.X, p.Center.Y);
            float zone = p.Radius + clearR, ring = zone + 6f;
            bool InZone(int k) => Vector2.DistanceSquared(Grid.At(k), c) < zone * zone;
            var comp = Components(half, InZone);
            var reach = new HashSet<int>();
            foreach (var b in Places.Where(q => q.Base)) { int s = Grid.Snap(b.At, half); if (s >= 0 && comp[s] > 0) reach.Add(comp[s]); }
            var capable = new List<float>();
            for (int deg = 0; deg < 360; deg += 3)
            {
                float rad = deg * MathF.PI / 180f;
                var q = c + new Vector2(MathF.Cos(rad), MathF.Sin(rad)) * ring;
                if (MathF.Abs(q.X) >= AshfallMap.Half - 1f || MathF.Abs(q.Y) >= AshfallMap.Half - 1f) continue;
                int k = Grid.Index(q);
                if (Grid.Clearance[k] >= half && reach.Contains(comp[k])) capable.Add(deg);
            }
            float sep = Plan?.Params.ApproachSeparation ?? 60f;
            var picked = new List<float>();
            // greedy on the circle from each start, the largest set kept
            foreach (float start in capable)
            {
                var set = new List<float> { start };
                foreach (float d in capable.Select(x => (x - start + 360f) % 360f).OrderBy(x => x).Select(x => (x + start) % 360f))
                    if (set.All(s => Sep(s, d) >= sep)) set.Add(d);
                if (set.Count > picked.Count) picked = set;
            }
            Approaches.Add(new(p.Name, picked.Count, [.. picked.OrderBy(x => x)], capable.Count));
        }
        // 3. Route widths along the plan's routes (perpendicular clear width every metre; pinch points under the army width plus 2 m).
        IEnumerable<(string Name, Vector2[] Path)> routes = Plan is not null ? Plan.Routes.Select(r => (r.Name, r.Path)) : AshfallLanes.Routes;
        foreach (var (name, path) in routes)
        {
            float min = float.MaxValue; Vector2 at = path[0];
            var pinches = new List<(Vector2, float)>();
            for (int i = 1; i < path.Length; i++)
            {
                var a = path[i - 1]; var b = path[i]; var dir = Vector2.Normalize(b - a); float len = Vector2.Distance(a, b);
                for (float t = 0f; t <= len; t += 1f)
                {
                    var q = a + dir * t;
                    if (InsideBase(q)) continue;   // inside the compound or the stronghold: their gates are authored
                    float w = Grid.WidthAcross(q, dir);
                    if (w < min) { min = w; at = q; }
                    if (w < MinArmyWidth + 2f && (pinches.Count == 0 || Vector2.Distance(pinches[^1].Item1, q) > 6f)) pinches.Add((q, w));
                }
            }
            RouteWidths.Add(new(name, min, at, pinches));
        }
        // 4. Sightlines: point to point; each point's view down its approaches (samples 20..150 m along each corridor that reaches it); landmarks; elevated positions.
        var points = Level.Points.ToList();
        Vector3 EyeAt(Vector2 q, float lift = Eye) => new(q.X, q.Y, Grid.GroundAt(q) + lift);
        for (int i = 0; i < points.Count; i++)
            for (int j = i + 1; j < points.Count; j++)
                PointSight.Add((points[i].Name, points[j].Name, Grid.Sees(EyeAt(new(points[i].Center.X, points[i].Center.Y)), EyeAt(new(points[j].Center.X, points[j].Center.Y), 2f))));
        if (Plan is not null)
            foreach (var p in points)
                foreach (var k in Plan.ApproachesOf(p))
                {
                    var c = new Vector2(p.Center.X, p.Center.Y);
                    var far = Vector2.Distance(k.A, c) > Vector2.Distance(k.B, c) ? k.A : k.B;
                    var dir = Vector2.Normalize(far - c);
                    int seen = 0, total = 0;
                    for (float t = 20f; t <= MathF.Min(150f, Vector2.Distance(far, c)); t += 10f) { total++; if (Grid.Sees(EyeAt(c), EyeAt(c + dir * t, 2f))) seen++; }
                    if (total > 0) Scouting.Add((p.Name, k.Name, seen / (float)total));
                }
        foreach (var s in Plan?.Structures.Where(s => s.Landmark) ?? [])
        {
            // the drawn model that stands for it (the nearest placed prop of its group's size)
            // seen when the model's top (its upper tenth) shows over everything between: the model's own footprint does not hide it
            var top = new Vector3(s.Centre.X, s.Centre.Y, AshfallMap.BlockTop + s.Size.Z);
            float skip = MathF.Max(s.Size.X, s.Size.Y) * 0.75f + 1f;
            var item = Dressing.Items.Where(i => i.Role == AshfallDressRole.Prop && Vector2.Distance(new(i.Position.X, i.Position.Y), s.Centre) < 6f).OrderByDescending(i => i.Size.Z).FirstOrDefault();
            if (item is not null) { top = new Vector3(item.Position.X, item.Position.Y, item.Position.Z + item.Size.Z * 0.95f); skip = MathF.Max(item.Size.X, item.Size.Y) * 0.75f + 1f; }
            var from = points.Where(p => SeesTall(EyeAt(new(p.Center.X, p.Center.Y)), top, skip)).Select(p => p.Name).ToList();
            Landmarks.Add((s.Name, top, string.Join("", from), from.Count));
        }
        foreach (var it in Dressing.Items.Where(i => i.Role == AshfallDressRole.Prop && AshfallDraft5Pass.PlatformIds.Contains(i.Asset)))
        {
            if (InsideBase(new Vector2(it.Position.X, it.Position.Y)) || (it.Position.Y > 96f && MathF.Abs(it.Position.X) < 84f)) { StrongholdHeights++; continue; }   // the stronghold's own terraces: authored
            var eye = new Vector3(it.Position.X, it.Position.Y, it.Position.Z + it.Size.Z + Eye);
            var seen = points.Where(p => Vector2.Distance(new(p.Center.X, p.Center.Y), new(eye.X, eye.Y)) <= 250f && SeesTall(eye, EyeAt(new(p.Center.X, p.Center.Y), 1f))).Select(p => p.Name).ToList();
            Heights.Add(new($"{it.Asset} at ({it.Position.X:0}, {it.Position.Y:0})", eye, seen.Count, string.Join("", seen)));
        }
        // 5. Protected places: blocked cells (the final collision's walking band) inside them.
        if (Plan is not null)
        {
            foreach (var (name, r) in Plan.Assembly) Count(name, k => { var q = Grid.At(k); return q.X > r.X && q.X < r.Z && q.Y > r.Y && q.Y < r.W; });
            foreach (var (name, c, rad) in Plan.ClearZones) Count($"{name} open zone", k => Vector2.DistanceSquared(Grid.At(k), c) < rad * rad);
            foreach (var k in Plan.Corridors) Count($"corridor {k.Name}", m => k.Contains(Grid.At(m)));
            foreach (var d in Plan.Districts)
            {
                float land = 0f, cover = 0f;
                foreach (var r in d.Rects) land += (r.Z - r.X) * (r.W - r.Y);
                foreach (var it in Dressing.Items.Where(i => i.Role == AshfallDressRole.Prop))
                    if (d.Rects.Any(r => it.Position.X >= r.X && it.Position.X <= r.Z && it.Position.Y >= r.Y && it.Position.Y <= r.W)) cover += it.Size.X * it.Size.Y;
                Density.Add((d.Name, cover / land));
            }
        }
        Judge();
    }

    void Count(string name, Func<int, bool> inside)
    {
        int blocked = 0, cells = 0;
        for (int k = 0; k < Grid.Blocked.Length; k++) if (inside(k)) { cells++; if (Grid.Blocked[k]) blocked++; }
        ProtectedBlocked.Add((name, blocked, cells));
    }

    bool InsideBase(Vector2 q) => (q.X > -38f && q.X < 38f && q.Y > -218f && q.Y < -157f) || (q.X > -72f && q.X < 72f && q.Y > 108f && q.Y < 220f);

    /// <summary>Sightline to a tall target: the target's own cell and its model's footprint do not hide it (a landmark's top is seen when nothing between hides it).</summary>
    bool SeesTall(Vector3 from, Vector3 to, float skip = 9f)
    {
        var d = to - from; float len = new Vector2(d.X, d.Y).Length();
        int steps = Math.Max(1, (int)(len / (Grid.Cell * 0.5f)));
        for (int s = 1; s < steps; s++)
        {
            var p = from + d * (s / (float)steps);
            if (Vector2.Distance(new(p.X, p.Y), new(to.X, to.Y)) < skip || Vector2.Distance(new(p.X, p.Y), new(from.X, from.Y)) < 2f) continue;
            if (Grid.Top[Grid.Index(new(p.X, p.Y))] > p.Z) return false;
        }
        return true;
    }

    /// <summary>Where a sightline is first hidden (null: seen), for the report.</summary>
    public Vector3? Hidden(Vector3 from, Vector3 to)
    {
        var d = to - from; float len = new Vector2(d.X, d.Y).Length();
        int steps = Math.Max(1, (int)(len / (Grid.Cell * 0.5f)));
        for (int s = 1; s < steps; s++)
        {
            var p = from + d * (s / (float)steps);
            if (Vector2.Distance(new(p.X, p.Y), new(to.X, to.Y)) < 9f || Vector2.Distance(new(p.X, p.Y), new(from.X, from.Y)) < 2f) continue;
            float top = Grid.Top[Grid.Index(new(p.X, p.Y))];
            if (top > p.Z) return new Vector3(p.X, p.Y, top);
        }
        return null;
    }

    static float Sep(float a, float b) { float d = MathF.Abs(a - b) % 360f; return d > 180f ? 360f - d : d; }

    int[] Components(float clearance, Func<int, bool> forbid)
    {
        var comp = new int[Grid.Blocked.Length];
        int label = 0, n = Grid.N;
        var stack = new Stack<int>();
        for (int s = 0; s < comp.Length; s++)
        {
            if (comp[s] != 0 || Grid.Clearance[s] < clearance || forbid(s)) continue;
            label++; comp[s] = label; stack.Push(s);
            while (stack.Count > 0)
            {
                int k = stack.Pop(); int x = k % n, y = k / n;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                        int m = ny * n + nx;
                        if (comp[m] != 0 || Grid.Clearance[m] < clearance || forbid(m)) continue;
                        comp[m] = label; stack.Push(m);
                    }
            }
        }
        return comp;
    }

    void Judge()
    {
        var p = Plan?.Params ?? AshfallFieldParams.Default;
        string M(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        foreach (var l in Links)
        {
            Constraints.Add(new($"army route {l.From} to {l.To}: width at its narrowest point", l.Width >= MinArmyWidth, $"{M(l.Width)} m at ({l.Pinch.X:0}, {l.Pinch.Y:0})", $">= {M(MinArmyWidth)} m"));
            if (l.From == "base") Constraints.Add(new($"reinforcement time base to {l.To} (Guard army, 3.6 m/s)", l.GuardSeconds is { } g && g <= p.MaxReinforceSeconds, l.GuardSeconds is { } g2 ? $"{g2:0} s" : "no army route", $"<= {p.MaxReinforceSeconds:0} s"));
            if (l.From == "stronghold") Constraints.Add(new($"wave time stronghold to {l.To} (Ork army, 3 m/s)", l.OrkSeconds is { } o && o <= p.MaxWaveSeconds, l.OrkSeconds is { } o2 ? $"{o2:0} s" : "no army route", $"<= {p.MaxWaveSeconds:0} s"));
        }
        foreach (var a in Approaches)
            Constraints.Add(new($"point {a.Point}: distinct formation-capable approaches", a.Count >= p.ApproachCount, $"{a.Count} (bearings {string.Join(", ", a.Bearings.Select(b => $"{b:0}"))})", $">= {p.ApproachCount}, {p.ApproachSeparation:0} degrees apart"));
        foreach (var r in RouteWidths)
            Constraints.Add(new($"route {r.Route}: perpendicular width along it", r.MinWidth >= MinArmyWidth, $"{M(r.MinWidth)} m at ({r.Pinch.X:0}, {r.Pinch.Y:0})", $">= {M(MinArmyWidth)} m"));
        foreach (var (name, blocked, cells) in ProtectedBlocked)
            Constraints.Add(new($"protected: {name} clear of collision", blocked == 0, $"{blocked} of {cells} cells blocked", "0"));
        foreach (var (name, _, from, count) in Landmarks)
            Constraints.Add(new($"landmark {name} seen from the points", count >= p.LandmarkVisibility, from.Length > 0 ? from : "none", $">= {p.LandmarkVisibility} of 3"));
        if (Heights.Count > 0)
        {
            int most = Heights.Max(h => h.PointsSeen);
            Constraints.Add(new("no elevated position sees every point", most <= p.MaxPointsSeenFromHeight, $"most seen from one platform: {most} ({Heights.Count(h => h.PointsSeen == most)} platforms)", $"<= {p.MaxPointsSeenFromHeight}"));
            foreach (var pt in Level.Points)
                Constraints.Add(new($"point {pt.Name}: high ground with a view of it", Heights.Any(h => h.Seen.Contains(pt.Name)), $"{Heights.Count(h => h.Seen.Contains(pt.Name))} platforms", ">= 1"));
        }
        foreach (var pt in Level.Points)
        {
            var sc = Scouting.Where(s => s.Point == pt.Name).ToList();
            if (sc.Count > 0) Constraints.Add(new($"point {pt.Name}: an approach seen from it (scouting)", sc.Any(s => s.Share >= 0.5f), string.Join(", ", sc.Select(s => $"{s.Approach} {s.Share:P0}")), "one approach >= 50 % seen"));
        }
        foreach (var (d, cover) in Density)
        {
            float target = d switch { "cathedral quarter" => p.DensityCathedral, "manufactorum district" => p.DensityManufactorum, "hab-block ruins" => p.DensityHab, _ => p.DensityNoMansLand };
            Constraints.Add(new($"density: {d} (drawn footprint share)", cover <= target * 1.5f + 0.02f, $"{cover:P0}", $"<= {target * 1.5f + 0.02f:P0} (target {target:P0})"));
        }
    }

    /// <summary>The constraint report as text (failed first).</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ashfall {Layout} layout: hash {Level.Hash:X16}, {Level.Parts.Count} parts, {Level.SolidCorners().Length / 3} collision triangles, {Dressing.Items.Count(i => i.Role == AshfallDressRole.Prop)} props; army width {MinArmyWidth:0.0} m");
        int failed = Constraints.Count(c => !c.Passed);
        sb.AppendLine(failed == 0 ? $"ALL {Constraints.Count} CONSTRAINTS PASS" : $"{failed} OF {Constraints.Count} CONSTRAINTS FAIL");
        foreach (var c in Constraints.OrderBy(c => c.Passed))
            sb.AppendLine($"  [{(c.Passed ? "pass" : "FAIL")}] {c.Name}: {c.Value} (needs {c.Threshold})");
        sb.AppendLine();
        sb.AppendLine(TravelTable());
        return sb.ToString();
    }

    /// <summary>The travel-time table (Markdown): narrowest width, army walk and the times at each speed; player walk and run time.</summary>
    public string TravelTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"| From | To | Narrowest (m) | Army route (m) | Guard army 3.6 m/s (s) | Ork army 3 m/s (s) | Player walk (m) | Player run 5 m/s (s) |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var l in Links)
            sb.AppendLine($"| {l.From} | {l.To} | {l.Width:0.0} | {F(l.ArmyLength)} | {F(l.GuardSeconds)} | {F(l.OrkSeconds)} | {F(l.WalkLength)} | {F(l.PlayerSeconds)} |");
        return sb.ToString();
        static string F(float? v) => v is { } x ? x.ToString("0", CultureInfo.InvariantCulture) : "none";
    }
}
