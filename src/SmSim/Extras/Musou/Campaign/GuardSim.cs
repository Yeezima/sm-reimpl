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
/// With <see cref="GuardRules.Spread"/> (musou/guard-spread, 2026-11-08) a squad walks in open order in its lane on a way kept off the walls, a stuck man detours, and a squad sent to hold a point splits into fire teams round it (<see cref="MoveManSpread"/>).
/// </summary>
public sealed class GuardSim
{
    public const int SquadSize = 10, MaxSquads = 40,   // 24 until expansion S6 (the battle flow's garrisons of 200 near a player; TfwArmyRules.NearSquadsMost)
    Capacity = SquadSize * MaxSquads, MaxEventsPerTick = 512;
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
    // musou/guard-spread (GuardRules.Spread): his post round a held point, whether his straight line to his place is walkable, how long he has been stuck, his detour left, and his place's pull-in.
    readonly Vector3[] _post = new Vector3[Capacity];
    readonly bool[] _direct = new bool[Capacity];
    readonly ushort[] _stuckTicks = new ushort[Capacity];
    readonly ushort[] _detourTicks = new ushort[Capacity];
    readonly float[] _fit = new float[Capacity];

    readonly bool[] _active = new bool[MaxSquads];           // any man in the squad's slots, dying included
    readonly int[] _living = new int[MaxSquads];
    readonly float[] _strength = new float[MaxSquads];   // musou/perf-pass (the owner's "near matches far"): a squad's share of the Guardsmen's health and shot damage (1: GuardRules' own)
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
    readonly float[] _holdRadius = new float[MaxSquads];     // musou/guard-spread: > 0, the order holds a point of this radius round _holdCentre (its men split into fire teams on arrival)
    readonly Vector3[] _holdCentre = new Vector3[MaxSquads];
    readonly bool[] _posted = new bool[MaxSquads];           // the posts of its hold are set

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
    /// <summary>Expansion S4 (the owner, 2026-11-08): whether a dome stands between a man's eye and his mark (from outside it), so the shot is not fired at that mark (null: never).</summary>
    public Func<Vector3, Vector3, bool>? Shielded { get; set; }
    /// <summary>Expansion S4 (the owner, 2026-11-08): the nearest built Rokkit turret within a range of a point, and its middle (id 0: none); null: no turrets.</summary>
    public TurretFinder? Turrets { get; set; }
    /// <summary>A Guardsman's hit on a Rokkit turret (its id, the damage).</summary>
    public Action<byte, float>? TurretHit { get; set; }
    /// <summary>The turret query's shape (<see cref="Turrets"/>).</summary>
    public delegate byte TurretFinder(Vector3 from, float range, out Vector3 centre);
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
    public int SpawnSquad(Vector3 centre, float yaw = 0f) => SpawnSquad(centre, yaw, SquadSize);

    /// <summary>A squad of <paramref name="men"/> (1 to <see cref="SquadSize"/>; expansion S6's armies promote a short one) in the first free squad, the rest of its slots empty; as <see cref="SpawnSquad(Vector3, float)"/>.</summary>
    public int SpawnSquad(Vector3 centre, float yaw, int men) => SpawnSquad(centre, yaw, men, 1f);

    /// <summary>
    /// As <see cref="SpawnSquad(Vector3, float, int)"/>, its men at <paramref name="strength"/> times the rules' health and shot damage (musou/perf-pass, the owner's "near matches far" of 2026-11-08: an army's
    /// squads promoted near a player fight as its aggregate would; the field's squads keep 1).
    /// </summary>
    public int SpawnSquad(Vector3 centre, float yaw, int men, float strength)
    {
        if (!Finite(centre) || !float.IsFinite(yaw) || men < 1 || !(strength > 0f)) return -1;
        men = Math.Min(men, SquadSize);
        int s = Array.IndexOf(_active, false);
        if (s < 0) return -1;
        var walk = _enemies.Walk;
        int anchorNode = walk?.Nearest(centre) ?? -1;
        if (anchorNode >= 0) centre = centre with { Z = walk!.Z(anchorNode) };
        _active[s] = true; _living[s] = men; _strength[s] = strength;
        _anchor[s] = centre; _anchorNode[s] = anchorNode; _squadYaw[s] = yaw; _order[s] = centre; _moving[s] = false;
        _seenCount[s] = 0; _goal[s] = -1; _halted[s] = false; _holdRadius[s] = 0f; _posted[s] = false;
        for (int m = 0; m < SquadSize; m++)
        {
            int i = s * SquadSize + m;
            _direct[i] = false; _stuckTicks[i] = 0; _detourTicks[i] = 0; _fit[i] = 1f;
            if (m >= men) { _state[i] = GuardState.Free; continue; }
            var place = Place(s, m);
            int node = walk?.Nearest(place) ?? -1;
            _state[i] = GuardState.Idle; _stateTicks[i] = 0;
            _position[i] = node >= 0 ? place with { Z = walk!.Z(node) } : walk is null ? place : centre;   // a place in a wall: he starts at the squad's centre
            _node[i] = node >= 0 ? node : anchorNode; _yaw[i] = yaw; _health[i] = Rules.Health * strength;
            _shotTicks[i] = NextShot(i, 0u);
        }
        _centre[s] = centre;
        _reach[s] = 0f;
        Alive += men; Count += men;
        return s;
    }

