using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The numbers of a battle, the Stormbird's reserves and the extraction (design sections D and F, every value ours or an estimate, spec "the battle and the run"). A change here raises
/// <see cref="TfwRules.Version"/>. Distances in metres, times in real seconds unless named otherwise.
/// </summary>
public static class TfwBattleRules
{
    // The positions (all three live from the landing, M2).
    public const float SecureSeconds = 25f;
    /// <summary>The share of a position's progress lost a second with no Imperial on it.</summary>
    public const float SecureDecayPerSecond = 0.02f;
    /// <summary>A field short of squads gets the next reserve after this long.</summary>
    public const float ReserveSeconds = 15f;
    /// <summary>A human on a position with no Guardsman there secures it at this share of the rate; an AI Marine holds it up but does not secure it (ours: a position never dead-ends).</summary>
    public const float HumanSecureRate = 0.5f;
    /// <summary>The squads' orders are looked at every this many seconds.</summary>
    public const float SquadOrderSeconds = 1f;
    /// <summary>A squad stands on a point at most this share of the point's radius from its centre (the squads must stand inside to take it).</summary>
    public const float SquadOnPoint = 0.7f;

    // The defenders of one position.
    public const int DefenderBoyz = 12, DefenderShootas = 5, DefenderArds = 3, Defenders = DefenderBoyz + DefenderShootas + DefenderArds;
    /// <summary>A leashed defender wakes when a target comes this near its home (<see cref="MusouSim.SpawnLeashed"/>).</summary>
    public const float DefenderAggro = 30f;

    // The arrivals.
    public const float ArrivalSeconds = 15f;
    public const int ArrivalsMin = 2, ArrivalsMax = 10;
    public const float ArrivalsPerHundredOrks = 4f;
    public const int AliveCapSolo = 140, AliveCapPerExtraHuman = 40;
    /// <summary>Of every ten arrivals, this many are Boyz; the rest Shootas (ours).</summary>
    public const int ArrivalBoyzInTen = 7;
    public const int ReleasesPerTick = 6;

    // The commander.
    public const float CommanderHealth = 1500f;
    public const int CommanderNobs = 1, CommanderArds = 5;

    // The Guardsmen of a battle.
    public const float ImperialPerSquad = 15f;
    public const int SquadsMin = 4, SquadsMax = 12, FieldSquads = 6, FinalFieldSquads = 12;

    // The Stormbird's reserves.
    public const int StartTransport = 5, StartSupplies = 4;
    public const int DeployCost = 1, PickupCost = 1, ExtractionSupplies = 2;
    public const int DepotTransport = 2, DepotSupplies = 1;
    public const float TravelSeconds = 5f, PickupSeconds = 20f, ExtractionPromptSeconds = 3f, ReviveWindowSeconds = 25f;
    /// <summary>War seconds between two transports from the HQ (10 war-minutes).</summary>
    public const double HqTransportSeconds = 600.0;

    /// <summary>The seconds a held respawn waits: re-armed every tick, so the core's timer never runs out (as Musou's hold).</summary>
    public const float RespawnHold = 0.1f;

    /// <summary>Enemies a source sends at Ork strength <paramref name="ork"/>.</summary>
    public static int ArrivalsAt(double ork) => Math.Clamp((int)Math.Round(ArrivalsPerHundredOrks * ork / 100.0, MidpointRounding.AwayFromZero), ArrivalsMin, ArrivalsMax);

    /// <summary>Most enemies alive with <paramref name="humans"/> humans in the battle.</summary>
    public static int AliveCap(int humans) => AliveCapSolo + AliveCapPerExtraHuman * Math.Max(0, humans - 1);

    /// <summary>The Guard squads of a battle at Imperial strength <paramref name="imperial"/>.</summary>
    public static int SquadsAt(double imperial) => Math.Clamp((int)Math.Round(imperial / ImperialPerSquad, MidpointRounding.AwayFromZero), SquadsMin, SquadsMax);
}

/// <summary>The places of one battle (<see cref="TfwSites.Layout"/>): each position's radius and half height are the map's point's (without them, the defaults below).</summary>
public sealed record TfwLayout(Vector3 LandingZone, Vector3[] Positions, Vector3 Hq, Vector3[] Sources, float[]? Radii = null, float[]? HalfHeights = null)
{
    /// <summary>A position's radius and half height when the layout names none (the spec's "Musou Mode's map"; the map's markers give the real ones).</summary>
    public const float DefaultRadius = 12f, DefaultHalfHeight = 2.5f;

