using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>What a Guardsman is doing (<see cref="TfwGuard.State"/>).</summary>
public enum GuardState : byte { Free = 0, Idle = 1, Moving = 2, Dying = 3, Knocked = 4 }

/// <summary>What a Guard event reports: a shot (<see cref="GuardEvent.Vector"/> where the tracer ends), a man hurt, a man dead (the vector his feet).</summary>
public enum GuardEventKind : byte { Shot = 1, Hurt = 2, Death = 3 }

/// <summary>One Guard event of a tick (for tag 10). <see cref="Enemy"/>: the enemy a shot hit (0: a miss).</summary>
public readonly record struct GuardEvent(GuardEventKind Kind, uint Tick, ushort Guard, ushort Enemy, Vector3 Vector);

/// <summary>
/// The Guardsmen of a war (design section E): a struct-of-arrays pool of <see cref="Capacity"/> men in <see cref="MaxSquads"/> squads of <see cref="SquadSize"/>; squad s owns
/// slots s x 10 to s x 10 + 9, so a squad's men are found without a search (the design's 256 slots with its 24 squads need 240). A squad walks to its order in a 2 x 5
/// formation, each man steering to his place with separation over a <see cref="SpatialGrid"/>, on the graph's walkable cells; it fires while it walks. On a graph (M2) a squad and its men go by <see cref="Ways"/>, round the walls, and stop to fight an enemy within <see cref="GuardRules.HaltDistance"/>; a shot needs a line of sight (<see cref="Sight"/>). Enemies target a squad at its
/// men's centre (<see cref="AppendTargets"/>) and a blow lands on the nearest man (<see cref="Hit"/>). Deterministic: slots in order, every roll a hash of the tick and the man.
/// </summary>
public sealed class GuardSim
{
    public const int SquadSize = 10, MaxSquads = 24, Capacity = SquadSize * MaxSquads, MaxEventsPerTick = 512;
    const int Columns = SquadSize / 2;

    readonly MusouSim _enemies;
    readonly GuardState[] _state = new GuardState[Capacity];
    readonly Vector3[] _position = new Vector3[Capacity];
    readonly Vector3[] _startPos = new Vector3[Capacity];   // where each stood at the tick's start: separation reads these
    readonly float[] _yaw = new float[Capacity];
    readonly float[] _health = new float[Capacity];
    readonly int[] _node = new int[Capacity];
    readonly ushort[] _shotTicks = new ushort[Capacity];
    readonly ushort[] _stateTicks = new ushort[Capacity];
    readonly Vector2[] _throw = new Vector2[Capacity];       // Knocked: the velocity of the throw under way
    readonly ushort[] _throwTicks = new ushort[Capacity];
    readonly ushort[] _downTicks = new ushort[Capacity];     // Knocked: ticks he stays down in all

    readonly bool[] _active = new bool[MaxSquads];           // any man in the squad's slots, dying included
    readonly int[] _living = new int[MaxSquads];
    readonly Vector3[] _anchor = new Vector3[MaxSquads];     // the formation's centre
    readonly int[] _anchorNode = new int[MaxSquads];
    readonly float[] _squadYaw = new float[MaxSquads];
    readonly Vector3[] _order = new Vector3[MaxSquads];
    readonly bool[] _moving = new bool[MaxSquads];
    readonly float[] _orderBest = new float[MaxSquads];      // the nearest it has come to its order
    readonly ushort[] _stallTicks = new ushort[MaxSquads];   // ticks since it last came nearer
    readonly int[] _goal = new int[MaxSquads];               // the graph node its way leads to (-1: straight)
    readonly bool[] _halted = new bool[MaxSquads];           // an enemy is on it: it stands and fights
    readonly float[] _reach = new float[MaxSquads];          // how far its men stand from their centre, on average
    readonly Vector3[] _centre = new Vector3[MaxSquads];     // the living men's centre
    readonly ushort[] _seen = new ushort[MaxSquads * GuardRules.SeenPerSquad];
    readonly int[] _seenCount = new int[MaxSquads];

    readonly SpatialGrid _grid;
    readonly Vector3[] _gridPos = new Vector3[Capacity];
    readonly int[] _gridSlot = new int[Capacity];
    readonly List<GuardEvent> _events = new();

