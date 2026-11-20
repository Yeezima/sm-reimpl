using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
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
        string slotName = "wf_a1", frames = ""; int frameEvery = 15, framePoint = 0; int seconds = 240;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--slot" when i + 1 < rest.Length: slotName = rest[++i]; break;
                case "--seconds" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int s): seconds = Math.Clamp(s, 10, 900); i++; break;
                case "--frames" when i + 1 < rest.Length: frames = Path.GetFullPath(rest[++i]); break;
                case "--frame-point" when i + 1 < rest.Length && int.TryParse(rest[i + 1], out int fp): framePoint = Math.Clamp(fp, 0, 2); i++; break;
                default: Console.Error.WriteLine($"warfront-guards: unknown argument \"{rest[i]}\" (usage: warfront-guards [--slot wf_a1] [--seconds 240] [--frames file.rgb] [--frame-point 0..2])"); return 2;
            }
        }
        var total = Stopwatch.StartNew();
        if (WarfrontSlots.ByName(slotName) is not { } slot) { Console.Error.WriteLine($"warfront-guards: no lane slot \"{slotName}\""); return 2; }
        var map = WarfrontMaps.Load(slotName, out string? problem);
        if (map?.Tfw is null) { Console.Error.WriteLine("warfront-guards: " + (problem ?? "no map")); return 1; }
        var config = Config();
        var nav = WarfrontMaps.Nav(map, config, out string navNote);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{slotName}: map {map.Hash:X16}, graph {nav.NodeCount:N0} nodes ({(navNote.Length > 80 ? navNote[..80] : navNote)}), {total.Elapsed.TotalSeconds:0.0} s; Guardsmen {(WarfrontGuardRules.Old ? "OLD (SM_WF_GUARD_OLD=1)" : $"x{WarfrontGuardRules.NumberScale}, strength x{WarfrontGuardRules.StrengthScale}")}"));
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = map.StartAreas };
        if (!w.StartTfw(nav, map.Tfw.Field)) { Console.Error.WriteLine("warfront-guards: the war did not start"); return 1; }
        var run = w.Tfw!;
        run.Guardsmen = true;
        run.Allies = false;
        w.TfwMeksOn = false;
        w.Spawn(1, w.AssignClass(0));
        for (int k = 0; k < 30; k++) w.Step(_ => default);
        run.Enqueue(1, new TfwCommandRecord(1, TfwCommand.Deploy, (byte)slot.Lane));
        for (int k = 0; k < (int)((TfwBattleRules.TravelSeconds + 0.5f) * 30); k++) w.Step(_ => default);
        if (run.Battle is not { } battle) { Console.Error.WriteLine("warfront-guards: no battle"); return 1; }
        var guards = w.TfwGuards!;
        Console.WriteLine($"battle: {battle.Squads} squads in all, {guards.SquadsAlive} on the field, garrison cap {battle.GarrisonCapNow}, {battle.Layout.Positions.Length} points");

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
            if (frameFile is not null && t % frameEvery == 0 && t <= 3600) Frame(frameFile, w, battle, guards, framePoint);
        }
        if (steps.Count == 0) { Console.WriteLine("no tick with a Guardsman standing"); return 1; }
        steps.Sort();
        double P(double q) => steps.Count == 0 ? 0 : steps[Math.Min(steps.Count - 1, (int)(q * steps.Count))];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"tick cost over {steps.Count} ticks with a Guardsman standing (mean {liveGuards / (double)live:0} Guardsmen, {liveOrks / (double)live:0} Orks): mean {steps.Average():0.00} ms, p50 {P(0.5):0.00}, p95 {P(0.95):0.00}, p99 {P(0.99):0.00}, worst {steps[^1]:0.0}; Guardsmen's section mean {(guardMs.Count > 0 ? guardMs.Average() : 0):0.00} ms a tick (windows of 10 s); Guardsmen alive {guards.Alive}, Orks alive {w.Musou!.Alive}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done in {total.Elapsed.TotalSeconds:0} s"));
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

    static SimConfig Config()
    {
        var rifle = new WeaponSpec("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
        var pistol = new WeaponSpec("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
        var light = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        ClassLoadout Class(string name, Team side) => new(name, -1, 1, [light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
        return new SimConfig { Weapons = [rifle, pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentProfiles.WarfrontProfile, RespawnTime = 2f };
    }
}
