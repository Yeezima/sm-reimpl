using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The numbers of a battle, the Stormbird's reserves and the extraction (design sections D and F, every value ours or an estimate, spec "the battle and the run"). A change here raises
/// <see cref="TfwRules.Version"/>. Distances in metres, times in real seconds unless named otherwise.
/// </summary>
public static class TfwBattleRules
{
    // The chain of positions.
    public const float ClearingRadius = 30f;
    public const float SecureSeconds = 25f;
    /// <summary>The share of a position's progress lost a second while its squad is wiped.</summary>
    public const float SecureDecayPerSecond = 0.02f;
    public const float ReserveSeconds = 15f;

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

/// <summary>The places of one battle: the map's layout, x mirrored on even nodes and turned 90 degrees on lane C (design section B).</summary>
public sealed record TfwLayout(Vector3 LandingZone, Vector3[] Positions, Vector3 Hq, Vector3[] Sources)
{
    /// <summary>The layout of <paramref name="node"/> (0 to 3) on <paramref name="lane"/>.</summary>
    public static TfwLayout For(TfwLane lane, int node)
    {
        bool mirror = node % 2 == 0;
        Vector3 Place(Vector3 p)
        {
            if (mirror) p.X = -p.X;
            return lane == TfwLane.C ? new Vector3(-p.Y, p.X, p.Z) : p;
        }
        return new TfwLayout(Place(TfwMap.LandingZone), [Place(TfwMap.Position1), Place(TfwMap.Position2), Place(TfwMap.Position3)], Place(TfwMap.Hq), TfwMap.Sources.Select(Place).ToArray());
    }
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
/// One battle (design section D), or the final assault: the chain of positions, their leashed defenders, the arrivals from the sources, the securing by a Guard squad, the commander, and
/// the failsafe for enemies nobody can reach. Works on the enemies' pool and the Guardsmen alone, deterministically; the run (<see cref="TfwRun"/>) owns everything around it. A position
/// goes Locked, Held, Clearing (a player within <see cref="TfwBattleRules.ClearingRadius"/>), Securing (its defenders dead and no Ork inside), Secured.
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
    int _reserves, _arrivalTicks, _released, _secureSquad = -1, _wipedTicks, _summons, _waaaghs, _next;

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
    /// <summary>The positions secured so far.</summary>
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
    public Vector3? ActivePosition => Won ? null : _next < Positions ? _layout.Positions[_next] : _layout.Hq;

    /// <summary>Whether source <paramref name="index"/> still sends enemies: until its position is secured, the HQ's until victory; every source in the final assault.</summary>
    public bool SourceActive(int index) => !Won && (_final || index >= Positions || _objectives[index].State != TfwPositionState.Secured);

    /// <summary>The start: the defenders at their posts, the field's squads at the landing zone and, in the final assault, the Warboss.</summary>
    public void Start()
    {
        for (int k = 0; k < Positions; k++) _objectives[k] = new TfwObjective((byte)k, _final ? TfwPositionState.Secured : k == 0 ? TfwPositionState.Held : TfwPositionState.Locked, _final ? 1f : 0f, _layout.Positions[k]);
        _objectives[CommanderObjective] = new TfwObjective(CommanderObjective, TfwPositionState.Locked, 0f, _layout.Hq);
        int field = Math.Min(Squads, _final ? TfwBattleRules.FinalFieldSquads : TfwBattleRules.FieldSquads);
        _reserves = Squads - field;
        for (int s = 0; s < field; s++) _guards.SpawnSquad(_layout.LandingZone + SquadOffset(s), YawTo(_layout.LandingZone, _layout.Hq));
        if (_final)
        {
            _next = Positions;
            Secured = Positions;
            SpawnBoss(MusouEnemyType.Warboss, _enemies.Rules.BossHealth * _scale * _bonus.BossHealthFactor, leashed: false);
            Advance(_layout.Hq);
            return;
        }
        for (int k = 0; k < Positions; k++) SpawnDefenders(k);
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
            var at = centre + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
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
            _enemies.SpawnLeashed(type, _layout.Hq + new Vector3(MathF.Cos(angle) * 3f, MathF.Sin(angle) * 3f, 0f), angle, Health(type), CommanderGroup, TfwBattleRules.DefenderAggro);
        }
        Advance(_layout.Hq);
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

