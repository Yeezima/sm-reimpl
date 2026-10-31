using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>What one <see cref="MusouDirector.Step"/> changed that the World acts on.</summary>
[Flags]
public enum MusouDirectorChange : byte
{
    None = 0,
    /// <summary>A break began: refill the living players' health and ammunition.</summary>
    BreakStarted = 1,
    /// <summary>A wave began.</summary>
    WaveStarted = 2,
    /// <summary>The Warboss came.</summary>
    BossSpawned = 4,
}

/// <summary>
/// A player's Musou score this round: KOs, points (each KO the type's points times the chain multiplier, rounded), the chain (KOs in a row, each within
/// <see cref="MusouRules.ChainSeconds"/> of the one before), the multiplier now and the tick of the last KO.
/// </summary>
public readonly record struct MusouPlayerScore(int Kos, int Points, int Chain, float Multiplier, uint LastKoTick);

/// <summary>
/// The Musou director (MU1; design section B, rules ours unless noted): the waves, the breaks, the boss and the shared lives pool. In play
/// (<see cref="Start"/>) it releases each wave's enemies from the spawn gates, at most <see cref="MusouRules.ReleasesPerTick"/> a tick while fewer than the
/// wave's cap are alive (scaled by 1 + 0.5 x (players - 1), never over <see cref="MusouRules.HardCap"/>), types interleaved by their shares and the Nobs spread
/// through the wave. A wave is over when all its enemies are released and dead; then a break of <see cref="MusouRules.BreakSeconds"/> (the World refills the
/// players at its start) and the next wave. When the last wave has released everything the Warboss comes (a scaled Nob for now; MU3 gives him his abilities);
/// his death wins. Losing (every player dead, no life left) is the World's call (<see cref="Lose"/>).
/// <para>
/// Gates (<see cref="Gates"/>): up to half from the map's spawn markers, chosen farthest apart, the rest farthest-point nav nodes in open ground that can walk
/// to a marker; on the flat ground a ring of 30 m. A gate releases only while every living player is at least <see cref="MusouRules.GateMinDistance"/> away
/// (and, with a flow field, can walk to a player); none such: the gate farthest from the players (ours). Releases rotate through the gates and through the
/// nodes around each.
/// </para>
/// </summary>
public sealed class MusouDirector
{
    readonly MusouSim _sim;
    readonly MusouRules _rules;
    readonly List<Gate> _gates = new();
    readonly List<Vector3> _gatePositions = new();
    readonly List<ushort> _stranded = new();
    int _gateCursor, _spawned;
    int[] _mixCounts = [];
    readonly Dictionary<int, MusouPlayerScore> _scores = new();

    sealed record Gate(Vector3 Position, int Node, Vector3[] Spots, int[] SpotNodes);

    public MusouDirector(MusouSim sim, IReadOnlyList<Vector3> spawnMarkers)
    {
        _sim = sim;
        _rules = sim.Rules;
        BuildGates(spawnMarkers);
        foreach (var g in _gates) _gatePositions.Add(g.Position);
        Reset();
    }

    /// <summary>Where the enemies come from (the gates' centres).</summary>
    public IReadOnlyList<Vector3> Gates => _gatePositions;
    /// <summary>Stranded enemies the failsafe moved to a gate, and removed (a KO for nobody), this round.</summary>
    public int Relocated { get; private set; }
    public int Removed { get; private set; }
    public MusouPhase Phase { get; private set; }
    /// <summary>The wave under way or last played (1-based; 0 before the first).</summary>
    public int Wave { get; private set; }
    public int Waves => _rules.Waves.Count;
    /// <summary>The wave scale (1 + 0.5 x (players - 1) at the wave's start).</summary>
    public float Scale { get; private set; } = 1f;
    /// <summary>This wave's enemies in all (its scaled total and its Nobs), released so far and KO'd so far.</summary>
    public int WaveTotal { get; private set; }
    public int Released { get; private set; }
    public int Killed { get; private set; }
    /// <summary>This wave's cap of enemies alive at once.</summary>
    public int MaxAlive { get; private set; }
    public int BreakTicksLeft { get; private set; }
    /// <summary>Lives left in the shared pool.</summary>
    public int Lives { get; private set; }
    /// <summary>Players counted into the pool (a later join adds its lives once).</summary>
    public int PlayersCounted { get; private set; }
    public ushort BossId { get; private set; }
    public float BossMaxHealth { get; private set; }
    /// <summary>KOs this round by players (the Space Marines' team score).</summary>
    public int Kos { get; private set; }
    /// <summary>Each player's KOs, points and chain this round.</summary>
    public IReadOnlyDictionary<int, MusouPlayerScore> Scores => _scores;
    /// <summary>
    /// Where the Warboss comes (owner, 2026-10-31: the Chaos spawn room; the World sets the map's Chaos start area here); null: at a gate. On a graph he
    /// stands on the node there, or, when no player can be walked to from it (a room sealed for the enemies), on the reachable node nearest it.
    /// </summary>
    public System.Numerics.Vector3? BossSpawn { get; set; }
    /// <summary>Test aid (ours): no releases while set (the waves and breaks wait).</summary>
    public bool Hold { get; set; }

