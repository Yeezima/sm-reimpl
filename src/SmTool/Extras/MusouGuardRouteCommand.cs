using System.Globalization;
using System.Numerics;
using System.Text;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmTool;

/// <summary>
/// <c>smtool musou-guard-route [--point k] [--seconds s] [--out dir]</c> (test aid, musou/guard-spread 2026-11-08): the field's six Guard squads walk from Ashfall's landing zone to position k (default: each in turn)
/// as <c>TfwBattle.Send</c> sends them, on the generated map's walking graph, no Orks; once with the spread (<see cref="GuardRules.Spread"/>) and once with the old walk. Writes for each a top-down SVG
/// (walkable ground light, cells beside a wall dark, every man's trail in his squad's colour, the end positions as dots, the point's ring) and a log of the numbers: men beside a wall on the way, the mean
/// distance to the nearest man on the move, the slowest man's arrival, men not on the point at the end, and the fire teams there. Nothing is read from the install. Default folder:
/// <c>%LOCALAPPDATA%\sm-reimpl\cache\previews\musou-guard-spread</c>.
/// </summary>
static class MusouGuardRouteCommand
{
    const int Squads = TfwBattleRules.FieldSquads, Hz = 30, SampleTicks = 10;
    static readonly string[] Colours = ["#d62728", "#1f77b4", "#2ca02c", "#9467bd", "#ff7f0e", "#17becf", "#8c564b", "#e377c2"];

