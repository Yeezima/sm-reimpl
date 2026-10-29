using System.Numerics;

namespace SmSim;

/// <summary>
/// One tick's steering from a <see cref="NavAgent"/>: the ground-plane direction to move (unit, zero when arrived), a speed
/// factor (1 = full; lower on the last metres), and the path state. During a jump-pack link the direction is the flight's
/// heading (face it even at speed 0), <see cref="Jump"/> says whether the Jump button is down this tick and
/// <see cref="Flying"/> that the agent is executing the link (press to landing: the caller should pass the controls through).
/// </summary>
public readonly record struct NavSteer(Vector2 Direction, float Speed, bool Arrived, bool HasPath, bool DropAhead, bool Recovering)
{
    public bool Jump { get; init; }
    public bool Flying { get; init; }
}

/// <summary>
/// What a <see cref="NavAgent"/> needs to know about the body to execute a jump-pack link: its jump state, the pack, the
/// tick length, its ground speed and whether something else (a melee attack, a throw, a switch) keeps it from jumping.
/// </summary>
public readonly record struct NavBody(JumpPhase Phase, int JumpTicks, float JumpPower, float Speed, JumpPackSpec? Pack, float TickSeconds, bool Busy = false);

/// <summary>
/// Path following for a test bot over a <see cref="NavGraph"/> (test aid, not the original AI). Each call to
/// <see cref="Steer"/> plans when needed (no path; or, at most every <see cref="MinPlanInterval"/> s: the goal moved more
/// than <see cref="RepathDistance"/> or a tenth of the path still to go, the path is <see cref="RepathInterval"/> s old,
/// or the bot is more than <see cref="OffPathDistance"/> off it; not while falling), moves to the
/// next waypoint once within <see cref="WaypointRadius"/> or past it, looks ahead (skips to a later waypoint a straight walk
/// already reaches) and slows on the last <see cref="SlowRadius"/> metres to arrive within <see cref="ArriveRadius"/>. Stuck
/// (less than <see cref="StuckDistance"/> moved in <see cref="StuckWindow"/> seconds of steering): it re-plans from the
/// current cell; stuck twice in a row, it first walks for <see cref="RecoverSeconds"/> toward the centre of the nearest
/// walkable cell (or sideways when already on it). Time not spent steering (a pause longer than 0.5 s) does not count, nor
/// does standing at the goal. A goal the graph does not reach (<see cref="NavPath.Partial"/>) is "arrived at" on the path's
/// last cell (<see cref="Unreachable"/>); while the goal stays put such a path is re-planned only every
/// <see cref="PartialRepathInterval"/> s. With <see cref="NavGraph.Jumps"/> and a <see cref="NavBody"/> it also flies jump-pack
/// links (<see cref="NavJumpFlight"/>): walks to the take-off cell, stands still, presses Jump when the power covers the link,
/// replays the checked controls to the landing, then goes on from where it landed (a landing off the planned cell by more than
/// <see cref="JumpMissDistance"/> metres re-plans at once). Drop links are one way: a bot that drops where no walk or drop leads back (a pit
/// without stairs; kill volumes are excluded from the graph) can only reach goals from there, and its paths are partial.
/// With <see cref="Background"/> the A* runs on the thread pool and its path is used from the first <see cref="Steer"/>
/// after it finished (the old path, or a straight walk at the goal, until then); without it, plans run inside
/// <see cref="Steer"/> and the agent is deterministic for the same inputs.
/// </summary>
public sealed class NavAgent(NavGraph graph)
{
    public NavGraph Graph { get; } = graph ?? throw new ArgumentNullException(nameof(graph));
    public float RepathDistance { get; init; } = 3f;
    /// <summary>Shortest time (seconds) between re-plans for a moved goal, an old path or leaving the path.</summary>
    public double MinPlanInterval { get; init; } = 0.5;
    public double RepathInterval { get; init; } = 1.0;
    /// <summary>Re-plan interval (seconds) for a partial path whose goal has not moved more than <see cref="RepathDistance"/>.</summary>
    public double PartialRepathInterval { get; init; } = 5.0;
    /// <summary>Plan on the thread pool instead of inside <see cref="Steer"/> (for a client's frame loop).</summary>
    public bool Background { get; init; }
    public float OffPathDistance { get; init; } = 2.5f;
    public float WaypointRadius { get; init; } = 0.6f;
    public float ArriveRadius { get; init; } = 1.0f;
    public float SlowRadius { get; init; } = 2.0f;
    public double StuckWindow { get; init; } = 1.5;
    public float StuckDistance { get; init; } = 0.75f;
    public double RecoverSeconds { get; init; } = 0.8;
    /// <summary>Metres of walkable, linked ground a recovery step needs in its direction (M44a; the bots' strafe check uses 1.5).</summary>
    public float RecoverWalkCheck { get; init; } = 1.5f;
    /// <summary>Plans in a row that may find nothing while the old path is kept (M44a); the next failure drops it.</summary>
    public int MaxKeptFailures { get; init; } = 6;
    int _failStreak;
    /// <summary>Distance (metres) from the take-off cell's centre within which the agent stops to jump (it leaves again beyond twice this).</summary>
    public float TakeOffRadius { get; init; } = 0.3f;
    /// <summary>A landing farther than this (metres) from the link's landing cell, or on another level, counts as a miss and re-plans.</summary>
    public float JumpMissDistance { get; init; } = 3f;
    /// <summary>Height difference (metres) within which the bot counts as on a waypoint's level.</summary>
    public float LevelTolerance { get; init; } = 0.25f;

