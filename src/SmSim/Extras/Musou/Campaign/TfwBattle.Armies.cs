using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The battle flow's far layer (expansion S6, docs/musou-expansion.md "S6 design"): the armies, their levels (real within <see cref="TfwArmyRules.NearMetres"/> of any human, aggregate past
/// <see cref="TfwArmyRules.FarMetres"/>), their ways between the battle's places, their marches and arrivals, and the far clashes at the positions. The waves (<c>TfwBattle.Waves.cs</c>), the Guard reinforcements
/// and garrisons (<c>TfwBattle.Garrisons.cs</c>) and the retreats and rallies (<c>TfwBattle.Retreats.cs</c>) make and move the armies; this file keeps them. Nothing here runs with <see cref="Armies"/> off.
/// </summary>
public sealed partial class TfwBattle
{
    // The armies' places: 0 to 2 the positions, 3 the Imperial base (the landing zone), 4 the HQ's source (always an Ork stronghold).
    const int BasePlace = Positions, HqPlace = Positions + 1, Places = Positions + 2;
    /// <summary>An Ork army going home has goal <c>StrongholdGoalFirst + place</c>.</summary>
    public const int StrongholdGoalFirst = 10;
    public static int StrongholdGoal(int place) => StrongholdGoalFirst + place;
    const int ArmyGroupFirst = 100_000;

    readonly List<TfwArmy> _armies = new();
    readonly TfwWay?[,] _ways = new TfwWay?[Places, Places];
    readonly int[] _squadArmy = Enumerable.Repeat(-1, GuardSim.MaxSquads).ToArray();   // the army owning a Guard squad (-1: a field squad)
    readonly List<Vector3> _armyHumans = new();
    readonly List<(float D, TfwArmy A)> _byDistance = new();
    readonly List<float> _armyBehind = new();
    Task<List<((int, int) Segment, List<Vector3>? Way, double Milliseconds)>>? _armyRouteTask;
    int _armyRouteTicks, _armyFarTicks, _farSteps, _nextGroup = ArmyGroupFirst;
    byte _nextArmy = 1;
    float _farOrkKills, _farGuardLosses;
    readonly float[] _realDebt = new float[2 * (Positions + 1)];   // a far clash's losses still owed by real units (Orks and Guardsmen of each position, then of the base)

    /// <summary>Whether the battle flow runs (expansion S6; false only in tests that count the old flow alone, as <see cref="Trickle"/>).</summary>
    public bool Armies { get; set; } = true;
    /// <summary>
    /// Whether an Ork army keeps an aggregate (the rules before 2026-11-09; <see cref="TfwArmyRules.OldOrkArmies"/>, and tests of the old far layer). Off: an Ork army is real Orks alone (the owner: "completely
    /// remove those orks from musou mode"): its men are made real wherever it stands, never folded back, what finds no room under the cap is never made, and no Ork aggregate clashes or counts on a point.
    /// </summary>
    public bool OrkAggregates { get; set; } = TfwArmyRules.OldOrkArmies;
    /// <summary>Ork army men never made for want of room (the real cap or the pool; tests and the profile).</summary>
    public int OrksNeverMade { get; private set; }
    /// <summary>The armies now (the wire's tag 18 and the tests).</summary>
    public IReadOnlyList<TfwArmy> ArmyList => _armies;
    /// <summary>The armies' real Orks alive now (outside the crowds' pool and cap).</summary>
    public int ArmyOrksAlive { get; private set; }
    /// <summary>Of <see cref="ArmyOrksAlive"/>, the big Orks (<see cref="TfwBattleRules.Big"/>), left out of the armies' real cap since 2026-11-10 (the owner: "let them exist independent of the cap").</summary>
    public int ArmyBigAlive { get; private set; }
    /// <summary>The armies' real Orks the real cap counts.</summary>
    int ArmyCapped => ArmyOrksAlive - ArmyBigAlive;
    /// <summary>Guard squads the armies own now.</summary>
    public int ArmySquads { get; private set; }
    /// <summary>Guardsmen of the aggregates lost in far clashes (the war's count of the battle's losses adds them to the real ones).</summary>
    public int GuardsLostFar { get; private set; }
    /// <summary>Promotions and demotions so far (units; the profile's).</summary>
    public int Promoted { get; private set; }
    public int Demoted { get; private set; }
    /// <summary>Test aid: the humans' feet as the armies saw them last.</summary>
    public IReadOnlyList<Vector3> Humans => _armyHumans;

    float FarStepSeconds => Ticks(TfwClashRules.StepSeconds) * Dt;

    /// <summary>A place of the armies' routes.</summary>
    Vector3 PlacePos(int place) => place < Positions ? _layout.Positions[place] : place == BasePlace ? _layout.LandingZone : _layout.Sources.Length > Positions ? _layout.Sources[Positions] : _layout.Hq;

    /// <summary>The place a goal names.</summary>
    static int GoalPlace(int goal) => goal >= StrongholdGoalFirst ? goal - StrongholdGoalFirst : goal == LandingGoal ? BasePlace : goal;

    bool Imperial(int k) => k is >= 0 and < Positions && _objectives[k].State == TfwPositionState.Secured;
    /// <summary>An Ork stronghold: a position the Orks hold (Held: not under capture) or the HQ's source.</summary>
    bool Stronghold(int place) => place == HqPlace || (place is >= 0 and < Positions && _objectives[place].State == TfwPositionState.Held && !_final);

