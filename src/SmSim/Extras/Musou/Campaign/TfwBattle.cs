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
    /// <summary>The Guardsmen of a battle: the owner, 2026-11-01, "Temporarily remove all guardsmen allies". False: no squad lands and players and AI Marines take the points at the full rate. True brings them back, nothing else to change.</summary>
    public const bool GuardsmenEnabled = false;
    /// <summary>What a secured position takes to fall: this long with Orks alone on it (a retake, and the way back up the same).</summary>
    public const float RetakeSeconds = 20f;
    /// <summary>A field short of squads gets the next reserve after this long.</summary>
    public const float ReserveSeconds = 15f;
    /// <summary>A human or an AI Marine on a position with no Guardsman there secures it at this share of the rate (ours: a position never dead-ends).</summary>
    public const float HumanSecureRate = 0.5f;
    /// <summary>The squads' orders are looked at every this many seconds.</summary>
    public const float SquadOrderSeconds = 1f;
    /// <summary>A squad stands on a point at most this share of the point's radius from its centre (the squads must stand inside to take it).</summary>
    public const float SquadOnPoint = 0.7f;

    /// <summary>A leashed Ork wakes when a target comes this near its home (<see cref="MusouSim.SpawnLeashed"/>), and is free for good: a garrison and the commander's group.</summary>
    public const float DefenderAggro = 30f;

    // The garrison of one position (owner, 2026-11-01: "there is always a consistent ork garrison waiting on each point that never respawns. they defend each point"): it stands leashed on the point from the landing and is never rebuilt.
    public const int Garrison = 12;

    // The crowds (owner, 2026-11-01): "the orks should pool up at their spawn point until they reach a crowd of 50 orks, then swarm at the player. there should be 3 major spawn points across the map doing this";
    // "pools replace the tide"; "50 per point, 150 total. Each point swarms on its own."
    /// <summary>The crowd of one spawn point: it stands there leashed, one more every <see cref="CrowdFillSeconds"/>, and at this many all of it swarms at once. The owner's number.</summary>
    public const int CrowdSize = 50;
    /// <summary>A crowd's Orks notice a target this near their own places (a crowd wakes as one when any of it does, or is hurt). Ours: a standoff further out than the point's ring lets the crowd grow.</summary>
    public const float CrowdAggro = 12f;

    /// <summary>
    /// A held point adds one Ork to its crowd every this many seconds (the owner's tide of 1 s, now the crowd's fill: 50 s for a crowd). The owner, 2026-11-01: "you have to fully capture a point to disable the spawning
    /// from that point. But it's temporarily paused while the point is being actively captured"; "Orks should begin spawning again immediately".
    /// </summary>
    public const float CrowdFillSeconds = 1f;
    /// <summary>A crowd and a garrison stand on a disc this wide round their point.</summary>
    public const float SpawnDisc = 8f;
    /// <summary>
    /// The owner, 2026-11-01: "50 per point, 150 total": the Orks of the crowds and the swarms alive at once, a garrison not counted (it never respawns, 12 a point); 80 more a further human; never over
    /// <see cref="AliveCapMost"/>, which leaves the pool's 512 slots room for the garrisons and those dying.
    /// </summary>
    public const int AliveCapSolo = 150, AliveCapPerExtraHuman = 80, AliveCapMost = 460;
    public const int ReleasesPerTick = 6;

    // What the Orks are made of (the design's section B; the owner's item 6). Of every 100 fodder and of every 100 elites:
    public const float EliteBase = 0.10f, EliteSlope = 0.50f;
    public const float FodderBoyz = 0.55f, FodderShootas = 0.30f, EliteArds = 0.75f;

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

    /// <summary>Most enemies alive with <paramref name="humans"/> humans in the battle.</summary>
    public static int AliveCap(int humans) => Math.Min(AliveCapMost, AliveCapSolo + AliveCapPerExtraHuman * Math.Max(0, humans - 1));

    /// <summary>The share of elites at <paramref name="progress"/> through the run (0 to 1): 10 % to 60 %.</summary>
    public static float EliteShare(float progress) => EliteBase + EliteSlope * Math.Clamp(progress, 0f, 1f);

    /// <summary>The run's progress: wins so far of the wins needed; the final assault is 1.</summary>
    public static float RunProgress(int wins, int needed, bool final) => final ? 1f : Math.Clamp(wins / (float)Math.Max(1, needed), 0f, 1f);

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

