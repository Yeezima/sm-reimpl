using System.Globalization;
using System.Numerics;
using SmSim;

namespace SmServer;

/// <summary>
/// Test aid (bots/new-maps; any match): <c>SM_BOT_STATS=&lt;s&gt;</c> in the server's environment. Every that many seconds one <c>BOTSTATS</c> line for the whole lobby and one
/// <c>BOTPLAYER</c> line per player, cumulative since the first player stood alive: seconds alive, seconds standing still (alive, less than <see cref="StillMetres"/> m over the last
/// <see cref="StillWindow"/> s), seconds near the spawn (alive more than <see cref="SpawnGrace"/> s and still within <see cref="SpawnRadius"/> m of where the life began), mean metres to the
/// nearest enemy, kills, deaths and the deaths by the world (a fall or a kill volume). Measures only; nothing changes without the variable and nothing is sent to clients.
/// </summary>
public sealed partial class GameServer
{
    static readonly double BotStatsSeconds = double.TryParse(Environment.GetEnvironmentVariable("SM_BOT_STATS"), CultureInfo.InvariantCulture, out double bs) && bs > 0.0 ? bs : 0.0;
    const double StillWindow = 6.0, StillMetres = 1.5, SpawnGrace = 20.0, SpawnRadius = 14.0, StatsSample = 0.5;

    sealed class BotStat
    {
        public double Alive, Still, NearSpawn, EnemyMetres, EnemySamples, Travelled;
        public int Kills, Deaths, WorldDeaths;
        public bool WasAlive;
        public Vector3 LifeStart, Last;
        public double LifeSeconds;
        public readonly Queue<Vector3> Trail = new();
        public Team Team;
        public string Class = "";
    }

    readonly Dictionary<int, BotStat> _botStats = new();
    double _botStatsNextSample, _botStatsNextPrint;
    int _botStatsKillSeq;
    double _botStatsStarted = double.NaN;

    void BotStatsTick()
    {
        if (BotStatsSeconds <= 0.0) return;
        double now = _clock.Now.TotalSeconds;
        if (now < _botStatsNextSample) return;
        _botStatsNextSample = now + StatsSample;
        var players = World.Players;
        foreach (var k in World.KillLog)
        {
            if (k.Sequence <= _botStatsKillSeq) continue;
            _botStatsKillSeq = k.Sequence;
            if (_botStats.TryGetValue(k.Victim, out var v))
            {
                v.Deaths++;
                if (k.Cause == KillCause.World)
                {
                    v.WorldDeaths++;
                    if (players.TryGetValue(k.Victim, out var fell))   // where a player died with no attacker (a fall or a kill volume)
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"BOTFALL P{k.Victim} {fell.Team} at {fell.Position.X:F0},{fell.Position.Y:F0},{fell.Position.Z:F0}, last life {v.LifeSeconds:F0} s"));
                }
            }
            if (k.Killer != 0 && k.Killer != k.Victim && _botStats.TryGetValue(k.Killer, out var s)) s.Kills++;
        }
        foreach (var (id, p) in players)
        {
            if (!_botStats.TryGetValue(id, out var st)) _botStats[id] = st = new BotStat();
            st.Team = p.Team;
            if (!p.Alive) { st.WasAlive = false; st.Trail.Clear(); continue; }
            if (double.IsNaN(_botStatsStarted)) { _botStatsStarted = now; _botStatsNextPrint = now + BotStatsSeconds; }
            if (!st.WasAlive) { st.WasAlive = true; st.LifeStart = p.Position; st.Last = p.Position; st.LifeSeconds = 0; st.Trail.Clear(); }
            st.Travelled += Vector3.Distance(st.Last, p.Position) < 20f ? Vector3.Distance(st.Last, p.Position) : 0f;
            st.Last = p.Position;
            st.LifeSeconds += StatsSample;
            st.Alive += StatsSample;
            st.Trail.Enqueue(p.Position);
            while (st.Trail.Count > (int)(StillWindow / StatsSample) + 1) st.Trail.Dequeue();
            if (st.Trail.Count > (int)(StillWindow / StatsSample) && Vector3.Distance(st.Trail.Peek(), p.Position) < StillMetres) st.Still += StatsSample;
            if (st.LifeSeconds > SpawnGrace && Vector2.Distance(new(st.LifeStart.X, st.LifeStart.Y), new(p.Position.X, p.Position.Y)) < SpawnRadius) st.NearSpawn += StatsSample;
            float nearest = float.MaxValue;
            foreach (var (oid, o) in players) if (o.Alive && o.Team != p.Team && o.Team != Team.None) nearest = MathF.Min(nearest, Vector3.Distance(o.Position, p.Position));
            if (nearest < float.MaxValue) { st.EnemyMetres += nearest; st.EnemySamples++; }
        }
        if (double.IsNaN(_botStatsStarted) || now < _botStatsNextPrint) return;
        _botStatsNextPrint = now + BotStatsSeconds;
        double minutes = (now - _botStatsStarted) / 60.0;
        double alive = _botStats.Values.Sum(s => s.Alive), still = _botStats.Values.Sum(s => s.Still), spawn = _botStats.Values.Sum(s => s.NearSpawn);
        int kills = _botStats.Values.Sum(s => s.Kills), deaths = _botStats.Values.Sum(s => s.Deaths), world = _botStats.Values.Sum(s => s.WorldDeaths);
        double enemyM = _botStats.Values.Sum(s => s.EnemyMetres), enemyN = _botStats.Values.Sum(s => s.EnemySamples);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BOTSTATS {minutes:F1} min, {_botStats.Count} players: kills {kills} ({kills / Math.Max(minutes, 0.01):F1}/min), deaths {deaths}, world deaths {world}, alive {alive:F0} s, still {100 * still / Math.Max(alive, 1):F1}%, near spawn {100 * spawn / Math.Max(alive, 1):F1}%, mean enemy distance {enemyM / Math.Max(enemyN, 1):F0} m, walked {_botStats.Values.Sum(s => s.Travelled) / Math.Max(alive, 1):F2} m/s"));
        foreach (var (id, s) in _botStats.OrderBy(kv => kv.Key))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"BOTPLAYER {id} team {s.Team}: alive {s.Alive:F0} s, still {100 * s.Still / Math.Max(s.Alive, 1):F1}%, near spawn {100 * s.NearSpawn / Math.Max(s.Alive, 1):F1}%, enemy {s.EnemyMetres / Math.Max(s.EnemySamples, 1):F0} m, walked {s.Travelled / Math.Max(s.Alive, 1):F2} m/s, kills {s.Kills}, deaths {s.Deaths}, world {s.WorldDeaths}"));
    }
}