    public GuardSim(MusouSim enemies, GuardRules? rules = null)
    {
        _enemies = enemies;
        Rules = rules ?? GuardRules.Default;
        _grid = new SpatialGrid(enemies.Box, Rules.SeparationCell);
        Ways = enemies.Walk is { } walk ? new AllyFlow(walk) : null;
    }

    /// <summary>The allies' ways over the graph (null without one: a squad walks straight). The AI Marines use them too.</summary>
    public AllyFlow? Ways { get; }
    /// <summary>What a shot's line of sight is checked against (null: always clear).</summary>
    public CollisionWorld? Sight { get; init; }
    /// <summary>Line-of-sight raycasts of the last <see cref="Step"/>.</summary>
    public int LastRays { get; private set; }

    public GuardRules Rules { get; }
    /// <summary>The world tick the events are of.</summary>
    public uint Tick { get; private set; }
    /// <summary>Men alive.</summary>
    public int Alive { get; private set; }
    /// <summary>Slots in use (alive or dying).</summary>
    public int Count { get; private set; }
    /// <summary>Men lost since the start or the last <see cref="Clear"/>.</summary>
    public int Lost { get; private set; }
    /// <summary>Shots fired and shots that hit since the start or the last <see cref="Clear"/>.</summary>
    public int Shots { get; private set; }
    public int ShotsHit { get; private set; }
    /// <summary>This tick's events.</summary>
    public IReadOnlyList<GuardEvent> Events => _events;
    /// <summary>Wall-clock milliseconds of the last <see cref="Step"/> (for the cost report).</summary>
    public double LastStepMilliseconds { get; private set; }

    float Dt => 1f / Math.Max(1, _enemies.TickRate);
    ushort Ticks(float seconds) => (ushort)Math.Clamp((int)MathF.Round(seconds * Math.Max(1, _enemies.TickRate)), 1, ushort.MaxValue);
    static Vector2 Forward(float yaw) => new(-MathF.Sin(yaw), MathF.Cos(yaw));
    static float YawOf(Vector2 direction) => MathF.Atan2(-direction.X, direction.Y);
    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    /// <summary>A new tick: clears the last tick's events (before the enemies' attacks land).</summary>
    public void BeginTick(uint tick)
    {
        Tick = tick;
        _events.Clear();
        Ways?.Step();
    }

    /// <summary>A full squad at <paramref name="centre"/>, facing <paramref name="yaw"/>, in the first free squad; its index, or -1 with none free or a position that is not finite.</summary>
    public int SpawnSquad(Vector3 centre, float yaw = 0f)
    {
        if (!Finite(centre) || !float.IsFinite(yaw)) return -1;
        int s = Array.IndexOf(_active, false);
        if (s < 0) return -1;
        var walk = _enemies.Walk;
        int anchorNode = walk?.Nearest(centre) ?? -1;
        if (anchorNode >= 0) centre = centre with { Z = walk!.Z(anchorNode) };
        _active[s] = true; _living[s] = SquadSize;
        _anchor[s] = centre; _anchorNode[s] = anchorNode; _squadYaw[s] = yaw; _order[s] = centre; _moving[s] = false;
        _seenCount[s] = 0; _goal[s] = -1; _halted[s] = false;
        for (int m = 0; m < SquadSize; m++)
        {
            int i = s * SquadSize + m;
            var place = Place(s, m);
            int node = walk?.Nearest(place) ?? -1;
            _state[i] = GuardState.Idle; _stateTicks[i] = 0;
            _position[i] = node >= 0 ? place with { Z = walk!.Z(node) } : walk is null ? place : centre;   // a place in a wall: he starts at the squad's centre
            _node[i] = node >= 0 ? node : anchorNode; _yaw[i] = yaw; _health[i] = Rules.Health;
            _shotTicks[i] = NextShot(i, 0u);
        }
        _centre[s] = centre;
        _reach[s] = 0f;
        Alive += SquadSize; Count += SquadSize;
        return s;
    }

