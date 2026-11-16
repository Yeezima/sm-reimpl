using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Hq;

namespace SmTool;

/// <summary>
/// <c>smtool warfront-hq</c> (Warfront slice W13b): the HQ defence's measurements, in-process on the composed HQ map with its walking graph (a cold graph build is 100 s or more; the private cache keeps it).
/// <c>--measure [seconds]</c>: 300 Orks sent from the assembly ground with no defenders and no scheduled waves; per chokepoint (the gate, the two keep doors, the keep's two rear openings, the two tunnels) the Orks through per
/// minute, the largest queue and the stuck-log records, plus the tick cost. <c>--waves [seconds]</c>: the real schedule (waves, Guardsmen, a human at the landing zone) with the tick cost while the third wave is on the field.
/// Nothing is read from the install; no socket is opened.
/// </summary>
static class WarfrontHqCommand
{
    sealed record Choke(string Name, float Line, float X0, float X1);

    static readonly Choke[] Chokes =
    [
        new("gate", -43f, -6f, 6f),
        new("keep doors (two)", 42f, -30f, 30f),
        new("keep rear openings (two)", 98f, -26f, 26f),
        new("tunnels (two)", 110f, -26f, 26f),
    ];

    public static int Run(string[] rest)
    {
        bool measure = false, waves = false; int seconds = 0;
        if (rest.Length == 2 && rest[0] == "--make-run") return MakeRun(Path.GetFullPath(rest[1]));
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--measure": measure = true; if (i + 1 < rest.Length && int.TryParse(rest[i + 1], out int a)) { seconds = a; i++; } break;
                case "--waves": waves = true; if (i + 1 < rest.Length && int.TryParse(rest[i + 1], out int b)) { seconds = b; i++; } break;
                default: Console.Error.WriteLine($"warfront-hq: unknown argument \"{rest[i]}\" (usage: warfront-hq --measure [seconds] | --waves [seconds])"); return 2; }
        }
        if (measure == waves) { Console.Error.WriteLine("warfront-hq: give --measure or --waves"); return 2; }
        return Simulate(waves, seconds > 0 ? seconds : waves ? 400 : 300);
    }

    /// <summary><c>--make-run &lt;dir&gt;</c>: writes a run in Travel to the HQ (every lane advanced, at node 0, the HQ mandatory) and the stage file for <c>wf_hq</c> into a run folder, for a real-server proof (<c>SM_WF_DIR</c>).</summary>
    static int MakeRun(string dir)
    {
        Directory.CreateDirectory(dir);
        var seed = new WarfrontCampaign(5);
        seed.Embark();
        var node = System.Text.Json.Nodes.JsonNode.Parse(seed.ToJson())!.AsObject();
        node["HqMandatory"] = true;
        node["Fronts"] = new System.Text.Json.Nodes.JsonArray(0, 0, 0);
        node["Advanced"] = new System.Text.Json.Nodes.JsonArray(true, true, true);
        node["Phase"] = "Travel";
        node["CurrentTarget"] = new System.Text.Json.Nodes.JsonObject { ["Kind"] = "Hq", ["Lane"] = -1 };
        File.WriteAllText(Path.Combine(dir, "run.json"), node.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "stage.json"), System.Text.Json.JsonSerializer.Serialize(new { Map = "wf_hq", Expected = Array.Empty<ulong>(), Ship = "wf_b1" }));
        Console.WriteLine("a run in Travel to the HQ written to " + dir);
        return 0;
    }

    static SimConfig Config()
    {
        var rifle = new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var pistol = new WeaponSpec("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
        var light = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        ClassLoadout Class(string name, Team side) => new(name, -1, 1, [light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
        return new SimConfig { Weapons = [rifle, pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentProfiles.WarfrontProfile, RespawnTime = 2f };
    }

    static int Simulate(bool waves, int seconds)
    {
        var total = Stopwatch.StartNew();
        var map = WarfrontMaps.Load(WarfrontMaps.HqName, out string? problem);
        if (map?.Tfw is null) { Console.Error.WriteLine("warfront-hq: " + (problem ?? "no map")); return 1; }
        var config = Config();
        var nav = WarfrontMaps.Nav(map, config, out string navNote);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"map {map.Hash:X16}, graph {nav.NodeCount:N0} nodes ({(navNote.Length > 90 ? navNote[..90] : navNote)}), {total.Elapsed.TotalSeconds:0.0} s"));
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = map.StartAreas };
        if (!w.StartTfw(nav, map.Tfw.Field)) { Console.Error.WriteLine("warfront-hq: the war did not start"); return 1; }
        var run = w.Tfw!;
        run.Guardsmen = waves;
        run.Patrols = false;
        w.TfwMeksOn = false;
        w.Spawn(1, w.AssignClass(0));
        for (int k = 0; k < 30; k++) w.Step(_ => default);
        run.Warfront!.Target = WarfrontTargetKind.Hq;
        var stuck = new List<WarfrontStuckReport>();
        run.Warfront.StuckSink = r => stuck.Add(r);
        run.Enqueue(1, new TfwCommandRecord(1, TfwCommand.Deploy, 0));
        for (int k = 0; k < (int)((TfwBattleRules.TravelSeconds + 0.5f) * 30); k++) w.Step(_ => default);
        if (run.Battle is not { } battle || !battle.WarfrontHq) { Console.Error.WriteLine("warfront-hq: the HQ battle did not begin"); return 1; }
        Console.WriteLine($"the HQ defence began; layout sites took {run.LastSitesMilliseconds:0} ms, landing {run.LastLandMilliseconds:0} ms");
        if (!waves) { battle.HqWavesOn = false; Console.WriteLine($"{battle.HqSendForTests(300)} Orks sent from the assembly ground, no defenders, no scheduled waves"); }

        var prev = new Vector3[MusouSim.Capacity + 1];
        var seen = new bool[MusouSim.Capacity + 1];
        var all = new MusouEnemy[MusouSim.Capacity];
        int minutes = (seconds + 59) / 60;
        var through = new int[Chokes.Length, minutes + 1];
        var queue = new int[Chokes.Length];
        var first = new int[Chokes.Length];
        Array.Fill(first, -1);
        var steps = new List<double>();
        var stepsBig = new List<double>();
        int maxAlive = 0;
        int ticks = seconds * 30;
        for (int t = 0; t < ticks && run.Battle is not null; t++)
        {
            if (waves && !w.Players[1].Alive) w.TfwRevive(1);   // the measuring human never stays down: the tick cost is read with the whole schedule on the field
            long start = Stopwatch.GetTimestamp();
            w.Step(_ => default);
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            steps.Add(ms);
            int n = w.Musou!.CopyTo(all);
            maxAlive = Math.Max(maxAlive, n);
            if (battle.HqWavesSent >= 3) stepsBig.Add(ms);
            var inBox = new int[Chokes.Length];
            for (int i = 0; i < n; i++)
            {
                var e = all[i];
                if (!e.Alive) { seen[e.Id] = false; continue; }
                var p = e.Position;
                if (seen[e.Id])
                    for (int c = 0; c < Chokes.Length; c++)
                    {
                        var ch = Chokes[c];
                        if (prev[e.Id].Y < ch.Line && p.Y >= ch.Line && p.X >= ch.X0 && p.X <= ch.X1)
                        {
                            through[c, Math.Min(minutes, t / 1800)]++;
                            if (first[c] < 0) first[c] = t;
                        }
                    }
                for (int c = 0; c < Chokes.Length; c++)
                {
                    var ch = Chokes[c];
                    if (p.Y < ch.Line && p.Y >= ch.Line - 25f && p.X >= ch.X0 - 15f && p.X <= ch.X1 + 15f) inBox[c]++;
                }
                prev[e.Id] = p; seen[e.Id] = true;
            }
            for (int c = 0; c < Chokes.Length; c++) queue[c] = Math.Max(queue[c], inBox[c]);
        }
        Console.WriteLine($"ran {(run.Battle is null ? "until the battle ended" : $"{seconds} s")}; fallen positions {battle.HqFallenCount}; Orks alive at the end {w.Musou!.Alive}, most {maxAlive}");
        if (!waves)
        {
            Console.WriteLine("chokepoint                    first through   per minute (min 1..)                    most per minute   largest queue (25 m before)");
            for (int c = 0; c < Chokes.Length; c++)
            {
                var row = Enumerable.Range(0, minutes).Select(m => through[c, m]).ToArray();
                Console.WriteLine($"{Chokes[c].Name,-28}  {(first[c] < 0 ? "never" : (first[c] / 30f).ToString("0.0", CultureInfo.InvariantCulture) + " s"),-14}  {string.Join(" ", row.Select(v => v.ToString().PadLeft(4))),-38}  {row.Max(),-16}  {queue[c]}");
            }
            Console.WriteLine($"stuck-log records: {stuck.Count} ({stuck.Count / 3.0:0.0} % of 300): " + string.Join(", ", stuck.GroupBy(s => Band(s.At.Y)).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()} ({string.Join("/", g.GroupBy(s => s.Why).Select(h => $"{h.Key} {h.Count()}"))})")));
        }
        else Console.WriteLine($"stuck-log records: {stuck.Count}: " + string.Join(", ", stuck.GroupBy(s => Band(s.At.Y)).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}")));
        foreach (var r in stuck.Where(x => x.Why == WarfrontStuckWhy.Relocated).GroupBy(x => ((int)MathF.Round(x.At.X / 10f), (int)MathF.Round(x.At.Y / 10f))).OrderByDescending(g => g.Count()).Take(8).Select(g => g.First()).Take(0)) Console.WriteLine(r);
        foreach (var g in stuck.GroupBy(x => ((int)MathF.Round(x.At.X / 10f) * 10, (int)MathF.Round(x.At.Y / 10f) * 10)).OrderByDescending(g => g.Count()).Take(8)) Console.WriteLine($"  stuck cell x {g.Key.Item1} y {g.Key.Item2}: {g.Count()}");
        foreach (var r in stuck.Take(0)) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  stuck: {r.Type} {r.Why} at ({r.At.X:0}, {r.At.Y:0}, {r.At.Z:0})"));
        Report("tick cost, whole run", steps);
        if (waves) Report("tick cost, third wave on the field", stepsBig);
        return 0;
    }

    static string Band(float y) => y < -46f ? "ramp and assembly" : y < -30f ? "gate" : y < 40f ? "courtyard" : y < 100f ? "keep hall" : y < 150f ? "tunnels" : "chamber";

    static void Report(string label, List<double> ms)
    {
        if (ms.Count == 0) { Console.WriteLine($"{label}: no ticks"); return; }
        var sorted = ms.OrderBy(x => x).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {ms.Count} ticks, p50 {sorted[sorted.Count / 2]:0.00} ms, p99 {sorted[(int)(sorted.Count * 0.99)]:0.00} ms, max {sorted[^1]:0.0} ms (a tick is 33.3 ms)"));
    }
}