    /// <summary>The radius of position <paramref name="k"/>.</summary>
    public float Radius(int k) => Radii is { } r && k < r.Length ? r[k] : DefaultRadius;

    /// <summary>How far above or below position <paramref name="k"/> an Ork still holds it (points differ in height; never less than a storey's <see cref="DefaultHalfHeight"/>).</summary>
    public float Reach(int k) => MathF.Max(DefaultHalfHeight, HalfHeights is { } h && k < h.Length ? h[k] : 0f);
}

/// <summary>What one <see cref="TfwBattle.Step"/> changed that the run acts on.</summary>
[Flags]
public enum TfwBattleChange : byte
{
    None = 0,
    PositionSecured = 1,
    /// <summary>The supply depot (position 2) was secured: its transport and supplies.</summary>
    DepotSecured = 2,
    CommanderCame = 4,
    Won = 8,
}

/// <summary>
/// One battle (design sections D and I), or the final assault: the three positions, their leashed defenders, the arrivals from the sources, the Guard squads, each going for the nearest position not
/// secured, the commander, and the failsafe for enemies nobody can reach. Works on the enemies' pool and the Guardsmen alone, deterministically; the run (<see cref="TfwRun"/>) owns everything around it. A
/// position is Held with no Imperial on it, Clearing with Imperials and Orks on it, Securing with Imperials alone, Secured when that has run its time. A secured position keeps one squad as long as two are free.
/// </summary>
public sealed class TfwBattle
{
    /// <summary>The fourth entry of <see cref="Objectives"/>: the commander, or the Warboss, at the HQ.</summary>
    public const int CommanderObjective = 3;
    const int Positions = WarRules.Positions, CommanderGroup = Positions + 1;

    readonly MusouSim _enemies;
    readonly GuardSim _guards;
    readonly TfwLayout _layout;
    readonly TfwObjective[] _objectives = new TfwObjective[Positions + 1];
    readonly List<ushort>[] _defenders = [new(), new(), new()];
    readonly List<ushort> _stranded = new();
    readonly bool _final;
    readonly WarFinalBonus _bonus;
    readonly int _humans, _arrivalsEach;
    readonly float _scale;
    readonly int[] _holder = [-1, -1, -1];                  // the squad holding a secured position
    readonly int[] _squadGoal = new int[GuardSim.MaxSquads];   // the objective a squad was sent to (-1: none)
    int _reserves, _arrivalTicks, _released, _reserveTicks, _squadTicks, _summons, _waaaghs;
    bool _commander, _depotPaid;

    public TfwBattle(MusouSim enemies, GuardSim guards, TfwLayout layout, double ork, double imperial, int humans, bool final = false, WarFinalBonus bonus = default)
    {
        _enemies = enemies;
        _guards = guards;
        _layout = layout;
        _final = final;
        _bonus = bonus;
        _humans = Math.Max(1, humans);
        _arrivalsEach = TfwBattleRules.ArrivalsAt(ork);
        _scale = 1f + enemies.Rules.ScalePerExtraPlayer * (_humans - 1);
        Squads = TfwBattleRules.SquadsAt(imperial) + (final ? bonus.ExtraSquads : 0);
        Array.Fill(_squadGoal, -1);
    }

    float Dt => 1f / Math.Max(1, _enemies.TickRate);
    int Ticks(float seconds) => Math.Max(1, (int)MathF.Round(seconds * Math.Max(1, _enemies.TickRate)));
    float Health(MusouEnemyType type) => _enemies.Rules.Of(type).Health;

    /// <summary>The Guard squads this battle has in all.</summary>
    public int Squads { get; }
    /// <summary>Whether the commander (the Warboss in the final assault) is dead.</summary>
    public bool Won { get; private set; }
    /// <summary>KOs of this battle by anyone (the failsafe's removals do not count).</summary>
    public int OrksKilled { get; private set; }
    /// <summary>The enemy the boss bar shows (0: none).</summary>
    public ushort BossId { get; private set; }
    public float BossMaxHealth { get; private set; }
    /// <summary>The positions secured now.</summary>
    public int Secured { get; private set; }
    /// <summary>Arrivals released from the sources so far (the Waaagh!s' Boyz not counted).</summary>
    public int Arrivals => _released;
    /// <summary>Squads still in reserve.</summary>
    public int Reserves => _reserves;
    /// <summary>The places of this battle.</summary>
    public TfwLayout Layout => _layout;
    /// <summary>The objectives as tag 8 carries them: the three positions, then the HQ.</summary>
    public IReadOnlyList<TfwObjective> Objectives => _objectives;

