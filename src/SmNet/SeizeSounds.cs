using SmSim;

namespace SmNet;

/// <summary>What a Seize Ground sound step asks the client to post.</summary>
public enum SeizeSoundKind
{
    /// <summary><c>play_mp_point_being_captured</c> (a 36 ms click; one post per tick).</summary>
    CaptureTick,
    /// <summary><c>stop_mp_point_being_captured</c> (the bank's matching stop event).</summary>
    CaptureStop,
    /// <summary><c>Play_Objective_Complete</c>: a point passed to the local player's side.</summary>
    ObjectiveComplete,
    /// <summary><c>Play_MP_Points_Total</c> (an 88 ms low-passed tick at -20 dB): the local side's score rose.</summary>
    ScoreTick,
}

/// <summary>One sound to post (the <paramref name="Point"/> it concerns, or -1).</summary>
public readonly record struct SeizeSoundEvent(SeizeSoundKind Kind, int Point);

/// <summary>
/// The Seize Ground sounds the original's scripts post (fidelity gap 9; client side, nothing on the wire). The events are the data's
/// (<c>In_Game_Default</c>): <see cref="CaptureTickEvent"/> and its stop, <see cref="ObjectiveCompleteEvent"/> (the lua's
/// <c>Sound_PlayObjectiveCompleteNoise</c>), <see cref="PointsTotalEvent"/> (posted by the HUD movie for the XP tally). **When they are posted is not in the data**
/// (the scripts are compiled; only their constant pools are read): the capture sound is a 36 ms click, so it is read as a tick, posted once per
/// <see cref="TickInterval"/> while a point the local player stands on is moving (**ours**); the objective noise plays when a point becomes the local side's
/// (**interpretation**); the points-total tick, which has no XP tally to belong to, plays when the local side's score rises (**ours**, at most every
/// <see cref="ScoreTickSpacing"/> s). Pure: it keeps the last points seen and returns the posts.
/// </summary>
public sealed class SeizeSounds
{
    public const string CaptureTickEvent = "play_mp_point_being_captured", CaptureStopEvent = "stop_mp_point_being_captured",
        ObjectiveCompleteEvent = "Play_Objective_Complete", PointsTotalEvent = "Play_MP_Points_Total";

    /// <summary>Seconds between capture ticks while a point is moving (ours).</summary>
    public const double TickInterval = 1.0;
    /// <summary>Least seconds between two score ticks (ours).</summary>
    public const double ScoreTickSpacing = 1.0;
    /// <summary>A point's progress counts as moving for this long after it last changed (s; snapshots carry it in 1/255 steps, a 20 s capture steps about every 80 ms).</summary>
    public const double MovingHold = 0.4;
    const float Epsilon = 1e-4f;

    readonly List<SeizeSoundEvent> _events = new();
    ControlPointView[] _prev = [];
    double[] _changedAt = [];
    int _tickPoint = -1;
    double _nextTick;
    int _prevScore = -1;
    double _lastScoreTick = double.NegativeInfinity;

    /// <summary>
    /// Whether a post plays its first pick only (BR-03). The three Play events are endless trigger-rate containers in the data (they tick until their Stop event: the tally's
    /// <c>Stop_MP_Points_Total</c> is never posted here, and every capture tick would add another chain beside the earlier ones), so posting them as they are stacked a growing
    /// hum. The client drops the container's continuation for these posts; one post, one sound. Stops are not posts of sound.
    /// </summary>
    public static bool SingleShot(SeizeSoundKind kind) => kind != SeizeSoundKind.CaptureStop;

    /// <summary>The event name of a post kind.</summary>
    public static string EventOf(SeizeSoundKind kind) => kind switch
    {
        SeizeSoundKind.CaptureTick => CaptureTickEvent,
        SeizeSoundKind.CaptureStop => CaptureStopEvent,
        SeizeSoundKind.ObjectiveComplete => ObjectiveCompleteEvent,
        _ => PointsTotalEvent,
    };

    /// <summary>
    /// One step. <paramref name="insidePoint"/>: the index of the point the local living player stands on (-1: none), <paramref name="own"/> the local side,
    /// <paramref name="ownScore"/> its score, <paramref name="now"/> seconds. The first call and a change of the point count only note the state. The returned list is a buffer
    /// the next call clears (no allocation per frame): read it before stepping again.
    /// </summary>
    public IReadOnlyList<SeizeSoundEvent> Step(IReadOnlyList<ControlPointView> points, int insidePoint, Team own, int ownScore, double now)
    {
        var events = _events;
        events.Clear();
        if (_prev.Length != points.Count)
        {
            _prev = points.ToArray();
            _changedAt = new double[points.Count];
            Array.Fill(_changedAt, double.NegativeInfinity);
            _prevScore = ownScore;
            if (_tickPoint >= 0) events.Add(new SeizeSoundEvent(SeizeSoundKind.CaptureStop, _tickPoint));
            _tickPoint = -1;
            return events;
        }
        bool completedHere = false;
        for (int i = 0; i < points.Count; i++)
        {
            var (was, cur) = (_prev[i], points[i]);
            if (MathF.Abs(was.Progress - cur.Progress) > Epsilon || was.Side != cur.Side || was.Owner != cur.Owner) _changedAt[i] = now;
            if (was.Owner != cur.Owner)
            {
                if (own != Team.None && cur.Owner == own) events.Add(new SeizeSoundEvent(SeizeSoundKind.ObjectiveComplete, i));
                if (i == insidePoint) completedHere = true;
            }
        }
        // The tick: the point the player stands on moves (not contested into a standstill, not just completed).
        int moving = insidePoint >= 0 && insidePoint < points.Count && !completedHere && now - _changedAt[insidePoint] <= MovingHold
                     && points[insidePoint].Side != Team.None ? insidePoint : -1;
        if (moving < 0)
        {
            if (_tickPoint >= 0) events.Add(new SeizeSoundEvent(SeizeSoundKind.CaptureStop, _tickPoint));
            _tickPoint = -1;
        }
        else if (moving != _tickPoint || now >= _nextTick)
        {
            if (_tickPoint >= 0 && moving != _tickPoint) events.Add(new SeizeSoundEvent(SeizeSoundKind.CaptureStop, _tickPoint));
            events.Add(new SeizeSoundEvent(SeizeSoundKind.CaptureTick, moving));
            _tickPoint = moving;
            _nextTick = now + TickInterval;
        }
        if (_prevScore >= 0 && ownScore > _prevScore && now - _lastScoreTick >= ScoreTickSpacing)
        {
            events.Add(new SeizeSoundEvent(SeizeSoundKind.ScoreTick, -1));
            _lastScoreTick = now;
        }
        _prevScore = ownScore;
        for (int i = 0; i < points.Count; i++) _prev[i] = points[i];
        return events;
    }

    /// <summary>Forgets everything (a new match, a lost connection): the next step only notes the state.</summary>
    public void Reset()
    {
        _prev = [];
        _tickPoint = -1;
        _prevScore = -1;
    }
}