    /// <summary>
    /// Takes squad <paramref name="squad"/> out at once, its living and dying alike, with no death and no event (expansion S6: an army folds a squad back into its aggregate far from every player). The men
    /// that were alive; 0, and nothing changed, for a squad not in use.
    /// </summary>
    public int Dismiss(int squad)
    {
        if (squad is < 0 or >= MaxSquads || !_active[squad]) return 0;
        int living = _living[squad];
        for (int i = squad * SquadSize; i < (squad + 1) * SquadSize; i++)
        {
            if (_state[i] == GuardState.Free) continue;
            _state[i] = GuardState.Free;
            Count--;
        }
        Alive -= living;
        _living[squad] = 0;
        _active[squad] = false; _moving[squad] = false; _seenCount[squad] = 0; _goal[squad] = -1; _halted[squad] = false; _holdRadius[squad] = 0f; _posted[squad] = false;
        return living;
    }

    /// <summary>The living man of squad <paramref name="squad"/> nearest <paramref name="at"/> dies (expansion S6: a far clash's losses on real men nobody sees); false with none.</summary>
    public bool KillOne(int squad, Vector3 at)
    {
        if (AliveIn(squad) == 0) return false;
        int best = -1; float bestD = float.PositiveInfinity;
        for (int i = squad * SquadSize; i < (squad + 1) * SquadSize; i++)
        {
            if (!Living(i)) continue;
            float d = Vector3.DistanceSquared(_position[i], at);
            if (d < bestD) { bestD = d; best = i; }
        }
        _health[best] = 0f;
        Fall(best);
        return true;
    }

