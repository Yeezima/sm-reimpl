using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Stormboy squads of a battle (expansion S3, docs/musou-expansion.md "Stormboys"; every value ours or an estimate, owner brief 2026-11-07, spec "Musou expansion: Stormboys"). They replace the two hunter
/// columns (<see cref="TfwPatrolRules.Hunters"/> is 0 since); the four route patrols stay. A change here raises <see cref="TfwRules.Version"/>. Distances in metres, times in real seconds.
/// </summary>
public static class TfwStormboyRules
{
    /// <summary>Squads at once in a battle (plan: 2, in place of the 2 hunter columns) and Stormboys in each (owner brief: 8).</summary>
    public const int Squads = 2, Size = 8;
    /// <summary>A squad hops together this often (owner brief: 3 s apart), each Stormboy to his own place in a loose formation round the squad's landing spot, this far apart (ours).</summary>
    public const float HopSeconds = 3f, Spacing = 3f;
    /// <summary>The first squads come this long after the landing (ours); a squad wiped out comes again this long after, at its source (ours, as the hunter columns' wait).</summary>
    public const float FirstSeconds = 20f, ReformSeconds = 30f;
    /// <summary>A counterattack fails when the squad is down to this share of the Stormboys it began it with (plan: "lost half").</summary>
    public const float FailShare = 0.5f;
    /// <summary>A hunting squad looks again for the nearest player by walk this often (ours); walks within <see cref="TieMetres"/> of the shortest are a tie, won by the player with the fewest squads on him.</summary>
    public const float HuntChooseSeconds = 5f, TieMetres = 5f;
    /// <summary>Nodes one walk search may expand (ours; a hunter's choice is a few searches every 5 s).</summary>
    public const int WalkExpansions = 30_000;
    /// <summary>On a point the squad spreads over this share of its radius (ours); a hunting squad lands this short of its prey (ours: inside the engage radius, not on top of him).</summary>
    public const float OnPointShare = 0.6f, HuntStandOff = 6f;
    /// <summary>An order's place counts as reached within this (ours): the squad stops hopping.</summary>
    public const float Arrived = MusouStormboy.HopMin + 2f;
}

/// <summary>A Stormboy squad's order (owner brief 2026-11-07, in priority order): counterattack the latest secured Imperial point, hunt a player, or hold the nearest Ork point when there is no target.</summary>
public enum TfwStormOrder : byte { None = 0, Counter = 1, Hunt = 2, Hold = 3 }

/// <summary>One Stormboy squad as the tests and the server's log see it.</summary>
public readonly record struct TfwStormSquadView(int Index, bool Formed, TfwStormOrder Order, int Point, int Prey, ushort[] Ids, int Wiped);

public sealed partial class TfwBattle
{
    sealed class StormSquad
    {
        public required int Index { get; init; }
        public required int Source { get; init; }
        public readonly List<ushort> Members = new();
        public bool Formed, EverFormed;
        public int ReformTicks, HopTicks, ChooseTicks;
        public TfwStormOrder Order;
        public int Point = -1, Prey, StartCount, Wiped;
    }

    StormSquad[]? _storm;
    readonly int[] _securedAt = new int[Positions];
    readonly bool[] _wasSecured = new bool[Positions];
    readonly List<int> _stormIds = new();
    readonly List<(int Id, Vector3 Feet)> _humansAlive = new();

    /// <summary>Whether Stormboy squads come in this battle (ours: always, with <see cref="Patrols"/>; tests turn them off to count the crowds alone).</summary>
    public bool StormboysOn { get; set; } = true;
    /// <summary>Test aid and the server's log: a squad's order changed (its index, the new order, the point or the prey).</summary>
    public Action<int, TfwStormOrder, int>? OnStormOrder { get; set; }

    /// <summary>
    /// The players' ids in the order of the feet the next <see cref="Step"/> is given (the run's; an AI Marine's is a puppet id). Without them (tests) every foot counts as a human with id k + 1.
    /// The squads hunt humans only.
    /// </summary>
    public void SetPlayerIds(ReadOnlySpan<int> ids) { _stormIds.Clear(); foreach (int id in ids) _stormIds.Add(id); }

    /// <summary>
    /// Test aid (the server's <c>SM_TFW_STORMBOY_TEST</c>, for a capture): the squads form at once, <see cref="StormTestDistance"/> in front of the first human (on the graph), and hunt; they come again
    /// as soon as they are wiped out. Off in a match.
    /// </summary>
    public bool StormboyTest { get; set; }
    public const float StormTestDistance = 40f;
    /// <summary>Test aid: the way the first human faces (flat, unit), so the test squads come into his view.</summary>
    public Vector2? StormTestFacing { get; set; }

