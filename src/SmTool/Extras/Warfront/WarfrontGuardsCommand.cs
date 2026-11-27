using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Faction;
using SmSim.Extras.Warfront.Maps;

namespace SmTool;

/// <summary>
/// <c>smtool warfront-guards [--slot wf_a1] [--seconds 240] [--frames file.rgb]</c> (Guardsmen depth, owner 2026-11-20): a Warfront lane battle in-process on the slot's real map and walking graph (a cold graph build is 100 s or more;
/// the private cache keeps it), no server and no Godot. A scripted human secures A, B and C (the Orks kept dead, as the test rig does), then stands off at the landing and the battle plays on for <c>--seconds</c> of simulated time
/// (capped at 900): the Orks' crowds and waves come at the Guardsmen on their points. Prints the Guardsmen and squads standing, the Orks, the formation's squads on each point and the tick cost (mean, p50, p95, p99, worst
/// of every <c>World.Step</c> after the points are secured) with the Guardsmen's own section. <c>--frames file.rgb</c>: raw 720 x 720 RGB frames of a top-down view of a point (<c>--frame-point</c>, default A) every 0.5 s of the first 120 s, for ffmpeg (<c>-f rawvideo -pix_fmt rgb24 -s 720x720 -framerate 4</c>). <c>SM_WF_GUARD_OLD=1</c> (environment) restores the Guardsmen as they were before
/// the change (numbers, strength, no rows) for the before row. In-process, loopback-free: no socket is opened; nothing is read from the install.
/// </summary>
static class WarfrontGuardsCommand
{
    public static int Run(string[] rest)
    {
        string slotName = "wf_a1", frames = "", scenario = ""; int frameEvery = 15, framePoint = 0, frameSeconds = 0; int seconds = 240, target = 0;
        var seeds = new List<int> { 1 }; bool json = false, chaos = false, survey = false;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--slot" when i + 1 < rest.Length: slotName = rest[++i]; break;
                case "--seconds" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int s): seconds = Math.Clamp(s, 10, 900); i++; break;
                case "--frames" when i + 1 < rest.Length: frames = Path.GetFullPath(rest[++i]); break;
                case "--frame-point" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int fp): framePoint = Math.Clamp(fp, 0, 2); i++; break;
                case "--frame-every" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int fe): frameEvery = Math.Clamp(fe, 1, 300); i++; break;
                case "--frame-seconds" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int fsec): frameSeconds = Math.Clamp(fsec, 1, 900); i++; break;
                case "--scenario" when i + 1 < rest.Length: scenario = rest[++i]; if (scenario != "all" && Array.IndexOf(WarfrontLineTrial.Scenarios, scenario) < 0) { Console.Error.WriteLine("warfront-guards: --scenario routine|elite|flank|collapse|break|all"); return 2; } break;
                case "--seed" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int sd): seeds = [sd]; i++; break;
                case "--seeds" when i + 1 < rest.Length: seeds = [.. rest[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture))]; break;
                case "--target" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int tg): target = Math.Clamp(tg, 0, 2); i++; break;
                case "--json": json = true; break;
                case "--faction" when i + 1 < rest.Length: chaos = rest[++i] == "chaos"; break;
                case "--survey": survey = true; break;
                case "--trickle": WarfrontLineTrial.TrickleOn = true; break;
                case "--hq-tide": WarfrontLineTrial.QuietHq = false; break;
                default: Console.Error.WriteLine($"warfront-guards: unknown argument \"{rest[i]}\" (usage: warfront-guards [--slot wf_a1] [--seconds 240] [--frames file.rgb] [--frame-point 0..2] [--frame-every ticks] [--frame-seconds s] [--scenario routine|elite|flank|collapse|break|all] [--seed n | --seeds a,b,c] [--target 0..2] [--faction orks|chaos] [--json] [--survey] [--trickle] [--hq-tide])"); return 2;
            }
        }
        var total = Stopwatch.StartNew();
        if (survey) return Survey(chaos, json, 12);
        if (!Boot(slotName, chaos, total, out var w, out var battle, out var guards, out int code)) return code;
        if (scenario.Length > 0) return Scenarios(w, slotName, chaos, scenario == "all" ? WarfrontLineTrial.Scenarios : [scenario], seeds, target, seconds, json, total, frames, frameEvery, frameSeconds > 0 ? frameSeconds : 180);

        var all = new MusouEnemy[MusouSim.Capacity];
        for (int p = 0; p < 3; p++)
        {
            w.TfwRespawnAt(1, battle.Layout.Positions[p], 0f);
            for (int t = 0; t < 120 * 30 && battle.Objectives[p].State != TfwPositionState.Secured; t++)
            {
                int n = w.Musou!.CopyTo(all);
                for (int k = 0; k < n; k++) if (all[k].Alive) w.Musou.Damage(all[k].Id, 1, 1e6f, Vector3.Zero, out _);
                w.Step(_ => default);
            }
            Console.WriteLine($"point {"ABC"[p]}: {battle.Objectives[p].State}");
        }
        w.TfwRespawnAt(1, battle.Layout.LandingZone, 0f);
        var steps = new List<double>(); var guardMs = new List<double>();
        SimProfile.On = true;
        var snaps = new List<(int T, string Line)>();
        using var frameFile = frames.Length > 0 ? File.Create(frames) : null;
        int ticks = seconds * 30, window = 0, live = 0; long liveGuards = 0, liveOrks = 0; double winSum = 0, winMax = 0;
        for (int t = 1; t <= ticks && !battle.Won; t++)
        {
            long start = Stopwatch.GetTimestamp();
            w.Step(_ => default);
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (guards.Alive > 0) { steps.Add(ms); live++; liveGuards += guards.Alive; liveOrks += w.Musou!.Alive; }   // the cost counts while any Guardsman stands
            SimProfile.EndTick();
            winSum += ms; winMax = Math.Max(winMax, ms); window++;
            if (t % 300 == 0)
            {
                var rep = SimProfile.WindowReport();
                if (Environment.GetEnvironmentVariable("SM_WF_GUARD_SECTIONS") == "1") Console.WriteLine("    sections (mean/worst ms a tick): " + rep);
                int g = rep.IndexOf("tfw: guards ", StringComparison.Ordinal);
                if (g >= 0 && guards.Alive > 0) { var part = rep[(g + 12)..].Split('/')[0]; if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double gm)) guardMs.Add(gm); }
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  t {t / 30,4} s: Guardsmen {guards.Alive,3} in {guards.SquadsAlive,2} squads, Orks {w.Musou!.Alive,4}, tick {winSum / window:0.00} ms mean / {winMax:0.0} max, formation orders {battle.WarfrontFormationOrders}"));
                window = 0; winSum = 0; winMax = 0;
            }
            if (Environment.GetEnvironmentVariable("SM_WF_GUARD_SLOTS") == "1" && t % 300 == 0 && t <= 2400)
                for (int pt = 0; pt < 3; pt++)
                {
                    var a2 = battle.WarfrontApproach(pt);
                    var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"    t {t / 30} s point {"ABC"[pt]} approach ({a2.X:0.00},{a2.Y:0.00}) centre ({battle.Layout.Positions[pt].X:0},{battle.Layout.Positions[pt].Y:0}):"));
                    for (int q = 0; q < GuardSim.MaxSquads; q++)
                        if (guards.AliveIn(q) > 0 && (battle.SquadPost(q) == pt || (Environment.GetEnvironmentVariable("SM_WF_GUARD_SLOTS_NEAR") == "1" && new Vector2(guards.Centre(q).X - battle.Layout.Positions[pt].X, guards.Centre(q).Y - battle.Layout.Positions[pt].Y).Length() < 30f))) { var sl = battle.WarfrontSlotOf(q); var cq = guards.Centre(q); line.Append(string.Create(CultureInfo.InvariantCulture, $" [s{q} {guards.AliveIn(q)}m slot ({sl.X - battle.Layout.Positions[pt].X:0},{sl.Y - battle.Layout.Positions[pt].Y:0}) at ({cq.X - battle.Layout.Positions[pt].X:0},{cq.Y - battle.Layout.Positions[pt].Y:0})]")); }
                    Console.WriteLine(line);
                }
            if (Environment.GetEnvironmentVariable("SM_WF_GUARD_MEN") == "1" && (t == 600 || t == 900))
            {
                var ap = battle.WarfrontApproach(0); var pc = battle.Layout.Positions[0]; var sd = new Vector2(-ap.Y, ap.X);
                var mm = new TfwGuard[GuardSim.Capacity]; int gn = guards.CopyTo(mm);
                Console.WriteLine($"  MEN t {t / 30} s point A approach ({ap.X:0.00},{ap.Y:0.00}) radius {battle.Layout.Radius(0):0.0} (squad: fwd/side per man)");
                for (int q = 0; q < 4; q++)
                {
                    var l = new StringBuilder($"    s{q}:");
                    for (int i = 0; i < gn; i++) if ((mm[i].Id - 1) / GuardSim.SquadSize == q) { var d = new Vector2(mm[i].Position.X - pc.X, mm[i].Position.Y - pc.Y); l.Append(string.Create(CultureInfo.InvariantCulture, $" ({Vector2.Dot(d, ap):0.0},{Vector2.Dot(d, sd):0.0})")); }
                    Console.WriteLine(l);
                }
            }
            if (frameFile is not null && t % frameEvery == 0 && t <= (frameSeconds > 0 ? frameSeconds : 120) * 30) Frame(frameFile, w, battle, guards, framePoint);
        }
        if (steps.Count == 0) { Console.WriteLine("no tick with a Guardsman standing"); return 1; }
        steps.Sort();
        double P(double q) => steps.Count == 0 ? 0 : steps[Math.Min(steps.Count - 1, (int)(q * steps.Count))];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"tick cost over {steps.Count} ticks with a Guardsman standing (mean {liveGuards / (double)live:0} Guardsmen, {liveOrks / (double)live:0} Orks): mean {steps.Average():0.00} ms, p50 {P(0.5):0.00}, p95 {P(0.95):0.00}, p99 {P(0.99):0.00}, worst {steps[^1]:0.0}; Guardsmen's section mean {(guardMs.Count > 0 ? guardMs.Average() : 0):0.00} ms a tick (windows of 10 s); Guardsmen alive {guards.Alive}, Orks alive {w.Musou!.Alive}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done in {total.Elapsed.TotalSeconds:0} s"));
        return 0;
    }

    /// <summary>The lane battle in-process on the slot's real map (see the class summary): false with <paramref name="code"/> when the map or the war does not start.</summary>
    static bool Boot(string slotName, bool chaos, Stopwatch total, out World w, out TfwBattle battle, out GuardSim guards, out int code)
    {
        w = null!; battle = null!; guards = null!; code = 0;
        if (WarfrontSlots.ByName(slotName) is not { } slot) { Console.Error.WriteLine($"warfront-guards: no lane slot \"{slotName}\""); code = 2; return false; }
        var map = WarfrontMaps.Load(slotName, out string? problem);
        if (map?.Tfw is null) { Console.Error.WriteLine("warfront-guards: " + (problem ?? "no map")); code = 1; return false; }
        var config = Config(chaos);
        var nav = WarfrontMaps.Nav(map, config, out string navNote);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{slotName}{(chaos ? " (Chaos)" : "")}: map {map.Hash:X16}, graph {nav.NodeCount:N0} nodes ({(navNote.Length > 80 ? navNote[..80] : navNote)}), {total.Elapsed.TotalSeconds:0.0} s; Guardsmen {(WarfrontGuardRules.Old ? "OLD (SM_WF_GUARD_OLD=1)" : $"x{WarfrontGuardRules.NumberScale}, strength x{WarfrontGuardRules.StrengthScale}")}"));
        w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = map.StartAreas };
        if (!w.StartTfw(nav, map.Tfw.Field)) { Console.Error.WriteLine("warfront-guards: the war did not start"); code = 1; return false; }
        var run = w.Tfw!;
        run.Guardsmen = true;
        run.Allies = false;
        w.TfwMeksOn = false;
        w.Spawn(1, w.AssignClass(0));
        for (int k = 0; k < 30; k++) w.Step(_ => default);
        run.Enqueue(1, new TfwCommandRecord(1, TfwCommand.Deploy, (byte)slot.Lane));
        for (int k = 0; k < (int)((TfwBattleRules.TravelSeconds + 0.5f) * 30); k++) w.Step(_ => default);
        if (run.Battle is not { } b) { Console.Error.WriteLine("warfront-guards: no battle"); code = 1; return false; }
        battle = b; guards = w.TfwGuards!;
        Console.WriteLine($"battle: {battle.Squads} squads in all, {guards.SquadsAlive} on the field, garrison cap {battle.GarrisonCapNow}, {battle.Layout.Positions.Length} points");
        return true;
    }

    static string ArmyText(TfwBattle b) => string.Join(",", b.ArmyList.Select(a => a.Side.ToString()[0] + a.Count.ToString(CultureInfo.InvariantCulture) + "/" + a.Order));

    /// <summary><c>--scenario</c>: each scenario for each seed on a freshly booted battle (the graph comes from the cache); one line each, or one JSON array with <c>--json</c> (the boot lines go to the same output: take the last line).</summary>
    /// <summary>
    /// <c>--scenario</c> with <c>--frames base.rgb</c> (Guardsmen G4's paired captures): each run also writes raw 720 x 720 RGB frames looking down on the target point, every <c>--frame-every</c> ticks (default 15: 2 a second)
    /// for the first <c>--frame-seconds</c> (default 180) after the wave leaves, to <c>base-&lt;slot&gt;-&lt;scenario&gt;-s&lt;seed&gt;-old.rgb</c> or <c>-new.rgb</c> (old: <c>SM_WF_GUARD_AI_OLD=1</c>). About 1.5 MB a frame: 360 frames are 560 MB, so
    /// convert to MP4 at once (<c>ffmpeg -f rawvideo -pix_fmt rgb24 -s 720x720 -framerate 2*30/every -i file.rgb ...</c>) and delete the raw file.
    /// </summary>
    static int Scenarios(World firstWorld, string slotName, bool chaos, string[] names, List<int> seeds, int target, int seconds, bool json, Stopwatch total, string frames = "", int frameEvery = 15, int frameSeconds = 180)
    {
        var results = new List<WarfrontLineTrialResult>();
        bool first = true;
        bool sections = Environment.GetEnvironmentVariable("SM_WF_GUARD_SECTIONS") == "1";
        foreach (string name in names)
            foreach (int seed in seeds)
            {
                World w = firstWorld;
                if (!first && !Boot(slotName, chaos, total, out w, out _, out _, out int code)) return code;
                first = false;
                if (sections) SimProfile.On = true;
                WarfrontLineTrial.QuietStage = chaos && Environment.GetEnvironmentVariable("SM_WF_GUARD_CHAOS_STAGE") != "1";   // the controlled Chaos variant (lead 42); SM_WF_GUARD_CHAOS_STAGE=1 lets the stage's own pressure come
                WarfrontLineTrial.Prepare(w);
                bool trace = Environment.GetEnvironmentVariable("SM_WF_GUARD_TRACE") == "1";
                var bt = w.Tfw!.Battle!;
                if (Environment.GetEnvironmentVariable("SM_WF_GUARD_LINE_TRACE") == "1")
                {
                    bt.OnRetreat = a => { if (a.Side == TfwArmySide.Guard) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    RETREAT t {w.Tick / 30} army {a.Id} from {a.Post} to goal {a.Goal}, men {a.Count} of {a.Start}, pressure {(a.Post is >= 0 and < 3 ? bt.WarfrontPressure(a.Post) : 0):0}, men at point {(a.Post is >= 0 and < 3 ? bt.WarfrontMen(a.Post) : 0)}")); };
                    bt.OnRally = a => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    RALLY t {w.Tick / 30} army {a.Id} to {a.Goal}, men {a.Count}, pressure there {(a.Goal is >= 0 and < 3 ? bt.WarfrontPressure(a.Goal) : 0):0}"));
                    bt.OnWarfrontWithdraw = (k, d, p, men, healthy) => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    WITHDRAW {"ABC"[k]} -> {(d < 3 ? "ABC"[d].ToString() : "LZ")} pressure {p:0} men {men} healthy {healthy}, Guardsmen {w.TfwGuards!.Alive}, garrison {bt.Stationed(k)}"));
                }
                FileStream? frameFile = null;
                if (frames.Length > 0)
                {
                    string stem = Path.Combine(Path.GetDirectoryName(frames) ?? ".", Path.GetFileNameWithoutExtension(frames));
                    string file = $"{stem}-{slotName}{(chaos ? "-chaos" : "")}-{name}-s{seed}-{(WarfrontLineRules.Old ? "old" : "new")}.rgb";
                    frameFile = File.Create(file);
                    Console.WriteLine($"frames: {file} (every {frameEvery} ticks for {frameSeconds} s, 720x720 rgb24)");
                }
                var fw = w;
                Action<int>? framer = frameFile is null ? null : t => { if (t % frameEvery == 0 && t <= frameSeconds * 30) Frame(frameFile, fw, fw.Tfw!.Battle!, fw.TfwGuards!, target); };
                var r = WarfrontLineTrial.Run(w, name, seed, target, seconds, sections || trace || framer is not null ? t => { framer?.Invoke(t); if (sections) SimProfile.EndTick(); if (t % 300 == 0 && sections) Console.WriteLine("    sections (mean/worst ms a tick): " + SimProfile.WindowReport()); if (trace && t % 150 == 0) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    t {t / 30,3} s: Orks {w.Musou!.Alive}, Guardsmen {w.TfwGuards!.Alive} in {w.TfwGuards.SquadsAlive} squads, secured {bt.Objectives.Take(3).Count(o => o.State == TfwPositionState.Secured)}, armies {ArmyText(bt)}")); } : null);
                frameFile?.Dispose();
                results.Add(r);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{slotName}{(chaos ? "/chaos" : "")} {r.Scenario,-8} seed {r.Seed} -> {r.Outcome,-16} points lost {r.PointsLost} (secured {r.PointsSecured}, first loss {r.FirstLossSeconds:0.0} s), Guardsmen {r.GuardsmenStart} -> {r.GuardsmenEnd} (min {r.GuardsmenMin}), squads {r.SquadsStart} -> {r.SquadsEnd}, wave {r.WaveSize} ({r.WaveElites:0} elites), Orks peak {r.OrksPeak} end {r.OrksEnd}, natural waves {r.NaturalWaves}, {r.Seconds} s; tick mean {r.TickMean:0.00} ms p50 {r.TickP50:0.00} p95 {r.TickP95:0.00} p99 {r.TickP99:0.00} worst {r.TickWorst:0.0} (mean {r.MeanGuardsmen:0} Guardsmen, {r.MeanOrks:0} Orks) hash {r.Hash}; line: withdrawals {r.Withdrawals}, rallies {r.Rallies}, flanks {r.Flanks}, vox {r.VoxLines} (flank {r.VoxFlank}){(r.BreakAtSeconds >= 0 ? $", break at {r.BreakAtSeconds:0.0} s" : "")}"));
            }
        if (json) Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = false }));
        return 0;
    }

    /// <summary><c>--survey [--squads 12]</c>: for each of the nine lane slots, how far the battle's private <c>SlotPlace</c> pulls a full line's places in from the open-ground grid (the metres between a placed slot and its grid slot), for each point and four approach bearings (the point's own, then 90, 180 and 270 degrees round). Reads <c>SlotPlace</c> by reflection (read-only; the line of a held point is only 4 to 8 squads in the first minutes, too few to reach the walls).</summary>
    static int Survey(bool chaos, bool json, int squads)
    {
        var rows = new List<object>();
        var place = typeof(TfwBattle).GetMethod("SlotPlace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (place is null) { Console.Error.WriteLine("warfront-guards: TfwBattle.SlotPlace is not there (renamed?); the survey needs it"); return 1; }
        foreach (var slot in WarfrontSlots.All)
        {
            var sw = Stopwatch.StartNew();
            if (!Boot(slot.Name, chaos, sw, out var w, out var battle, out _, out int code)) return code;
            WarfrontLineTrial.Prepare(w, 20);
            var cuts = new List<float>(); var perPoint = new List<string>(); int walk = 0;
            for (int p = 0; p < 3; p++)
            {
                var point = battle.Layout.Positions[p]; var dir0 = battle.WarfrontApproach(p); float radius = battle.Layout.Radius(p);
                var pc = new List<float>(); int cells = 0;
                if (w.Musou!.Nav is { } nv) for (int n = 0; n < nv.NodeCount; n++) { var np = nv.Position(n); if (MathF.Abs(np.Z - point.Z) < 4f && new Vector2(np.X - point.X, np.Y - point.Y).Length() <= 45f) cells++; }
                walk += cells;
                for (int turn = 0; turn < 4; turn++)
                {
                    float a = turn * MathF.PI / 2f;
                    var dir = new Vector2(dir0.X * MathF.Cos(a) - dir0.Y * MathF.Sin(a), dir0.X * MathF.Sin(a) + dir0.Y * MathF.Cos(a)); var side = new Vector2(-dir.Y, dir.X);
                    for (int i = 0; i < squads; i++)
                    {
                        var (row, lat) = TfwBattle.SlotGrid(i, squads);
                        float fwd = WarfrontGuardRules.FrontShare * radius - row * WarfrontGuardRules.RowSpacing;
                        var ideal = new Vector2(point.X, point.Y) + dir * fwd + side * lat;
                        var got = (Vector3)place.Invoke(battle, [point, dir, i, squads, radius])!;
                        pc.Add((new Vector2(got.X, got.Y) - ideal).Length());
                    }
                }
                cuts.AddRange(pc);
                perPoint.Add(string.Create(CultureInfo.InvariantCulture, $"{"ABC"[p]}: mean {pc.Average():0.0} m, max {pc.Max():0.0} m, {pc.Count(x => x > 1f)}/{pc.Count} over 1 m, {cells} nav cells within 45 m"));
            }
            rows.Add(new { slot = slot.Name, meanShortenedMetres = Math.Round(cuts.Average(), 2), overOneMetre = cuts.Count(x => x > 1f), of = cuts.Count, navCellsWithin45m = walk, points = perPoint });
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"SURVEY {slot.Name}: mean shortening {cuts.Average():0.00} m, {cuts.Count(x => x > 1f)}/{cuts.Count} slots over 1 m, {walk} nav cells within 45 m of the 3 points | ") + string.Join(" | ", perPoint));
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(rows));
        return 0;
    }

    const int Px = 720;   // the frame's side in pixels; 96 m across (7.5 px a metre) round the first secured point

    /// <summary>One raw RGB frame (<see cref="Px"/> square) looking straight down on the first point: its ring, the Guardsmen by squad colour, the Orks green; written after the previous ones to one file for ffmpeg.</summary>
    static void Frame(Stream into, World w, TfwBattle battle, GuardSim guards, int point)
    {
        var rgb = new byte[Px * Px * 3];
        for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = 34; rgb[i + 1] = 34; rgb[i + 2] = 38; }
        var c = battle.Layout.Positions[point];
        float scale = Px / (float.TryParse(Environment.GetEnvironmentVariable("SM_WF_GUARD_FRAME_M"), NumberStyles.Float, CultureInfo.InvariantCulture, out float fm) && fm >= 20f ? fm : 96f);   // SM_WF_GUARD_FRAME_M: the frame's side in metres
        void Disc(float x, float y, float r, byte R, byte G, byte B)
        {
            int cx = (int)((x - c.X) * scale + Px / 2f), cy = (int)(Px / 2f - (y - c.Y) * scale), ri = Math.Max(1, (int)(r * scale));
            for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    int px = cx + dx, py = cy + dy;
                    if (dx * dx + dy * dy > ri * ri || px < 0 || py < 0 || px >= Px || py >= Px) continue;
                    int o = (py * Px + px) * 3; rgb[o] = R; rgb[o + 1] = G; rgb[o + 2] = B;
                }
        }
        float ring = battle.Layout.Radius(point);
        for (int a = 0; a < 360; a += 2) Disc(c.X + MathF.Cos(a * MathF.PI / 180f) * ring, c.Y + MathF.Sin(a * MathF.PI / 180f) * ring, 0.25f, 120, 160, 120);
        var orks = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(orks);
        for (int i = 0; i < n; i++) if (orks[i].Alive) Disc(orks[i].Position.X, orks[i].Position.Y, 0.45f, 80, 200, 80);
        var men = new TfwGuard[GuardSim.Capacity];
        int g = guards.CopyTo(men);
        for (int i = 0; i < g; i++)
        {
            int squad = (men[i].Id - 1) / GuardSim.SquadSize;
            byte R = (byte)(90 + squad * 47 % 150), G = (byte)(120 + squad * 71 % 110), B = (byte)(255 - squad * 29 % 60);
            Disc(men[i].Position.X, men[i].Position.Y, 0.55f, R, G, B);
        }
        into.Write(rgb);
    }

    static SimConfig Config(bool chaos = false)
    {
        var rifle = new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var pistol = new WeaponSpec("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
        var light = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        ClassLoadout Class(string name, Team side) => new(name, -1, 1, [light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
        var cfg = new SimConfig { Weapons = [rifle, pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentProfiles.WarfrontProfile, RespawnTime = 2f };
        return chaos ? WarfrontChaosTuning.Apply(cfg, WarfrontFaction.Chaos) : cfg;
    }
}

// ---- the scenario core: a copy of tests/SmNet.Tests/Extras/Warfront/Guards/WarfrontLineTrial.cs (SmTool is not referenced by the tests); keep the two in step ----
/// <summary>What one scenario run measured (Guardsmen G0, 2026-11-26): the outcome, the points lost and the Guardsmen and squads standing, with the tick cost over the ticks a Guardsman stood.</summary>
internal sealed record WarfrontLineTrialResult(
    string Scenario, int Seed, int Target, int WaveSize, double WaveElites, string Outcome, int PointsLost, int PointsSecured, double FirstLossSeconds,
    int GuardsmenStart, int GuardsmenEnd, int GuardsmenMin, int SquadsStart, int SquadsEnd, int OrksPeak, int OrksEnd, int NaturalWaves, int Seconds,
    double TickMean, double TickP50, double TickP95, double TickP99, double TickWorst, double MeanGuardsmen, double MeanOrks, int Ticks, string Hash,
    double BreakAtSeconds = -1, int Withdrawals = 0, int Rallies = 0, int Flanks = 0, int VoxLines = 0, int VoxFlank = 0);

/// <summary>
/// The battle-level trial of the Guardsmen's line (design-guardsmen-1125, G0): one lane battle in-process, the points secured, the scripted human standing at the landing zone, then one of five scripted pushes:
/// <c>routine</c> (a 200-Ork low-elite wave on one point), <c>elite</c> (the same size, 0.6 elites), <c>flank</c> (the routine wave and a second Ork army from another bearing at the same point), <c>collapse</c> (a 400-Ork,
/// 0.6-elite wave: no help comes) and <c>break</c> (as collapse, then once a line has fallen back, else at 90 s, the human walks onto that point and for 60 s the Orks within 70 m die each second, as the Marines breaking the push). Seeded: the seed only moves the wave's
/// delay (0 to 5 s), its size (+/- 10 %), its elite share (+/- 0.05) and the flank's side, so the same seed is the same run. Reads the simulation only through its public test aids; the simulation is unchanged.
/// The same logic is copied into <c>smtool warfront-guards --scenario</c> (SmTool is not referenced by the tests), which runs it on the real lane maps; keep the two in step.
/// </summary>
internal static class WarfrontLineTrial
{
    /// <summary>The Orks' endless trickle from the sources (TfwBattle.Trickle): off for the scripted pushes, so the wave alone is the pressure; on only to see the whole tide.</summary>
    public static bool TrickleOn { get; set; }
    /// <summary>Keeps the HQ stronghold's own crowd dead (every second, the Orks that belong to no army within 50 m of its source): once every point is secured the HQ fills its crowd and swarms it, a tide that wipes any line in 2 minutes and would hide the scripted push. False (<c>--hq-tide</c>) lets it come.</summary>
    public static bool QuietHq { get; set; } = true;
    /// <summary>
    /// Guardsmen G1 round 2 (lead 42): the controlled stage. Every second every enemy that belongs to no army dies (the crowds, the swarms, a Chaos stage's breach Daemons and its own sources), so the scripted wave
    /// (and the flank army, and the stage's natural waves) decide the fight. Set for the Chaos variant, where the stage's own pressure took every point at 23 to 28 s before the wave arrived.
    /// </summary>
    public static bool QuietStage { get; set; }
    public static readonly string[] Scenarios = ["routine", "elite", "flank", "collapse", "break"];
    public const int Human = 1, Points = 3;   // the lane battle's three capture points (a fourth objective, where a test arena has one, is the commander's)
    /// <summary>The Marines break the push when the target's line first reaches Breaking (Guardsmen G1 round 2, lead 42), at the latest <see cref="BreakAtSeconds"/> after the wave left (the restore switch never breaks).</summary>
    /// <summary>Round 5 (lead 42, 2026-11-27): the break clears the approach ring (70 m, the line's quiet-ring rule) of the first point whose line has fallen back, so it can rally; the target at 90 s when none breaks first.</summary>
    public const float BreakAtSeconds = 90f, BreakRadius = 70f, FlankDistance = 80f;

    /// <summary>Secures every position of the battle (the test aid), kills the Orks that are left and lets the line stand 20 s. The human stands at the landing zone.</summary>
    public static void Prepare(World w, int settleSeconds = 20)
    {
        var battle = w.Tfw!.Battle!;
        battle.Trickle = TrickleOn;
        for (int k = 0; k < Points; k++) battle.ClearAndSecureForTests(k);
        KillAll(w);
        w.TfwRespawnAt(Human, battle.Layout.LandingZone, 0f);
        for (int t = 0; t < settleSeconds * 30; t++) { w.Step(_ => default); if (t % 30 == 29) KillAll(w); }
    }

    static void KillAll(World w)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++) if (all[k].Alive) w.Musou.Damage(all[k].Id, Human, 1e6f, Vector3.Zero, out _);
    }

    static (int Size, float Share, bool Flank, bool Break) Plan(string scenario) => scenario switch
    {
        "routine" => (200, 0.10f, false, false),
        "elite" => (200, 0.60f, false, false),
        "flank" => (200, 0.10f, true, false),
        "collapse" => (400, 0.60f, false, false),
        "break" => (400, 0.60f, false, true),
        _ => throw new ArgumentException("unknown scenario " + scenario),
    };

    /// <summary>Runs <paramref name="scenario"/> on a prepared battle for <paramref name="seconds"/> after the wave left (or until the battle is won).</summary>
    public static WarfrontLineTrialResult Run(World w, string scenario, int seed, int target = 0, int seconds = 240, Action<int>? onTick = null)
    {
        var (size, share, flank, brk) = Plan(scenario);
        var battle = w.Tfw!.Battle!;
        var guards = w.TfwGuards!;
        var rng = new Random(unchecked(seed * 7919 + Array.IndexOf(Scenarios, scenario) * 104729 + 17));
        int delay = rng.Next(0, 150);
        size = Math.Max(10, (int)Math.Round(size * (0.9 + 0.2 * rng.NextDouble())));
        share = Math.Clamp(share + (float)(rng.NextDouble() - 0.5) * 0.1f, 0f, 1f);
        int flankSide = rng.Next(2) == 0 ? 1 : -1;
        int natural0 = battle.WavesSent;
        for (int t = 0; t < delay; t++) w.Step(_ => default);
        int guardsStart = guards.Alive, squadsStart = guards.SquadsAlive;
        var wave = battle.SendWaveForTests(size, target);
        if (wave is null) throw new InvalidOperationException("the wave was refused (no secured target or no muster)");
        wave.FarElites = size * share;
        var point = battle.Layout.Positions[target];
        int breakPoint = target;
        if (flank)
        {
            var back = new Vector2(battle.Layout.Hq.X - point.X, battle.Layout.Hq.Y - point.Y);
            if (back.LengthSquared() < 4f) back = Vector2.UnitY;
            back = Vector2.Normalize(back);
            float a = flankSide * 110f * MathF.PI / 180f;
            var dir = new Vector2(back.X * MathF.Cos(a) - back.Y * MathF.Sin(a), back.X * MathF.Sin(a) + back.Y * MathF.Cos(a));
            var at = new Vector3(point.X + dir.X * FlankDistance, point.Y + dir.Y * FlankDistance, point.Z);
            if (w.Musou!.Nav is { } nav) { int node = nav.Nearest(at, 25f, 25f); if (node >= 0) at = nav.Position(node); else at = point; }
            battle.AddArmyForTests(TfwArmySide.Ork, TfwArmyOrder.Attack, target, 100, at, MathF.Atan2(dir.X, -dir.Y), 0.1f);
        }
        var steps = new List<double>();
        long liveGuards = 0, liveOrks = 0;
        int ticks = seconds * 30, orksPeak = 0, guardsMin = guards.Alive, lostMask = 0, firstLoss = -1, ranTicks = 0;
        int breakAt = (int)(BreakAtSeconds * 30f);
        for (int t = 1; t <= ticks && !battle.Won; t++, ranTicks++)
        {
            if (brk && t < breakAt)
                for (int p = 0; p < Points; p++)
                    if (battle.WarfrontLineOf(p) == WarfrontLineState.Withdrawn) { breakAt = t; breakPoint = p; point = battle.Layout.Positions[p]; break; }   // round 5: the break comes once a line has fallen back (wherever the wave hit it), so the journey shows the rally
            if (brk && t >= breakAt && t <= breakAt + 60 * 30 && (t == breakAt || t % 30 == 0))
            {
                if (t == breakAt) w.TfwRespawnAt(Human, point, 0f);
                var all = new MusouEnemy[MusouSim.Capacity];
                int n = w.Musou!.CopyTo(all);
                for (int k = 0; k < n; k++)
                    if (all[k].Alive && new Vector2(all[k].Position.X - point.X, all[k].Position.Y - point.Y).Length() <= BreakRadius) w.Musou.Damage(all[k].Id, Human, 1e6f, Vector3.Zero, out _);
            }
            if (QuietHq && t % 30 == 0) QuietHqCrowd(w, battle);
            if (QuietStage && t % 30 == 0) QuietStageCrowds(w, battle);
            if (t % 30 == 0 && w.Players.TryGetValue(Human, out var me) && !me.Alive) w.TfwRespawnAt(Human, brk && t >= breakAt ? point : battle.Layout.LandingZone, 0f);
            long start = Stopwatch.GetTimestamp();
            w.Step(_ => default);
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            onTick?.Invoke(t);
            if (guards.Alive > 0) { steps.Add(ms); liveGuards += guards.Alive; liveOrks += w.Musou!.Alive; }
            orksPeak = Math.Max(orksPeak, w.Musou!.Alive);
            guardsMin = Math.Min(guardsMin, guards.Alive);
            for (int p = 0; p < Points; p++)
                if (battle.Objectives[p].State != TfwPositionState.Secured && (lostMask & (1 << p)) == 0) { lostMask |= 1 << p; if (firstLoss < 0) firstLoss = t; }
        }
        int secured = 0;
        for (int p = 0; p < Points; p++) if (battle.Objectives[p].State == TfwPositionState.Secured) secured++;
        int lost = System.Numerics.BitOperations.PopCount((uint)lostMask);
        string outcome = guards.Alive == 0 ? "WIPED" : lost == 0 ? "HELD" : secured == Points ? "LOST-AND-RETAKEN" : "POINT-LOST";
        steps.Sort();
        double P(double q) => steps.Count == 0 ? 0 : steps[Math.Min(steps.Count - 1, (int)(q * steps.Count))];
        string hash = Hash(w, battle);
        return new WarfrontLineTrialResult(scenario, seed, target, size, size * share, outcome, lost, secured, firstLoss < 0 ? -1 : Math.Round(firstLoss / 30.0, 1),
            guardsStart, guards.Alive, guardsMin, squadsStart, guards.SquadsAlive, orksPeak, w.Musou!.Alive, battle.WavesSent - natural0 - 1, ranTicks / 30,
            steps.Count == 0 ? 0 : steps.Average(), P(0.5), P(0.95), P(0.99), steps.Count == 0 ? 0 : steps[^1], liveGuards / (double)Math.Max(1, steps.Count), liveOrks / (double)Math.Max(1, steps.Count), steps.Count, hash,
            brk ? Math.Round(breakAt / 30.0, 1) : -1, battle.WarfrontWithdrawals, battle.WarfrontLineRallies, battle.WarfrontFlanksRaised, battle.WarfrontVoxLimiter?.Made ?? 0,
            battle.WarfrontVoxLimiter?.MadeOf(SmSim.Extras.Warfront.WarfrontVoxKind.Flank) ?? 0);
    }

    static void QuietStageCrowds(World w, TfwBattle battle)
    {
        var army = new HashSet<ushort>();
        foreach (var a in battle.ArmyList) foreach (ushort id in a.Orks) army.Add(id);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++)
            if (all[k].Alive && !army.Contains(all[k].Id)) w.Musou.Damage(all[k].Id, Human, 1e6f, Vector3.Zero, out _);
    }

    static void QuietHqCrowd(World w, TfwBattle battle)
    {
        var src = battle.Layout.Sources.Length > 3 ? battle.Layout.Sources[3] : battle.Layout.Hq;
        var army = new HashSet<ushort>();
        foreach (var a in battle.ArmyList) foreach (ushort id in a.Orks) army.Add(id);
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++)
            if (all[k].Alive && !army.Contains(all[k].Id) && new Vector2(all[k].Position.X - src.X, all[k].Position.Y - src.Y).Length() <= 50f) w.Musou.Damage(all[k].Id, Human, 1e6f, Vector3.Zero, out _);
    }

    /// <summary>A short state hash (the Guardsmen's and Orks' places in id order, the objectives): the same seeded run gives the same one; the baseline of the restore switch.</summary>
    public static string Hash(World w, TfwBattle battle)
    {
        ulong h = 1469598103934665603UL;
        void Mix(float v) { h = (h ^ (uint)BitConverter.SingleToInt32Bits(MathF.Round(v * 100f) / 100f)) * 1099511628211UL; }
        var men = new TfwGuard[GuardSim.Capacity];
        int g = w.TfwGuards!.CopyTo(men);
        for (int i = 0; i < g; i++) { Mix(men[i].Id); Mix(men[i].Position.X); Mix(men[i].Position.Y); }
        var orks = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(orks);
        for (int i = 0; i < n; i++) if (orks[i].Alive) { Mix(orks[i].Id); Mix(orks[i].Position.X); Mix(orks[i].Position.Y); }
        for (int p = 0; p < Points; p++) { Mix((float)battle.Objectives[p].State); Mix(battle.Objectives[p].Progress); }
        return h.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    }
}
