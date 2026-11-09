using System.Globalization;
using System.Numerics;
using System.Text;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmTool;

/// <summary>
/// <c>smtool musou-guard-vantage [--secure k|none] [--seconds s] [--out dir]</c> (test aid, musou/guard-vantage 2026-11-09): the Guardsmen's vantage points on Ashfall's generated map (<see cref="GuardVantage"/>), and a
/// battle on its walking graph with position k secured (default 1; <c>none</c>: no point, the base's front), the field's squads and their vantage teams (sent every 5 to 8 s here, so a short run shows several),
/// frozen Orks, nobody fighting. Writes <c>overview.png</c> (the whole map, 2 px a metre: walkable ground shaded by height, the vantage points as orange dots, 50 m rings round the points in red, the base's
/// 50 and 100 m in blue, each team's trail and its men at the end), <c>closeup.png</c> (4 px a metre round the secured point or the base) and <c>vantage-log.txt</c>. Nothing is read from the install.
/// Default folder: <c>%LOCALAPPDATA%\sm-reimpl\cache\previews\musou-guard-vantage</c>.
/// </summary>
static class MusouGuardVantageCommand
{
    const int Hz = 30, SampleTicks = 10;
    static readonly uint[] Colours = [0xD62728, 0x1F77B4, 0x2CA02C, 0x9467BD, 0xFF7F0E, 0x17BECF, 0x8C564B, 0xE377C2];

