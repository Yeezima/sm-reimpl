using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>What one scenario run measured (Guardsmen G0, 2026-11-26): the outcome, the points lost and the Guardsmen and squads standing, with the tick cost over the ticks a Guardsman stood.</summary>
public sealed record WarfrontLineTrialResult(
    string Scenario, int Seed, int Target, int WaveSize, double WaveElites, string Outcome, int PointsLost, int PointsSecured, double FirstLossSeconds,
    int GuardsmenStart, int GuardsmenEnd, int GuardsmenMin, int SquadsStart, int SquadsEnd, int OrksPeak, int OrksEnd, int NaturalWaves, int Seconds,
    double TickMean, double TickP50, double TickP95, double TickP99, double TickWorst, double MeanGuardsmen, double MeanOrks, int Ticks, string Hash);

/// <summary>
/// The battle-level trial of the Guardsmen's line (design-guardsmen-1125, G0): one lane battle in-process, the points secured, the scripted human standing at the landing zone, then one of five scripted pushes:
/// <c>routine</c> (a 200-Ork low-elite wave on one point), <c>elite</c> (the same size, 0.6 elites), <c>flank</c> (the routine wave and a second Ork army from another bearing at the same point), <c>collapse</c> (a 400-Ork,
/// 0.6-elite wave: no help comes) and <c>break</c> (as collapse, then at 90 s the human walks onto the point and for 60 s the Orks within 40 m die each second, as the Marines breaking the push). Seeded: the seed only moves the wave's
/// delay (0 to 5 s), its size (+/- 10 %), its elite share (+/- 0.05) and the flank's side, so the same seed is the same run. Reads the simulation only through its public test aids; the simulation is unchanged.
/// The same logic is copied into <c>smtool warfront-guards --scenario</c> (SmTool is not referenced by the tests), which runs it on the real lane maps; keep the two in step.
/// </summary>
public static class WarfrontLineTrial
{
    /// <summary>The Orks' endless trickle from the sources (TfwBattle.Trickle): off for the scripted pushes, so the wave alone is the pressure; on only to see the whole tide.</summary>
    public static bool TrickleOn { get; set; }
    /// <summary>Keeps the HQ stronghold's own crowd dead (every second, the Orks that belong to no army within 50 m of its source): once every point is secured the HQ fills its crowd and swarms it, a tide that wipes any line in 2 minutes and would hide the scripted push. False (<c>--hq-tide</c>) lets it come.</summary>
    public static bool QuietHq { get; set; } = true;
    public static readonly string[] Scenarios = ["routine", "elite", "flank", "collapse", "break"];
    public const int Human = 1, Points = 3;   // the lane battle's three capture points (a fourth objective, where a test arena has one, is the commander's)
    public const float BreakAtSeconds = 90f, BreakRadius = 40f, FlankDistance = 80f;

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
            if (brk && t >= breakAt && t <= breakAt + 60 * 30 && (t == breakAt || t % 30 == 0))
            {
                if (t == breakAt) w.TfwRespawnAt(Human, point, 0f);
                var all = new MusouEnemy[MusouSim.Capacity];
                int n = w.Musou!.CopyTo(all);
                for (int k = 0; k < n; k++)
                    if (all[k].Alive && new Vector2(all[k].Position.X - point.X, all[k].Position.Y - point.Y).Length() <= BreakRadius) w.Musou.Damage(all[k].Id, Human, 1e6f, Vector3.Zero, out _);
            }
            if (QuietHq && t % 30 == 0) QuietHqCrowd(w, battle);
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
            steps.Count == 0 ? 0 : steps.Average(), P(0.5), P(0.95), P(0.99), steps.Count == 0 ? 0 : steps[^1], liveGuards / (double)Math.Max(1, steps.Count), liveOrks / (double)Math.Max(1, steps.Count), steps.Count, hash);
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
