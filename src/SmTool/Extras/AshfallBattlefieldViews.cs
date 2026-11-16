using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using SmSim;
using SmSim.Extras.Musou.Ashfall;

namespace SmTool;

/// <summary>
/// Draft 11's views of a layout (docs/musou-ashfall-graybox.md "Draft 11 battlefield layout"): the plan's overlays (protected areas, routes, width pinch points, assembly zones, objective approaches, sightlines, travel times)
/// as SVG, and raster views as PNG (top-down by height, clearance, height with the points' sightlines). Nothing is read from the install; the files go wherever <c>--views</c> points (never the repository).
/// </summary>
static class AshfallBattlefieldViews
{
    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The overlays as an SVG group in the plan's frame (<paramref name="X"/>, <paramref name="Y"/> map metres to pixels; <paramref name="scale"/> pixels a metre).</summary>
    public static string Overlays(AshfallBattlefield bf, Func<float, float> X, Func<float, float> Y, float scale)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<g id=\"battlefield-overlays\">");
        var plan = bf.Plan;
        if (plan is not null)
        {
            // protected areas (hatched), assembly zones (blue), clear zones (red rings)
            sb.AppendLine("<defs><pattern id=\"hatch\" width=\"8\" height=\"8\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"8\" stroke=\"#7a1c1c\" stroke-width=\"1.5\" stroke-opacity=\"0.35\"/></pattern></defs>");
            foreach (var (name, r) in plan.Protected)
            {
                bool asm = plan.Assembly.Any(a => a.Name == name);
                sb.AppendLine($"<rect x=\"{F(X(r.X))}\" y=\"{F(Y(r.W))}\" width=\"{F((r.Z - r.X) * scale)}\" height=\"{F((r.W - r.Y) * scale)}\" fill=\"{(asm ? "#2a6fdb" : "url(#hatch)")}\" fill-opacity=\"{(asm ? "0.16" : "1")}\" stroke=\"{(asm ? "#2a6fdb" : "#7a1c1c")}\" stroke-width=\"1.5\" stroke-dasharray=\"6 3\"/>");
                sb.AppendLine($"<text x=\"{F(X((r.X + r.Z) / 2))}\" y=\"{F(Y(r.W) + 12)}\" font-size=\"10\" text-anchor=\"middle\" fill=\"{(asm ? "#1c4fd0" : "#7a1c1c")}\">{(asm ? "assembly: " : "protected: ")}{name}</text>");
            }
            foreach (var (name, c, rad) in plan.ClearZones)
                sb.AppendLine($"<circle cx=\"{F(X(c.X))}\" cy=\"{F(Y(c.Y))}\" r=\"{F(rad * scale)}\" fill=\"none\" stroke=\"#c01818\" stroke-width=\"1.2\" stroke-dasharray=\"4 3\"/>");
            // corridors (the reserved army routes)
            foreach (var k in plan.Corridors)
            {
                var b = k.Box(); var ax = b.AxisX; var ay = b.AxisY; float hx = b.Length / 2f, hy = b.Depth / 2f;
                var pts = new[] { (-hx, -hy), (hx, -hy), (hx, hy), (-hx, hy) }.Select(q => new Vector2(b.Cx, b.Cy) + ax * q.Item1 + ay * q.Item2);
                sb.AppendLine($"<polygon points=\"{string.Join(' ', pts.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}"))}\" fill=\"#f2c14e\" fill-opacity=\"0.14\" stroke=\"#c79a1e\" stroke-width=\"1\"/>");
            }
            // districts (labels)
            foreach (var d in plan.Districts)
            {
                var r = d.Rects[0];
                sb.AppendLine($"<text x=\"{F(X((r.X + r.Z) / 2))}\" y=\"{F(Y((r.Y + r.W) / 2))}\" font-size=\"15\" font-style=\"italic\" text-anchor=\"middle\" fill=\"#333\" fill-opacity=\"0.65\">{d.Name}</text>");
            }
        }
        // routes: the plan's routes (or draft 10's lanes) as lines, the widest army ways between places dashed
        IEnumerable<(string Name, Vector2[] Path)> routes = plan is not null ? plan.Routes.Select(r => (r.Name, r.Path)) : AshfallLanes.Routes;
        foreach (var (_, path) in routes)
            sb.AppendLine($"<polyline points=\"{string.Join(' ', path.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}"))}\" fill=\"none\" stroke=\"#1d7a3a\" stroke-width=\"2\" stroke-opacity=\"0.55\"/>");
        foreach (var l in bf.Links)
        {
            var a = bf.Places.First(p => p.Name == l.From).At; var b = bf.Places.First(p => p.Name == l.To).At;
            var (_, _, path) = bf.Grid.Widest(a, b);
            var pts = path.Where((_, i) => i % 12 == 0).Select(bf.Grid.At).ToList();
            sb.AppendLine($"<polyline points=\"{string.Join(' ', pts.Select(p => $"{F(X(p.X))},{F(Y(p.Y))}"))}\" fill=\"none\" stroke=\"{(l.Width >= bf.MinArmyWidth ? "#0a5" : "#d01")}\" stroke-width=\"1.2\" stroke-dasharray=\"5 4\"/>");
            // pinch point of the link, labelled with its width
            sb.AppendLine($"<circle cx=\"{F(X(l.Pinch.X))}\" cy=\"{F(Y(l.Pinch.Y))}\" r=\"5\" fill=\"{(l.Width >= bf.MinArmyWidth ? "#0a5" : "#d01")}\" fill-opacity=\"0.8\"/><text x=\"{F(X(l.Pinch.X) + 7)}\" y=\"{F(Y(l.Pinch.Y) + 4)}\" font-size=\"10\" font-weight=\"bold\" fill=\"#000\">{l.Width:0.0} m</text>");
            // travel time at the link's middle
            var mid = pts.Count > 0 ? pts[pts.Count / 2] : (a + b) / 2f;
            string tt = l.ArmyLength is { } al ? $"{l.From}-{l.To}: {al:0} m, Guard {l.GuardSeconds:0} s, Ork {l.OrkSeconds:0} s, run {l.PlayerSeconds:0} s" : $"{l.From}-{l.To}: no army route; run {l.PlayerSeconds:0} s";
            sb.AppendLine($"<text x=\"{F(X(mid.X))}\" y=\"{F(Y(mid.Y) - 6)}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#0b3d1e\" stroke=\"#fff\" stroke-width=\"2.5\" paint-order=\"stroke\">{tt}</text>");
        }
        // route pinch points along the plan's routes (perpendicular width under the army width plus 2 m)
        foreach (var r in bf.RouteWidths)
            foreach (var (at, w) in r.Pinches)
                sb.AppendLine($"<rect x=\"{F(X(at.X) - 4)}\" y=\"{F(Y(at.Y) - 4)}\" width=\"8\" height=\"8\" fill=\"{(w >= bf.MinArmyWidth ? "#f0a000" : "#d01")}\" stroke=\"#000\" stroke-width=\"0.6\"><title>{r.Route}: {w:0.0} m</title></rect>");
        // approaches: arrows into each point at the measured bearings
        foreach (var a in bf.Approaches)
        {
            var p = bf.Level.Points.First(q => q.Name == a.Point);
            float rr = p.Radius + (plan?.Params.ObjectiveClear ?? 16f) + 6f;
            foreach (float deg in a.Bearings)
            {
                float rad = deg * MathF.PI / 180f; var dir = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
                var outer = new Vector2(p.Center.X, p.Center.Y) + dir * (rr + 26f); var inner = new Vector2(p.Center.X, p.Center.Y) + dir * (rr - 4f);
                sb.AppendLine($"<line x1=\"{F(X(outer.X))}\" y1=\"{F(Y(outer.Y))}\" x2=\"{F(X(inner.X))}\" y2=\"{F(Y(inner.Y))}\" stroke=\"#6a1b9a\" stroke-width=\"4\" marker-end=\"url(#arrow)\"/>");
            }
            sb.AppendLine($"<text x=\"{F(X(p.Center.X))}\" y=\"{F(Y(p.Center.Y) - rr * scale - 8)}\" font-size=\"11\" font-weight=\"bold\" text-anchor=\"middle\" fill=\"#6a1b9a\">{a.Count} approaches</text>");
        }
        sb.AppendLine("<defs><marker id=\"arrow\" markerWidth=\"6\" markerHeight=\"6\" refX=\"3\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L6,3 L0,6 z\" fill=\"#6a1b9a\"/></marker></defs>");
        // sightlines: point to point (green seen, red hidden), landmarks to the points that see them
        foreach (var (a, b, sees) in bf.PointSight)
        {
            var pa = bf.Level.Points.First(p => p.Name == a).Center; var pb = bf.Level.Points.First(p => p.Name == b).Center;
            sb.AppendLine($"<line x1=\"{F(X(pa.X))}\" y1=\"{F(Y(pa.Y))}\" x2=\"{F(X(pb.X))}\" y2=\"{F(Y(pb.Y))}\" stroke=\"{(sees ? "#13a538" : "#d01")}\" stroke-width=\"1\" stroke-dasharray=\"2 3\"/>");
        }
        foreach (var (name, top, from, _) in bf.Landmarks)
        {
            sb.AppendLine($"<circle cx=\"{F(X(top.X))}\" cy=\"{F(Y(top.Y))}\" r=\"7\" fill=\"none\" stroke=\"#0b6fa4\" stroke-width=\"2.5\"/><text x=\"{F(X(top.X))}\" y=\"{F(Y(top.Y) - 10)}\" font-size=\"10\" text-anchor=\"middle\" fill=\"#0b6fa4\">{name} ({top.Z:0} m)</text>");
            foreach (char c in from)
            {
                var p = bf.Level.Points.First(q => q.Name == c.ToString()).Center;
                sb.AppendLine($"<line x1=\"{F(X(p.X))}\" y1=\"{F(Y(p.Y))}\" x2=\"{F(X(top.X))}\" y2=\"{F(Y(top.Y))}\" stroke=\"#0b6fa4\" stroke-width=\"0.8\" stroke-opacity=\"0.6\"/>");
            }
        }
        foreach (var h in bf.Heights)
            sb.AppendLine($"<text x=\"{F(X(h.Eye.X))}\" y=\"{F(Y(h.Eye.Y) + 3)}\" font-size=\"9\" font-weight=\"bold\" text-anchor=\"middle\" fill=\"#fff\" stroke=\"#0b6fa4\" stroke-width=\"2\" paint-order=\"stroke\">{(h.Seen.Length > 0 ? h.Seen : "-")}</text>");
        // failed constraints in a box
        var failed = bf.Constraints.Where(c => !c.Passed).ToList();
        float ty = Y(238f);
        sb.AppendLine($"<text x=\"{F(X(-236f))}\" y=\"{F(ty + 12)}\" font-size=\"13\" font-weight=\"bold\" fill=\"{(failed.Count == 0 ? "#0a5" : "#d01")}\">{(failed.Count == 0 ? $"all {bf.Constraints.Count} constraints pass" : $"{failed.Count} of {bf.Constraints.Count} constraints FAIL")}</text>");
        int row = 0;
        foreach (var c in failed.Take(18))
            sb.AppendLine($"<text x=\"{F(X(-236f))}\" y=\"{F(ty + 26 + 11 * row++)}\" font-size=\"9\" fill=\"#d01\">FAIL {System.Security.SecurityElement.Escape(c.Name)}: {System.Security.SecurityElement.Escape(c.Value)}</text>");
        sb.AppendLine("</g>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- raster views (PNG)

    /// <summary>Writes the raster views of a layout into <paramref name="dir"/> with <paramref name="prefix"/>: top-down by height, clearance, height with sightlines from the points.</summary>
    public static List<string> Rasters(AshfallBattlefield bf, string dir, string prefix)
    {
        Directory.CreateDirectory(dir);
        var g = bf.Grid; int n = g.N, step = 1;   // one pixel a cell (0.5 m)
        var files = new List<string>();
        int w = n / step;
        // 1. top-down: ground warm grey, solids by top height (darker higher), points red, corridors tinted
        var img = new byte[w * w * 3];
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                int k = (n - 1 - y * step) * n + x * step;
                float top = g.Top[k] - g.GroundAt(g.At(k));
                (byte r, byte gg, byte b) c = top < 0.3f ? ((byte)206, (byte)198, (byte)182) : Shade(top);
                if (g.Blocked[k] && top < 0.3f) c = (90, 80, 70);
                Put(img, w, x, y, c);
            }
        Mark(bf, img, w, step);
        files.Add(Png(Path.Combine(dir, prefix + "topdown.png"), w, w, img));
        // 2. clearance: blocked black; free cells by clearance, the army's half width (10 m) as the turn from amber to green
        img = new byte[w * w * 3];
        float half = bf.MinArmyWidth / 2f;
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                int k = (n - 1 - y * step) * n + x * step;
                float c = g.Clearance[k];
                (byte, byte, byte) col = g.Blocked[k] ? ((byte)20, (byte)20, (byte)24)
                    : c < 0.45f ? ((byte)120, (byte)20, (byte)20)                                     // not even one body
                    : c < half ? Lerp((200, 60, 30), (240, 190, 60), c / half)                          // a body, not a 20 m front
                    : Lerp((120, 200, 110), (30, 120, 60), MathF.Min(1f, (c - half) / 20f));           // a 20 m front fits
                Put(img, w, x, y, col);
            }
        Mark(bf, img, w, step);
        files.Add(Png(Path.Combine(dir, prefix + "clearance.png"), w, w, img));
        // 3. height and sightlines: height shaded; cells seen from any point (eye 1.6 m, a man's 2 m target) tinted blue
        img = new byte[w * w * 3];
        var eyes = bf.Level.Points.Select(p => new Vector3(p.Center.X, p.Center.Y, g.GroundAt(new(p.Center.X, p.Center.Y)) + AshfallBattlefield.Eye)).ToList();
        int vstep = 4;   // the viewshed on a 2 m lattice (the cost), filled per block
        for (int y = 0; y < w; y += vstep)
            for (int x = 0; x < w; x += vstep)
            {
                int k = (n - 1 - y * step) * n + x * step;
                var at = g.At(k);
                int seen = 0;
                foreach (var e in eyes) if (Vector2.Distance(new(e.X, e.Y), at) < 260f && g.Sees(e, new Vector3(at.X, at.Y, g.GroundAt(at) + 1f))) seen++;
                for (int yy = y; yy < Math.Min(w, y + vstep); yy++)
                    for (int xx = x; xx < Math.Min(w, x + vstep); xx++)
                    {
                        int kk = (n - 1 - yy * step) * n + xx * step;
                        float top = g.Top[kk] - g.GroundAt(g.At(kk));
                        var c = top < 0.3f ? ((byte)200, (byte)196, (byte)186) : Shade(top);
                        if (seen > 0 && top < 0.3f) c = Lerp(c, (60, 120, 220), 0.25f + 0.15f * seen);
                        Put(img, w, xx, yy, c);
                    }
            }
        Mark(bf, img, w, step);
        files.Add(Png(Path.Combine(dir, prefix + "height-sightlines.png"), w, w, img));
        return files;
    }

    static (byte, byte, byte) Shade(float top)
    {
        float t = Math.Clamp(top / 30f, 0f, 1f);
        return Lerp((176, 140, 110), (60, 40, 40), t);
    }

    static (byte, byte, byte) Lerp((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return ((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    static void Put(byte[] img, int w, int x, int y, (byte R, byte G, byte B) c)
    {
        if (x < 0 || y < 0 || x >= w || y >= w) return;
        int i = (y * w + x) * 3; img[i] = c.R; img[i + 1] = c.G; img[i + 2] = c.B;
    }

    /// <summary>The points (red rings) and the bases' nodes (blue squares) on a raster.</summary>
    static void Mark(AshfallBattlefield bf, byte[] img, int w, int step)
    {
        float px(float m) => (m + AshfallMap.Half) / bf.Grid.Cell / step;
        foreach (var p in bf.Level.Points)
        {
            float cx = px(p.Center.X), cy = w - 1 - px(p.Center.Y), r = p.Radius / bf.Grid.Cell / step;
            for (int a = 0; a < 720; a++) { float t = a * MathF.PI / 360f; for (float d = -1f; d <= 1f; d += 1f) Put(img, w, (int)(cx + (r + d) * MathF.Cos(t)), (int)(cy + (r + d) * MathF.Sin(t)), (220, 30, 30)); }
        }
        foreach (var b in bf.Places.Where(q => q.Base))
        {
            float cx = px(b.At.X), cy = w - 1 - px(b.At.Y);
            for (int dy = -6; dy <= 6; dy++) for (int dx = -6; dx <= 6; dx++) if (Math.Abs(dx) == 6 || Math.Abs(dy) == 6) Put(img, w, (int)cx + dx, (int)cy + dy, (30, 60, 220));
        }
    }

    /// <summary>A PNG (RGB, 8 bits, no filter) written with the framework's zlib; returns the path.</summary>
    public static string Png(string path, int w, int h, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgb, y * w * 3, w * 3); }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
        return path;
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var t = Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        var c = new byte[4]; BE(c, 0, (int)crc); s.Write(c);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();

    static uint Crc(uint c, byte[] b) { foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8); return c; }
}