    static bool Leader(MusouEnemyType t) => t is MusouEnemyType.Nob or MusouEnemyType.ArdBoy or MusouEnemyType.BigShootaNob or MusouEnemyType.Warboss || MusouBossOrks.Is(t);
    static readonly Func<MusouEnemyType, bool> IsLeader = Leader;

    static float YawOf(Vector2 way) => way.LengthSquared() > 1e-8f ? MathF.Atan2(-way.X, way.Y) : 0f;
    static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    /// <summary>The routes between every pair of the armies' places, searched off the tick from the start (as the patrols' are) and taken at <see cref="TfwArmyRules.RouteSeconds"/>.</summary>
    void PlanArmyRoutes()
    {
        if (!Armies) return;
        var ends = new List<((int, int), Vector3, Vector3)>();
        for (int a = 0; a < Places; a++)
            for (int b = a + 1; b < Places; b++)
                ends.Add(((a, b), Ground(PlacePos(a), PlacePos(a)), Ground(PlacePos(b), PlacePos(b))));
        var nav = _enemies.Nav;
        var array = ends.ToArray();
        _armyRouteTask = nav is null ? Task.FromResult(SearchAll(null, array)) : Task.Run(() => SearchAll(nav, array, TfwArmyRules.RouteHalfWidth));
    }

    void StepArmyRoutes()
    {
        if (_armyRouteTask is null || ++_armyRouteTicks < Ticks(TfwArmyRules.RouteSeconds)) return;
        foreach (var (segment, way, _) in _armyRouteTask.Result)
        {
            if (way is null || way.Count < 2) continue;
            var w = new TfwWay(way);
            _ways[segment.Item1, segment.Item2] = w;
            _ways[segment.Item2, segment.Item1] = w.Reversed();
        }
        _armyRouteTask = null;
    }

    /// <summary>The way from place <paramref name="a"/> to place <paramref name="b"/>: the searched route, else a straight line (before the routes are in, or where none was found).</summary>
    TfwWay WayBetween(int a, int b)
    {
        if (a is >= 0 and < Places && b is >= 0 and < Places && _ways[a, b] is { } w) return w;
        var from = a is >= 0 and < Places ? Ground(PlacePos(a), PlacePos(a)) : _layout.LandingZone;
        var to = b is >= 0 and < Places ? Ground(PlacePos(b), PlacePos(b)) : _layout.LandingZone;
        return new TfwWay([from, to]);
    }

    /// <summary>A new army in the list (null with <see cref="TfwArmyRules.MaxArmies"/> already).</summary>
    TfwArmy? NewArmy(TfwArmySide side, TfwArmyOrder order, int planned, float eliteShare)
    {
        if (_armies.Count >= TfwArmyRules.MaxArmies) return null;
        byte id = _nextArmy;
        for (int tries = 0; tries < 255 && _armies.Exists(x => x.Id == id); tries++) id = (byte)(id % 255 + 1);
        _nextArmy = (byte)(id % 255 + 1);
        var army = new TfwArmy(id, side, order, TfwArmyLayout.Width(side, planned), eliteShare) { Group = _nextGroup++ };
        _armies.Add(army);
        return army;
    }

    /// <summary>
    /// One tick of the far layer, before the positions are stepped: the routes, the humans' feet (co-op: every human counts, an AI Marine never), the dead and the living of every army, the marches, the levels,
    /// and twice a second the clashes, retreats and rallies; then the waves and the reinforcements.
    /// </summary>
    void StepArmies(ReadOnlySpan<Vector3> players)
    {
        long section = SimProfile.Begin();   // musou/perf-pass: SM_TICK_STATS's sections
        StepArmyRoutes();
        SimProfile.End("armies: routes", section);
        _armyHumans.Clear();
        bool ids = _stormIds.Count == players.Length;
        for (int k = 0; k < players.Length; k++) if (!ids || !TfwContracts.IsPuppet(_stormIds[k])) _armyHumans.Add(players[k]);
        section = SimProfile.Begin();
        TidyArmies();
        MoveArmies();
        SimProfile.End("armies: tidy and march", section);
        section = SimProfile.Begin();
        StepLevels();
        SimProfile.End("armies: levels (promote, fold)", section);
        section = SimProfile.Begin();
        if (++_armyFarTicks >= Ticks(TfwClashRules.StepSeconds))
        {
            _armyFarTicks = 0;
            _farSteps++;
            StepFarClashes();
            StepRetreats();   // TfwBattle.Retreats.cs
            StepRallies();
        }
        StepWaves();            // TfwBattle.Waves.cs
        StepReinforcements();   // TfwBattle.Garrisons.cs
        SimProfile.End("armies: clash, waves, garrisons", section);
        _armies.RemoveAll(a => a.Count == 0 && a.Order != TfwArmyOrder.Muster && a.Orks.Count == 0 && a.Squads.Count == 0);
    }