    /// <summary>The current path (null before the first plan or when none was found).</summary>
    public NavPath? Path { get; private set; }
    /// <summary>Index of the waypoint steered to.</summary>
    public int Waypoint { get; private set; }
    /// <summary>Plans made (including re-plans).</summary>
    public int Plans { get; private set; }
    /// <summary>Plans that found no path (an end off the graph).</summary>
    public int Failures { get; private set; }
    /// <summary>Stuck detections.</summary>
    public int StuckCount { get; private set; }
    /// <summary>Time spent planning (milliseconds, all plans) and the longest plan.</summary>
    public double PlanMilliseconds { get; private set; }
    public double MaxPlanMilliseconds { get; private set; }
    /// <summary>Path length still to go (metres along the waypoints) after the last <see cref="Steer"/>.</summary>
    public float Remaining { get; private set; }
    /// <summary>The goal the current path was planned for.</summary>
    public Vector3 PlannedGoal { get; private set; }
    /// <summary>The last <see cref="Steer"/> stopped at the end of a partial path: the graph does not reach the goal.</summary>
    public bool Unreachable { get; private set; }
    /// <summary>The last <see cref="Steer"/> was on a jump link's approach, wait or flight (a caller may then want the speed factor and the heading honoured).</summary>
    public bool OnJumpLink { get; private set; }
    /// <summary>Seconds a bot may stand on a take-off cell waiting to jump (still, power, other business) before the wait counts as stuck and the link is dropped; the power's recharge time is added.</summary>
    public double JumpWaitSeconds { get; init; } = 3.0;
    /// <summary>Misses (landing off, or the jump never starting) after which a link is no longer planned through by this agent.</summary>
    public int JumpMissLimit { get; init; } = 2;
    /// <summary>Test aid: milliseconds each background search sleeps before it runs (to land a plan at a chosen moment).</summary>
    public int SearchDelayMilliseconds { get; init; }
    /// <summary>Jump links this agent no longer plans through.</summary>
    public int ExcludedJumps => _bad.Count;
    /// <summary>Where the flight now under way is meant to land (valid while <see cref="JumpInProgress"/>).</summary>
    public Vector3 FlightTarget => _flyTo;
    /// <summary>A jump-pack link is being flown (from the press to the landing).</summary>
    public bool JumpInProgress => _flying;
    /// <summary>Jump links started (the press), landed within <see cref="JumpMissDistance"/> of their landing cell, and missed.</summary>
    public int JumpsStarted { get; private set; }
    public int JumpsLanded { get; private set; }
    public int JumpsMissed { get; private set; }
    /// <summary>The link last flown: take-off and planned landing position, and where the body came down (for logs).</summary>
    public (Vector3 From, Vector3 To, Vector3 Came)? LastJump { get; private set; }
    /// <summary>Longest time one <see cref="Steer"/> call took (milliseconds; with <see cref="Background"/>, without the A* itself).</summary>
    public double MaxSteerMilliseconds { get; private set; }

    System.Threading.Tasks.Task<(NavPath? Path, double Ms)>? _planTask;
    Vector3 _taskGoal;
    double _taskAt;
    bool _resting;
    bool _flying;
    double _flyStart, _waitStart = double.NaN;
    readonly Dictionary<long, int> _misses = new();
    readonly HashSet<long> _bad = new();
    Vector3 _flyFrom, _flyTo;