    /// <summary>
    /// Sends a squad to <paramref name="destination"/>: by the way to <paramref name="via"/> (no place named: to the destination), then straight the last metres, so squads bound for one point share a field;
    /// straight all the way without a graph. False for a squad without living men or a position that is not finite.
    /// </summary>
    public bool Order(int squad, Vector3 destination, Vector3? via = null)
    {
        if (AliveIn(squad) == 0 || !Finite(destination)) return false;
        _order[squad] = destination;
        _goal[squad] = Ways?.GoalNode(via ?? destination) ?? -1;
        _moving[squad] = true;
        _orderBest[squad] = float.PositiveInfinity; _stallTicks[squad] = 0;
        var way = new Vector2(destination.X - _anchor[squad].X, destination.Y - _anchor[squad].Y);
        if (way.LengthSquared() > 1e-4f) _squadYaw[squad] = YawOf(way);
        return true;
    }

    /// <summary>Whether a squad is done with its order: it arrived, stands, or gave up on a place it cannot walk to (<see cref="GuardRules.StallSeconds"/>).</summary>
    public bool Arrived(int squad) => squad is >= 0 and < MaxSquads && !_moving[squad];

    /// <summary>Men alive in a squad (0 for an unknown one).</summary>
    public int AliveIn(int squad) => squad is >= 0 and < MaxSquads ? _living[squad] : 0;

    /// <summary>A squad's living men's centre as of the last step or spawn.</summary>
    public Vector3 Centre(int squad) => _centre[squad];

    /// <summary>Squads the enemies can target.</summary>
    public int SquadsAlive { get { int n = 0; foreach (int c in _living) n += c > 0 ? 1 : 0; return n; } }

    /// <summary>The living squads as the enemies see them (ids <see cref="TfwContracts.GuardTargetId"/>, positions the men's centres), written from <paramref name="into"/>'s start; the number written.</summary>
    public int AppendTargets(Span<MusouTarget> into)
    {
        int n = 0;
        for (int s = 0; s < MaxSquads && n < into.Length; s++)
            if (_living[s] > 0) into[n++] = new MusouTarget(TfwContracts.GuardTargetId(s), _centre[s], true, _reach[s]);
        return n;
    }