    /// <summary>Every army's dead are forgotten and counted; its real men and elites are counted.</summary>
    void TidyArmies()
    {
        int orks = 0, squads = 0, bigs = 0;
        foreach (var a in _armies)
        {
            int real = 0, elites = 0;
            for (int j = a.Orks.Count - 1; j >= 0; j--)
            {
                if (_enemies.TryGet(a.Orks[j], out var e) && e.Alive) { real++; if (Leader(e.Type)) elites++; if (TfwBattleRules.BigOutsideCaps && TfwBattleRules.Big(e.Type)) bigs++; continue; }
                a.Orks.RemoveAt(j); a.OrkSlots.RemoveAt(j);
                a.Lost++;
            }
            for (int j = a.Squads.Count - 1; j >= 0; j--)
            {
                int s = a.Squads[j], living = _guards.AliveIn(s);
                if (living < a.SquadLiving[j]) a.Lost += a.SquadLiving[j] - living;
                if (living == 0) { _squadArmy[s] = -1; a.Squads.RemoveAt(j); a.SquadBlocks.RemoveAt(j); a.SquadLiving.RemoveAt(j); continue; }
                a.SquadLiving[j] = living;
                real += living;
            }
            a.Real = real;
            a.RealElites = elites;
            orks += a.Orks.Count;
            squads += a.Squads.Count;
            if (a.Orks.Count == 0 && a.Squads.Count == 0 && !a.Near) a.SlotBase = 0;   // all aggregate again, far from everyone: its slots start at the front
        }
        ArmyOrksAlive = orks;
        ArmyBigAlive = bigs;
        ArmySquads = squads;
    }

    /// <summary>The marches: an army on its way goes on (an Ork army waits for its real ranks), its real Orks' places go with it, and at the way's end it arrives. A wave attacking closes on its point.</summary>
    void MoveArmies()
    {
        for (int n = _armies.Count - 1; n >= 0; n--)   // backwards: an arrival may remove the army (or add a garrison at the end)
        {
            if (n >= _armies.Count) continue;
            var a = _armies[n];
            float speed = a.Side == TfwArmySide.Ork ? TfwArmyRules.OrkSpeed : TfwArmyRules.GuardSpeed;
            if (a.Order is TfwArmyOrder.March or TfwArmyOrder.Retreat && a.Way is { } way)
            {
                bool wait = a.Side == TfwArmySide.Ork && OrksBehind(a) > TfwArmyRules.WaitBehind;
                a.WaitTicks = wait ? a.WaitTicks + 1 : 0;
                float pace = !wait ? speed : a.WaitTicks > Ticks(TfwArmyRules.WaitMostSeconds) ? speed * TfwArmyRules.CreepShare : 0f;   // expansion S6 (2026-11-08): waited long enough, it creeps on
                a.Along = MathF.Min(way.Length, a.Along + pace * Dt);
                wait = pace == 0f;
                var (at, dir) = way.Sample(a.Along);
                a.Anchor = at;
                a.Yaw = YawOf(dir);
                HomeOrks(a, wait ? 0f : speed);
                if (a.Along >= way.Length - 1e-3f) Arrive(a);
                continue;
            }
            if (a.Order == TfwArmyOrder.Attack && a.Closing > 0f)
            {
                float step = MathF.Min(a.Closing, speed * Dt);
                a.Anchor += new Vector3(TfwArmyLayout.Forward(a.Yaw) * step, 0f);
                a.Closing -= step;
            }
            HomeOrks(a, 0f);
        }
    }

    /// <summary>The middle of an army's leashed real Orks' distances behind their places (0 with none).</summary>
    float OrksBehind(TfwArmy a)
    {
        _armyBehind.Clear();
        foreach (ushort id in a.Orks)
            if (_enemies.HomeOf(id) is { } home && _enemies.TryGet(id, out var e) && e.State != MusouEnemyState.Spawning) _armyBehind.Add(FlatDistance(e.Position, home));
        if (_armyBehind.Count == 0) return 0f;
        _armyBehind.Sort();
        return _armyBehind[_armyBehind.Count / 2];
    }

    /// <summary>Each leashed real Ork's place: its slot along the way while the army marches (the patrols' way: ranks follow the bends), else its slot round the army.</summary>
    void HomeOrks(TfwArmy a, float speed)
    {
        bool marching = a.Order is TfwArmyOrder.March or TfwArmyOrder.Retreat && a.Way is not null;
        for (int j = 0; j < a.Orks.Count; j++)
        {
            ushort id = a.Orks[j];
            if (!_enemies.Leashed(id)) continue;
            var (home, way) = marching ? WaySlot(a, a.OrkSlots[j]) : (TfwArmyLayout.Slot(a.Side, a.Width, a.Anchor, a.Yaw, a.OrkSlots[j]), TfwArmyLayout.Forward(a.Yaw));
            _enemies.MoveHome(id, home, way * speed);
        }
    }

    /// <summary>Slot <paramref name="slot"/> of a marching army along its way, and the way's direction there; a slot that falls in a wall or off a ledge closes in on the way.</summary>
    (Vector3 Home, Vector2 Way) WaySlot(TfwArmy a, int slot)
    {
        var o = TfwArmyLayout.Offset(a.Side, a.Width, slot);
        var (at, way) = a.Way!.Sample(a.Along - o.Y);
        var spot = at + new Vector3(new Vector2(way.Y, -way.X) * o.X, 0f);
        if (_enemies.Nav is { } nav && nav.Nearest(spot, 0.6f, 2.5f) < 0) spot = at;
        return (spot, way);
    }

