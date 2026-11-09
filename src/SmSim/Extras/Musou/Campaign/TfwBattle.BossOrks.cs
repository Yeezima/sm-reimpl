using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// When and where the roaming Boss Orks come (owner, 2026-11-06; design docs/musou-boss-orks-design.md; every value his or ours, spec "Musou Mode: Boss Orks"). A change raises
/// <see cref="TfwRules.Version"/>.
/// </summary>
public static class TfwBossOrkRules
{
    /// <summary>The first Boss Ork of a battle: this long after the landing, plus up to <see cref="FirstSpread"/> more (the owner: "more often": 1 to 2 min).</summary>
    public const float FirstSeconds = 60f, FirstSpread = 60f;
    /// <summary>The next: this long after the last one came, plus up to <see cref="NextSpread"/> more (the owner: 1.5 to 3 min).</summary>
    public const float NextSeconds = 90f, NextSpread = 90f;
    /// <summary>Most Boss Orks alive at once (the owner: "up to 2"); one that is due waits for a free place.</summary>
    public const int MaxAlive = 2;
    /// <summary>The elites that come with each (the owner: 20, "fight on" after the boss dies): 'Ard Boyz and Nobs in the battle's mix (<see cref="TfwBattleRules.EliteArds"/>).</summary>
    public const int Retinue = 20;   // 16 (80 %) on 2026-11-09; back to 100 % on 2026-11-10 (the owner: "roll back the 20% less orks change"); the 80 % was (the owner: "reduce the total amount of orks on the map to 80% of current"); the Boss Orks themselves unchanged
    /// <summary>The retinue stands on a ring this far round its boss at the spawn (ours; the boss's body is 1.2 to 1.8 m wide).</summary>
    public const float RingMin = 3f, RingMax = 6f;
    /// <summary>A Boss Ork spawns only at a spawn point at least this far from every living player (ours); with none that far it tries again a second later.</summary>
    public const float MinPlayerDistance = 40f;
    /// <summary>Seconds between two tries when one is due and nowhere is far enough, or the pool is full.</summary>
    public const float RetrySeconds = 1f;

    /// <summary>
    /// Most alive at once and the share of the waits since 2026-11-10 (the owner: "double boss and elite enemy spawns", then "let there be up to 8 boss orks alive at the same time and make them spawn more
    /// often"): 8, and half the waits (first 30 to 60 s, next 45 to 90 s); <see cref="TfwBattleRules.OldBigSpawns"/>: <see cref="MaxAlive"/> and the full waits.
    /// </summary>
    public static int MostAlive => TfwBattleRules.OldBigSpawns ? MaxAlive : 8;
    public static float Interval => TfwBattleRules.OldBigSpawns ? 1f : 0.5f;
}

/// <summary>One Boss Ork that came (<see cref="TfwBattle.OnBossOrk"/>, the server's log): its id, kind, full health, where, and its retinue.</summary>
public readonly record struct TfwBossOrkArrival(ushort Id, MusouEnemyType Type, float MaxHealth, Vector3 Position, int Point, ushort[] Retinue);

public sealed partial class TfwBattle
{
    readonly List<ushort> _bossOrks = new();
    readonly Dictionary<ushort, float> _bossOrkMax = new();
    int _bossOrkDue = -1;   // the battle's tick the next one is due on (-1: not yet drawn)
    int _bossOrkCount, _bossOrkPicked;

    /// <summary>Test aid and the server's log: called once for every Boss Ork that comes.</summary>
    public Action<TfwBossOrkArrival>? OnBossOrk { get; set; }
    /// <summary>Whether Boss Orks come in this battle (ours: always in a battle, never in the final assault; tests turn it off to count the crowds alone).</summary>
    public bool BossOrksOn { get; set; } = true;
    /// <summary>The Boss Orks alive now, in the order they came.</summary>
    public IReadOnlyList<ushort> BossOrks => _bossOrks;
    /// <summary>Boss Orks that came in this battle.</summary>
    public int BossOrksCome => _bossOrkCount;
    /// <summary>The battle's tick the next Boss Ork is due on (-1 before the first step).</summary>
    public int BossOrkDueTick => _bossOrkDue;
    /// <summary>The battle's ticks since its start.</summary>
    public int BattleTicks => _now;

    /// <summary>The full health a Boss Ork came with (its bar's 100 %); null for any other enemy.</summary>
    public float? BossOrkMaxHealth(ushort id) => _bossOrkMax.TryGetValue(id, out float max) ? max : null;

    /// <summary>A roll of 0 to 1 that only the battle's places, its elite share (the run's progress) and <paramref name="salt"/> decide (the same battle, the same rolls).</summary>
    float Roll(uint salt)
    {
        uint seed = MusouSim.Mix(BitConverter.SingleToUInt32Bits(_layout.Hq.X), BitConverter.SingleToUInt32Bits(_layout.Hq.Y));
        seed = MusouSim.Mix(seed, BitConverter.SingleToUInt32Bits(_layout.LandingZone.X) ^ BitConverter.SingleToUInt32Bits(_layout.LandingZone.Y));
        foreach (var source in _layout.Sources) seed = MusouSim.Mix(seed, BitConverter.SingleToUInt32Bits(source.X) ^ BitConverter.SingleToUInt32Bits(source.Y) * 31u);
        seed = MusouSim.Mix(seed, BitConverter.SingleToUInt32Bits(EliteShare));   // the run's progress: a later battle on the same sites rolls anew
        return MusouSim.Unit(MusouSim.Mix(seed, salt));
    }

