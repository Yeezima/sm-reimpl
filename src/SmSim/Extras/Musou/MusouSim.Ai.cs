using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>A player as the enemies see it in a tick (<see cref="MusouSim.Step"/>): its id, feet and whether it is alive.</summary>
public readonly record struct MusouTarget(int Id, Vector3 Position, bool Alive);

/// <summary>
/// An enemy's attack that landed this tick (<see cref="MusouSim.Step"/>), for the World to apply: the enemy, the player, the damage (before the player's own
/// received-damage multipliers), melee or a ranged round, whether it staggers, and where it came from (the enemy's feet).
/// </summary>
public readonly record struct MusouAttack(ushort Enemy, int Player, float Damage, bool Melee, bool Staggers, Vector3 From);

/// <summary>A KO (<see cref="MusouSim.TakeKills"/>): the enemy, its type, the player credited (0: none) and how it died.</summary>
public readonly record struct MusouKill(ushort Enemy, MusouEnemyType Type, int Player, MusouDeathKind Cause);

/// <summary>
/// The enemies' moves and attacks (MU1; design section B; rules ours unless noted). Each tick (<see cref="Step"/>): the flow field advances; the bodies are
/// hashed for the separation; each enemy keeps a target (the nearest living player, sticky), asks for one of its target's attack tokens (melee: within
/// <see cref="MusouRules.MeleeTokenDistance"/>; shooters: within reach of their band) and then
/// <list type="bullet">
/// <item>far away follows the flow field (the gradient to the next node), near (<see cref="MusouRules.DirectSeekDistance"/>, about one floor, and not
/// blocked lately) seeks its target directly;</item>
/// <item>a melee enemy with a token closes in and in reach winds up (<see cref="MusouEnemyState.WindUp"/>, a <see cref="MusouEventKind.Telegraph"/>), strikes
/// when the wind-up ends (reach and cone checked then: a <see cref="MusouEventKind.Strike"/> and a <see cref="MusouAttack"/>) and recovers; one without a token
/// circles its target on its own ring (<see cref="MusouRules.CircleMin"/> to <see cref="MusouRules.CircleMax"/>);</item>
/// <item>a shooter holds its band (backs off when closer, closes in when farther or without a line of sight); with a token, a fresh line of sight and its
/// cooldown over it fires a burst, each round a hit roll by distance (a <see cref="MusouEventKind.Tracer"/>; no projectile). Lines of sight are raycast at most
/// <see cref="MusouRules.LosChecksPerTick"/> a tick, round-robin.</item>
/// </list>
/// Moves stay on the graph: a step into a new cell needs a walk or drop link to it, else the enemy slides along one axis or stops. Deterministic: slots in id
/// order, players in the order given, the hit roll a hash of the tick and the enemy.
/// </summary>
public sealed partial class MusouSim
{
    /// <summary><see cref="MusouEnemy.AttackToken"/>: a melee token on its target.</summary>
    public const byte MeleeToken = 1;
    /// <summary><see cref="MusouEnemy.AttackToken"/>: a shooter's token on its target.</summary>
    public const byte ShooterToken = 2;
    /// <summary><see cref="MusouEvent.Value"/> of a <see cref="MusouEventKind.Telegraph"/> for an enemy's melee wind-up (the boss's abilities are 1 and up).</summary>
    public const byte MeleeWindUpTelegraph = 0;

    readonly ushort[] _cooldown = new ushort[Capacity];    // ticks until the next attack may start
    readonly byte[] _burst = new byte[Capacity];            // rounds left in the burst
    readonly ushort[] _roundTicks = new ushort[Capacity];   // ticks to the burst's next round
    readonly uint[] _losTick = new uint[Capacity];          // when the line of sight was last raycast (0: never)
    readonly bool[] _los = new bool[Capacity];
    readonly float[] _ring = new float[Capacity];           // the circling radius
    readonly byte[] _blocked = new byte[Capacity];          // ticks in a row a move found no way
    readonly ushort[] _flowTicks = new ushort[Capacity];    // ticks left following the flow even near the target
    readonly int[] _transit = new int[Capacity];            // the node a drop link is taking it to (-1: none)
    readonly int[] _aim = new int[Capacity];                // the flow's next node this tick (-1: none; a move into its cell takes it)
    readonly Vector3[] _anchor = new Vector3[Capacity];     // where it stood when it last made headway
    readonly float[] _best = new float[Capacity];           // its lowest distance to a player since then (flow, else straight)
    readonly uint[] _headwayTick = new uint[Capacity];      // when it last made headway
    readonly byte[] _relocations = new byte[Capacity];      // times the failsafe moved it to a gate
    readonly List<ushort> _stranded = new();
    readonly Dictionary<int, int> _lastNode = new();        // per player id: the last graph node it stood on or above
    readonly SpatialGrid _grid;
    readonly Vector3[] _gridPos = new Vector3[Capacity];
    readonly int[] _gridSlot = new int[Capacity];
    readonly Vector3[] _startPos = new Vector3[Capacity];   // where each stood at the tick's start: separation reads these, so the order of moves does not bias it
    readonly List<int> _dead = new();
    int[] _targetNode = new int[16], _sources = new int[16], _owners = new int[16], _meleeHeld = new int[16], _shotHeld = new int[16], _meleeWant = new int[16], _shotWant = new int[16];
    float[] _meleeWantD = new float[16], _shotWantD = new float[16];
    int _losCursor;