    /// <summary>Distance from the nearest human to the army's footprint (infinite with none).</summary>
    float HumanDistance(TfwArmy a)
    {
        var middle = a.Middle;
        float best = float.PositiveInfinity;
        foreach (var h in _armyHumans) best = MathF.Min(best, FlatDistance(h, middle));
        return best - a.Radius;
    }

    bool FarFromHumans(Vector3 p) => FarFromHumans(p, TfwArmyRules.FarMetres);

    bool FarFromHumans(Vector3 p, float metres)
    {
        foreach (var h in _armyHumans) if (FlatDistance(h, p) <= metres) return false;
        return true;
    }

    /// <summary>
    /// The levels, nearest army first: an army is near within <see cref="TfwArmyRules.NearMetres"/> of any human and far again past <see cref="TfwArmyRules.FarMetres"/>; a near army's aggregate becomes real units
    /// in its slots, a far one's real units fold back in where no human is within <see cref="TfwArmyRules.FarMetres"/>, under each tick's caps.
    /// </summary>
    void StepLevels()
    {
        _byDistance.Clear();
        foreach (var a in _armies) _byDistance.Add((HumanDistance(a), a));
        _byDistance.Sort((x, y) => x.D.CompareTo(y.D));
        int orksUp = TfwArmyRules.PromotePerTick, squadsUp = TfwArmyRules.PromoteSquadsPerTick, orksDown = TfwArmyRules.DemotePerTick, squadsDown = TfwArmyRules.DemoteSquadsPerTick;
        foreach (var (d, a) in _byDistance)
        {
            if (!a.Near && d <= TfwArmyRules.NearMetres) a.Near = true;
            else if (a.Near && d > TfwArmyRules.FarMetres) a.Near = false;
            if (a.Side == TfwArmySide.Ork && !OrkAggregates) { MakeOrks(a, ref orksUp); continue; }   // real Orks alone (2026-11-09): made wherever the army is, never folded back
            if (a.Near)
            {
                if (a.Side == TfwArmySide.Ork) PromoteOrks(a, ref orksUp);
                else PromoteSquads(a, ref squadsUp);
            }
            else if (a.Side == TfwArmySide.Ork) DemoteOrks(a, ref orksDown);
            else DemoteSquads(a, ref squadsDown);
        }
        if (!TfwArmyRules.OldRange) MakeRoom(ref orksDown, ref squadsDown);
    }

    /// <summary>
    /// An Ork army with no aggregate (2026-11-09): the men its muster brought are made real Orks in its slots under the tick's budget (<see cref="TfwArmyRules.PromotePerTick"/>), wherever it stands; those that
    /// find no room (the armies' real cap, the pool) are never made, so no Ork of an army ever stands as a count no one can fight. What waits for the next tick's budget is a spawn not yet made: never drawn,
    /// never in a clash, never on a point (a muster brings about one a tick; only the test aid that sends a whole wave at once queues more, for at most a cap's worth over 24 ticks).
    /// </summary>
    void MakeOrks(TfwArmy a, ref int budget)
    {
        PromoteOrks(a, ref budget);
        if (a.Far < 1f) return;
        int pool = Math.Max(0, MusouSim.Capacity - TfwBattleRules.PoolReserve - _enemies.Alive);
        int room = Math.Max(0, Math.Min(TfwArmyRules.NearOrksMost - ArmyCapped, pool));
        if (a.FarCount <= room) return;
        int keep = room;
        if (TfwBattleRules.BigOutsideCaps)   // 2026-11-10: the queue's big Orks take no room under the real cap (only the pool's, twice over); it keeps men down its slots until the room's worth of the others
        {
            int capped = 0, bodies = 0;
            room = Math.Max(0, TfwArmyRules.NearOrksMost - ArmyCapped);
            for (keep = 0; keep < a.FarCount && capped < room && bodies < pool; keep++)
            {
                bool big = TfwBattleRules.Big(TfwArmyLayout.OrkType(a.SlotBase + keep, a.FarEliteShare));
                if (!big) capped++;
                bodies += big ? TfwBattleRules.BigTwins : 1;
            }
        }
        if (a.FarCount <= keep) return;
        int drop = a.FarCount - keep;
        OrksNeverMade += drop;
        if (a.Far > 0f) a.FarElites = MathF.Max(0f, a.FarElites * (1f - drop / a.Far));
        a.Far = MathF.Max(0f, a.Far - drop);
        if (a.Far < 1f) { a.Far = 0f; a.FarElites = 0f; }
    }

    /// <summary>
    /// 2026-11-09 (300 m levels): with the real units' cap full and a near army still partly aggregate, the farthest armies at least <see cref="TfwArmyRules.RoomMargin"/> farther fold back their real units
    /// beyond <see cref="TfwArmyRules.CoreFarMetres"/> of every human (under the tick's demotion budget), so the fight nearest the players is the one that is real.
    /// </summary>
    void MakeRoom(ref int orksDown, ref int squadsDown)
    {
        float orkWant = float.NaN, squadWant = float.NaN;
        foreach (var (d, a) in _byDistance)
        {
            if (!a.Near || a.FarCount < 1) continue;
            if (a.Side == TfwArmySide.Ork && OrkAggregates && float.IsNaN(orkWant) && ArmyCapped > TfwArmyRules.NearOrksMost - TfwArmyRules.PromotePerTick) orkWant = d;
            if (a.Side == TfwArmySide.Guard && float.IsNaN(squadWant) && ArmySquads >= TfwArmyRules.NearSquadsMost) squadWant = d;
        }
        if (float.IsNaN(orkWant) && float.IsNaN(squadWant)) return;
        for (int k = _byDistance.Count - 1; k >= 0; k--)
        {
            var (d, a) = _byDistance[k];
            bool ork = a.Side == TfwArmySide.Ork;
            float want = ork ? orkWant : squadWant;
            if (float.IsNaN(want) || d <= want + TfwArmyRules.RoomMargin || a.Real <= 0) continue;
            if (ork) DemoteOrks(a, ref orksDown, TfwArmyRules.CoreFarMetres);
            else DemoteSquads(a, ref squadsDown, TfwArmyRules.CoreFarMetres);
        }
    }