    /// <summary>
    /// Sends a squad to <paramref name="destination"/>: by the way to <paramref name="via"/> (no place named: to the destination), then straight the last metres, so squads bound for one point share a field;
    /// straight all the way without a graph. False for a squad without living men or a position that is not finite. <paramref name="holdRadius"/> &gt; 0 (musou/guard-spread, with
    /// <see cref="GuardRules.Spread"/>): the order holds a point of that radius round <paramref name="via"/> (else the destination): on arrival the squad splits into fire teams round it.
    /// </summary>
    public bool Order(int squad, Vector3 destination, Vector3? via = null, float holdRadius = 0f)
    {
        if (AliveIn(squad) == 0 || !Finite(destination)) return false;
        _holdRadius[squad] = Rules.Spread && float.IsFinite(holdRadius) && holdRadius > 0f ? holdRadius : 0f;
        _holdCentre[squad] = via is { } v && Finite(v) ? v : destination;
        _posted[squad] = false;
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

    /// <summary>Every man within <paramref name="radius"/> of <paramref name="centre"/> (flat) dies (a shell, World.TfwFire.cs); how many.</summary>
    public int KillWithin(Vector3 centre, float radius)
    {
        int n = 0; float r2 = radius * radius;
        for (int i = 0; i < Capacity; i++)
        {
            if (!Living(i)) continue;
            float dx = _position[i].X - centre.X, dy = _position[i].Y - centre.Y;
            if (dx * dx + dy * dy > r2) continue;
            _health[i] = 0f; _state[i] = GuardState.Dying; _stateTicks[i] = 0;
            _living[i / SquadSize]--; Alive--; Lost++; n++;
            AddEvent(new GuardEvent(GuardEventKind.Death, Tick, (ushort)(i + 1), 0, _position[i]));
        }
        return n;
    }

    /// <summary>
    /// An explosion on the men (expansion S1, <c>World.MusouOrkBlast</c>): every living man within <paramref name="radius"/> of <paramref name="point"/> (metres, height x 3) takes <paramref name="damage"/>
    /// times <paramref name="intensity"/> of his distance. Returns the men hit; the dead are counted in <paramref name="killed"/>.
    /// </summary>
    public int Blast(Vector3 point, float radius, Func<float, float> intensity, float damage, out int killed, Func<Vector3, bool>? spared = null)
    {
        int hit = 0; killed = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!Living(i)) continue;
            if (spared is not null && spared(_position[i] + new Vector3(0f, 0f, Rules.ChestHeight))) continue;   // expansion S4: a dome keeps a blast from outside off him
            var d = _position[i] - point;
            float distance = MathF.Sqrt(d.X * d.X + d.Y * d.Y + 9f * d.Z * d.Z);
            if (distance > radius) continue;
            float amount = damage * intensity(distance);
            if (!(amount > 0f)) continue;
            hit++;
            _health[i] = MathF.Max(0f, _health[i] - amount);
            if (_health[i] > 0f) { AddEvent(new GuardEvent(GuardEventKind.Hurt, Tick, (ushort)(i + 1), 0, _position[i])); continue; }
            Fall(i); killed++;
        }
        return hit;
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
            Knock(i, heading + away, distance, ticks, rules);
        }
        return shoved;
    }

    /// <summary>Man <paramref name="i"/> is thrown <paramref name="distance"/> along <paramref name="push"/> over <paramref name="ticks"/> and lies down (<see cref="GuardState.Knocked"/>).</summary>
    void Knock(int i, Vector2 push, float distance, ushort ticks, TfwCombatRules rules)
    {
        float length = push.Length();
        _throw[i] = length > 1e-4f ? push / length * (distance / (ticks * Dt)) : Vector2.Zero;
        _throwTicks[i] = ticks;
        _downTicks[i] = Ticks(rules.KnockedSeconds);
        _state[i] = GuardState.Knocked; _stateTicks[i] = 0;
    }

    /// <summary>
    /// A Boss Ork's cleave (owner, 2026-11-06: "the boss orks should be able to cleave guardsmen the way that players cleave orks"; the players' cleave's rules, <c>World.MusouCleave</c>): every living man
    /// within <paramref name="reach"/> of <paramref name="at"/> (flat, 1.5 m up or down), in front of <paramref name="forward"/> by at least <paramref name="minCos"/> (one overlapping counts as in front),
    /// at most <paramref name="most"/> nearest first, takes <paramref name="damage"/>; one who lives is thrown <see cref="TfwCombatRules.MeleePush"/> and falls, or with <see cref="TfwCombatRules.MeleeDeath"/>
    /// dies at once (as a fodder Ork the players' melee hits). Men killed count in <see cref="Lost"/>, credited to nobody. How many were struck.
    /// </summary>
    public int Cleave(Vector3 at, Vector2 forward, float reach, float minCos, int most, float damage, TfwCombatRules rules)
    {
        if (!(damage > 0f) || most <= 0 || !Finite(at)) return 0;
        Span<int> slots = stackalloc int[Capacity];
        Span<float> distances = stackalloc float[Capacity];
        int n = 0;
        float r2 = reach * reach;
        for (int i = 0; i < Capacity; i++)
        {
            if (!Living(i) || MathF.Abs(_position[i].Z - at.Z) > 1.5f) continue;
            var flat = new Vector2(_position[i].X - at.X, _position[i].Y - at.Y);
            float d2 = flat.LengthSquared();
            if (d2 > r2) continue;
            if (d2 > 1e-8f && Vector2.Dot(flat / MathF.Sqrt(d2), forward) < minCos) continue;
            int k = n++;
            while (k > 0 && distances[k - 1] > d2) { slots[k] = slots[k - 1]; distances[k] = distances[k - 1]; k--; }   // nearest first (ties: the lower slot)
            slots[k] = i; distances[k] = d2;
        }
        n = Math.Min(n, most);
        ushort ticks = Ticks(_enemies.Rules.Combat.KnockbackSeconds);
        for (int k = 0; k < n; k++)
        {
            int i = slots[k];
            _health[i] = MathF.Max(0f, _health[i] - damage);
            if (_health[i] <= 0f || MusouSim.Unit(MusouSim.Mix(Tick, (uint)i * 0x27D4EB2Fu + 0x61C88647u)) < rules.MeleeDeath) { Fall(i); continue; }
            AddEvent(new GuardEvent(GuardEventKind.Hurt, Tick, (ushort)(i + 1), 0, _position[i]));
            var away = new Vector2(_position[i].X - at.X, _position[i].Y - at.Y);
            Knock(i, away.LengthSquared() > 1e-8f ? Vector2.Normalize(away) : forward, rules.MeleePush, ticks, rules);
        }
        return n;
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
        Array.Clear(_state); Array.Clear(_active); Array.Clear(_living); Array.Clear(_seenCount); Array.Clear(_moving); Array.Clear(_halted); Array.Clear(_holdRadius); Array.Clear(_posted);
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
            int next = WayNext(field, node);
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

    /// <summary>
    /// One step down a field (musou/guard-spread, the owner's "walk in straight lines directly next to walls"): with <see cref="GuardRules.Spread"/>, of the linked neighbours that get nearer, the one
    /// whose directness (metres nearer a metre walked) plus its clearance from the walls (to <see cref="GuardRules.WayClearance"/>, times <see cref="GuardRules.WayClearanceWeight"/>) is highest, so the
    /// way leaves the wall where it can and still always gets nearer (no loop); a drop is taken as the field's own step. Without the spread, the field's own step.
    /// </summary>
    int WayNext(FlowField field, int node)
    {
        int next = field.Next(node);
        if (!Rules.Spread || next < 0) return next;
        var walk = _enemies.Walk!;
        if (!walk.Adjacent(node, next) || MathF.Abs(walk.Z(next) - walk.Z(node)) > 1f) return next;
        float here = field.Distance(node), reach = MathF.Max(0.01f, Rules.WayClearance), best = float.NegativeInfinity;
        int choice = next;
        for (int l = walk.ForwardStart(node); l < walk.ForwardEnd(node); l++)
        {
            int t = walk.ForwardTo(l);
            float d = field.Distance(t);
            if (!(d < here) || !walk.Adjacent(node, t) || MathF.Abs(walk.Z(t) - walk.Z(node)) > 1f) continue;
            float cost = MathF.Max(1e-3f, walk.ForwardCost(l));
            float score = MathF.Min(1f, (here - d) / cost) + Rules.WayClearanceWeight * MathF.Min(walk.EdgeDistance(t), reach) / reach;
            if (score > best) { best = score; choice = t; }
        }
        return choice;
    }

    /// <summary>
    /// <paramref name="origin"/> + <paramref name="offset"/> pulled in towards <paramref name="origin"/> (whole, three quarters, half, a quarter, none) until it stands on the graph at least
    /// <see cref="GuardRules.PlaceClearance"/> from a wall and, with <paramref name="straight"/>, can be walked to in a straight line from <paramref name="origin"/>; the scale taken.
    /// Without a graph the whole offset.
    /// </summary>
    float FitScale(Vector3 origin, Vector2 offset, bool straight)
    {
        var walk = _enemies.Walk;
        if (walk is null) return 1f;
        float bestScale = 0f, bestEdge = -1f;
        for (int k = 4; k >= 1; k--)
        {
            float f = k * 0.25f;
            var q = new Vector2(origin.X + offset.X * f, origin.Y + offset.Y * f);
            int node = walk.NodeIn(walk.ColumnOf(q.X, q.Y), origin.Z, 1.5f);
            if (node < 0) continue;
            if (straight && !walk.Graph.CanWalk(origin, offset * f)) continue;
            float edge = walk.EdgeDistance(node);
            if (edge >= Rules.PlaceClearance) return f;
            if (edge > bestEdge) { bestEdge = edge; bestScale = f; }
        }
        return bestScale;
    }

    /// <summary>A man's place on the move (musou/guard-spread): two loose ranks of five, <see cref="GuardRules.MoveSpacing"/> abreast, the second half a gap aside, each man shifted by his own roll, the squad in its lane; his last <see cref="FitScale"/> applied.</summary>
    Vector2 MoveOffset(int s, int man)
    {
        int i = s * SquadSize + man, column = man % Columns, row = man / Columns;
        var forward = Forward(_squadYaw[s]);
        var right = new Vector2(forward.Y, -forward.X);
        float jx = (2f * MusouSim.Unit(MusouSim.Mix((uint)i, 0x5EA7u)) - 1f) * Rules.MoveJitter, jy = (2f * MusouSim.Unit(MusouSim.Mix((uint)i, 0xF11Eu)) - 1f) * Rules.MoveJitter;
        int lane = (s % 3) switch { 1 => 1, 2 => -1, _ => 0 };
        float across = (column - (Columns - 1) / 2f) * Rules.MoveSpacing + (row == 0 ? -0.25f : 0.25f) * Rules.MoveSpacing + jx + lane * Rules.LaneSpacing;
        float along = (0.5f - row) * Rules.MoveRankSpacing + jy;
        return right * across + forward * along;
    }

    /// <summary>
    /// The posts of a squad holding a point (musou/guard-spread, the owner's "after its captured they splinter and spread out around the point to defend it"): <see cref="GuardRules.HoldTeams"/> fire teams over an
    /// arc of <see cref="GuardRules.HoldArcDegrees"/> centred on the squad's bearing from the point (its own place on it), at <see cref="GuardRules.HoldRadiusScale"/> of the radius, each in the best cover near its spot
    /// (<see cref="CoverSpot"/>), its men <see cref="GuardRules.TeamSpacing"/> apart across the bearing. Set once, on arrival.
    /// </summary>
    void SetPosts(int s)
    {
        _posted[s] = true;
        var centre = _holdCentre[s];
        var bearing = new Vector2(_order[s].X - centre.X, _order[s].Y - centre.Y);
        float baseAngle = bearing.LengthSquared() > 0.25f ? MathF.Atan2(bearing.Y, bearing.X) : s * 2.399963f;
        int teams = Math.Clamp(Rules.HoldTeams, 1, SquadSize);
        float arc = Rules.HoldArcDegrees * MathF.PI / 180f, radius = _holdRadius[s] * Rules.HoldRadiusScale;
        for (int t = 0; t < teams; t++)
        {
            int first = (t * SquadSize + teams - 1) / teams, end = ((t + 1) * SquadSize + teams - 1) / teams;
            float angle = baseAngle + (teams == 1 ? 0f : (t / (teams - 1f) - 0.5f) * arc);
            var outward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var spot = CoverSpot(centre, new Vector2(centre.X, centre.Y) + outward * radius, _holdRadius[s] * 0.95f);
            var across = new Vector2(-outward.Y, outward.X);
            for (int m = first; m < end; m++)
            {
                int i = s * SquadSize + m;
                var offset = across * ((m - first - (end - first - 1) / 2f) * Rules.TeamSpacing);
                float f = FitScale(spot, offset, straight: false);
                var q = spot + new Vector3(offset * f, 0f);
                _post[i] = Grounded(q, spot.Z);
                _direct[i] = false;
            }
        }
    }

    /// <summary>The place on the graph under <paramref name="q"/> near <paramref name="z"/> (its own height when none).</summary>
    Vector3 Grounded(Vector3 q, float z)
    {
        var walk = _enemies.Walk;
        if (walk is null) return q;
        int node = walk.NodeIn(walk.ColumnOf(q.X, q.Y), z, 1.5f);
        return node >= 0 ? q with { Z = walk.Z(node) } : q with { Z = z };
    }

    /// <summary>
    /// A fire team's spot near <paramref name="want"/>: of it and 16 places round it (<see cref="GuardRules.CoverSearch"/> and half that out), the walkable one reachable in a straight line from the point's
    /// <paramref name="centre"/> and within <paramref name="within"/> of it whose wall distance is nearest <see cref="GuardRules.CoverEdge"/> (beside cover, not in it), the nearer to <paramref name="want"/> on a tie; none: pulled in towards the centre.
    /// </summary>
    Vector3 CoverSpot(Vector3 centre, Vector2 want, float within)
    {
        var walk = _enemies.Walk;
        if (walk is null) return new Vector3(want, centre.Z);
        var best = Vector3.Zero;
        float bestScore = float.PositiveInfinity;
        for (int k = 0; k < 17; k++)
        {
            var q = want;
            if (k > 0)
            {
                float a = (k - 1) % 8 * (MathF.PI / 4f), r = k <= 8 ? Rules.CoverSearch : Rules.CoverSearch * 0.5f;
                q += new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
            }
            int node = walk.NodeIn(walk.ColumnOf(q.X, q.Y), centre.Z, 2f);
            if (node < 0) continue;
            var from = new Vector2(q.X - centre.X, q.Y - centre.Y);
            if (from.LengthSquared() > within * within || !walk.Graph.CanWalk(centre, from)) continue;   // inside the point, so the squad's centre stays on it
            float score = MathF.Abs(walk.EdgeDistance(node) - Rules.CoverEdge) + 0.05f * Vector2.Distance(q, want);
            if (score < bestScore) { bestScore = score; best = new Vector3(q, walk.Z(node)); }
        }
        if (!float.IsPositiveInfinity(bestScore)) return best;
        var off = want - new Vector2(centre.X, centre.Y);
        float f = FitScale(centre, off, straight: true);
        return Grounded(centre + new Vector3(off * f, 0f), centre.Z);
    }

    void MoveMan(int i, int s, int m, Span<int> near)
    {
        if (Rules.Spread) { MoveManSpread(i, s, m, near); return; }
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

    /// <summary>
    /// A man's step with <see cref="GuardRules.Spread"/> (musou/guard-spread): his place is his loose-order place on the move (<see cref="MoveOffset"/>, pulled in off the walls and kept in a straight line of the
    /// squad's centre), his post holding a point (<see cref="SetPosts"/>), else his place in the standing formation. He walks straight to it while that line is walkable (checked every
    /// <see cref="GuardRules.DirectCheckTicks"/>), else, and while on a detour, he goes by the way from his own node (<see cref="WayNext"/>). A man who stops getting anywhere is put on a detour
    /// (<see cref="GuardRules.StuckSeconds"/>). Separation as before.
    /// </summary>
    void MoveManSpread(int i, int s, int m, Span<int> near)
    {
        var p = _position[i];
        var walk = _enemies.Walk;
        bool check = (Tick + (uint)i) % (uint)Math.Max(1, Rules.DirectCheckTicks) == 0u;
        bool holding = !_moving[s] && _holdRadius[s] > 0f;
        if (holding && !_posted[s]) SetPosts(s);
        Vector3 place;
        if (_moving[s])
        {
            var offset = MoveOffset(s, m);
            float left = new Vector2(_order[s].X - _anchor[s].X, _order[s].Y - _anchor[s].Y).Length();
            offset *= MathF.Max(Rules.Spacing / MathF.Max(Rules.Spacing, Rules.MoveSpacing), MathF.Min(1f, left / MathF.Max(0.1f, Rules.MoveTaper)));   // the open order closes over the last metres
            if (check) _fit[i] = FitScale(_anchor[s], offset, straight: true);
            place = Grounded(_anchor[s] + new Vector3(offset * _fit[i], 0f), _anchor[s].Z);
        }
        else place = holding ? _post[i] : Place(s, m);
        var way = new Vector2(place.X - p.X, place.Y - p.Y);
        float distance = way.Length();
        if (walk is null) _direct[i] = true;
        else if (check || distance <= 0.2f) _direct[i] = distance <= 0.2f || (distance <= 30f && walk.Graph.CanWalk(p, way));
        bool detour = _detourTicks[i] > 0;
        if (detour) _detourTicks[i]--;
        var v = distance > 0.2f ? way / distance * MathF.Min(Rules.Speed, distance / Dt) : Vector2.Zero;

        var field = FieldOf(s, out _);
        int prefer = -1;
        int node = _node[i];
        if (field is not null && node >= 0 && !float.IsPositiveInfinity(field.Distance(node)) && field.Distance(node) > Rules.LastStraight && (detour || !_direct[i]) && distance > 0.2f)
        {
            int next = WayNext(field, node);
            if (next >= 0 && (!walk!.Adjacent(node, next) || MathF.Abs(walk.Z(next) - walk.Z(node)) > 1f)) { Drop(i, next); return; }
            if (next >= 0)
            {
                prefer = next;
                way = new Vector2(walk!.X(next) - p.X, walk.Y(next) - p.Y);
                if (way.LengthSquared() < 0.0025f && WayNext(field, next) is var after and >= 0) { prefer = after; way = new Vector2(walk.X(after) - p.X, walk.Y(after) - p.Y); }
                float length = way.Length();
                bool ahead = _moving[s] && _anchorNode[s] >= 0 && field.Distance(_anchorNode[s]) - field.Distance(node) > Rules.AheadOfSquad;   // he waits for the squad
                v = length > 1e-4f ? way / length * (ahead ? Rules.Speed * Rules.WaitScale : Rules.Speed) : Vector2.Zero;
            }
        }
        else if (detour && v != Vector2.Zero)
        {
            // No way to follow: sideways round what holds him, his side by his slot and the detour's half.
            float side = ((i + (int)(Tick / Math.Max(1u, (uint)Ticks(Rules.DetourSeconds)))) & 1) == 0 ? 1f : -1f;
            var d = v / v.Length();
            v = (d * 0.5f + new Vector2(d.Y, -d.X) * side * 0.866f) * Rules.Speed;
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
        float wanted = v.Length() * Dt;
        v += push;
        float l = v.Length();
        if (l > Rules.Speed) v *= Rules.Speed / l;
        if (l > 1e-3f) Walk(ref _position[i], ref _node[i], v * Dt, prefer);
        if (going) _yaw[i] = YawOf(way);
        else if (holding)
        {
            var outward = new Vector2(p.X - _holdCentre[s].X, p.Y - _holdCentre[s].Y);
            if (outward.LengthSquared() > 1f) _yaw[i] = YawOf(outward);   // at his post he faces out
        }
        _state[i] = going ? GuardState.Moving : GuardState.Idle;

        // Stuck: wanting to walk and getting nowhere (a corner, a wall in his straight line) puts him on a detour.
        var moved = new Vector2(_position[i].X - p.X, _position[i].Y - p.Y).Length();
        if (going && wanted > 0.01f && moved < Rules.StuckFraction * wanted)
        {
            if (++_stuckTicks[i] >= Ticks(Rules.StuckSeconds)) { _stuckTicks[i] = 0; _detourTicks[i] = Ticks(Rules.DetourSeconds); _direct[i] = false; }
        }
        else _stuckTicks[i] = 0;
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
        bool turretFirst = man % 2 == 0;   // expansion S4: half the squad takes a Rokkit turret before the Orks (ours)
        if (turretFirst && ShootTurret(i)) return;
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
            if (Shielded is { } dome && dome(_position[i] + new Vector3(0f, 0f, Rules.EyeHeight), enemy.Position + new Vector3(0f, 0f, Rules.ChestHeight))) continue;   // expansion S4: no shot into a dome from outside
            Fire(i, id, enemy, distance, flat);
            return;
        }
        if (!turretFirst) ShootTurret(i);
    }

    /// <summary>Expansion S4: a shot at the nearest built Rokkit turret in range, sight and no dome between; whether one was fired.</summary>
    bool ShootTurret(int i)
    {
        if (Turrets is not { } find) return false;
        byte turret = find(_position[i], Rules.Range, out var centre);
        if (turret == 0) return false;
        var eye = _position[i] + new Vector3(0f, 0f, Rules.EyeHeight);
        if (Shielded is { } dome && dome(eye, centre)) return false;
        var ray = centre - eye;
        float length = ray.Length();
        if (Sight is not null && length > 1e-3f)
        {
            LastRays++;
            if (Sight.Weapons.Raycast(eye, ray / length, length) is { } wall && wall.Distance < length - 0.1f) return false;
        }
        var flat = new Vector2(ray.X, ray.Y);
        float distance = flat.Length();
        if (flat.LengthSquared() > 1e-6f) _yaw[i] = YawOf(flat);
        float span = MathF.Max(1e-3f, Rules.Range - Rules.HitNearDistance);
        float chance = Rules.HitNear + (Rules.HitFar - Rules.HitNear) * Math.Clamp((distance - Rules.HitNearDistance) / span, 0f, 1f);
        bool hit = MusouSim.Unit(MusouSim.Mix(Tick, (uint)i * 0x85EBCA6Bu + 0xC0FFEEu + turret)) < chance;
        Shots++;
        if (!hit) { AddEvent(new GuardEvent(GuardEventKind.Shot, Tick, (ushort)(i + 1), 0, centre + new Vector3(Forward(_yaw[i] + MathF.PI / 2f), 0f))); return true; }
        ShotsHit++;
        AddEvent(new GuardEvent(GuardEventKind.Shot, Tick, (ushort)(i + 1), 0, centre));
        TurretHit?.Invoke(turret, Rules.Damage);
        return true;
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
        _enemies.Damage(id, 0, Rules.Damage * _strength[i / SquadSize], new Vector3(direction, 0f), out _);
    }

    void AddEvent(GuardEvent e)
    {
        if (_events.Count < MaxEventsPerTick) _events.Add(e);
    }
}