    /// <summary>Simulation ticks a second (the World's).</summary>
    public int TickRate { get; init; } = 30;
    /// <summary>Wall-clock milliseconds the World's Musou part of the last tick took (the director, the players' shots, <see cref="Step"/>, the hits; for the cost report).</summary>
    public double LastStepMilliseconds { get; internal set; }
    /// <summary>Test aid (ours): the enemies do nothing at all in <see cref="Step"/> (no move, attack, token, failsafe or freeing), for tests and test aids that
    /// place and move enemies themselves (the server's <c>SM_MUSOU_DUMMIES</c>).</summary>
    public bool Frozen { get; set; }
    /// <summary>Line-of-sight raycasts of the last <see cref="Step"/>.</summary>
    public int LastLosChecks { get; private set; }

    float Dt => 1f / Math.Max(1, TickRate);
    ushort Ticks(float seconds) => (ushort)Math.Clamp((int)MathF.Round(seconds * TickRate), 1, ushort.MaxValue);

    void SpawnedAi(int i)
    {
        _cooldown[i] = 0; _burst[i] = 0; _roundTicks[i] = 0; _losTick[i] = 0; _los[i] = false;
        _blocked[i] = 0; _flowTicks[i] = 0; _transit[i] = -1; _aim[i] = -1;
        SpawnedCombat(i);   // MU3 (MusouCombat.cs): no script, no drive
        _relocations[i] = 0;
        _ring[i] = Rules.CircleMin + (Rules.CircleMax - Rules.CircleMin) * Unit(Mix((uint)i * 2654435761u, 0x9E3779B9u));
        if (Walk is not null && _node[i] < 0) _node[i] = Walk.Nearest(_position[i]);
        if (Walk is not null && _node[i] >= 0) _position[i] = _position[i] with { Z = Walk.Z(_node[i]) };
        _anchor[i] = _position[i]; _best[i] = float.PositiveInfinity; _headwayTick[i] = Tick;
    }