    /// <summary>A new round: back to waiting (no wave, full pool to come).</summary>
    public void Reset()
    {
        Phase = MusouPhase.Waiting;
        Wave = 0; Scale = 1f; WaveTotal = 0; Released = 0; Killed = 0; MaxAlive = 0; BreakTicksLeft = 0;
        Lives = 0; PlayersCounted = 0; BossId = 0; BossMaxHealth = 0f; Kos = 0;
        _gateCursor = 0; _spawned = 0;
        _summons = 0;
        _scores.Clear();
        Relocated = 0; Removed = 0;
    }

    int _summons;
    MusouEnemyType _summonType;

    /// <summary>Enemies a summon (the Warboss's Waaagh!) has yet to release.</summary>
    public int Summons => _summons;

    /// <summary>
    /// MU3, the Warboss's Waaagh!: <paramref name="count"/> more enemies of <paramref name="type"/> come through the gates during the boss (as a wave's: at most
    /// <see cref="MusouRules.ReleasesPerTick"/> a tick, under the hard cap); each counts into the wave's enemies when it is released.
    /// </summary>
    public void Summon(MusouEnemyType type, int count)
    {
        if (count <= 0 || Phase != MusouPhase.Boss) return;
        _summonType = type;
        _summons += count;
    }

    /// <summary>Play begins with <paramref name="players"/> players: the lives pool and the first wave.</summary>
    public MusouDirectorChange Start(int players)
    {
        Reset();
        players = Math.Max(1, players);
        PlayersCounted = players;
        Lives = _rules.Lives + _rules.LivesPerExtraPlayer * (players - 1);
        BeginWave(1, players);
        return MusouDirectorChange.WaveStarted;
    }

    /// <summary>More players than counted: each new one adds its lives (ours).</summary>
    public void CountPlayers(int players)
    {
        if (Phase == MusouPhase.Waiting || players <= PlayersCounted) return;
        Lives += _rules.LivesPerExtraPlayer * (players - PlayersCounted);
        PlayersCounted = players;
    }

    /// <summary>A respawn took a life.</summary>
    public void SpendLife() { if (Lives > 0) Lives--; }

    /// <summary>Every player is dead and no life is left.</summary>
    public void Lose() { if (Phase is MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss) Phase = MusouPhase.Lost; }

    /// <summary>A KO: counted for the wave (and for the score when a player made it); the Warboss's wins.</summary>
    public void OnKill(MusouKill kill)
    {
        if (Phase is MusouPhase.Waiting or MusouPhase.Won or MusouPhase.Lost) return;
        if (kill.Player != 0)
        {
            Kos++;
            var was = _scores.GetValueOrDefault(kill.Player);
            uint tick = _sim.Tick;
            bool chained = was.Kos > 0 && tick - was.LastKoTick <= (uint)MathF.Round(_rules.ChainSeconds * _sim.TickRate);
            int chain = chained ? was.Chain + 1 : 0;
            float multiplier = MathF.Min(_rules.ChainMax, 1f + _rules.ChainStep * chain);
            _scores[kill.Player] = new MusouPlayerScore(was.Kos + 1, was.Points + (int)MathF.Round(_rules.Of(kill.Type).Points * multiplier), chain, multiplier, tick);
        }
        if (kill.Enemy == BossId && BossId != 0) { Phase = MusouPhase.Won; return; }
        Killed++;
    }

    void BeginWave(int wave, int players)
    {
        Wave = wave;
        Phase = MusouPhase.Wave;
        var spec = _rules.Waves[wave - 1];
        Scale = 1f + _rules.ScalePerExtraPlayer * (Math.Max(1, players) - 1);
        int total = Math.Max(0, (int)MathF.Round(spec.Total * Scale));
        WaveTotal = total + spec.Nobs;
        MaxAlive = Math.Clamp((int)MathF.Round(spec.MaxAlive * Scale), 1, Math.Min(_rules.HardCap, MusouSim.Capacity));
        Released = 0; Killed = 0;
        _mixCounts = new int[spec.Mix.Count];
        _nobsReleased = 0;
    }

