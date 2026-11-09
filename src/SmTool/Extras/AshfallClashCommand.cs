using System.Globalization;
using System.Numerics;
using System.Text;
using SmSim.Extras.Musou.Ashfall;

namespace SmTool;

/// <summary>
/// <c>smtool ashfall-clash [--layouts city,field,draft10] [--points B,A] [--sizes 200,500,1000] [--ai shipped,adaptive] [--table file.md] [--clips dir]</c> (draft 11): the clash bench
/// (<see cref="AshfallClash"/>) over a matrix of layouts, points, sizes and army AIs; prints the Markdown table (frontage, queue length, stuck units, arrival spread, throughput, retreat). <c>--clips</c> writes each run's
/// top-down frames (PNG, 2 a second, the action's box) under <c>dir/&lt;run&gt;/</c> for ffmpeg. A movement model only (docs/musou-ashfall-graybox.md "Draft 11 battlefield layout": nobody is hurt).
/// </summary>
static class AshfallClashCommand
{
    public static int Run(string[] rest)
    {
        string[] layouts = ["city", "draft10"], points = ["B"], ais = ["shipped"];
        int[] sizes = [200, 500, 1000];
        string? table = null, clips = null;
        for (int i = 0; i < rest.Length; i++)
        {
            string a = rest[i];
            if (i + 1 >= rest.Length) { Console.Error.WriteLine($"ashfall-clash: {a} needs a value"); return 2; }
            string v = rest[++i];
            switch (a)
            {
                case "--layouts": layouts = v.Split(','); break;
                case "--points": points = v.Split(','); break;
                case "--sizes": sizes = [.. v.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))]; break;
                case "--ai": ais = v.Split(','); break;
                case "--table": table = Path.GetFullPath(v); break;
                case "--clips": clips = Path.GetFullPath(v); break;
                default: Console.Error.WriteLine($"ashfall-clash: unknown argument {a}"); return 2;
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine(AshfallClash.Result.Header);
        Console.WriteLine(AshfallClash.Result.Header);
        foreach (var l in layouts)
            foreach (var p in points)
                foreach (int n in sizes)
                    foreach (var ai in ais)
                    {
                        var layout = AshfallLayouts.Parse(l);
                        var setup = new AshfallClash.Setup(layout, p, n, ai == "adaptive" ? AshfallClash.ClashAi.Adaptive : AshfallClash.ClashAi.Shipped);
                        Action<AshfallClash.Snapshot>? frame = null;
                        string? dir = null; int f = 0;
                        if (clips is not null)
                        {
                            dir = Path.Combine(clips, $"{l}-{p}-{n}-{ai}");
                            Directory.CreateDirectory(dir);
                            foreach (var old in Directory.GetFiles(dir, "f*.png")) File.Delete(old);
                            var grid = new AshfallGrid(AshfallMap.Build(layout));
                            var bg = Background(grid, layout);
                            frame = s => Frame(Path.Combine(dir, $"f{f++:0000}.png"), grid, bg, s, $"{(layout switch { AshfallLayout.Field => "Draft 11 battlefield", AshfallLayout.City => "Draft 12 city", _ => "Draft 10" })} - point {p} - {n} a side - {s.Phase} - {s.Time:0} s");
                        }
                        var w = System.Diagnostics.Stopwatch.StartNew();
                        var r = AshfallClash.Run(setup, frame);
                        Console.WriteLine(r.Row + $" <!-- {w.ElapsedMilliseconds} ms -->");
                        sb.AppendLine(r.Row);
                        if (dir is not null) Console.WriteLine($"ashfall-clash: {f} frames -> {dir}");
                    }
        if (table is not null) { Directory.CreateDirectory(Path.GetDirectoryName(table)!); File.WriteAllText(table, sb.ToString(), new UTF8Encoding(false)); Console.WriteLine($"ashfall-clash: table -> {table}"); }
        return 0;
    }

    // ---------------------------------------------------------------- frames: the box 140 m round the point's approaches, 4 px a metre... kept at 720 x 720

    const int Size = 720;
    const float Span = 260f;   // metres across the frame

    static (Vector2 Min, byte[] Rgb) Background(AshfallGrid g, AshfallLayout layout)
    {
        var img = new byte[Size * Size * 3];
        var min = new Vector2(-Span / 2f, -Span / 2f);
        return (min, img);
    }