    /// <summary>Where the fight is: the first position not secured, then the HQ; null once won (<see cref="TfwRun.ActivePosition"/>).</summary>
    public Vector3? ActivePosition => Won ? null : FirstOpen() is var k and >= 0 ? _layout.Positions[k] : _layout.Hq;

    int FirstOpen()
    {
        for (int k = 0; k < Positions; k++) if (_objectives[k].State != TfwPositionState.Secured) return k;
        return -1;
    }

    /// <summary>The nearest place from <paramref name="at"/> that needs taking: the nearest position not secured, then the HQ; null once won.</summary>
    public Vector3? GoalFrom(Vector3 at) => Won ? null : Nearest(at) is var k and >= 0 ? _layout.Positions[k] : _layout.Hq;

    int Nearest(Vector3 at)
    {
        int best = -1;
        float bestD = float.PositiveInfinity;
        for (int k = 0; k < Positions; k++)
        {
            if (_objectives[k].State == TfwPositionState.Secured) continue;
            float d = Vector3.DistanceSquared(_layout.Positions[k], at);
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    /// <summary>Whether source <paramref name="index"/> still sends enemies: until its position is secured, the HQ's until victory; every source in the final assault.</summary>
    public bool SourceActive(int index) => !Won && (_final || index >= Positions || _objectives[index].State != TfwPositionState.Secured);

    /// <summary>The start: the defenders at their posts, the field's squads at the landing zone and, in the final assault, the Warboss.</summary>
    public void Start()
    {
        for (int k = 0; k < Positions; k++) _objectives[k] = new TfwObjective((byte)k, _final ? TfwPositionState.Secured : TfwPositionState.Held, _final ? 1f : 0f, _layout.Positions[k], _layout.Radius(k));
        _objectives[CommanderObjective] = new TfwObjective(CommanderObjective, TfwPositionState.Locked, 0f, _layout.Hq, TfwMap.HqRadius);
        int field = Math.Min(Squads, _final ? TfwBattleRules.FinalFieldSquads : TfwBattleRules.FieldSquads);
        _reserves = Squads - field;
        for (int s = 0; s < field; s++) _guards.SpawnSquad(Ground(_layout.LandingZone + SquadOffset(s), _layout.LandingZone), YawTo(_layout.LandingZone, _layout.Hq));
        if (_final)
        {
            Secured = Positions;
            _commander = true;
            SpawnBoss(MusouEnemyType.Warboss, _enemies.Rules.BossHealth * _scale * _bonus.BossHealthFactor, leashed: false);
            return;
        }
        for (int k = 0; k < Positions; k++) SpawnDefenders(k);
    }

    /// <summary><paramref name="spot"/> on the graph's ground; <paramref name="anchor"/> when it fell in a wall or off the graph (without a graph, as it is). Every place round a site passes here: the real map's sites have walls and heights near.</summary>
    Vector3 Ground(Vector3 spot, Vector3 anchor)
    {
        if (_enemies.Nav is not { } nav) return spot;
        int node = nav.Nearest(spot, 1.5f, 2.5f);
        return node >= 0 ? nav.Position(node) : anchor;
    }

    static Vector3 SquadOffset(int s)
    {
        float angle = s * 2.399963f, radius = 6f + 1.5f * s;
        return new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
    }

    static float YawTo(Vector3 from, Vector3 to)
    {
        var way = new Vector2(to.X - from.X, to.Y - from.Y);
        return way.LengthSquared() > 1e-6f ? MathF.Atan2(-way.X, way.Y) : 0f;
    }

    void SpawnDefenders(int position)
    {
        var centre = _layout.Positions[position];
        for (int k = 0; k < TfwBattleRules.Defenders; k++)
        {
            var type = k < TfwBattleRules.DefenderBoyz ? MusouEnemyType.Boy : k < TfwBattleRules.DefenderBoyz + TfwBattleRules.DefenderShootas ? MusouEnemyType.ShootaBoy : MusouEnemyType.ArdBoy;
            float angle = k * 2.399963f, radius = 1.5f + 0.5f * MathF.Sqrt(k) * 2f;
            var at = Ground(centre + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f), centre);
            ushort id = _enemies.SpawnLeashed(type, at, angle, Health(type), position + 1, TfwBattleRules.DefenderAggro);
            if (id != 0) _defenders[position].Add(id);
        }
    }

    void SpawnBoss(MusouEnemyType type, float health, bool leashed)
    {
        var hq = _layout.Hq;
        float yaw = YawTo(hq, _layout.LandingZone);
        BossId = leashed ? _enemies.SpawnLeashed(type, hq, yaw, health, CommanderGroup, TfwBattleRules.DefenderAggro) : _enemies.Spawn(type, hq, yaw, health);
        BossMaxHealth = health;
        _objectives[CommanderObjective] = _objectives[CommanderObjective] with { State = TfwPositionState.Held };
    }

    void SpawnCommander()
    {
        SpawnBoss(MusouEnemyType.Nob, TfwBattleRules.CommanderHealth * _scale, leashed: true);
        int escorts = TfwBattleRules.CommanderNobs + TfwBattleRules.CommanderArds;
        for (int k = 0; k < escorts; k++)
        {
            var type = k < TfwBattleRules.CommanderNobs ? MusouEnemyType.Nob : MusouEnemyType.ArdBoy;
            float angle = k * MathF.Tau / escorts;
            _enemies.SpawnLeashed(type, Ground(_layout.Hq + new Vector3(MathF.Cos(angle) * 3f, MathF.Sin(angle) * 3f, 0f), _layout.Hq), angle, Health(type), CommanderGroup, TfwBattleRules.DefenderAggro);
        }
        _commander = true;
    }

    /// <summary>A KO, from the World's <see cref="MusouSim.TakeKills"/> (a Guardsman's KO names no player, so the players' scores would miss it).</summary>
    public void NoteKill(MusouKill kill)
    {
        if (kill.Cause != MusouDeathKind.World) OrksKilled++;
    }

    /// <summary>The boss's Waaagh!s so far: its Boyz come from the sources (no director in a war).</summary>
    public void NoteWaaaghs(int waaaghs)
    {
        if (waaaghs <= _waaaghs) return;
        _summons += (waaaghs - _waaaghs) * _enemies.Rules.Boss.WaaaghBoyz;
        _waaaghs = waaaghs;
    }

    /// <summary>One tick, before the fight: <paramref name="players"/> are the feet of the living players, AI Marines included; <paramref name="humans"/> those of the living humans in the field.</summary>
    public TfwBattleChange Step(ReadOnlySpan<Vector3> players, ReadOnlySpan<Vector3> humans = default)
    {
        var change = TfwBattleChange.None;
        Unstrand();
        foreach (var defenders in _defenders) defenders.RemoveAll(id => !_enemies.TryGet(id, out var e) || !e.Alive);   // each is seen before its slot can be reused (the dying keep it for seconds)
        if (Won) return change;
        if (BossId != 0)
        {
            if (!_enemies.TryGet(BossId, out var boss) || !boss.Alive)
            {
                Won = true;
                BossId = 0;
                _objectives[CommanderObjective] = _objectives[CommanderObjective] with { State = TfwPositionState.Secured, Progress = 1f };
                return change | TfwBattleChange.Won;
            }
            _objectives[CommanderObjective] = _objectives[CommanderObjective] with { Progress = 1f - boss.Health / MathF.Max(1f, BossMaxHealth) };
        }
        if (!_final) for (int k = 0; k < Positions; k++) change |= StepPosition(k, players, humans);
        StepSquads();
        StepReserves();
        StepArrivals();
        return change;
    }

    /// <summary>One tick of a position: who stands on it says what it does (the type's summary).</summary>
    TfwBattleChange StepPosition(int k, ReadOnlySpan<Vector3> players, ReadOnlySpan<Vector3> humans)
    {
        var o = _objectives[k];
        if (o.State == TfwPositionState.Secured) return TfwBattleChange.None;
        bool guards = _guards.CountInside(o.Position, _layout.Radius(k)) > 0, imperial = guards || AnyInside(k, players);
        if (!imperial) o = o with { State = TfwPositionState.Held, Progress = MathF.Max(0f, o.Progress - TfwBattleRules.SecureDecayPerSecond * Dt) };
        else if (OrksInside(k)) o = o with { State = TfwPositionState.Clearing };
        else
        {
            float rate = guards ? 1f : AnyInside(k, humans) ? TfwBattleRules.HumanSecureRate : 0f;
            o = o with { State = TfwPositionState.Securing, Progress = o.Progress + rate * Dt / TfwBattleRules.SecureSeconds };
        }
        if (o.Progress < 1f) { _objectives[k] = o; return TfwBattleChange.None; }
        _objectives[k] = o with { State = TfwPositionState.Secured, Progress = 1f };
        return Secure(k);
    }

    /// <summary>Whether any of <paramref name="feet"/> stands on position <paramref name="k"/>.</summary>
    bool AnyInside(int k, ReadOnlySpan<Vector3> feet)
    {
        var at = _layout.Positions[k];
        float r2 = _layout.Radius(k) * _layout.Radius(k), reach = _layout.Reach(k);
        foreach (var p in feet)
            if (MathF.Abs(p.Z - at.Z) <= reach && new Vector2(p.X - at.X, p.Y - at.Y).LengthSquared() <= r2) return true;
        return false;
    }

    bool OrksInside(int k)
    {
        Span<ushort> one = stackalloc ushort[1];
        return _enemies.Query(_layout.Positions[k], _layout.Radius(k), one, _layout.Reach(k)) > 0;
    }

    TfwBattleChange Secure(int k)
    {
        Secured++;
        var change = TfwBattleChange.PositionSecured;
        if (k == 1 && !_depotPaid) { _depotPaid = true; change |= TfwBattleChange.DepotSecured; }
        for (int field = _guards.SquadsAlive; field < TfwBattleRules.FieldSquads && _reserves > 0; field++)
            if (_guards.SpawnSquad(Ground(_layout.LandingZone + SquadOffset(field), _layout.LandingZone), YawTo(_layout.LandingZone, _layout.Positions[k])) >= 0) _reserves--;
        if (Secured < Positions || _commander) return change;
        SpawnCommander();
        return change | TfwBattleChange.CommanderCame;
    }

    /// <summary>
    /// The squads' orders, once a second: a secured position without a squad keeps the nearest one while two or more are free (it cannot be left empty for nothing); every other squad goes for the
    /// nearest position not secured, then the HQ. A squad is sent again when its goal changed, or when it stopped outside it.
    /// </summary>
    void StepSquads()
    {
        if (--_squadTicks > 0) return;
        _squadTicks = Ticks(TfwBattleRules.SquadOrderSeconds);
        int free = 0;
        for (int s = 0; s < GuardSim.MaxSquads; s++) if (_guards.AliveIn(s) > 0) free++;
        for (int k = 0; k < Positions; k++)
        {
            if (_holder[k] >= 0 && (_guards.AliveIn(_holder[k]) == 0 || _objectives[k].State != TfwPositionState.Secured)) _holder[k] = -1;
            if (_holder[k] >= 0) free--;
        }
        for (int k = 0; k < Positions && !_final; k++)
        {
            if (_objectives[k].State != TfwPositionState.Secured || _holder[k] >= 0 || free < 2) continue;
            int squad = NearestFree(_layout.Positions[k]);
            if (squad < 0) break;
            _holder[k] = squad;
            free--;
            Send(squad, k);
        }
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_guards.AliveIn(s) == 0) { _squadGoal[s] = -1; continue; }
            if (Holds(s)) continue;
            int goal = Nearest(_guards.Centre(s));
            if (goal < 0) { if (!_commander) continue; goal = CommanderObjective; }
            if (goal != _squadGoal[s] || (_guards.Arrived(s) && !OnGoal(s, goal))) Send(s, goal);
        }
    }

    bool Holds(int squad)
    {
        foreach (int s in _holder) if (s == squad) return true;
        return false;
    }

    int NearestFree(Vector3 at)
    {
        int best = -1;
        float bestD = float.PositiveInfinity;
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_guards.AliveIn(s) == 0 || Holds(s)) continue;
            float d = Vector3.DistanceSquared(_guards.Centre(s), at);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    Vector3 PlaceOf(int goal) => goal == CommanderObjective ? _layout.Hq : _layout.Positions[goal];

    float RadiusOf(int goal) => goal == CommanderObjective ? TfwMap.HqRadius : _layout.Radius(goal);

    bool OnGoal(int squad, int goal) => new Vector2(_guards.Centre(squad).X - PlaceOf(goal).X, _guards.Centre(squad).Y - PlaceOf(goal).Y).Length() <= RadiusOf(goal);

    /// <summary>Squad <paramref name="s"/> goes to its own place on an objective: all by the objective's way, each to its own spot inside the radius.</summary>
    void Send(int s, int goal)
    {
        _squadGoal[s] = goal;
        var at = PlaceOf(goal);
        var offset = SquadOffset(s);
        float most = RadiusOf(goal) * TfwBattleRules.SquadOnPoint;
        if (offset.Length() > most) offset *= most / offset.Length();
        _guards.Order(s, Ground(at + offset, at), at);
    }

    /// <summary>A field short of squads gets the next reserve every <see cref="TfwBattleRules.ReserveSeconds"/>, from the landing zone.</summary>
    void StepReserves()
    {
        if (_reserves == 0 || _guards.SquadsAlive >= Math.Min(Squads, _final ? TfwBattleRules.FinalFieldSquads : TfwBattleRules.FieldSquads)) { _reserveTicks = 0; return; }
        if (++_reserveTicks < Ticks(TfwBattleRules.ReserveSeconds)) return;
        _reserveTicks = 0;
        if (_guards.SpawnSquad(_layout.LandingZone, YawTo(_layout.LandingZone, _layout.Hq)) >= 0) _reserves--;
    }

    void StepArrivals()
    {
        int cap = TfwBattleRules.AliveCap(_humans);
        if (++_arrivalTicks >= Ticks(TfwBattleRules.ArrivalSeconds))
        {
            _arrivalTicks = 0;
            for (int s = 0; s < _layout.Sources.Length; s++)
                for (int k = 0; k < _arrivalsEach && SourceActive(s) && _enemies.Alive < cap; k++)
                {
                    var type = _released % 10 < TfwBattleRules.ArrivalBoyzInTen ? MusouEnemyType.Boy : MusouEnemyType.ShootaBoy;
                    if (SpawnAtSource(s, type)) _released++;
                }
        }
        for (int k = 0; k < TfwBattleRules.ReleasesPerTick && _summons > 0 && _enemies.Alive < cap; k++)
        {
            _summons--;
            SpawnAtSource(_summons % _layout.Sources.Length, MusouEnemyType.Boy);
        }
    }

    bool SpawnAtSource(int source, MusouEnemyType type)
    {
        var at = SourceSpot(source);
        return _enemies.Spawn(type, at, YawTo(at, _layout.LandingZone), Health(type)) != 0;
    }

    int _spot;

    /// <summary>The next spot of a 5 x 5 grid around a source, 1.5 m apart.</summary>
    Vector3 SourceSpot(int source)
    {
        int spot = _spot++ % 25;
        var at = _layout.Sources[source];
        return Ground(at + new Vector3((spot % 5 - 2) * 1.5f, (spot / 5 - 2) * 1.5f, 0f), at);
    }

    /// <summary>
    /// The war's failsafe (no director runs): an enemy without headway for <see cref="MusouRules.StrandedSeconds"/>, a woken defender with no reachable target among them, goes to a source
    /// still sending; one moved <see cref="MusouRules.StrandedRelocations"/> times already is removed, a KO for nobody. The boss is only ever moved.
    /// </summary>
    void Unstrand()
    {
        _stranded.Clear();
        _enemies.TakeStranded(_stranded);
        foreach (ushort id in _stranded)
        {
            if (!_enemies.TryGet(id, out var e) || !e.Alive) continue;
            if (id == BossId || _enemies.Relocations(id) < _enemies.Rules.StrandedRelocations)
            {
                int source = _layout.Sources.Length - 1;
                for (int s = 0; s < _layout.Sources.Length; s++) if (SourceActive(s)) { source = s; break; }
                _enemies.Relocate(id, SourceSpot(source));
            }
            else _enemies.Damage(id, 0, e.Health, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World);
        }
    }
}