    int _nobsReleased;

    /// <summary>One tick in play: the releases, the end of a wave, the break, the boss.</summary>
    public MusouDirectorChange Step(ReadOnlySpan<MusouTarget> targets)
    {
        var change = MusouDirectorChange.None;
        int players = targets.Length;
        Unstrand(targets);
        switch (Phase)
        {
            case MusouPhase.Wave:
            {
                var spec = _rules.Waves[Wave - 1];
                if (!Hold) Release(spec, targets);
                bool all = Released >= WaveTotal;
                if (all && Wave == Waves)
                {
                    if (SpawnBoss(targets)) { Phase = MusouPhase.Boss; change |= MusouDirectorChange.BossSpawned; }
                }
                else if (all && Killed >= WaveTotal)
                {
                    Phase = MusouPhase.Break;
                    BreakTicksLeft = Math.Max(1, (int)MathF.Round(_rules.BreakSeconds * _sim.TickRate));
                    change |= MusouDirectorChange.BreakStarted;
                }
                break;
            }
            case MusouPhase.Boss:
                // MU3: the Waaagh!'s summons.
                for (int k = 0; !Hold && k < _rules.ReleasesPerTick && _summons > 0 && _sim.Alive < Math.Min(_rules.HardCap, MusouSim.Capacity); k++)
                {
                    if (SpawnAtGate(_summonType, _rules.Of(_summonType).Health, targets) == 0) break;
                    _summons--; WaveTotal++; Released++;
                }
                break;
            case MusouPhase.Break:
                if (Hold) break;
                if (--BreakTicksLeft <= 0) { BreakTicksLeft = 0; BeginWave(Wave + 1, players); change |= MusouDirectorChange.WaveStarted; }
                break;
        }
        return change;
    }

    void Release(MusouWaveSpec spec, ReadOnlySpan<MusouTarget> targets)
    {
        int total = WaveTotal - spec.Nobs;
        for (int k = 0; k < _rules.ReleasesPerTick && Released < WaveTotal && _sim.Alive < MaxAlive && _sim.Alive < _rules.HardCap; k++)
        {
            // The Nobs come at even points through the wave; the rest by the largest shortfall against their shares (ties: the first listed).
            MusouEnemyType type;
            int mixReleased = Released - _nobsReleased, pick = -1;
            bool nob = _nobsReleased < spec.Nobs && (mixReleased >= total || mixReleased >= (long)(_nobsReleased + 1) * total / (spec.Nobs + 1));
            if (nob) type = MusouEnemyType.Nob;
            else
            {
                float most = float.MinValue, sum = 0f;
                for (int m = 0; m < spec.Mix.Count; m++) sum += MathF.Max(0f, spec.Mix[m].Share);
                for (int m = 0; m < spec.Mix.Count; m++)
                {
                    float shortfall = MathF.Max(0f, spec.Mix[m].Share) / MathF.Max(1e-6f, sum) * (mixReleased + 1) - _mixCounts[m];
                    if (shortfall > most) { most = shortfall; pick = m; }
                }
                type = pick >= 0 ? spec.Mix[pick].Type : MusouEnemyType.Boy;
            }
            if (SpawnAtGate(type, _rules.Of(type).Health, targets) == 0) break;
            if (nob) _nobsReleased++;
            else if (pick >= 0) _mixCounts[pick]++;
            Released++;
        }
    }

    /// <summary>
    /// The stranded-enemy failsafe (<see cref="MusouRules.StrandedSeconds"/>): each enemy the simulation listed goes to the next gate clear of the players;
    /// one already moved <see cref="MusouRules.StrandedRelocations"/> times is removed instead, a KO for nobody (the wave's count goes on). The Warboss is
    /// only ever moved. It runs while <see cref="Hold"/> is set too (that holds only the releases), not out of play.
    /// </summary>
    void Unstrand(ReadOnlySpan<MusouTarget> targets)
    {
        _stranded.Clear();
        _sim.TakeStranded(_stranded);
        if (Phase is not (MusouPhase.Wave or MusouPhase.Break or MusouPhase.Boss)) return;
        foreach (ushort id in _stranded)
        {
            if (!_sim.TryGet(id, out var e) || !e.Alive) continue;
            if (id == BossId || _sim.Relocations(id) < _rules.StrandedRelocations)
            {
                var (at, node) = NextGateSpot(targets);
                if (_sim.Relocate(id, at, node)) Relocated++;
            }
            else if (_sim.Damage(id, 0, e.Health, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World)) Removed++;
        }
    }

