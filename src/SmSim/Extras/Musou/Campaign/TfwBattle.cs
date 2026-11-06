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
    /// <summary>
    /// The Guardsmen of a battle: off on 2026-11-01 ("Temporarily remove all guardsmen allies"), back on 2026-11-06 ("re add the imperial guardsmen infantry formations to the game. they move and act on their own to
    /// defend existing points. only allied space marine lords and players and retainers go capture new territory"). They never capture: they hold the landing zone and the secured positions.
    /// </summary>
    public const bool GuardsmenEnabled = true;
    /// <summary>What a secured position takes to fall: this long with Orks alone on it (a retake, and the way back up the same).</summary>
    public const float RetakeSeconds = 20f;
    /// <summary>A field short of squads gets the next reserve after this long.</summary>
    public const float ReserveSeconds = 15f;
    /// <summary>The squads' orders are looked at every this many seconds.</summary>
    public const float SquadOrderSeconds = 1f;
    /// <summary>A squad stands on a point at most this share of the point's radius from its centre.</summary>
    public const float SquadOnPoint = 0.7f;
    /// <summary>
    /// The defence (owner, 2026-11-06; every value ours, an estimate): the squads share the secured positions out by the Orks within <see cref="ThreatRadius"/> of each (counted up to
    /// <see cref="ThreatMost"/>, in steps of <see cref="ThreatStep"/> so a squad does not walk off for one Ork), each counting <see cref="ThreatFloor"/> more, so with no threat they spread evenly.
    /// With no position secured they hold the landing zone, within <see cref="LandingHoldRadius"/>.
    /// </summary>
    public const float ThreatRadius = 40f;
    public const int ThreatMost = 64, ThreatStep = 8;
    public const float ThreatFloor = 8f;
    public const float LandingHoldRadius = 10f;

    /// <summary>A leashed Ork wakes when a target comes this near its home (<see cref="MusouSim.SpawnLeashed"/>), and is free for good: a garrison and the commander's group.</summary>
    public const float DefenderAggro = 30f;

    // The garrison of one position (owner, 2026-11-01: "there is always a consistent ork garrison waiting on each point that never respawns. they defend each point"): it stands leashed on the point from the landing and is never rebuilt.
    public const int Garrison = 96;   // the owner, 2026-11-02, 2026-11-03 and 2026-11-06: double the Orks of the battle, three times (12, 24, then 48)

    // The crowds (owner, 2026-11-01): "the orks should pool up at their spawn point until they reach a crowd of 50 orks, then swarm at the player. there should be 3 major spawn points across the map doing this";
    // "pools replace the tide"; "50 per point, 150 total. Each point swarms on its own."
    /// <summary>The crowd of one spawn point: it stands there leashed, one more every <see cref="CrowdFillSeconds"/>, and at this many all of it swarms at once. The owner's number.</summary>
    public const int CrowdSize = 400;   // the owner, 2026-11-02, 2026-11-03 and 2026-11-06: double, three times (50, 100, then 200; the fill keeps a crowd at about 53 s)
    /// <summary>A crowd's Orks notice a target this near their own places (a crowd wakes as one when any of it does, or is hurt). Ours: a player at the ring (13.5 to 15 m out) wakes a fresh small crowd on the far side of the disc (about 30 m).</summary>
    public const float CrowdAggro = 30f;

    /// <summary>
    /// A held point adds one Ork to its crowd every this many seconds (the owner's tide of 1 s, now the crowd's fill: 50 s for a crowd). The owner, 2026-11-01: "you have to fully capture a point to disable the spawning
    /// from that point. But it's temporarily paused while the point is being actively captured"; "Orks should begin spawning again immediately".
    /// </summary>
    public const float CrowdFillSeconds = 4f / 30f;   // 4 ticks: 7.5 a second, a crowd of 400 in 53 s (2026-11-06; was 8 ticks for 200)
    /// <summary>
    /// The owner, 2026-11-02: "the reinforcements that spawn at all points pool and then swarm the player even if far away". The alive cap counts the swarms still fighting, so a far crowd could stall under
    /// <see cref="CrowdSize"/> and stand for good: a crowd goes with what it has this long after the point's last swarm (ours; a crowd under no cap fills in 50 s).
    /// </summary>
    public const float CrowdWaitSeconds = 60f;
    /// <summary>The owner, 2026-11-02: "elite ork enemies should spawn in packs or squads of at least 20": a crowd's elites come as runs of this many, the same share of the head count.</summary>
    public const int EliteSquad = 20;
    /// <summary>Slots of the pool's 2048 no crowd or trickle may take: the commander, his escorts and the Warboss spawn only if the pool has room, and a battle cannot end without its boss.</summary>
    public const int PoolReserve = 80;   // 40 in a pool of 1024; doubled with the pool (2026-11-06)
    /// <summary>A crowd and a garrison stand on a disc this wide round their point.</summary>
    public const float SpawnDisc = 11f;   // 8 m for crowds of 200; 11 m holds 400 at about the same room an Ork (2026-11-06)
    /// <summary>
    /// The owner, 2026-11-01: "50 per point, 150 total": the Orks of the crowds and the swarms alive at once, a garrison not counted (it never respawns, 12 a point); 80 more a further human; never over
    /// <see cref="AliveCapMost"/>, which leaves the pool's 2048 slots room for the garrisons and those dying.
    /// </summary>
    public const int AliveCapSolo = 1200, AliveCapPerExtraHuman = 640, AliveCapMost = 1680;   // the owner, 2026-11-02, 2026-11-03 and 2026-11-06: double, three times (150 and 80, 300 and 160, then 600 and 320); the most leaves the pool's 2048 room for the 288 of the garrisons and the reserve
    public const int ReleasesPerTick = 6;

    // The trickle (owner, 2026-11-01: "re add in the trickling enemies resulting in roughly 2x the current ork enemy count on the map"; every value ours, spec "the trickle"): beside the pools and swarms, each source that sends
    // also lets one unleashed Ork walk to the players every TrickleSeconds, up to as many trickled Orks alive as the crowds' cap (so about twice the Orks on the map), and never past TrickleTotalMax in all (the pool has 2048 slots).
    /// <summary>Seconds between two trickled Orks from one sending source (ours; three held points send two a second together).</summary>
    public const float TrickleSeconds = 0.75f;   // 1.5 s before the owner's third doubling (2026-11-06)
    /// <summary>Most Orks alive (garrisons not counted) while a trickled one is added: the pool's 2048 slots less the garrisons (288), those dying and a margin; so the trickle adds 400 to the crowds' 1200 for one human (the pool's reserve ends it).</summary>
    public const int TrickleTotalMax = 1600;   // 800 in a pool of 1024 (2026-11-06)
    /// <summary>Most trickled Orks alive at once with <paramref name="humans"/> humans: the crowds' cap again, so the map holds about twice <see cref="AliveCap"/>.</summary>
    public static int TrickleCap(int humans) => AliveCap(humans);

    // The muster (the owner, 2026-11-06: "they should pool up at their spawn point before fanning out"; every value ours, an estimate): a source's trickled Orks stand leashed where they came until this many have
    // gathered, or the first has waited this long, or one of them is woken or hurt; then they go as one pack (MusouSim.Pack.cs).
    public const int MusterSize = 10;      // a source sending every 0.75 s fills it in about 7 s
    public const float MusterSeconds = 10f;   // the wait only ends a muster whose source stopped sending (a capture, the cap)

    // What the Orks are made of (the design's section B; the owner's item 6). Of every 100 fodder and of every 100 elites:
    public const float EliteBase = 0.10f, EliteSlope = 0.50f;
    public const float FodderBoyz = 0.70f, FodderShootas = 0.30f, EliteArds = 0.75f;   // the owner, 2026-11-02: no Gretchin; their 0.15 went to the Boyz, the head count is unchanged

    // The commander. The owner, 2026-11-06: "replace him with the warboss": the battle's boss at the HQ is the Warboss (MusouEnemyType.Warboss, its model, size, abilities and
    // MusouRules.BossHealth), still leashed at the HQ with his escort; CommanderHealth is what he had as a Nob (kept for the record, no longer used).
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
    /// <summary>The owner, 2026-11-06 (stream D): a fallen human's deploy back at the landing zone costs this many supplies; the host's recall comes after this many seconds ("players must survive 20 s").</summary>
    public const int RedeployCost = 1;
    public const float RecallSeconds = 20f;
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
public sealed record TfwLayout(Vector3 LandingZone, Vector3[] Positions, Vector3 Hq, Vector3[] Sources, float[]? Radii = null, float[]? HalfHeights = null, Vector3? Boss = null)
{
    /// <summary>Where the final boss comes: the open courtyard just outside the HQ's room (<see cref="TfwSites.Courtyard"/>), else the HQ itself.</summary>
    public Vector3 BossSpot => Boss ?? Hq;