    static void Frame(string path, AshfallGrid g, (Vector2 Min, byte[] Rgb) bg, AshfallClash.Snapshot s, string title)
    {
        // centre: the middle of everyone (clamped into the square)
        var all = s.Orks.Concat(s.Guards).ToList();
        var c = all.Count == 0 ? Vector2.Zero : all.Aggregate(Vector2.Zero, (a, b) => a + b) / all.Count;
        c = Vector2.Clamp(c, new Vector2(-AshfallMap.Half + Span / 2f), new Vector2(AshfallMap.Half - Span / 2f));
        float mpp = Span / Size;
        var img = new byte[Size * Size * 3];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = new Vector2(c.X - Span / 2f + (x + 0.5f) * mpp, c.Y + Span / 2f - (y + 0.5f) * mpp);
                int k = g.Index(p);
                float top = g.Top[k] - g.GroundAt(p);
                (byte, byte, byte) col = g.Blocked[k] ? Shade(top) : top > 0.3f ? ((byte)170, (byte)160, (byte)150) : ((byte)214, (byte)206, (byte)190);
                int i = (y * Size + x) * 3; img[i] = col.Item1; img[i + 1] = col.Item2; img[i + 2] = col.Item3;
            }
        void Dot(Vector2 p, (byte, byte, byte) col, int r)
        {
            int px = (int)((p.X - (c.X - Span / 2f)) / mpp), py = (int)(((c.Y + Span / 2f) - p.Y) / mpp);
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = px + dx, y = py + dy;
                    if (x < 0 || y < 0 || x >= Size || y >= Size || dx * dx + dy * dy > r * r + 1) continue;
                    int i = (y * Size + x) * 3; img[i] = col.Item1; img[i + 1] = col.Item2; img[i + 2] = col.Item3;
                }
        }
        for (int k = 0; k < s.Guards.Length; k++) Dot(s.Guards[k], s.GuardEngaged[k] ? ((byte)120, (byte)200, (byte)255) : ((byte)30, (byte)70, (byte)200), 1);
        for (int k = 0; k < s.Orks.Length; k++) Dot(s.Orks[k], s.OrkEngaged[k] ? ((byte)255, (byte)170, (byte)60) : ((byte)40, (byte)130, (byte)30), 1);
        Text(img, 6, 6, title);
        Text(img, 6, 20, "GREEN ORKS  BLUE GUARD  ORANGE/CYAN ENGAGED  1 PX = " + mpp.ToString("0.00", CultureInfo.InvariantCulture) + " M");
        AshfallBattlefieldViews.Png(path, Size, Size, img);
    }

    static (byte, byte, byte) Shade(float top) { float t = Math.Clamp(top / 30f, 0f, 1f); return ((byte)(120 - 70 * t), (byte)(96 - 60 * t), (byte)(80 - 50 * t)); }

    // A 3 x 5 pixel font (upper case, digits and a few signs), drawn at 2x, black on a pale box.
    static readonly Dictionary<char, string> Glyphs = new()
    {
        ['A'] = "010101111101101", ['B'] = "110101110101110", ['C'] = "011100100100011", ['D'] = "110101101101110", ['E'] = "111100110100111", ['F'] = "111100110100100", ['G'] = "011100101101011",
        ['H'] = "101101111101101", ['I'] = "111010010010111", ['J'] = "001001001101010", ['K'] = "101101110101101", ['L'] = "100100100100111", ['M'] = "101111111101101", ['N'] = "110101101101101",
        ['O'] = "010101101101010", ['P'] = "110101110100100", ['Q'] = "010101101110011", ['R'] = "110101110101101", ['S'] = "011100010001110", ['T'] = "111010010010010", ['U'] = "101101101101111",
        ['V'] = "101101101101010", ['W'] = "101101111111101", ['X'] = "101101010101101", ['Y'] = "101101010010010", ['Z'] = "111001010100111",
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "110001010100111", ['3'] = "110001010001110", ['4'] = "101101111001001", ['5'] = "111100110001110", ['6'] = "011100111101111",
        ['7'] = "111001010010010", ['8'] = "111101111101111", ['9'] = "111101111001110", ['-'] = "000000111000000", ['.'] = "000000000000010", ['/'] = "001001010100100", [' '] = "000000000000000", ['='] = "000111000111000",
    };

    static void Text(byte[] img, int x0, int y0, string text)
    {
        text = text.ToUpperInvariant();
        int w = text.Length * 8 + 4;
        for (int y = y0 - 2; y < y0 + 12; y++) for (int x = x0 - 2; x < x0 + w; x++) if (x >= 0 && y >= 0 && x < Size && y < Size) { int i = (y * Size + x) * 3; img[i] = 245; img[i + 1] = 240; img[i + 2] = 230; }
        for (int n = 0; n < text.Length; n++)
        {
            if (!Glyphs.TryGetValue(text[n], out var g)) continue;
            for (int r = 0; r < 5; r++)
                for (int col = 0; col < 3; col++)
                    if (g[r * 3 + col] == '1')
                        for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                        {
                            int x = x0 + n * 8 + col * 2 + dx, y = y0 + r * 2 + dy;
                            if (x < Size && y < Size) { int i = (y * Size + x) * 3; img[i] = 20; img[i + 1] = 20; img[i + 2] = 20; }
                        }
        }
    }
}
