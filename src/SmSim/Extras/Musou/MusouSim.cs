using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// Added content "Musou" (experimental; design section B): the enemies of a Musou match, owned by the <see cref="World"/> (<see cref="World.Musou"/>, set only
/// by <see cref="World.StartMusou"/> under <see cref="ContentExtras.Musou"/>). MU0 is the contract: the pool (struct of arrays, <see cref="Capacity"/> slots,
/// a first-in first-out free list so a freed id is reused as late as possible), <see cref="Spawn"/>, <see cref="Damage"/>, <see cref="Query"/> and the tick's
/// <see cref="Events"/>. MU1 (<c>MusouSim.Ai.cs</c>): the enemies' moves and attacks (<see cref="Step"/>: the flow field, the separation, the attack tokens,
/// the melee wind-up and strike, the shooters' line of sight and hit roll); the director (<see cref="MusouDirector"/>) releases them. MU3: combat and the boss.
/// </summary>
public sealed partial class MusouSim
{
    /// <summary>
    /// The Musou version: its simulation rules and its wire sections (<c>MusouWire.Version</c> is this). Hashed into the config only under
    /// <see cref="ContentExtras.Musou"/> (<c>SimConfig.Extras.cs</c>); raise it for any Musou-only change (never <see cref="ContentExtras.WireVersion"/>).
    /// </summary>
    public const uint Version = 6;   // 2: MU3's combat and the Warboss's abilities; 3: the Nobs' weapon drops (wire tag 6); 4: Chaos Marines drop too; 5: Musou Mode (TfwRules.Version in the same hash block, phase 6, command kind 17); 6: the crowd's rules, MusouSim.Swarm (a war's alone: the 5-wave run is unchanged)
    /// <summary>The pool's slots (design: 512; the hard cap of enemies alive is lower, 400).</summary>
    public const int Capacity = 512;
    /// <summary>The most events one tick keeps (ours; more are dropped and counted in <see cref="EventsDropped"/>).</summary>
    public const int MaxEventsPerTick = 1024;

    // One slot per enemy (index = id - 1).
    readonly bool[] _used = new bool[Capacity];
    readonly MusouEnemyType[] _type = new MusouEnemyType[Capacity];
    readonly MusouEnemyState[] _state = new MusouEnemyState[Capacity];
    readonly Vector3[] _position = new Vector3[Capacity];
    readonly Vector2[] _velocity = new Vector2[Capacity];
    readonly float[] _yaw = new float[Capacity];
    readonly float[] _health = new float[Capacity];
    readonly int[] _node = new int[Capacity];
    readonly int[] _target = new int[Capacity];   // a player's id, or a Guard squad's (negative, TfwContracts.GuardTargetId); 0: none
    readonly ushort[] _stateTicks = new ushort[Capacity];
    readonly byte[] _token = new byte[Capacity];
    readonly Queue<int> _free = new(Capacity);
    readonly List<MusouEvent> _events = new();
    readonly List<MusouKill> _kills = new();
    readonly float[] _queryDistance = new float[Capacity];   // Query's scratch (single-threaded)

    public MusouSim(NavGraph? nav, MusouRules? rules = null)
    {
        Nav = nav;
        Rules = rules ?? MusouRules.Default;
        for (int i = 0; i < Capacity; i++) _free.Enqueue(i);
        Walk = nav is { NodeCount: > 0 } ? new MusouNav(nav) : null;
        Flow = Walk is null ? null : new FlowField(Walk);
        Box = Walk?.Box() ?? new MusouBox(new Vector3(-64f, -64f, -2f), new Vector3(64f, 64f, 8f));
        _grid = new SpatialGrid(Box, Rules.SeparationCell);
    }

    /// <summary>The walking graph enemies move on (the server's; null on the flat placeholder ground or when it could not be built).</summary>
    public NavGraph? Nav { get; }
    /// <summary>The tuning (the match's <see cref="SimConfig.MusouRules"/>, else <see cref="MusouRules.Default"/>).</summary>
    public MusouRules Rules { get; }
    /// <summary>The graph as the enemies use it (null without one: they then walk the flat ground at z = 0).</summary>
    public MusouNav? Walk { get; }
    /// <summary>The shared flow field (null without a graph).</summary>
    public FlowField? Flow { get; }
    /// <summary>The box positions are quantised over on the wire (the graph's, or 128 m around the origin on the flat ground).</summary>
    public MusouBox Box { get; private set; }
    /// <summary>Grows <see cref="Box"/> to hold <paramref name="other"/> as well (the war's map: the Stormbird lies outside the graph). The enemies' grid stays over the graph, where they walk.</summary>
    public void WidenBox(MusouBox other) => Box = new MusouBox(Vector3.Min(Box.Min, other.Min), Vector3.Max(Box.Max, other.Max));
    /// <summary>The world tick the events are of (<see cref="BeginTick"/>).</summary>
    public uint Tick { get; private set; }
    /// <summary>Slots in use (alive or dying).</summary>
    public int Count { get; private set; }
    /// <summary>Enemies alive and in play.</summary>
    public int Alive { get; private set; }
    /// <summary>This tick's events, in the order they happened.</summary>
    public IReadOnlyList<MusouEvent> Events => _events;
    /// <summary>Events dropped over <see cref="MaxEventsPerTick"/> since the start.</summary>
    public int EventsDropped { get; private set; }
    /// <summary>The director's state for the clients (tag 5). The director (MU1) sets it each tick; the server sends it (MU2).</summary>
    public MusouDirectorView Director { get; set; }