    void PromoteOrks(TfwArmy a, ref int budget)
    {
        float aggro = a.Order is TfwArmyOrder.March or TfwArmyOrder.Retreat ? TfwArmyRules.MarchAggro : TfwArmyRules.AttackAggro;
        while (budget > 0 && a.FarCount >= 1 && ArmyCapped < TfwArmyRules.NearOrksMost && _enemies.Alive < MusouSim.Capacity - TfwBattleRules.PoolReserve)
        {
            int slot = a.SlotBase;
            var type = TfwArmyLayout.OrkType(slot, a.FarEliteShare);
            var spot = TfwArmyLayout.Slot(a.Side, a.Width, a.Anchor, a.Yaw, slot);   // where the client's impostor of this slot stood
            var at = Ground(spot, a.Order is TfwArmyOrder.March or TfwArmyOrder.Retreat && a.Way is not null ? WaySlot(a, slot).Home : a.Anchor);
            ushort id = _enemies.SpawnLeashed(type, at, a.Yaw, Health(type), a.Group, aggro, keepYaw: true);   // an army made real keeps its march facing (a fresh spawn faces the centre: owner 2026-11-09)
            if (id == 0) break;
            a.Orks.Add(id); a.OrkSlots.Add(slot);
            a.SlotBase++;
            a.Far = MathF.Max(0f, a.Far - 1f);
            if (Leader(type)) { a.FarElites = MathF.Max(0f, a.FarElites - 1f); a.RealElites++; }
            a.Real++;
            ArmyOrksAlive++;
            Promoted++;
            budget--;
            if (TfwBattleRules.Big(type) && TfwBattleRules.BigOutsideCaps) ArmyBigAlive++;
            if (TfwBattleRules.Big(type) && !OrkAggregates)   // 2026-11-10: a Nob twice over (his twin beside his slot, a man of the army), and off the real cap; not with the old aggregates (a test aid: they fold back by slot)
            {
                var twins = new List<ushort>(1);
                SpawnTwins(type, at, a.Yaw, Health(type), a.Group, aggro, twins, track: false);
                foreach (ushort twin in twins) { a.Orks.Add(twin); a.OrkSlots.Add(slot); a.Real++; a.RealElites++; a.Twins++; a.Start++; ArmyOrksAlive++; if (TfwBattleRules.BigOutsideCaps) ArmyBigAlive++; }
            }
        }
    }

    void PromoteSquads(TfwArmy a, ref int budget)
    {
        while (budget > 0 && a.FarCount >= 1 && ArmySquads < TfwArmyRules.NearSquadsMost)
        {
            int block = (a.SlotBase + GuardSim.SquadSize - 1) / GuardSim.SquadSize, men = Math.Min(GuardSim.SquadSize, a.FarCount);
            var centre = TfwArmyLayout.SquadCentre(a.Width, a.Anchor, a.Yaw, block);
            int s = _guards.SpawnSquad(Ground(centre, a.Anchor), a.Yaw, men);
            if (s < 0) break;
            _squadArmy[s] = a.Id;
            a.Squads.Add(s); a.SquadBlocks.Add(block); a.SquadLiving.Add(men);
            a.SlotBase = (block + 1) * GuardSim.SquadSize;
            a.Far = MathF.Max(0f, a.Far - men);
            a.Real += men;
            ArmySquads++;
            Promoted += men;
            budget--;
            OrderSquad(a, a.Squads.Count - 1);
        }
    }

    void DemoteOrks(TfwArmy a, ref int budget) => DemoteOrks(a, ref budget, TfwArmyRules.FarMetres);

    void DemoteOrks(TfwArmy a, ref int budget, float clear)
    {
        for (int j = a.Orks.Count - 1; j >= 0 && budget > 0; j--)
        {
            ushort id = a.Orks[j];
            if (!_enemies.TryGet(id, out var e) || !e.Alive || !FarFromHumans(e.Position, clear)) continue;
            _enemies.Remove(id);
            a.Orks.RemoveAt(j); a.OrkSlots.RemoveAt(j);
            a.Far += 1f;
            if (Leader(e.Type)) { a.FarElites += 1f; a.RealElites--; }
            a.Real--;
            ArmyOrksAlive--;
            Demoted++;
            budget--;
        }
        CompactSlots(a);
    }

    /// <summary>After a demotion the aggregate starts right after the highest real slot still standing, so promote/demote cycles never push the army's slots back.</summary>
    static void CompactSlots(TfwArmy a)
    {
        int high = 0;
        foreach (int slot in a.OrkSlots) high = Math.Max(high, slot + 1);
        foreach (int block in a.SquadBlocks) high = Math.Max(high, (block + 1) * GuardSim.SquadSize);
        a.SlotBase = Math.Min(a.SlotBase, high);
    }