    /// <summary>One tick, before the fight: <paramref name="players"/> are the feet of the living players, AI Marines included.</summary>
    public TfwBattleChange Step(ReadOnlySpan<Vector3> players)
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
        if (_next < Positions) change |= StepPosition(_next, players);
        StepArrivals();
        return change;
    }

    TfwBattleChange StepPosition(int k, ReadOnlySpan<Vector3> players)
    {
        var o = _objectives[k];
        switch (o.State)
        {
            case TfwPositionState.Held:
                float r2 = TfwBattleRules.ClearingRadius * TfwBattleRules.ClearingRadius;
                foreach (var p in players)
                    if (new Vector2(p.X - o.Position.X, p.Y - o.Position.Y).LengthSquared() <= r2) { o = o with { State = TfwPositionState.Clearing }; break; }
                break;
            case TfwPositionState.Clearing:
                if (_defenders[k].Count == 0 && !OrksInside(o.Position))
                {
                    o = o with { State = TfwPositionState.Securing };
                    _wipedTicks = 0;
                    AssignSquad(o.Position);
                }
                break;
            case TfwPositionState.Securing:
                o = o with { Progress = Securing(o) };
                if (o.Progress >= 1f)
                {
                    _objectives[k] = o with { State = TfwPositionState.Secured, Progress = 1f };
                    return Secure(k);
                }
                break;
        }
        _objectives[k] = o;
        return TfwBattleChange.None;
    }

    bool OrksInside(Vector3 at)
    {
        Span<ushort> one = stackalloc ushort[1];
        return _enemies.Query(at, TfwMap.CaptureRadius, one) > 0;
    }

    /// <summary>One tick of a position's securing: a Guardsman inside and no Ork moves it, an Ork pauses it, a wiped squad lets it decay until the reserve is sent.</summary>
    float Securing(in TfwObjective o)
    {
        bool inside = _guards.CountInside(o.Position, TfwMap.CaptureRadius) > 0;
        if (inside || _guards.AliveIn(_secureSquad) > 0)
        {
            _wipedTicks = 0;
            return inside && !OrksInside(o.Position) ? o.Progress + Dt / TfwBattleRules.SecureSeconds : o.Progress;
        }
        if (++_wipedTicks >= Ticks(TfwBattleRules.ReserveSeconds))
        {
            _wipedTicks = 0;
            if (_reserves > 0 && _guards.SpawnSquad(_layout.LandingZone, YawTo(_layout.LandingZone, o.Position)) >= 0) _reserves--;
            AssignSquad(o.Position);
        }
        return MathF.Max(0f, o.Progress - TfwBattleRules.SecureDecayPerSecond * Dt);
    }

    void AssignSquad(Vector3 position)
    {
        _secureSquad = _guards.NearestSquad(position);
        if (_secureSquad >= 0) _guards.Order(_secureSquad, position);
    }

    TfwBattleChange Secure(int k)
    {
        Secured = k + 1;
        _next = k + 1;
        _secureSquad = -1;
        var change = TfwBattleChange.PositionSecured | (k == 1 ? TfwBattleChange.DepotSecured : TfwBattleChange.None);
        for (int field = _guards.SquadsAlive; field < TfwBattleRules.FieldSquads && _reserves > 0; field++)
            if (_guards.SpawnSquad(_layout.LandingZone + SquadOffset(field), YawTo(_layout.LandingZone, _layout.Positions[k])) >= 0) _reserves--;
        if (_next < Positions)
        {
            _objectives[_next] = _objectives[_next] with { State = TfwPositionState.Held };
            Advance(_layout.Positions[k]);
            return change;
        }
        SpawnCommander();
        return change | TfwBattleChange.CommanderCame;
    }

    /// <summary>Every living squad moves up around <paramref name="to"/>.</summary>
    void Advance(Vector3 to)
    {
        for (int s = 0; s < GuardSim.MaxSquads; s++)
            if (_guards.AliveIn(s) > 0) _guards.Order(s, to + SquadOffset(s));
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
        return _enemies.Spawn(type, at, YawTo(at, _layout.Positions[Math.Min(_next, Positions - 1)]), Health(type)) != 0;
    }

    int _spot;

    /// <summary>The next spot of a 5 x 5 grid around a source, 1.5 m apart.</summary>
    Vector3 SourceSpot(int source)
    {
        int spot = _spot++ % 25;
        return _layout.Sources[source] + new Vector3((spot % 5 - 2) * 1.5f, (spot / 5 - 2) * 1.5f, 0f);
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