    bool SpawnBoss(ReadOnlySpan<MusouTarget> targets)
    {
        float health = _rules.BossHealth * Scale;
        ushort id = BossSpawn is { } room ? SpawnInRoom(room, health, targets) : SpawnAtGate(MusouEnemyType.Warboss, health, targets);
        if (id == 0) return false;
        BossId = id;
        BossMaxHealth = health;
        return true;
    }

    ushort SpawnInRoom(Vector3 room, float health, ReadOnlySpan<MusouTarget> targets)
    {
        var at = room;
        int node = -1;
        if (_sim.Walk is { } nav)
        {
            node = nav.Nearest(room, 3f, 3f);
            if (_sim.Flow is { Ready: true } flow && (node < 0 || float.IsPositiveInfinity(flow.Distance(node))))
            {
                // Sealed for the enemies (or off the graph): the nearest node a player can be walked to from.
                float best = float.PositiveInfinity;
                for (int n = 0; n < nav.NodeCount; n++)
                {
                    if (float.IsPositiveInfinity(flow.Distance(n))) continue;
                    float d = Vector3.DistanceSquared(nav.Position(n), room);
                    if (d < best) { best = d; node = n; }
                }
            }
            if (node >= 0) at = nav.Position(node);
        }
        var toward = NearestPlayer(at, targets) is { } p ? new Vector2(p.X - at.X, p.Y - at.Y) : Vector2.UnitY;
        float yaw = toward.LengthSquared() > 1e-6f ? MathF.Atan2(-toward.X, toward.Y) : 0f;
        return _sim.Spawn(MusouEnemyType.Warboss, at, yaw, health, node);
    }

    /// <summary>Spawns at the next usable gate (one of the nodes around it, in turn); its id, or 0 (no gate, the pool is full).</summary>
    ushort SpawnAtGate(MusouEnemyType type, float health, ReadOnlySpan<MusouTarget> targets)
    {
        if (_gates.Count == 0) return 0;
        var (at, node) = NextGateSpot(targets);
        var toward = NearestPlayer(at, targets) is { } p ? new Vector2(p.X - at.X, p.Y - at.Y) : Vector2.UnitY;
        float yaw = toward.LengthSquared() > 1e-6f ? MathF.Atan2(-toward.X, toward.Y) : 0f;
        return _sim.Spawn(type, at, yaw, health, node);
    }

    /// <summary>The next usable gate's next spot (a gate at least the gate distance from every living player that can walk to one; none: the farthest).</summary>
    (Vector3 At, int Node) NextGateSpot(ReadOnlySpan<MusouTarget> targets)
    {
        int chosen = -1;
        float min2 = _rules.GateMinDistance * _rules.GateMinDistance;
        for (int k = 0; k < _gates.Count; k++)
        {
            int g = (_gateCursor + k) % _gates.Count;
            if (NearestPlayerSquared(_gates[g].Position, targets) < min2) continue;
            if (_sim.Flow is { Ready: true } flow && _gates[g].Node >= 0 && float.IsPositiveInfinity(flow.Distance(_gates[g].Node)) && AnyAlive(targets)) continue;
            chosen = g;
            break;
        }
        if (chosen < 0)
        {
            float far = -1f;
            for (int g = 0; g < _gates.Count; g++)
            {
                float d = NearestPlayerSquared(_gates[g].Position, targets);
                if (d > far) { far = d; chosen = g; }
            }
        }
        _gateCursor = (chosen + 1) % _gates.Count;
        var gate = _gates[chosen];
        int spot = _spawned++ % gate.Spots.Length;
        return (gate.Spots[spot], gate.SpotNodes[spot]);
    }

    static bool AnyAlive(ReadOnlySpan<MusouTarget> targets)
    {
        foreach (var t in targets) if (t.Alive) return true;
        return false;
    }

    static float NearestPlayerSquared(Vector3 p, ReadOnlySpan<MusouTarget> targets)
    {
        float best = float.PositiveInfinity;
        foreach (var t in targets) if (t.Alive) best = MathF.Min(best, Vector3.DistanceSquared(t.Position, p));
        return best;
    }

    static Vector3? NearestPlayer(Vector3 p, ReadOnlySpan<MusouTarget> targets)
    {
        Vector3? best = null; float d = float.PositiveInfinity;
        foreach (var t in targets)
            if (t.Alive && Vector3.DistanceSquared(t.Position, p) is var e && e < d) { d = e; best = t.Position; }
        return best;
    }