    void DemoteSquads(TfwArmy a, ref int budget) => DemoteSquads(a, ref budget, TfwArmyRules.FarMetres);

    void DemoteSquads(TfwArmy a, ref int budget, float clear)
    {
        for (int j = a.Squads.Count - 1; j >= 0 && budget > 0; j--)
        {
            int s = a.Squads[j];
            if (!FarFromHumans(_guards.Centre(s), clear)) continue;
            int men = _guards.Dismiss(s);
            _squadArmy[s] = -1;
            a.Squads.RemoveAt(j); a.SquadBlocks.RemoveAt(j); a.SquadLiving.RemoveAt(j);
            a.Far += men;
            a.Real -= men;
            ArmySquads--;
            Demoted += men;
            budget--;
        }
        CompactSlots(a);
    }

    /// <summary>Squad <paramref name="j"/> of a Guard army takes its army's order: its block round a held point, else the army's goal.</summary>
    void OrderSquad(TfwArmy a, int j)
    {
        int s = a.Squads[j];
        if (a.Order == TfwArmyOrder.Hold)
        {
            var centre = TfwArmyLayout.SquadCentre(a.Width, a.Anchor, a.Yaw, a.SquadBlocks[j]);
            _guards.Order(s, Ground(centre, a.Anchor), PlacePos(GoalPlace(a.Goal)));
            return;
        }
        var goal = PlacePos(GoalPlace(a.Goal));
        _guards.Order(s, Ground(goal + SquadOffset(a.SquadBlocks[j]), goal), goal);
    }

    void OrderSquads(TfwArmy a)
    {
        for (int j = 0; j < a.Squads.Count; j++) OrderSquad(a, j);
    }

    /// <summary>Lets an army's leashed real Orks go as one pack (an attack begins); later promotions stand under a new group.</summary>
    void ReleaseArmy(TfwArmy a)
    {
        _enemies.ReleaseGroup(a.Group);
        a.Group = _nextGroup++;
    }

    /// <summary>
    /// Sends an army to <paramref name="place"/> from where it is: one standing at a place goes from its anchor by that place's route; one on its way turns back to the place it passed or goes on to the
    /// place ahead, whichever way is shorter, then by that place's route.
    /// </summary>
    void Redirect(TfwArmy a, int place)
    {
        if (a.Way is null || a.Order is TfwArmyOrder.Hold or TfwArmyOrder.Attack or TfwArmyOrder.Muster)
        {
            int at = a.To >= 0 ? a.To : a.From;
            var start = new TfwWay([a.Anchor]);
            var route = at >= 0 ? WayBetween(at, place) : new TfwWay([Ground(PlacePos(place), PlacePos(place))]);
            a.Way = start.Then(route);
            a.From = at;
            a.FromAlong = route.Count > 0 ? FlatDistance(a.Anchor, route.First) : 0f;
        }
        else
        {
            var way = a.Way;
            float s = a.Along, back = MathF.Abs(s - a.FromAlong) + WayBetween(a.From, place).Length, ahead = way.Length - s + WayBetween(a.To, place).Length;
            if (a.From >= 0 && back <= ahead)
            {
                a.Way = way.Slice(s, a.FromAlong).Then(WayBetween(a.From, place));
                a.FromAlong = MathF.Abs(s - a.FromAlong);
            }
            else
            {
                a.Way = way.Slice(s, way.Length).Then(WayBetween(a.To, place));
                a.FromAlong = way.Length - s;
                a.From = a.To;
            }
        }
        a.Along = 0f;
        a.To = place;
        a.Closing = 0f;
    }

    /// <summary>An army at its way's end.</summary>
    void Arrive(TfwArmy a)
    {
        int place = GoalPlace(a.Goal);
        if (a.Side == TfwArmySide.Ork)
        {
            if (a.Order == TfwArmyOrder.Retreat || a.Goal >= StrongholdGoalFirst) { OrksHome(a); return; }
            a.Order = Imperial(a.Goal) || a.Goal == LandingGoal ? TfwArmyOrder.Attack : TfwArmyOrder.Hold;   // the base is always the Imperium's (the owner's "base is a target")
            a.Post = a.Goal;
            a.Closing = TfwArmyLayout.Depth(a.Side, a.Width, a.Slots) * 0.5f;   // the wave walks on until its middle stands on the point
            if (FirstArrival < 0) FirstArrival = _now;
            if (a.Order == TfwArmyOrder.Attack) { ReleaseArmy(a); OnWaveArrived?.Invoke(a); }
            return;
        }
        if (place == BasePlace) { GuardsHome(a); return; }
        if (Imperial(a.Goal)) { MergeIntoGarrison(a, a.Goal); return; }   // TfwBattle.Garrisons.cs
        RedirectGuards(a);
    }

    /// <summary>An Ork army home at its stronghold: its aggregate joins the next wave; its real Orks stay where they are, free (no one vanishes before a player's eyes).</summary>
    void OrksHome(TfwArmy a)
    {
        _orkHome += a.FarCount;
        foreach (ushort id in a.Orks) _enemies.Unleash(id);
        a.Orks.Clear(); a.OrkSlots.Clear();
        a.Far = 0f; a.FarElites = 0f; a.Real = 0;
        _armies.Remove(a);
    }