    /// <summary>A position's radius and half height when the layout names none (the spec's "Musou Mode's map"; the map's markers give the real ones).</summary>
    public const float DefaultRadius = 12f, DefaultHalfHeight = 2.5f;

    /// <summary>The radius of position <paramref name="k"/>.</summary>
    public float Radius(int k) => Radii is { } r && k < r.Length ? r[k] : DefaultRadius;

    /// <summary>How far above or below position <paramref name="k"/> an Ork still holds it (points differ in height; never less than a storey's <see cref="DefaultHalfHeight"/>).</summary>
    public float Reach(int k) => MathF.Max(DefaultHalfHeight, HalfHeights is { } h && k < h.Length ? h[k] : 0f);
}

/// <summary>Why a crowd swarmed: it reached <see cref="TfwBattleRules.CrowdSize"/>; or a target came near it, or one of it was hurt, and it went with what it had.</summary>
public enum TfwSwarmWhy : byte { Full = 0, Woken = 1, Waited = 2 }

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
/// One battle (design sections D and I), or the final assault: the three positions, the crowd each spawn point pools and then swarms with (owner, 2026-11-01), the Guard squads, which hold the landing zone and defend the secured positions and never take one (owner, 2026-11-06), the commander
/// (he comes once each point has fallen, whatever has been retaken since, so a battle cannot hang on holding all three at once), and the failsafe for enemies nobody can reach. Works on the enemies' pool and the Guardsmen alone, deterministically; the run (<see cref="TfwRun"/>) owns everything around it. A
/// position is Held with no capturer (player, lord, retainer) on it, Clearing with capturers and Orks on it, Securing with capturers alone (both grow its progress at the full rate, owner 2026-11-06), Secured when that has run
/// its time; only Held sends Orks (a capture pauses it, a full one stops it, and the tick a capture ends or a retake is done it sends again). A secured position with Orks alone on it (no capturer, no Guardsman) falls
/// back to Held in <see cref="TfwBattleRules.RetakeSeconds"/>. The squads share the secured positions out by threat.
/// </summary>
public sealed partial class TfwBattle
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
    Predicate<ushort>? _notAtPostCache;
    Predicate<ushort> _notAtPost => _notAtPostCache ??= id => !_enemies.TryGet(id, out var e) || !e.Alive || !_enemies.Leashed(id);
    // The stuck-Ork failsafe of a position being cleared (ours): an Ork in its volume that stays within StuckRadius of one spot, seeking and attacking nothing, for StuckSeconds is removed (a World KO for nobody).
    readonly Dictionary<ushort, (Vector3 Anchor, uint Since)> _idleInside = new();
    uint _clearTick;
    public const float StuckSeconds = 6f, StuckRadius = 2.5f;
    readonly bool _final, _guardsmen;
    readonly WarFinalBonus _bonus;
    readonly int _humans;
    readonly float _scale;
    readonly int[] _crowdWait = new int[Positions + 1];   // ticks a crowd has waited
    readonly float[] _eliteOwed = [15f, 15f, 15f, 15f];   // elites the share has earned that no squad has spent
    readonly int[] _tideTicks = new int[Positions + 1];   // ticks to the point's next Ork; 0: on this tick
    readonly int[] _lastTide = new int[Positions + 1];    // the battle's tick of its last
    readonly int[] _trickleTicks = new int[Positions + 1];   // ticks to the source's next trickled Ork
    readonly List<ushort> _trickled = new();                 // the trickled Orks standing (pruned each tick)
    readonly List<ushort>[] _musters = [new(), new(), new(), new()];   // each source's trickled Orks still mustering at it
    readonly int[] _musterWait = new int[Positions + 1];     // ticks its first has waited
    int _now, _humansNow, _swarms;
    const int CrowdGroupFirst = Positions + 2;   // after the garrisons' (1 to 3) and the commander's (4): the positions' crowds, then the HQ source's
    const int MusterGroupFirst = CrowdGroupFirst + Positions + 1;   // then each source's muster (9 to 12)
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
        Array.Fill(_trickleTicks, Ticks(TfwBattleRules.TrickleSeconds));
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
        PlanColumns();   // TfwBattle.Patrols.cs: the patrols and hunter columns (their routes are searched off the tick, taken 3 s after the start)
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

        /// <summary>A Boy or a Shoota by its share, a low-discrepancy sequence, no dice (an elite only ever comes in a squad: <see cref="AddSquad"/>).</summary>
    static MusouEnemyType Fodder(int n) => n * 0.7548776662466927 % 1.0 < TfwBattleRules.FodderBoyz ? MusouEnemyType.Boy : MusouEnemyType.ShootaBoy;

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
        var type = Fodder(_picked++);
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
        if (_enemies.Alive >= MusouSim.Capacity - TfwBattleRules.PoolReserve) return false;
        var type = Fodder(_picked++);
        var at = DiscSpot(CrowdAnchor(point));
        ushort id = _enemies.SpawnLeashed(type, at, YawTo(at, _layout.LandingZone), Health(type), CrowdGroup(point), TfwBattleRules.CrowdAggro);
        if (id == 0) return false;
        _crowds[point].Add(id);
        if (_crowds[point].Count >= TfwBattleRules.CrowdSize) Swarm(point, TfwSwarmWhy.Full);
        return true;
    }

    /// <summary>
    /// A whole squad of <see cref="TfwBattleRules.EliteSquad"/> elites joins spawn point <paramref name="point"/>'s crowd at once, when <see cref="EliteShare"/> has earned one and it fits the crowd, the cap (<paramref name="spare"/>) and the pool:
    /// at once, so no swarm splits it and no stall leaves it short. The point's next arrival waits as long as those Orks would have taken. True when it came.
    /// </summary>
    bool AddSquad(int point, int spare)
    {
        int squad = TfwBattleRules.EliteSquad;
        if (_eliteOwed[point] < squad || spare < squad || TfwBattleRules.CrowdSize - _crowds[point].Count < squad || _enemies.Alive + squad > MusouSim.Capacity - TfwBattleRules.PoolReserve) return false;
        _eliteOwed[point] -= squad - EliteShare * (squad - 1);   // the squad's own head count earns its share too
        for (int k = 0; k < squad; k++)
        {
            int n = _picked++;
            var type = n * 0.7548776662466927 % 1.0 < TfwBattleRules.EliteArds ? MusouEnemyType.ArdBoy : MusouEnemyType.Nob;
            var at = DiscSpot(CrowdAnchor(point));
            ushort id = _enemies.SpawnLeashed(type, at, YawTo(at, _layout.LandingZone), Health(type), CrowdGroup(point), TfwBattleRules.CrowdAggro);
            if (id != 0) _crowds[point].Add(id);
        }
        _tideTicks[point] = Ticks(TfwBattleRules.CrowdFillSeconds) * squad;
        _lastTide[point] = _now + Ticks(TfwBattleRules.CrowdFillSeconds) * (squad - 1);   // SendAgain keeps the wait too
        if (_crowds[point].Count >= TfwBattleRules.CrowdSize) Swarm(point, TfwSwarmWhy.Full);
        return true;
    }

    /// <summary>Lets spawn point <paramref name="point"/>'s whole crowd go: every one of them is free for good and goes for the players; the point starts a new crowd. <paramref name="extra"/>: Orks of it already woken.</summary>
    void Swarm(int point, TfwSwarmWhy why, IReadOnlyList<ushort>? extra = null)
    {
        var crowd = _crowds[point];
        _enemies.ReleaseGroup(CrowdGroup(point));
        _swarms++;
        _crowdWait[point] = 0;
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
        foreach (var garrison in _garrisons) garrison.RemoveAll(_notAtPost);   // the garrison is those still at their post
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
        var hq = !leashed ? _layout.BossSpot : _layout.Hq;   // the final boss stands in the courtyard outside the HQ's room (owner, 2026-11-01); the commander (a Warboss since 2026-11-06) keeps the HQ
        float yaw = YawTo(hq, _layout.LandingZone);
        BossId = leashed ? _enemies.SpawnLeashed(type, hq, yaw, health, CommanderGroup, TfwBattleRules.DefenderAggro) : _enemies.Spawn(type, hq, yaw, health);
        BossMaxHealth = health;
        _objectives[CommanderObjective] = _objectives[CommanderObjective] with { State = TfwPositionState.Held };
    }

    void SpawnCommander()
    {
        SpawnBoss(MusouEnemyType.Warboss, _enemies.Rules.BossHealth * _scale, leashed: true);   // the owner, 2026-11-06: "replace him with the warboss" (was a Nob of CommanderHealth)
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
        TidyMusters();
        _enemies.KeepOut.Clear();
        if (Won) { StepVictoryWave(players); return change; }   // TfwBattle.Victory.cs: the wave from every side
        if (BossId != 0)
        {
            if (!_enemies.TryGet(BossId, out var boss) || !boss.Alive)
            {
                Won = true;
                BossId = 0;
                BeginVictoryWave();
                _objectives[CommanderObjective] = _objectives[CommanderObjective] with { State = TfwPositionState.Secured, Progress = 1f };
                return change | TfwBattleChange.Won;
            }
            _objectives[CommanderObjective] = _objectives[CommanderObjective] with { Progress = 1f - boss.Health / MathF.Max(1f, BossMaxHealth) };
        }
        if (!_final)
        {
            _clearTick++;
            for (int k = 0; k < Positions; k++) change |= StepPosition(k, players);
            // A position being taken is kept clear of shooters: they hold their band, which lies inside its ring (see MusouSim.KeepOut).
            for (int k = 0; k < Positions; k++)
                if (_objectives[k].State is TfwPositionState.Clearing or TfwPositionState.Securing) _enemies.KeepOut.Add((_layout.Positions[k], _layout.Radius(k), _layout.Reach(k)));
        }
        StepSquads();
        StepReserves();
        StepColumns(players);   // TfwBattle.Patrols.cs: before the arrivals, so the crowds see the cap the columns take
        StepArrivals();
        StepBossOrks(players);   // the roaming Boss Orks (owner, 2026-11-06; TfwBattle.BossOrks.cs)
        StepWeirdboyz(players);   // the Weirdboyz (TfwBattle.Weirdboyz.cs)
        return change;
    }

    /// <summary>
    /// One tick of a position: who stands on it says what it does (the type's summary). Only a capturer takes ground: a player, a lord or a retainer (<paramref name="players"/>, the living players
    /// and AI Marines); a Guardsman never does (owner, 2026-11-06). The owner, 2026-11-06: "players should be able to make progress capturing a point even if orks are still defending it. only stop capturing
    /// if no player remains on the point": with a capturer on it the progress grows at the full rate whatever Orks stand there (Clearing with Orks on it, Securing without); with none it is Held and
    /// decays. A secured position is kept by any Imperial on it, Guardsmen included (they defend it), and falls with Orks alone on it.
    /// </summary>
    TfwBattleChange StepPosition(int k, ReadOnlySpan<Vector3> players)
    {
        var o = _objectives[k];
        bool capturers = AnyInside(k, players), orks = OrksInside(k);
        if (o.State == TfwPositionState.Secured)
        {
            bool imperial = capturers || _guards.CountInside(o.Position, _layout.Radius(k)) > 0;
            float change = Dt / TfwBattleRules.RetakeSeconds;
            float held = orks && !imperial ? o.Progress - change : MathF.Min(1f, o.Progress + change);
            if (held > 0f) { _objectives[k] = o with { Progress = held }; return TfwBattleChange.None; }
            _objectives[k] = o with { State = TfwPositionState.Held, Progress = 0f };
            Secured--;
            SendAgain(k);
            return TfwBattleChange.None;
        }
        if (!capturers)
        {
            if (o.State != TfwPositionState.Held) SendAgain(k);
            o = o with { State = TfwPositionState.Held, Progress = MathF.Max(0f, o.Progress - TfwBattleRules.SecureDecayPerSecond * Dt) };
        }
        else
        {
            if (orks) RemoveStuck(k);
            o = o with { State = orks ? TfwPositionState.Clearing : TfwPositionState.Securing, Progress = o.Progress + Dt / TfwBattleRules.SecureSeconds };
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

    /// <summary>While position <paramref name="k"/> is Clearing: an unleashed Ork in its volume that has stayed in one spot seeking (no attack, no headway) for <see cref="StuckSeconds"/> is removed, so one on a ledge it cannot leave never keeps the point from being taken.</summary>
    void RemoveStuck(int k)
    {
        Span<ushort> ids = stackalloc ushort[96];
        int n = _enemies.Query(_layout.Positions[k], _layout.Radius(k), ids, _layout.Reach(k));
        uint limit = (uint)MathF.Ceiling(StuckSeconds / Dt);
        for (int i = 0; i < n; i++)
        {
            ushort id = ids[i];
            if (id == BossId || _enemies.Leashed(id) || !_enemies.TryGet(id, out var e) || !e.Alive || MusouBossOrks.Is(e.Type)) continue;   // a Boss Ork is never removed (2026-11-06)
            if (e.State is not (MusouEnemyState.Seek or MusouEnemyState.Spawning)) { _idleInside.Remove(id); continue; }
            if (!_idleInside.TryGetValue(id, out var seen) || Vector3.DistanceSquared(seen.Anchor, e.Position) > StuckRadius * StuckRadius)
            { _idleInside[id] = (e.Position, _clearTick); continue; }
            if (_clearTick - seen.Since < limit) continue;
            _idleInside.Remove(id);
            _enemies.Damage(id, 0, e.Health, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World);
        }
        if (_idleInside.Count > 256) _idleInside.Clear();
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

    /// <summary>Where a squad holds with no position secured: the landing zone (a goal past the HQ's).</summary>
    public const int LandingGoal = CommanderObjective + 1;
    readonly int[] _want = new int[Positions], _have = new int[Positions];

    /// <summary>
    /// The squads' orders, once a second (owner, 2026-11-06: "they move and act on their own to defend existing points"): they never go for a position not secured. With none secured every squad holds the
    /// landing zone; otherwise the squads are shared out over the secured positions by threat (<see cref="TfwBattleRules.ThreatRadius"/>): each squad goes, one at a time, where (threat + floor) / (squads + 1)
    /// is highest. A squad keeps its post while its post still wants it, the others go to the nearest post short of squads. A squad is sent again when its goal changed, or when it stopped outside it.
    /// </summary>
    void StepSquads()
    {
        if (--_squadTicks > 0) return;
        _squadTicks = Ticks(TfwBattleRules.SquadOrderSeconds);
        int alive = 0, owned = 0;
        for (int s = 0; s < GuardSim.MaxSquads; s++)
            if (_guards.AliveIn(s) > 0) alive++;
            else _squadGoal[s] = -1;
        if (alive == 0) return;
        Span<float> threat = stackalloc float[Positions];
        Span<ushort> near = stackalloc ushort[TfwBattleRules.ThreatMost];
        for (int k = 0; k < Positions; k++)
        {
            _want[k] = _have[k] = 0;
            if (_objectives[k].State != TfwPositionState.Secured) { threat[k] = -1f; continue; }
            owned++;
            int n = _enemies.Query(_layout.Positions[k], TfwBattleRules.ThreatRadius, near, TfwBattleRules.ThreatRadius);
            threat[k] = n / TfwBattleRules.ThreatStep * TfwBattleRules.ThreatStep + TfwBattleRules.ThreatFloor;
        }
        if (owned == 0)
        {
            for (int s = 0; s < GuardSim.MaxSquads; s++) if (_guards.AliveIn(s) > 0) Keep(s, LandingGoal);
            return;
        }
        for (int n = 0; n < alive; n++)
        {
            int best = -1;
            float bestScore = float.NegativeInfinity;
            for (int k = 0; k < Positions; k++)
            {
                if (threat[k] < 0f) continue;
                float score = threat[k] / (_want[k] + 1);
                if (score > bestScore) { bestScore = score; best = k; }
            }
            _want[best]++;
        }
        Span<bool> placed = stackalloc bool[GuardSim.MaxSquads];
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            int g = _squadGoal[s];
            if (_guards.AliveIn(s) == 0 || g is < 0 or >= Positions || _have[g] >= _want[g]) continue;
            _have[g]++;
            placed[s] = true;
            Keep(s, g);
        }
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (_guards.AliveIn(s) == 0 || placed[s]) continue;
            int best = -1;
            float bestD = float.PositiveInfinity;
            for (int k = 0; k < Positions; k++)
            {
                if (_have[k] >= _want[k]) continue;
                float d = Vector3.DistanceSquared(_guards.Centre(s), _layout.Positions[k]);
                if (d < bestD) { bestD = d; best = k; }
            }
            if (best < 0) continue;
            _have[best]++;
            Keep(s, best);
        }
    }

    /// <summary>Squad <paramref name="s"/>'s post is <paramref name="goal"/>: sent there when it was posted elsewhere, or stopped outside it.</summary>
    void Keep(int s, int goal)
    {
        if (goal != _squadGoal[s] || (_guards.Arrived(s) && !OnGoal(s, goal))) Send(s, goal);
    }

    /// <summary>The post of squad <paramref name="squad"/> (a position, <see cref="LandingGoal"/>, or -1: none yet) (tests).</summary>
    public int SquadPost(int squad) => squad is >= 0 and < GuardSim.MaxSquads ? _squadGoal[squad] : -1;

    Vector3 PlaceOf(int goal) => goal == LandingGoal ? _layout.LandingZone : goal == CommanderObjective ? _layout.Hq : _layout.Positions[goal];

    float RadiusOf(int goal) => goal == LandingGoal ? TfwBattleRules.LandingHoldRadius : goal == CommanderObjective ? TfwMap.HqRadius : _layout.Radius(goal);

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
        _trickled.RemoveAll(_notTrickling);
        int counted = Counted() - _trickled.Count;   // the crowds' cap counts the crowds and swarms; the trickle has its own
        for (int k = 0; k <= Positions; k++)
        {
            if (_crowds[k].Count > 0 && ++_crowdWait[k] >= Ticks(TfwBattleRules.CrowdWaitSeconds)) Swarm(k, TfwSwarmWhy.Waited);
            if (!SourceActive(k) || --_tideTicks[k] > 0) continue;
            _tideTicks[k] = Ticks(TfwBattleRules.CrowdFillSeconds);
            _lastTide[k] = _now;
            if (counted >= cap) continue;
            _eliteOwed[k] += EliteShare;
            if (AddSquad(k, cap - counted)) { _released += TfwBattleRules.EliteSquad; counted += TfwBattleRules.EliteSquad; continue; }
            if (AddToCrowd(k)) { _released++; counted++; }
        }
        StepTrickle();
        for (int k = 0; k < TfwBattleRules.ReleasesPerTick && _summons > 0 && Counted() - _trickled.Count < cap && _enemies.Alive < MusouSim.Capacity - TfwBattleRules.PoolReserve; k++)
        {
            _summons--;
            var at = SourceSpot(_summons % _layout.Sources.Length);
            _enemies.Spawn(MusouEnemyType.Boy, at, YawTo(at, _layout.LandingZone), Health(MusouEnemyType.Boy));
        }
    }

    Predicate<ushort>? _notTricklingCache;
    Predicate<ushort> _notTrickling => _notTricklingCache ??= id => !_enemies.TryGet(id, out var e) || !e.Alive;

    /// <summary>
    /// The trickle (ours; the owner's "trickling enemies"): every source that sends (a position only while Held, so a capture in progress pauses it and a full capture stops it; the HQ's from the commander on; all four in the
    /// final assault) lets one unleashed Ork go every <see cref="TfwBattleRules.TrickleSeconds"/>. It spawns at the source and walks to the players like a swarm's; none is in a crowd, so none keeps a crowd from swarming.
    /// </summary>
    void StepTrickle()
    {
        if (!Trickle) return;
        int cap = TfwBattleRules.TrickleCap(_humansNow);
        for (int k = 0; k <= Positions; k++)
        {
            if (!SourceActive(k)) { _trickleTicks[k] = Ticks(TfwBattleRules.TrickleSeconds); continue; }
            if (--_trickleTicks[k] > 0) continue;
            _trickleTicks[k] = Ticks(TfwBattleRules.TrickleSeconds);
            if (_trickled.Count >= cap || Counted() >= TfwBattleRules.TrickleTotalMax || _enemies.Alive >= MusouSim.Capacity - TfwBattleRules.PoolReserve) continue;
            var type = Fodder(_picked++);
            var at = SourceSpot(k);
            ushort id = _enemies.SpawnLeashed(type, at, YawTo(at, _layout.LandingZone), Health(type), MusterGroupFirst + k, TfwBattleRules.CrowdAggro);
            if (id == 0) continue;
            _trickled.Add(id);
            _musters[k].Add(id);
            if (_musters[k].Count >= TfwBattleRules.MusterSize) Muster(k);
        }
    }

    /// <summary>Lets source <paramref name="k"/>'s muster go as one pack (<see cref="MusouSim.ReleaseGroup"/>).</summary>
    void Muster(int k)
    {
        _enemies.ReleaseGroup(MusterGroupFirst + k);
        _musters[k].Clear();
        _musterWait[k] = 0;
    }

    /// <summary>Each tick: a muster's dead and woken are forgotten (one woken, or hurt, lets the rest go with it), and one whose first has waited <see cref="TfwBattleRules.MusterSeconds"/> goes.</summary>
    void TidyMusters()
    {
        for (int k = 0; k <= Positions; k++)
        {
            var muster = _musters[k];
            if (muster.Count == 0) { _musterWait[k] = 0; continue; }
            bool woken = false;
            for (int n = muster.Count - 1; n >= 0; n--)
            {
                ushort id = muster[n];
                bool alive = _enemies.TryGet(id, out var e) && e.Alive;
                if (alive && _enemies.Leashed(id)) continue;
                woken |= alive;
                muster.RemoveAt(n);
            }
            if (woken || ++_musterWait[k] >= Ticks(TfwBattleRules.MusterSeconds)) Muster(k);
        }
    }

    /// <summary>Test aid: the trickled Orks mustering at source <paramref name="k"/> now.</summary>
    public ushort[] MusterIds(int k) => _musters[k].ToArray();

    /// <summary>Whether the trickle runs (ours; false only in tests that count the crowds alone).</summary>
    public bool Trickle { get; set; } = true;

    /// <summary>Test aid: the trickled Orks alive now.</summary>
    public int Trickled => _trickled.Count;

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
            if (id == BossId || MusouBossOrks.Is(e.Type) || _enemies.Relocations(id) < _enemies.Rules.StrandedRelocations)   // a boss, and a Boss Ork, is only ever moved
            {
                int source = _layout.Sources.Length - 1;
                for (int s = 0; s < _layout.Sources.Length && s <= Positions; s++) if (SourceActive(s)) { source = s; break; }
                _enemies.Relocate(id, SourceSpot(source));
            }
            else _enemies.Damage(id, 0, e.Health, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.World);
        }
    }
}
