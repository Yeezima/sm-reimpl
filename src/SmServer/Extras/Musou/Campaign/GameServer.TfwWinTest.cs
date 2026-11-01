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
/// spread and the 3 m cells it covers); with it also a <c>TFWSWARM</c> line when a spawn point's crowd is let loose, a <c>TFWARRIVE</c> line when the first of that swarm is within 25 m of a human (with the time since the
/// last swarm's arrival) and a <c>TFWQUIET</c> line for each stretch of 2 s or more with no awake Ork within 25 m of any human. <c>SM_TFW_KEEP_HUMAN=1</c>: a human in the field who falls stands up where it fell (a scripted player stays in the fight while it is filmed). Neither touches an enemy. <c>SM_TFW_GUARDSMEN=1</c>: battles land Guardsmen, whatever <see cref="TfwBattleRules.GuardsmenEnabled"/> says.
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
    TfwBattle? _tfwHooked;
    readonly List<(TfwSwarm Swarm, uint Released)> _tfwWatching = new();
    uint _tfwLastArrival, _tfwQuietFrom;
    const float TfwNearHuman = 25f;

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

    /// <summary>Test aid: the swarms' release and arrival times, and the quiet between them (<c>SM_TFW_FIGHT_LOG</c>).</summary>
    void TfwSwarmWatch(TfwRun run, TfwBattle battle, MusouSim orks, int rate)
    {
        if (battle != _tfwHooked)
        {
            _tfwHooked = battle;
            _tfwWatching.Clear();
            _tfwLastArrival = 0; _tfwQuietFrom = 0;
            for (int k = 0; k < 3; k++)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TFWPOINT {(char)('A' + k)} at {battle.Layout.Positions[k].X:F1},{battle.Layout.Positions[k].Y:F1},{battle.Layout.Positions[k].Z:F1}: radius {battle.Layout.Radius(k):F1} m, vertical reach {battle.Layout.Reach(k):F1} m"));
            battle.OnSwarm = swarm =>
            {
                _tfwWatching.Add((swarm, World.Tick));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TFWSWARM tick {World.Tick}: point {swarm.Point} size {swarm.Size} {swarm.Why}, crowds {string.Join("/", Enumerable.Range(0, 4).Select(battle.Pooled))}, alive {orks.Alive}"));
            };
        }
        var humans = new List<System.Numerics.Vector3>();
        foreach (int id in run.InField) if (World.Players.TryGetValue(id, out var p) && p.Alive) humans.Add(p.Position);
        float near2 = TfwNearHuman * TfwNearHuman;
        bool Near(System.Numerics.Vector3 at)
        {
            foreach (var h in humans) if ((at.X - h.X) * (at.X - h.X) + (at.Y - h.Y) * (at.Y - h.Y) <= near2) return true;
            return false;
        }
        for (int w = _tfwWatching.Count - 1; w >= 0; w--)
        {
            var (swarm, released) = _tfwWatching[w];
            bool any = false, arrived = false;
            foreach (ushort id in swarm.Ids)
            {
                if (!orks.TryGet(id, out var e) || !e.Alive) continue;
                any = true;
                if (Near(e.Position)) { arrived = true; break; }
            }
            if (!any) { _tfwWatching.RemoveAt(w); continue; }
            if (!arrived) continue;
            string since = _tfwLastArrival == 0 ? "the first" : $"{(World.Tick - _tfwLastArrival) / (double)rate:F1} s after the last";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TFWARRIVE tick {World.Tick}: point {swarm.Point} size {swarm.Size} {swarm.Why}, released at tick {released}, walked {(World.Tick - released) / (double)rate:F1} s, {since}"));
            _tfwLastArrival = World.Tick;
            _tfwWatching.RemoveAt(w);
        }
        _tfwFightEnemies ??= new MusouEnemy[MusouSim.Capacity];
        int n = orks.CopyTo(_tfwFightEnemies);
        bool awake = false;
        for (int k = 0; k < n && !awake && humans.Count > 0; k++) awake = _tfwFightEnemies[k].Alive && !orks.Leashed(_tfwFightEnemies[k].Id) && Near(_tfwFightEnemies[k].Position);
        if (awake || humans.Count == 0)
        {
            if (_tfwQuietFrom != 0 && World.Tick - _tfwQuietFrom >= 2u * (uint)rate)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TFWQUIET tick {World.Tick}: {(World.Tick - _tfwQuietFrom) / (double)rate:F1} s with no awake Ork within {TfwNearHuman:F0} m of a human (from tick {_tfwQuietFrom})"));
            _tfwQuietFrom = 0;
        }
        else if (_tfwQuietFrom == 0) _tfwQuietFrom = World.Tick;
    }

    void TfwFightLog(TfwRun run, int rate)
    {
        if (TfwFightSeconds <= 0.0 || World.Musou is not { } orks || World.TfwGuards is not { } guards || run.Battle is not { } battle) return;
        if (orks.LastStepMilliseconds >= 40.0) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"TFWSLOW tick {World.Tick}: fight step {orks.LastStepMilliseconds:F1} ms, guards {guards.LastStepMilliseconds:F1} ms, generation 2 collections {GC.CollectionCount(2)}"));
        TfwSwarmWatch(run, battle, orks, rate);
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
            $"TFWFIGHT tick {World.Tick}: orks {orks.Alive}, melee tokens {melee}, shooter tokens {shooters}, striking {striking}, moving {moving}, heading {heading:F2}, cells {cells.Count}; guards {guards.Alive}, lost {guards.Lost}, shots {guards.Shots}, hit {guards.ShotsHit}; killed {battle.OrksKilled}, sent {battle.Arrivals}, crowds {string.Join("/", Enumerable.Range(0, 4).Select(battle.Pooled))}, swarms {battle.Swarms}, orks on points {string.Join("/", Enumerable.Range(0, 3).Select(battle.OrksOn))}{string.Concat(Enumerable.Range(0, 3).Where(k => battle.Objectives[k].State == TfwPositionState.Clearing).Select(k => $" [clearing {(char)('A' + k)}: {battle.OrksOnReport(k)}]"))}; points {points}; squads {squads}; marines {marines}; step {orks.LastStepMilliseconds:F2} ms; down {orks.KnockedDown}, down dead {orks.KnockKills}, best cleave {World.TfwBestCleave}, best pierce {World.TfwBestPierce}, frags out {World.TfwFragsOut}"));
    }
}