    /// <summary>A Guard army at the base: its aggregate and its squads far from everyone join the next reinforcement; squads a player can see stay as field squads.</summary>
    void GuardsHome(TfwArmy a)
    {
        _guardHome += a.FarCount;
        for (int j = a.Squads.Count - 1; j >= 0; j--)
        {
            int s = a.Squads[j];
            if (FarFromHumans(_guards.Centre(s))) _guardHome += _guards.Dismiss(s);
            _squadArmy[s] = -1;
        }
        a.Squads.Clear(); a.SquadBlocks.Clear(); a.SquadLiving.Clear();
        a.Far = 0f; a.Real = 0;
        _armies.Remove(a);
    }

    /// <summary>Whether squad <paramref name="s"/> belongs to an army (the field's orders leave it alone).</summary>
    bool ArmySquad(int s) => s is >= 0 and < GuardSim.MaxSquads && _squadArmy[s] >= 0;

    /// <summary>The field's squads alive (the armies' not counted).</summary>
    int FieldSquadsAlive
    {
        get { int n = 0; for (int s = 0; s < GuardSim.MaxSquads; s++) if (_guards.AliveIn(s) > 0 && !ArmySquad(s) && !VantageSquad(s)) n++; return n; }   // a vantage team is part of its squad (TfwBattle.Vantage.cs)
    }

    /// <summary>
    /// Whether an army stands at position <paramref name="k"/> (or, <paramref name="k"/> = <c>Positions</c>, at the base: the owner's "base is a target", 2026-11-08): attacking or holding it, its footprint on the
    /// point's ring (with <see cref="TfwArmyRules.AtPointMargin"/>).
    /// </summary>
    bool AtPoint(TfwArmy a, int k) => (k == Positions ? a.Goal == LandingGoal : a.Goal == k) && a.Order is TfwArmyOrder.Attack or TfwArmyOrder.Hold
        && FlatDistance(a.Middle, ClashPoint(k)) <= ClashRadius(k) + TfwArmyRules.AtPointMargin + a.Radius;

    /// <summary>A clash's place: position <paramref name="k"/>, or the base (k = <c>Positions</c>).</summary>
    Vector3 ClashPoint(int k) => k == Positions ? _layout.LandingZone : _layout.Positions[k];
    float ClashRadius(int k) => k == Positions ? TfwArmyRules.BaseRadius : _layout.Radius(k);
    float ClashReach(int k) => k == Positions ? _layout.Reach(0) : _layout.Reach(k);

    /// <summary>Whether an aggregate of Orks stands at position <paramref name="k"/> (it counts there as real Orks do).</summary>
    bool ArmyOrksOn(int k)
    {
        if (!OrkAggregates) return false;   // 2026-11-09: no Ork aggregate; the real Orks on the point count as every Ork does
        foreach (var a in _armies) if (a.Side == TfwArmySide.Ork && AtPoint(a, k) && a.FarCount > 0 && a.Closing <= 0f) return true;
        return false;
    }

    /// <summary>Whether an aggregate of Guardsmen holds position <paramref name="k"/>.</summary>
    bool ArmyGuardsOn(int k)
    {
        foreach (var a in _armies) if (a.Side == TfwArmySide.Guard && AtPoint(a, k) && a.FarCount > 0) return true;
        return false;
    }

    readonly List<TfwArmy> _clashOrks = new(), _clashGuards = new();
    readonly HashSet<TfwArmy> _inClash = new();