    double _plannedAt = double.NegativeInfinity, _lastSteer = double.NegativeInfinity, _checkAt, _recoverUntil = double.NegativeInfinity, _lookAt = double.NegativeInfinity;
    Vector3 _checkPos;
    int _consecutiveStuck;
    Vector2 _recoverDir;

    /// <summary>Drops the path (the next <see cref="Steer"/> plans again); counters are kept.</summary>
    public void Reset()
    {
        LegLength = 0f;
        _failStreak = 0;
        Path = null;
        Waypoint = 0;
        Unreachable = false;
        _planTask = null;   // a plan under way is dropped (it finishes on its own)
        _plannedAt = double.NegativeInfinity;
        _consecutiveStuck = 0;
        _recoverUntil = double.NegativeInfinity;
        _flying = false;
        _waitStart = double.NaN;
    }

    /// <summary>
    /// Path length when the current goal was first planned for (re-plans for a goal that moved less than 10 m since then
    /// keep it), and the progress against it, 0..1.
    /// </summary>
    public float LegLength { get; private set; }
    public float Progress => LegLength > 0.01f ? Math.Clamp(1f - Remaining / LegLength, 0f, 1f) : 0f;
    Vector3 _legGoal;

    (NavPath? Path, double Ms) Search(Vector3 position, Vector3 goal, IReadOnlySet<long>? excluded)
    {
        if (SearchDelayMilliseconds > 0 && Background) Thread.Sleep(SearchDelayMilliseconds);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var path = Graph.FindPath(position, goal, excludedJumps: excluded);
        return (path, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    void Plan(Vector3 position, Vector3 goal, double now)
    {
        // No plan from the press to the landing: one landing after lift-off would replace the path (and the flight's controls)
        // mid-flight. The next plan comes at the landing.
        if (_flying) return;
        _plannedAt = now;
        var excluded = _bad.Count > 0 ? new HashSet<long>(_bad) : null;   // a snapshot: the search may run on another thread
        if (!Background) { Apply(Search(position, goal, excluded), goal); return; }
        if (_planTask is not null) return;   // one search at a time; its result is used when it lands
        _taskGoal = goal;
        _taskAt = now;
        var graphPosition = position;
        _planTask = System.Threading.Tasks.Task.Run(() => Search(graphPosition, goal, excluded));
    }

    void Apply((NavPath? Path, double Ms) result, Vector3 goal)
    {
        Plans++;
        PlanMilliseconds += result.Ms;
        MaxPlanMilliseconds = Math.Max(MaxPlanMilliseconds, result.Ms);
        if (result.Path is null)
        {
            // M44a: a plan that found nothing (an end off the graph: the bot mid-air, the goal over a drop) keeps the path being followed; dropping it sent the
            // bot straight at the goal, into whatever lay between. It is tried again at the next re-plan.
            // After MaxKeptFailures in a row the path goes after all (the straight walk at the goal, as before M44a): the old path may lead nowhere useful any more.
            Failures++;
            _failStreak++;
            if (Path is not null && _failStreak < MaxKeptFailures) return;
        }
        else _failStreak = 0;
        Path = result.Path;
        Waypoint = Path is { Waypoints.Count: > 1 } ? 1 : 0;
        PlannedGoal = goal;
        if (LegLength <= 0f || Flat(goal, _legGoal) > 10f) { LegLength = Path?.Length ?? 0f; _legGoal = goal; }
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    static Vector2 Toward(Vector3 from, Vector3 to)
    {
        var d = new Vector2(to.X - from.X, to.Y - from.Y);
        return d.LengthSquared() > 1e-8f ? Vector2.Normalize(d) : Vector2.Zero;
    }

    /// <summary>Horizontal distance from <paramref name="p"/> to the segment <paramref name="a"/>-<paramref name="b"/>, and how far along it (0..1+).</summary>
    static (float Distance, float T) ToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = new Vector2(b.X - a.X, b.Y - a.Y);
        var ap = new Vector2(p.X - a.X, p.Y - a.Y);
        float len2 = ab.LengthSquared();
        float t = len2 > 1e-8f ? Vector2.Dot(ap, ab) / len2 : 1f;
        var closest = ab * Math.Clamp(t, 0f, 1f);
        return ((ap - closest).Length(), t);
    }

    /// <summary>The steering toward <paramref name="goal"/> from <paramref name="position"/> (feet) at time <paramref name="now"/> (seconds).</summary>
    public NavSteer Steer(Vector3 position, bool grounded, Vector3 goal, double now, NavBody? body = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var steer = SteerCore(position, grounded, goal, now, body);
        if (!OnJumpLink) _waitStart = double.NaN;   // a wait at a take-off cell does not survive leaving the link
        _resting = steer.Arrived;
        MaxSteerMilliseconds = Math.Max(MaxSteerMilliseconds, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return steer;
    }

    long _flyKey;

    /// <summary>Counts <paramref name="n"/> misses of a link; at <see cref="JumpMissLimit"/> this agent stops planning through it.</summary>
    void Miss(long key, int n)
    {
        _misses[key] = _misses.GetValueOrDefault(key) + n;
        if (_misses[key] >= JumpMissLimit) _bad.Add(key);
    }

    static bool InFlight(JumpPhase p) => p is JumpPhase.TakeOff or JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall;

    NavSteer SteerCore(Vector3 position, bool grounded, Vector3 goal, double now, NavBody? body)
    {
        OnJumpLink = false;
        if (now - _lastSteer > 0.5) { _checkPos = position; _checkAt = now; _consecutiveStuck = 0; }
        _lastSteer = now;
        // A background search that finished: its path from now on (the advance and off-path checks below catch up with
        // where the bot has got to meanwhile).
        if (_planTask is { IsCompleted: true } task)
        {
            _planTask = null;
            Apply(task.IsCompletedSuccessfully ? task.Result : (null, 0), _taskGoal);
            _plannedAt = _taskAt;
        }

        // A flight that ended (the body is out of the jump phases after the press had time to take effect): count it, and either
        // go on past the landing waypoint or, when it came down elsewhere, plan again from here.
        if (_flying && body is { } fb && !InFlight(fb.Phase) && (fb.Phase == JumpPhase.Landed || (grounded && now - _flyStart > 0.5)))
        {
            _flying = false;
            _checkPos = position; _checkAt = now; _consecutiveStuck = 0;
            bool missed = true;
            if (Path is { } fp && Waypoint < fp.Waypoints.Count && fp.Waypoints[Waypoint].Kind == NavLinkKind.Jump)
            {
                var landing = fp.Waypoints[Waypoint].Position;
                missed = Flat(position, landing) > JumpMissDistance || MathF.Abs(position.Z - landing.Z) > 1.5f;
                LastJump = (_flyFrom, _flyTo, position);
                if (!missed && Waypoint < fp.Waypoints.Count - 1) Waypoint++;
            }
            if (missed)
            {
                JumpsMissed++;
                _plannedAt = double.NegativeInfinity;
                if (_flyKey != 0) Miss(_flyKey, 1);
            }
            else JumpsLanded++;
        }

        // Re-plan: no path, the goal moved, the path is old, or the bot is well off it (only on the ground: a drop's fall
        // keeps its path).
        bool off = false;
        if (Path is { } p0 && Waypoint > 0 && Waypoint < p0.Waypoints.Count)
            off = ToSegment(position, p0.Waypoints[Waypoint - 1].Position, p0.Waypoints[Waypoint].Position).Distance > OffPathDistance;
        bool moved = Path is not null && Flat(goal, PlannedGoal) > MathF.Max(RepathDistance, Remaining * 0.1f);
        double every = Path is { Partial: true } && !moved ? PartialRepathInterval : RepathInterval;
        bool stale = Path is null ? now - _plannedAt >= RepathInterval * 0.5 || _plannedAt == double.NegativeInfinity
            : now - _plannedAt >= MinPlanInterval && (moved || now - _plannedAt >= every || off);
        if (stale && (grounded || Path is null)) Plan(position, goal, now);

        // Stuck: little movement over the window while steering.
        if (now - _checkAt >= StuckWindow)
        {
            bool arrivedNow = Flat(position, goal) <= ArriveRadius || _resting;
            if (!arrivedNow && Flat(position, _checkPos) < StuckDistance)
            {
                StuckCount++;
                _consecutiveStuck++;
                Plan(position, goal, now);
                if (_consecutiveStuck >= 2)
                {
                    int node = Graph.Nearest(position, 1.5f, 1.5f);
                    var centre = node >= 0 ? Graph.Position(node) : position;
                    var ahead = Path is { Waypoints.Count: > 1 } ? Toward(position, Path.Waypoints[Waypoint].Position) : Vector2.Zero;
                    if (ahead == Vector2.Zero) ahead = Vector2.UnitY;
                    // M44a: from the third detection in a row (still stuck after a step toward the cell's centre or to the side) the steps alternate backwards and to the
                    // sides: a bot that walked into a dead end (a pocket the graph has no cell in) leaves the way it came.
                    int attempt = _consecutiveStuck - 2;
                    float side = (StuckCount & 1) == 0 ? 1f : -1f;
                    var toCentre = Flat(centre, position) > 0.15f ? Toward(position, centre) : new Vector2(-ahead.Y, ahead.X) * side;
                    // The candidates in this attempt's order; the first whose step of RecoverWalkCheck metres stays on walkable linked cells wins (as the bots' strafe checks
                    // it). Off the graph (a pocket with no cell) none can be checked: the first is used, so the order alone gets the bot out.
                    Vector2[] order = (attempt % 3) switch
                    {
                        1 => [-ahead, new Vector2(ahead.Y, -ahead.X) * side, new Vector2(-ahead.Y, ahead.X) * side, toCentre],
                        2 => [new Vector2(ahead.Y, -ahead.X) * side, -ahead, toCentre, new Vector2(-ahead.Y, ahead.X) * side],
                        _ => [toCentre, new Vector2(-ahead.Y, ahead.X) * side, new Vector2(ahead.Y, -ahead.X) * side, -ahead],
                    };
                    _recoverDir = order[0];
                    foreach (var candidate in order)
                        if (Graph.CanWalk(position, candidate * RecoverWalkCheck)) { _recoverDir = candidate; break; }
                    _recoverUntil = now + RecoverSeconds;
                }
            }
            else _consecutiveStuck = 0;
            _checkPos = position;
            _checkAt = now;
        }
        if (now < _recoverUntil && _recoverDir != Vector2.Zero)
            return new NavSteer(_recoverDir, 1f, false, Path is not null, false, true);

        Unreachable = false;
        if (Path is not { } path || path.Waypoints.Count == 0)
        {
            // No path (an end off the graph): straight at the goal.
            Remaining = Flat(position, goal);
            return Remaining <= ArriveRadius ? new NavSteer(Vector2.Zero, 0f, true, false, false, false)
                : new NavSteer(Toward(position, goal), 1f, false, false, false, false);
        }

        var wps = path.Waypoints;
        int last = wps.Count - 1;
        // Advance: within reach of the waypoint on its level (a step's first cell counts once the bot is up on it), or past it
        // along the segment.
        while (Waypoint < last)
        {
            var wp = wps[Waypoint].Position;
            bool level = grounded && MathF.Abs(position.Z - wp.Z) < LevelTolerance;
            if (Flat(position, wp) < WaypointRadius && level) { Waypoint++; continue; }
            if (Waypoint > 0 && wps[Waypoint].Kind == NavLinkKind.Walk && level)
            {
                var (dist, t) = ToSegment(position, wps[Waypoint - 1].Position, wp);
                if (t >= 1f && dist < 1f) { Waypoint++; continue; }
            }
            break;
        }
        // Look ahead (4 times a second): skip to the farthest later waypoint a straight walk from here already reaches.
        if (grounded && now - _lookAt >= 0.25 && Waypoint < last)
        {
            _lookAt = now;
            int here = Graph.Nearest(position, 1f, 1.5f);
            if (here >= 0)
                for (int j = Math.Min(last, Waypoint + 3); j > Waypoint; j--)
                {
                    bool walkOnly = true;
                    for (int k = Waypoint; k <= j; k++) if (wps[k].Kind != NavLinkKind.Walk) { walkOnly = false; break; }
                    if (walkOnly && Graph.StraightWalk(here, wps[j].Node)) { Waypoint = j; break; }
                }
        }

        if (Waypoint > 0 && wps[Waypoint].Kind == NavLinkKind.Jump && body is { Pack: not null } jumper)
            return SteerJump(path, position, grounded, jumper, now);

        var target = wps[Waypoint].Position;
        float toTarget = Flat(position, target);
        float remaining = toTarget;
        for (int k = Waypoint + 1; k <= last; k++) remaining += Vector3.Distance(wps[k - 1].Position, wps[k].Position);
        Remaining = remaining;
        if (Waypoint == last && !path.Partial && toTarget <= ArriveRadius)
            return new NavSteer(Vector2.Zero, 0f, true, true, false, false);
        if (Waypoint == last && path.Partial && toTarget <= ArriveRadius)
        {
            // As near as the graph gets to a goal it does not reach: stop here.
            Unreachable = Flat(position, goal) > ArriveRadius;
            return new NavSteer(Vector2.Zero, 0f, true, true, false, false);
        }
        float speed = Waypoint == last ? Math.Clamp(toTarget / SlowRadius, 0.35f, 1f) : 1f;
        bool dropAhead = wps[Waypoint].Kind == NavLinkKind.Drop && toTarget < 4f;
        return new NavSteer(Toward(position, target), speed, false, true, dropAhead, false);
    }

    /// <summary>
    /// The jump link <see cref="Waypoint"/> is (take-off cell = the waypoint before it): walk to the cell (slowing; no
    /// sprint within a few metres), stop on it, press Jump once the body is still, on the ground, out of other business and
    /// has the power, then pass the checked controls through until the landing (<see cref="NavJumpFlight.Controls"/>).
    /// </summary>
    NavSteer SteerJump(NavPath path, Vector3 position, bool grounded, NavBody body, double now)
    {
        var wps = path.Waypoints;
        var wp = wps[Waypoint];
        var move = wp.Jump;
        OnJumpLink = true;
        var take = wps[Waypoint - 1].Position;
        var heading = NavJumpFlight.Heading(move.Direction);
        float rest = 0f;
        for (int k = Waypoint + 1; k < wps.Count; k++) rest += Vector3.Distance(wps[k - 1].Position, wps[k].Position);
        float dist = Flat(position, take);
        Remaining = dist + Flat(take, wp.Position) + rest;
        if (InFlight(body.Phase))
        {
            if (!_flying) return new NavSteer(Vector2.Zero, 0f, false, true, true, false);   // another jump (a fight's) is under way: leave it alone
            _checkPos = position; _checkAt = now;
            var (stick, button) = NavJumpFlight.Controls(body.Phase, body.JumpTicks, move.Hold, body.Pack!, body.TickSeconds);
            return new NavSteer(heading, stick ? 1f : 0f, false, true, false, false) { Jump = button, Flying = true };
        }
        if (!grounded) return new NavSteer(Vector2.Zero, 0f, false, true, true, false);   // falling: nothing to steer
        if (_flying)
        {
            // Pressed, not in the air yet: keep the controls (the press tick's output is repeated until the take-off shows).
            _checkPos = position; _checkAt = now;
            return new NavSteer(heading, 0f, false, true, false, false) { Jump = body.Phase == JumpPhase.None, Flying = true };
        }
        if (dist > TakeOffRadius)
        {
            // Not on the cell (arriving, or drifted off while waiting): walk back to it, slowing as it nears.
            _waitStart = double.NaN;
            return new NavSteer(Toward(position, take), Math.Clamp(dist / 1.5f, 0.25f, 1f), false, true, dist < 6f, false);
        }
        bool ready = body.Speed < 0.6f && body.Phase == JumpPhase.None && !body.Busy && body.JumpPower + 1e-3f >= move.Power;
        if (!ready)
        {
            // Standing on the cell on purpose (so not "stuck") for a while: the power's recharge is allowed for, the rest is not.
            _checkPos = position; _checkAt = now;
            if (double.IsNaN(_waitStart)) _waitStart = now;
            double allowed = JumpWaitSeconds + Math.Max(0f, move.Power - body.JumpPower) / Math.Max(body.Pack!.RechargeRate, 1f);
            if (now - _waitStart > allowed)
            {
                // Waited too long (held up by something): that is stuck; drop the link and plan again.
                StuckCount++;
                Miss(NavGraph.JumpKey(move.From, wp.Node), JumpMissLimit);
                _waitStart = double.NaN;
                _plannedAt = double.NegativeInfinity;
            }
            return new NavSteer(heading, 0f, false, true, false, false);
        }
        _waitStart = double.NaN;
        _flying = true; _flyStart = now; _flyFrom = position; _flyTo = wp.Position;
        _flyKey = NavGraph.JumpKey(move.From, wp.Node);
        _planTask = null;   // a search under way would land after the lift-off; it is dropped (the plan is the one being flown)
        JumpsStarted++;
        return new NavSteer(heading, 0f, false, true, false, false) { Jump = true, Flying = true };
    }
}
