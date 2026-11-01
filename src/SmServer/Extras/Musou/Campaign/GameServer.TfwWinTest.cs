using System.Globalization;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Test aids of Musou Mode on a real server (TF9; <c>GameServer.TfwDownTest.cs</c> is their sibling). Off without their variables.
/// <para>
/// <c>SM_TFW_WIN_TEST=&lt;m&gt;</c>: once a second every enemy within that many metres of a human in the field falls, the boss too, credited to that human. The positions, the Guardsmen's walk and securing, the depot, the commander's
/// coming, the win, the free pickup, the front's move and the final assault all stay the run's own: only the fighting is left out, so a scripted player (<c>--tfw-test-win</c>) can walk a whole run.
/// </para>
/// <para>
/// <c>SM_TFW_STRESS=&lt;orks&gt;,&lt;squads&gt;</c>: during a battle the enemies are topped up to <c>orks</c> near the landing zone and the Guard squads to <c>squads</c>, once a second, and a human in the field who falls
/// stands up where it fell, so the load holds for as long as the run is measured (<c>SM_TICK_STATS</c>).
/// </para>
/// <para>
/// <c>SM_TFW_FIGHT_LOG=&lt;s&gt;</c>: every that many seconds of a battle one <c>TFWFIGHT</c> line: the Orks alive and their attack tokens, how many strike, Guardsmen, losses and shots, the points, and how the crowd lies (a heading
/// spread and the 3 m cells it covers). <c>SM_TFW_KEEP_HUMAN=1</c>: a human in the field who falls stands up where it fell (a scripted player stays in the fight while it is filmed). Neither touches an enemy. <c>SM_TFW_GUARDSMEN=1</c>: battles land Guardsmen, whatever <see cref="TfwBattleRules.GuardsmenEnabled"/> says.
/// </para>
/// </summary>
public sealed partial class GameServer
{
    static readonly float TfwWinRadius = float.TryParse(Environment.GetEnvironmentVariable("SM_TFW_WIN_TEST"), CultureInfo.InvariantCulture, out float m) ? m : -1f;
    static readonly (int Orks, int Squads)? TfwStress = ParseStress(Environment.GetEnvironmentVariable("SM_TFW_STRESS"));
    static readonly double TfwFightSeconds = double.TryParse(Environment.GetEnvironmentVariable("SM_TFW_FIGHT_LOG"), CultureInfo.InvariantCulture, out double f) ? f : 0.0;
    static readonly bool TfwKeepHuman = Environment.GetEnvironmentVariable("SM_TFW_KEEP_HUMAN") == "1";
    static readonly bool TfwGuardsmenOn = Environment.GetEnvironmentVariable("SM_TFW_GUARDSMEN") == "1";
    MusouEnemy[]? _tfwFightEnemies;
    double _tfwLandSeen;

    static (int, int)? ParseStress(string? text)
    {
        var parts = text?.Split(',') ?? [];
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int orks) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int squads) ? (orks, squads) : null;
    }

    void TfwTestAids()
    {
        if ((TfwWinRadius < 0f && TfwStress is null && TfwFightSeconds <= 0.0 && !TfwKeepHuman && !TfwGuardsmenOn) || World.Tfw is not { } run) return;
        if (TfwGuardsmenOn) run.Guardsmen = true;
        int rate = Math.Max(1, World.Config.TickRate);
        if (TickStatsSeconds > 0.0 && World.Tick % Math.Max(1u, (uint)(rate * TickStatsSeconds)) == 0)
            Console.WriteLine($"TFWSTATS tick {World.Tick}: {run.Phase}, enemies {World.Musou?.Alive}, guards {World.TfwGuards?.Alive}, marines {run.Marines.Count}, players {World.Players.Count}");
        if (run.Battle is null) return;
        TfwFightLog(run, rate);
        if (TfwStress is not null || TfwKeepHuman)
            foreach (int human in run.InField)
                if (World.Players.TryGetValue(human, out var p) && !p.Alive) World.TfwRespawnAt(human, p.Position, p.Yaw);
        if (World.Tick % (uint)rate != 0) return;
        if (TfwStress is { } fill) World.TfwFillForTests(fill.Orks, fill.Squads);
        if (TfwWinRadius < 0f) return;
        foreach (int human in run.InField)
            if (World.Players.TryGetValue(human, out var p) && p.Alive) World.TfwSmiteForTests(human, p.Position, TfwWinRadius);
    }

    void TfwFightLog(TfwRun run, int rate)
    {
        if (TfwFightSeconds <= 0.0 || World.Musou is not { } orks || World.TfwGuards is not { } guards || run.Battle is not { } battle) return;
        if (orks.LastStepMilliseconds >= 40.0) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TFWSLOW tick {World.Tick}: fight step {orks.LastStepMilliseconds:F1} ms, guards {guards.LastStepMilliseconds:F1} ms, generation 2 collections {GC.CollectionCount(2)}"));
        if (run.LastLandMilliseconds != _tfwLandSeen)
        {
            _tfwLandSeen = run.LastLandMilliseconds;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TFWLAND tick {World.Tick}: landing {run.LastLandMilliseconds:F2} ms, of which the sites {run.LastSitesMilliseconds:F2} ms"));
        }
        if (World.Tick % Math.Max(1u, (uint)(rate * TfwFightSeconds)) != 0) return;
        _tfwFightEnemies ??= new MusouEnemy[MusouSim.Capacity];
        int n = orks.CopyTo(_tfwFightEnemies), moving = 0;
        var cells = new HashSet<(int, int)>();
        float sumX = 0f, sumY = 0f;
        for (int k = 0; k < n; k++)
        {
            var e = _tfwFightEnemies[k];
            if (!e.Alive) continue;
            cells.Add(((int)MathF.Floor(e.Position.X / 3f), (int)MathF.Floor(e.Position.Y / 3f)));
            float speed = e.Velocity.Length();
            if (speed < 1f) continue;
            moving++; sumX += e.Velocity.X / speed; sumY += e.Velocity.Y / speed;
        }
        float heading = moving > 0 ? MathF.Sqrt(sumX * sumX + sumY * sumY) / moving : 0f;   // 1: all one way
        var (melee, shooters, striking) = orks.Tally();
        var points = string.Join(" ", battle.Objectives.Select(o => $"{o.State}:{o.Progress:F2}"));
        var marines = string.Join(" ", run.Marines.Select(m => World.Players.TryGetValue(m.Id, out var p) ? $"{m.Role}:{(p.Alive ? "up" : "down")}@{p.Position.X:F0},{p.Position.Y:F0}" : "gone"));
        var squads = string.Join(" ", Enumerable.Range(0, GuardSim.MaxSquads).Where(s => guards.AliveIn(s) > 0).Select(s => $"{guards.AliveIn(s)}@{guards.Centre(s).X:F0},{guards.Centre(s).Y:F0}{(guards.Arrived(s) ? "" : ">")}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TFWFIGHT tick {World.Tick}: orks {orks.Alive}, melee tokens {melee}, shooter tokens {shooters}, striking {striking}, moving {moving}, heading {heading:F2}, cells {cells.Count}; guards {guards.Alive}, lost {guards.Lost}, shots {guards.Shots}, hit {guards.ShotsHit}; killed {battle.OrksKilled}, sent {battle.Arrivals}; points {points}; squads {squads}; marines {marines}; step {orks.LastStepMilliseconds:F2} ms; down {orks.KnockedDown}, down dead {orks.KnockKills}, best cleave {World.TfwBestCleave}, best pierce {World.TfwBestPierce}, frags out {World.TfwFragsOut}"));
    }
}