    /// <summary>Test aid: position <paramref name="k"/> is secured now, as a finished capture secures it.</summary>
    public void SecureForTests(int k)
    {
        if (k < 0 || k >= Positions || _final || _objectives[k].State == TfwPositionState.Secured) return;
        _objectives[k] = _objectives[k] with { State = TfwPositionState.Secured, Progress = 1f };
        Secure(k);
    }

    /// <summary>Test aid: the squads as they are now.</summary>
    public IReadOnlyList<TfwStormSquadView> StormSquads()
    {
        var list = new List<TfwStormSquadView>();
        if (_storm is null) return list;
        foreach (var s in _storm) list.Add(new TfwStormSquadView(s.Index, s.Formed, s.Order, s.Point, s.Prey, s.Members.ToArray(), s.Wiped));
        return list;
    }

    /// <summary>At the start: the squads, their first coming <see cref="TfwStormboyRules.FirstSeconds"/> after the landing.</summary>
    void PlanStormboys()
    {
        if (!Patrols || !StormboysOn || _layout.Sources.Length == 0) return;   // the moving formations' switch (Patrols: off in the crowd-counting tests and under SM_TFW_NO_PATROLS) covers them too
        _storm = new StormSquad[TfwStormboyRules.Squads];
        for (int s = 0; s < _storm.Length; s++) _storm[s] = new StormSquad { Index = s, Source = (2 * s + 1) % _layout.Sources.Length, ReformTicks = Ticks(TfwStormboyRules.FirstSeconds) };
        for (int k = 0; k < Positions; k++) _wasSecured[k] = _objectives[k].State == TfwPositionState.Secured;
    }

    /// <summary>Each tick, after the columns: the secured points' order, then every squad: its dead forgotten, formed again, its order, its hops.</summary>
    void StepStormboys(ReadOnlySpan<Vector3> players)
    {
        if (_storm is null) return;
        for (int k = 0; k < Positions; k++)
        {
            bool secured = _objectives[k].State == TfwPositionState.Secured;
            if (secured && !_wasSecured[k]) _securedAt[k] = _now;
            _wasSecured[k] = secured;
        }
        _humansAlive.Clear();
        for (int k = 0; k < players.Length; k++)
        {
            int id = _stormIds.Count == players.Length ? _stormIds[k] : k + 1;
            if (!TfwContracts.IsPuppet(id)) _humansAlive.Add((id, players[k]));
        }
        foreach (var s in _storm)
        {
            s.Members.RemoveAll(id => !_enemies.TryGet(id, out var e) || !e.Alive || e.Type != MusouEnemyType.Stormboy);
            if (s.Formed && s.Members.Count == 0)
            {
                s.Formed = false; s.Wiped++;
                s.ReformTicks = Ticks(TfwStormboyRules.ReformSeconds);
                SetOrder(s, TfwStormOrder.None, -1, 0);
            }
            if (!s.Formed)
            {
                if (StormboyTest && _humansAlive.Count > 0) s.ReformTicks = Math.Min(s.ReformTicks, 1);
                if (s.ReformTicks > 0) s.ReformTicks--;
                else FormStorm(s);
                continue;
            }
            StormOrders(s);
            StormMove(s);
        }
    }

    /// <summary>A squad forms at its source when the crowds' cap and the pool have room (after its first time, only while its source sends); a new squad always begins with a point (owner brief: the next squad attacks a point).</summary>
    void FormStorm(StormSquad s)
    {
        if (Won) return;
        int size = TfwStormboyRules.Size;
        int source = Math.Min(s.Source, _layout.Sources.Length - 1);
        if (s.EverFormed)
        {
            // Again: at its own source while it sends, else the next one that does (the HQ's sends only from the commander on); none sending, it waits.
            int found = -1;
            for (int k = 0; k < _layout.Sources.Length && found < 0; k++)
            {
                int c = (source + k) % _layout.Sources.Length;
                if (SourceActive(Math.Min(c, Positions))) found = c;
            }
            if (found < 0) return;
            source = found;
        }
        if (Counted() - _trickled.Count + size > TfwBattleRules.AliveCap(_humansNow) || _enemies.Alive + size > MusouSim.Capacity - TfwBattleRules.PoolReserve) return;
        var at = Ground(_layout.Sources[source], _layout.Sources[source]);
        if (StormboyTest && _humansAlive.Count > 0)
        {
            var human = _humansAlive[0].Feet;
            var ahead = StormTestFacing is { } f ? f : Vector2.UnitY;
            ahead = Vector2.Transform(ahead, Matrix3x2.CreateRotation((s.Index - 0.5f) * 0.6f));
            if (LandingSpot(human, ahead, StormTestDistance, out var near)) at = near;
        }
        float yaw = YawTo(at, _layout.LandingZone);
        for (int k = 0; k < size; k++)
        {
            var spot = Ground(at + Offset(k, yaw), at);
            ushort id = _enemies.Spawn(MusouEnemyType.Stormboy, spot, yaw, Health(MusouEnemyType.Stormboy));
            if (id == 0) continue;
            _enemies.Order(id, spot);
            s.Members.Add(id);
        }
        if (s.Members.Count == 0) return;
        s.Formed = s.EverFormed = true;
        s.HopTicks = Ticks(MusouStormboy.FlightSeconds);   // a moment on the ground after the spawn's stand
        if (!(StormboyTest && HuntOrder(s))) CaptureOrder(s);
    }