    public static int Run(string[] rest)
    {
        int only = -1; float seconds = 90f;
        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "cache", "previews", "musou-guard-spread");
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--point" && i + 1 < rest.Length) only = int.Parse(rest[++i], CultureInfo.InvariantCulture);
            else if (rest[i] == "--seconds" && i + 1 < rest.Length) seconds = float.Parse(rest[++i], CultureInfo.InvariantCulture);
            else if (rest[i] == "--out" && i + 1 < rest.Length) outDir = Path.GetFullPath(rest[++i]);
            else { Console.Error.WriteLine($"musou-guard-route: unknown argument \"{rest[i]}\" (usage: musou-guard-route [--point k] [--seconds s] [--out dir])"); return 2; }
        }
        var data = TfwWorld.Load("", out string? problem) ?? throw new InvalidOperationException(problem);
        var nav = TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out string note);
        Console.WriteLine($"musou-guard-route: Ashfall, {nav.NodeCount} nodes ({note}); landing zone {data.Field.MarineStart}");
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        log.AppendLine($"musou-guard-route {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC; Ashfall {nav.NodeCount} nodes; {Squads} squads from the landing zone {F(data.Field.MarineStart.X)},{F(data.Field.MarineStart.Y)}; {seconds:0} s at {Hz} Hz; no Orks");
        for (int k = 0; k < data.Field.Points.Count; k++)
        {
            if (only >= 0 && k != only) continue;
            foreach (bool spread in new[] { false, true })
            {
                var r = Walk(nav, data.Field.MarineStart, data.Field.Points[k], new GuardRules { Spread = spread }, seconds);
                string name = $"point{k}-{(spread ? "spread" : "old")}";
                File.WriteAllText(Path.Combine(outDir, name + ".svg"), r.Svg, new UTF8Encoding(false));
                File.WriteAllBytes(Path.Combine(outDir, name + ".png"), r.Png);
                string line = $"{name}: beside a wall {r.Beside:P1} of man-samples on the way; mean nearest man on the way {r.Nearest:0.00} m; squad centres across the way {r.Lanes:0.0} m; " +
                              $"last man in at {r.LastIn:0.0} s; {r.Outside} of {r.Men} men off the point at the end; {r.Teams} fire teams at the point (men within 3.5 m chained); step {r.StepMs:0.000} ms a tick mean";
                Console.WriteLine(line);
                log.AppendLine(line);
            }
        }
        File.WriteAllText(Path.Combine(outDir, "route-log.txt"), log.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"musou-guard-route: -> {outDir}");
        return 0;
    }

    readonly record struct Result(string Svg, byte[] Png, float Beside, float Nearest, float Lanes, float LastIn, int Outside, int Men, int Teams, double StepMs);

    static Vector3 Ground(MusouNav walk, Vector3 spot, Vector3 anchor)
    {
        int node = walk.Nearest(spot, 1.5f, 2.5f);
        return node >= 0 ? walk.Position(node) : anchor;
    }

    static Result Walk(NavGraph graph, Vector3 landing, TfwPoint point, GuardRules rules, float seconds)
    {
        var sim = new MusouSim(graph) { TickRate = Hz };
        var walk = sim.Walk!;
        var guards = new GuardSim(sim, rules);
        landing = Ground(walk, landing, landing);
        var at = Ground(walk, point.Centre, point.Centre);
        float yaw = MathF.Atan2(-(at.X - landing.X), at.Y - landing.Y);
        for (int s = 0; s < Squads; s++)
        {
            float a = s * 2.399963f, r = 6f + 1.5f * s;   // TfwBattle.SquadOffset
            guards.SpawnSquad(Ground(walk, landing + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), landing), yaw);
        }
        for (int s = 0; s < Squads; s++)
        {
            float a = s * 2.399963f, r = MathF.Min(6f + 1.5f * s, point.Radius * TfwBattleRules.SquadOnPoint);   // TfwBattle.Send
            guards.Order(s, Ground(walk, at + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), at), at, point.Radius);
        }
        var men = new TfwGuard[GuardSim.Capacity];
        var trails = new List<Vector2>[GuardSim.Capacity];
        for (int i = 0; i < trails.Length; i++) trails[i] = [];
        int samples = 0, beside = 0, nearSamples = 0;
        double nearSum = 0, laneSum = 0, stepMs = 0;
        int laneSamples = 0;
        var inAt = new float[GuardSim.Capacity];
        Array.Fill(inAt, float.NaN);
        int ticks = (int)(seconds * Hz);
        for (int t = 1; t <= ticks; t++)
        {
            guards.BeginTick((uint)t);
            guards.Step(false);
            stepMs += guards.LastStepMilliseconds;
            if (t % SampleTicks != 0) continue;
            int n = guards.CopyTo(men);
            for (int k = 0; k < n; k++)
            {
                var m = men[k];
                trails[m.Id - 1].Add(new Vector2(m.Position.X, m.Position.Y));
                bool on = Vector2.Distance(new Vector2(m.Position.X, m.Position.Y), new Vector2(at.X, at.Y)) <= point.Radius;
                if (on && float.IsNaN(inAt[m.Id - 1])) inAt[m.Id - 1] = t / (float)Hz;
                if (on) continue;
                int node = walk.Nearest(m.Position);
                if (node >= 0) { samples++; if (walk.EdgeDistance(node) < 0.5f) beside++; }
                float best = float.PositiveInfinity;
                for (int j = 0; j < n; j++) if (j != k) best = MathF.Min(best, Vector3.Distance(m.Position, men[j].Position));
                if (float.IsFinite(best)) { nearSum += best; nearSamples++; }
            }
            // Squads' lanes: how far apart the moving squads' centres stand across the way to the point.
            var heading = Vector2.Normalize(new Vector2(at.X - landing.X, at.Y - landing.Y));
            var right = new Vector2(heading.Y, -heading.X);
            var across = Enumerable.Range(0, Squads).Where(s => !guards.Arrived(s) && guards.AliveIn(s) > 0).Select(s => Vector2.Dot(new Vector2(guards.Centre(s).X, guards.Centre(s).Y), right)).ToArray();
            if (across.Length >= 2) { laneSum += across.Max() - across.Min(); laneSamples++; }
        }
        int count = guards.CopyTo(men);
        var final = men.Take(count).ToArray();
        int outside = final.Count(m => Vector2.Distance(new Vector2(m.Position.X, m.Position.Y), new Vector2(at.X, at.Y)) > point.Radius);
        var team = Enumerable.Range(0, final.Length).ToArray();
        int Root(int a) => team[a] == a ? a : team[a] = Root(team[a]);
        for (int a = 0; a < final.Length; a++)
            for (int b = a + 1; b < final.Length; b++)
                if (Vector3.Distance(final[a].Position, final[b].Position) < 3.5f) team[Root(a)] = Root(b);
        int teams = Enumerable.Range(0, final.Length).Select(Root).Distinct().Count();
        float lastIn = inAt.Take(Squads * GuardSim.SquadSize).Any(float.IsNaN) ? float.PositiveInfinity : inAt.Take(Squads * GuardSim.SquadSize).Max();
        string svg = Svg(walk, landing, at, point.Radius, trails, final, rules.Spread);
        byte[] png = Png(walk, landing, at, point.Radius, trails, final);
        return new Result(svg, png, beside / (float)Math.Max(1, samples), (float)(nearSum / Math.Max(1, nearSamples)), (float)(laneSum / Math.Max(1, laneSamples)), lastIn, outside, final.Length, teams, stepMs / ticks);
    }

    /// <summary>The same picture as a PNG (4 px a metre): ground, trails, end dots, the rings; no text.</summary>
    static byte[] Png(MusouNav walk, Vector3 landing, Vector3 at, float radius, List<Vector2>[] trails, TfwGuard[] final)
    {
        var all = trails.SelectMany(t => t).Append(new Vector2(landing.X, landing.Y)).Append(new Vector2(at.X, at.Y)).ToArray();
        float x0 = MathF.Floor(all.Min(p => p.X) - 25f), x1 = MathF.Ceiling(all.Max(p => p.X) + 25f), y0 = MathF.Floor(all.Min(p => p.Y) - 25f), y1 = MathF.Ceiling(all.Max(p => p.Y) + 25f);
        const float scale = 4f;
        int w = (int)((x1 - x0) * scale), h = (int)((y1 - y0) * scale);
        var px = new byte[w * h * 3];
        void Set(int x, int y, uint rgb) { if ((uint)x >= (uint)w || (uint)y >= (uint)h) return; int o = (y * w + x) * 3; px[o] = (byte)(rgb >> 16); px[o + 1] = (byte)(rgb >> 8); px[o + 2] = (byte)rgb; }
        int PX(float x) => (int)((x - x0) * scale);
        int PY(float y) => (int)((y1 - y) * scale);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float wx = x0 + (x + 0.5f) / scale, wy = y1 - (y + 0.5f) / scale;
                int col = walk.ColumnOf(wx, wy);
                int node = col >= 0 ? walk.NodeIn(col, landing.Z, 1000f) : -1;
                Set(x, y, node < 0 ? 0x2B2B2Bu : walk.EdgeDistance(node) < 0.5f ? 0x6D6359u : 0xCFC8B8u);
            }
        void Ring(Vector3 c, float r, uint rgb) { for (int k = 0; k < 720; k++) { float a = k * MathF.PI / 360f; for (int d = -1; d <= 1; d++) Set(PX(c.X + MathF.Cos(a) * r) + d, PY(c.Y + MathF.Sin(a) * r), rgb); } }
        Ring(at, radius, 0xE03030u); Ring(landing, 10f, 0x3060E0u);
        uint Colour(int i) => uint.Parse(Colours[i / GuardSim.SquadSize % Colours.Length][1..], NumberStyles.HexNumber);
        for (int i = 0; i < trails.Length; i++)
            for (int k = 1; k < trails[i].Count; k++)
            {
                var a = trails[i][k - 1]; var b = trails[i][k];
                int steps = Math.Max(1, (int)(Vector2.Distance(a, b) * scale * 2f));
                for (int t = 0; t <= steps; t++) { var q = Vector2.Lerp(a, b, t / (float)steps); Set(PX(q.X), PY(q.Y), Colour(i)); }
            }
        foreach (var m in final)
            for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                if (dx * dx + dy * dy <= 16) Set(PX(m.Position.X) + dx, PY(m.Position.Y) + dy, dx * dx + dy * dy >= 10 ? 0u : Colour(m.Id - 1));
        return RgbPng(px, w, h);
    }

    /// <summary>An RGB PNG of <paramref name="px"/> (3 bytes a pixel, rows top down): no filter, zlib (also musou-guard-vantage's).</summary>
    internal static byte[] RgbPng(byte[] px, int w, int h)
    {
        var raw = new byte[h * (w * 3 + 1)];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zs.Write(raw);
        using var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            o.Write(len);
            var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            o.Write(td);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, Crc(td));
            o.Write(len);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr); Chunk("IDAT", z.ToArray()); Chunk("IEND", []);
        return o.ToArray();
    }

    static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFFu;
    }

    static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static string Svg(MusouNav walk, Vector3 landing, Vector3 at, float radius, List<Vector2>[] trails, TfwGuard[] final, bool spread)
    {
        var all = trails.SelectMany(t => t).Append(new Vector2(landing.X, landing.Y)).Append(new Vector2(at.X, at.Y)).ToArray();
        float x0 = MathF.Floor(all.Min(p => p.X) - 25f), x1 = MathF.Ceiling(all.Max(p => p.X) + 25f), y0 = MathF.Floor(all.Min(p => p.Y) - 25f), y1 = MathF.Ceiling(all.Max(p => p.Y) + 25f);
        const float scale = 4f;   // pixels a metre
        float W = (x1 - x0) * scale, H = (y1 - y0) * scale;
        string X(float x) => F((x - x0) * scale);
        string Y(float y) => F((y1 - y) * scale);
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(W)}\" height=\"{F(H + 40)}\" viewBox=\"0 0 {F(W)} {F(H + 40)}\">");
        sb.Append($"<rect width=\"{F(W)}\" height=\"{F(H + 40)}\" fill=\"#2b2b2b\"/>");
        // The ground at 0.5 m: walkable light, beside a wall or ledge dark (run-length rows). Height ignored: the lowest-wall node of a column decides.
        float cell = walk.CellSize;
        for (float y = y0; y < y1; y += cell)
        {
            int runKind = -1; float runStart = x0;
            for (float x = x0; x <= x1; x += cell)
            {
                int kind = 0;
                int col = walk.ColumnOf(x, y);
                if (col >= 0)
                {
                    int node = walk.NodeIn(col, landing.Z, 1000f);
                    if (node >= 0) kind = walk.EdgeDistance(node) < 0.5f ? 1 : 2;
                }
                if (kind != runKind || x + cell > x1)
                {
                    if (runKind > 0) sb.Append($"<rect x=\"{X(runStart)}\" y=\"{Y(y + cell)}\" width=\"{F((x - runStart) * scale)}\" height=\"{F(cell * scale + 0.5f)}\" fill=\"{(runKind == 1 ? "#6d6359" : "#cfc8b8")}\"/>");
                    runKind = kind; runStart = x;
                }
            }
        }
        sb.Append($"<circle cx=\"{X(at.X)}\" cy=\"{Y(at.Y)}\" r=\"{F(radius * scale)}\" fill=\"none\" stroke=\"#e03030\" stroke-width=\"3\" stroke-dasharray=\"8 6\"/>");
        sb.Append($"<circle cx=\"{X(landing.X)}\" cy=\"{Y(landing.Y)}\" r=\"{F(10f * scale)}\" fill=\"none\" stroke=\"#3060e0\" stroke-width=\"2\" stroke-dasharray=\"4 4\"/>");
        for (int i = 0; i < trails.Length; i++)
        {
            if (trails[i].Count < 2) continue;
            string colour = Colours[i / GuardSim.SquadSize % Colours.Length];
            sb.Append($"<polyline fill=\"none\" stroke=\"{colour}\" stroke-opacity=\"0.75\" stroke-width=\"1.4\" points=\"");
            foreach (var p in trails[i]) sb.Append(X(p.X)).Append(',').Append(Y(p.Y)).Append(' ');
            sb.Append("\"/>");
        }
        foreach (var m in final)
            sb.Append($"<circle cx=\"{X(m.Position.X)}\" cy=\"{Y(m.Position.Y)}\" r=\"3.2\" fill=\"{Colours[(m.Id - 1) / GuardSim.SquadSize % Colours.Length]}\" stroke=\"#000\" stroke-width=\"0.8\"/>");
        sb.Append($"<text x=\"10\" y=\"{F(H + 26)}\" font-family=\"sans-serif\" font-size=\"18\" fill=\"#eee\">Guardsmen, landing zone (blue ring) to the point (red ring): {(spread ? "spread (musou/guard-spread)" : "old walk (GuardRules.Spread = false)")}; trails every {SampleTicks / (float)Hz:0.##} s, dots the end; dark = beside a wall; 1 m = {F(scale)} px</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