    /// <summary>
    /// The far clashes, twice a second: at each position the aggregates of the Orks attacking or holding it against the aggregates holding it (and, where no human is near, the real units standing there, who
    /// fire at full cohesion and die as men nobody sees). An army out of every clash regains cohesion.
    /// </summary>
    void StepFarClashes()
    {
        _inClash.Clear();
        float dt = FarStepSeconds;
        Span<TfwClashUnit> orkUnits = stackalloc TfwClashUnit[TfwArmyRules.MaxArmies + 1];
        Span<TfwClashUnit> guardUnits = stackalloc TfwClashUnit[TfwArmyRules.MaxArmies + 1];
        for (int k = 0; k <= Positions; k++)   // the positions, then the base (the owner's "base is a target", 2026-11-08)
        {
            _clashOrks.Clear(); _clashGuards.Clear();
            foreach (var a in _armies)
            {
                if (!AtPoint(a, k) || a.Far < 1f) continue;   // musou/perf-pass: a fraction of a man is no remainder
                if (a.Side == TfwArmySide.Ork && !OrkAggregates) continue;   // 2026-11-09: an Ork army's men not yet made real never fight as a count
                if (a.Side == TfwArmySide.Ork) { if (a.Closing <= 0f) _clashOrks.Add(a); }
                else _clashGuards.Add(a);
            }
            if (_clashOrks.Count == 0 && _clashGuards.Count == 0) continue;
            var point = ClashPoint(k);
            float reach = ClashRadius(k) + TfwArmyRules.AtPointMargin;
            bool seen = !FarFromHumans(point);
            // The real units standing there take part. Unseen they fight at full cohesion; seen (a human near) they only take the aggregates' fire: their own fire is real already, and the
            // aggregate remainder behind the real line still wears the enemy's real front down, so no army's reserve stands idle beside the fight (expansion S6, the owner's "fight off-screen",
            // 2026-11-08; the one implementation kept at the musou/perf-pass integration). Their deaths are real deaths.
            int realOrks = _enemies.CountWithin(point, reach, ClashReach(k) + 2f);
            int realGuards = _guards.CountInside(point, reach);
            if ((_clashOrks.Count == 0 && realOrks == 0) || (_clashGuards.Count == 0 && realGuards == 0)) continue;
            var orks = orkUnits[..(_clashOrks.Count + 1)];
            var guards = guardUnits[..(_clashGuards.Count + 1)];
            const float ork = TfwClashRules.OrkWorth, guard = TfwClashRules.GuardWorth;
            for (int j = 0; j < _clashOrks.Count; j++) orks[j] = new TfwClashUnit(_clashOrks[j].Far, ClashStart(_clashOrks[j]), _clashOrks[j].Cohesion, worth: ork);
            for (int j = 0; j < _clashGuards.Count; j++) guards[j] = new TfwClashUnit(_clashGuards[j].Far, ClashStart(_clashGuards[j]), _clashGuards[j].Cohesion, worth: guard);
            orks[^1] = new TfwClashUnit(realOrks, realOrks, steady: true, worth: ork) { Silent = seen };
            guards[^1] = new TfwClashUnit(realGuards, realGuards, steady: true, worth: guard) { Silent = seen };
            TfwClash.Step(orks, guards, dt, TfwClash.Roll((uint)k + 1u, (uint)_farSteps, 0u), TfwClash.Roll((uint)k + 1u, (uint)_farSteps, 1u));
            for (int j = 0; j < _clashOrks.Count; j++) TakeFarLosses(_clashOrks[j], orks[j]);
            for (int j = 0; j < _clashGuards.Count; j++) TakeFarLosses(_clashGuards[j], guards[j]);
            PayRealLosses(k, orks[^1].Lost, guards[^1].Lost, point, reach);
        }
        foreach (var a in _armies) if (!_inClash.Contains(a)) a.Cohesion = TfwClash.Recover(a.Cohesion, dt);
    }

    /// <summary>
    /// An army's aggregate as a clash unit's start (musou/perf-pass): the aggregate's count over the share of the army still standing, so the unit's loss share is the army's. The army's start alone counted the men
    /// promoted to real units as lost, and a near army's remainder routed (and turned the whole army home) the moment it met an enemy. With nothing promoted it is the army's start, as before.
    /// </summary>
    static float ClashStart(TfwArmy a) => a.Far / Math.Clamp(a.Count / (float)Math.Max(1, a.Start), 0.05f, 1f);

    void TakeFarLosses(TfwArmy a, in TfwClashUnit u)
    {
        _inClash.Add(a);
        float lost = MathF.Min(a.Far, u.Lost);
        if (a.Side == TfwArmySide.Ork && a.Far > 0f) a.FarElites = MathF.Max(0f, a.FarElites - lost * (a.FarElites / a.Far) * TfwClashRules.EliteLossShare);
        int before = a.FarCount;
        a.Far = MathF.Max(0f, a.Far - lost);
        a.Lost += before - a.FarCount;
        a.Cohesion = u.Cohesion;
        if (a.Side == TfwArmySide.Ork)
        {
            _farOrkKills += lost;
            int whole = (int)_farOrkKills;
            OrksKilled += whole;
            _farOrkKills -= whole;
        }
        else
        {
            _farGuardLosses += lost;
            int whole = (int)_farGuardLosses;
            GuardsLostFar += whole;
            _farGuardLosses -= whole;
        }
        // musou/perf-pass: a near army's remainder that routs stops fighting; the army itself goes home by its own loss rule (StepRetreats), not because the men still waiting to be made real broke
        if (u.Routed && a.Order != TfwArmyOrder.Retreat && a.Real == 0) Retreat(a);   // TfwBattle.Retreats.cs
    }

    /// <summary>The real units' share of a far clash's losses at position <paramref name="k"/> (nobody near to see): whole men die, the nearest the point first; fractions wait for the next step.</summary>
    void PayRealLosses(int k, float orks, float guards, Vector3 point, float reach)
    {
        int o = 2 * k, g = 2 * k + 1;
        _realDebt[o] += orks;
        _realDebt[g] += guards;
        Span<ushort> near = stackalloc ushort[16];
        while (_realDebt[o] >= 1f)
        {
            int n = _enemies.Query(point, reach, near[..1], ClashReach(k) + 2f);
            if (n == 0 || !_enemies.TryGet(near[0], out var e)) { _realDebt[o] = 0f; break; }
            _enemies.Damage(near[0], 0, e.Health, Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.Shot);
            _realDebt[o] -= 1f;
        }
        while (_realDebt[g] >= 1f)
        {
            int s = -1;
            float best = reach;
            for (int q = 0; q < GuardSim.MaxSquads; q++)
                if (_guards.AliveIn(q) > 0 && FlatDistance(_guards.Centre(q), point) is var d && d <= best) { best = d; s = q; }   // only men standing at this point pay its losses
            if (s < 0 || !_guards.KillOne(s, point)) { _realDebt[g] = 0f; break; }
            _realDebt[g] -= 1f;
        }
    }

    /// <summary>Test aid: called when a wave reaches its point and attacks.</summary>
    public Action<TfwArmy>? OnWaveArrived { get; set; }
}