    /// <summary>The latest point the Imperials secured that is still theirs (-1: none, or the final assault, where the Orks retake nothing).</summary>
    int LatestSecured()
    {
        if (_final) return -1;
        int best = -1, at = int.MinValue;
        for (int k = 0; k < Positions; k++)
            if (_objectives[k].State == TfwPositionState.Secured && _securedAt[k] > at) { at = _securedAt[k]; best = k; }
        return best;
    }

    /// <summary>The nearest point the Orks hold from <paramref name="from"/> (any not secured; the HQ, as <see cref="Positions"/>, when all are).</summary>
    int NearestOrkPoint(Vector3 from)
    {
        int best = Positions;
        float bestD = float.PositiveInfinity;
        for (int k = 0; k < Positions; k++)
        {
            if (_objectives[k].State == TfwPositionState.Secured) continue;
            float d = Vector3.DistanceSquared(_layout.Positions[k], from);
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    /// <summary>Back to capturing: counterattack the latest secured point; with none, hold the nearest Ork point (in the final assault: hunt).</summary>
    void CaptureOrder(StormSquad s)
    {
        if (_final && HuntOrder(s)) return;
        int k = LatestSecured();
        if (k >= 0) SetOrder(s, TfwStormOrder.Counter, k, 0);
        else SetOrder(s, TfwStormOrder.Hold, NearestOrkPoint(Centre(s)), 0);
    }

    /// <summary>Hunt the nearest human by walk (ties: the one fewest squads hunt); false when no human is alive in the field.</summary>
    bool HuntOrder(StormSquad s)
    {
        int prey = ChoosePrey(s);
        if (prey == 0) return false;
        SetOrder(s, TfwStormOrder.Hunt, -1, prey);
        return true;
    }

    void SetOrder(StormSquad s, TfwStormOrder order, int point, int prey)
    {
        bool changed = s.Order != order || s.Point != point || s.Prey != prey;
        s.Order = order; s.Point = point; s.Prey = prey;
        s.StartCount = s.Members.Count;
        s.ChooseTicks = Ticks(TfwStormboyRules.HuntChooseSeconds);
        if (changed && order != TfwStormOrder.None) OnStormOrder?.Invoke(s.Index, order, order == TfwStormOrder.Hunt ? prey : point);
    }

    /// <summary>
    /// The squad's orders (owner brief, in priority order): a counterattack ends when its point is no longer the Imperials' (taken back) or the squad is down to half (failed), and the squad hunts;
    /// a hunt goes on while its prey lives (looked at again every <see cref="TfwStormboyRules.HuntChooseSeconds"/>), and with no human left alive the survivors go back to capturing; a squad holding
    /// an Ork point counterattacks as soon as a point is secured.
    /// </summary>
    void StormOrders(StormSquad s)
    {
        switch (s.Order)
        {
            case TfwStormOrder.Counter:
                bool taken = s.Point < 0 || _objectives[s.Point].State != TfwPositionState.Secured;
                bool failed = s.Members.Count <= s.StartCount * TfwStormboyRules.FailShare;
                if ((taken || failed) && !HuntOrder(s)) CaptureOrder(s);
                break;
            case TfwStormOrder.Hunt:
                if (_humansAlive.Count == 0) { CaptureOrder(s); break; }   // all the players are down: back to capturing
                bool gone = true;
                foreach (var (id, _) in _humansAlive) if (id == s.Prey) { gone = false; break; }
                if (gone || --s.ChooseTicks <= 0)
                {
                    int prey = ChoosePrey(s);
                    if (prey == 0) CaptureOrder(s);
                    else if (prey != s.Prey) SetOrder(s, TfwStormOrder.Hunt, -1, prey);
                    else s.ChooseTicks = Ticks(TfwStormboyRules.HuntChooseSeconds);
                }
                break;
            case TfwStormOrder.Hold:
                if (LatestSecured() >= 0) CaptureOrder(s);
                else if (s.Point < Positions && _objectives[s.Point].State == TfwPositionState.Secured) CaptureOrder(s);
                break;
            default:
                CaptureOrder(s);
                break;
        }
    }

    /// <summary>The prey for squad <paramref name="s"/>: the living human nearest by walk from the squad, a walk within <see cref="TfwStormboyRules.TieMetres"/> of the shortest a tie that the human with the fewest squads on him wins (then the shorter walk, then the lower id); 0 for none.</summary>
    int ChoosePrey(StormSquad s)
    {
        if (_humansAlive.Count == 0) return 0;
        var from = Centre(s);
        Span<float> walk = stackalloc float[_humansAlive.Count];
        float shortest = float.PositiveInfinity;
        for (int h = 0; h < _humansAlive.Count; h++) { walk[h] = WalkLength(from, _humansAlive[h].Feet); shortest = MathF.Min(shortest, walk[h]); }
        int best = 0, bestHunters = int.MaxValue;
        float bestWalk = float.PositiveInfinity;
        for (int h = 0; h < _humansAlive.Count; h++)
        {
            if (walk[h] > shortest + TfwStormboyRules.TieMetres) continue;
            int id = _humansAlive[h].Id, hunters = 0;
            foreach (var other in _storm!) if (other != s && other.Formed && other.Order == TfwStormOrder.Hunt && other.Prey == id) hunters++;
            if (hunters < bestHunters || hunters == bestHunters && (walk[h] < bestWalk || walk[h] == bestWalk && id < best))
            { best = id; bestHunters = hunters; bestWalk = walk[h]; }
        }
        return best;
    }

    /// <summary>The walk from <paramref name="from"/> to <paramref name="to"/> on the graph (jump links count: a Stormboy hops), else straight (a search that gives up: straight, half as long again).</summary>
    float WalkLength(Vector3 from, Vector3 to)
    {
        float straight = Vector2.Distance(Flat(from), Flat(to));
        if (_enemies.Nav is not { } nav) return straight;
        try
        {
            var path = nav.FindPath(from, to, 10f, TfwStormboyRules.WalkExpansions);
            if (path is { Partial: false }) return path.Length;
        }
        catch (Exception) { }   // a failed search is a guess, never the battle's end
        return straight * 1.5f;
    }

    /// <summary>The squad's middle: its Stormboys on the ground (all of them when all are in the air).</summary>
    Vector3 Centre(StormSquad s)
    {
        Vector3 sum = Vector3.Zero, all = Vector3.Zero;
        int n = 0, m = 0;
        foreach (ushort id in s.Members)
        {
            if (!_enemies.TryGet(id, out var e)) continue;
            all += e.Position; m++;
            if (e.State != MusouEnemyState.Airborne) { sum += e.Position; n++; }
        }
        return n > 0 ? sum / n : m > 0 ? all / m : _layout.Sources[Math.Min(s.Source, _layout.Sources.Length - 1)];
    }

    /// <summary>Where the squad's order takes it now (null: nowhere) and how far short of it the squad lands.</summary>
    (Vector3 At, float Short, float Spread)? StormGoal(StormSquad s)
    {
        switch (s.Order)
        {
            case TfwStormOrder.Counter or TfwStormOrder.Hold when s.Point >= 0:
                bool hq = s.Point >= Positions;
                return (hq ? _layout.Hq : _layout.Positions[s.Point], 0f, (hq ? TfwMap.HqRadius : _layout.Radius(s.Point)) * TfwStormboyRules.OnPointShare);
            case TfwStormOrder.Hunt:
                foreach (var (id, feet) in _humansAlive) if (id == s.Prey) return (feet, TfwStormboyRules.HuntStandOff, TfwStormboyRules.Spacing);
                return null;
        }
        return null;
    }

    /// <summary>Member <paramref name="k"/>'s place in a loose formation round its squad's spot (the first at it, the rest on a ring, turned by <paramref name="yaw"/>).</summary>
    static Vector3 Offset(int k, float yaw, float spread = TfwStormboyRules.Spacing)
    {
        if (k == 0) return Vector3.Zero;
        float angle = yaw + (k - 1) * MathF.Tau / (TfwStormboyRules.Size - 1);
        float r = spread * (1f + 0.25f * ((k - 1) % 2));
        return new Vector3(MathF.Cos(angle) * r, MathF.Sin(angle) * r, 0f);
    }

    /// <summary>
    /// The squad's moves: every <see cref="TfwStormboyRules.HopSeconds"/>, while its goal is farther than <see cref="TfwStormboyRules.Arrived"/>, it hops together, the squad's spot up to
    /// <see cref="MusouStormboy.HopDistance"/> nearer its goal on the graph, each Stormboy to his own place round it. One of them fighting holds the hop, and the rest close in on him; at the goal they spread on it.
    /// </summary>
    void StormMove(StormSquad s)
    {
        if (StormGoal(s) is not { } goal) return;
        var centre = Centre(s);
        // One fighting: no hop; the others join him.
        ushort fighter = 0;
        bool anyAir = false;
        foreach (ushort id in s.Members) { if (_enemies.Engaged(id)) fighter = fighter == 0 ? id : fighter; if (_enemies.IsAirborne(id)) anyAir = true; }
        if (fighter != 0 && _enemies.TryGet(fighter, out var f))
        {
            foreach (ushort id in s.Members) if (id != fighter && !_enemies.Engaged(id) && !_enemies.IsAirborne(id)) _enemies.Order(id, f.Position);
            return;
        }
        float distance = Vector2.Distance(Flat(centre), Flat(goal.At));
        float yaw = YawTo(centre, goal.At);
        if (distance <= TfwStormboyRules.Arrived + goal.Short)
        {
            for (int k = 0; k < s.Members.Count; k++) _enemies.Order(s.Members[k], Ground(goal.At + Offset(k, yaw, goal.Spread), goal.At));
            return;
        }
        if (s.HopTicks > 0) s.HopTicks--;   // 3 s from take-off to take-off (the flight counts)
        if (anyAir || s.HopTicks > 0) return;
        s.HopTicks = Ticks(TfwStormboyRules.HopSeconds);
        var heading = Vector2.Normalize(Flat(goal.At) - Flat(centre));
        float reach = MathF.Min(MusouStormboy.HopDistance - TfwStormboyRules.Spacing * 1.25f, distance - goal.Short);
        if (!LandingSpot(centre, heading, reach, out var spot)) return;
        for (int k = 0; k < s.Members.Count; k++)
        {
            ushort id = s.Members[k];
            if (!_enemies.TryGet(id, out var e) || e.State != MusouEnemyState.Seek) continue;
            var want = spot + Offset(k, yaw);
            int node = _enemies.Nav?.Nearest(want, 2f, 3f) ?? -1;
            if (_enemies.Nav is { } nav && node < 0) { want = spot; node = nav.Nearest(spot, 1f, 2.5f); }
            if (_enemies.Nav is { } graph && node >= 0) want = graph.Position(node);
            var across = Flat(want) - Flat(e.Position);
            if (across.Length() > MusouStormboy.HopDistance - 0.5f)
            {
                // Too far behind for one hop: a full hop his way, to catch up at the next.
                if (!LandingSpot(e.Position, across / across.Length(), MusouStormboy.HopDistance - 1f, out var partway)) { _enemies.Order(id, want); continue; }
                want = partway;
                node = _enemies.Nav?.Nearest(want, 1f, 2.5f) ?? -1;
                across = Flat(want) - Flat(e.Position);
            }
            if (across.Length() < 2f || !_enemies.Hop(id, want, node)) { _enemies.Order(id, want); continue; }
            _enemies.Order(id, want);
        }
    }

    /// <summary>A landing spot about <paramref name="reach"/> along <paramref name="heading"/> from <paramref name="from"/> on the graph, a spot players can be walked to from (tried straight, then turning out, then shorter); false when none is.</summary>
    bool LandingSpot(Vector3 from, Vector2 heading, float reach, out Vector3 spot)
    {
        ReadOnlySpan<float> turns = [0f, 25f, -25f, 50f, -50f];
        ReadOnlySpan<float> lengths = [1f, 0.7f, 0.45f];
        foreach (float share in lengths)
            foreach (float degrees in turns)
            {
                float a = degrees * MathF.PI / 180f, c = MathF.Cos(a), sn = MathF.Sin(a);
                var dir = new Vector2(heading.X * c - heading.Y * sn, heading.X * sn + heading.Y * c);
                var at = from + new Vector3(dir * reach * share, 0f);
                if (_enemies.Nav is not { } nav) { spot = at; return true; }
                int node = nav.Nearest(at, 3f, 6f);
                if (node < 0) continue;
                if (_enemies.Walk?.Nearest(nav.Position(node)) is int walk and >= 0 && !_enemies.Reachable(walk)) continue;
                spot = nav.Position(node);
                return true;
            }
        spot = default;
        return false;
    }
}