    /// <summary>A new tick: clears the last tick's events. Called by the World once a tick (the <c>ExtrasStep</c> hook).</summary>
    public void BeginTick(uint tick)
    {
        Tick = tick;
        _events.Clear();
    }

    /// <summary>
    /// Takes a free slot for an enemy of <paramref name="type"/> at <paramref name="position"/> with <paramref name="health"/> hit points, in
    /// <see cref="MusouEnemyState.Spawning"/>; its id, or 0 when the pool is full or a value is not finite (the director keeps the lower cap of enemies alive,
    /// <see cref="MusouRules.HardCap"/>).
    /// </summary>
    public ushort Spawn(MusouEnemyType type, Vector3 position, float yaw, float health, int node = -1)
    {
        if (_free.Count == 0 || !float.IsFinite(health) || !float.IsFinite(yaw) || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return 0;
        if ((int)type >= Rules.Types.Count) return 0;
        int i = _free.Dequeue();
        _used[i] = true;
        _type[i] = type; _state[i] = MusouEnemyState.Spawning;
        _position[i] = position; _velocity[i] = Vector2.Zero; _yaw[i] = yaw;
        Array.Fill(_trail, position, i * TrailLength, TrailLength);
        _health[i] = MathF.Max(1f, health); _node[i] = node;
        _target[i] = 0; _stateTicks[i] = 0; _token[i] = 0;
        SpawnedAi(i);
        Count++; Alive++;
        return (ushort)(i + 1);
    }

    /// <summary>
    /// <see cref="Spawn"/> for a leashed defender (Musou Mode, design section D): it stands at home until a target comes within <paramref name="aggro"/> metres of that home
    /// (flat), goes back when none is, and is free for good once any member of <paramref name="group"/> (above 0) is hurt or <see cref="ReleaseGroup"/> is called. Its id, or 0 as
    /// <see cref="Spawn"/>, or for a group that is not above 0 or an aggro that is not 0 or more. A plain <see cref="Spawn"/> has no leash (aggro infinite), so the 5-wave run is unchanged.
    /// </summary>
    public ushort SpawnLeashed(MusouEnemyType type, Vector3 position, float yaw, float health, int group, float aggro, int node = -1)
    {
        if (group <= 0 || !(aggro >= 0f)) return 0;
        ushort id = Spawn(type, position, yaw, health, node);
        if (id == 0) return 0;
        _home[id - 1] = _position[id - 1];   // as the graph placed it
        _aggro[id - 1] = aggro;
        _group[id - 1] = group;
        return id;
    }

    /// <summary>Frees every member of a leashed <paramref name="group"/> for good, in id order; the number freed.</summary>
    public int ReleaseGroup(int group)
    {
        if (group <= 0) return 0;
        int n = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || _group[i] != group) continue;
            _group[i] = 0; _aggro[i] = float.PositiveInfinity;
            n++;
        }
        return n;
    }

    /// <summary>Whether the enemy still has a leash (false for an unknown, unleashed or released one).</summary>
    public bool Leashed(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _group[id - 1] != 0;

    /// <summary>What the enemy goes for as stored: a player's id, a Guard squad's negative <see cref="MusouTarget.Id"/>, or 0 (<see cref="MusouEnemy.TargetPlayer"/> shows players only).</summary>
    public int TargetOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] ? _target[id - 1] : 0;

    /// <summary>The enemy's target as the public view has it: a player, or 0 for none or a Guard squad.</summary>
    ushort TargetPlayer(int i) => (ushort)Math.Max(0, _target[i]);

    /// <summary>The enemy with <paramref name="id"/>, if its slot is in use (alive or dying).</summary>
    public bool TryGet(ushort id, out MusouEnemy enemy)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i]) { enemy = default; return false; }
        enemy = new MusouEnemy(id, _type[i], _state[i], _position[i], _velocity[i], _yaw[i], _health[i], _node[i], TargetPlayer(i), _stateTicks[i], _token[i]);
        return true;
    }

    /// <summary>
    /// Damage to an enemy alive and in play (<paramref name="amount"/> above 0): its health goes down, a <see cref="MusouEventKind.Hit"/> is recorded with
    /// <paramref name="marker"/>, and at 0 it dies (<see cref="MusouEnemyState.Dying"/>, a <see cref="MusouEventKind.Death"/> with the push). False, and
    /// nothing changed, for an unknown, dying or dead enemy or no damage. The slot stays used until <see cref="Remove"/>.
    /// </summary>
    public bool Damage(ushort id, int attacker, float amount, Vector3 push, out bool killed, byte marker = 1, MusouDeathKind cause = MusouDeathKind.Shot)
    {
        killed = false;
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _state[i] == MusouEnemyState.Dying || !(amount > 0f)) return false;
        _health[i] = MathF.Max(0f, _health[i] - amount);
        if (_group[i] != 0) ReleaseGroup(_group[i]);   // a hurt defender wakes its whole group (MusouSim.Ai.cs)
        AddEvent(new MusouEvent(MusouEventKind.Hit, Tick, id, (ushort)Math.Clamp(attacker, 0, ushort.MaxValue), marker, _position[i]));
        if (_health[i] > 0f) return true;
        killed = true;
        _state[i] = MusouEnemyState.Dying; _stateTicks[i] = 0; _velocity[i] = Vector2.Zero; _token[i] = 0;
        Alive--;
        _kills.Add(new MusouKill(id, _type[i], attacker, cause));
        float length = push.Length();
        AddEvent(new MusouEvent(MusouEventKind.Death, Tick, id, (ushort)Math.Clamp(attacker, 0, ushort.MaxValue), (byte)cause, length > 1f ? push / length : push));
        return true;
    }

    /// <summary>Frees an enemy's slot (a dead one's body is gone, or the round restarts); false when it was not in use.</summary>
    public bool Remove(ushort id)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i]) return false;
        if (_state[i] != MusouEnemyState.Dying) Alive--;
        _used[i] = false;
        Count--;
        _free.Enqueue(i);
        return true;
    }

    /// <summary>Frees every slot (a new round), in id order, and forgets the round's own state (the flow field, the dropped-event count, the AI's bookkeeping).</summary>
    public void Clear()
    {
        for (int i = 0; i < Capacity; i++) if (_used[i]) Remove((ushort)(i + 1));
        _frenzyTicks = 0;   // MU3: the Waaagh!'s speed ends with the round
        _events.Clear();
        _kills.Clear();
        EventsDropped = 0;
        ResetAi();
    }

    /// <summary>
    /// The KOs since the last call (each enemy once, in the order they died, whenever <see cref="Damage"/> killed it: in a tick or between ticks), moved into
    /// <paramref name="into"/>; the number moved. The World credits them (scores, the director's count).
    /// </summary>
    public int TakeKills(List<MusouKill> into)
    {
        into.AddRange(_kills);
        int n = _kills.Count;
        _kills.Clear();
        return n;
    }

    /// <summary>
    /// The enemies alive and in play whose feet are within <paramref name="radius"/> of <paramref name="centre"/> horizontally and within
    /// <paramref name="verticalReach"/> vertically, nearest first (ties: lowest id), written into <paramref name="into"/> as far as it holds them; the number
    /// written.
    /// </summary>
    public int Query(Vector3 centre, float radius, Span<ushort> into, float verticalReach = 2.5f)
    {
        if (into.Length == 0 || !(radius >= 0f)) return 0;
        Span<float> distance = into.Length <= _queryDistance.Length ? _queryDistance.AsSpan(0, into.Length) : new float[into.Length];   // MU3a: no allocation
        int n = 0;
        float r2 = radius * radius;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || _state[i] == MusouEnemyState.Dying) continue;
            var d = _position[i] - centre;
            if (MathF.Abs(d.Z) > verticalReach) continue;
            float flat = d.X * d.X + d.Y * d.Y;
            if (flat > r2) continue;
            // Insert in order (nearest first; equal distances keep id order, as slots are visited in id order).
            int at = n;
            while (at > 0 && distance[at - 1] > flat) at--;
            if (at >= into.Length) continue;
            int last = Math.Min(n, into.Length - 1);
            for (int k = last; k > at; k--) { into[k] = into[k - 1]; distance[k] = distance[k - 1]; }
            into[at] = (ushort)(i + 1); distance[at] = flat;
            if (n < into.Length) n++;
        }
        return n;
    }

    /// <summary>Every slot in use (alive or dying), in id order, written into <paramref name="into"/> as far as it holds them; the number written.</summary>
    public int CopyTo(Span<MusouEnemy> into)
    {
        int n = 0;
        for (int i = 0; i < Capacity && n < into.Length; i++)
            if (_used[i]) into[n++] = new MusouEnemy((ushort)(i + 1), _type[i], _state[i], _position[i], _velocity[i], _yaw[i], _health[i], _node[i], TargetPlayer(i), _stateTicks[i], _token[i]);
        return n;
    }

    /// <summary>For the fight's report: the melee and shooter tokens held and the enemies in a strike's wind-up, blow or recovery.</summary>
    public (int Melee, int Shooters, int Striking) Tally()
    {
        int melee = 0, shooters = 0, striking = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_used[i] || _state[i] == MusouEnemyState.Dying) continue;
            if (_token[i] == MeleeToken) melee++;
            else if (_token[i] == ShooterToken) shooters++;
            if (_state[i] is MusouEnemyState.WindUp or MusouEnemyState.Strike or MusouEnemyState.Recover) striking++;
        }
        return (melee, shooters, striking);
    }

    /// <summary>Records an event of this tick (dropped and counted over <see cref="MaxEventsPerTick"/>).</summary>
    public void AddEvent(MusouEvent e)
    {
        if (_events.Count >= MaxEventsPerTick) { EventsDropped++; return; }
        _events.Add(e);
    }
}
