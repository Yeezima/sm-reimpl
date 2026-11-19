using System.Globalization;
using System.Numerics;
using System.Text;
using SmSim.Extras.Musou.Ashfall;

namespace SmTool;

/// <summary>
/// The Musou front pilot's overhead annotations (<c>smtool ashfall-plan --layout front --band n</c>; docs/musou-ashfall-graybox.md "Front pilot"): the base and the stronghold named, the capture points, the primary route between each pair of
/// places (thick blue: the first planned route) and the alternates (dashed orange), the through road along x = 0, and gate A's and gate B's results; and the same as a PNG over the battlefield's top-down raster.
/// </summary>
static class AshfallFrontViews
{
    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The routes split into the primary (the first planned route of each from-to pair) and the alternates.</summary>
    static (List<AshfallRoute> Primary, List<AshfallRoute> Alternate) Routes(AshfallFieldPlan plan)
    {
        var primary = new List<AshfallRoute>(); var alternate = new List<AshfallRoute>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in plan.Routes) (seen.Add(r.From + ">" + r.To) ? primary : alternate).Add(r);
        return (primary, alternate);
    }

    /// <summary>The SVG layer (drawn over the plan's other overlays).</summary>
    public static string Svg(AshfallBattlefield bf, AshfallFieldPlan plan, IReadOnlyList<string> gateB, Func<float, float> X, Func<float, float> Y, float scale)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<g id=\"front-annotations\">");
        var (primary, alternate) = Routes(plan);
        // the through road
        sb.AppendLine($"<rect x=\"{F(X(-12f))}\" y=\"{F(Y(240f))}\" width=\"{F(24f * scale)}\" height=\"{F(480f * scale)}\" fill=\"#6b4fbf\" fill-opacity=\"0.10\" stroke=\"#6b4fbf\" stroke-width=\"1.2\" stroke-dasharray=\"10 4\"/>");
        sb.AppendLine($"<text x=\"{F(X(14f))}\" y=\"{F(Y(232f))}\" font-size=\"11\" fill=\"#4b2f9f\">main avenue 24 m (through the base and the stronghold, edge to edge)</text>");
        foreach (var r in alternate)
            sb.AppendLine($"<polyline points=\"{string.Join(' ', r.Path.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}"))}\" fill=\"none\" stroke=\"#e07a10\" stroke-width=\"3\" stroke-opacity=\"0.85\" stroke-dasharray=\"9 5\"/>");
        foreach (var r in primary)
            sb.AppendLine($"<polyline points=\"{string.Join(' ', r.Path.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}"))}\" fill=\"none\" stroke=\"#1c4fd0\" stroke-width=\"4\" stroke-opacity=\"0.75\"/>");
        // the strongholds
        foreach (var (name, at, c) in new[] { ("IMPERIAL BASE (through variant)", AshfallFront.BaseCentre, "#1c4fd0"), ("ORK STRONGHOLD (through variant: north gate, keep in two wings)", AshfallFront.StrongholdCentre, "#7a1c1c") })
            sb.AppendLine($"<text x=\"{F(X(at.X))}\" y=\"{F(Y(at.Y))}\" font-size=\"14\" font-weight=\"bold\" text-anchor=\"middle\" fill=\"{c}\" stroke=\"#fff\" stroke-width=\"3\" paint-order=\"stroke\">{name}</text>");
        foreach (var p in plan.Points)
            sb.AppendLine($"<text x=\"{F(X(p.Center.X))}\" y=\"{F(Y(p.Center.Y) - 22)}\" font-size=\"13\" font-weight=\"bold\" text-anchor=\"middle\" fill=\"#c01818\" stroke=\"#fff\" stroke-width=\"3\" paint-order=\"stroke\">capture point {p.Name}</text>");
        // legend and the gates
        float lx = X(-240f), ly = Y(-250f);
        sb.AppendLine($"<line x1=\"{F(lx)}\" y1=\"{F(ly)}\" x2=\"{F(lx + 40)}\" y2=\"{F(ly)}\" stroke=\"#1c4fd0\" stroke-width=\"4\"/><text x=\"{F(lx + 46)}\" y=\"{F(ly + 4)}\" font-size=\"11\">primary route ({primary.Count})</text>");
        sb.AppendLine($"<line x1=\"{F(lx + 200)}\" y1=\"{F(ly)}\" x2=\"{F(lx + 240)}\" y2=\"{F(ly)}\" stroke=\"#e07a10\" stroke-width=\"3\" stroke-dasharray=\"9 5\"/><text x=\"{F(lx + 246)}\" y=\"{F(ly + 4)}\" font-size=\"11\">alternate route ({alternate.Count})</text>");
        int aFail = plan.GateA.Count(r => !r.Passed), bFail = gateB.Count(r => r.StartsWith("[FAIL]", StringComparison.Ordinal));
        sb.AppendLine($"<text x=\"10\" y=\"44\" font-size=\"12\" font-weight=\"bold\" fill=\"{(aFail + bFail == 0 ? "#0a5" : "#d01")}\">Front band {plan.Band} ({AshfallBandProfile.Front[plan.Band].Name}), seed {plan.Params.LayoutSeed}: gate A (empty frame) {plan.GateA.Count - aFail}/{plan.GateA.Count} pass; gate B (final collision) {gateB.Count - bFail}/{gateB.Count} pass; fabric cover {plan.Coverage:P0} of {plan.FreeLand / 10000f:0.0} ha free land</text>");
        sb.AppendLine("</g>");
        return sb.ToString();
    }

    /// <summary>The annotated overhead PNG: the battlefield's top-down raster (0.5 m a pixel), the through road, the routes (primary blue, alternate orange), the points and the two strongholds' outlines.</summary>
    public static string Png(AshfallBattlefield bf, AshfallFieldPlan plan, string path)
    {
        var g = bf.Grid; int n = g.N;
        var img = new byte[n * n * 3];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int k = (n - 1 - y) * n + x;
                float top = g.Top[k] - g.GroundAt(g.At(k));
                float t = Math.Clamp(top / 30f, 0f, 1f);
                (byte, byte, byte) c = top < 0.3f ? (g.Blocked[k] ? ((byte)90, (byte)80, (byte)70) : ((byte)206, (byte)198, (byte)182)) : ((byte)(176 - 116 * t), (byte)(140 - 100 * t), (byte)(110 - 70 * t));
                Put(img, n, x, y, c);
            }
        int Px(float m) => (int)((m + AshfallMap.Half) / g.Cell);
        int Py(float m) => n - 1 - (int)((m + AshfallMap.Half) / g.Cell);
        void Line(Vector2 a, Vector2 b, (byte, byte, byte) c, int thick, bool dashed)
        {
            float len = Vector2.Distance(a, b);
            for (float s = 0f; s <= len; s += g.Cell * 0.5f)
            {
                if (dashed && (int)(s / 6f) % 2 == 1) continue;
                var q = Vector2.Lerp(a, b, len < 1e-3f ? 0f : s / len);
                for (int dy = -thick; dy <= thick; dy++) for (int dx = -thick; dx <= thick; dx++) Put(img, n, Px(q.X) + dx, Py(q.Y) + dy, c);
            }
        }
        // through road edges
        Line(new(-12f, -240f), new(-12f, 240f), (107, 79, 191), 1, true);
        Line(new(12f, -240f), new(12f, 240f), (107, 79, 191), 1, true);
        var (primary, alternate) = Routes(plan);
        foreach (var r in alternate) for (int i = 1; i < r.Path.Length; i++) Line(r.Path[i - 1], r.Path[i], (224, 122, 16), 2, true);
        foreach (var r in primary) for (int i = 1; i < r.Path.Length; i++) Line(r.Path[i - 1], r.Path[i], (28, 79, 208), 3, false);
        // strongholds' outlines
        foreach (var (rect, c) in new[] { (new Vector4(-44f, -224f, 44f, -156f), ((byte)28, (byte)79, (byte)208)), (new Vector4(-84f, 96f, 84f, 226f), ((byte)122, (byte)28, (byte)28)) })
        {
            Line(new(rect.X, rect.Y), new(rect.Z, rect.Y), c, 1, false); Line(new(rect.Z, rect.Y), new(rect.Z, rect.W), c, 1, false);
            Line(new(rect.Z, rect.W), new(rect.X, rect.W), c, 1, false); Line(new(rect.X, rect.W), new(rect.X, rect.Y), c, 1, false);
        }
        foreach (var p in plan.Points)
        {
            float r = p.Radius / g.Cell;
            for (int a = 0; a < 1440; a++) { float t = a * MathF.PI / 720f; for (int d = -2; d <= 2; d++) Put(img, n, (int)(Px(p.Center.X) + (r + d) * MathF.Cos(t)), (int)(Py(p.Center.Y) + (r + d) * MathF.Sin(t)), (220, 30, 30)); }
        }
        return AshfallBattlefieldViews.Png(path, n, n, img);
    }

    static void Put(byte[] img, int w, int x, int y, (byte R, byte G, byte B) c)
    {
        if (x < 0 || y < 0 || x >= w || y >= w) return;
        int i = (y * w + x) * 3; img[i] = c.R; img[i + 1] = c.G; img[i + 2] = c.B;
    }
}