    /// <summary>The squad with living men whose centre is nearest <paramref name="p"/> (ties: the lowest index); -1 with none.</summary>
    public int NearestSquad(Vector3 p)
    {
        int best = -1; float bestD = float.PositiveInfinity;
        for (int s = 0; s < MaxSquads; s++)
        {
            if (_living[s] == 0) continue;
            float d = Vector3.DistanceSquared(_centre[s], p);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    /// <summary>Men alive within <paramref name="radius"/> of <paramref name="centre"/> (flat).</summary>
    public int CountInside(Vector3 centre, float radius)
    {
        int n = 0; float r2 = radius * radius;
        for (int i = 0; i < Capacity; i++)
        {
            if (!Living(i)) continue;
            float dx = _position[i].X - centre.X, dy = _position[i].Y - centre.Y;
            if (dx * dx + dy * dy <= r2) n++;
        }
        return n;
    }

    /// <summary>Every man in a slot (alive or dying), in slot order, for tag 9 (id = slot + 1); the number written.</summary>
    public int CopyTo(Span<TfwGuard> into)
    {
        int n = 0;
        for (int i = 0; i < Capacity && n < into.Length; i++)
            if (_state[i] != GuardState.Free) into[n++] = new TfwGuard((ushort)(i + 1), _position[i], _yaw[i], (byte)_state[i]);
        return n;
    }

    /// <summary>
    /// A blow on a squad (an Ork's <see cref="MusouAttack"/>): it lands on the living man nearest <paramref name="from"/> (ties: the lowest slot); false, and nothing changed, for an
    /// unknown or wiped squad or no damage.
    /// </summary>
    public bool Hit(int squad, float damage, Vector3 from)
    {
        if (AliveIn(squad) == 0 || !(damage > 0f)) return false;
        int best = -1; float bestD = float.PositiveInfinity;
        for (int i = squad * SquadSize; i < (squad + 1) * SquadSize; i++)
        {
            if (!Living(i)) continue;
            float d = Vector3.DistanceSquared(_position[i], from);
            if (d < bestD) { bestD = d; best = i; }
        }
        _health[best] = MathF.Max(0f, _health[best] - damage);
        if (_health[best] > 0f) { AddEvent(new GuardEvent(GuardEventKind.Hurt, Tick, (ushort)(best + 1), 0, _position[best])); return true; }
        Fall(best);
        return true;
    }

    void Fall(int i)
    {
        _state[i] = GuardState.Dying; _stateTicks[i] = 0;
        _living[i / SquadSize]--; Alive--; Lost++;
        AddEvent(new GuardEvent(GuardEventKind.Death, Tick, (ushort)(i + 1), 0, _position[i]));
    }

    /// <summary>
    /// A Marine's shove (Musou pass 2, item 7): the standing men within <paramref name="reach"/> of <paramref name="at"/> (flat), in front of <paramref name="heading"/> (zero: all round), at most
    /// <see cref="TfwCombatRules.ShoveTests"/> of them, are thrown <paramref name="distance"/> and fall (<see cref="GuardState.Knocked"/>); with <paramref name="deathChance"/> a man dies instead,
    /// counted in <see cref="Lost"/> with no credit, as any Guardsman lost. How many were shoved.
    /// </summary>
    public int Shove(Vector3 at, float reach, Vector2 heading, float distance, float deathChance, TfwCombatRules rules)
    {
        int shoved = 0;
        ushort ticks = Ticks(_enemies.Rules.Combat.KnockbackSeconds);
        for (int i = 0; i < Capacity && shoved < rules.ShoveTests; i++)
        {
            if (!Standing(i) || MathF.Abs(_position[i].Z - at.Z) > 1.5f) continue;
            var flat = new Vector2(_position[i].X - at.X, _position[i].Y - at.Y);
            float d = flat.Length();
            if (d > reach) continue;
            var away = d > 1e-4f ? flat / d : heading;
            if (heading != Vector2.Zero && Vector2.Dot(away, heading) < 0f) continue;
            shoved++;
            if (MusouSim.Unit(MusouSim.Mix(Tick, (uint)i * 0xC2B2AE35u + 0x165667B1u)) < deathChance) { Fall(i); continue; }
            var push = heading + away;
            float length = push.Length();
            _throw[i] = length > 1e-4f ? push / length * (distance / (ticks * Dt)) : Vector2.Zero;
            _throwTicks[i] = ticks;
            _downTicks[i] = Ticks(rules.KnockedSeconds);
            _state[i] = GuardState.Knocked; _stateTicks[i] = 0;
        }
        return shoved;
    }

    /// <summary>One tick on the ground: the throw under way, then he stands. He neither walks nor fires.</summary>
    void StepKnocked(int i)
    {
        if (_throwTicks[i] > 0) { _throwTicks[i]--; Walk(ref _position[i], ref _node[i], _throw[i] * Dt); }
        if (++_stateTicks[i] >= _downTicks[i]) { _state[i] = GuardState.Idle; _stateTicks[i] = 0; }
    }

    /// <summary>Frees every man and squad and the counters (a new battle).</summary>
    public void Clear()
    {
        Array.Clear(_state); Array.Clear(_active); Array.Clear(_living); Array.Clear(_seenCount); Array.Clear(_moving); Array.Clear(_halted);
        _events.Clear();
        Alive = 0; Count = 0; Lost = 0; Shots = 0; ShotsHit = 0;
    }

    /// <summary>One tick of every squad after the enemies' step: moves, and with <paramref name="fight"/> the shooting.</summary>
    public void Step(bool fight)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        LastRays = 0;
        int gn = 0;
        for (int i = 0; i < Capacity; i++)
        {
            _startPos[i] = _position[i];
            if (Living(i)) { _gridPos[gn] = _position[i]; _gridSlot[gn] = i; gn++; }
        }
        _grid.Build(_gridPos, _gridSlot, gn);
        Span<int> near = stackalloc int[64];
        for (int s = 0; s < MaxSquads; s++)
        {
            if (!_active[s]) continue;
            StepSquad(s, fight, near);
        }
        LastStepMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    bool Living(int i) => _state[i] is GuardState.Idle or GuardState.Moving or GuardState.Knocked;
    bool Standing(int i) => _state[i] is GuardState.Idle or GuardState.Moving;

    void StepSquad(int s, bool fight, Span<int> near)
    {
        MoveAnchor(s);
        if (!fight) _halted[s] = false;
        else if (_living[s] > 0 && (Tick + (uint)s) % (uint)Math.Max(1, Rules.QueryTicks) == 0u)
        {
            _seenCount[s] = _enemies.Query(_centre[s], Rules.Range, _seen.AsSpan(s * GuardRules.SeenPerSquad, GuardRules.SeenPerSquad));
            _halted[s] = Ways is not null && _seenCount[s] > 0 && _enemies.TryGet(_seen[s * GuardRules.SeenPerSquad], out var nearest)
                && new Vector2(nearest.Position.X - _centre[s].X, nearest.Position.Y - _centre[s].Y).LengthSquared() <= Rules.HaltDistance * Rules.HaltDistance;
        }
        var sum = Vector3.Zero;
        int living = 0, used = 0;
        for (int m = 0; m < SquadSize; m++)
        {
            int i = s * SquadSize + m;
            if (_state[i] == GuardState.Free) continue;
            if (_state[i] == GuardState.Dying)
            {
                if (++_stateTicks[i] >= Ticks(Rules.DyingSeconds)) { _state[i] = GuardState.Free; Count--; }
                else used++;
                continue;
            }
            used++;
            if (_state[i] == GuardState.Knocked) StepKnocked(i);
            else
            {
                MoveMan(i, s, m, near);
                if (fight) Shoot(i, s, m);
            }
            sum += _position[i]; living++;
        }
        if (living > 0)
        {
            var centre = sum / living;
            _centre[s] = centre;
            float spread = 0f;
            for (int i = s * SquadSize; i < (s + 1) * SquadSize; i++)
                if (Living(i)) spread += new Vector2(_position[i].X - centre.X, _position[i].Y - centre.Y).Length();
            _reach[s] = spread / living;
        }
        if (used == 0) { _active[s] = false; _moving[s] = false; _seenCount[s] = 0; _goal[s] = -1; _halted[s] = false; }
    }

    /// <summary>A man's place in the formation: five abreast, two rows, turned to the squad's facing.</summary>
    Vector3 Place(int s, int man)
    {
        int column = man % Columns, row = man / Columns;
        var forward = Forward(_squadYaw[s]);
        var right = new Vector2(forward.Y, -forward.X);
        var offset = right * ((column - (Columns - 1) / 2f) * Rules.Spacing) + forward * ((0.5f - row) * Rules.Spacing);
        return _anchor[s] + new Vector3(offset, 0f);
    }

    /// <summary>The field a squad goes by; null without a way, or while it is worked on. <paramref name="waiting"/>: it has a way, not ready yet.</summary>
    FlowField? FieldOf(int s, out bool waiting)
    {
        waiting = false;
        if (_goal[s] < 0 || Ways is null) return null;
        var field = Ways.Ready(_goal[s]);
        waiting = field is null;
        return field;
    }

    void MoveAnchor(int s)
    {
        if (!_moving[s] || _halted[s]) return;
        var field = FieldOf(s, out bool waiting);
        if (waiting) return;
        var p = _anchor[s];
        var way = new Vector2(_order[s].X - p.X, _order[s].Y - p.Y);
        float left = way.Length(), stride = MathF.Min(left, Rules.Speed * Rules.SquadSpeedScale * Dt);
        int node = _anchorNode[s];
        if (field is not null && node >= 0 && left > Rules.LastStraight && field.Distance(node) > Rules.LastStraight && field.Next(node) >= 0)
        {
            AnchorAlong(s, field, Rules.Speed * Rules.SquadSpeedScale * Dt);
            float along = field.Distance(_anchorNode[s]);
            if (along < _orderBest[s] - 0.001f) { _orderBest[s] = along; _stallTicks[s] = 0; }
            else if (++_stallTicks[s] >= Ticks(Rules.StallSeconds * 4f)) _moving[s] = false;   // a way has corners: longer than the straight walk's
            return;
        }
        if (left > 1e-4f) Walk(ref _anchor[s], ref _anchorNode[s], way / left * stride);
        // By where it now stands, not by the step it wanted: the walk may have gone nowhere.
        var now = _anchor[s];
        float remaining = new Vector2(_order[s].X - now.X, _order[s].Y - now.Y).Length();
        if (remaining <= 0.01f) { _moving[s] = false; return; }
        if (remaining < _orderBest[s] - 0.001f) { _orderBest[s] = remaining; _stallTicks[s] = 0; }
        else if (++_stallTicks[s] >= Ticks(Rules.StallSeconds)) _moving[s] = false;
    }

    /// <summary>The squad's place along its field's nodes, a walk or a drop alike: it is no body and fits anywhere the men do.</summary>
    void AnchorAlong(int s, FlowField field, float stride)
    {
        var walk = _enemies.Walk!;
        var p = _anchor[s];
        int node = _anchorNode[s];
        for (int k = 0; k < 8 && stride > 0f; k++)
        {
            int next = field.Next(node);
            if (next < 0) break;
            var to = walk.Position(next);
            float d = Vector3.Distance(p, to);
            if (d > stride) { p += (to - p) / d * stride; break; }
            stride -= d;
            p = to;
            node = next;
        }
        var flat = new Vector2(p.X - _anchor[s].X, p.Y - _anchor[s].Y);
        if (flat.LengthSquared() > 1e-6f) _squadYaw[s] = YawOf(flat);
        _anchor[s] = p;
        _anchorNode[s] = node;
    }

    void MoveMan(int i, int s, int m, Span<int> near)
    {
        var p = _position[i];
        var place = Place(s, m);
        var way = new Vector2(place.X - p.X, place.Y - p.Y);
        float distance = way.Length();
        var v = distance > 0.2f ? way / distance * MathF.Min(Rules.Speed, distance / Dt) : Vector2.Zero;

        // On a way: down the field from his own node, keeping with the squad; near his place, or a stand with him close, straight to it.
        var field = FieldOf(s, out _);
        int prefer = -1;
        int node = _node[i];
        if (field is not null && node >= 0 && !float.IsPositiveInfinity(field.Distance(node)) && field.Distance(node) > Rules.LastStraight && ((_moving[s] && !_halted[s]) || distance > Rules.LastStraight))
        {
            int next = field.Next(node);
            var walk = _enemies.Walk!;
            if (next >= 0 && (!walk.Adjacent(node, next) || MathF.Abs(walk.Z(next) - walk.Z(node)) > 1f)) { Drop(i, next); return; }
            if (next >= 0)
            {
                prefer = next;
                way = new Vector2(walk.X(next) - p.X, walk.Y(next) - p.Y);
                if (way.LengthSquared() < 0.0025f && field.Next(next) is var after and >= 0) { prefer = after; way = new Vector2(walk.X(after) - p.X, walk.Y(after) - p.Y); }
                float length = way.Length();
                bool ahead = _anchorNode[s] >= 0 && field.Distance(_anchorNode[s]) - field.Distance(node) > Rules.AheadOfSquad;   // he waits for the squad
                v = length > 1e-4f ? way / length * (ahead ? Rules.Speed * Rules.WaitScale : Rules.Speed) : Vector2.Zero;
            }
        }

        int found = _grid.Around(p, near), pushed = 0, most = Math.Max(0, Rules.MaxNeighbours);
        float gap = Rules.Radius * 2f;
        var push = Vector2.Zero;
        for (int k = 0; k < found && pushed < most; k++)
        {
            int j = near[k];
            if (j == i) continue;
            var o = _startPos[j];
            var away = new Vector2(p.X - o.X, p.Y - o.Y);
            float d = away.Length();
            if (d >= gap) continue;
            push += (d > 1e-4f ? away / d : Forward((i - j) * 2.399963f)) * (gap - d) * Rules.SeparationStrength;
            pushed++;
        }
        bool going = v != Vector2.Zero;
        v += push;
        float l = v.Length();
        if (l > Rules.Speed) v *= Rules.Speed / l;
        if (l > 1e-3f) Walk(ref _position[i], ref _node[i], v * Dt, prefer);
        if (going) _yaw[i] = YawOf(way);
        _state[i] = going ? GuardState.Moving : GuardState.Idle;
    }

    /// <summary>Over a drop's edge: straight to the landing node at his speed, then on the graph again.</summary>
    void Drop(int i, int to)
    {
        var land = _enemies.Walk!.Position(to);
        var p = _position[i];
        float left = Vector3.Distance(p, land), stride = Rules.Speed * Dt;
        if (left <= stride) { _position[i] = land; _node[i] = to; }
        else _position[i] = p + (land - p) / left * stride;
        _state[i] = GuardState.Moving;
    }

    /// <summary>A step on the walkable cells (the nav graph; free without one): straight, else along one axis, else none.</summary>
    void Walk(ref Vector3 position, ref int node, Vector2 step, int prefer = -1)
    {
        var walk = _enemies.Walk;
        var p = position;
        if (walk is not null && node < 0) node = walk.Nearest(p);
        if (walk is null || node < 0) { position = new Vector3(p.X + step.X, p.Y + step.Y, p.Z); return; }
        var at = new Vector2(p.X + step.X, p.Y + step.Y);
        int next = walk.MoveTo(node, at.X, at.Y, prefer);
        if (next < 0) { at = new Vector2(p.X + step.X, p.Y); next = walk.MoveTo(node, at.X, at.Y); }
        if (next < 0) { at = new Vector2(p.X, p.Y + step.Y); next = walk.MoveTo(node, at.X, at.Y); }
        if (next < 0) return;
        node = next;
        position = new Vector3(at.X, at.Y, walk.Z(next));
    }

    ushort NextShot(int i, uint salt)
    {
        float roll = MusouSim.Unit(MusouSim.Mix(Tick + salt, (uint)i * 0x9E3779B1u + 17u));
        return Ticks(Rules.FirePeriod + Rules.FireJitter * (2f * roll - 1f));
    }

    /// <summary>A man's shot when his timer is up: at the squad's first enemy from his own place in its list that is alive, in range and in sight (at most <see cref="GuardRules.SightTries"/> looked at). With none his timer just starts again, so the men stay out of step.</summary>
    void Shoot(int i, int s, int man)
    {
        if (_shotTicks[i] > 0 && --_shotTicks[i] > 0) return;
        _shotTicks[i] = NextShot(i, 1u);
        int seen = _seenCount[s], tries = 0;
        for (int k = 0; k < seen; k++)
        {
            ushort id = _seen[s * GuardRules.SeenPerSquad + (man + k) % seen];
            if (!_enemies.TryGet(id, out var enemy) || !enemy.Alive) continue;
            var d = enemy.Position - _position[i];
            var flat = new Vector2(d.X, d.Y);
            float distance = flat.Length();
            if (distance > Rules.Range) continue;
            if (Sight is not null)
            {
                if (tries++ >= Rules.SightTries) return;
                if (!InSight(i, enemy)) continue;
            }
            Fire(i, id, enemy, distance, flat);
            return;
        }
    }

    /// <summary>Whether a man's eyes see the enemy's chest: the weapons mesh, as a shot.</summary>
    bool InSight(int i, in MusouEnemy enemy)
    {
        LastRays++;
        var eye = _position[i] + new Vector3(0f, 0f, Rules.EyeHeight);
        var ray = enemy.Position + new Vector3(0f, 0f, Rules.ChestHeight) - eye;
        float length = ray.Length();
        return length < 1e-3f || !(Sight!.Weapons.Raycast(eye, ray / length, length) is { } wall && wall.Distance < length - 0.1f);
    }

    void Fire(int i, ushort id, MusouEnemy enemy, float distance, Vector2 flat)
    {
        if (flat.LengthSquared() > 1e-6f) _yaw[i] = YawOf(flat);
        float span = MathF.Max(1e-3f, Rules.Range - Rules.HitNearDistance);
        float chance = Rules.HitNear + (Rules.HitFar - Rules.HitNear) * Math.Clamp((distance - Rules.HitNearDistance) / span, 0f, 1f);
        bool hit = MusouSim.Unit(MusouSim.Mix(Tick, (uint)i * 0x85EBCA6Bu + id)) < chance;
        Shots++;
        var chest = enemy.Position + new Vector3(0f, 0f, 1.2f);
        if (!hit) { AddEvent(new GuardEvent(GuardEventKind.Shot, Tick, (ushort)(i + 1), 0, chest + new Vector3(Forward(_yaw[i] + MathF.PI / 2f), 0f))); return; }
        ShotsHit++;
        AddEvent(new GuardEvent(GuardEventKind.Shot, Tick, (ushort)(i + 1), id, chest));
        var direction = distance > 1e-3f ? flat / distance : Vector2.Zero;
        _enemies.Damage(id, 0, Rules.Damage, new Vector3(direction, 0f), out _);
    }

    void AddEvent(GuardEvent e)
    {
        if (_events.Count < MaxEventsPerTick) _events.Add(e);
    }
}