    /// <summary>The view clients get (tag 5).</summary>
    public MusouDirectorView View()
    {
        float bossHealth = BossId != 0 && _sim.TryGet(BossId, out var boss) ? boss.Health : 0f;
        int left = Phase switch
        {
            MusouPhase.Wave => WaveTotal - Killed,
            MusouPhase.Boss => Math.Max(0, WaveTotal - Killed) + _summons + (bossHealth > 0f ? 1 : 0),   // MU3: summons still to come count
            _ => 0,
        };
        return new MusouDirectorView((byte)Math.Clamp(Wave, 0, 255), (byte)Math.Clamp(Waves, 0, 255), Phase, (ushort)Math.Clamp(BreakTicksLeft, 0, ushort.MaxValue),
            (ushort)Math.Clamp(left, 0, ushort.MaxValue), (ushort)Math.Clamp(_sim.Alive, 0, ushort.MaxValue), BossId, bossHealth, BossMaxHealth,
            (byte)Math.Clamp(Lives, 0, 255), _sim.Box);
    }

    // ---------------------------------------------------------------- the gates

    void BuildGates(IReadOnlyList<Vector3> markers)
    {
        int count = Math.Max(1, _rules.Gates);
        var nav = _sim.Walk;
        if (nav is null)
        {
            // The flat ground: a ring of 30 m around the origin.
            for (int g = 0; g < count; g++)
            {
                float a = g * MathF.Tau / count;
                var c = new Vector3(MathF.Cos(a) * 30f, MathF.Sin(a) * 30f, 0f);
                var spots = new Vector3[9];
                for (int s = 0; s < spots.Length; s++) spots[s] = c + new Vector3((s % 3 - 1) * 1.2f, (s / 3 - 1) * 1.2f, 0f);
                _gates.Add(new Gate(c, -1, spots, Enumerable.Repeat(-1, spots.Length).ToArray()));
            }
            return;
        }
        // Nodes that can walk to a spawn marker (a field from the markers), so a gate is never in a pocket the horde cannot leave.
        var markerNodes = markers.Select(m => nav.Nearest(m, 3f, 3f)).Where(n => n >= 0).Distinct().ToArray();
        var reach = new FlowField(nav);
        if (markerNodes.Length > 0) reach.Complete(markerNodes);
        bool Reaches(int n) => markerNodes.Length == 0 || !float.IsPositiveInfinity(reach.Distance(n));
        bool Open(int n)
        {
            int walks = 0;
            for (int l = nav.ForwardStart(n); l < nav.ForwardEnd(n); l++) walks++;
            return walks >= 8;
        }
        var chosen = new List<int>();
        var centre = Centre(nav);
        void FarthestFrom(IReadOnlyList<int> pool, int want)
        {
            while (chosen.Count < want)
            {
                int best = -1; float bestD = -1f;
                foreach (int n in pool)
                {
                    if (chosen.Contains(n)) continue;
                    float d = float.PositiveInfinity;
                    foreach (int c in chosen) d = MathF.Min(d, Vector3.DistanceSquared(nav.Position(c), nav.Position(n)));
                    if (chosen.Count == 0) d = Vector3.DistanceSquared(nav.Position(n), centre);
                    if (d > bestD) { bestD = d; best = n; }
                }
                if (best < 0) return;
                chosen.Add(best);
            }
        }
        FarthestFrom(markerNodes.Where(Reaches).ToList(), Math.Min(count / 2, markerNodes.Length));
        var open = new List<int>();
        for (int n = 0; n < nav.NodeCount; n += 7) if (Open(n) && Reaches(n)) open.Add(n);
        if (open.Count == 0) for (int n = 0; n < nav.NodeCount; n += 7) open.Add(n);
        FarthestFrom(open, count);
        foreach (int node in chosen)
        {
            // The spots: the gate's node and the nodes a few walk links around it (breadth first, in link order).
            var spots = new List<int> { node };
            for (int k = 0; k < spots.Count && spots.Count < 24; k++)
                for (int l = nav.ForwardStart(spots[k]); l < nav.ForwardEnd(spots[k]) && spots.Count < 24; l++)
                {
                    int t = nav.ForwardTo(l);
                    if (!spots.Contains(t) && Vector3.DistanceSquared(nav.Position(t), nav.Position(node)) <= 6.25f && MathF.Abs(nav.Z(t) - nav.Z(node)) < 0.5f) spots.Add(t);
                }
            // Spread the order so consecutive releases stand apart.
            var order = spots.Where((_, k) => k % 2 == 0).Concat(spots.Where((_, k) => k % 2 == 1)).ToArray();
            _gates.Add(new Gate(nav.Position(node), node, order.Select(nav.Position).ToArray(), order));
        }
    }

    static Vector3 Centre(MusouNav nav)
    {
        var box = nav.Box();
        return (box.Min + box.Max) * 0.5f;
    }
}
