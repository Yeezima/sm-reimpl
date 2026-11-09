using System.Globalization;
using System.Numerics;
using System.Text;
using SmContent;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;

namespace SmTool;

/// <summary>
/// <c>smtool ashfall-plan [--out plan.svg]</c>: Ashfall Citadel's top-down plan from the generator (docs/musou-ashfall-graybox.md, check 6): every solid part coloured by kind (darker to lighter with its top
/// height; labels on floors and perches), the 4 m grid faint and 40 m lines stronger, the capture points with their radii, both start areas, the Stormbird and the Firing Range footprints as the war composes
/// them, a 50 m scale bar and a north arrow. Nothing is read from the install. Default output: <c>%LOCALAPPDATA%\sm-reimpl\cache\previews\ashfall-plan.svg</c>.
/// </summary>
static class AshfallPlanCommand
{
    const float Scale = 1.5f;   // pixels a metre

    static readonly Dictionary<AshfallKind, (byte R, byte G, byte B)> Colours = new()
    {
        [AshfallKind.Ground] = (200, 192, 176), [AshfallKind.Road] = (120, 120, 126), [AshfallKind.Paving] = (170, 160, 140), [AshfallKind.Curb] = (212, 206, 190),
        [AshfallKind.Wall] = (150, 110, 90), [AshfallKind.Cover] = (60, 140, 90), [AshfallKind.Rubble] = (110, 100, 90), [AshfallKind.Floor] = (90, 130, 190),
        [AshfallKind.Stair] = (230, 170, 60), [AshfallKind.Perch] = (170, 80, 170), [AshfallKind.Objective] = (230, 60, 60), [AshfallKind.Stronghold] = (120, 50, 50),
        [AshfallKind.Boundary] = (50, 50, 60), [AshfallKind.Backdrop] = (90, 90, 110),
    };