    public static int Run(string[] rest)
    {
        int secure = 1; float seconds = 150f;
        string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "cache", "previews", "musou-guard-vantage");
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--secure" && i + 1 < rest.Length) { string v = rest[++i]; secure = v == "none" ? -1 : int.Parse(v, CultureInfo.InvariantCulture); }
            else if (rest[i] == "--seconds" && i + 1 < rest.Length) seconds = float.Parse(rest[++i], CultureInfo.InvariantCulture);
            else if (rest[i] == "--out" && i + 1 < rest.Length) outDir = Path.GetFullPath(rest[++i]);
            else { Console.Error.WriteLine($"musou-guard-vantage: unknown argument \"{rest[i]}\" (usage: musou-guard-vantage [--secure k|none] [--seconds s] [--out dir])"); return 2; }
        }
        var data = TfwWorld.Load("", out string? problem) ?? throw new InvalidOperationException(problem);
        var field = data.Field;
        var nav = TfwWorld.Nav(data.Collision, field, new SimConfig(), out string note);
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        void Say(string line) { Console.WriteLine(line); log.AppendLine(line); }
        Say($"musou-guard-vantage {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC; Ashfall {nav.NodeCount} nodes ({note}); base {F(field.MarineStart.X)},{F(field.MarineStart.Y)}; secured {(secure < 0 ? "none" : secure.ToString(CultureInfo.InvariantCulture))}");

        var sim = new MusouSim(nav) { TickRate = Hz, Frozen = true };
        var walk = sim.Walk!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var spots = GuardVantage.Find(walk, data.Collision.Weapons, walk.Nearest(field.MarineStart, 4f, 4f), GuardVantageRules.Default);
        Say($"finder: {watch.ElapsedMilliseconds} ms, {GuardVantage.LastCandidates} candidates, {GuardVantage.LastRays} rays, {spots.Length} vantage points");
        for (int k = 0; k < field.Points.Count; k++)
            Say($"  point {k} at {F(field.Points[k].Centre.X)},{F(field.Points[k].Centre.Y)}: {spots.Count(v => Flat(v.Position, field.Points[k].Centre) <= GuardVantageRules.Default.PointRadius)} within 50 m");

        var guards = new GuardSim(sim) { Sight = data.Collision };
        var centres = field.Points.Select(p => p.Centre).ToArray();
        var layout = new TfwLayout(field.MarineStart, centres, field.Commander, [.. centres, field.Commander], field.Points.Select(p => p.Radius).ToArray(), field.Points.Select(p => p.HalfHeight).ToArray());
        var battle = new TfwBattle(sim, guards, layout, 100.0, 1, false, default, TfwBattleRules.EliteBase, guardsmen: true)
        {
            Trickle = false, Patrols = false, Armies = false, BossOrksOn = false, StormboysOn = false, MeksOn = false, WeirdboyzOn = false, BigShootasOn = false, RokkitsOn = false, VictoryWave = false,
            VantageRules = GuardVantageRules.Default with { IntervalMin = 5f, IntervalMax = 8f },
        };
        battle.Start();
        if (secure >= 0) battle.ClearAndSecureForTests(secure);
        Say($"battle: base radius {battle.VantageBaseRadius:0} m; vantage teams every 5 to 8 s for the picture (the game: 20 to 45 s)");
        Vector3[] nobody = [field.MarineStart + new Vector3(0f, -500f, 0f)];
        var men = new TfwGuard[GuardSim.Capacity];
        var trails = new Dictionary<int, List<Vector2>>();
        int ticks = (int)(seconds * Hz);
        double stepMs = 0;
        for (int t = 1; t <= ticks; t++)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            battle.Step(nobody);
            stepMs += w.Elapsed.TotalMilliseconds;
            guards.BeginTick((uint)t);
            guards.Step(false);
            if (t % SampleTicks != 0) continue;
            var teams = battle.VantageTeamsForTests();
            int n = guards.CopyTo(men);
            for (int k = 0; k < n; k++)
            {
                int squad = (men[k].Id - 1) / GuardSim.SquadSize, team = Array.FindIndex(teams, x => x.Squad == squad);
                if (team < 0) continue;
                int key = men[k].Id * 1000 + teams[team].Spot;   // a slot reused by a later team draws a new trail
                if (!trails.TryGetValue(key, out var trail)) trails[key] = trail = [];
                trail.Add(new Vector2(men[k].Position.X, men[k].Position.Y));
            }
        }
        var end = battle.VantageTeamsForTests();
        int count = guards.CopyTo(men);
        var final = men.Take(count).Where(m => end.Any(x => x.Squad == (m.Id - 1) / GuardSim.SquadSize)).ToArray();
        Say($"after {seconds:0} s: {battle.VantageTeamsSent} teams sent, {end.Length} out; battle step {stepMs / ticks:0.000} ms a tick mean");
        foreach (var x in end)
        {
            var v = battle.VantagePoints[x.Spot];
            int up = final.Count(m => (m.Id - 1) / GuardSim.SquadSize == x.Squad && MathF.Abs(m.Position.Z - v.Position.Z) < 0.6f && Flat(m.Position, v.Position) < 4f);
            Say($"  team {x.Squad}: {(x.Holding ? "holding" : x.Returning ? "returning" : "going")} spot ({F(v.Position.X)}, {F(v.Position.Y)}, {F(v.Position.Z)}) height {v.Height:0.0} open {v.Open:0.00}; {up} of {guards.AliveIn(x.Squad)} men on it");
        }

        var all = Enumerable.Range(0, walk.NodeCount).Select(i => new Vector2(walk.X(i), walk.Y(i)));
        float x0 = all.Min(p => p.X) - 10f, x1 = all.Max(p => p.X) + 10f, y0 = all.Min(p => p.Y) - 10f, y1 = all.Max(p => p.Y) + 10f;
        File.WriteAllBytes(Path.Combine(outDir, "overview.png"), Draw(walk, spots, layout, battle, trails.Values, final, x0, x1, y0, y1, 2f, secure));
        var c = secure >= 0 ? centres[secure] : field.MarineStart + new Vector3(0f, 50f * MathF.Sign(field.Commander.Y - field.MarineStart.Y), 0f);
        File.WriteAllBytes(Path.Combine(outDir, "closeup.png"), Draw(walk, spots, layout, battle, trails.Values, final, c.X - 65f, c.X + 65f, c.Y - 65f, c.Y + 65f, 5f, secure));
        File.WriteAllText(Path.Combine(outDir, "vantage-log.txt"), log.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"musou-guard-vantage: -> {outDir}");
        return 0;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();
    static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    static byte[] Draw(MusouNav walk, GuardVantagePoint[] spots, TfwLayout layout, TfwBattle battle, IEnumerable<List<Vector2>> trails, TfwGuard[] final, float x0, float x1, float y0, float y1, float scale, int secure)
    {
        int w = (int)((x1 - x0) * scale), h = (int)((y1 - y0) * scale);
        var px = new byte[w * h * 3];
        void Set(int x, int y, uint rgb) { if ((uint)x >= (uint)w || (uint)y >= (uint)h) return; int o = (y * w + x) * 3; px[o] = (byte)(rgb >> 16); px[o + 1] = (byte)(rgb >> 8); px[o + 2] = (byte)rgb; }
        int PX(float x) => (int)((x - x0) * scale);
        int PY(float y) => (int)((y1 - y) * scale);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int col = walk.ColumnOf(x0 + (x + 0.5f) / scale, y1 - (y + 0.5f) / scale);
                int node = col >= 0 ? walk.NodeIn(col, 100f, 1000f) : -1;   // the highest floor
                if (node < 0) { Set(x, y, 0x262626u); continue; }
                float z = Math.Clamp(walk.Z(node), 0f, 8f) / 8f;
                uint g = (uint)(150 + 95 * z);
                Set(x, y, walk.EdgeDistance(node) < 0.5f ? 0x6D6359u : (g << 16) | ((uint)(g * 0.95f) << 8) | (uint)(g * 0.85f));
            }
        void Ring(Vector3 c, float r, uint rgb) { int steps = (int)(r * scale * 8f); for (int k = 0; k < steps; k++) { float a = k * 2f * MathF.PI / steps; Set(PX(c.X + MathF.Cos(a) * r), PY(c.Y + MathF.Sin(a) * r), rgb); Set(PX(c.X + MathF.Cos(a) * r) + 1, PY(c.Y + MathF.Sin(a) * r), rgb); } }
        void Dot(Vector2 p, int r, uint rgb, uint edge) { for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++) if (dx * dx + dy * dy <= r * r) Set(PX(p.X) + dx, PY(p.Y) + dy, dx * dx + dy * dy >= (r - 1) * (r - 1) ? edge : rgb); }
        for (int k = 0; k < layout.Positions.Length; k++)
        {
            Ring(layout.Positions[k], layout.Radius(k), k == secure ? 0x2060FFu : 0xE03030u);
            Ring(layout.Positions[k], GuardVantageRules.Default.PointRadius, k == secure ? 0x2060FFu : 0xE03030u);
        }
        Ring(layout.LandingZone, GuardVantageRules.Default.BaseRadius, 0x3060E0u);
        Ring(layout.LandingZone, battle.VantageBaseRadius, 0x3060E0u);
        int ci = 0;
        foreach (var trail in trails)
        {
            uint colour = Colours[ci++ / 6 % Colours.Length];
            for (int k = 1; k < trail.Count; k++)
            {
                int steps = Math.Max(1, (int)(Vector2.Distance(trail[k - 1], trail[k]) * scale * 2f));
                for (int t = 0; t <= steps; t++) { var q = Vector2.Lerp(trail[k - 1], trail[k], t / (float)steps); Set(PX(q.X), PY(q.Y), colour); }
            }
        }
        int dot = Math.Max(3, (int)(scale * 1.2f));
        foreach (var v in spots) Dot(new Vector2(v.Position.X, v.Position.Y), dot, 0xFF9900u, 0x000000u);
        foreach (var m in final) Dot(new Vector2(m.Position.X, m.Position.Y), Math.Max(2, (int)(scale * 0.6f)), 0x00C040u, 0x003010u);
        return MusouGuardRouteCommand.RgbPng(px, w, h);
    }
}
