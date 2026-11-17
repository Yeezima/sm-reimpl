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
/// <c>--scenario passive|fallback|gate9 [seconds]</c> (second pass): the real schedule with the human driven by a script, at level 5 Ork scaling, reporting how the defence ended and when each position fell: <c>passive</c> a human alive in the caves who never shoots;
/// <c>fallback</c> (W13c; replaces <c>gate</c>, whose bot stood in the gate until it died and so measured the script) one human shoots the nearest Ork from inside the front position's ring and, when its hold is under half or his health under half, walks to the next position and goes on there (a death puts him back at his position; reported, not required); <c>gate9</c> one human at the gate shooting, with with nine squad Marines on Hold in the wall's ring (modelled: each counts 2 in the hold and kills <see cref="MarineKillsPerSecond"/> Orks a second within 40 m; not the squad's own simulation).
/// In-process on the composed map and graph (the same simulation the server runs), not a Godot client. Nothing is read from the install; no socket is opened.
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

    /// <summary>The kill rate of a modelled squad Marine (Orks a second; ours, an estimate).</summary>
    const float MarineKillsPerSecond = 0.5f;
    static string? _scenario;
    /// <summary>The fallback human's stand point at W, K and V, and the waypoints of each walk back (to K by the east door, to V by the east tunnel).</summary>
    static readonly Vector3[] Stand = [new(0f, -26f, WarfrontHqMap.PlateauZ), new(0f, 66f, WarfrontHqMap.PlateauZ), new(0f, 164f, WarfrontHqMap.PlateauZ)];
    static readonly Vector3[][] Walk =
    [
        [],
        [new(0f, 8f, WarfrontHqMap.PlateauZ), new(26f, 30f, WarfrontHqMap.PlateauZ), new(26f, 52f, WarfrontHqMap.PlateauZ), new(0f, 66f, WarfrontHqMap.PlateauZ)],
        [new(22f, 80f, WarfrontHqMap.PlateauZ), new(22f, 108f, WarfrontHqMap.PlateauZ), new(22f, 146f, WarfrontHqMap.PlateauZ), new(0f, 164f, WarfrontHqMap.PlateauZ)],
    ];
    static bool _diagnose, _noAllies;

    public static int Run(string[] rest)
    {
        bool measure = false, waves = false; int seconds = 0;
        if (rest.Length == 2 && rest[0] == "--make-run") return MakeRun(Path.GetFullPath(rest[1]));
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--measure": measure = true; if (i + 1 < rest.Length && int.TryParse(rest[i + 1], out int a)) { seconds = a; i++; } break;
                case "--scenario": if (i + 1 < rest.Length) { _scenario = rest[++i]; waves = true; if (i + 1 < rest.Length && int.TryParse(rest[i + 1], out int c)) { seconds = c; i++; } } break;
                case "--diagnose": _diagnose = true; break;
                case "--no-allies": _noAllies = true; break;   // test aid: the two allied Marines of the pair are not spawned (the Guardsmen and the human alone)
                case "--waves": waves = true; if (i + 1 < rest.Length && int.TryParse(rest[i + 1], out int b)) { seconds = b; i++; } break;
                default: Console.Error.WriteLine($"warfront-hq: unknown argument \"{rest[i]}\" (usage: warfront-hq --measure [seconds] | --waves [seconds] | --scenario passive|fallback|gate9 [seconds])"); return 2; }
        }
        if (measure == waves) { Console.Error.WriteLine("warfront-hq: give --measure or --waves"); return 2; }
        if (_scenario is not null and not ("passive" or "fallback" or "gate9")) { Console.Error.WriteLine("warfront-hq: --scenario takes passive, fallback or gate9"); return 2; }
        return Simulate(waves, seconds > 0 ? seconds : _scenario is not null ? 500 : waves ? 400 : 300);
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
        if (_noAllies) run.Allies = false;
        w.TfwMeksOn = false;
        w.Spawn(1, w.AssignClass(0));
        for (int k = 0; k < 30; k++) w.Step(_ => default);
        run.Warfront!.Target = WarfrontTargetKind.Hq;
        var stuck = new List<WarfrontStuckReport>();
        var diag = new List<string>();
        run.Warfront.StuckSink = r =>
        {
            stuck.Add(r);
            if (!_diagnose || diag.Count >= 400) return;
            // what the Ork was doing: its target (a Guard squad, with that squad's place and goal, or a player), its state and the graph node under it
            w.Musou!.TryGet(r.Id, out var e);
            int tgt = w.Musou.TargetOf(r.Id);
            string who = tgt <= -2 ? $"squad {TfwContracts.SquadOf(tgt)} at ({w.TfwGuards!.Centre(TfwContracts.SquadOf(tgt)).X:0},{w.TfwGuards.Centre(TfwContracts.SquadOf(tgt)).Y:0},{w.TfwGuards.Centre(TfwContracts.SquadOf(tgt)).Z:0})" : tgt > 0 ? "player " + tgt + (w.Players.TryGetValue(tgt, out var tp) ? string.Create(CultureInfo.InvariantCulture, $" ({(tp.Alive ? "alive" : "down")}) at ({tp.Position.X:0},{tp.Position.Y:0},{tp.Position.Z:0})") : "") : "none";
            diag.Add(string.Create(CultureInfo.InvariantCulture, $"{(run.Battle?.HqSeconds ?? 0f):0}s {r.Type} {r.Why} at ({r.At.X:0},{r.At.Y:0},{r.At.Z:0}) state {e.State} vel ({e.Velocity.X:0.0},{e.Velocity.Y:0.0}) node {e.Node} {FlowNote(w.Musou, e.Node)} target {who}"));
        };
        run.Enqueue(1, new TfwCommandRecord(1, TfwCommand.Deploy, 0));
        for (int k = 0; k < (int)((TfwBattleRules.TravelSeconds + 0.5f) * 30); k++) w.Step(_ => default);
        if (run.Battle is not { } battle || !battle.WarfrontHq) { Console.Error.WriteLine("warfront-hq: the HQ battle did not begin"); return 1; }
        Console.WriteLine($"the HQ defence began; layout sites took {run.LastSitesMilliseconds:0} ms, landing {run.LastLandMilliseconds:0} ms");
        var human = new Vector3(0f, 188f, WarfrontHqMap.PlateauZ);
        if (_scenario is not null)
        {
            var scale = SmSim.Extras.Warfront.Progression.WarfrontOrkScaling.LevelScale(5);
            run.Warfront.OrkLevelScale = () => (scale.Health, scale.Damage);   // level 5 Ork scaling (the review's acceptance)
            human = _scenario == "passive" ? new Vector3(0f, 172f, WarfrontHqMap.PlateauZ) : Stand[0];
            w.TfwRespawnAt(1, human, _scenario == "passive" ? MathF.PI : 0f);
            if (_scenario == "gate9")
            {
                var ring = run.Battle!.Layout.Positions[0] with { Z = WarfrontHqMap.PlateauZ };
                run.Warfront.SquadTargets = into => { int n = Math.Min(9, into.Length); for (int i = 0; i < n; i++) into[i] = new MusouTarget(i, ring + new Vector3(i * 2f - 8f, 3f, 0f), true, 0.5f); return n; };
            }
        }
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
        TfwMapResult? result = null;
        run.Warfront.NodeEnded = (_, _, r) => result = r;
        var slow = new List<(double Ms, int Tick, byte Flags, int Stuck, int Alive)>();
        var falls = new List<string>();
        int fallen = 0, kills = 0, marineClock = 0, lastStuck = 0, nAll = 0;
        bool shooter = _scenario is "fallback" or "gate9";
        var openTicks = new int[3]; var stageOpenTicks = new int[3]; var perSource = new Dictionary<(int, int), int>();   // finding 3: the seconds each stage source was open (clear of every human's sight)
        int at = 0, wp = 0, deaths = 0; bool walking = false, wasAlive = true; float maxHp = 0f;
        for (int t = 0; t < ticks && run.Battle is not null; t++)
        {
            if (waves && _scenario is null && !w.Players[1].Alive) w.TfwRevive(1);   // the measuring human never stays down: the tick cost is read with the whole schedule on the field
            var input = default(PlayerInput);
            if (_scenario == "fallback")
            {
                var me0 = w.Players[1];
                if (!me0.Alive) { if (wasAlive) deaths++; wasAlive = false; }
                else
                {
                    if (maxHp <= 0f) maxHp = me0.Health;
                    at = Math.Max(at, battle.HqFront);
                    if (!wasAlive) { wasAlive = true; walking = false; w.TfwRespawnAt(1, Stand[Math.Min(2, at)], 0f); }   // a death: back at his position
                    else if (!walking && at == battle.HqFront && at < 2 && (battle.HqHold < 0.5f || me0.Health < maxHp * 0.5f)) { at++; walking = true; wp = 0; }
                }
            }
            if (_scenario == "fallback" && walking && w.Players[1].Alive)
            {
                var me = w.Players[1].Position;
                var wpts = Walk[Math.Min(2, at)];
                if (wp >= wpts.Length || new Vector2(wpts[wp].X - me.X, wpts[wp].Y - me.Y).Length() < 2.5f) { if (++wp >= wpts.Length) walking = false; }
                if (walking)
                {
                    var d = wpts[wp] - me;
                    input = new PlayerInput(w.Tick, 0f, 1f, MathF.Atan2(-d.X, d.Y), InputButtons.None, 0f);
                }
            }
            else if (shooter && w.Players[1].Alive)
            {
                var me = w.Players[1].Position + new Vector3(0f, 0f, 1.5f);
                int best = -1; float bestD = 90f * 90f;
                for (int i = 0; i < nAll; i++)
                {
                    if (!all[i].Alive || all[i].Position.Y < -170f) continue;
                    float d2 = Vector3.DistanceSquared(all[i].Position, me);
                    if (d2 < bestD && all[i].Position.Y > me.Y - 60f) { bestD = d2; best = i; }
                }
                if (best >= 0)
                {
                    var d = all[best].Position + new Vector3(0f, 0f, 1f) - me;
                    input = new PlayerInput(w.Tick, 0f, 0f, MathF.Atan2(-d.X, d.Y), InputButtons.Fire, MathF.Atan2(d.Z, new Vector2(d.X, d.Y).Length()));
                }
            }
            int gc2 = GC.CollectionCount(2), gc0 = GC.CollectionCount(0);
            long start = Stopwatch.GetTimestamp();
            w.Step(id => id == 1 ? input : default);
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            steps.Add(ms);
            if (_scenario == "gate9" && !battle.HqFell(0) && ++marineClock >= (int)(30f / (MarineKillsPerSecond * 9f)) + 0)
            {
                marineClock = 0;   // nine Marines together kill one Ork every 1 / (0.5 x 9) s: the nearest to the ring within 40 m
                var ring = battle.Layout.Positions[0];
                int pick = -1; float pd = 40f * 40f;
                for (int i = 0; i < nAll; i++)
                    if (all[i].Alive && Vector3.DistanceSquared(all[i].Position, ring) < pd) { pd = Vector3.DistanceSquared(all[i].Position, ring); pick = i; }
                if (pick >= 0) { w.Musou!.Damage(all[pick].Id, 0, all[pick].Health, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World); kills++; }
            }
            int n = w.Musou!.CopyTo(all);
            nAll = n;
            if (battle.HqFallenCount != fallen) { fallen = battle.HqFallenCount; falls.Add($"{"WKV"[Math.Min(2, fallen - 1)]} fell at {t / 30f:0} s ({t / 1800}:{t / 30 % 60:00})"); }
            if (_scenario is not null && t % 450 == 0) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {t / 1800}:{t / 30 % 60:00}  front {battle.HqFront}  hold {battle.HqHold:0.00}  Orks' weight {battle.HqWeights.Orks} vs defenders' {battle.HqWeights.Defenders}  Orks alive {n}  Guardsmen {w.TfwGuards?.Alive}  Orks killed {battle.OrksKilled}  human {(w.Players[1].Alive ? "alive" : "down")} at y {w.Players[1].Position.Y:0}"));
            if (waves && t % 10 == 0)
                for (int st = 1; st <= 2; st++)
                {
                    if (!battle.HqSourceOpen(st)) continue;
                    stageOpenTicks[st] += 10;
                    var hp = w.Players[1].Alive ? new[] { w.Players[1].Position } : Array.Empty<Vector3>();
                    int si = 0;
                    foreach (var src in WarfrontHqLayout.SourcesOf(st)) { if (battle.HqSourceClear(src, hp)) { perSource[(st, si)] = perSource.GetValueOrDefault((st, si)) + 10; } si++; }
                }
            slow.Add((ms, t, (byte)(battle.HqTickFlags | (GC.CollectionCount(2) != gc2 ? 32 : 0) | (GC.CollectionCount(0) != gc0 ? 64 : 0)), stuck.Count - lastStuck, n));
            lastStuck = stuck.Count;
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
        if (waves)
        {
            double tEnd = battle.HqSeconds;
            Console.WriteLine($"result {(result?.ToString() ?? "still running")} at {(int)tEnd / 60}:{(int)tEnd % 60:00}; {string.Join("; ", falls)}; waves sent {battle.HqWavesSent}; Orks made {battle.HqOrksMade}, skipped {battle.HqOrksSkipped}, Weirdboyz {battle.WeirdboyzCome}; the human {(w.Players[1].Alive ? "alive" : "down")}; modelled Marine kills {kills}; Guardsmen routed {battle.HqGuardsRouted}{(_scenario == "fallback" ? $"; the fallback human's deaths {deaths}, last position held by him {"WKV"[Math.Min(2, at)]}" : "")}");
            Console.WriteLine($"Orks killed by the human and others: {battle.OrksKilled}; stuck-log records {stuck.Count} = {stuck.Count * 100.0 / Math.Max(1, battle.HqOrksMade):0.0} % of the Orks made");
            var relocated = stuck.Where(x => x.Why == WarfrontStuckWhy.Relocated).GroupBy(x => x.Id).ToList();
            Console.WriteLine($"stranded Orks sent back to the assembly ground: {relocated.Sum(g => g.Count())} times by {relocated.Count} Orks, {relocated.Count(g => g.Count() >= 2)} of them twice or more; removed {stuck.Count(x => x.Why == WarfrontStuckWhy.Removed)}");
            for (int st = 1; st <= 2; st++)
                if (stageOpenTicks[st] > 0)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stage {st} sources: the stage was open {stageOpenTicks[st] / 30f:0} s; seconds each source was clear of the human's sight: {string.Join(", ", Enumerable.Range(0, 3).Select(i => (perSource.GetValueOrDefault((st, i)) / 30f).ToString("0")))} (a fifth of the open time is {stageOpenTicks[st] / 150f:0} s)"));
            Console.WriteLine("slowest ticks (ms, second, what ran: 1 muster, 2 stage spawns, 4 chunk let go, 8 wave left, 16 position fell, 32 a gen 2 GC, 64 a gen 0 GC; stuck records that tick; Orks alive):");
            foreach (var x in slow.OrderByDescending(x => x.Ms).Take(5)) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {x.Ms:0.0} ms at {x.Tick / 30f:0.0} s, flags {x.Flags}, stuck {x.Stuck}, alive {x.Alive}"));
        }
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
        if (_diagnose)
            foreach (var grp in diag.GroupBy(d => d.Contains("Relocated") || d.Contains("Removed") ? Hot(d) : "other"))
            { Console.WriteLine($"diagnosis, {grp.Key}: {grp.Count()} records, first 10:"); foreach (var d in grp.Take(10)) Console.WriteLine("  " + d); }
        Report("tick cost, whole run", steps);
        if (waves) Report("tick cost, third wave on the field", stepsBig);
        return 0;
    }

    static string FlowNote(MusouSim m, int node)
    {
        if (m.Flow is not { Ready: true } f || m.Walk is null || node < 0) return "flow n/a";
        int next = f.Next(node);
        string nx = next < 0 ? "no next" : $"next ({m.Walk.X(next):0.0},{m.Walk.Y(next):0.0},{m.Walk.Z(next):0.0}) dist {f.Distance(next):0.0}";
        return $"flow dist {f.Distance(node):0.0} nodeAt ({m.Walk.X(node):0.0},{m.Walk.Y(node):0.0},{m.Walk.Z(node):0.0}) {nx}";
    }

    static string Hot(string d)
    {
        int a = d.IndexOf(" at (", StringComparison.Ordinal) + 5, b = d.IndexOf(')', a);
        var xy = d[a..b].Split(',');
        float x = float.Parse(xy[0], CultureInfo.InvariantCulture), y = float.Parse(xy[1], CultureInfo.InvariantCulture);
        return y < -120f && y > -140f ? "ramp foot" : y is > -60f and < -40f ? "wall west/east of the gate" : y < -140f ? "muster/assembly" : "elsewhere";
    }

    static string Band(float y) => y < -46f ? "ramp and assembly" : y < -30f ? "gate" : y < 40f ? "courtyard" : y < 100f ? "keep hall" : y < 150f ? "tunnels" : "chamber";

    static void Report(string label, List<double> ms)
    {
        if (ms.Count == 0) { Console.WriteLine($"{label}: no ticks"); return; }
        var sorted = ms.OrderBy(x => x).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}: {ms.Count} ticks, p50 {sorted[sorted.Count / 2]:0.00} ms, p99 {sorted[(int)(sorted.Count * 0.99)]:0.00} ms, max {sorted[^1]:0.0} ms (a tick is 33.3 ms)"));
    }
}