    public static int Run(string[] rest)
    {
        string? outPath = null, jsonPath = null, glbPath = null, reportPath = null, viewsDir = null;
        bool noRoadway = false, overlays = false;
        var layout = AshfallLayout.City;   // draft 12: the city is the map tfw_war loads
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--out" && i + 1 < rest.Length) outPath = rest[++i];
            else if (rest[i] == "--dressing-json" && i + 1 < rest.Length) jsonPath = Path.GetFullPath(rest[++i]);
            else if (rest[i] == "--graybox-glb" && i + 1 < rest.Length) glbPath = Path.GetFullPath(rest[++i]);
            else if (rest[i] == "--no-roadway-rule") noRoadway = true;   // report aid: the placement without draft 8's roadway rule (before/after plans)
            else if (rest[i] == "--layout" && i + 1 < rest.Length)   // draft 11/12: city (map tfw_war), field (map tfw_war_field) or draft10 (the rollback, tfw_war_draft10); the battlefield overlays come with it
            {
                string v = rest[++i];
                if (v is not ("city" or "field" or "draft10")) { Console.Error.WriteLine($"ashfall-plan: --layout takes city, field or draft10, not \"{v}\""); return 2; }
                layout = AshfallLayouts.Parse(v); overlays = true;
            }
            else if (rest[i] == "--overlays") overlays = true;
            else if (rest[i] == "--report" && i + 1 < rest.Length) { reportPath = Path.GetFullPath(rest[++i]); overlays = true; }
            else if (rest[i] == "--views" && i + 1 < rest.Length) { viewsDir = Path.GetFullPath(rest[++i]); overlays = true; }
            else { Console.Error.WriteLine($"ashfall-plan: unknown argument \"{rest[i]}\" (usage: ashfall-plan [--layout city|field|draft10] [--overlays] [--report file.txt] [--views dir] [--out plan.svg] [--dressing-json file.json] [--graybox-glb file.glb] [--no-roadway-rule])"); return 2; }
        }
        outPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "cache", "previews", "ashfall-plan.svg");
        outPath = Path.GetFullPath(outPath);
        var (level, dressing) = AshfallMap.BuildWithDressing(layout);
        if (noRoadway) { var before = AshfallMap.RebuildWithoutRoadwayRule(); (level, dressing) = (before.Level, before.Dressing); Console.WriteLine("ashfall-plan: WITHOUT the draft-8 roadway rule (report aid)"); }
        var (_, hub, _) = TfwMap.WithHub(AshfallContent.ToMapContent(level).ToCollisionWorld(), manufactorum: false);
        var range = TfwRange.For(AshfallContent.ToMapContent(level).ToCollisionWorld());
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        AshfallBattlefield? bf = null;
        if (overlays && !noRoadway)
        {
            bf = new AshfallBattlefield(layout, level, dressing, layout.PlanOf());
            string report = bf.Report() + (bf.Plan is { } pl ? "\nPlan notes:\n" + string.Join("\n", pl.Notes.Select(n => "  " + n)) + "\n" : "") + "\nPlacement flags:\n" + string.Join("\n", dressing.Flags.Where(f => f.StartsWith("draft", StringComparison.Ordinal)).Select(f => "  " + (f.Length > 400 ? f[..400] + " ..." : f)));
            Console.WriteLine(report);
            if (reportPath is not null) { Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!); File.WriteAllText(reportPath, report, new UTF8Encoding(false)); Console.WriteLine($"ashfall-plan: report -> {reportPath}"); }
            if (viewsDir is not null)
                foreach (var f in AshfallBattlefieldViews.Rasters(bf, viewsDir, layout.Key() + "-")) Console.WriteLine($"ashfall-plan: view -> {f}");
        }
        File.WriteAllText(outPath, Render(level, dressing, hub, range, bf), new UTF8Encoding(false));
        Console.WriteLine($"ashfall-plan: {level.Parts.Count} parts, {level.SolidCorners().Length / 3} collision triangles, hash {level.Hash:X16} -> {outPath}");
        foreach (var g in level.Parts.Where(p => p.Solid).GroupBy(p => p.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-10} {g.Count(),5} parts {g.Sum(p => p.Corners.Length / 3),7} triangles");
        Console.WriteLine($"dressing version {dressing.Version}: {dressing.Items.Count} items, {dressing.HiddenParts.Count} hidden parts");
        foreach (var g in dressing.Items.GroupBy(i => i.Role).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-10} {g.Count(),5} items {g.Sum(i => AshfallKit.Get(i.Asset).Triangles),10} model triangles");
        Console.WriteLine("  per asset: " + string.Join(", ", dressing.Items.GroupBy(i => i.Asset).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} x{g.Count()}")));
        if (jsonPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            File.WriteAllText(jsonPath, AshfallExport.DressingJson(level, dressing), new UTF8Encoding(false));
            Console.WriteLine($"ashfall-plan: dressing JSON -> {jsonPath}");
        }
        if (glbPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(glbPath)!);
            File.WriteAllBytes(glbPath, AshfallExport.GrayboxGlb(level));
            Console.WriteLine($"ashfall-plan: graybox GLB -> {glbPath}");
        }
        return 0;
    }

    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static string Render(AshfallLevel level, AshfallDressing dressing, TfwHub hub, TfwRange range, AshfallBattlefield? bf = null)
    {
        // Canvas: the playable square, its backdrop, the Stormbird to the south and the range to the east.
        float x0 = -290f, x1 = range.Box.Max.X + 20f, y1 = 290f, y0 = hub.Box.Min.Y - 20f;
        float W = (x1 - x0) * Scale, H = (y1 - y0) * Scale;
        float X(float x) => (x - x0) * Scale;
        float Y(float y) => (y1 - y) * Scale;
        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}\" height=\"{F(H)}\" viewBox=\"0 0 {F(W)} {F(H)}\" font-family=\"sans-serif\">");
        sb.AppendLine($"<rect width=\"100%\" height=\"100%\" fill=\"#f4f1ea\"/>");

        // Parts, lowest top first so taller things draw over them.
        foreach (var part in level.Parts.OrderBy(p => p.Kind == AshfallKind.Backdrop ? -1 : p.Kind == AshfallKind.Boundary ? 0 : 1).ThenBy(p => p.Corners.Max(c => c.Z)))
        {
            var (r, g, b) = Colours[part.Kind];
            float top = part.Corners.Max(c => c.Z);
            float shade = Math.Clamp(1.15f - top / 40f, 0.55f, 1.15f);
            string Fill(float k) => $"#{(byte)Math.Clamp(r * k, 0, 255):x2}{(byte)Math.Clamp(g * k, 0, 255):x2}{(byte)Math.Clamp(b * k, 0, 255):x2}";
            if (part.Corners.Length > 600 && part.Kind == AshfallKind.Ground)
            {
                // The crater heightfield: each triangle by its mean height.
                for (int i = 0; i + 2 < part.Corners.Length; i += 3)
                {
                    var a = part.Corners[i]; var bb = part.Corners[i + 1]; var c = part.Corners[i + 2];
                    float z = (a.Z + bb.Z + c.Z) / 3f;
                    float k = Math.Clamp(1f + (z - 0.25f) * 0.12f, 0.5f, 1.2f);
                    sb.AppendLine($"<polygon points=\"{F(X(a.X))},{F(Y(a.Y))} {F(X(bb.X))},{F(Y(bb.Y))} {F(X(c.X))},{F(Y(c.Y))}\" fill=\"{Fill(k)}\" stroke=\"{Fill(k)}\" stroke-width=\"0.4\"/>");
                }
                continue;
            }
            var hull = Hull(part.Corners.Select(c => new Vector2(c.X, c.Y)));
            if (hull.Count < 3) continue;
            string opacity = part.Kind is AshfallKind.Road or AshfallKind.Paving ? "0.85" : "1";
            sb.AppendLine($"<polygon points=\"{string.Join(' ', hull.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}"))}\" fill=\"{Fill(shade)}\" fill-opacity=\"{opacity}\" stroke=\"#222\" stroke-opacity=\"0.35\" stroke-width=\"0.3\"/>");
        }

        // Dressing items: an outline per item, coloured by role (Skin blue, Surface grey-green, Decoration orange); props are filled.
        foreach (var it in dressing.Items.OrderBy(i => i.Role == AshfallDressRole.Prop ? 1 : 0))
        {
            float sn = MathF.Sin(it.Yaw), cs = MathF.Cos(it.Yaw), hx = it.Size.X / 2f, hy = it.Size.Y / 2f;
            string pts = string.Join(' ', new[] { (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy) }.Select(c => $"{F(X(it.Position.X + c.Item1 * cs - c.Item2 * sn))},{F(Y(it.Position.Y + c.Item1 * sn + c.Item2 * cs))}"));
            (string stroke, string fill) = it.Role switch
            {
                AshfallDressRole.Skin => ("#1f5fbf", "none"),
                AshfallDressRole.Surface => ("#4c8a6a", "none"),
                AshfallDressRole.Decoration => ("#e07a10", "none"),
                _ => ("#c0189c", "#c0189c"),
            };
            sb.AppendLine($"<polygon points=\"{pts}\" fill=\"{fill}\" fill-opacity=\"0.45\" stroke=\"{stroke}\" stroke-width=\"{(it.Role == AshfallDressRole.Prop ? "1" : "0.5")}\" stroke-opacity=\"0.8\"/>");
        }

        // Grid: 4 m faint, 40 m stronger, over the playable square.
        for (int m = -240; m <= 240; m += 4)
        {
            bool strong = m % 40 == 0;
            string style = strong ? "stroke=\"#000\" stroke-opacity=\"0.30\" stroke-width=\"0.6\"" : "stroke=\"#000\" stroke-opacity=\"0.07\" stroke-width=\"0.3\"";
            sb.AppendLine($"<line x1=\"{F(X(m))}\" y1=\"{F(Y(-240))}\" x2=\"{F(X(m))}\" y2=\"{F(Y(240))}\" {style}/>");
            sb.AppendLine($"<line x1=\"{F(X(-240))}\" y1=\"{F(Y(m))}\" x2=\"{F(X(240))}\" y2=\"{F(Y(m))}\" {style}/>");
            if (strong && m != 0) sb.AppendLine($"<text x=\"{F(X(m))}\" y=\"{F(Y(-240) + 12)}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#444\">{m}</text><text x=\"{F(X(-240) - 4)}\" y=\"{F(Y(m) + 3)}\" font-size=\"9\" text-anchor=\"end\" fill=\"#444\">{m}</text>");
        }

        // Heights of floors and perches.
        foreach (var part in level.Parts.Where(p => p.Kind is AshfallKind.Floor or AshfallKind.Perch))
        {
            float top = part.Corners.Max(c => c.Z);
            if (top < 1.9f) continue;
            var cx = part.Corners.Average(c => c.X); var cy = part.Corners.Average(c => c.Y);
            string tag = part.Kind == AshfallKind.Perch ? "P" : "F";
            sb.AppendLine($"<text x=\"{F(X(cx))}\" y=\"{F(Y(cy) + 3)}\" font-size=\"9\" font-weight=\"bold\" text-anchor=\"middle\" fill=\"#fff\" stroke=\"#000\" stroke-width=\"0.4\">{tag}{F(top)}</text>");
        }

        // Capture points with radii, start areas.
        foreach (var p in level.Points)
        {
            sb.AppendLine($"<circle cx=\"{F(X(p.Center.X))}\" cy=\"{F(Y(p.Center.Y))}\" r=\"{F(p.Radius * Scale)}\" fill=\"#e63c3c\" fill-opacity=\"0.12\" stroke=\"#c01818\" stroke-width=\"1.5\"/>");
            sb.AppendLine($"<text x=\"{F(X(p.Center.X))}\" y=\"{F(Y(p.Center.Y) + 5)}\" font-size=\"16\" font-weight=\"bold\" text-anchor=\"middle\" fill=\"#c01818\">{p.Name}</text>");
            sb.AppendLine($"<text x=\"{F(X(p.Center.X))}\" y=\"{F(Y(p.Center.Y) + 18)}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#c01818\">r {F(p.Radius)} m</text>");
        }
        foreach (var a in level.Areas)
        {
            string c = a.Side == Team.SpaceMarines ? "#1c4fd0" : "#7a1c1c";
            sb.AppendLine($"<rect x=\"{F(X(a.Center.X - a.Size.X / 2))}\" y=\"{F(Y(a.Center.Y + a.Size.Y / 2))}\" width=\"{F(a.Size.X * Scale)}\" height=\"{F(a.Size.Y * Scale)}\" fill=\"none\" stroke=\"{c}\" stroke-width=\"2\" stroke-dasharray=\"6 4\"/>");
            sb.AppendLine($"<text x=\"{F(X(a.Center.X))}\" y=\"{F(Y(a.Center.Y - a.Size.Y / 2) + 13)}\" font-size=\"11\" text-anchor=\"middle\" fill=\"{c}\">{(a.Side == Team.SpaceMarines ? "Space Marine start" : "Chaos start (commander)")}</text>");
        }
        foreach (var s in level.Spawns) sb.AppendLine($"<circle cx=\"{F(X(s.X))}\" cy=\"{F(Y(s.Y))}\" r=\"2.5\" fill=\"#fff\" stroke=\"#000\" stroke-width=\"0.8\"/>");

        // The Stormbird and the Firing Range footprints.
        foreach (var (name, box) in new[] { ("Stormbird (hub)", hub.Box), ("Firing Range", range.Box) })
        {
            sb.AppendLine($"<rect x=\"{F(X(box.Min.X))}\" y=\"{F(Y(box.Max.Y))}\" width=\"{F((box.Max.X - box.Min.X) * Scale)}\" height=\"{F((box.Max.Y - box.Min.Y) * Scale)}\" fill=\"#b8c8e0\" fill-opacity=\"0.45\" stroke=\"#345\" stroke-width=\"1.5\"/>");
            sb.AppendLine($"<text x=\"{F(X((box.Min.X + box.Max.X) / 2))}\" y=\"{F(Y((box.Min.Y + box.Max.Y) / 2))}\" font-size=\"12\" text-anchor=\"middle\" fill=\"#234\">{name}</text>");
        }

        // Playable square outline, scale bar, north arrow, legend.
        sb.AppendLine($"<rect x=\"{F(X(-240))}\" y=\"{F(Y(240))}\" width=\"{F(480 * Scale)}\" height=\"{F(480 * Scale)}\" fill=\"none\" stroke=\"#000\" stroke-width=\"1.2\"/>");
        float bx = X(-280), by = Y(-270);
        sb.AppendLine($"<line x1=\"{F(bx)}\" y1=\"{F(by)}\" x2=\"{F(bx + 50 * Scale)}\" y2=\"{F(by)}\" stroke=\"#000\" stroke-width=\"3\"/><text x=\"{F(bx + 25 * Scale)}\" y=\"{F(by - 6)}\" font-size=\"11\" text-anchor=\"middle\">50 m</text>");
        float nx = X(-270), ny = Y(250);
        sb.AppendLine($"<polygon points=\"{F(nx)},{F(ny - 22)} {F(nx - 7)},{F(ny)} {F(nx + 7)},{F(ny)}\" fill=\"#000\"/><text x=\"{F(nx)}\" y=\"{F(ny + 14)}\" font-size=\"12\" font-weight=\"bold\" text-anchor=\"middle\">N</text>");
        int row = 0;
        foreach (var (kind, (r, g, b)) in Colours)
        {
            float ly = Y(y1 - 20) + 8 + row++ * 14;
            sb.AppendLine($"<rect x=\"{F(X(range.Box.Min.X))}\" y=\"{F(ly)}\" width=\"10\" height=\"10\" fill=\"#{r:x2}{g:x2}{b:x2}\" stroke=\"#222\" stroke-width=\"0.4\"/><text x=\"{F(X(range.Box.Min.X) + 14)}\" y=\"{F(ly + 9)}\" font-size=\"10\">{kind}</text>");
        }
        sb.AppendLine($"<text x=\"{F(X(range.Box.Min.X))}\" y=\"{F(Y(y1 - 20) + 8 + row * 14 + 14)}\" font-size=\"9\" fill=\"#444\">F6 / P15: floor or perch, top height m. Shade: higher is darker.</text>");
        sb.AppendLine($"<text x=\"10\" y=\"16\" font-size=\"13\" font-weight=\"bold\">Ashfall Citadel {(level.Layout switch { AshfallLayout.Field => $"battlefield layout (draft 11, field version {AshfallField.Version})", AshfallLayout.City => $"city layout (draft 12, city version {AshfallCity.Version})", _ => $"graybox, version {AshfallMap.Version}" })}, hash {level.Hash:X16}, kit dressing {dressing.Version}: {dressing.Items.Count} items (ours: placeholder demonstration)</text>");
        sb.AppendLine($"<text x=\"10\" y=\"30\" font-size=\"11\"><tspan fill=\"#1f5fbf\">Skin {dressing.Items.Count(i => i.Role == AshfallDressRole.Skin)}</tspan>  <tspan fill=\"#4c8a6a\">Surface {dressing.Items.Count(i => i.Role == AshfallDressRole.Surface)}</tspan>  <tspan fill=\"#e07a10\">Decoration {dressing.Items.Count(i => i.Role == AshfallDressRole.Decoration)}</tspan>  <tspan fill=\"#c0189c\">Prop (filled) {dressing.Items.Count(i => i.Role == AshfallDressRole.Prop)}</tspan></text>");
        if (bf is not null) sb.Append(AshfallBattlefieldViews.Overlays(bf, X, Y, Scale));   // draft 11: protected areas, routes, pinch points, assembly zones, approaches, sightlines, travel times, failed constraints
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    /// <summary>The convex hull of the points (Andrew's monotone chain), counter-clockwise.</summary>
    static List<Vector2> Hull(IEnumerable<Vector2> points)
    {
        var p = points.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3) return p;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<Vector2>();
        foreach (var v in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], v) <= 0) h.RemoveAt(h.Count - 1); h.Add(v); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(p[i]); }
        h.RemoveAt(h.Count - 1);
        return h;
    }
}