/// <summary>Why a crowd swarmed: it reached <see cref="TfwBattleRules.CrowdSize"/>; or a target came near it, or one of it was hurt, and it went with what it had.</summary>
public enum TfwSwarmWhy : byte { Full = 0, Woken = 1 }

/// <summary>A crowd let loose (<see cref="TfwBattle.OnSwarm"/>): the spawn point (0 to 2 the positions, 3 the HQ's source), how many Orks went, why, and who.</summary>
public readonly record struct TfwSwarm(int Point, int Size, TfwSwarmWhy Why, ushort[] Ids);

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
/// One battle (design sections D and I), or the final assault: the three positions, the crowd each spawn point pools and then swarms with (owner, 2026-11-01), the Guard squads, each going for the nearest position not secured, the commander
/// (he comes once each point has fallen, whatever has been retaken since, so a battle cannot hang on holding all three at once), and the failsafe for enemies nobody can reach. Works on the enemies' pool and the Guardsmen alone, deterministically; the run (<see cref="TfwRun"/>) owns everything around it. A
/// position is Held with no Imperial on it, Clearing with Imperials and Orks on it, Securing with Imperials alone, Secured when that has run its time; only Held sends Orks (a capture pauses it, a full one stops it, and the
/// tick a capture ends or a retake is done it sends again). A secured position with Orks alone on it falls back to Held in <see cref="TfwBattleRules.RetakeSeconds"/>. A secured position keeps one squad as long as two are free.
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
    readonly List<ushort>[] _crowds = [new(), new(), new(), new()];   // each spawn point's crowd: the three positions', then the HQ's source
    readonly List<ushort>[] _garrisons = [new(), new(), new()];       // each position's garrison still standing at its post
    readonly List<ushort> _stranded = new();
    readonly bool _final, _guardsmen;
    readonly WarFinalBonus _bonus;
    readonly int _humans;
    readonly float _scale;
    readonly int[] _tideTicks = new int[Positions + 1];   // ticks to the point's next Ork; 0: on this tick
    readonly int[] _lastTide = new int[Positions + 1];    // the battle's tick of its last
    int _now, _humansNow, _swarms;
    const int CrowdGroupFirst = Positions + 2;   // after the garrisons' (1 to 3) and the commander's (4): the positions' crowds, then the HQ source's
    readonly int[] _holder = [-1, -1, -1];                  // the squad holding a secured position
    readonly int[] _squadGoal = new int[GuardSim.MaxSquads];   // the objective a squad was sent to (-1: none)
    int _reserves, _released, _picked, _spots, _reserveTicks, _squadTicks, _summons, _waaaghs, _taken;
    bool _commander, _depotPaid;

    public TfwBattle(MusouSim enemies, GuardSim guards, TfwLayout layout, double imperial, int humans, bool final = false, WarFinalBonus bonus = default, float elite = TfwBattleRules.EliteBase, bool guardsmen = true)
    {
        EliteShare = elite;
        _guardsmen = guardsmen;
        _enemies = enemies;
        _guards = guards;
        _layout = layout;
        _final = final;
        _bonus = bonus;
        _humans = Math.Max(1, humans);
        _humansNow = _humans;
        _scale = 1f + enemies.Rules.ScalePerExtraPlayer * (_humans - 1);
        Squads = guardsmen ? TfwBattleRules.SquadsAt(imperial) + (final ? bonus.ExtraSquads : 0) : 0;
        Array.Fill(_squadGoal, -1);
    }

    float Dt => 1f / Math.Max(1, _enemies.TickRate);
    int Ticks(float seconds) => Math.Max(1, (int)MathF.Round(seconds * Math.Max(1, _enemies.TickRate)));
    float Health(MusouEnemyType type) => _enemies.Rules.Of(type).Health;

    /// <summary>The Guard squads this battle has in all.</summary>
    public int Squads { get; }
    /// <summary>The share of elites among the Orks this battle sends.</summary>
    public float EliteShare { get; }
    /// <summary>Whether the commander (the Warboss in the final assault) is dead.</summary>
    public bool Won { get; private set; }
    /// <summary>KOs of this battle by anyone (the failsafe's removals do not count).</summary>
    public int OrksKilled { get; private set; }
    /// <summary>The enemy the boss bar shows (0: none).</summary>
    public ushort BossId { get; private set; }
    public float BossMaxHealth { get; private set; }
    /// <summary>The positions secured now.</summary>
    public int Secured { get; private set; }
    /// <summary>Orks added to the crowds so far (the Waaagh!s' Boyz not counted).</summary>
    public int Arrivals => _released;
    /// <summary>Swarms let loose so far (a full crowd, a woken one, a stalled one).</summary>
    public int Swarms => _swarms;
    /// <summary>The Orks standing in spawn point <paramref name="point"/>'s crowd now (0 to 2 the positions, 3 the HQ's source).</summary>
    public int Pooled(int point) => _crowds[point].Count;
    /// <summary>The Orks of position <paramref name="position"/>'s garrison still standing at its post (never rebuilt: 12 at the landing).</summary>
    public int Garrisoned(int position) => _garrisons[position].Count;
    /// <summary>The ids standing in spawn point <paramref name="point"/>'s crowd now, and in position <paramref name="position"/>'s garrison (for tests).</summary>
    public ushort[] CrowdIds(int point) => _crowds[point].ToArray();
    public ushort[] GarrisonIds(int position) => _garrisons[position].ToArray();
    /// <summary>Test aid: called once for every swarm let loose, with the Orks that were in it.</summary>
    public Action<TfwSwarm>? OnSwarm { get; set; }
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

    /// <summary>Whether <paramref name="index"/> (a position, or 3 the HQ) sends Orks: a position only while Held, the HQ from the commander's coming until victory; all four in the final assault.</summary>
    public bool SourceActive(int index) => !Won && (_final || (index < Positions ? _objectives[index].State == TfwPositionState.Held : _commander));

    /// <summary>The secured positions as places for the Orks to retake (<see cref="MusouTarget.Goal"/>), written from <paramref name="into"/>'s start; the number written.</summary>
    public int AppendGoals(Span<MusouTarget> into)
    {
        int n = 0;
        for (int k = 0; k < Positions && !_final && n < into.Length; k++)
            if (_objectives[k].State == TfwPositionState.Secured) into[n++] = new MusouTarget(TfwContracts.PointTargetId(k), _layout.Positions[k], true, 0f, Goal: true);
        return n;
    }


    /// <summary>The start: the crowds part-grown at their points, the field's squads at the landing zone and, in the final assault, the Warboss.</summary>
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
            for (int k = 0; k <= Positions; k++) _tideTicks[k] = Ticks(TfwBattleRules.CrowdFillSeconds);
            SpawnBoss(MusouEnemyType.Warboss, _enemies.Rules.BossHealth * _scale * _bonus.BossHealthFactor, leashed: false);
            return;
        }
        for (int k = 0; k < Positions; k++)
        {
            for (int n = 0; n < TfwBattleRules.Garrison; n++) SpawnGarrison(k);   // once: a garrison is never rebuilt
            _tideTicks[k] = Ticks(TfwBattleRules.CrowdFillSeconds);
        }
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

    /// <summary>The next Ork of the mix: elites by <see cref="EliteShare"/>, each kind by its share, spread evenly (two low-discrepancy sequences, no dice).</summary>
    MusouEnemyType NextType()
    {
        int n = _picked++;
        double elite = n * 0.6180339887498949 % 1.0, kind = n * 0.7548776662466927 % 1.0;
        if (elite < EliteShare) return kind < TfwBattleRules.EliteArds ? MusouEnemyType.ArdBoy : MusouEnemyType.Nob;
        return kind < TfwBattleRules.FodderBoyz ? MusouEnemyType.Boy : kind < TfwBattleRules.FodderBoyz + TfwBattleRules.FodderShootas ? MusouEnemyType.ShootaBoy : MusouEnemyType.Gretchin;
    }

    /// <summary>The next spot of a disc round <paramref name="centre"/>, jittered, on the graph's ground (the centre when it fell in a wall).</summary>
    Vector3 DiscSpot(Vector3 centre)
    {
        int n = _spots++;
        float angle = n * 2.399963f, radius = TfwBattleRules.SpawnDisc * MathF.Sqrt(MusouSim.Unit(MusouSim.Mix((uint)n, 0x7469646Bu)));
        return Ground(centre + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f), centre);
    }

    /// <summary>The group a spawn point's crowd is leashed under (<see cref="MusouSim.ReleaseGroup"/> frees all of it), apart from the garrisons' and the commander's.</summary>
    static int CrowdGroup(int point) => CrowdGroupFirst + point;

    /// <summary>Where spawn point <paramref name="point"/>'s crowd stands: a position's own ground, the HQ's source.</summary>
    Vector3 CrowdAnchor(int point) => point < Positions ? _layout.Positions[point] : _layout.Sources[point];

    /// <summary>One Ork of position <paramref name="position"/>'s garrison: leashed on the point's disc, woken by a target within <see cref="TfwBattleRules.DefenderAggro"/>, never replaced.</summary>
    void SpawnGarrison(int position)
    {
        var type = NextType();
        var at = DiscSpot(_layout.Positions[position]);
        ushort id = _enemies.SpawnLeashed(type, at, YawTo(at, _layout.LandingZone), Health(type), position + 1, TfwBattleRules.DefenderAggro);
        if (id != 0) _garrisons[position].Add(id);
    }

    /// <summary>The Orks that count against the cap: all alive but the garrisons still standing at their posts (they never respawn; 12 a point).</summary>
    int Counted()
    {
        int garrison = 0;
        foreach (var g in _garrisons) garrison += g.Count;
        return _enemies.Alive - garrison;
    }

    /// <summary>One more Ork in spawn point <paramref name="point"/>'s crowd (leashed on its disc); false when the pool had no room. The crowd that reaches <see cref="TfwBattleRules.CrowdSize"/> swarms.</summary>
    bool AddToCrowd(int point)
    {
        var type = NextType();
        var at = DiscSpot(CrowdAnchor(point));
        ushort id = _enemies.SpawnLeashed(type, at, YawTo(at, _layout.LandingZone), Health(type), CrowdGroup(point), TfwBattleRules.CrowdAggro);
        if (id == 0) return false;
        _crowds[point].Add(id);
        if (_crowds[point].Count >= TfwBattleRules.CrowdSize) Swarm(point, TfwSwarmWhy.Full);
        return true;
    }

    /// <summary>Lets spawn point <paramref name="point"/>'s whole crowd go: every one of them is free for good and goes for the players; the point starts a new crowd. <paramref name="extra"/>: Orks of it already woken.</summary>
    void Swarm(int point, TfwSwarmWhy why, IReadOnlyList<ushort>? extra = null)
    {
        var crowd = _crowds[point];
        _enemies.ReleaseGroup(CrowdGroup(point));
        _swarms++;
        if (OnSwarm is { } watch)
        {
            var ids = new List<ushort>(crowd);
            if (extra is not null) ids.AddRange(extra);
            watch(new TfwSwarm(point, ids.Count, why, ids.ToArray()));
        }
        crowd.Clear();
    }

    /// <summary>
    /// Each tick: a crowd's Orks that died are forgotten, and a crowd with one awake (a target came near it, or one was hurt, which frees that one's group) is woken as a whole, so a crowd never trickles out.
    /// Each is seen before its slot can be reused (the dying keep it for seconds).
    /// </summary>
    void TidyCrowds()
    {
        foreach (var garrison in _garrisons) garrison.RemoveAll(id => !_enemies.TryGet(id, out var e) || !e.Alive || !_enemies.Leashed(id));   // the garrison is those still at their post
        for (int point = 0; point <= Positions; point++)
        {
            var crowd = _crowds[point];
            if (crowd.Count == 0) continue;
            List<ushort>? awake = null;
            for (int n = crowd.Count - 1; n >= 0; n--)
            {
                ushort id = crowd[n];
                bool alive = _enemies.TryGet(id, out var e) && e.Alive;
                if (alive && _enemies.Leashed(id)) continue;
                if (alive) (awake ??= new()).Add(id);
                crowd.RemoveAt(n);
            }
            if (awake is not null) Swarm(point, TfwSwarmWhy.Woken, awake);
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
        _tideTicks[Positions] = Ticks(TfwBattleRules.CrowdFillSeconds);
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

    /// <summary>One tick, before the fight: <paramref name="players"/> are the feet of the living players, AI Marines included; <paramref name="humans"/> the humans in the match now (a late joiner raises the cap; 0: as at the landing).</summary>
    public TfwBattleChange Step(ReadOnlySpan<Vector3> players, int humans = 0)
    {
        var change = TfwBattleChange.None;
        _now++;
        if (humans > 0) _humansNow = humans;
        Unstrand();
        TidyCrowds();
        _enemies.KeepOut.Clear();
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
        if (!_final)
        {
            for (int k = 0; k < Positions; k++) change |= StepPosition(k, players);
            // A position being taken is kept clear of shooters: they hold their band, which lies inside its ring (see MusouSim.KeepOut).
            for (int k = 0; k < Positions; k++)
                if (_objectives[k].State is TfwPositionState.Clearing or TfwPositionState.Securing) _enemies.KeepOut.Add((_layout.Positions[k], _layout.Radius(k), _layout.Reach(k)));
        }
        StepSquads();
        StepReserves();
        StepArrivals();
        return change;
    }

    /// <summary>One tick of a position: who stands on it says what it does (the type's summary).</summary>
    TfwBattleChange StepPosition(int k, ReadOnlySpan<Vector3> players)
    {
        var o = _objectives[k];
        bool guards = _guards.CountInside(o.Position, _layout.Radius(k)) > 0, imperial = guards || AnyInside(k, players), orks = OrksInside(k);
        if (o.State == TfwPositionState.Secured)
        {
            float change = Dt / TfwBattleRules.RetakeSeconds;
            float held = orks && !imperial ? o.Progress - change : MathF.Min(1f, o.Progress + change);
            if (held > 0f) { _objectives[k] = o with { Progress = held }; return TfwBattleChange.None; }
            _objectives[k] = o with { State = TfwPositionState.Held, Progress = 0f };
            Secured--;
            SendAgain(k);
            return TfwBattleChange.None;
        }
        if (!imperial)
        {
            if (o.State != TfwPositionState.Held) SendAgain(k);
            o = o with { State = TfwPositionState.Held, Progress = MathF.Max(0f, o.Progress - TfwBattleRules.SecureDecayPerSecond * Dt) };
        }
        else if (orks) o = o with { State = TfwPositionState.Clearing };
        else
        {
            float rate = guards || !_guardsmen ? 1f : TfwBattleRules.HumanSecureRate;
            o = o with { State = TfwPositionState.Securing, Progress = o.Progress + rate * Dt / TfwBattleRules.SecureSeconds };
        }
        if (o.Progress < 1f) { _objectives[k] = o; return TfwBattleChange.None; }
        _objectives[k] = o with { State = TfwPositionState.Secured, Progress = 1f };
        return Secure(k);
    }

    /// <summary>The owner: "Orks should begin spawning again immediately": the point's next Ork is due on this tick, if its interval has passed since the point last sent, so stepping on and off a point sends no faster than holding it.</summary>
    void SendAgain(int k) => _tideTicks[k] = Math.Max(0, _lastTide[k] + Ticks(TfwBattleRules.CrowdFillSeconds) - _now);

    /// <summary>Whether any of <paramref name="feet"/> stands on position <paramref name="k"/>.</summary>
    bool AnyInside(int k, ReadOnlySpan<Vector3> feet)
    {
        var at = _layout.Positions[k];
        float r2 = _layout.Radius(k) * _layout.Radius(k), reach = _layout.Reach(k);
        foreach (var p in feet)
            if (MathF.Abs(p.Z - at.Z) <= reach && new Vector2(p.X - at.X, p.Y - at.Y).LengthSquared() <= r2) return true;
        return false;
    }

    /// <summary>Test aid: the Orks (up to 64) inside position <paramref name="k"/>'s capture volume now.</summary>
    public int OrksOn(int k)
    {
        Span<ushort> ids = stackalloc ushort[64];
        return _enemies.Query(_layout.Positions[k], _layout.Radius(k), ids, _layout.Reach(k));
    }

    /// <summary>Test aid: what the Orks inside position <paramref name="k"/>'s capture volume are doing, for example "Seek 3, Knocked 7, leashed 2" (empty: none).</summary>
    public string OrksOnReport(int k)
    {
        var ids = new ushort[64];
        int n = _enemies.Query(_layout.Positions[k], _layout.Radius(k), ids, _layout.Reach(k));
        var counts = new SortedDictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            if (!_enemies.TryGet(ids[i], out var e)) continue;
            float dz = e.Position.Z - _layout.Positions[k].Z;
            string key = (_enemies.Leashed(ids[i]) ? "leashed" : e.State.ToString()) + (dz > 2.5f ? " above" : dz < -2.5f ? " below" : "");
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var sample = new List<string>();
        for (int i = 0; i < n && sample.Count < 3; i++)
            if (_enemies.TryGet(ids[i], out var e) && e.State == MusouEnemyState.Seek)
                sample.Add($"#{ids[i]} {e.Type} at {e.Position.X - _layout.Positions[k].X:F0},{e.Position.Y - _layout.Positions[k].Y:F0},{e.Position.Z - _layout.Positions[k].Z:F1} speed {e.Velocity.Length():F1} target {e.TargetPlayer} node {e.Node}");
        return string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")) + (sample.Count > 0 ? " e.g. " + string.Join("; ", sample) : "");
    }

    bool OrksInside(int k)
    {
        Span<ushort> one = stackalloc ushort[1];
        return _enemies.Query(_layout.Positions[k], _layout.Radius(k), one, _layout.Reach(k)) > 0;
    }

    TfwBattleChange Secure(int k)
    {
        Secured++;
        _taken |= 1 << k;
        var change = TfwBattleChange.PositionSecured;
        if (k == 1 && !_depotPaid) { _depotPaid = true; change |= TfwBattleChange.DepotSecured; }
        for (int field = _guards.SquadsAlive; field < TfwBattleRules.FieldSquads && _reserves > 0; field++)
            if (_guards.SpawnSquad(Ground(_layout.LandingZone + SquadOffset(field), _layout.LandingZone), YawTo(_layout.LandingZone, _layout.Positions[k])) >= 0) _reserves--;
        if (_taken != (1 << Positions) - 1 || _commander) return change;
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

    /// <summary>
    /// The crowds grow: one Ork per <see cref="TfwBattleRules.CrowdFillSeconds"/> from each point that sends, up to the cap (a crowd counts into it, so a crowd refills as the swarms are killed; a crowd is never let
    /// go below 50 for the cap's sake, and the stranded-Ork failsafe removes the Orks nobody can reach). The Waaagh!s' Boyz come from the sources at once.
    /// </summary>
    void StepArrivals()
    {
        int cap = TfwBattleRules.AliveCap(_humansNow);
        int counted = Counted();
        for (int k = 0; k <= Positions; k++)
        {
            if (!SourceActive(k) || --_tideTicks[k] > 0) continue;
            _tideTicks[k] = Ticks(TfwBattleRules.CrowdFillSeconds);
            _lastTide[k] = _now;
            if (counted >= cap) continue;
            if (AddToCrowd(k)) { _released++; counted++; }
        }
        for (int k = 0; k < TfwBattleRules.ReleasesPerTick && _summons > 0 && Counted() < cap; k++)
        {
            _summons--;
            var at = SourceSpot(_summons % _layout.Sources.Length);
            _enemies.Spawn(MusouEnemyType.Boy, at, YawTo(at, _layout.LandingZone), Health(MusouEnemyType.Boy));
        }
    }

    /// <summary>A spot round a drawn source (the failsafe's, and the Waaagh!s' Boyz).</summary>
    Vector3 SourceSpot(int source) => DiscSpot(_layout.Sources[source]);

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
                for (int s = 0; s < _layout.Sources.Length && s <= Positions; s++) if (SourceActive(s)) { source = s; break; }
                _enemies.Relocate(id, SourceSpot(source));
            }
            else _enemies.Damage(id, 0, e.Health, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World);
        }
    }
}