    /// <summary>
    /// Once a tick from <see cref="Step"/> (a battle, not the final assault): the dead are forgotten; when one is due, fewer than <see cref="TfwBossOrkRules.MaxAlive"/> live and the pool has room,
    /// and someone is in the field, a Boss Ork of a random kind comes with its retinue at a sending spawn point far enough from every player, and the next is drawn.
    /// </summary>
    void StepBossOrks(ReadOnlySpan<Vector3> players)
    {
        for (int k = _bossOrks.Count - 1; k >= 0; k--)
            if (!_enemies.TryGet(_bossOrks[k], out var e) || !e.Alive || !MusouBossOrks.Is(e.Type)) { _bossOrkMax.Remove(_bossOrks[k]); _bossOrks.RemoveAt(k); }
        if (_final || !BossOrksOn) return;
        if (_bossOrkDue < 0) _bossOrkDue = _now + Ticks((TfwBossOrkRules.FirstSeconds + TfwBossOrkRules.FirstSpread * Roll(0xB055_0001u)) * TfwBossOrkRules.Interval);
        if (_now < _bossOrkDue || _bossOrks.Count >= TfwBossOrkRules.MostAlive || players.IsEmpty) return;   // none while nobody is in the field (ours): one due comes when someone is
        if (PoolUsed + 1 + EliteBodies(_bossOrkPicked, TfwBossOrkRules.Retinue) > TfwBattleRules.CrowdPoolRoom || !SpawnBossOrk(players))   // his retinue's Nobs' twins counted (review fix 2026-11-10)
        {
            _bossOrkDue = _now + Ticks(TfwBossOrkRules.RetrySeconds);
            return;
        }
        _bossOrkDue = _now + Ticks((TfwBossOrkRules.NextSeconds + TfwBossOrkRules.NextSpread * Roll(0xB055_1000u + (uint)_bossOrkCount)) * TfwBossOrkRules.Interval);
    }

    /// <summary>The spawn point a Boss Ork comes at: of the points that send (<see cref="SourceActive"/>), those at least <see cref="TfwBossOrkRules.MinPlayerDistance"/> from every player, one by a roll; -1 when none is.</summary>
    int BossOrkPoint(ReadOnlySpan<Vector3> players)
    {
        Span<int> far = stackalloc int[Positions + 1];
        int n = 0;
        float min2 = TfwBossOrkRules.MinPlayerDistance * TfwBossOrkRules.MinPlayerDistance;
        for (int point = 0; point <= Positions; point++)
        {
            if (!SourceActive(point) || point == Positions && _layout.Sources.Length <= Positions) continue;
            var at = CrowdAnchor(point);
            bool clear = true;
            foreach (var p in players) if (Vector3.DistanceSquared(p, at) < min2) { clear = false; break; }
            if (clear) far[n++] = point;
        }
        return n == 0 ? -1 : far[Math.Min(n - 1, (int)(Roll(0xB055_2000u + (uint)_bossOrkCount) * n))];
    }

    bool SpawnBossOrk(ReadOnlySpan<Vector3> players)
    {
        int point = BossOrkPoint(players);
        if (point < 0) return false;
        var kinds = MusouBossOrks.Kinds;
        var type = kinds[Math.Min(kinds.Length - 1, (int)(Roll(0xB055_3000u + (uint)_bossOrkCount) * kinds.Length))];
        var anchor = CrowdAnchor(point);
        var at = Ground(anchor, anchor);
        float health = Health(type) * _scale;
        float yaw = FaceCentre(at);
        ushort id = _enemies.Spawn(type, at, yaw, health);
        if (id == 0) return false;
        _bossOrks.Add(id);
        TrackBig(id);   // off the crowds' cap since 2026-11-10
        _bossOrkMax[id] = health;
        _bossOrkCount++;
        var retinue = new List<ushort>(TfwBossOrkRules.Retinue);
        for (int k = 0; k < TfwBossOrkRules.Retinue; k++)
        {
            int n = _bossOrkPicked++;
            var kind = n * 0.7548776662466927 % 1.0 < TfwBattleRules.EliteArds ? MusouEnemyType.ArdBoy : MusouEnemyType.Nob;   // the crowds' elite mix (AddSquad)
            float angle = k * 2.399963f, radius = TfwBossOrkRules.RingMin + (TfwBossOrkRules.RingMax - TfwBossOrkRules.RingMin) * MusouSim.Unit(MusouSim.Mix((uint)n, 0x7265746Eu));
            var spot = Ground(at + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f), at);
            ushort guard = _enemies.Spawn(kind, spot, yaw, Health(kind));
            if (guard == 0) continue;
            retinue.Add(guard);
            if (TfwBattleRules.Big(kind)) { TrackBig(guard); SpawnTwins(kind, spot, yaw, Health(kind), -1, 0f, retinue); }   // a Nob twice since 2026-11-10
        }
        OnBossOrk?.Invoke(new TfwBossOrkArrival(id, type, health, at, point, retinue.ToArray()));
        return true;
    }
}