    /// <summary>
    /// One tick of every enemy (after <see cref="BeginTick"/>): moves, tokens, wind-ups, strikes and bursts; the attacks that landed go into
    /// <paramref name="attacks"/> (applied by the World). <paramref name="fight"/> false (not in play): nobody starts or lands an attack, moves go on.
    /// <paramref name="collision"/>: the shooters' line of sight (null: always clear). Dying enemies are freed after <see cref="MusouRules.DyingSeconds"/>.
    /// </summary>
    public void Step(ReadOnlySpan<MusouTarget> targets, CollisionWorld? collision, bool fight, List<MusouAttack> attacks)
    {
        if (Frozen) { LastLosChecks = 0; return; }
        int nt = targets.Length;
        if (_targetNode.Length < nt) Grow(nt);
        int ns = 0;
        for (int t = 0; t < nt; t++)
        {
            _targetNode[t] = targets[t].Alive ? SourceNode(targets[t]) : -1;
            if (_targetNode[t] >= 0) { _sources[ns] = _targetNode[t]; _owners[ns] = targets[t].Id; ns++; }
            _meleeHeld[t] = 0; _shotHeld[t] = 0; _meleeWant[t] = -1; _shotWant[t] = -1;
            _meleeWantD[t] = float.MaxValue; _shotWantD[t] = float.MaxValue;
        }
        Flow?.Step(_sources.AsSpan(0, ns), _owners.AsSpan(0, ns), Rules.FlowExpansionsPerTick, Ticks(Rules.FlowRefreshSeconds));
        if (_frenzyTicks > 0) _frenzyTicks--;   // MU3: the Waaagh!'s speed

        int gn = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || _state[i] == MusouEnemyState.Dying) continue;
            _gridPos[gn] = _position[i]; _gridSlot[gn] = i; gn++;
            _startPos[i] = _position[i];
            if (_token[i] == 0) continue;
            int t = IndexOf(targets, _target[i]);
            if (t < 0) { _token[i] = 0; continue; }
            if (_token[i] == MeleeToken) _meleeHeld[t]++; else _shotHeld[t]++;
        }
        _grid.Build(_gridPos, _gridSlot, gn);

        _dead.Clear();
        Span<int> near = stackalloc int[64];
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i]) continue;
            if (_stateTicks[i] < ushort.MaxValue) _stateTicks[i]++;
            var state = _state[i];
            if (state == MusouEnemyState.Dying)
            {
                if (_stateTicks[i] >= Ticks(Rules.DyingSeconds)) _dead.Add(i);
                continue;
            }
            if (state == MusouEnemyState.Spawning)
            {
                if (_stateTicks[i] >= Ticks(Rules.SpawnSeconds)) SetState(i, MusouEnemyState.Seek);
                continue;
            }
            if (_cooldown[i] > 0) _cooldown[i]--;
            var spec = Rules.Of(_type[i]);
            int t = Target(i, targets);
            Vector2 desired = Vector2.Zero;
            if (_scripted[i]) { }   // MU3: the boss's ability drives it (MusouBoss)
            else if (t < 0)
            {
                _token[i] = 0; _burst[i] = 0;
                if (state is not (MusouEnemyState.Seek or MusouEnemyState.Stagger)) SetState(i, MusouEnemyState.Seek);
            }
            else
            {
                _aim[i] = -1;
                desired = Behave(i, t, targets, spec, fight, collision, attacks) * SpeedScale(i);
            }
            Move(i, spec, desired, targets, near);
            Headway(i, spec, targets);
        }
        foreach (int i in _dead) Remove((ushort)(i + 1));

        // Tokens: each player's free slots go to the nearest enemy that asked this tick (one of each kind a tick).
        for (int t = 0; t < nt; t++)
        {
            if (_meleeWant[t] >= 0 && _meleeHeld[t] < Rules.MeleeTokens) _token[_meleeWant[t]] = MeleeToken;
            if (_shotWant[t] >= 0 && _shotHeld[t] < Rules.ShooterTokens) { _token[_shotWant[t]] = ShooterToken; _losTick[_shotWant[t]] = 0; }
        }
        CheckLinesOfSight(targets, collision);
    }

    /// <summary>
    /// The graph node a player counts from in the flow field: the one under its feet, else (in a jump-pack flight, on a ledge the graph leaves out) the
    /// nearest within 6 m around and 12 m below it, else the last one it had; -1 when it never had one.
    /// </summary>
    int SourceNode(MusouTarget target)
    {
        if (Walk is null) return -1;
        int node = Walk.Nearest(target.Position);
        if (node < 0) node = Walk.Graph.Nearest(target.Position - new Vector3(0f, 0f, 5f), 6f, 7f);
        if (node >= 0) _lastNode[target.Id] = node;
        else if (_lastNode.TryGetValue(target.Id, out int last)) node = last;
        return node;
    }

    /// <summary>
    /// The stranded-enemy failsafe's bookkeeping (ours): headway is a step of 1 m nearer a player (by the flow field, else straight), 2 m away from where
    /// it last made some, being near a player (12 m; a shooter within its band's far edge plus 3 m), attacking, or there being no player to go for. An
    /// enemy in play without headway for <see cref="MusouRules.StrandedSeconds"/> is listed for the director (<see cref="TakeStranded"/>).
    /// </summary>
    void Headway(int i, MusouTypeSpec spec, ReadOnlySpan<MusouTarget> targets)
    {
        var p = _position[i];
        float straight = float.PositiveInfinity;
        foreach (var t in targets) if (t.Alive) straight = MathF.Min(straight, Vector3.Distance(t.Position, p));
        float arrived = spec.Ranged ? MathF.Max(12f, spec.RangedMax + 3f) : 12f;
        float metric = Flow is { Ready: true } && _node[i] >= 0 && !float.IsPositiveInfinity(Flow.Distance(_node[i])) ? Flow.Distance(_node[i]) : straight;
        bool near = float.IsPositiveInfinity(straight) || straight <= arrived;
        if (near || _state[i] != MusouEnemyState.Seek || metric < _best[i] - 1f || Vector3.DistanceSquared(p, _anchor[i]) > 4f)
        {
            _headwayTick[i] = Tick; _anchor[i] = p;
            _best[i] = near ? metric : MathF.Min(_best[i], metric);
            return;
        }
        if (Tick - _headwayTick[i] < Ticks(Rules.StrandedSeconds)) return;
        _stranded.Add((ushort)(i + 1));
        _headwayTick[i] = Tick;   // listed once; again only after another spell without headway
    }

    /// <summary>The enemies the failsafe listed since the last call (in the order they were), moved into <paramref name="into"/>; the number moved.</summary>
    public int TakeStranded(List<ushort> into)
    {
        into.AddRange(_stranded);
        int n = _stranded.Count;
        _stranded.Clear();
        return n;
    }

    /// <summary>Times the failsafe has moved this enemy (0 for an unknown one).</summary>
    public int Relocations(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] ? _relocations[id - 1] : 0;

    /// <summary>
    /// The failsafe's move (the director's, to a gate): the enemy alive and in play stands at <paramref name="position"/> on <paramref name="node"/> (-1:
    /// found from the position), seeking afresh, its token given back; false for an unknown, dying or dead one, or a position that is not finite.
    /// </summary>
    public bool Relocate(ushort id, Vector3 position, int node = -1)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _state[i] == MusouEnemyState.Dying || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return false;
        _position[i] = position; _node[i] = node; _velocity[i] = Vector2.Zero;
        _token[i] = 0; _burst[i] = 0; _transit[i] = -1; _aim[i] = -1; _blocked[i] = 0; _flowTicks[i] = 0;
        if (Walk is not null && _node[i] < 0) _node[i] = Walk.Nearest(position);
        if (Walk is not null && _node[i] >= 0) _position[i] = position with { Z = Walk.Z(_node[i]) };
        SetState(i, MusouEnemyState.Seek);
        _anchor[i] = _position[i]; _best[i] = float.PositiveInfinity; _headwayTick[i] = Tick;
        if (_relocations[i] < 255) _relocations[i]++;
        return true;
    }

    /// <summary>Forgets the AI's own state between rounds (the flow field, the players' last nodes, the round-robin, the failsafe's list).</summary>
    void ResetAi()
    {
        Flow?.Reset();
        _lastNode.Clear();
        _stranded.Clear();
        _losCursor = 0;
    }

    void Grow(int n)
    {
        int size = Math.Max(n, _targetNode.Length * 2);
        _targetNode = new int[size]; _sources = new int[size]; _owners = new int[size]; _meleeHeld = new int[size]; _shotHeld = new int[size];
        _meleeWant = new int[size]; _shotWant = new int[size]; _meleeWantD = new float[size]; _shotWantD = new float[size];
    }

    static int IndexOf(ReadOnlySpan<MusouTarget> targets, int id)
    {
        if (id == 0) return -1;
        for (int t = 0; t < targets.Length; t++) if (targets[t].Id == id) return targets[t].Alive ? t : -1;
        return -1;
    }

    void SetState(int i, MusouEnemyState state)
    {
        _state[i] = state;
        _stateTicks[i] = 0;
    }

    /// <summary>
    /// The target's index: the current one while it lives (a token holder keeps it), re-chosen every half second (staggered). On the graph the choice is the
    /// player the flow field says is nearest by walking, unless one is within direct reach (<see cref="MusouRules.DirectSeekDistance"/> with height x 3);
    /// an enemy no player can be walked to from has no target (it holds, and the failsafe moves it). Off the graph: the nearest straight (height x 3),
    /// switched to only when much nearer.
    /// </summary>
    int Target(int i, ReadOnlySpan<MusouTarget> targets)
    {
        int current = IndexOf(targets, _target[i]);
        bool look = current < 0 || (_token[i] == 0 && (Tick + (uint)i) % 15u == 0u);
        if (!look) return current;
        int best = -1; float bestScore = float.MaxValue, currentScore = float.MaxValue;
        for (int t = 0; t < targets.Length; t++)
        {
            if (!targets[t].Alive) continue;
            var d = targets[t].Position - _position[i];
            float score = d.X * d.X + d.Y * d.Y + 9f * d.Z * d.Z;
            if (t == current) currentScore = score;
            if (score < bestScore) { bestScore = score; best = t; }
        }
        if (Flow is { Ready: true } && _node[i] >= 0 && !(bestScore <= Rules.DirectSeekDistance * Rules.DirectSeekDistance))
        {
            bool reachable = !float.IsPositiveInfinity(Flow.Distance(_node[i]));
            int owner = reachable ? IndexOf(targets, Flow.Owner(_node[i])) : -1;
            if (reachable && owner < 0) owner = best;   // the field's player died or left since the refresh: the nearest living one until the next
            if (owner != current) { _token[i] = 0; _burst[i] = 0; _losTick[i] = 0; }
            _target[i] = owner >= 0 ? (ushort)Math.Clamp(targets[owner].Id, 0, ushort.MaxValue) : (ushort)0;
            return owner;
        }
        if (current >= 0 && !(bestScore < 0.6f * currentScore)) return current;
        if (best != current) { _token[i] = 0; _burst[i] = 0; _losTick[i] = 0; }
        _target[i] = best >= 0 ? (ushort)Math.Clamp(targets[best].Id, 0, ushort.MaxValue) : (ushort)0;
        return best;
    }

    /// <summary>One enemy's decision against its target: the attack state machine, the token requests, and the way it wants to move.</summary>
    Vector2 Behave(int i, int t, ReadOnlySpan<MusouTarget> targets, MusouTypeSpec spec, bool fight, CollisionWorld? collision, List<MusouAttack> attacks)
    {
        var target = targets[t];
        var d = target.Position - _position[i];
        var flat = new Vector2(d.X, d.Y);
        float distance = flat.Length();
        var toward = distance > 1e-4f ? flat / distance : Forward(_yaw[i]);
        switch (_state[i])
        {
            case MusouEnemyState.WindUp:
                Face(i, toward);
                if (_stateTicks[i] >= Ticks(spec.MeleeWindUp))
                {
                    SetState(i, MusouEnemyState.Strike);
                    // The strike lands only on a target still in reach and in front (a step back or aside dodges it).
                    bool inCone = distance <= 1e-4f || Vector2.Dot(toward, Forward(_yaw[i])) >= MathF.Cos(spec.MeleeConeDegrees * MathF.PI / 180f);
                    if (fight && distance <= spec.MeleeRange + 0.3f && MathF.Abs(d.Z) <= 1.5f && inCone)
                    {
                        attacks.Add(new MusouAttack((ushort)(i + 1), target.Id, spec.MeleeDamage, true, spec.MeleeStaggers, _position[i]));
                        AddEvent(new MusouEvent(MusouEventKind.Strike, Tick, (ushort)(i + 1), (ushort)Math.Clamp(target.Id, 0, ushort.MaxValue), 0, target.Position));
                    }
                }
                return Vector2.Zero;
            case MusouEnemyState.Strike:
                SetState(i, MusouEnemyState.Recover);
                return Vector2.Zero;
            case MusouEnemyState.Recover:
                if (_stateTicks[i] >= Math.Max(1, Ticks(spec.MeleePeriod) - Ticks(spec.MeleeWindUp) - 1)) SetState(i, MusouEnemyState.Seek);
                return Vector2.Zero;
            case MusouEnemyState.Stagger:
                if (_stateTicks[i] >= Ticks(Rules.Combat.StaggerSeconds)) SetState(i, MusouEnemyState.Seek);   // MU3 staggers enemies (Stagger)
                return Vector2.Zero;
        }

        bool melee = spec.Melee && (!spec.Hybrid || distance <= spec.HybridMeleeDistance);
        bool ranged = spec.Ranged && !melee;
        if ((_token[i] == ShooterToken && !ranged) || (_token[i] == MeleeToken && !melee)) { _token[i] = 0; _burst[i] = 0; }
        bool level = MathF.Abs(d.Z) <= 1.5f;
        if (melee)
        {
            if (_token[i] == MeleeToken)
            {
                if (distance > Rules.MeleeTokenDistance * 2f) _token[i] = 0;
                else if (fight && level && distance <= spec.MeleeRange && _cooldown[i] == 0)
                {
                    SetState(i, MusouEnemyState.WindUp);
                    _cooldown[i] = Ticks(spec.MeleePeriod);
                    Face(i, toward);
                    AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), (ushort)Math.Clamp(target.Id, 0, ushort.MaxValue), MeleeWindUpTelegraph,
                        _position[i] + new Vector3(Forward(_yaw[i]) * spec.MeleeRange, 0f)));
                    return Vector2.Zero;
                }
                if (distance <= spec.MeleeRange * 0.8f && level) { Face(i, toward); return Vector2.Zero; }   // in reach: wait for the cooldown
                return Approach(i, t, d, spec.Speed);
            }
            if (distance <= Rules.MeleeTokenDistance && MathF.Abs(d.Z) <= 2.5f && distance < _meleeWantD[t]) { _meleeWantD[t] = distance; _meleeWant[t] = i; }
            return Circle(i, t, d, spec.Speed);
        }
        if (!ranged) return Approach(i, t, d, spec.Speed);

        bool fresh = _losTick[i] != 0 && Tick - _losTick[i] <= (uint)Rules.LosValidTicks;
        if (_token[i] == ShooterToken)
        {
            if (distance > spec.RangedMax * 1.5f) { _token[i] = 0; _burst[i] = 0; }
            else
            {
                if (_burst[i] > 0)
                {
                    Face(i, toward);
                    if (_roundTicks[i] > 0) _roundTicks[i]--;
                    else
                    {
                        Fire(i, target, distance, spec, attacks, fight);
                        _burst[i]--;
                        _roundTicks[i] = Ticks(spec.RangedRoundInterval);
                    }
                    return Vector2.Zero;
                }
                if (fight && fresh && _los[i] && distance >= spec.RangedMin * 0.8f && distance <= spec.RangedMax && _cooldown[i] == 0)
                {
                    _burst[i] = (byte)Math.Clamp(spec.RangedRounds, 1, 255);
                    _roundTicks[i] = 0;
                    _cooldown[i] = Ticks(spec.RangedPeriod);
                    Face(i, toward);
                    return Vector2.Zero;
                }
            }
        }
        else if (distance <= spec.RangedMax * 1.2f && distance < _shotWantD[t]) { _shotWantD[t] = distance; _shotWant[t] = i; }
        // The band: closer backs off, farther or blind closes in, else holds facing the target.
        if (distance > spec.RangedMax || (fresh && !_los[i])) return Approach(i, t, d, spec.Speed);
        if (distance < spec.RangedMin) return -toward * spec.Speed * 0.6f;
        Face(i, toward);
        return Vector2.Zero;
    }

    /// <summary>One round of a burst: the hit roll by distance (a hash of the tick and the enemy), the tracer, and the damage on a hit.</summary>
    void Fire(int i, MusouTarget target, float distance, MusouTypeSpec spec, List<MusouAttack> attacks, bool fight)
    {
        float span = MathF.Max(1e-3f, spec.RangedMax - spec.RangedMin);
        float chance = spec.RangedHitNear + (spec.RangedHitFar - spec.RangedHitNear) * Math.Clamp((distance - spec.RangedMin) / span, 0f, 1f);
        uint roll = Mix(Tick, (uint)i * 0x85EBCA6Bu + _burst[i]);
        bool hit = fight && Unit(roll) < chance;
        var chest = target.Position + new Vector3(0f, 0f, 1.2f);
        var end = chest;
        if (!hit)
        {
            // A miss passes beside the target: 0.6 to 1.4 m off, left or right, a little high or low.
            var from = _position[i];
            var side = Vector2.Normalize(new Vector2(-(chest.Y - from.Y), chest.X - from.X) + new Vector2(1e-4f, 0f));
            float off = (0.6f + 0.8f * Unit(Mix(roll, 1u))) * ((roll & 1u) == 0u ? 1f : -1f);
            end = chest + new Vector3(side * off, (Unit(Mix(roll, 2u)) - 0.5f) * 0.8f);
        }
        else attacks.Add(new MusouAttack((ushort)(i + 1), target.Id, spec.RangedDamage, false, false, _position[i]));
        AddEvent(new MusouEvent(MusouEventKind.Tracer, Tick, (ushort)(i + 1), hit ? (ushort)Math.Clamp(target.Id, 0, ushort.MaxValue) : (ushort)0, 0, end));
    }

    /// <summary>The way to the target at <paramref name="speed"/>: directly when near and not blocked lately, else down the flow field (directly without one).</summary>
    Vector2 Approach(int i, int t, Vector3 d, float speed)
    {
        var flat = new Vector2(d.X, d.Y);
        float distance = flat.Length();
        bool direct = Walk is null || Flow is null || (_flowTicks[i] == 0 && distance <= Rules.DirectSeekDistance && MathF.Abs(d.Z) < 1.5f);
        if (!direct && _node[i] >= 0)
        {
            int next = Flow!.Next(_node[i]);
            if (next >= 0 && (!Walk!.Adjacent(_node[i], next) || MathF.Abs(Walk.Z(next) - Walk.Z(_node[i])) > 1f)) { _transit[i] = next; return Vector2.Zero; }   // a drop: Move takes it over the edge
            _aim[i] = next;
            if (next >= 0)
            {
                var p = _position[i];
                var to = new Vector2(Walk!.X(next) - p.X, Walk.Y(next) - p.Y);
                if (to.LengthSquared() < 0.0025f && Flow.Next(next) is var after and >= 0) to = new Vector2(Walk.X(after) - p.X, Walk.Y(after) - p.Y);
                float l = to.Length();
                if (l > 1e-4f) return to / l * speed;
            }
            // No way down the field from here (no player can be walked to): hold rather than walk into a wall.
            if (Flow!.Ready && float.IsPositiveInfinity(Flow.Distance(_node[i]))) return Vector2.Zero;
        }
        return distance > 1e-4f ? flat / distance * speed : Vector2.Zero;
    }

    /// <summary>Circling the target on the enemy's own ring (a melee enemy without a token): closes in from afar, then walks around at under half speed.</summary>
    Vector2 Circle(int i, int t, Vector3 d, float speed)
    {
        var flat = new Vector2(d.X, d.Y);
        float distance = flat.Length();
        float ring = _ring[i];
        if (distance > ring + 2f || distance <= 1e-4f) return Approach(i, t, d, speed);
        var radial = flat / distance;
        var tangent = new Vector2(-radial.Y, radial.X) * ((i & 1) == 0 ? 1f : -1f);
        var way = tangent * 0.7f + radial * Math.Clamp(distance - ring, -1f, 1f);
        float l = way.Length();
        return l > 1e-4f ? way / l * speed * 0.45f : Vector2.Zero;
    }

    /// <summary>The separation (other enemies and the ring around players), then the move on the graph (or the flat ground), the facing and the velocity.</summary>
    void Move(int i, MusouTypeSpec spec, Vector2 desired, ReadOnlySpan<MusouTarget> targets, Span<int> near)
    {
        var p = _position[i];
        if (_transit[i] >= 0 && Walk is not null)
        {
            // Over a drop: straight to the landing node at run speed, falling with it, then on the graph again. A drive (MU3's knockback) waits: Driven
            // is not called, so its ticks are kept for after the landing.
            int to = _transit[i];
            var land = Walk.Position(to);
            var way = new Vector2(land.X - p.X, land.Y - p.Y);
            float left = way.Length(), stride = spec.Speed * Dt;
            if (left <= stride)
            {
                _position[i] = land; _node[i] = to; _transit[i] = -1;
                _velocity[i] = way / Dt;
                return;
            }
            var along = way / left * stride;
            float fall = (p.Z - land.Z) * stride / left;
            _position[i] = new Vector3(p.X + along.X, p.Y + along.Y, p.Z - fall);
            _velocity[i] = along / Dt;
            Face(i, way / left);
            return;
        }
        var push = Vector2.Zero;
        // The nearest overlapping neighbours, at most MaxNeighbours (nearest first; ties: the hash's order), so a dense pile pushes evenly.
        int found = _grid.Around(p, near), kept = 0, most = Math.Clamp(Rules.MaxNeighbours, 0, 16);
        Span<int> keptSlot = stackalloc int[16];
        Span<float> keptDist = stackalloc float[16];
        for (int k = 0; k < found; k++)
        {
            int j = near[k];
            if (j == i) continue;
            var o = _startPos[j];
            if (MathF.Abs(o.Z - p.Z) > 1.2f) continue;
            float dist = new Vector2(p.X - o.X, p.Y - o.Y).Length();
            if (dist >= Rules.Of(_type[j]).Radius + spec.Radius) continue;
            int at = kept;
            while (at > 0 && keptDist[at - 1] > dist) at--;
            if (at >= most) continue;
            for (int m = Math.Min(kept, most - 1); m > at; m--) { keptSlot[m] = keptSlot[m - 1]; keptDist[m] = keptDist[m - 1]; }
            keptSlot[at] = j; keptDist[at] = dist;
            if (kept < most) kept++;
        }
        for (int k = 0; k < kept; k++)
        {
            int j = keptSlot[k];
            var o = _startPos[j];
            var away = new Vector2(p.X - o.X, p.Y - o.Y);
            float gap = Rules.Of(_type[j]).Radius + spec.Radius, dist = keptDist[k];
            var dir = dist > 1e-4f ? away / dist : Forward((i - j) * 2.399963f);
            push += dir * (gap - dist) * Rules.SeparationStrength;
        }
        foreach (var target in targets)
        {
            if (!target.Alive || MathF.Abs(target.Position.Z - p.Z) > 1.5f) continue;
            var away = new Vector2(p.X - target.Position.X, p.Y - target.Position.Y);
            float gap = Rules.PlayerRing + spec.Radius, dist = away.Length();
            if (dist >= gap) continue;
            var dir = dist > 1e-4f ? away / dist : Forward(i * 2.399963f);
            push += dir * (gap - dist) * Rules.SeparationStrength * 2f;
        }
        var v = desired + push;
        float max = spec.Speed * 1.5f * SpeedScale(i), len = v.Length();
        if (len > max) v *= max / len;
        v += Driven(i);   // MU3: a knockback or the boss's charge, over the clamp
        if (desired.LengthSquared() > 0.01f && _state[i] == MusouEnemyState.Seek) Face(i, Vector2.Normalize(desired));
        if (v.LengthSquared() < 1e-6f) { _velocity[i] = Vector2.Zero; return; }

        var step = v * Dt;
        var moved = p;
        if (Walk is null) moved = new Vector3(p.X + step.X, p.Y + step.Y, p.Z);
        else
        {
            if (_node[i] < 0) _node[i] = Walk.Nearest(p);
            if (_node[i] < 0) moved = new Vector3(p.X + step.X, p.Y + step.Y, p.Z);   // off the graph: free until it finds it again
            else
            {
                int node = Walk.MoveTo(_node[i], p.X + step.X, p.Y + step.Y, _aim[i]);
                var at = new Vector2(p.X + step.X, p.Y + step.Y);
                if (node < 0) { node = Walk.MoveTo(_node[i], p.X + step.X, p.Y); at = new Vector2(p.X + step.X, p.Y); }
                if (node < 0) { node = Walk.MoveTo(_node[i], p.X, p.Y + step.Y); at = new Vector2(p.X, p.Y + step.Y); }
                if (node < 0 && push != Vector2.Zero)
                {
                    // The crowd pushed it into a wall: the way it wanted alone.
                    var own = desired * Dt;
                    node = Walk.MoveTo(_node[i], p.X + own.X, p.Y + own.Y, _aim[i]); at = new Vector2(p.X + own.X, p.Y + own.Y);
                }
                if (node < 0)
                {
                    if (_blocked[i] < 255) _blocked[i]++;
                    if (_blocked[i] >= 3) _flowTicks[i] = Ticks(1.5f);   // blocked near the target: follow the flow around the wall for a while
                    _velocity[i] = Vector2.Zero;
                    return;
                }
                _blocked[i] = 0;
                _node[i] = node;
                moved = new Vector3(at.X, at.Y, Walk.Z(node));
            }
        }
        if (_flowTicks[i] > 0) _flowTicks[i]--;
        _velocity[i] = new Vector2(moved.X - p.X, moved.Y - p.Y) / Dt;
        _position[i] = moved;
    }

    void Face(int i, Vector2 direction)
    {
        if (direction.LengthSquared() > 1e-8f) _yaw[i] = MathF.Atan2(-direction.X, direction.Y);
    }

    /// <summary>The ground-plane facing of a yaw (the players' convention: yaw 0 looks along +y).</summary>
    static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw));

    /// <summary>The shooters' lines of sight: those holding a token whose last raycast is stale, at most the budget a tick, round-robin from the last one checked.</summary>
    void CheckLinesOfSight(ReadOnlySpan<MusouTarget> targets, CollisionWorld? collision)
    {
        int checks = 0;
        for (int k = 0; k < Capacity && checks < Rules.LosChecksPerTick; k++)
        {
            int i = (_losCursor + k) % Capacity;
            if (!_used[i] || _token[i] != ShooterToken || _state[i] == MusouEnemyState.Dying) continue;
            if (_losTick[i] != 0 && Tick - _losTick[i] <= (uint)Rules.LosValidTicks) continue;
            int t = IndexOf(targets, _target[i]);
            if (t < 0) continue;
            checks++;
            _losTick[i] = Math.Max(1u, Tick);
            var eye = _position[i] + new Vector3(0f, 0f, 1.5f);
            var chest = targets[t].Position + new Vector3(0f, 0f, 1.2f);
            var ray = chest - eye;
            float length = ray.Length();
            _los[i] = collision is null || length < 1e-3f || !(collision.Weapons.Raycast(eye, ray / length, length) is { } wall && wall.Distance < length - 0.1f);
            if (checks == Rules.LosChecksPerTick) _losCursor = (i + 1) % Capacity;
        }
        LastLosChecks = checks;
    }

    // ---------------------------------------------------------------- the players' shots against the enemies

    /// <summary>
    /// The nearest enemy alive and in play a ray from <paramref name="origin"/> along <paramref name="direction"/> (unit) meets within
    /// <paramref name="maxDistance"/>: its id (0: none), the distance and whether it struck the head (a sphere at the top of the body; the body a vertical
    /// cylinder below it, the type's radius). Ties: the lowest id.
    /// </summary>
    public ushort Raycast(Vector3 origin, Vector3 direction, float maxDistance, out float distance, out bool head)
    {
        ushort best = 0; distance = maxDistance; head = false;
        var dxy = new Vector2(direction.X, direction.Y);
        float a = Vector2.Dot(dxy, dxy);
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || _state[i] == MusouEnemyState.Dying) continue;
            var spec = Rules.Of(_type[i]);
            float scale = _type[i] == MusouEnemyType.Warboss ? Rules.BossScale : 1f;
            float r = spec.Radius * scale, height = spec.Height * scale, headRadius = 0.22f * height / 1.9f;
            var c = _position[i];
            var m = new Vector2(origin.X - c.X, origin.Y - c.Y);
            // Quick reject: the ray's line passes farther than the body's radius from the axis.
            float reach = r + headRadius;
            if (a > 1e-9f)
            {
                float along = -Vector2.Dot(m, dxy) / a;
                if (along < -reach || along > maxDistance + reach) continue;
                var closest = m + dxy * along;
                if (closest.LengthSquared() > reach * reach) continue;
            }
            else if (m.LengthSquared() > reach * reach) continue;
            // Head: a sphere at the top.
            var headCentre = c + new Vector3(0f, 0f, height - headRadius);
            if (RaySphere(origin, direction, headCentre, headRadius) is { } th && th < distance) { distance = th; best = (ushort)(i + 1); head = true; }
            // Body: the vertical cylinder from the feet to under the head.
            if (a > 1e-9f)
            {
                float b = Vector2.Dot(m, dxy), cc = Vector2.Dot(m, m) - r * r, disc = b * b - a * cc;
                if (disc >= 0f)
                {
                    float tb = (-b - MathF.Sqrt(disc)) / a;
                    if (tb < 0f && cc <= 0f) tb = 0f;   // starts inside
                    float z = origin.Z + direction.Z * tb;
                    if (tb >= 0f && tb < distance && z >= c.Z && z <= c.Z + height - 2f * headRadius) { distance = tb; best = (ushort)(i + 1); head = false; }
                }
            }
        }
        if (best == 0) distance = 0f;
        return best;
    }

    static float? RaySphere(Vector3 o, Vector3 d, Vector3 centre, float r)
    {
        var m = o - centre;
        float b = Vector3.Dot(m, d), c = Vector3.Dot(m, m) - r * r;
        if (c > 0f && b > 0f) return null;
        float disc = b * b - c;
        if (disc < 0f) return null;
        return MathF.Max(0f, -b - MathF.Sqrt(disc));
    }

    /// <summary>The squared distance from <paramref name="p"/> to the nearest enemy alive and in play (infinite: none).</summary>
    public float NearestSquared(Vector3 p)
    {
        float best = float.PositiveInfinity;
        for (int i = 0; i < Capacity; i++)
            if (_used[i] && _state[i] != MusouEnemyState.Dying) best = MathF.Min(best, Vector3.DistanceSquared(_position[i], p));
        return best;
    }

    // ---------------------------------------------------------------- deterministic hashing

    static uint Mix(uint a, uint b)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u);
        h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
        return h;
    }

    static float Unit(uint h) => (h >> 8) * (1f / 16777216f);
}
